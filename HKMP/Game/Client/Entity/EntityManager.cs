using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = Hkmp.Logging.Logger;
using Vector2 = Hkmp.Math.Vector2;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Manager class that handles entity creation, updating, networking and destruction.
/// </summary>
internal class EntityManager {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Dictionary mapping entity IDs to their respective entity instances.
    /// </summary>
    private readonly Dictionary<byte, Entity> _entities;

    /// <summary>
    /// Whether the client user is the scene host.
    /// </summary>
    private bool _isSceneHost;

    /// <summary>
    /// The last used ID of an entity.
    /// </summary>
    private byte _lastId;

    public EntityManager(NetClient netClient) {
        _netClient = netClient;
        _entities = new Dictionary<byte, Entity>();

        _lastId = 0;

        EntityFsmActions.EntitySpawnEvent += OnGameObjectSpawned;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
    }

    /// <summary>
    /// Initializes the entity manager if we are the scene host.
    /// </summary>
    public void InitializeSceneHost() {
        Logger.Info("Releasing control of all registered entities");

        _isSceneHost = true;

        foreach (var entity in _entities.Values) {
            entity.InitializeHost();
        }
    }

    /// <summary>
    /// Initializes the entity manager if we are a scene client.
    /// </summary>
    public void InitializeSceneClient() {
        Logger.Info("Taking control of all registered entities");

        _isSceneHost = false;
    }

    /// <summary>
    /// Updates the entity manager if we become the scene host.
    /// </summary>
    public void BecomeSceneHost() {
        Logger.Info("Becoming scene host");

        _isSceneHost = true;

        foreach (var entity in _entities.Values) {
            entity.MakeHost();
        }
    }

    /// <summary>
    /// Spawn an entity with the given ID and type, that was spawned from the given entity type.
    /// </summary>
    /// <param name="id">The ID of the entity.</param>
    /// <param name="spawningType">The type of the entity that spawned the new entity.</param>
    /// <param name="spawnedType">The type of the spawned entity.</param>
    public void SpawnEntity(byte id, EntityType spawningType, EntityType spawnedType) {
        Logger.Info($"Trying to spawn entity with ID {id} with types: {spawningType}, {spawnedType}");

        // If an entity with the new ID already exists, we return
        if (_entities.ContainsKey(id)) {
            Logger.Info($"  Entity with ID {id} already exists, assuming it has been spawned by action");
            return;
        }

        // Find the list of client FSMs that correspond to an entity with the given type in our current scene
        // Doesn't matter which instance of entity it is, because the FSMs will be the same
        List<PlayMakerFSM> clientFsms = null;
        foreach (var existingEntity in _entities.Values) {
            if (existingEntity.Type == spawningType) {
                clientFsms = existingEntity.GetClientFsms();
                break;
            }
        }

        // If no such FSMs exist we return again, because we can't spawn the new entity
        if (clientFsms == null) {
            Logger.Warn($"Could not find entity with same type for spawning");
            return;
        }

        var gameObject = EntitySpawner.SpawnEntityGameObject(spawningType, spawnedType, clientFsms);

        // Make sure to initialize all entities that should be in the system
        foreach (var fsm in gameObject.GetComponents<PlayMakerFSM>()) {
            if (EntityRegistry.TryGetEntry(gameObject.name, fsm.Fsm.Name, out _)) {
                EntityInitializer.InitializeFsm(fsm);
            }
        }

        var entity = new Entity(
            _netClient,
            id,
            spawnedType,
            gameObject
        );
        _entities[id] = entity;
    }

    /// <summary>
    /// Update the position for the entity with the given ID.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="position">The new position.</param>
    public void UpdateEntityPosition(byte entityId, Vector2 position) {
        if (_isSceneHost) {
            return;
        }

        if (!_entities.TryGetValue(entityId, out var entity)) {
            return;
        }

        entity.UpdatePosition(position);
    }

    /// <summary>
    /// Update the scale for the entity with the given ID.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="scale">The new scale.</param>
    public void UpdateEntityScale(byte entityId, bool scale) {
        if (_isSceneHost) {
            return;
        }

        if (!_entities.TryGetValue(entityId, out var entity)) {
            return;
        }

        entity.UpdateScale(scale);
    }

