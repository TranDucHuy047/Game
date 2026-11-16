using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;
using CoralArchipelago.UI;

namespace CoralArchipelago.ClientIOS
{
    /// <summary>
    /// Mobile Touch HUD & Virtual Controls for iPhone.
    /// Supports Virtual Joystick movement, Touch-drag camera orbit, Attack/Harvest,
    /// Building placement, and Asynchronous Shared Chest deposit/withdraw actions.
    /// </summary>
    public class MobileTouchInputUI : MonoBehaviour
    {
        public static MobileTouchInputUI Instance { get; private set; }

        [Header("Virtual Joystick (Optional UI Refs)")]
        [SerializeField] private RectTransform joystickBackground;
        [SerializeField] private RectTransform joystickHandle;
        [SerializeField] private float joystickRadius = 70f;

        [Header("Optional Status Texts & Inputs")]
        [SerializeField] private Text hudStatusText;
        [SerializeField] private Text campLogText;
        [SerializeField] private InputField serverIpInput;
        [SerializeField] private InputField passcodeInput;
        [SerializeField] private InputField campNoteInput;

        public Vector2 MoveVector { get; private set; }
        public Vector2 LookDelta { get; private set; }
        public bool IsSprinting { get; private set; }

        private int _moveTouchId = -1;
        private int _lookTouchId = -1;
        private Vector2 _joystickOrigin;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            ProcessTouchInputs();
            UpdateDebugHudText();

#if UNITY_EDITOR || UNITY_STANDALONE
            if (Input.GetKeyDown(KeyCode.I) || Input.GetKeyDown(KeyCode.Tab))
            {
                OnClickToggleInventoryModal();
            }
            if (Input.GetKeyDown(KeyCode.C))
            {
                OnClickToggleCraftingModal();
            }
#endif
        }

        private void LateUpdate()
        {
            // Reset frame delta after NetworkPlayerController consumes it
            LookDelta = Vector2.zero;
        }

        private void ProcessTouchInputs()
        {
            if (Input.touchCount == 0)
            {
                if (_moveTouchId != -1)
                {
                    ResetJoystick();
                }
                _lookTouchId = -1;
                return;
            }

            float halfScreenWidth = Screen.width * 0.5f;

            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);

                if (touch.phase == TouchPhase.Began)
                {
                    // Skip touches starting on UI buttons
                    if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(touch.fingerId))
                        continue;

