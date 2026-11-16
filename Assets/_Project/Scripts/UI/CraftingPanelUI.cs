using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using CoralArchipelago.Gameplay.Crafting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Player;

namespace CoralArchipelago.UI
{
    /// <summary>
    /// Cooperative Crafting Panel UI.
    /// Shows each recipe's combined available ingredients from Personal Bag + nearby Shared Chests,
    /// and allows crafting with one tap on iPhone.
    /// </summary>
    public class CraftingPanelUI : MonoBehaviour
    {
        public static CraftingPanelUI Instance { get; private set; }

        [Header("Modal & Recipe List")]
        [SerializeField] private GameObject modalRoot;
        [SerializeField] private Transform recipeListContainer;
        [SerializeField] private Button recipeButtonPrefab;
        [SerializeField] private Text nearbyChestHintText;
        [SerializeField] private Text feedbackText;

        private readonly List<Button> _spawnedButtons = new List<Button>();
        private readonly List<Text> _spawnedButtonLabels = new List<Text>();
        private readonly List<SharedChestInventory> _subscribedChests = new List<SharedChestInventory>();

        private PlayerInventory _boundPlayerInv;
        private float _refreshTimer;

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
            UnsubscribeAll();
        }

        private void Update()
        {
            if (!IsOpen)
                return;

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer >= 0.5f)
            {
                _refreshTimer = 0f;
                RebindProximityChests();
                RefreshRecipeList();
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
            if (NetworkPlayerController.LocalPlayer == null || NetworkCraftingManager.Instance == null)
                return;

            EnsureRecipeButtonsInitialized();
            BindEvents();

            if (modalRoot != null)
                modalRoot.SetActive(true);

            if (feedbackText != null)
                feedbackText.text = string.Empty;

            RefreshRecipeList();
        }

        public void CloseModal()
        {
            UnsubscribeAll();
            if (modalRoot != null)
                modalRoot.SetActive(false);
        }

        public void OnClickCraftById(string recipeId)
        {
            if (NetworkCraftingManager.Instance == null || string.IsNullOrEmpty(recipeId))
                return;

            NetworkCraftingManager.Instance.RequestCraftRecipeServerRpc(recipeId);
        }

        private void EnsureRecipeButtonsInitialized()
        {
            if (recipeButtonPrefab == null || recipeListContainer == null || NetworkCraftingManager.Instance == null)
                return;

            IReadOnlyList<CraftingRecipeDefinition> recipes = NetworkCraftingManager.Instance.Recipes;
            if (_spawnedButtons.Count == recipes.Count)
                return;

            for (int i = 0; i < _spawnedButtons.Count; i++)
            {
                if (_spawnedButtons[i] != null)
                    Destroy(_spawnedButtons[i].gameObject);
            }
            _spawnedButtons.Clear();
            _spawnedButtonLabels.Clear();

            for (int i = 0; i < recipes.Count; i++)
            {
                CraftingRecipeDefinition recipe = recipes[i];
                Button btn = Instantiate(recipeButtonPrefab, recipeListContainer);
                btn.name = $"RecipeBtn_{recipe.RecipeId}";

                string capturedId = recipe.RecipeId;
                btn.onClick.AddListener(() => OnClickCraftById(capturedId));

                _spawnedButtons.Add(btn);
                _spawnedButtonLabels.Add(btn.GetComponentInChildren<Text>());
            }
        }

        private void BindEvents()
        {
            UnsubscribeAll();

            var localPlayer = NetworkPlayerController.LocalPlayer;
            if (localPlayer != null)
            {
                _boundPlayerInv = localPlayer.GetComponent<PlayerInventory>();
                if (_boundPlayerInv != null)
                    _boundPlayerInv.OnInventoryChanged += RefreshRecipeList;
            }

            if (NetworkCraftingManager.Instance != null)
            {
                NetworkCraftingManager.Instance.OnCraftFeedbackReceived += HandleCraftFeedback;
            }

            RebindProximityChests();
        }

        private void RebindProximityChests()
        {
            for (int i = 0; i < _subscribedChests.Count; i++)
            {
                if (_subscribedChests[i] != null)
                    _subscribedChests[i].OnChestChanged -= RefreshRecipeList;
            }
            _subscribedChests.Clear();

            var localPlayer = NetworkPlayerController.LocalPlayer;
            if (localPlayer == null || NetworkCraftingManager.Instance == null)
                return;

            List<SharedChestInventory> nearby = NetworkCraftingManager.Instance.FindNearbySharedChests(localPlayer.transform.position);
            for (int i = 0; i < nearby.Count; i++)
            {
                nearby[i].OnChestChanged += RefreshRecipeList;
                _subscribedChests.Add(nearby[i]);
            }
        }

        private void UnsubscribeAll()
        {
            if (_boundPlayerInv != null)
            {
                _boundPlayerInv.OnInventoryChanged -= RefreshRecipeList;
                _boundPlayerInv = null;
            }

            if (NetworkCraftingManager.Instance != null)
            {
                NetworkCraftingManager.Instance.OnCraftFeedbackReceived -= HandleCraftFeedback;
            }

            for (int i = 0; i < _subscribedChests.Count; i++)
            {
                if (_subscribedChests[i] != null)
                    _subscribedChests[i].OnChestChanged -= RefreshRecipeList;
            }
            _subscribedChests.Clear();
        }

        private void HandleCraftFeedback(bool success, string message)
        {
            if (feedbackText != null)
            {
                feedbackText.text = message;
                feedbackText.color = success ? new Color(0.35f, 0.95f, 0.72f) : new Color(1f, 0.45f, 0.45f);
            }
            RefreshRecipeList();
        }

        private void RefreshRecipeList()
        {
            if (NetworkCraftingManager.Instance == null)
                return;

            if (nearbyChestHintText != null)
            {
                nearbyChestHintText.text = _subscribedChests.Count > 0
                    ? $"Co-op Crafting Active: Linking {_subscribedChests.Count} nearby Shared Chest(s)"
                    : "Co-op Crafting: Personal Bag only (move within 8m of Shared Chest to share mats)";
            }

            IReadOnlyList<CraftingRecipeDefinition> recipes = NetworkCraftingManager.Instance.Recipes;
            var sb = new StringBuilder();

            for (int i = 0; i < _spawnedButtons.Count && i < recipes.Count; i++)
            {
                CraftingRecipeDefinition recipe = recipes[i];
                bool canCraft = true;
                sb.Clear();
                sb.Append(recipe.DisplayName).Append("  [");

                for (int k = 0; k < recipe.Ingredients.Count; k++)
                {
                    CraftingIngredient ing = recipe.Ingredients[k];
                    int have = NetworkCraftingManager.Instance.GetTotalAvailableItemCount(
                        _boundPlayerInv, _subscribedChests, ing.ItemId);

                    if (have < ing.Amount)
                        canCraft = false;

                    if (k > 0) sb.Append(", ");
                    sb.Append(ing.ItemId).Append(' ').Append(have).Append('/').Append(ing.Amount);
                }

                sb.Append(']');
                _spawnedButtons[i].interactable = canCraft;

                if (_spawnedButtonLabels[i] != null)
                {
                    _spawnedButtonLabels[i].text = sb.ToString();
                }
            }
        }
    }
}
