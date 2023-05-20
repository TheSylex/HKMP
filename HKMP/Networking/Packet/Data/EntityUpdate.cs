using System;
using System.Collections.Generic;
using Hkmp.Logging;
using Hkmp.Math;

namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Packet data for an entity update.
/// </summary>
internal class EntityUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => false;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the entity.
    /// </summary>
    public byte Id { get; set; }

    /// <summary>
    /// A set containing the types of updates contained in this packet.
    /// </summary>
    public HashSet<EntityUpdateType> UpdateTypes { get; }

    /// <summary>
    /// The position of the entity.
    /// </summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// The boolean representation of the scale of the entity.
    /// </summary>
    public bool Scale { get; set; }
        
    /// <summary>
    /// The ID of the animation of the entity.
    /// </summary>
    public byte AnimationId { get; set; }
    /// <summary>
    /// The wrap mode of the animation.
    /// </summary>
    public byte AnimationWrapMode { get; set; }

    /// <summary>
    /// Whether the entity is active or not.
    /// </summary>
    public bool IsActive { get; set; }
        
    public List<EntityNetworkData> GenericData { get; }
    
    public Dictionary<byte, EntityHostFsmData> HostFsmData { get; }

    /// <summary>
    /// Construct the entity update data.
    /// </summary>
    public EntityUpdate() {
        UpdateTypes = new HashSet<EntityUpdateType>();
        GenericData = new List<EntityNetworkData>();
        HostFsmData = new Dictionary<byte, EntityHostFsmData>();
    }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Id);

        // Construct the byte flag representing update types
        byte updateTypeFlag = 0;
        // Keep track of value of current bit
        byte currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(EntityUpdateType)).Length; i++) {
            // Cast the current index of the loop to a PlayerUpdateType and check if it is
            // contained in the update type list, if so, we add the current bit to the flag
            if (UpdateTypes.Contains((EntityUpdateType) i)) {
                updateTypeFlag |= currentTypeValue;
            }

            currentTypeValue *= 2;
        }

        // Write the update type flag
        packet.Write(updateTypeFlag);

        // Conditionally write the state and data fields
        if (UpdateTypes.Contains(EntityUpdateType.Position)) {
            packet.Write(Position);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Scale)) {
            packet.Write(Scale);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Animation)) {
            packet.Write(AnimationId);
            packet.Write(AnimationWrapMode);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Active)) {
            packet.Write(IsActive);
        }

        if (UpdateTypes.Contains(EntityUpdateType.Data)) {
            if (GenericData.Count > byte.MaxValue) {
                Logger.Error("Length of entity network data instances exceeded max value of byte");
            }
                
            var length = (byte)System.Math.Min(GenericData.Count, byte.MaxValue);

            packet.Write(length);
            for (var i = 0; i < length; i++) {
                GenericData[i].WriteData(packet);
            }
        }

        if (UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            var length = (byte) HostFsmData.Count;
            packet.Write(length);

            foreach (var pair in HostFsmData) {
                packet.Write(pair.Key);

                pair.Value.WriteData(packet);
            }
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Id = packet.ReadByte();

        // Read the byte flag representing update types and reconstruct it
        var updateTypeFlag = packet.ReadByte();
        // Keep track of value of current bit
        var currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(EntityUpdateType)).Length; i++) {
            // If this bit was set in our flag, we add the type to the list
            if ((updateTypeFlag & currentTypeValue) != 0) {
                UpdateTypes.Add((EntityUpdateType) i);
            }

            // Increase the value of current bit
            currentTypeValue *= 2;
        }

        // Based on the update types, we read the corresponding values
        if (UpdateTypes.Contains(EntityUpdateType.Position)) {
            Position = packet.ReadVector2();
        }
            
        if (UpdateTypes.Contains(EntityUpdateType.Scale)) {
            Scale = packet.ReadBool();
        }

        if (UpdateTypes.Contains(EntityUpdateType.Animation)) {
            AnimationId = packet.ReadByte();
            AnimationWrapMode = packet.ReadByte();
        }

        if (UpdateTypes.Contains(EntityUpdateType.Active)) {
            IsActive = packet.ReadBool();
        }

        if (UpdateTypes.Contains(EntityUpdateType.Data)) {
            var length = packet.ReadByte();

            for (var i = 0; i < length; i++) {
                var entityNetworkData = new EntityNetworkData();
                entityNetworkData.ReadData(packet);
                    
                GenericData.Add(entityNetworkData);
            }
        }

        if (UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            var length = packet.ReadByte();

            for (var i = 0; i < length; i++) {
                var key = packet.ReadByte();

                var data = new EntityHostFsmData();
                data.ReadData(packet);
                
                HostFsmData.Add(key, data);
            }
        }
    }
}

