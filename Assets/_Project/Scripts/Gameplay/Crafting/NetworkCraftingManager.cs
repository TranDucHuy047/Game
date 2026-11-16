using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Gameplay.Inventory;

namespace CoralArchipelago.Gameplay.Crafting
{
    /// <summary>
    /// Server-authoritative cooperative Crafting Manager.
    /// Automatically pulls required ingredients from the player's personal bag first,
    /// then draws any remaining materials from nearby Shared Chests (within chestProximityRadius).
    /// </summary>
    public class NetworkCraftingManager : NetworkBehaviour
    {
        public static NetworkCraftingManager Instance { get; private set; }

        [Header("Co-op Proximity Crafting")]
        [SerializeField] private float chestProximityRadius = 8.0f;
        [SerializeField] private List<CraftingRecipeDefinition> recipes = new List<CraftingRecipeDefinition>();

        private readonly Dictionary<string, CraftingRecipeDefinition> _recipeById =
            new Dictionary<string, CraftingRecipeDefinition>();

        public IReadOnlyList<CraftingRecipeDefinition> Recipes => recipes;
        public float ChestProximityRadius => chestProximityRadius;

        public event Action<bool, string> OnCraftFeedbackReceived;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (recipes.Count == 0)
            {
                PopulateDefaultRecipes();
            }

            RebuildRecipeLookup();
        }

        private void RebuildRecipeLookup()
        {
            _recipeById.Clear();
            for (int i = 0; i < recipes.Count; i++)
            {
                CraftingRecipeDefinition r = recipes[i];
                if (r != null && !string.IsNullOrEmpty(r.RecipeId))
                {
                    _recipeById[r.RecipeId] = r;
                }
            }
        }

        private void PopulateDefaultRecipes()
        {
            recipes.Add(new CraftingRecipeDefinition
            {
                RecipeId = "coral_pickaxe",
                DisplayName = "Coral Pickaxe (x2 Harvest)",
                OutputItemId = "coral_pickaxe",
                OutputAmount = 1,
                Ingredients = new List<CraftingIngredient>
                {
                    new CraftingIngredient("coral_wood", 3)
                }
            });

            recipes.Add(new CraftingRecipeDefinition
            {
                RecipeId = "biolum_spear",
                DisplayName = "Biolum Spear (x2 Combat)",
                OutputItemId = "biolum_spear",
                OutputAmount = 1,
                Ingredients = new List<CraftingIngredient>
                {
                    new CraftingIngredient("coral_wood", 2),
                    new CraftingIngredient("glow_crystal", 2)
                }
            });

            recipes.Add(new CraftingRecipeDefinition
            {
                RecipeId = "coral_tonic",
                DisplayName = "Coral Tonic (+50 HP/Stam)",
                OutputItemId = "coral_tonic",
                OutputAmount = 1,
                Ingredients = new List<CraftingIngredient>
                {
                    new CraftingIngredient("coral_wood", 1),
                    new CraftingIngredient("glow_crystal", 1)
                }
            });

            recipes.Add(new CraftingRecipeDefinition
            {
                RecipeId = "refined_prism",
                DisplayName = "Refined Prism",
                OutputItemId = "refined_prism",
                OutputAmount = 1,
                Ingredients = new List<CraftingIngredient>
                {
                    new CraftingIngredient("glow_crystal", 3)
                }
            });
        }

        public List<SharedChestInventory> FindNearbySharedChests(Vector3 center)
        {
            var result = new List<SharedChestInventory>();
            Collider[] hits = Physics.OverlapSphere(center, chestProximityRadius);

            for (int i = 0; i < hits.Length; i++)
            {
                SharedChestInventory chest = hits[i].GetComponentInParent<SharedChestInventory>();
                if (chest != null && chest.IsSpawned && !result.Contains(chest))
                {
                    result.Add(chest);
                }
            }

            return result;
        }

