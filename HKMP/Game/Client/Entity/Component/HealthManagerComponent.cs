using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

// TODO: make sure that the data sent on death is saved as state on the server, so new clients entering
// scenes can start with the entity disabled/already dead
// TODO: periodically (or on hit) sync the health of the entity so on scene host transfer we can reset health
internal class HealthManagerComponent : EntityComponent {
    private readonly HostClientPair<HealthManager> _healthManager;

    private bool _allowDeath;

    public HealthManagerComponent(
        NetClient netClient,
        byte entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<HealthManager> healthManager
    ) : base(netClient, entityId, gameObject) {
        _healthManager = healthManager;

        On.HealthManager.Die += HealthManagerOnDie;
    }

    private void HealthManagerOnDie(
        On.HealthManager.orig_Die orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        bool ignoreEvasion
    ) {
        if (self != _healthManager.Host && self != _healthManager.Client) {
            orig(self, attackDirection, attackType, ignoreEvasion);
            return;
        }

        if (self == _healthManager.Client) {
            if (!_allowDeath) {
                Logger.Info("HealthManager Die was called on client entity");
            } else {
                Logger.Info("HealthManager Die was called on client entity, but it is allowed death");

                orig(self, attackDirection, attackType, ignoreEvasion);

                _allowDeath = false;
            }

            return;
        }

        Logger.Info("HealthManager Die was called on host entity");

        orig(self, attackDirection, attackType, ignoreEvasion);

        var data = new EntityNetworkData {
            Type = EntityNetworkData.DataType.HealthManager
        };

        if (attackDirection.HasValue) {
            data.Packet.Write(true);
            data.Packet.Write(attackDirection.Value);
        } else {
            data.Packet.Write(false);
        }

        data.Packet.Write((byte)attackType);

        data.Packet.Write(ignoreEvasion);

        SendData(data);
    }

    public override void InitializeHost() {
    }

    public override void Update(EntityNetworkData data) {
        Logger.Info("Received health manager update");

        if (!IsControlled) {
            Logger.Info("  Entity was not controlled");
            return;
        }

        var attackDirection = new float?();
        if (data.Packet.ReadBool()) {
            attackDirection = data.Packet.ReadFloat();
        }

        var attackType = (AttackTypes)data.Packet.ReadByte();
        var ignoreEvasion = data.Packet.ReadBool();

        // Set a boolean to indicate that the client health manager is allowed to execute the Die method
        _allowDeath = true;
        _healthManager.Client.Die(attackDirection, attackType, ignoreEvasion);
    }

    public override void Destroy() {
        On.HealthManager.Die -= HealthManagerOnDie;
    }
}