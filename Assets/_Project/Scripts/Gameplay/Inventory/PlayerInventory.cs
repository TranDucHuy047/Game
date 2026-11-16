using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Inventory
{
    /// <summary>
    /// Network-serializable inventory slot used by both PlayerInventory and SharedChestInventory.
    /// </summary>
    public struct NetworkInventorySlot : INetworkSerializable, IEquatable<NetworkInventorySlot>
    {
        public FixedString64Bytes ItemId;
        public int Amount;

        public NetworkInventorySlot(FixedString64Bytes itemId, int amount)
        {
            ItemId = itemId;
            Amount = amount;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ItemId);
            serializer.SerializeValue(ref Amount);
        }

        public bool Equals(NetworkInventorySlot other)
        {
            return ItemId.Equals(other.ItemId) && Amount == other.Amount;
        }

        public override bool Equals(object obj) => obj is NetworkInventorySlot other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ItemId, Amount);
    }

    /// <summary>
    /// Server-authoritative personal player bag synced to clients via NetworkList.
    /// </summary>
    public class PlayerInventory : NetworkBehaviour
    {
        [Header("Bag Limits")]
        [SerializeField] private int maxSlots = 20;
        [SerializeField] private int maxStackSize = 99;

        public readonly NetworkList<NetworkInventorySlot> Slots = new NetworkList<NetworkInventorySlot>(
            readPerm: NetworkVariableReadPermission.Everyone,
            writePerm: NetworkVariableWritePermission.Server);

        public int MaxSlots => maxSlots;
        public int MaxStackSize => maxStackSize;

        public event Action OnInventoryChanged;

        public override void OnNetworkSpawn()
        {
            Slots.OnListChanged += HandleListChanged;
            OnInventoryChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            Slots.OnListChanged -= HandleListChanged;
        }

        private void HandleListChanged(NetworkListEvent<NetworkInventorySlot> changeEvent)
        {
            OnInventoryChanged?.Invoke();
        }

        public int GetItemCount(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return 0;

            FixedString64Bytes key = itemId;
            int total = 0;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].ItemId.Equals(key))
                    total += Slots[i].Amount;
            }
            return total;
        }

        public bool HasItem(string itemId, int requiredAmount)
        {
            return requiredAmount <= 0 || GetItemCount(itemId) >= requiredAmount;
        }

        /// <summary>
        /// Server-only: Adds items into existing stacks or new slots. Returns leftover amount that could not fit.
        /// </summary>
        public int AddItemServer(string itemId, int amount)
        {
            if (!IsServer || string.IsNullOrEmpty(itemId) || amount <= 0)
                return amount;

            FixedString64Bytes key = itemId;
            int remaining = amount;

            // 1. Fill existing partial stacks
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                NetworkInventorySlot slot = Slots[i];
                if (!slot.ItemId.Equals(key) || slot.Amount >= maxStackSize)
                    continue;

                int canAdd = Mathf.Min(remaining, maxStackSize - slot.Amount);
                slot.Amount += canAdd;
                Slots[i] = slot;
                remaining -= canAdd;
            }

            // 2. Open new slots up to maxSlots
            while (remaining > 0 && Slots.Count < maxSlots)
            {
                int add = Mathf.Min(remaining, maxStackSize);
                Slots.Add(new NetworkInventorySlot(key, add));
                remaining -= add;
            }

            return remaining;
        }

        /// <summary>
        /// Server-only: Removes exact required amount if available. Returns true on success.
        /// </summary>
        public bool RemoveItemServer(string itemId, int amount)
        {
            if (!IsServer || string.IsNullOrEmpty(itemId) || amount <= 0)
                return false;

            if (!HasItem(itemId, amount))
                return false;

            FixedString64Bytes key = itemId;
            int remaining = amount;

            for (int i = Slots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                NetworkInventorySlot slot = Slots[i];
                if (!slot.ItemId.Equals(key))
                    continue;

                if (slot.Amount <= remaining)
                {
                    remaining -= slot.Amount;
                    Slots.RemoveAt(i);
                }
                else
                {
                    slot.Amount -= remaining;
                    Slots[i] = slot;
                    remaining = 0;
                }
            }

            return true;
        }

        public void LoadFromSaveServer(List<InventorySlotRecord> savedSlots)
        {
            if (!IsServer)
                return;

            Slots.Clear();
            if (savedSlots == null)
                return;

            for (int i = 0; i < savedSlots.Count && i < maxSlots; i++)
            {
                InventorySlotRecord rec = savedSlots[i];
                if (rec == null || string.IsNullOrEmpty(rec.ItemId) || rec.Amount <= 0)
                    continue;

                Slots.Add(new NetworkInventorySlot(rec.ItemId, Mathf.Min(rec.Amount, maxStackSize)));
            }
        }

        public void ExportToSave(List<InventorySlotRecord> targetList)
        {
            if (targetList == null)
                return;

            targetList.Clear();
            for (int i = 0; i < Slots.Count; i++)
            {
                NetworkInventorySlot slot = Slots[i];
                if (slot.Amount > 0 && !slot.ItemId.IsEmpty)
                {
                    targetList.Add(new InventorySlotRecord(slot.ItemId.ToString(), slot.Amount));
                }
            }
        }
    }
}