        public int GetTotalAvailableItemCount(
            PlayerInventory playerInv,
            List<SharedChestInventory> nearbyChests,
            string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return 0;

            int total = playerInv != null ? playerInv.GetItemCount(itemId) : 0;

            if (nearbyChests != null)
            {
                for (int i = 0; i < nearbyChests.Count; i++)
                {
                    if (nearbyChests[i] != null)
                        total += nearbyChests[i].GetItemCount(itemId);
                }
            }

            return total;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestCraftRecipeServerRpc(FixedString64Bytes recipeId, ServerRpcParams rpcParams = default)
        {
            ulong senderId = rpcParams.Receive.SenderClientId;
            if (NetworkManager.Singleton == null ||
                !NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return;
            }

            string id = recipeId.ToString();
            if (!_recipeById.TryGetValue(id, out CraftingRecipeDefinition recipe))
            {
                SendCraftFeedback(senderId, false, "Unknown recipe.");
                return;
            }

            PlayerInventory playerInv = client.PlayerObject.GetComponent<PlayerInventory>();
            if (playerInv == null)
                return;

            List<SharedChestInventory> nearbyChests = FindNearbySharedChests(client.PlayerObject.transform.position);

            // 1. Atomic check across bag + nearby shared chests
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CraftingIngredient ing = recipe.Ingredients[i];
                if (ing == null || ing.Amount <= 0)
                    continue;

                int available = GetTotalAvailableItemCount(playerInv, nearbyChests, ing.ItemId);
                if (available < ing.Amount)
                {
                    SendCraftFeedback(senderId, false, $"Not enough {ing.ItemId} ({available}/{ing.Amount}).");
                    return;
                }
            }

            // 2. Verify bag capacity before consuming ingredients
            if (!CanFitItemInBag(playerInv, recipe.OutputItemId, recipe.OutputAmount))
            {
                SendCraftFeedback(senderId, false, "Personal bag is full.");
                return;
            }

            // 3. Consume from personal bag first, then nearby shared chests
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CraftingIngredient ing = recipe.Ingredients[i];
                if (ing == null || ing.Amount <= 0)
                    continue;

                int remaining = ing.Amount;
                int inBag = Mathf.Min(remaining, playerInv.GetItemCount(ing.ItemId));
                if (inBag > 0 && playerInv.RemoveItemServer(ing.ItemId, inBag))
                {
                    remaining -= inBag;
                }

                for (int c = 0; c < nearbyChests.Count && remaining > 0; c++)
                {
                    int taken = nearbyChests[c].ConsumeItemUpToServer(ing.ItemId, remaining);
                    remaining -= taken;
                }
            }

            // 4. Grant output item
            playerInv.AddItemServer(recipe.OutputItemId, recipe.OutputAmount);
            SendCraftFeedback(senderId, true, $"Crafted {recipe.DisplayName}!");
        }

        private static bool CanFitItemInBag(PlayerInventory inv, string itemId, int amount)
        {
            if (inv == null || amount <= 0)
                return false;

            int freeCapacity = (inv.MaxSlots - inv.Slots.Count) * inv.MaxStackSize;
            FixedString64Bytes key = itemId;

            for (int i = 0; i < inv.Slots.Count; i++)
            {
                if (inv.Slots[i].ItemId.Equals(key) && inv.Slots[i].Amount < inv.MaxStackSize)
                {
                    freeCapacity += inv.MaxStackSize - inv.Slots[i].Amount;
                }
            }

            return freeCapacity >= amount;
        }

        private void SendCraftFeedback(ulong targetClientId, bool success, string message)
        {
            var clientRpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { targetClientId }
                }
            };
            NotifyCraftResultClientRpc(success, message, clientRpcParams);
        }

        [ClientRpc]
        private void NotifyCraftResultClientRpc(bool success, string message, ClientRpcParams clientRpcParams = default)
        {
            OnCraftFeedbackReceived?.Invoke(success, message);
        }
    }
}
