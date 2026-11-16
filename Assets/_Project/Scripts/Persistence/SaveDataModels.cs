using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoralArchipelago.Persistence
{
    /// <summary>
    /// JSON-serializable 3D vector helper to avoid Unity Vector3 serialization issues.
    /// </summary>
    [Serializable]
    public struct SerializableVector3
    {
        public float x;
        public float y;
        public float z;

        public SerializableVector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3 ToVector3() => new Vector3(x, y, z);

        public static SerializableVector3 FromVector3(Vector3 v) => new SerializableVector3(v.x, v.y, v.z);
    }

    /// <summary>
    /// Represents a single item stack inside either a player bag or a shared chest.
    /// </summary>
    [Serializable]
    public class InventorySlotRecord
    {
        public string ItemId;
        public int Amount;

        public InventorySlotRecord() { }

        public InventorySlotRecord(string itemId, int amount)
        {
            ItemId = itemId;
            Amount = amount;
        }
    }

    /// <summary>
    /// Persistent record for a player-built structure in the shared world.
    /// </summary>
    [Serializable]
    public class PlacedStructureRecord
    {
        public string StructureGuid;
        public string PrefabId;
        public SerializableVector3 Position;
        public float RotationY;
        public float CurrentHP;
    }

    /// <summary>
    /// Persistent record for a shared chest whose contents persist across asynchronous sessions.
    /// </summary>
    [Serializable]
    public class SharedChestRecord
    {
        public string ChestGuid;
        public List<InventorySlotRecord> Items = new List<InventorySlotRecord>();
    }

    /// <summary>
    /// Persistent record for a depleted resource node waiting to respawn based on frozen-safe TotalWorldHours.
    /// </summary>
    [Serializable]
    public class HarvestedNodeRecord
    {
        public string NodeUniqueId;
        public double RespawnAtTotalWorldHours;
    }

    /// <summary>
    /// Persistent camp activity log or teammate note displayed across asynchronous sessions.
    /// </summary>
    [Serializable]
    public class CampLogEntryRecord
    {
        public string TimestampText;
        public string Message;

        public CampLogEntryRecord() { }

        public CampLogEntryRecord(string timestampText, string message)
        {
            TimestampText = timestampText;
            Message = message;
        }
    }

    /// <summary>
    /// World-level save data shared by all up to 10 asynchronous players.
    /// Stored independently from player save files in world_save.json.
    /// </summary>
    [Serializable]
    public class WorldSaveData
    {
        public int WorldSeed = 20261116;
        public float WorldTimeHours = 8.0f;
        public int CurrentDay = 1;
        public double TotalWorldHours = 8.0;
        public int IslandCoreLevel = 1;

        public List<PlacedStructureRecord> Structures = new List<PlacedStructureRecord>();
        public List<SharedChestRecord> SharedChests = new List<SharedChestRecord>();
        public List<HarvestedNodeRecord> HarvestedNodes = new List<HarvestedNodeRecord>();
        public List<CampLogEntryRecord> CampLogs = new List<CampLogEntryRecord>();
    }

    /// <summary>
    /// Per-player save data stored independently in players/{PlayerUUID}.json.
    /// Guarantees asynchronous players never overwrite each other's position or bag.
    /// </summary>
    [Serializable]
    public class PlayerSaveData
    {
        public string PlayerUUID;
        public SerializableVector3 LastPosition = new SerializableVector3(0f, 2f, 0f);
        public float LastRotationY = 0f;
        public float CurrentHP = 100f;
        public float CurrentStamina = 100f;
        public float CurrentOxygen = 100f;
        public long LastSavedUtcTicks;
        public int TutorialStepIndex = 0;
        public bool TutorialCompleted = false;

        public List<InventorySlotRecord> Inventory = new List<InventorySlotRecord>();
    }
}
