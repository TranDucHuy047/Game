using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Onboarding;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Building
{
    [Serializable]
    public class BuildableDefinition
    {
        public string PrefabId;
        public GameObject Prefab;
        public string CostItemId = "coral_wood";
        public int CostAmount = 2;
        public float MaxHP = 100f;
    }

    /// <summary>
    /// Server-authoritative structure placement and world persistence manager.
    /// Reconstructs all player-built floors, glowing coral lamps, and shared chests from world_save.json on startup.
    /// </summary>
    public class NetworkBuildingManager : NetworkBehaviour
    {
        public static NetworkBuildingManager Instance { get; private set; }

        [Header("Buildable Catalog")]
        [SerializeField] private List<BuildableDefinition> buildables = new List<BuildableDefinition>();

        [Header("Placement Constraints")]
        [SerializeField] private float maxBuildRange = 8.0f;
        [SerializeField] private float demolishRange = 4.0f;
        [SerializeField] private float gridSnapSize = 1.0f;

        private readonly Dictionary<string, BuildableDefinition> _catalogById = new Dictionary<string, BuildableDefinition>();
        private readonly Dictionary<ulong, string> _spawnedGuidsByNetId = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _spawnedPrefabIdsByNetId = new Dictionary<ulong, string>();
        private readonly List<NetworkObject> _spawnedStructureObjects = new List<NetworkObject>();
        private bool _worldRestored;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            _catalogById.Clear();
            for (int i = 0; i < buildables.Count; i++)
            {
                BuildableDefinition def = buildables[i];
                if (def != null && !string.IsNullOrEmpty(def.PrefabId) && def.Prefab != null)
                {
                    _catalogById[def.PrefabId] = def;
                }
            }
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer && !_worldRestored)
            {
                RestoreWorldStructuresServer();
            }
        }

        private void RestoreWorldStructuresServer()
        {
            _worldRestored = true;
            if (SplitSaveManager.Instance == null)
                return;

            List<PlacedStructureRecord> savedStructures = SplitSaveManager.Instance.CurrentWorldSave.Structures;
            for (int i = 0; i < savedStructures.Count; i++)
            {
                PlacedStructureRecord rec = savedStructures[i];
                if (rec == null || !_catalogById.TryGetValue(rec.PrefabId, out BuildableDefinition def))
                    continue;

                SpawnStructureInternal(def, rec.StructureGuid, rec.Position.ToVector3(), rec.RotationY);
            }

            Debug.Log($"[NetworkBuildingManager] Restored {savedStructures.Count} world structures from world_save.json.");
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestBuildStructureServerRpc(
            FixedString64Bytes prefabId,
            Vector3 targetPosition,
            float rotationY,
            ServerRpcParams rpcParams = default)
        {
            string id = prefabId.ToString();
            if (!_catalogById.TryGetValue(id, out BuildableDefinition def))
                return;

            ulong senderId = rpcParams.Receive.SenderClientId;
            if (NetworkManager.Singleton == null ||
                !NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return;
            }

            if (Vector3.Distance(client.PlayerObject.transform.position, targetPosition) > maxBuildRange)
                return;

            PlayerInventory playerInv = client.PlayerObject.GetComponent<PlayerInventory>();
            if (playerInv == null)
                return;

            if (!string.IsNullOrEmpty(def.CostItemId) && def.CostAmount > 0)
            {
                if (!playerInv.RemoveItemServer(def.CostItemId, def.CostAmount))
                    return;
            }

            Vector3 snappedPos = SnapToGrid(targetPosition);
            string newGuid = Guid.NewGuid().ToString("N");

            if (!SpawnStructureInternal(def, newGuid, snappedPos, rotationY))
            {
                // Refund cost if spawn failed
                if (!string.IsNullOrEmpty(def.CostItemId) && def.CostAmount > 0)
                {
                    playerInv.AddItemServer(def.CostItemId, def.CostAmount);
                }
                return;
            }

            SplitSaveManager.Instance?.UpsertStructureRecord(new PlacedStructureRecord
            {
                StructureGuid = newGuid,
                PrefabId = id,
                Position = SerializableVector3.FromVector3(snappedPos),
                RotationY = rotationY,
                CurrentHP = def.MaxHP
            });

            client.PlayerObject.GetComponent<PlayerOnboardingController>()?.NotifyStructureBuiltServer(id);
            SharedCampLogManager.Instance?.RecordActivityServer($"Built {id} at camp.");
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestDemolishNearestStructureServerRpc(ServerRpcParams rpcParams = default)
        {
            ulong senderId = rpcParams.Receive.SenderClientId;
            if (NetworkManager.Singleton == null ||
                !NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return;
            }

            Vector3 playerPos = client.PlayerObject.transform.position;
            NetworkObject nearestNetObj = null;
            float bestDistSq = demolishRange * demolishRange;

            for (int i = _spawnedStructureObjects.Count - 1; i >= 0; i--)
            {
                NetworkObject candidate = _spawnedStructureObjects[i];
                if (candidate == null || !candidate.IsSpawned)
                {
                    _spawnedStructureObjects.RemoveAt(i);
                    continue;
                }

                float dSq = (candidate.transform.position - playerPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    nearestNetObj = candidate;
                }
            }

            if (nearestNetObj == null)
                return;

            // Protect teammates' items: block demolishing a Shared Chest while it still holds items
            SharedChestInventory chest = nearestNetObj.GetComponent<SharedChestInventory>();
            if (chest != null && chest.ChestSlots.Count > 0)
            {
                Debug.Log("[NetworkBuildingManager] Demolish blocked: Shared Chest still contains items.");
                return;
            }

            PlayerInventory playerInv = client.PlayerObject.GetComponent<PlayerInventory>();
            if (playerInv != null &&
                _spawnedPrefabIdsByNetId.TryGetValue(nearestNetObj.NetworkObjectId, out string prefabId) &&
                _catalogById.TryGetValue(prefabId, out BuildableDefinition def) &&
                !string.IsNullOrEmpty(def.CostItemId) &&
                def.CostAmount > 0)
            {
                playerInv.AddItemServer(def.CostItemId, def.CostAmount);
            }

            DestroyStructureServer(nearestNetObj);
        }

        public void DestroyStructureServer(NetworkObject structureNetObj)
        {
            if (!IsServer || structureNetObj == null || !structureNetObj.IsSpawned)
                return;

            if (_spawnedGuidsByNetId.TryGetValue(structureNetObj.NetworkObjectId, out string guid))
            {
                SplitSaveManager.Instance?.RemoveStructureRecord(guid);
                _spawnedGuidsByNetId.Remove(structureNetObj.NetworkObjectId);
            }

            _spawnedPrefabIdsByNetId.Remove(structureNetObj.NetworkObjectId);
            _spawnedStructureObjects.Remove(structureNetObj);

            structureNetObj.Despawn(destroy: true);
        }

        private bool SpawnStructureInternal(BuildableDefinition def, string structureGuid, Vector3 pos, float rotY)
        {
            GameObject instance = Instantiate(def.Prefab, pos, Quaternion.Euler(0f, rotY, 0f));
            NetworkObject netObj = instance.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Debug.LogError($"[NetworkBuildingManager] Buildable prefab '{def.PrefabId}' is missing a NetworkObject component!");
                Destroy(instance);
                return false;
            }

            netObj.Spawn(destroyWithScene: true);
            _spawnedGuidsByNetId[netObj.NetworkObjectId] = structureGuid;
            _spawnedPrefabIdsByNetId[netObj.NetworkObjectId] = def.PrefabId;
            _spawnedStructureObjects.Add(netObj);

            SharedChestInventory chest = instance.GetComponent<SharedChestInventory>();
            if (chest != null)
            {
                chest.InitializeChestServer(structureGuid);
            }

            return true;
        }

        private Vector3 SnapToGrid(Vector3 rawPos)
        {
            if (gridSnapSize <= 0.01f)
                return rawPos;

            return new Vector3(
                Mathf.Round(rawPos.x / gridSnapSize) * gridSnapSize,
                rawPos.y,
                Mathf.Round(rawPos.z / gridSnapSize) * gridSnapSize);
        }
    }
}
