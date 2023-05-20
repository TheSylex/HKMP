using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Hkmp.Animation;
using Hkmp.Api.Command.Server;
using Hkmp.Api.Eventing.ServerEvents;
using Hkmp.Api.Server;
using Hkmp.Eventing;
using Hkmp.Eventing.ServerEvents;
using Hkmp.Game.Command.Server;
using Hkmp.Game.Server.Auth;
using Hkmp.Game.Settings;
using Hkmp.Logging;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Server;

namespace Hkmp.Game.Server;

/// <summary>
/// Class that manages the server state (similar to ClientManager). For example the current scene of
/// each player, to prevent sending redundant traffic.
/// </summary>
internal abstract class ServerManager : IServerManager {
    #region Internal server manager variables and properties

    /// <summary>
    /// The name of the authorized file.
    /// </summary>
    private const string AuthorizedFileName = "authorized.json";

    /// <summary>
    /// The net server instance.
    /// </summary>
    private readonly NetServer _netServer;

    /// <summary>
    /// Dictionary mapping player IDs to their server player data instances.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, ServerPlayerData> _playerData;

    private readonly ConcurrentDictionary<ServerEntityKey, ServerEntityData> _entityData;
    
    /// <summary>
    /// The white-list for managing player logins.
    /// </summary>
    private readonly WhiteList _whiteList;

    /// <summary>
    /// Authorized list for managing player permission.
    /// </summary>
    private readonly AuthKeyList _authorizedList;

    /// <summary>
    /// The list of banned users.
    /// </summary>
    private readonly BanList _banList;

    /// <summary>
    /// The server settings.
    /// </summary>
    protected readonly ServerSettings InternalServerSettings;

    /// <summary>
    /// The server command manager instance.
    /// </summary>
    protected readonly ServerCommandManager CommandManager;

    /// <summary>
    /// The addon manager instance.
    /// </summary>
    protected readonly ServerAddonManager AddonManager;

    #endregion

    #region IServerManager properties

    /// <inheritdoc />
    public IReadOnlyCollection<IServerPlayer> Players => new List<IServerPlayer>(_playerData.Values);

    /// <inheritdoc />
    public IServerSettings ServerSettings => InternalServerSettings;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerConnectEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerDisconnectEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerEnterSceneEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer> PlayerLeaveSceneEvent;

    /// <inheritdoc />
    public event Action<IPlayerChatEvent> PlayerChatEvent;

    #endregion

    /// <summary>
    /// Constructs the server manager.
    /// </summary>
    /// <param name="netServer">The net server instance.</param>
    /// <param name="serverSettings">The server settings.</param>
    /// <param name="packetManager">The packet manager instance.</param>
    protected ServerManager(
        NetServer netServer,
        ServerSettings serverSettings,
        PacketManager packetManager
    ) {
        _netServer = netServer;
        InternalServerSettings = serverSettings;
        _playerData = new ConcurrentDictionary<ushort, ServerPlayerData>();
        _entityData = new ConcurrentDictionary<ServerEntityKey, ServerEntityData>();

        CommandManager = new ServerCommandManager();
        var eventAggregator = new EventAggregator();

        var serverApi = new ServerApi(this, CommandManager, _netServer, eventAggregator);
        AddonManager = new ServerAddonManager(serverApi);

        // Load the lists
        _whiteList = WhiteList.LoadFromFile();
        _authorizedList = AuthKeyList.LoadFromFile(AuthorizedFileName);
        _banList = BanList.LoadFromFile();

        // Register packet handlers
        packetManager.RegisterServerPacketHandler<HelloServer>(ServerPacketId.HelloServer, OnHelloServer);
        packetManager.RegisterServerPacketHandler<ServerPlayerEnterScene>(ServerPacketId.PlayerEnterScene,
            OnClientEnterScene);
        packetManager.RegisterServerPacketHandler(ServerPacketId.PlayerLeaveScene, OnClientLeaveScene);
        packetManager.RegisterServerPacketHandler<PlayerUpdate>(ServerPacketId.PlayerUpdate, OnPlayerUpdate);
        packetManager.RegisterServerPacketHandler<PlayerMapUpdate>(ServerPacketId.PlayerMapUpdate,
            OnPlayerMapUpdate);
        packetManager.RegisterServerPacketHandler<EntitySpawn>(ServerPacketId.EntitySpawn, OnEntitySpawn);
        packetManager.RegisterServerPacketHandler<EntityUpdate>(ServerPacketId.EntityUpdate, OnEntityUpdate);
        packetManager.RegisterServerPacketHandler(ServerPacketId.PlayerDisconnect, OnPlayerDisconnect);
        packetManager.RegisterServerPacketHandler(ServerPacketId.PlayerDeath, OnPlayerDeath);
        packetManager.RegisterServerPacketHandler<ServerPlayerTeamUpdate>(ServerPacketId.PlayerTeamUpdate,
            OnPlayerTeamUpdate);
        packetManager.RegisterServerPacketHandler<ServerPlayerSkinUpdate>(ServerPacketId.PlayerSkinUpdate,
            OnPlayerSkinUpdate);
        packetManager.RegisterServerPacketHandler<ChatMessage>(ServerPacketId.ChatMessage, OnChatMessage);

        // Register a timeout handler
        _netServer.ClientTimeoutEvent += OnClientTimeout;

        // Register server shutdown handler
        _netServer.ShutdownEvent += OnServerShutdown;

        // Register a handler for when a client wants to login
        _netServer.LoginRequestEvent += OnLoginRequest;
    }

