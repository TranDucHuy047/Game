using UnityEngine;
using UnityEngine.UI;
using CoralArchipelago.Gameplay.Harvesting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Onboarding;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

namespace CoralArchipelago.UI
{
    /// <summary>
    /// Displays the asynchronous 6-step Starter Quest panel on iPhone HUD
    /// and steers an optional 3D Waypoint Arrow around the local player toward
    /// the nearest required Coral Wood tree, Glow Crystal, or teammate's Shared Chest.
    /// </summary>
    public class OnboardingTutorialUI : MonoBehaviour
    {
        [Header("HUD Panel References")]
        [SerializeField] private GameObject tutorialPanelRoot;
        [SerializeField] private Text stepTitleText;
        [SerializeField] private Text stepDescriptionText;
        [SerializeField] private Text stepProgressText;

        [Header("3D Waypoint Arrow (Optional Scene/Prefab Object)")]
        [SerializeField] private Transform worldWaypointArrow;
        [SerializeField] private float arrowOrbitRadius = 1.6f;
        [SerializeField] private float arrowHeightOffset = 0.35f;

        private PlayerOnboardingController _boundOnboarding;
        private PlayerInventory _boundInventory;
        private Transform _currentTargetTransform;
        private bool _worldHasSharedChest;
        private float _targetScanTimer;

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                Destroy(gameObject);
                return;
            }

