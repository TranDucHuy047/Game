using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Player;

namespace CoralArchipelago.UI
{
    public enum TransferBatchMode
    {
        One = 0,
        Five = 1,
        All = 2
    }

    /// <summary>
    /// Full Grid UI Modal for Personal Bag (20 slots) and nearby Shared Chest (24 slots).
    /// Subscribes live to NetworkList changes so simultaneous asynchronous players see
    /// chest transactions update immediately without item duplication.
    /// </summary>
    public class InventoryGridUI : MonoBehaviour
    {
        public static InventoryGridUI Instance { get; private set; }

        [Header("Modal & Grid Containers")]
        [SerializeField] private GameObject modalRoot;
        [SerializeField] private Transform playerGridContainer;
        [SerializeField] private Transform chestGridContainer;
        [SerializeField] private GameObject chestAvailablePanel;
        [SerializeField] private GameObject noChestHintObject;
        [SerializeField] private InventorySlotButtonUI slotButtonPrefab;

        [Header("Labels & Controls")]
        [SerializeField] private Text headerTitleText;
        [SerializeField] private Text transferModeButtonText;
        [SerializeField] private int playerSlotCapacity = 20;
        [SerializeField] private int chestSlotCapacity = 24;
        [SerializeField] private float chestSearchRange = 4.5f;

        private readonly List<InventorySlotButtonUI> _playerSlotPool = new List<InventorySlotButtonUI>();
        private readonly List<InventorySlotButtonUI> _chestSlotPool = new List<InventorySlotButtonUI>();

        private PlayerInventory _boundPlayerInventory;
        private SharedChestInventory _boundChest;
        private TransferBatchMode _transferMode = TransferBatchMode.One;
        private float _proximityCheckTimer;

        public bool IsOpen => modalRoot != null && modalRoot.activeSelf;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (modalRoot != null)
                modalRoot.SetActive(false);
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
        }

        private void Update()
        {
            if (!IsOpen)
                return;

            _proximityCheckTimer += Time.unscaledDeltaTime;
            if (_proximityCheckTimer >= 0.4f)
            {
                _proximityCheckTimer = 0f;
                RefreshNearbyChestBinding();
            }
        }

        public void ToggleModal()
        {
            if (IsOpen)
                CloseModal();
            else
                OpenModal();
        }

        public void OpenModal()
        {
            var localPlayer = NetworkPlayerController.LocalPlayer;
            if (localPlayer == null)
                return;

            EnsureSlotPoolsInitialized();
            BindPlayerAndChest(localPlayer);

            if (modalRoot != null)
                modalRoot.SetActive(true);

            UpdateTransferModeLabel();
            RefreshUI();
        }

        public void CloseModal()
        {
            UnsubscribeEvents();
            if (modalRoot != null)
                modalRoot.SetActive(false);
        }

        public void OnClickCycleTransferMode()
        {
            _transferMode = (TransferBatchMode)(((int)_transferMode + 1) % 3);
            UpdateTransferModeLabel();
        }

        private void BindPlayerAndChest(NetworkPlayerController localPlayer)
        {
            UnsubscribeEvents();

            _boundPlayerInventory = localPlayer.GetComponent<PlayerInventory>();
            if (_boundPlayerInventory != null)
            {
                _boundPlayerInventory.OnInventoryChanged += RefreshUI;
            }

            _boundChest = localPlayer.FindNearestSharedChest(chestSearchRange);
            if (_boundChest != null)
            {
                _boundChest.OnChestChanged += RefreshUI;
            }
        }

        private void RefreshNearbyChestBinding()
        {
            var localPlayer = NetworkPlayerController.LocalPlayer;
            if (localPlayer == null)
            {
                CloseModal();
                return;
            }

            SharedChestInventory nearest = localPlayer.FindNearestSharedChest(chestSearchRange);
            if (nearest == _boundChest)
                return;

            if (_boundChest != null)
                _boundChest.OnChestChanged -= RefreshUI;

            _boundChest = nearest;

            if (_boundChest != null)
                _boundChest.OnChestChanged += RefreshUI;

            RefreshUI();
        }

        private void UnsubscribeEvents()
        {
            if (_boundPlayerInventory != null)
            {
                _boundPlayerInventory.OnInventoryChanged -= RefreshUI;
                _boundPlayerInventory = null;
            }

            if (_boundChest != null)
            {
                _boundChest.OnChestChanged -= RefreshUI;
                _boundChest = null;
            }
        }