    #region Internal server manager methods

    // TODO: move registering packet handler and method hooks in here
    /// <summary>
    /// Initializes the server manager.
    /// </summary>
    public void Initialize() {
        RegisterCommands();
    }

    /// <summary>
    /// Register the default server commands.
    /// </summary>
    protected virtual void RegisterCommands() {
        CommandManager.RegisterCommand(new ListCommand(this));
        CommandManager.RegisterCommand(new WhiteListCommand(_whiteList, this));
        CommandManager.RegisterCommand(new AuthorizeCommand(_authorizedList, this));
        CommandManager.RegisterCommand(new AnnounceCommand(_playerData, _netServer));
        CommandManager.RegisterCommand(new BanCommand(_banList, this));
        CommandManager.RegisterCommand(new KickCommand(this));
    }

    /// <summary>
    /// Starts a server with the given port.
    /// </summary>
    /// <param name="port">The port the server should run on.</param>
    public void Start(int port) {
        // Stop existing server
        if (_netServer.IsStarted) {
            Logger.Info("Server was running, shutting it down before starting");
            _netServer.Stop();
        }

        // Start server again with given port
        _netServer.Start(port);
    }

    /// <summary>
    /// Stops the currently running server.
    /// </summary>
    public void Stop() {
        if (_netServer.IsStarted) {
            // Before shutting down, send TCP packets to all clients indicating
            // that the server is shutting down
            _netServer.SetDataForAllClients(updateManager => {
                updateManager.SetDisconnect(DisconnectReason.Shutdown);
            });

            _netServer.Stop();
        } else {
            Logger.Info("Could not stop server, it was not started");
        }
    }

    /// <summary>
    /// Authorizes a given authentication key.
    /// </summary>
    /// <param name="authKey">The authentication key to authorize.</param>
    public void AuthorizeKey(string authKey) {
        _authorizedList.Add(authKey);
    }

    /// <summary>
    /// Called when the server settings are updated, and need to be broadcast.
    /// </summary>
    public void OnUpdateServerSettings() {
        if (!_netServer.IsStarted) {
            return;
        }

        _netServer.SetDataForAllClients(updateManager => { updateManager.UpdateServerSettings(InternalServerSettings); });
    }

    /// <summary>
    /// Callback method for when HelloServer data is received from a client.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    /// <param name="helloServer">The HelloServer packet data.</param>
    private void OnHelloServer(ushort id, HelloServer helloServer) {
        Logger.Info($"Received HelloServer data from ({id}, {helloServer.Username})");

        // Start by sending the new client the current Server Settings
        _netServer.GetUpdateManagerForClient(id)?.UpdateServerSettings(InternalServerSettings);

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Could not find player data for ({id}, {helloServer.Username})");
            return;
        }

        playerData.CurrentScene = helloServer.SceneName;
        playerData.Position = helloServer.Position;
        playerData.Scale = helloServer.Scale;
        playerData.AnimationId = helloServer.AnimationClipId;

        var clientInfo = new List<(ushort, string)>();

        foreach (var idPlayerDataPair in _playerData) {
            var otherId = idPlayerDataPair.Key;
            if (otherId == id) {
                continue;
            }

            var otherPd = idPlayerDataPair.Value;

            clientInfo.Add((otherId, otherPd.Username));

            // If the other player has an active map icon, we also send that to the new player
            if (otherPd.HasMapIcon) {
                _netServer.GetUpdateManagerForClient(id).UpdatePlayerMapIcon(otherId, true);
                if (otherPd.MapPosition != null) {
                    _netServer.GetUpdateManagerForClient(id).UpdatePlayerMapPosition(otherId, otherPd.MapPosition);
                }
            }

            // Send to the other players that this client has just connected
            _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerConnectData(
                id,
                helloServer.Username
            );
        }

