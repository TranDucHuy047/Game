using System;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Onboarding
{
    public enum OnboardingStep
    {
        MoveAndLook = 0,
        HarvestCoralWood = 1,
        CraftPickaxe = 2,
        HarvestGlowCrystal = 3,
        CoopSharedChest = 4,
        CraftSpearOrTonic = 5,
        Completed = 6
    }

    /// <summary>
    /// Server-authoritative 6-step onboarding tutorial per individual player.
    /// Progress is persisted independently in players/{uuid}.json so asynchronous players
    /// joining hours apart each receive their own onboarding flow, while Step 5 adapts
    /// automatically if teammates already built a Shared Chest on the island.
    /// </summary>
    [RequireComponent(typeof(PlayerInventory))]
    public class PlayerOnboardingController : NetworkBehaviour
    {
        public static PlayerOnboardingController LocalOnboarding { get; private set; }

        public const int TotalSteps = 6;

        [Header("Tutorial Parameters")]
        [SerializeField] private float requiredIntroMoveDistance = 4.0f;

        public readonly NetworkVariable<int> CurrentStepIndex = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> IsTutorialCompleted = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public event Action OnTutorialStateChanged;

        private PlayerInventory _inventory;
        private Vector3 _stepZeroAnchorPos;
        private bool _hasAnchor;
        private bool _loadedFromSave;

        private void Awake()
        {
            _inventory = GetComponent<PlayerInventory>();
        }

        public override void OnNetworkSpawn()
        {
            CurrentStepIndex.OnValueChanged += HandleStepChanged;
            IsTutorialCompleted.OnValueChanged += HandleCompletedChanged;

            if (IsServer)
            {
                _stepZeroAnchorPos = transform.position;
                _hasAnchor = true;

                PlayerSaveData save = SplitSaveManager.Instance?.GetActivePlayerSave(OwnerClientId);
                if (save != null)
                {
                    LoadFromSaveServer(save.TutorialStepIndex, save.TutorialCompleted);
                }

                if (_inventory != null)
                {
                    _inventory.OnInventoryChanged += EvaluateInventoryMilestonesServer;
                }
            }

            if (IsOwner)
            {
                LocalOnboarding = this;
            }

            OnTutorialStateChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            CurrentStepIndex.OnValueChanged -= HandleStepChanged;
            IsTutorialCompleted.OnValueChanged -= HandleCompletedChanged;

            if (IsServer && _inventory != null)
            {
                _inventory.OnInventoryChanged -= EvaluateInventoryMilestonesServer;
            }

            if (IsOwner && LocalOnboarding == this)
            {
                LocalOnboarding = null;
            }
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || IsTutorialCompleted.Value)
                return;

            if (CurrentStepIndex.Value == (int)OnboardingStep.MoveAndLook && _hasAnchor)
            {
                if (Vector3.Distance(transform.position, _stepZeroAnchorPos) >= requiredIntroMoveDistance)
                {
                    AdvanceToStepServer((int)OnboardingStep.HarvestCoralWood);
                }
            }
        }

        public void LoadFromSaveServer(int savedStepIndex, bool savedCompleted)
        {
            if (!IsServer || _loadedFromSave)
                return;

            _loadedFromSave = true;
            IsTutorialCompleted.Value = savedCompleted || savedStepIndex >= TotalSteps;
            CurrentStepIndex.Value = IsTutorialCompleted.Value
                ? TotalSteps
                : Mathf.Clamp(savedStepIndex, 0, TotalSteps - 1);

            EvaluateInventoryMilestonesServer();
        }

        public void ExportToSave(PlayerSaveData saveData)
        {
            if (saveData == null)
                return;

            saveData.TutorialStepIndex = CurrentStepIndex.Value;
            saveData.TutorialCompleted = IsTutorialCompleted.Value;
        }

        /// <summary>
        /// Server-only: Called when this player builds a structure (e.g., shared_chest or glow_lamp).
        /// </summary>
        public void NotifyStructureBuiltServer(string prefabId)
        {
            if (!IsServer || !IsSpawned || IsTutorialCompleted.Value)
                return;

            if (CurrentStepIndex.Value == (int)OnboardingStep.CoopSharedChest &&
                (prefabId == "shared_chest" || prefabId == "glow_lamp"))
            {
                AdvanceToStepServer((int)OnboardingStep.CraftSpearOrTonic);
            }
        }

        /// <summary>
        /// Server-only: Called when this player deposits items into any Shared Chest on the island.
        /// </summary>
        public void NotifyCoopChestInteractionServer()
        {
            if (!IsServer || !IsSpawned || IsTutorialCompleted.Value)
                return;

            if (CurrentStepIndex.Value == (int)OnboardingStep.CoopSharedChest)
            {
                AdvanceToStepServer((int)OnboardingStep.CraftSpearOrTonic);
            }
        }

        [ServerRpc]
        public void SkipTutorialServerRpc()
        {
            if (IsTutorialCompleted.Value)
                return;

            CompleteTutorialServer(grantReward: false);
        }

        private void EvaluateInventoryMilestonesServer()
        {
            if (!IsServer || !IsSpawned || IsTutorialCompleted.Value || _inventory == null)
                return;

            // Loop handles players who already fulfilled multiple sequential steps
            bool advanced;
            do
            {
                advanced = false;
                switch ((OnboardingStep)CurrentStepIndex.Value)
                {
                    case OnboardingStep.HarvestCoralWood:
                        if (_inventory.GetItemCount("coral_wood") >= 3)
                        {
                            AdvanceToStepServer((int)OnboardingStep.CraftPickaxe);
                            advanced = true;
                        }
                        break;

                    case OnboardingStep.CraftPickaxe:
                        if (_inventory.HasItem("coral_pickaxe", 1))
                        {
                            AdvanceToStepServer((int)OnboardingStep.HarvestGlowCrystal);
                            advanced = true;
                        }
                        break;

                    case OnboardingStep.HarvestGlowCrystal:
                        if (_inventory.GetItemCount("glow_crystal") >= 2)
                        {
                            AdvanceToStepServer((int)OnboardingStep.CoopSharedChest);
                            advanced = true;
                        }
                        break;

                    case OnboardingStep.CraftSpearOrTonic:
                        if (_inventory.HasItem("biolum_spear", 1) || _inventory.HasItem("coral_tonic", 1))
                        {
                            CompleteTutorialServer(grantReward: true);
                        }
                        break;
                }
            } while (advanced && !IsTutorialCompleted.Value);
        }

        private void AdvanceToStepServer(int nextStep)
        {
            CurrentStepIndex.Value = Mathf.Clamp(nextStep, 0, TotalSteps);
            SplitSaveManager.Instance?.SavePlayerByClientId(OwnerClientId, removeFromActiveCache: false);
            Debug.Log($"[Onboarding] Client {OwnerClientId} advanced to tutorial step {CurrentStepIndex.Value}/{TotalSteps}.");
        }

        private void CompleteTutorialServer(bool grantReward)
        {
            CurrentStepIndex.Value = TotalSteps;
            IsTutorialCompleted.Value = true;

            if (grantReward && _inventory != null)
            {
                _inventory.AddItemServer("coral_tonic", 2);
                _inventory.AddItemServer("coral_wood", 3);
                _inventory.AddItemServer("glow_crystal", 2);
            }

            SplitSaveManager.Instance?.SavePlayerByClientId(OwnerClientId, removeFromActiveCache: false);
            Debug.Log($"[Onboarding] Client {OwnerClientId} completed onboarding tutorial (Reward={grantReward}).");
        }

        private void HandleStepChanged(int previous, int next)
        {
            OnTutorialStateChanged?.Invoke();
        }

        private void HandleCompletedChanged(bool previous, bool next)
        {
            OnTutorialStateChanged?.Invoke();
        }
    }
}