/// <summary>
/// Generic data for a networked entity.
/// </summary>
internal class EntityNetworkData {
    /// <summary>
    /// The type of the data.
    /// </summary>
    public DataType Type { get; set; }
    /// <summary>
    /// Packet instance containing the data for easy reading and writing of data.
    /// </summary>
    public Packet Packet { get; set; }

    public EntityNetworkData() {
        Packet = new Packet();
    }

    /// <inheritdoc cref="IPacketData.WriteData" />
    public void WriteData(IPacket packet) {
        packet.Write((byte)Type);

        var data = Packet.ToArray();
        
        if (data.Length > byte.MaxValue) {
            Logger.Error("Length of entity network data exceeded max value of byte");
        }
            
        var length = (byte)System.Math.Min(data.Length, byte.MaxValue);

        packet.Write(length);
        for (var i = 0; i < length; i++) {
            packet.Write(data[i]);
        }
    }

    /// <inheritdoc cref="IPacketData.ReadData" />
    public void ReadData(IPacket packet) {
        Type = (DataType) packet.ReadByte();

        var length = packet.ReadByte();
        var data = new byte[length];
        
        for (var i = 0; i < length; i++) {
            data[i] = packet.ReadByte();
        }

        Packet = new Packet(data);
    }

    /// <summary>
    /// Enum for data types.
    /// </summary>
    public enum DataType : byte {
        Fsm = 0,
        HealthManager,
        Rotation,
        Collider
    }
}

/// <summary>
/// Class containing data for host FSMs including state and FSM variables.
/// Used to make host transfer easier since all clients receive updates on host FSM details.
/// </summary>
internal class EntityHostFsmData {
    /// <summary>
    /// The types of content that is in this data class.
    /// </summary>
    public HashSet<Type> Types { get; }
    
    /// <summary>
    /// The index of the current (or last) state of the FSM.
    /// </summary>
    public byte CurrentState { get; set; }
    
    /// <summary>
    /// Dictionary containing indices of float variables to their respective values.
    /// </summary>
    public Dictionary<byte, float> Floats { get; }
    /// <summary>
    /// Dictionary containing indices of int variables to their respective values.
    /// </summary>
    public Dictionary<byte, int> Ints { get; }
    /// <summary>
    /// Dictionary containing indices of bool variables to their respective values.
    /// </summary>
    public Dictionary<byte, bool> Bools { get; }
    /// <summary>
    /// Dictionary containing indices of string variables to their respective values.
    /// </summary>
    public Dictionary<byte, string> Strings { get; }
    /// <summary>
    /// Dictionary containing indices of vector2 variables to their respective values.
    /// </summary>
    public Dictionary<byte, Vector2> Vec2s { get; }
    /// <summary>
    /// Dictionary containing indices of vector3 variables to their respective values.
    /// </summary>
    public Dictionary<byte, Vector3> Vec3s { get; }

    public EntityHostFsmData() {
        Types = new HashSet<Type>();

        Floats = new Dictionary<byte, float>();
        Ints = new Dictionary<byte, int>();
        Bools = new Dictionary<byte, bool>();
        Strings = new Dictionary<byte, string>();
        Vec2s = new Dictionary<byte, Vector2>();
        Vec3s = new Dictionary<byte, Vector3>();
    }