        _netServer.GetUpdateManagerForClient(id).SetHelloClientData(clientInfo);

        try {
            PlayerConnectEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerConnect event:\n{e}");
        }

        OnClientEnterScene(playerData);
    }

    /// <summary>
    /// Callback method for when a player enters a scene.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerEnterScene">The ServerPlayerEnterScene packet data.</param>
    private void OnClientEnterScene(ushort id, ServerPlayerEnterScene playerEnterScene) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received EnterScene data from {id}, but player is not in mapping");
            return;
        }

        var newSceneName = playerEnterScene.NewSceneName;

        Logger.Info($"Received EnterScene data from ({id}, {playerData.Username}), new scene: {newSceneName}");

        // Store it in their PlayerData object
        playerData.CurrentScene = newSceneName;
        playerData.Position = playerEnterScene.Position;
        playerData.Scale = playerEnterScene.Scale;
        playerData.AnimationId = playerEnterScene.AnimationClipId;

        OnClientEnterScene(playerData);

        try {
            PlayerEnterSceneEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerEnterScene event:\n{e}");
        }
    }

    /// <summary>
    /// Method that handles a player entering a scene.
    /// </summary>
    /// <param name="playerData">The ServerPlayerData corresponding to the player.</param>
    private void OnClientEnterScene(ServerPlayerData playerData) {
        var enterSceneList = new List<ClientPlayerEnterScene>();
        var alreadyPlayersInScene = false;

        foreach (var idPlayerDataPair in _playerData) {
            // Skip source player
            if (idPlayerDataPair.Key == playerData.Id) {
                continue;
            }

            var otherPlayerData = idPlayerDataPair.Value;

            // Send the packet to all clients on the new scene
            // to indicate that this client has entered their scene
            if (otherPlayerData.CurrentScene.Equals(playerData.CurrentScene)) {
                Logger.Debug($"Sending EnterScene data to {idPlayerDataPair.Key}");

                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddPlayerEnterSceneData(
                    playerData.Id,
                    playerData.Username,
                    playerData.Position,
                    playerData.Scale,
                    playerData.Team,
                    playerData.SkinId,
                    playerData.AnimationId
                );

                Logger.Debug($"Sending that {idPlayerDataPair.Key} is already in scene to {playerData.Id}");

                alreadyPlayersInScene = true;

                // Also send a packet to the client that switched scenes,
                // notifying that these players are already in this new scene.
                enterSceneList.Add(new ClientPlayerEnterScene {
                    Id = idPlayerDataPair.Key,
                    Username = otherPlayerData.Username,
                    Position = otherPlayerData.Position,
                    Scale = otherPlayerData.Scale,
                    Team = otherPlayerData.Team,
                    SkinId = otherPlayerData.SkinId,
                    AnimationClipId = otherPlayerData.AnimationId
                });
            }
        }

        var entitySpawnList = new List<EntitySpawn>();
        var entityUpdateList = new List<EntityUpdate>();

        foreach (var keyDataPair in _entityData) {
            var entityKey = keyDataPair.Key;
            
            // Check which entities are actually in the scene that the player is entering
            if (!entityKey.Scene.Equals(playerData.CurrentScene)) {
                continue;
            }

            var entityData = keyDataPair.Value;
            if (entityData.Spawned) {
                Logger.Info($"Sending that entity '{entityKey.EntityId}' has spawned in the scene to '{playerData.Id}'");

                var entitySpawn = new EntitySpawn {
                    Id = entityKey.EntityId,
                    SpawningType = entityData.SpawningType,
                    SpawnedType = entityData.SpawnedType
                };

                entitySpawnList.Add(entitySpawn);
            }
            
            Logger.Info($"Sending that entity '{entityKey.EntityId}' is already in scene to '{playerData.Id}'");

            var entityUpdate = new EntityUpdate {
                Id = entityKey.EntityId
            };

            if (entityData.Position != null) {
                entityUpdate.UpdateTypes.Add(EntityUpdateType.Position);
                entityUpdate.Position = entityData.Position;
            }

            if (entityData.Scale.HasValue) {
                entityUpdate.UpdateTypes.Add(EntityUpdateType.Scale);
                entityUpdate.Scale = entityData.Scale.Value;
            }
            
            if (entityData.AnimationId.HasValue) {
                entityUpdate.UpdateTypes.Add(EntityUpdateType.Animation);

                entityUpdate.AnimationId = entityData.AnimationId.Value;
                entityUpdate.AnimationWrapMode = entityData.AnimationWrapMode;
            }

            if (entityData.IsActive.HasValue) {
                entityUpdate.UpdateTypes.Add(EntityUpdateType.Active);
                entityUpdate.IsActive = entityData.IsActive.Value;
            }

            if (entityData.GenericData.Count > 0) {
                entityUpdate.UpdateTypes.Add(EntityUpdateType.Data);
                entityUpdate.GenericData.AddRange(entityData.GenericData);
            }

            if (entityData.HostFsmData.Count > 0) {
                entityUpdate.UpdateTypes.Add(EntityUpdateType.HostFsm);

                foreach (var pair in entityData.HostFsmData) {
                    entityUpdate.HostFsmData[pair.Key] = pair.Value;
                }
            }

            entityUpdateList.Add(entityUpdate);
        }

        if (!alreadyPlayersInScene) {
            playerData.IsSceneHost = true;
        }

        _netServer.GetUpdateManagerForClient(playerData.Id)?.AddPlayerAlreadyInSceneData(
            enterSceneList,
            entitySpawnList,
            entityUpdateList,
            !alreadyPlayersInScene
        );
    }

    /// <summary>
    /// Callback method for when a player leaves a scene.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnClientLeaveScene(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received LeaveScene data from {id}, but player is not in mapping");
            return;
        }

        HandlePlayerLeaveScene(id, false);

        try {
            PlayerLeaveSceneEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerLeaveScene event:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when a player update is received.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerUpdate">The PlayerUpdate packet data.</param>
    private void OnPlayerUpdate(ushort id, PlayerUpdate playerUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Position)) {
            playerData.Position = playerUpdate.Position;

            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerPosition(id, playerUpdate.Position);
                }
            );
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Scale)) {
            playerData.Scale = playerUpdate.Scale;

            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => { _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerScale(id, playerUpdate.Scale); }
            );
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.MapPosition)) {
            playerData.MapPosition = playerUpdate.MapPosition;

            // If the player does not have an active map icon, we do not send the map position update
            if (!playerData.HasMapIcon) {
                return;
            }

            // If the map icons need to be broadcast, we add the data to the next packet
            if (InternalServerSettings.AlwaysShowMapIcons || InternalServerSettings.OnlyBroadcastMapIconWithWaywardCompass) {
                foreach (var idPlayerDataPair in _playerData) {
                    if (idPlayerDataPair.Key == id) {
                        continue;
                    }

                    _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                        .UpdatePlayerMapPosition(id, playerUpdate.MapPosition);
                }
            }
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Animation)) {
            var animationInfos = playerUpdate.AnimationInfos;

            // Check whether there is any animation info to be stored
            if (animationInfos.Count != 0) {
                // Find the last animation clip that is not a custom clip to set as the players animation ID
                // Since that is the last clip that the player updated
                for (var i = animationInfos.Count - 1; i >= 0; i--) {
                    var clipId = animationInfos[i].ClipId;
                    if (clipId < (ushort) AnimationClip.DashEnd) {
                        playerData.AnimationId = clipId;
                        break;
                    }
                }

                // Set the animation data for each player in the same scene
                SendDataInSameScene(
                    id,
                    playerData.CurrentScene,
                    otherId => {
                        foreach (var animationInfo in animationInfos) {
                            _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerAnimation(
                                id,
                                animationInfo.ClipId,
                                animationInfo.Frame,
                                animationInfo.EffectInfo
                            );
                        }
                    }
                );
            }
        }
    }

    /// <summary>
    /// Callback method for when a player map update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerMapUpdate">The PlayerMapUpdate packet data.</param>
    private void OnPlayerMapUpdate(ushort id, PlayerMapUpdate playerMapUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerMapUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        playerData.HasMapIcon = playerMapUpdate.HasIcon;

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                .UpdatePlayerMapIcon(id, playerData.HasMapIcon);

            if (playerData.HasMapIcon && playerData.MapPosition != null) {
                // If the player now has a map icon, we also send the map position
                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                    .UpdatePlayerMapPosition(id, playerData.MapPosition);
            }
        }
    }
    
    /// <summary>
    /// Callback method for when an entity spawn is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entitySpawn">The EntitySpawn packet data.</param>
    private void OnEntitySpawn(ushort id, EntitySpawn entitySpawn) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Info($"Received EntitySpawn data, but player with ID {id} is not in mapping");
            return;
        }

        // If the player is not the scene host, ignore this data
        if (!playerData.IsSceneHost) {
            return;
        }
        
        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entitySpawn.Id
        );
        
        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }
        
        Logger.Info($"Received EntitySpawn from {id}, with entity {entitySpawn.Id}, {entitySpawn.SpawningType}, {entitySpawn.SpawnedType}");

        entityData.Spawned = true;
        entityData.SpawningType = entitySpawn.SpawningType;
        entityData.SpawnedType = entitySpawn.SpawnedType;
        
        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => {
                _netServer.GetUpdateManagerForClient(otherId)?.SetEntitySpawn(
                    entitySpawn.Id,
                    entitySpawn.SpawningType,
                    entitySpawn.SpawnedType
                );
            }
        );
    }

    /// <summary>
    /// Callback method for when an entity update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entityUpdate">The EntityUpdate packet data.</param>
    private void OnEntityUpdate(ushort id, EntityUpdate entityUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received EntityUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entityUpdate.Id
        );
        
        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Position)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityPosition(
                        entityUpdate.Id,
                        entityUpdate.Position
                    );
                }
            );

            entityData.Position = entityUpdate.Position;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Scale)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityScale(
                        entityUpdate.Id,
                        entityUpdate.Scale
                    );
                }
            );

            entityData.Scale = entityUpdate.Scale;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Animation)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityAnimation(
                        entityUpdate.Id,
                        entityUpdate.AnimationId,
                        entityUpdate.AnimationWrapMode
                    );
                }
            );

            entityData.AnimationId = entityUpdate.AnimationId;
            entityData.AnimationWrapMode = entityUpdate.AnimationWrapMode;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Active)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityIsActive(
                        entityUpdate.Id,
                        entityUpdate.IsActive
                    );
                }
            );

            entityData.IsActive = entityUpdate.IsActive;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Data)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.AddEntityData(
                        entityUpdate.Id,
                        entityUpdate.GenericData
                    );
                }
            );

            void ReplaceExistingDataWithSameType(EntityNetworkData.DataType type, Packet data) {
                var existingData = entityData.GenericData.Find(
                    d => d.Type == type
                );
                if (existingData == null) {
                    entityData.GenericData.Add(new EntityNetworkData {
                        Type = type,
                        Packet = data
                    });
                } else {
                    existingData.Packet = data;
                }
            }

            foreach (var updateData in entityUpdate.GenericData) {
                if (updateData.Type == EntityNetworkData.DataType.Rotation
                    || updateData.Type == EntityNetworkData.DataType.Collider) {
                    ReplaceExistingDataWithSameType(updateData.Type, updateData.Packet);
                }
            }
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            foreach (var pair in entityUpdate.HostFsmData) {
                var fsmIndex = pair.Key;
                var data = pair.Value;

                if (!entityData.HostFsmData.TryGetValue(fsmIndex, out var existingData)) {
                    existingData = new EntityHostFsmData();
                    entityData.HostFsmData[fsmIndex] = existingData;
                }

                existingData.MergeData(data);
                
                SendDataInSameScene(
                    id,
                    playerData.CurrentScene,
                    otherId => {
                        _netServer.GetUpdateManagerForClient(otherId)?.AddEntityHostFsmData(
                            entityUpdate.Id,
                            fsmIndex,
                            data
                        );
                    }
                );
            }
        }
    }

    /// <summary>
    /// Callback method for when a player disconnect is received.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnPlayerDisconnect(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerDisconnect data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerDisconnect data from ({id}, {playerData.Username})");

        ProcessPlayerDisconnect(id);
    }

    /// <summary>
    /// Internal method for disconnecting a player with the given ID for the given reason.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="reason">The reason for the disconnect.</param>
    public void InternalDisconnectPlayer(ushort id, DisconnectReason reason) {
        _netServer.GetUpdateManagerForClient(id)?.SetDisconnect(reason);

        ProcessPlayerDisconnect(id);
    }
    
    /// <summary>
    /// Handle a player leaving a scene by transition, disconnect or timeout.
    /// </summary>
    /// <param name="id">The ID of the player that left the scene.</param>
    /// <param name="disconnected">Whether the player disconnected from the server.</param>
    /// <param name="timeout">Whether the disconnect was due to connection timeout.</param>
    private void HandlePlayerLeaveScene(ushort id, bool disconnected, bool timeout = false) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Handling player leave scene (dc: {disconnected}) for ID {id}, but player is not in mapping");
            return;
        }

        var sceneName = playerData.CurrentScene;

        if (!disconnected && sceneName.Length == 0) {
            Logger.Warn($"Handling player leave scene for ID {id}, but there was no last scene registered");
            return;
        }
        
        Logger.Info($"Handling player leave scene (dc: {disconnected}) for ID {id}, last scene: {sceneName}");

        playerData.CurrentScene = "";

        var username = playerData.Username;
        
        // Keep track of whether the scene that the player has left is now empty
        var isSceneNowEmpty = true;

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            var otherPlayerData = idPlayerDataPair.Value;
            
            // Send a packet to all clients in the scene that the player has left their scene
            if (otherPlayerData.CurrentScene == sceneName) {
                Logger.Info($"Sending leave scene packet to {idPlayerDataPair.Key}");

                // We have now found at least one player that is still in this scene
                isSceneNowEmpty = false;

                var updateManager = _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key);

                if (playerData.IsSceneHost) {
                    // If the leaving player was the scene host, we can make this player the new scene host
                    updateManager.SetSceneHostTransfer();

                    // Reset the scene host variable in the leaving player, so only a single other player
                    // becomes the scene host
                    playerData.IsSceneHost = false;
                    
                    // Also set the player data of the new scene host
                    otherPlayerData.IsSceneHost = true;
                    
                    Logger.Info($"  {idPlayerDataPair.Key} has become scene host");
                }

                if (disconnected) {
                    updateManager.AddPlayerDisconnectData(id, username, timeout);
                } else {
                    updateManager.AddPlayerLeaveSceneData(id);
                }
            }
        }
        
        // In case there were no other players to make scene host, we still need to reset the leaving
        // player's status of scene host
        playerData.IsSceneHost = false;
        
        // If the scene is now empty, we can remove all data from stored entities in that scene
        if (isSceneNowEmpty) {
            foreach (var keyDataPair in _entityData) {
                if (keyDataPair.Key.Scene == sceneName) {
                    _entityData.TryRemove(keyDataPair.Key, out _);
                }
            }
        }

        if (disconnected) {
            // Now remove the client from the player data mapping
            _playerData.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Process a disconnect for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="timeout">Whether this player timed out or disconnected normally.</param>
    private void ProcessPlayerDisconnect(ushort id, bool timeout = false) {
        if (!timeout) {
            // If this isn't a timeout, then we need to propagate this packet to the NetServer
            _netServer.OnClientDisconnect(id);
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            return;
        }

        var username = playerData.Username;

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddPlayerDisconnectData(
                id,
                username,
                timeout
            );
        }

        // Now remove the client from the player data mapping
        _playerData.TryRemove(id, out _);
        
        HandlePlayerLeaveScene(id, true, timeout);

        try {
            PlayerDisconnectEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerDisconnect event:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when a player dies. 
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnPlayerDeath(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerDeath data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerDeath data from ({id}, {playerData.Username})");

        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => { _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerDeathData(id); }
        );
    }

    /// <summary>
    /// Callback method for when a player updates their team.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="teamUpdate">The ServerPlayerTeamUpdate packet data.</param>
    private void OnPlayerTeamUpdate(ushort id, ServerPlayerTeamUpdate teamUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerTeamUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerTeamUpdate data from ({id}, {playerData.Username}), new team: {teamUpdate.Team}");

        // Update the team in the player data
        playerData.Team = teamUpdate.Team;

        // Broadcast the packet to all players except the player we received the update from
        foreach (var playerId in _playerData.Keys) {
            if (id == playerId) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(playerId)?.AddPlayerTeamUpdateData(
                id,
                playerData.Username,
                teamUpdate.Team
            );
        }
    }

    /// <summary>
    /// Callback method for when a player updates their skin.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="skinUpdate">The ServerPlayerSkinUpdate packet data.</param>
    private void OnPlayerSkinUpdate(ushort id, ServerPlayerSkinUpdate skinUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerSkinUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (playerData.SkinId == skinUpdate.SkinId) {
            Logger.Debug($"Received PlayerSkinUpdate data from ({id}, {playerData.Username}), but skin was the same");
            return;
        }

        Logger.Debug($"Received PlayerSkinUpdate data from ({id}, {playerData.Username}), new skin ID: {skinUpdate.SkinId}");

        // Update the skin ID in the player data
        playerData.SkinId = skinUpdate.SkinId;

        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => {
                _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerSkinUpdateData(id, playerData.SkinId);
            }
        );
    }

    /// <summary>
    /// Callback method for when the server is shut down.
    /// </summary>
    private void OnServerShutdown() {
        // Clear all existing player data
        _playerData.Clear();
    }

    /// <summary>
    /// Handle a login request for a client that has invalid addons.
    /// </summary>
    /// <param name="updateManager">The update manager for the client.</param>
    private void HandleInvalidLoginAddons(ServerUpdateManager updateManager) {
        var loginResponse = new LoginResponse {
            LoginResponseStatus = LoginResponseStatus.InvalidAddons
        };
        loginResponse.AddonData.AddRange(AddonManager.GetNetworkedAddonData());

        updateManager.SetLoginResponse(loginResponse);
    }

    /// <summary>
    /// Method for handling a login request for a new client.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    /// <param name="endPoint">The IP endpoint of the client.</param>
    /// <param name="loginRequest">The LoginRequest packet data.</param>
    /// <param name="updateManager">The update manager for the client.</param>
    /// <returns>true if the login request was approved, false otherwise.</returns>
    private bool OnLoginRequest(
        ushort id,
        IPEndPoint endPoint,
        LoginRequest loginRequest,
        ServerUpdateManager updateManager
    ) {
        Logger.Info($"Received login request from IP: {endPoint.Address}, username: {loginRequest.Username}");

        if (_banList.IsIpBanned(endPoint.Address.ToString()) || _banList.Contains(loginRequest.AuthKey)) {
            updateManager.SetLoginResponse(new LoginResponse {
                LoginResponseStatus = LoginResponseStatus.Banned
            });
            return false;
        }

        if (_whiteList.IsEnabled) {
            if (!_whiteList.Contains(loginRequest.AuthKey)) {
                if (!_whiteList.IsPreListed(loginRequest.Username)) {
                    updateManager.SetLoginResponse(new LoginResponse {
                        LoginResponseStatus = LoginResponseStatus.NotWhiteListed
                    });
                    return false;
                }

                Logger.Info("  Username was pre-listed, auth key has been added to whitelist");

                _whiteList.Add(loginRequest.AuthKey);
                _whiteList.RemovePreList(loginRequest.Username);
            }
        }
        
        // Check whether the username is valid
        foreach (var character in loginRequest.Username) {
            if (!char.IsLetterOrDigit(character)) {
                updateManager.SetLoginResponse(new LoginResponse {
                    LoginResponseStatus = LoginResponseStatus.InvalidUsername
                });
                return false;
            }
        }

        // Check whether the username is not already in use
        foreach (var existingPlayerData in _playerData.Values) {
            if (existingPlayerData.Username.ToLower().Equals(loginRequest.Username.ToLower())) {
                updateManager.SetLoginResponse(new LoginResponse {
                    LoginResponseStatus = LoginResponseStatus.InvalidUsername
                });
                return false;
            }
        }

        var addonData = loginRequest.AddonData;

        // Construct a string that contains all addons and respective versions by mapping the items in the addon data
        var addonStringList = string.Join(", ", addonData.Select(addon => $"{addon.Identifier} v{addon.Version}"));
        Logger.Info($"  Client tries to connect with following addons: {addonStringList}");

        // If there is a mismatch between the number of networked addons of the client and the server,
        // we can immediately invalidate the request
        if (addonData.Count != AddonManager.GetNetworkedAddonData().Count) {
            HandleInvalidLoginAddons(updateManager);
            return false;
        }

        // Create a byte list denoting the order of the addons on the server
        var addonOrder = new List<byte>();

        foreach (var addon in addonData) {
            // Check and retrieve the server addon with the same name and version
            if (!AddonManager.TryGetNetworkedAddon(
                    addon.Identifier,
                    addon.Version,
                    out var correspondingServerAddon
                )) {
                // There was no corresponding server addon, so we send a login response with an invalid status
                // and the addon data that is present on the server, so the client knows what is invalid
                HandleInvalidLoginAddons(updateManager);
                return false;
            }

            if (!correspondingServerAddon.Id.HasValue) {
                continue;
            }

            // If the addon is also present on the server, we append the addon order with the correct index
            addonOrder.Add(correspondingServerAddon.Id.Value);
        }

        var loginResponse = new LoginResponse {
            LoginResponseStatus = LoginResponseStatus.Success,
            AddonOrder = addonOrder.ToArray()
        };

        updateManager.SetLoginResponse(loginResponse);

        // Create new player data and store it
        var playerData = new ServerPlayerData(
            id,
            endPoint.Address.ToString(),
            loginRequest.Username,
            loginRequest.AuthKey,
            _authorizedList
        );
        _playerData[id] = playerData;

        return true;
    }

    /// <summary>
    /// Callback method for when a client times out.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    private void OnClientTimeout(ushort id) {
        if (!_playerData.TryGetValue(id, out _)) {
            Logger.Debug($"Received timeout from unknown player with ID: {id}");
            return;
        }

        // Since the client has timed out, we can formally disconnect them
        ProcessPlayerDisconnect(id, true);
    }

    /// <summary>
    /// Execute a given action by passing the ID of each player that is in the same scene as the given
    /// scene name except for the source ID.
    /// </summary>
    /// <param name="sourceId">The ID of the source player.</param>
    /// <param name="sceneName">The name of the scene to send to.</param>
    /// <param name="dataAction">The action to execute with each ID.</param>
    private void SendDataInSameScene(ushort sourceId, string sceneName, Action<ushort> dataAction) {
        foreach (var idPlayerDataPair in _playerData) {
            // Skip sending to same ID
            if (idPlayerDataPair.Key == sourceId) {
                continue;
            }

            var otherPd = idPlayerDataPair.Value;

            // Skip sending to players not in the same scene
            if (!string.Equals(otherPd.CurrentScene, sceneName)) {
                continue;
            }

            dataAction(idPlayerDataPair.Key);
        }
    }

    /// <summary>
    /// Try and process a given message by a given command sender as a command.
    /// </summary>
    /// <param name="commandSender">The command sender that sent the message.</param>
    /// <param name="message">The message that was sent.</param>
    /// <returns>true if the message was processed as a command, false otherwise.</returns>
    public bool TryProcessCommand(ICommandSender commandSender, string message) {
        return CommandManager.ProcessCommand(commandSender, message);
    }

    /// <summary>
    /// Callback method for when a chat message is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="chatMessage">The ChatMessage packet data.</param>
    private void OnChatMessage(ushort id, ChatMessage chatMessage) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Could not process chat message from unknown player ID: {id}");
            return;
        }

        Logger.Info($"Chat from ({id}, {playerData.Username}): \"{chatMessage.Message}\"");

        if (TryProcessCommand(
                new PlayerCommandSender(
                    _authorizedList.Contains(playerData.AuthKey),
                    id,
                    _netServer.GetUpdateManagerForClient(id)
                ),
                chatMessage.Message
            )) {
            Logger.Debug("Chat message was processed as command");
            return;
        }

        var playerChatEvent = new PlayerChatEvent(playerData, chatMessage.Message);
        
        try {
            PlayerChatEvent?.Invoke(playerChatEvent);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerChat event:\n{e}");
        }

        // If the event has been cancelled, we don't proceed with sending the chat message to other players
        if (playerChatEvent.Cancelled) {
            return;
        }

        var message = $"[{playerData.Username}]: {playerChatEvent.Message}";

        foreach (var idPlayerDataPair in _playerData) {
            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddChatMessage(message);
        }
    }

    #endregion

    #region IServerManager methods

    /// <inheritdoc />
    public IServerPlayer GetPlayer(ushort id) {
        return TryGetPlayer(id, out var player) ? player : null;
    }

    /// <inheritdoc />
    public bool TryGetPlayer(ushort id, out IServerPlayer player) {
        var found = _playerData.TryGetValue(id, out var playerData);
        player = playerData;

        return found;
    }

    /// <summary>
    /// Check whether a given string message is valid for sending over the network.
    /// </summary>
    /// <param name="message">The string message to check.</param>
    /// <exception cref="ArgumentException">Thrown if the message is null, exceeds the max length or contains
    /// invalid characters.</exception>
    private void CheckValidMessage(string message) {
        if (message == null) {
            throw new ArgumentException("Message cannot be null");
        }

        if (message.Length > ChatMessage.MaxMessageLength) {
            throw new ArgumentException($"Message length exceeds max length of {ChatMessage.MaxMessageLength}");
        }
    }

    /// <inheritdoc />
    public void SendMessage(ushort id, string message) {
        CheckValidMessage(message);

        var updateManager = _netServer.GetUpdateManagerForClient(id);
        
        // Break message up in parts denoted by newline
        var messages = message.Split('\n');
        foreach (var line in messages) {
            updateManager?.AddChatMessage(line);
        }
    }

    /// <inheritdoc />
    public void SendMessage(IServerPlayer player, string message) {
        if (player == null) {
            throw new ArgumentException("Player cannot be null");
        }

        SendMessage(player.Id, message);
    }

    /// <inheritdoc />
    public void BroadcastMessage(string message) {
        CheckValidMessage(message);

        foreach (var player in _playerData.Values) {
            SendMessage(player.Id, message);
        }
    }

    /// <inheritdoc />
    public void DisconnectPlayer(ushort id, DisconnectReason reason) {
        if (!_playerData.TryGetValue(id, out _)) {
            throw new ArgumentException("There is no player connected with the given ID");
        }

        InternalDisconnectPlayer(id, reason);
    }

    /// <inheritdoc />
    public void ApplyServerSettings(ServerSettings serverSettings) {
        if (serverSettings == null) {
            throw new ArgumentException("Cannot apply null ServerSettings", nameof(serverSettings));
        }
    
        // If these ServerSettings instances are equal in value, we can immediately return
        if (InternalServerSettings.Equals(serverSettings)) {
            return;
        }
        
        // Set all properties of the given instance and then call the OnUpdate method to network the changes
        InternalServerSettings.SetAllProperties(serverSettings);
        OnUpdateServerSettings();
    }

    #endregion
}