            if (worldWaypointArrow != null)
                worldWaypointArrow.gameObject.SetActive(false);
        }

        private void OnDisable()
        {
            UnbindLocalPlayer();
        }

        private void Update()
        {
            TryBindLocalPlayer();

            if (_boundOnboarding == null || !_boundOnboarding.IsSpawned || _boundOnboarding.IsTutorialCompleted.Value)
            {
                SetVisualVisibility(false);
                return;
            }

            SetVisualVisibility(true);

            _targetScanTimer += Time.unscaledDeltaTime;
            if (_targetScanTimer >= 0.45f)
            {
                _targetScanTimer = 0f;
                ScanNearestGuidanceTarget();
                RefreshTutorialTexts();
            }

            UpdateWaypointArrowTransform();
        }

        public void OnClickSkipTutorial()
        {
            _boundOnboarding?.SkipTutorialServerRpc();
        }

        private void TryBindLocalPlayer()
        {
            var target = PlayerOnboardingController.LocalOnboarding;
            if (target == _boundOnboarding)
                return;

            UnbindLocalPlayer();

            _boundOnboarding = target;
            if (_boundOnboarding == null)
                return;

            _boundOnboarding.OnTutorialStateChanged += HandleTutorialStateChanged;

            if (NetworkPlayerController.LocalPlayer != null)
            {
                _boundInventory = NetworkPlayerController.LocalPlayer.GetComponent<PlayerInventory>();
                if (_boundInventory != null)
                    _boundInventory.OnInventoryChanged += HandleTutorialStateChanged;
            }

            ScanNearestGuidanceTarget();
            RefreshTutorialTexts();
        }

        private void UnbindLocalPlayer()
        {
            if (_boundOnboarding != null)
            {
                _boundOnboarding.OnTutorialStateChanged -= HandleTutorialStateChanged;
                _boundOnboarding = null;
            }

            if (_boundInventory != null)
            {
                _boundInventory.OnInventoryChanged -= HandleTutorialStateChanged;
                _boundInventory = null;
            }
        }

        private void HandleTutorialStateChanged()
        {
            ScanNearestGuidanceTarget();
            RefreshTutorialTexts();
        }

        private void SetVisualVisibility(bool visible)
        {
            if (tutorialPanelRoot != null && tutorialPanelRoot.activeSelf != visible)
            {
                tutorialPanelRoot.SetActive(visible);
            }

            if (!visible && worldWaypointArrow != null && worldWaypointArrow.gameObject.activeSelf)
            {
                worldWaypointArrow.gameObject.SetActive(false);
            }
        }

        private void RefreshTutorialTexts()
        {
            if (_boundOnboarding == null)
                return;

            int stepIndex = _boundOnboarding.CurrentStepIndex.Value;
            OnboardingStep step = (OnboardingStep)stepIndex;

            int woodCount = _boundInventory != null ? _boundInventory.GetItemCount("coral_wood") : 0;
            int crystalCount = _boundInventory != null ? _boundInventory.GetItemCount("glow_crystal") : 0;
            bool hasPickaxe = _boundInventory != null && _boundInventory.HasItem("coral_pickaxe", 1);
            bool hasCombatOrHeal = _boundInventory != null &&
                                   (_boundInventory.HasItem("biolum_spear", 1) || _boundInventory.HasItem("coral_tonic", 1));

            string title = $"Huong dan Tan thu ({Mathf.Min(stepIndex + 1, PlayerOnboardingController.TotalSteps)}/{PlayerOnboardingController.TotalSteps})";
            string desc = string.Empty;
            string progress = string.Empty;

            switch (step)
            {
                case OnboardingStep.MoveAndLook:
                    title += ": Lam quen Dieu khien";
                    desc = "Dung Joystick ben trai de di chuyen tren bo bien va vuot ben phai de xoay Camera.";
                    progress = "Di chuyen 4m";
                    break;

                case OnboardingStep.HarvestCoralWood:
                    title += ": Khai thac Go San Ho";
                    desc = "Di theo mui ten den cay Go San Ho va bam Attack/Harvest de thu thap du 3 go.";
                    progress = $"Go San Ho: {Mathf.Min(woodCount, 3)}/3";
                    break;

                case OnboardingStep.CraftPickaxe:
                    title += ": Che tao Cuoc San Ho";
                    desc = "Bam nut Craft de che tao Cuoc San Ho (tang x2 toc do khai thac).";
                    progress = $"Cuoc San Ho: {(hasPickaxe ? 1 : 0)}/1";
                    break;

                case OnboardingStep.HarvestGlowCrystal:
                    title += ": Tim Da Pha Le Phat Quang";
                    desc = "Dung Cuoc San Ho khai thac mo Da Pha Le theo mui ten chi duong.";
                    progress = $"Da Pha Le: {Mathf.Min(crystalCount, 2)}/2";
                    break;

                case OnboardingStep.CoopSharedChest:
                    title += ": Hop tac Bat dong bo";
                    desc = _worldHasSharedChest
                        ? "Dao da co Ruong Chung cua dong doi! Hay di theo mui ten mo Bag/Chest de gui thu 1 vat pham (hoac xay them Den Pha Le)."
                        : "Bam Build Lamp (2 Pha Le) hoac Build Chest de dung can cu chung cho dong doi vao sau.";
                    progress = _worldHasSharedChest ? "Gui 1 mon vao Ruong Chung" : "Xay Den Pha Le / Ruong Chung";
                    break;

                case OnboardingStep.CraftSpearOrTonic:
                    title += ": San sang Sinh ton";
                    desc = "Mo Craft che tao Giao Phat Quang (x2 sat thuong quai) hoac Thuoc Hoi Phuc de nhan qua tan thu!";
                    progress = $"Trang bi / Thuoc: {(hasCombatOrHeal ? 1 : 0)}/1";
                    break;
            }

            if (stepTitleText != null)
                stepTitleText.text = title;
            if (stepDescriptionText != null)
                stepDescriptionText.text = desc;
            if (stepProgressText != null)
                stepProgressText.text = progress;
        }

        private void ScanNearestGuidanceTarget()
        {
            _currentTargetTransform = null;
            _worldHasSharedChest = false;

            SharedChestInventory[] chests = FindObjectsByType<SharedChestInventory>(FindObjectsSortMode.None);
            _worldHasSharedChest = chests.Length > 0;

            if (_boundOnboarding == null || NetworkPlayerController.LocalPlayer == null)
                return;

            Vector3 playerPos = NetworkPlayerController.LocalPlayer.transform.position;
            OnboardingStep step = (OnboardingStep)_boundOnboarding.CurrentStepIndex.Value;

            if (step == OnboardingStep.HarvestCoralWood)
            {
                _currentTargetTransform = FindNearestActiveResource(playerPos, "coral_wood");
            }
            else if (step == OnboardingStep.HarvestGlowCrystal)
            {
                _currentTargetTransform = FindNearestActiveResource(playerPos, "glow_crystal");
            }
            else if (step == OnboardingStep.CoopSharedChest && chests.Length > 0)
            {
                float bestDistSq = float.MaxValue;
                for (int i = 0; i < chests.Length; i++)
                {
                    if (chests[i] == null || !chests[i].IsSpawned)
                        continue;

                    float dSq = (chests[i].transform.position - playerPos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        _currentTargetTransform = chests[i].transform;
                    }
                }
            }
        }

        private static Transform FindNearestActiveResource(Vector3 origin, string targetItemId)
        {
            HarvestableResourceNode[] nodes = FindObjectsByType<HarvestableResourceNode>(FindObjectsSortMode.None);
            Transform best = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < nodes.Length; i++)
            {
                HarvestableResourceNode node = nodes[i];
                if (node == null || !node.IsSpawned || node.IsDepleted.Value || node.ResourceItemId != targetItemId)
                    continue;

                float dSq = (node.transform.position - origin).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = node.transform;
                }
            }

            return best;
        }

        private void UpdateWaypointArrowTransform()
        {
            if (worldWaypointArrow == null)
                return;

            var player = NetworkPlayerController.LocalPlayer;
            if (player == null || _currentTargetTransform == null)
            {
                if (worldWaypointArrow.gameObject.activeSelf)
                    worldWaypointArrow.gameObject.SetActive(false);
                return;
            }

            Vector3 flatDir = _currentTargetTransform.position - player.transform.position;
            flatDir.y = 0f;

            if (flatDir.sqrMagnitude < 1.2f)
            {
                if (worldWaypointArrow.gameObject.activeSelf)
                    worldWaypointArrow.gameObject.SetActive(false);
                return;
            }

            if (!worldWaypointArrow.gameObject.activeSelf)
                worldWaypointArrow.gameObject.SetActive(true);

            flatDir.Normalize();
            worldWaypointArrow.position = player.transform.position + Vector3.up * arrowHeightOffset + flatDir * arrowOrbitRadius;
            worldWaypointArrow.rotation = Quaternion.LookRotation(flatDir);
        }
    }
}