    /// <summary>
    /// Update the animation for the entity with the given ID.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="animationId">The ID of the animation.</param>
    /// <param name="animationWrapMode">The wrap mode of the animation.</param>
    /// <param name="alreadyInSceneUpdate">Whether this update is when we are entering the scene.</param>
    public void UpdateEntityAnimation(
        byte entityId,
        byte animationId,
        byte animationWrapMode,
        bool alreadyInSceneUpdate
    ) {
        if (_isSceneHost) {
            return;
        }

        if (!_entities.TryGetValue(entityId, out var entity)) {
            return;
        }

        entity.UpdateAnimation(animationId, (tk2dSpriteAnimationClip.WrapMode) animationWrapMode,
            alreadyInSceneUpdate);
    }

    /// <summary>
    /// Update whether the entity with the given ID is active.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="isActive">The new value for active.</param>        
    public void UpdateEntityIsActive(byte entityId, bool isActive) {
        if (_isSceneHost) {
            return;
        }

        if (!_entities.TryGetValue(entityId, out var entity)) {
            return;
        }

        entity.UpdateIsActive(isActive);
    }

    /// <summary>
    /// Update the entity with the given ID with the given generic data.
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="data">The list of data to update the entity with.</param>
    public void UpdateEntityData(byte entityId, List<EntityNetworkData> data) {
        if (_isSceneHost) {
            return;
        }

        if (!_entities.TryGetValue(entityId, out var entity)) {
            return;
        }

        entity.UpdateData(data);
    }

    /// <summary>
    /// Update the host FSMs of the entity with the given ID with the given data.
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="hostFsmData">Dictionary mapping FSM index to FSM data.</param>
    public void UpdateHostEntityFsmData(byte entityId, Dictionary<byte, EntityHostFsmData> hostFsmData) {
        if (_isSceneHost) {
            return;
        }

        if (!_entities.TryGetValue(entityId, out var entity)) {
            return;
        }
        
        entity.UpdateHostFsmData(hostFsmData);
    }

    /// <summary>
    /// Callback method for when a game object is spawned from an existing entity.
    /// </summary>
    /// <param name="action">The action from which the game object was spawned.</param>
    /// <param name="gameObject">The game object that was spawned.</param>
    private void OnGameObjectSpawned(FsmStateAction action, GameObject gameObject) {
        if (_entities.Values.Any(entity => entity.Object.Host == gameObject)) {
            Logger.Debug("Spawned object was already a registered entity");
            return;
        }

        foreach (var fsm in gameObject.GetComponents<PlayMakerFSM>()) {
            if (!ProcessGameObjectFsm(fsm, out var entity, out var entityId)) {
                continue;
            }

            if (_isSceneHost) {
                // Since an entity was created and we are the scene host, we need to notify the server
                var spawningObjectName = action.Fsm.GameObject.name;
                var spawningFsmName = action.Fsm.Name;
                if (EntityRegistry.TryGetEntry(spawningObjectName, spawningFsmName, out var entry)) {
                    Logger.Info(
                        $"Notifying server of entity ({spawningObjectName}, {entry.Type}) spawning entity ({gameObject.name}, {entity.Type}) with ID {entityId}");
                    _netClient.UpdateManager.SetEntitySpawn(entityId, entry.Type, entity.Type);
                }
                    
                // Also initialize the entity as host, since otherwise it will stay disabled
                entity.InitializeHost();
            } else {
                // Since an entity was created and we are not the scene host, we need to manually initialize
                // all the FSM on the client
                foreach (var clientFsm in entity.GetClientFsms()) {
                    Logger.Info($"Manually initializing client entity FSM: {clientFsm.Fsm.Name}, {clientFsm.gameObject.name}");
                    EntityInitializer.InitializeFsm(clientFsm);
                }
                        
                // We also need to update the 'active' state of the entity since it was spawned
                entity.UpdateIsActive(true);
            }
        }
    }

    /// <summary>
    /// Callback method for when the scene changes. Will clear existing entities and start checking for
    /// new entities.
    /// </summary>
    /// <param name="oldScene">The old scene.</param>
    /// <param name="newScene">The new scene.</param>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        Logger.Info($"Scene changed, clearing registered entities");
            
