using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Gameplay.Player;

namespace CoralArchipelago.Persistence
{
    /// <summary>
    /// Server-authoritative Split Save Manager.
    /// Separates shared World Save (world_save.json) from individual Player Saves (players/{uuid}.json)
    /// and uses atomic temp-file writes to prevent save corruption on VPS crashes or container restarts.
    /// </summary>
    public class SplitSaveManager : MonoBehaviour
    {
        public static SplitSaveManager Instance { get; private set; }

        [Header("Persistence Settings")]
        [SerializeField] private float autoSaveIntervalSeconds = 60f;
        [SerializeField] private float rollingBackupIntervalMinutes = 30f;
        [SerializeField] private int maxRollingBackupSlots = 5;
        [SerializeField] private string localSubFolderName = "saves";

        private string _saveRootDirectory;
        private string _playersDirectory;
        private string _backupDirectory;
        private string _worldSavePath;

        private WorldSaveData _currentWorldSave;
        private bool _isWorldDirty;
        private float _autoSaveTimer;
        private float _rollingBackupTimer;
        private int _nextBackupSlot;

        // Cache of active connected player saves keyed by clientId
        private readonly Dictionary<ulong, PlayerSaveData> _activePlayerSaves = new Dictionary<ulong, PlayerSaveData>();

        public WorldSaveData CurrentWorldSave
        {
            get
            {
                if (_currentWorldSave == null)
                {
                    LoadWorldSave();
                }
                return _currentWorldSave;
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializePaths();
        }

        private void Update()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            _rollingBackupTimer += Time.unscaledDeltaTime;
            if (_rollingBackupTimer >= rollingBackupIntervalMinutes * 60f)
            {
                _rollingBackupTimer = 0f;
                CreateRollingWorldBackup();
            }

            if (!_isWorldDirty)
                return;

            _autoSaveTimer += Time.unscaledDeltaTime;
            if (_autoSaveTimer >= autoSaveIntervalSeconds)
            {
                _autoSaveTimer = 0f;
                SaveWorldNow();
            }
        }

