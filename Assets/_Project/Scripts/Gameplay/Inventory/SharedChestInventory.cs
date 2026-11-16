using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Onboarding;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Inventory
{
    /// <summary>
    /// Server-authoritative Shared Chest inventory for asynchronous PvE cooperation.
    /// Executes atomic check-and-mutate transactions on the Server main thread so simultaneous
    /// withdrawals by multiple players can never duplicate items.
    /// </summary>
    public class SharedChestInventory : NetworkBehaviour
    {
        [Header("Chest Configuration")]
        [SerializeField] private string sceneFallbackChestGuid;
        [SerializeField] private int maxChestSlots = 24;
        [SerializeField] private int maxStackSize = 99;
        [SerializeField] private float maxInteractDistance = 5.0f;

        public readonly NetworkVariable<FixedString64Bytes> ChestGuid = new NetworkVariable<FixedString64Bytes>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkList<NetworkInventorySlot> ChestSlots = new NetworkList<NetworkInventorySlot>(
            readPerm: NetworkVariableReadPermission.Everyone,
            writePerm: NetworkVariableWritePermission.Server);

        public event Action OnChestChanged;

        public override void OnNetworkSpawn()
        {
            ChestSlots.OnListChanged += HandleChestChanged;

            if (IsServer && ChestGuid.Value.IsEmpty)
            {
                string guid = string.IsNullOrEmpty(sceneFallbackChestGuid)
                    ? $"scene_chest_{NetworkObjectId}"
                    : sceneFallbackChestGuid;
                InitializeChestServer(guid);
            }

            OnChestChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            ChestSlots.OnListChanged -= HandleChestChanged;
        }

        private void HandleChestChanged(NetworkListEvent<NetworkInventorySlot> changeEvent)
        {
            OnChestChanged?.Invoke();
        }

        /// <summary>
        /// Server-only: Binds this chest to a persistent GUID and restores saved items from world_save.json.
        /// </summary>
        public void InitializeChestServer(string guid)
        {
            if (!IsServer || string.IsNullOrEmpty(guid))
                return;

            ChestGuid.Value = guid;
            ChestSlots.Clear();

            if (SplitSaveManager.Instance == null)
                return;

            SharedChestRecord record = SplitSaveManager.Instance.GetOrCreateSharedChestRecord(guid);
            if (record?.Items == null)
                return;

            for (int i = 0; i < record.Items.Count && i < maxChestSlots; i++)
            {
                InventorySlotRecord item = record.Items[i];
                if (item != null && !string.IsNullOrEmpty(item.ItemId) && item.Amount > 0)
                {
                    ChestSlots.Add(new NetworkInventorySlot(item.ItemId, Mathf.Min(item.Amount, maxStackSize)));
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void DepositItemServerRpc(FixedString64Bytes itemId, int amount, ServerRpcParams rpcParams = default)
        {
            if (amount <= 0 || itemId.IsEmpty)
                return;

            if (!TryGetAuthorizedPlayerInventory(rpcParams.Receive.SenderClientId, out PlayerInventory playerInv))
                return;

            string itemStr = itemId.ToString();
            int availableInBag = playerInv.GetItemCount(itemStr);
            int toDeposit = Mathf.Min(amount, availableInBag);
            if (toDeposit <= 0)
                return;

            int capacity = CalculateChestFreeCapacity(itemId);
            int actualTransfer = Mathf.Min(toDeposit, capacity);
            if (actualTransfer <= 0)
                return;

            if (!playerInv.RemoveItemServer(itemStr, actualTransfer))
                return;

            int leftover = AddToChestInternal(itemId, actualTransfer);
            if (leftover > 0)
            {
                playerInv.AddItemServer(itemStr, leftover);
            }

            PersistChestToWorldSave();
            SharedCampLogManager.Instance?.RecordActivityServer($"Stored +{actualTransfer} {itemStr} in chest.");

            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out NetworkClient senderClient) &&
                senderClient.PlayerObject != null)
            {
                senderClient.PlayerObject.GetComponent<PlayerOnboardingController>()?.NotifyCoopChestInteractionServer();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void WithdrawItemServerRpc(int slotIndex, int amount, ServerRpcParams rpcParams = default)
        {
            if (amount <= 0 || slotIndex < 0 || slotIndex >= ChestSlots.Count)
                return;

            if (!TryGetAuthorizedPlayerInventory(rpcParams.Receive.SenderClientId, out PlayerInventory playerInv))
                return;

            NetworkInventorySlot chestSlot = ChestSlots[slotIndex];
            if (chestSlot.Amount <= 0 || chestSlot.ItemId.IsEmpty)
                return;

            int requested = Mathf.Min(amount, chestSlot.Amount);
            int unadded = playerInv.AddItemServer(chestSlot.ItemId.ToString(), requested);
            int actuallyTransferred = requested - unadded;

            if (actuallyTransferred <= 0)
                return;

            string takenItemId = chestSlot.ItemId.ToString();
            if (chestSlot.Amount <= actuallyTransferred)
            {
                ChestSlots.RemoveAt(slotIndex);
            }
            else
            {
                chestSlot.Amount -= actuallyTransferred;
                ChestSlots[slotIndex] = chestSlot;
            }

            PersistChestToWorldSave();
            SharedCampLogManager.Instance?.RecordActivityServer($"Took -{actuallyTransferred} {takenItemId} from chest.");
        }

        public int GetItemCount(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return 0;

            FixedString64Bytes key = itemId;
            int total = 0;
            for (int i = 0; i < ChestSlots.Count; i++)
            {
                if (ChestSlots[i].ItemId.Equals(key))
                    total += ChestSlots[i].Amount;
            }
            return total;
        }

        /// <summary>
        /// Server-only: Consumes up to maxAmount of itemId for cooperative proximity crafting and saves chest state.
        /// </summary>
        public int ConsumeItemUpToServer(string itemId, int maxAmount)
        {
            if (!IsServer || string.IsNullOrEmpty(itemId) || maxAmount <= 0)
                return 0;

            FixedString64Bytes key = itemId;
            int remaining = maxAmount;
            int consumed = 0;

            for (int i = ChestSlots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                NetworkInventorySlot slot = ChestSlots[i];
                if (!slot.ItemId.Equals(key) || slot.Amount <= 0)
                    continue;

                if (slot.Amount <= remaining)
                {
                    consumed += slot.Amount;
                    remaining -= slot.Amount;
                    ChestSlots.RemoveAt(i);
                }
                else
                {
                    consumed += remaining;
                    slot.Amount -= remaining;
                    ChestSlots[i] = slot;
                    remaining = 0;
                }
            }

            if (consumed > 0)
            {
                PersistChestToWorldSave();
            }

            return consumed;
        }

        private bool TryGetAuthorizedPlayerInventory(ulong senderClientId, out PlayerInventory playerInventory)
        {
            playerInventory = null;
            if (NetworkManager.Singleton == null ||
                !NetworkManager.Singleton.ConnectedClients.TryGetValue(senderClientId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return false;
            }

            float dist = Vector3.Distance(client.PlayerObject.transform.position, transform.position);
            if (dist > maxInteractDistance)
                return false;

            playerInventory = client.PlayerObject.GetComponent<PlayerInventory>();
            return playerInventory != null;
        }

        private int CalculateChestFreeCapacity(FixedString64Bytes itemId)
        {
            int capacity = 0;
            for (int i = 0; i < ChestSlots.Count; i++)
            {
                if (ChestSlots[i].ItemId.Equals(itemId) && ChestSlots[i].Amount < maxStackSize)
                {
                    capacity += maxStackSize - ChestSlots[i].Amount;
                }
            }

            int freeSlots = Mathf.Max(0, maxChestSlots - ChestSlots.Count);
            capacity += freeSlots * maxStackSize;
            return capacity;
        }

        private int AddToChestInternal(FixedString64Bytes itemId, int amount)
        {
            int remaining = amount;

            for (int i = 0; i < ChestSlots.Count && remaining > 0; i++)
            {
                NetworkInventorySlot slot = ChestSlots[i];
                if (!slot.ItemId.Equals(itemId) || slot.Amount >= maxStackSize)
                    continue;

                int add = Mathf.Min(remaining, maxStackSize - slot.Amount);
                slot.Amount += add;
                ChestSlots[i] = slot;
                remaining -= add;
            }

            while (remaining > 0 && ChestSlots.Count < maxChestSlots)
            {
                int add = Mathf.Min(remaining, maxStackSize);
                ChestSlots.Add(new NetworkInventorySlot(itemId, add));
                remaining -= add;
            }

            return remaining;
        }

        private void PersistChestToWorldSave()
        {
            if (SplitSaveManager.Instance == null || ChestGuid.Value.IsEmpty)
                return;

            var record = new SharedChestRecord
            {
                ChestGuid = ChestGuid.Value.ToString(),
                Items = new List<InventorySlotRecord>(ChestSlots.Count)
            };

            for (int i = 0; i < ChestSlots.Count; i++)
            {
                NetworkInventorySlot slot = ChestSlots[i];
                if (slot.Amount > 0 && !slot.ItemId.IsEmpty)
                {
                    record.Items.Add(new InventorySlotRecord(slot.ItemId.ToString(), slot.Amount));
                }
            }

            SplitSaveManager.Instance.UpsertSharedChestRecord(record);
        }
    }
}