        foreach (var entity in _entities.Values) {
            entity.Destroy();
        }

        _entities.Clear();

        _lastId = 0;

        if (!_netClient.IsConnected) {
            return;
        }

        FindEntitiesInScene(newScene, false);
    }

    /// <summary>
    /// Callback method for when a scene is loaded.
    /// </summary>
    /// <param name="scene">The scene that is loaded.</param>
    /// <param name="mode">The load scene mode.</param>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        // If this scene is a boss or boss-defeated scene it starts with the same name, so we skip all other
        // loaded scenes
        if (!scene.name.StartsWith(currentSceneName)) {
            return;
        }

        Logger.Info($"Additional scene loaded ({scene.name}), looking for entities");

        FindEntitiesInScene(scene, true);
    }

    /// <summary>
    /// Find entities to register in the given scene.
    /// </summary>
    /// <param name="scene">The scene to find entities in.</param>
    /// <param name="lateLoad">Whether this scene was loaded late.</param>
    private void FindEntitiesInScene(Scene scene, bool lateLoad) {
        // Find all PlayMakerFSM components
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>()) {
            // Logger.Info($"Found FSM: {fsm.Fsm.Name} in scene: {fsm.gameObject.scene.name}");
            if (fsm.gameObject.scene != scene) {
                continue;
            }

            if (!ProcessGameObjectFsm(fsm, out var entity, out _) || !lateLoad) {
                continue;
            }

            if (_isSceneHost) {
                // Since this is a late load it needs to be initialized as host if we are the scene host
                entity.InitializeHost();
            } else {
                // Since this is a late load we need to update the 'active' state of the entity
                entity.UpdateIsActive(true);
            }
        }

        // Find all Climber components
        foreach (var climber in Object.FindObjectsOfType<Climber>()) {
            if (climber.gameObject.scene != scene) {
                continue;
            }
            
            if (!EntityRegistry.TryGetEntry(climber.gameObject.name, EntityType.Tiktik, out var entry)) {
                continue;
            }

            RegisterGameObjectAsEntity(climber.gameObject, entry.Type, out _);
        }
    }

    /// <summary>
    /// Process the FSM of a game object to check whether the game object should be registered as an entity.
    /// </summary>
    /// <param name="fsm">The FSM to process.</param>
    /// <param name="entity">The resulting entity if one was created; otherwise null.</param>
    /// <param name="entityId">The ID of the entity if one was created; otherwise 0.</param>
    /// <returns>True if an entity was created; otherwise false.</returns>
    private bool ProcessGameObjectFsm(PlayMakerFSM fsm, out Entity entity, out byte entityId) {
        // Logger.Info($"Processing FSM: {fsm.Fsm.Name}, {fsm.gameObject.name}");

        if (!EntityRegistry.TryGetEntry(fsm.gameObject.name, fsm.Fsm.Name, out var entry)) {
            entity = null;
            entityId = 0;
            return false;
        }

        entity = RegisterGameObjectAsEntity(fsm.gameObject, entry.Type, out entityId);
        return true;
    }

    /// <summary>
    /// Register a given game object as an entity and return that entity.
    /// </summary>
    /// <param name="gameObject">The game object to register.</param>
    /// <param name="type">The type of the entity.</param>
    /// <param name="entityId">The ID of the registered entity.</param>
    /// <returns>The entity that was created.</returns>
    private Entity RegisterGameObjectAsEntity(GameObject gameObject, EntityType type, out byte entityId) {
        // First find a usable ID that is not registered to an entity already
        while (_entities.ContainsKey(_lastId)) {
            _lastId++;
        }

        Logger.Info($"Registering entity ({type}) '{gameObject.name}' with ID '{_lastId}'");

        // TODO: maybe we need to check whether this entity game object has already been registered, which can
        // happen with game objects that have multiple FSMs

        var entity = new Entity(
            _netClient,
            _lastId,
            type,
            gameObject
        );
        _entities[_lastId] = entity;

        entityId = _lastId;

        _lastId++;

        return entity;
    }
}