    /// <summary>
    /// Merges the data from the given data class into the current one.
    /// </summary>
    /// <param name="otherData">The other <see cref="EntityHostFsmData"/> instance.</param>
    public void MergeData(EntityHostFsmData otherData) {
        if (otherData.Types.Contains(Type.State)) {
            Types.Add(Type.State);

            CurrentState = otherData.CurrentState;
        }

        if (otherData.Types.Contains(Type.Floats)) {
            Types.Add(Type.Floats);

            foreach (var pair in otherData.Floats) {
                Floats[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Ints)) {
            Types.Add(Type.Ints);
            
            foreach (var pair in otherData.Ints) {
                Ints[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Bools)) {
            Types.Add(Type.Bools);
            
            foreach (var pair in otherData.Bools) {
                Bools[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Strings)) {
            Types.Add(Type.Strings);
            
            foreach (var pair in otherData.Strings) {
                Strings[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Vector2s)) {
            Types.Add(Type.Vector2s);
            
            foreach (var pair in otherData.Vec2s) {
                Vec2s[pair.Key] = pair.Value;
            }
        }
        
        if (otherData.Types.Contains(Type.Vector3s)) {
            Types.Add(Type.Vector3s);
            
            foreach (var pair in otherData.Vec3s) {
                Vec3s[pair.Key] = pair.Value;
            }
        }
    }

    /// <inheritdoc cref="IPacketData.WriteData" />
    public void WriteData(IPacket packet) {
        // Construct the byte flag representing update types
        byte updateTypeFlag = 0;
        // Keep track of value of current bit
        byte currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(Type)).Length; i++) {
            // Cast the current index of the loop to a PlayerUpdateType and check if it is
            // contained in the update type list, if so, we add the current bit to the flag
            if (Types.Contains((Type) i)) {
                updateTypeFlag |= currentTypeValue;
            }

            currentTypeValue *= 2;
        }

        // Write the update type flag
        packet.Write(updateTypeFlag);

        if (Types.Contains(Type.State)) {
            packet.Write(CurrentState);
        }

        void WriteVarDict<T>(Type type, Dictionary<byte, T> dict, Action<T> writeValue) {
            if (Types.Contains(type)) {
                var length = (byte) dict.Count;
                packet.Write(length);

                foreach (var pair in dict) {
                    packet.Write(pair.Key);
                    writeValue.Invoke(pair.Value);
                }
            }
        }

        WriteVarDict(Type.Floats, Floats, packet.Write);
        WriteVarDict(Type.Ints, Ints, packet.Write);
        WriteVarDict(Type.Bools, Bools, packet.Write);
        WriteVarDict(Type.Strings, Strings, packet.Write);
        WriteVarDict(Type.Vector2s, Vec2s, packet.Write);
        WriteVarDict(Type.Vector3s, Vec3s, packet.Write);
    }

    /// <inheritdoc cref="IPacketData.ReadData" />
    public void ReadData(IPacket packet) {
        // Read the byte flag representing update types and reconstruct it
        var updateTypeFlag = packet.ReadByte();
        // Keep track of value of current bit
        var currentTypeValue = 1;

        for (var i = 0; i < Enum.GetNames(typeof(Type)).Length; i++) {
            // If this bit was set in our flag, we add the type to the list
            if ((updateTypeFlag & currentTypeValue) != 0) {
                Types.Add((Type) i);
            }

            // Increase the value of current bit
            currentTypeValue *= 2;
        }

        if (Types.Contains(Type.State)) {
            CurrentState = packet.ReadByte();
        }

        void ReadVarDict<T>(Type type, Dictionary<byte, T> dict, Func<T> readValue) {
            if (Types.Contains(type)) {
                var length = packet.ReadByte();

                for (var i = 0; i < length; i++) {
                    dict.Add(packet.ReadByte(), readValue.Invoke());
                }
            }
        }
        
        ReadVarDict(Type.Floats, Floats, packet.ReadFloat);
        ReadVarDict(Type.Ints, Ints, packet.ReadInt);
        ReadVarDict(Type.Bools, Bools, packet.ReadBool);
        ReadVarDict(Type.Strings, Strings, packet.ReadString);
        ReadVarDict(Type.Vector2s, Vec2s, packet.ReadVector2);
        ReadVarDict(Type.Vector3s, Vec3s, packet.ReadVector3);
    }

    /// <summary>
    /// Enum for update types of this class.
    /// </summary>
    public enum Type : byte {
        State,
        Floats,
        Ints,
        Bools,
        Strings,
        Vector2s,
        Vector3s
    }
}

/// <summary>
/// Enumeration of entity update types.
/// </summary>
internal enum EntityUpdateType {
    Position = 0,
    Scale,
    Animation,
    Active,
    Data,
    HostFsm
}