        private void EnsureSlotPoolsInitialized()
        {
            if (slotButtonPrefab == null)
                return;

            if (_playerSlotPool.Count == 0 && playerGridContainer != null)
            {
                for (int i = 0; i < playerSlotCapacity; i++)
                {
                    InventorySlotButtonUI btn = Instantiate(slotButtonPrefab, playerGridContainer);
                    btn.name = $"PlayerSlot_{i:00}";
                    _playerSlotPool.Add(btn);
                }
            }

            if (_chestSlotPool.Count == 0 && chestGridContainer != null)
            {
                for (int i = 0; i < chestSlotCapacity; i++)
                {
                    InventorySlotButtonUI btn = Instantiate(slotButtonPrefab, chestGridContainer);
                    btn.name = $"ChestSlot_{i:00}";
                    _chestSlotPool.Add(btn);
                }
            }
        }

        private void RefreshUI()
        {
            bool hasChest = _boundChest != null && _boundChest.IsSpawned;

            if (chestAvailablePanel != null)
                chestAvailablePanel.SetActive(hasChest);
            if (noChestHintObject != null)
                noChestHintObject.SetActive(!hasChest);

            if (headerTitleText != null)
            {
                headerTitleText.text = hasChest
                    ? "Personal Bag  <->  Shared Chest (Tap slot to transfer)"
                    : "Personal Bag (Walk near a Shared Chest to store items)";
            }

            // Refresh personal bag slots
            int playerUsedCount = _boundPlayerInventory != null ? _boundPlayerInventory.Slots.Count : 0;
            for (int i = 0; i < _playerSlotPool.Count; i++)
            {
                if (i < playerUsedCount)
                {
                    NetworkInventorySlot slot = _boundPlayerInventory.Slots[i];
                    _playerSlotPool[i].Configure(false, i, slot.ItemId.ToString(), slot.Amount, HandleSlotClicked);
                }
                else
                {
                    _playerSlotPool[i].Configure(false, i, null, 0, null);
                }
            }

            // Refresh shared chest slots
            int chestUsedCount = hasChest ? _boundChest.ChestSlots.Count : 0;
            for (int i = 0; i < _chestSlotPool.Count; i++)
            {
                if (hasChest && i < chestUsedCount)
                {
                    NetworkInventorySlot slot = _boundChest.ChestSlots[i];
                    _chestSlotPool[i].Configure(true, i, slot.ItemId.ToString(), slot.Amount, HandleSlotClicked);
                }
                else
                {
                    _chestSlotPool[i].Configure(true, i, null, 0, null);
                }
            }
        }

        private void HandleSlotClicked(bool isChestSlot, int slotIndex, string itemId)
        {
            if (_boundChest == null || !_boundChest.IsSpawned || string.IsNullOrEmpty(itemId))
                return;

            if (isChestSlot)
            {
                if (slotIndex < 0 || slotIndex >= _boundChest.ChestSlots.Count)
                    return;

                int available = _boundChest.ChestSlots[slotIndex].Amount;
                int transferAmount = ComputeTransferAmount(available);
                if (transferAmount > 0)
                {
                    _boundChest.WithdrawItemServerRpc(slotIndex, transferAmount);
                }
            }
            else
            {
                if (_boundPlayerInventory == null || slotIndex < 0 || slotIndex >= _boundPlayerInventory.Slots.Count)
                    return;

                int available = _boundPlayerInventory.Slots[slotIndex].Amount;
                int transferAmount = ComputeTransferAmount(available);
                if (transferAmount > 0)
                {
                    _boundChest.DepositItemServerRpc(itemId, transferAmount);
                }
            }
        }

        private int ComputeTransferAmount(int availableInSlot)
        {
            if (availableInSlot <= 0)
                return 0;

            switch (_transferMode)
            {
                case TransferBatchMode.One: return 1;
                case TransferBatchMode.Five: return Mathf.Min(5, availableInSlot);
                case TransferBatchMode.All: return availableInSlot;
                default: return 1;
            }
        }

        private void UpdateTransferModeLabel()
        {
            if (transferModeButtonText == null)
                return;

            switch (_transferMode)
            {
                case TransferBatchMode.One:
                    transferModeButtonText.text = "Transfer: x1";
                    break;
                case TransferBatchMode.Five:
                    transferModeButtonText.text = "Transfer: x5";
                    break;
                case TransferBatchMode.All:
                    transferModeButtonText.text = "Transfer: All";
                    break;
            }
        }
    }
}