                    if (touch.position.x < halfScreenWidth && _moveTouchId == -1)
                    {
                        _moveTouchId = touch.fingerId;
                        _joystickOrigin = joystickBackground != null
                            ? (Vector2)joystickBackground.position
                            : touch.position;
                    }
                    else if (touch.position.x >= halfScreenWidth && _lookTouchId == -1)
                    {
                        _lookTouchId = touch.fingerId;
                    }
                }
                else if (touch.fingerId == _moveTouchId)
                {
                    if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                    {
                        ResetJoystick();
                    }
                    else
                    {
                        Vector2 delta = touch.position - _joystickOrigin;
                        Vector2 clamped = Vector2.ClampMagnitude(delta, joystickRadius);
                        MoveVector = clamped / Mathf.Max(1f, joystickRadius);
                        if (joystickHandle != null)
                        {
                            joystickHandle.position = _joystickOrigin + clamped;
                        }
                    }
                }
                else if (touch.fingerId == _lookTouchId)
                {
                    if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                    {
                        _lookTouchId = -1;
                    }
                    else if (touch.phase == TouchPhase.Moved)
                    {
                        LookDelta += touch.deltaPosition;
                    }
                }
            }
        }

        private void ResetJoystick()
        {
            _moveTouchId = -1;
            MoveVector = Vector2.zero;
            if (joystickHandle != null && joystickBackground != null)
            {
                joystickHandle.position = joystickBackground.position;
            }
        }

        private void UpdateDebugHudText()
        {
            if (campLogText != null && SharedCampLogManager.Instance != null)
            {
                campLogText.text = SharedCampLogManager.Instance.BuildFormattedLogText(4);
            }

            if (hudStatusText == null)
                return;

            var player = NetworkPlayerController.LocalPlayer;
            if (player == null)
            {
                bool reconnecting = NetworkBootstrap.Instance != null && NetworkBootstrap.Instance.IsReconnecting;
                hudStatusText.text = reconnecting
                    ? "Connection lost... Auto-reconnecting to Coral Archipelago..."
                    : "Disconnected - Enter VPS IP & PIN, then tap Connect";
                return;
            }

            var inv = player.GetComponent<PlayerInventory>();
            int wood = inv != null ? inv.GetItemCount("coral_wood") : 0;
            int crystal = inv != null ? inv.GetItemCount("glow_crystal") : 0;

            float hour = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.WorldTimeHours.Value : 8f;
            int day = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.CurrentDay.Value : 1;
            int online = PvEDirector.Instance != null ? PvEDirector.Instance.ActivePlayerCount.Value : 1;
            float hpMult = PvEDirector.Instance != null ? PvEDirector.Instance.CurrentHpMultiplier.Value : 1f;
            int altarLv = IslandCoreAltar.Instance != null ? IslandCoreAltar.Instance.CoreLevel.Value : 1;

            hudStatusText.text =
                $"Day {day} ({hour:00.0}h) | Online: {online}/10 (HP x{hpMult:0.00}) | Altar Lv.{altarLv}\n" +
                $"HP: {player.CurrentHP.Value:0} | Stam: {player.CurrentStamina.Value:0} | O2: {player.CurrentOxygen.Value:0}% | Wood: {wood} | Crystal: {crystal}";
        }

        #region UI Button Hooks (Wire in Unity Inspector)

        public void OnClickConnectServer()
        {
            string ip = serverIpInput != null && !string.IsNullOrWhiteSpace(serverIpInput.text)
                ? serverIpInput.text
                : null;
            string pin = passcodeInput != null ? passcodeInput.text : null;
            NetworkBootstrap.Instance?.ConnectAsClient(ip, null, null, pin);
        }

        public void OnClickDisconnect()
        {
            NetworkBootstrap.Instance?.DisconnectClientIntentionally();
        }

        public void OnClickStartHostTest()
        {
            NetworkBootstrap.Instance?.StartLocalHostForTesting();
        }

        public void OnClickAttackOrHarvest()
        {
            NetworkPlayerController.LocalPlayer?.TriggerPrimaryAction();
        }

        public void OnClickToggleSprint()
        {
            IsSprinting = !IsSprinting;
        }

        public void OnClickBuildFloor()
        {
            NetworkPlayerController.LocalPlayer?.RequestBuildInFront("coral_floor");
        }

        public void OnClickBuildGlowLamp()
        {
            NetworkPlayerController.LocalPlayer?.RequestBuildInFront("glow_lamp");
        }

        public void OnClickBuildSharedChest()
        {
            NetworkPlayerController.LocalPlayer?.RequestBuildInFront("shared_chest");
        }

        public void OnClickDepositWoodToNearestChest()
        {
            var player = NetworkPlayerController.LocalPlayer;
            if (player == null)
                return;

            SharedChestInventory chest = player.FindNearestSharedChest();
            if (chest != null)
            {
                chest.DepositItemServerRpc("coral_wood", 1);
            }
        }

        public void OnClickWithdrawFirstFromNearestChest()
        {
            var player = NetworkPlayerController.LocalPlayer;
            if (player == null)
                return;

            SharedChestInventory chest = player.FindNearestSharedChest();
            if (chest != null && chest.ChestSlots.Count > 0)
            {
                chest.WithdrawItemServerRpc(0, 1);
            }
        }

        public void OnClickToggleInventoryModal()
        {
            InventoryGridUI.Instance?.ToggleModal();
        }

        public void OnClickToggleCraftingModal()
        {
            CraftingPanelUI.Instance?.ToggleModal();
        }

        public void OnClickCraftItem(string recipeId)
        {
            CraftingPanelUI.Instance?.OnClickCraftById(recipeId);
        }

        public void OnClickUseCoralTonic()
        {
            NetworkPlayerController.LocalPlayer?.RequestUseCoralTonic();
        }

        public void OnClickDemolishNearestStructure()
        {
            NetworkPlayerController.LocalPlayer?.RequestDemolishNearest();
        }

        public void OnClickUpgradeIslandAltar()
        {
            IslandCoreAltar.Instance?.RequestUpgradeAltarServerRpc();
        }

        public void OnClickPostCampNote()
        {
            if (SharedCampLogManager.Instance == null || campNoteInput == null)
                return;

            string note = campNoteInput.text;
            if (string.IsNullOrWhiteSpace(note))
                return;

            SharedCampLogManager.Instance.PostPlayerNoteServerRpc(note);
            campNoteInput.text = string.Empty;
        }

        #endregion
    }
}
