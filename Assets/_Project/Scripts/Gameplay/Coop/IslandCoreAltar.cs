using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.Crafting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Coop
{
    /// <summary>
    /// Island Core / Bioluminescent Altar (Lv.1 -> Lv.5).
    /// Serves as the shared long-term progression goal for asynchronous teammates:
    /// upgrades pull resources from bag + nearby Shared Chests, widen the healing aura around camp,
    /// and persist globally in world_save.json.
    /// </summary>
    public class IslandCoreAltar : NetworkBehaviour
    {
        public static IslandCoreAltar Instance { get; private set; }

        public const int MaxCoreLevel = 5;

        [Header("Aura & Upgrade Settings")]
        [SerializeField] private float baseAuraRadius = 10f;
        [SerializeField] private float radiusPerLevel = 3f;
        [SerializeField] private float upgradeInteractRange = 6f;

        public readonly NetworkVariable<int> CoreLevel = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private float _auraTickTimer;

        public float CurrentAuraRadius => baseAuraRadius + (Mathf.Max(1, CoreLevel.Value) - 1) * radiusPerLevel;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer && SplitSaveManager.Instance != null)
            {
                CoreLevel.Value = Mathf.Clamp(SplitSaveManager.Instance.CurrentWorldSave.IslandCoreLevel, 1, MaxCoreLevel);
            }
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned)
                return;

            if (WorldTimeManager.Instance != null && WorldTimeManager.Instance.IsPaused.Value)
                return;

            _auraTickTimer += Time.deltaTime;
            if (_auraTickTimer < 1.0f)
                return;

            _auraTickTimer = 0f;
            ApplyCampHealingAuraServer();
        }

        public void GetNextUpgradeCosts(out int requiredWood, out int requiredCrystal)
        {
            int lvl = Mathf.Clamp(CoreLevel.Value, 1, MaxCoreLevel);
            requiredWood = 4 * lvl;
            requiredCrystal = 2 * lvl;
        }

        private void ApplyCampHealingAuraServer()
        {
            if (NetworkManager.Singleton == null)
                return;

            float radiusSq = CurrentAuraRadius * CurrentAuraRadius;
            float hpHeal = 1.5f * CoreLevel.Value;
            float stamHeal = 4.0f * CoreLevel.Value;

            foreach (NetworkClient client in NetworkManager.Singleton.ConnectedClientsList)
            {
                if (client.PlayerObject == null)
                    continue;

                if ((client.PlayerObject.transform.position - transform.position).sqrMagnitude > radiusSq)
                    continue;

                var player = client.PlayerObject.GetComponent<NetworkPlayerController>();
                if (player != null && player.CurrentHP.Value > 0f)
                {
                    player.HealVitalsServer(hpHeal, stamHeal);
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestUpgradeAltarServerRpc(ServerRpcParams rpcParams = default)
        {
            if (CoreLevel.Value >= MaxCoreLevel)
                return;

            ulong senderId = rpcParams.Receive.SenderClientId;
            if (NetworkManager.Singleton == null ||
                !NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return;
            }

            if (Vector3.Distance(client.PlayerObject.transform.position, transform.position) > upgradeInteractRange)
                return;

            PlayerInventory playerInv = client.PlayerObject.GetComponent<PlayerInventory>();
            if (playerInv == null)
                return;

            GetNextUpgradeCosts(out int reqWood, out int reqCrystal);

            List<SharedChestInventory> nearbyChests = NetworkCraftingManager.Instance != null
                ? NetworkCraftingManager.Instance.FindNearbySharedChests(transform.position)
                : new List<SharedChestInventory>();

            int totalWood = playerInv.GetItemCount("coral_wood");
            int totalCrystal = playerInv.GetItemCount("glow_crystal");
            for (int i = 0; i < nearbyChests.Count; i++)
            {
                totalWood += nearbyChests[i].GetItemCount("coral_wood");
                totalCrystal += nearbyChests[i].GetItemCount("glow_crystal");
            }

            if (totalWood < reqWood || totalCrystal < reqCrystal)
                return;

            ConsumeSharedMaterial(playerInv, nearbyChests, "coral_wood", reqWood);
            ConsumeSharedMaterial(playerInv, nearbyChests, "glow_crystal", reqCrystal);

            CoreLevel.Value += 1;

            if (SplitSaveManager.Instance != null)
            {
                SplitSaveManager.Instance.CurrentWorldSave.IslandCoreLevel = CoreLevel.Value;
                SplitSaveManager.Instance.MarkWorldDirty();
            }

            SharedCampLogManager.Instance?.RecordActivityServer($"Altar upgraded to Lv.{CoreLevel.Value}!");
            PlayUpgradeEffectClientRpc(CoreLevel.Value);
        }

        private static void ConsumeSharedMaterial(
            PlayerInventory playerInv,
            List<SharedChestInventory> chests,
            string itemId,
            int amount)
        {
            int remaining = amount;
            int fromBag = Mathf.Min(remaining, playerInv.GetItemCount(itemId));
            if (fromBag > 0 && playerInv.RemoveItemServer(itemId, fromBag))
            {
                remaining -= fromBag;
            }

            for (int i = 0; i < chests.Count && remaining > 0; i++)
            {
                remaining -= chests[i].ConsumeItemUpToServer(itemId, remaining);
            }
        }

        [ClientRpc]
        private void PlayUpgradeEffectClientRpc(int newLevel)
        {
            CombatFeedbackManager.Instance?.TriggerHaptic(2);
            CombatFeedbackManager.Instance?.SpawnWorldPopup(
                transform.position + Vector3.up * 2.5f,
                $"Altar Lv.{newLevel}!",
                new Color(0.25f, 1.0f, 0.90f));
        }
    }
}
