using System;
using UnityEngine;
using UnityEngine.UI;

namespace CoralArchipelago.UI
{
    /// <summary>
    /// Reusable UI slot button for both Personal Bag and Shared Chest grids.
    /// Displays item label, stack count, and visual color coding for touch readability on iPhone.
    /// </summary>
    public class InventorySlotButtonUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private Button slotButton;
        [SerializeField] private Image slotBackground;
        [SerializeField] private Text itemLabelText;
        [SerializeField] private Text amountText;

        [Header("Item Visual Colors")]
        [SerializeField] private Color emptySlotColor = new Color(0.11f, 0.15f, 0.22f, 0.65f);
        [SerializeField] private Color coralWoodColor = new Color(0.78f, 0.42f, 0.28f, 0.92f);
        [SerializeField] private Color glowCrystalColor = new Color(0.14f, 0.72f, 0.88f, 0.92f);
        [SerializeField] private Color defaultItemColor = new Color(0.28f, 0.45f, 0.52f, 0.92f);

        private bool _isChestSlot;
        private int _slotIndex;
        private string _itemId;
        private Action<bool, int, string> _onClicked;

        private void Awake()
        {
            if (slotButton == null)
                slotButton = GetComponent<Button>();

            if (slotBackground == null)
                slotBackground = GetComponent<Image>();

            if (slotButton != null)
                slotButton.onClick.AddListener(HandleButtonClick);
        }

        private void OnDestroy()
        {
            if (slotButton != null)
                slotButton.onClick.RemoveListener(HandleButtonClick);
        }

        public void Configure(
            bool isChestSlot,
            int slotIndex,
            string itemId,
            int amount,
            Action<bool, int, string> onClicked)
        {
            _isChestSlot = isChestSlot;
            _slotIndex = slotIndex;
            _itemId = itemId;
            _onClicked = onClicked;

            bool hasItem = !string.IsNullOrEmpty(itemId) && amount > 0;

            if (slotButton != null)
                slotButton.interactable = hasItem;

            if (!hasItem)
            {
                if (slotBackground != null)
                    slotBackground.color = emptySlotColor;
                if (itemLabelText != null)
                    itemLabelText.text = string.Empty;
                if (amountText != null)
                    amountText.text = string.Empty;
                return;
            }

            if (slotBackground != null)
                slotBackground.color = ResolveColorForItem(itemId);

            if (itemLabelText != null)
                itemLabelText.text = FormatDisplayName(itemId);

            if (amountText != null)
                amountText.text = $"x{amount}";
        }

        private void HandleButtonClick()
        {
            if (string.IsNullOrEmpty(_itemId))
                return;

            _onClicked?.Invoke(_isChestSlot, _slotIndex, _itemId);
        }

        private Color ResolveColorForItem(string itemId)
        {
            switch (itemId)
            {
                case "coral_wood": return coralWoodColor;
                case "glow_crystal": return glowCrystalColor;
                default: return defaultItemColor;
            }
        }

        private static string FormatDisplayName(string itemId)
        {
            switch (itemId)
            {
                case "coral_wood": return "Coral\nWood";
                case "glow_crystal": return "Glow\nCrystal";
                default: return itemId.Replace('_', '\n');
            }
        }
    }
}