        private void OnApplicationQuit()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                FlushAllSavesToDisk();
            }
        }

        private void InitializePaths()
        {
            string envPath = Environment.GetEnvironmentVariable("SAVE_DATA_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                _saveRootDirectory = envPath.Trim();
            }
            else
            {
                _saveRootDirectory = Path.Combine(Application.persistentDataPath, localSubFolderName);
            }

            _playersDirectory = Path.Combine(_saveRootDirectory, "players");
            _backupDirectory = Path.Combine(_saveRootDirectory, "backups");
            _worldSavePath = Path.Combine(_saveRootDirectory, "world_save.json");

            Directory.CreateDirectory(_saveRootDirectory);
            Directory.CreateDirectory(_playersDirectory);
            Directory.CreateDirectory(_backupDirectory);

            Debug.Log($"[SplitSaveManager] Save directory initialized at: {_saveRootDirectory}");
        }

        #region World Save Operations

        public WorldSaveData LoadWorldSave()
        {
            if (File.Exists(_worldSavePath))
            {
                try
                {
                    string json = File.ReadAllText(_worldSavePath, Encoding.UTF8);
                    WorldSaveData loaded = JsonUtility.FromJson<WorldSaveData>(json);
                    if (loaded != null)
                    {
                        _currentWorldSave = loaded;
                        EnsureWorldListsNonNull(_currentWorldSave);
                        Debug.Log($"[SplitSaveManager] Loaded world_save.json (Day {_currentWorldSave.CurrentDay}, {_currentWorldSave.Structures.Count} structures, {_currentWorldSave.SharedChests.Count} chests).");
                        return _currentWorldSave;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SplitSaveManager] Failed to read world_save.json, creating backup and starting fresh: {ex.Message}");
                    TryBackupCorruptFile(_worldSavePath);
                }
            }

            _currentWorldSave = new WorldSaveData();
            SaveWorldNow();
            return _currentWorldSave;
        }

        public void MarkWorldDirty()
        {
            _isWorldDirty = true;
        }

        public void SaveWorldNow()
        {
            if (_currentWorldSave == null)
                return;

            string json = JsonUtility.ToJson(_currentWorldSave, prettyPrint: true);
            WriteJsonAtomic(_worldSavePath, json);
            _isWorldDirty = false;
            _autoSaveTimer = 0f;
            Debug.Log("[SplitSaveManager] Saved world_save.json atomically.");
        }

        public void UpsertStructureRecord(PlacedStructureRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.StructureGuid))
                return;

            var list = CurrentWorldSave.Structures;
            int idx = list.FindIndex(s => s.StructureGuid == record.StructureGuid);
            if (idx >= 0)
            {
                list[idx] = record;
            }
            else
            {
                list.Add(record);
            }
            MarkWorldDirty();
        }

        public void RemoveStructureRecord(string structureGuid)
        {
            int removed = CurrentWorldSave.Structures.RemoveAll(s => s.StructureGuid == structureGuid);
            if (removed > 0)
            {
                MarkWorldDirty();
            }
        }

        public SharedChestRecord GetOrCreateSharedChestRecord(string chestGuid)
        {
            var chests = CurrentWorldSave.SharedChests;
            SharedChestRecord found = chests.Find(c => c.ChestGuid == chestGuid);
            if (found == null)
            {
                found = new SharedChestRecord { ChestGuid = chestGuid };
                chests.Add(found);
                MarkWorldDirty();
            }
            return found;
        }

        public void UpsertSharedChestRecord(SharedChestRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.ChestGuid))
                return;

            var chests = CurrentWorldSave.SharedChests;
            int idx = chests.FindIndex(c => c.ChestGuid == record.ChestGuid);
            if (idx >= 0)
            {
                chests[idx] = record;
            }
            else
            {
                chests.Add(record);
            }
            MarkWorldDirty();
        }

        public void RecordHarvestedNode(string nodeUniqueId, double respawnAtTotalWorldHours)
        {
            if (string.IsNullOrEmpty(nodeUniqueId))
                return;

            var nodes = CurrentWorldSave.HarvestedNodes;
            int idx = nodes.FindIndex(n => n.NodeUniqueId == nodeUniqueId);
            var record = new HarvestedNodeRecord
            {
                NodeUniqueId = nodeUniqueId,
                RespawnAtTotalWorldHours = respawnAtTotalWorldHours
            };

            if (idx >= 0)
                nodes[idx] = record;
            else
                nodes.Add(record);

            MarkWorldDirty();
        }

        public bool TryGetHarvestedNodeRespawn(string nodeUniqueId, out double respawnAtTotalWorldHours)
        {
            var found = CurrentWorldSave.HarvestedNodes.Find(n => n.NodeUniqueId == nodeUniqueId);
            if (found != null)
            {
                respawnAtTotalWorldHours = found.RespawnAtTotalWorldHours;
                return true;
            }
            respawnAtTotalWorldHours = 0d;
            return false;
        }

        public void ClearHarvestedNode(string nodeUniqueId)
        {
            int removed = CurrentWorldSave.HarvestedNodes.RemoveAll(n => n.NodeUniqueId == nodeUniqueId);
            if (removed > 0)
            {
                MarkWorldDirty();
            }
        }

        #endregion

        #region Player Save Operations

        public PlayerSaveData LoadOrCreatePlayerSave(string playerUuid, Vector3 defaultSpawnPosition)
        {
            string sanitizedUuid = SanitizeFileName(playerUuid);
            string playerPath = GetPlayerSavePath(sanitizedUuid);

            if (File.Exists(playerPath))
            {
                try
                {
                    string json = File.ReadAllText(playerPath, Encoding.UTF8);
                    PlayerSaveData loaded = JsonUtility.FromJson<PlayerSaveData>(json);
                    if (loaded != null)
                    {
                        loaded.PlayerUUID = sanitizedUuid;
                        if (loaded.Inventory == null)
                            loaded.Inventory = new List<InventorySlotRecord>();
                        return loaded;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SplitSaveManager] Corrupt player save for {sanitizedUuid}: {ex.Message}");
                    TryBackupCorruptFile(playerPath);
                }
            }

            var newSave = new PlayerSaveData
            {
                PlayerUUID = sanitizedUuid,
                LastPosition = SerializableVector3.FromVector3(defaultSpawnPosition),
                LastRotationY = 0f,
                CurrentHP = 100f,
                CurrentStamina = 100f,
                LastSavedUtcTicks = DateTime.UtcNow.Ticks
            };

            SavePlayerNow(newSave);
            return newSave;
        }

        public void RegisterActivePlayerSave(ulong clientId, PlayerSaveData saveData)
        {
            if (saveData == null)
                return;

            _activePlayerSaves[clientId] = saveData;
        }

        public PlayerSaveData GetActivePlayerSave(ulong clientId)
        {
            _activePlayerSaves.TryGetValue(clientId, out PlayerSaveData save);
            return save;
        }

        public void SavePlayerByClientId(ulong clientId, bool removeFromActiveCache = false)
        {
            if (!_activePlayerSaves.TryGetValue(clientId, out PlayerSaveData saveData))
                return;

            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) &&
                client.PlayerObject != null)
            {
                var controller = client.PlayerObject.GetComponent<NetworkPlayerController>();
                if (controller != null)
                {
                    controller.ExportSaveData(saveData);
                }
            }

            SavePlayerNow(saveData);

            if (removeFromActiveCache)
            {
                _activePlayerSaves.Remove(clientId);
            }
        }

        public void SavePlayerNow(PlayerSaveData saveData)
        {
            if (saveData == null || string.IsNullOrEmpty(saveData.PlayerUUID))
                return;

            saveData.LastSavedUtcTicks = DateTime.UtcNow.Ticks;
            string path = GetPlayerSavePath(SanitizeFileName(saveData.PlayerUUID));
            string json = JsonUtility.ToJson(saveData, prettyPrint: true);
            WriteJsonAtomic(path, json);
        }

        public void FlushAllSavesToDisk()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                foreach (ulong clientId in new List<ulong>(_activePlayerSaves.Keys))
                {
                    SavePlayerByClientId(clientId, removeFromActiveCache: false);
                }
            }

            SaveWorldNow();
            CreateRollingWorldBackup();
            Debug.Log("[SplitSaveManager] Flushed all active player saves and world save to disk.");
        }

        public void CreateRollingWorldBackup()
        {
            if (_currentWorldSave == null || string.IsNullOrEmpty(_backupDirectory) || maxRollingBackupSlots <= 0)
                return;

            string slotPath = Path.Combine(_backupDirectory, $"world_save_slot_{_nextBackupSlot}.json");
            string json = JsonUtility.ToJson(_currentWorldSave, prettyPrint: true);
            WriteJsonAtomic(slotPath, json);
            _nextBackupSlot = (_nextBackupSlot + 1) % maxRollingBackupSlots;
        }

        public void AppendCampLogRecord(string timestampText, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var logs = CurrentWorldSave.CampLogs;
            logs.Add(new CampLogEntryRecord(timestampText, message));
            while (logs.Count > 30)
            {
                logs.RemoveAt(0);
            }
            MarkWorldDirty();
        }

        #endregion

        #region Atomic File I/O Helpers

        private string GetPlayerSavePath(string sanitizedUuid)
        {
            return Path.Combine(_playersDirectory, $"{sanitizedUuid}.json");
        }

        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "anonymous_player";

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            return sb.ToString();
        }

        private static void EnsureWorldListsNonNull(WorldSaveData data)
        {
            data.Structures ??= new List<PlacedStructureRecord>();
            data.SharedChests ??= new List<SharedChestRecord>();
            data.HarvestedNodes ??= new List<HarvestedNodeRecord>();
            data.CampLogs ??= new List<CampLogEntryRecord>();
            data.IslandCoreLevel = Mathf.Clamp(data.IslandCoreLevel, 1, 5);
        }

        private static void WriteJsonAtomic(string targetPath, string json)
        {
            string tempPath = targetPath + ".tmp";
            string backupPath = targetPath + ".bak";

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SplitSaveManager] Atomic write failed for {targetPath}: {ex.Message}");
            }
        }

        private static void TryBackupCorruptFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string corruptBackup = $"{path}.corrupt_{DateTime.UtcNow:yyyyMMddHHmmss}";
                    File.Move(path, corruptBackup);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SplitSaveManager] Could not back up corrupt file {path}: {ex.Message}");
            }
        }

        #endregion
    }
}
