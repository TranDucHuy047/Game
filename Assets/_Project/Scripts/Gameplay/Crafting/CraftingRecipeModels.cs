using System;
using System.Collections.Generic;

namespace CoralArchipelago.Gameplay.Crafting
{
    /// <summary>
    /// Single material requirement for a crafting recipe.
    /// </summary>
    [Serializable]
    public class CraftingIngredient
    {
        public string ItemId;
        public int Amount = 1;

        public CraftingIngredient() { }

        public CraftingIngredient(string itemId, int amount)
        {
            ItemId = itemId;
            Amount = amount;
        }
    }

    /// <summary>
    /// Crafting recipe definition supporting cooperative crafting from both personal bag and nearby shared chests.
    /// </summary>
    [Serializable]
    public class CraftingRecipeDefinition
    {
        public string RecipeId;
        public string DisplayName;
        public string OutputItemId;
        public int OutputAmount = 1;
        public List<CraftingIngredient> Ingredients = new List<CraftingIngredient>();
    }
}
