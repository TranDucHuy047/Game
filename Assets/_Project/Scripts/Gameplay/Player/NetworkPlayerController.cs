using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Building;
using CoralArchipelago.Gameplay.Harvesting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Onboarding;
using CoralArchipelago.Gameplay.Visuals;
using CoralArchipelago.Networking;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Player
{
    /// <summary>
    /// Server-authoritative Third-Person Player Controller for iOS Mobile & Editor testing.
    /// Handles smooth touch camera orbit, vitals (HP/Stamina), harvesting/combat, building requests,
    /// shared chest interaction, and immediate emergency saving when iOS backgrounds the app.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(PlayerInventory))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        public static NetworkPlayerController LocalPlayer { get; private set; }

        [Header("Movement & Camera")]
        [SerializeField] private float walkSpeed = 5.5f;
        [SerializeField] private float sprintSpeed = 8.2f;
        [SerializeField] private float gravity = -19.6f;
        [SerializeField] private float cameraDistance = 6.5f;
        [SerializeField] private float cameraHeight = 2.6f;
        [SerializeField] private float cameraSensitivity = 0.25f;

        [Header("Vitals & Combat")]
        [SerializeField] private float maxHP = 100f;
        [SerializeField] private float maxStamina = 100f;
        [SerializeField] private float maxOxygen = 100f;
        [SerializeField] private float headOffsetY = 1.45f;
        [SerializeField] private float actionRange = 3.0f;
        [SerializeField] private float actionDamage = 15f;
        [SerializeField] private float actionCooldown = 0.45f;
        [SerializeField] private Vector3 respawnBeachPosition = new Vector3(0f, 3f, 0f);

        public readonly NetworkVariable<float> CurrentHP = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CurrentStamina = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CurrentOxygen = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private CharacterController _characterController;
        private PlayerInventory _inventory;
        private PlayerOnboardingController _onboarding;
        private CharacterAnimationSync _animSync;

        // Server movement state
        private Vector2 _serverMoveInput;
        private float _serverCameraYaw;
        private bool _serverSprinting;
        private float _verticalVelocity;
        private float _serverActionTimer;
        private bool _isMountedOnRaft;
        private float _mountSyncTimer;
        private bool _lastSentMountedState;

        // Client camera & input throttling state
        private float _clientYaw;
        private float _clientPitch = 18f;
        private float _inputSendTimer;
        private Vector2 _lastSentMove;
        private bool _lastSentSprint;
        private Transform _mainCamTransform;

        private string _playerUuid;

        private void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _inventory = GetComponent<PlayerInventory>();
            _onboarding = GetComponent<PlayerOnboardingController>();
            _animSync = GetComponent<CharacterAnimationSync>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _playerUuid = NetworkBootstrap.Instance != null
                    ? NetworkBootstrap.Instance.GetPlayerUuid(OwnerClientId)
                    : $"player_{OwnerClientId}";

                PlayerSaveData save = SplitSaveManager.Instance?.GetActivePlayerSave(OwnerClientId);
                if (save != null)
                {
                    CurrentHP.Value = Mathf.Clamp(save.CurrentHP, 10f, maxHP);
                    CurrentStamina.Value = Mathf.Clamp(save.CurrentStamina, 0f, maxStamina);
                    CurrentOxygen.Value = Mathf.Clamp(save.CurrentOxygen <= 0f ? maxOxygen : save.CurrentOxygen, 0f, maxOxygen);
                    _inventory.LoadFromSaveServer(save.Inventory);
                    _onboarding?.LoadFromSaveServer(save.TutorialStepIndex, save.TutorialCompleted);
                }
                else
                {
                    CurrentHP.Value = maxHP;
                    CurrentStamina.Value = maxStamina;
                    CurrentOxygen.Value = maxOxygen;
                }
            }

            if (IsOwner)
            {
                LocalPlayer = this;
                _clientYaw = transform.eulerAngles.y;
                if (Camera.main != null)
                {
                    _mainCamTransform = Camera.main.transform;
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                PlayerSaveData save = SplitSaveManager.Instance?.GetActivePlayerSave(OwnerClientId);
                if (save != null)
                {
                    ExportSaveData(save);
                }
            }

            if (IsOwner && LocalPlayer == this)
            {
                LocalPlayer = null;
            }
        }

        private void Update()
        {
            if (!IsSpawned)
                return;

            if (IsOwner)
            {
                HandleClientInputAndCamera();
            }

            if (IsServer)
            {
                SimulateServerMovement();
                if (_serverActionTimer > 0f)
                    _serverActionTimer -= Time.deltaTime;
            }
        }

        /// <summary>
        /// iOS Lifecycle Safety: Immediately ask Server to flush our personal save when user swipes app
        /// into background or receives a phone call.
        /// </summary>
        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused && IsOwner && IsSpawned)
            {
                EmergencySavePlayerServerRpc();
            }
        }

        private void HandleClientInputAndCamera()
        {
            Vector2 moveInput = Vector2.zero;
            Vector2 lookDelta = Vector2.zero;
            bool sprint = false;

            if (MobileTouchInputUI.Instance != null)
            {
                moveInput = MobileTouchInputUI.Instance.MoveVector;
                lookDelta = MobileTouchInputUI.Instance.LookDelta;
                sprint = MobileTouchInputUI.Instance.IsSprinting;
            }

            // Editor keyboard/mouse fallback for rapid testing on PC/Mac
#if UNITY_EDITOR || UNITY_STANDALONE
            float kbX = Input.GetAxisRaw("Horizontal");
            float kbY = Input.GetAxisRaw("Vertical");
            if (Mathf.Abs(kbX) > 0.05f || Mathf.Abs(kbY) > 0.05f)
            {
                moveInput = new Vector2(kbX, kbY).normalized;
            }
            if (Input.GetMouseButton(1))
            {
                lookDelta += new Vector2(Input.GetAxis("Mouse X") * 12f, Input.GetAxis("Mouse Y") * 12f);
            }
            if (Input.GetKey(KeyCode.LeftShift))
            {
                sprint = true;
            }
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0))
            {
                // Avoid triggering world attack when clicking UI buttons
                if (UnityEngine.EventSystems.EventSystem.current == null ||
                    !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                {
                    TriggerPrimaryAction();
                }
            }
#endif

            _clientYaw += lookDelta.x * cameraSensitivity;
            _clientPitch = Mathf.Clamp(_clientPitch - lookDelta.y * cameraSensitivity, 5f, 65f);

            UpdateThirdPersonCamera();

            _inputSendTimer += Time.deltaTime;
            bool changed = (moveInput - _lastSentMove).sqrMagnitude > 0.0025f || sprint != _lastSentSprint;
            if (changed || _inputSendTimer >= 0.05f)
            {
                _inputSendTimer = 0f;
                _lastSentMove = moveInput;
                _lastSentSprint = sprint;
                SendMoveInputServerRpc(moveInput, _clientYaw, sprint);
            }
        }

        private void UpdateThirdPersonCamera()
        {
            if (_mainCamTransform == null && Camera.main != null)
                _mainCamTransform = Camera.main.transform;

            if (_mainCamTransform == null)
                return;

            Quaternion orbitRot = Quaternion.Euler(_clientPitch, _clientYaw, 0f);
            Vector3 pivot = transform.position + Vector3.up * cameraHeight;
            Vector3 desiredPos = pivot - (orbitRot * Vector3.forward * cameraDistance);

            _mainCamTransform.position = Vector3.Lerp(_mainCamTransform.position, desiredPos, Time.deltaTime * 15f);
            _mainCamTransform.LookAt(pivot);
        }

        private void SimulateServerMovement()
        {
            if (_isMountedOnRaft || _characterController == null || !_characterController.enabled)
                return;

            if (_characterController.isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = -2f;
            }
            _verticalVelocity += gravity * Time.deltaTime;

            Quaternion yawRot = Quaternion.Euler(0f, _serverCameraYaw, 0f);
            Vector3 forward = yawRot * Vector3.forward;
            Vector3 right = yawRot * Vector3.right;
            Vector3 moveDir = (forward * _serverMoveInput.y + right * _serverMoveInput.x);

            if (moveDir.sqrMagnitude > 1f)
                moveDir.Normalize();

            bool canSprint = _serverSprinting && moveDir.sqrMagnitude > 0.05f && CurrentStamina.Value > 1f;
            float speed = canSprint ? sprintSpeed : walkSpeed;

            if (canSprint)
            {
                CurrentStamina.Value = Mathf.Max(0f, CurrentStamina.Value - 18f * Time.deltaTime);
            }
            else if (CurrentStamina.Value < maxStamina)
            {
                CurrentStamina.Value = Mathf.Min(maxStamina, CurrentStamina.Value + 12f * Time.deltaTime);
            }

            if (moveDir.sqrMagnitude > 0.01f)
            {
                Quaternion targetRot = Quaternion.LookRotation(moveDir);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 12f);
            }

            Vector3 velocity = moveDir * speed + Vector3.up * _verticalVelocity;
            _characterController.Move(velocity * Time.deltaTime);

            SimulateUnderwaterOxygenServer();
        }

        private void SimulateUnderwaterOxygenServer()
        {
            float seaLevelY = WorldTimeManager.Instance != null
                ? WorldTimeManager.Instance.GetCurrentSeaLevelY()
                : 0f;

            bool headSubmerged = (transform.position.y + headOffsetY) < seaLevelY;
            if (headSubmerged)
            {
                if (CurrentOxygen.Value > 0f)
                {
                    CurrentOxygen.Value = Mathf.Max(0f, CurrentOxygen.Value - 12f * Time.deltaTime);
                }
                else
                {
                    TakeDamageServer(8f * Time.deltaTime);
                }
            }
            else if (CurrentOxygen.Value < maxOxygen)
            {
                CurrentOxygen.Value = Mathf.Min(maxOxygen, CurrentOxygen.Value + 25f * Time.deltaTime);
            }
        }

        #region Client Actions & RPCs

        public void TriggerPrimaryAction()
        {
            if (!IsOwner || !IsSpawned)
                return;

            PerformPrimaryActionServerRpc(transform.forward);
            global::IslandRelicAndVolcanoBossSystem.Instance?.TryPlayerAttackBossFromHUD();
        }

        public void RequestTeleportToPosition(Vector3 targetPosition)
        {
            if (_characterController != null) _characterController.enabled = false;
            transform.position = targetPosition;
            _verticalVelocity = 0f;
            if (_characterController != null) _characterController.enabled = true;

            if (IsOwner && IsSpawned && !IsServer)
            {
                TeleportToPositionServerRpc(targetPosition);
            }
        }

        public void SyncExternalMountPosition(Vector3 worldPos, float yaw, bool isMounted)
        {
            _isMountedOnRaft = isMounted;
            if (_characterController != null && _characterController.enabled == isMounted)
            {
                _characterController.enabled = !isMounted;
            }

            transform.position = worldPos;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _verticalVelocity = 0f;

            if (IsOwner && IsSpawned && !IsServer)
            {
                _mountSyncTimer += Time.deltaTime;
                if (isMounted != _lastSentMountedState || _mountSyncTimer >= 0.05f)
                {
                    _lastSentMountedState = isMounted;
                    _mountSyncTimer = 0f;
                    SyncMountStateServerRpc(worldPos, yaw, isMounted);
                }
            }
        }

        public void RequestBossAoEDamage(float damage)
        {
            if (damage <= 0f || !IsOwner || !IsSpawned)
                return;

            ApplyBossAoEDamageServerRpc(damage);
        }

        public void RequestBuildInFront(string prefabId)
        {
            if (!IsOwner || !IsSpawned || NetworkBuildingManager.Instance == null)
                return;

            Vector3 buildPos = transform.position + transform.forward * 2.5f;
            NetworkBuildingManager.Instance.RequestBuildStructureServerRpc(prefabId, buildPos, transform.eulerAngles.y);
        }

        public void RequestDemolishNearest()
        {
            if (!IsOwner || !IsSpawned || NetworkBuildingManager.Instance == null)
                return;

            NetworkBuildingManager.Instance.RequestDemolishNearestStructureServerRpc();
        }

        public void RequestUseCoralTonic()
        {
            if (!IsOwner || !IsSpawned)
                return;

            UseCoralTonicServerRpc();
        }

        public SharedChestInventory FindNearestSharedChest(float maxRange = 4.5f)
        {
            Collider[] hits = Physics.OverlapSphere(transform.position, maxRange);
            SharedChestInventory best = null;
            float bestDistSq = maxRange * maxRange;

            for (int i = 0; i < hits.Length; i++)
            {
                SharedChestInventory chest = hits[i].GetComponentInParent<SharedChestInventory>();
                if (chest == null)
                    continue;

                float dSq = (chest.transform.position - transform.position).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = chest;
                }
            }
            return best;
        }

        [ServerRpc]
        private void SendMoveInputServerRpc(Vector2 moveInput, float cameraYaw, bool sprint)
        {
            _serverMoveInput = Vector2.ClampMagnitude(moveInput, 1f);
            _serverCameraYaw = cameraYaw;
            _serverSprinting = sprint;
        }

        [ServerRpc]
        private void PerformPrimaryActionServerRpc(Vector3 forwardDir)
        {
            if (_serverActionTimer > 0f || CurrentHP.Value <= 0f)
                return;

            _serverActionTimer = actionCooldown;
            PlayPlayerAttackClientRpc();

            Vector3 center = transform.position + Vector3.up * 1.0f + forwardDir.normalized * (actionRange * 0.5f);
            Collider[] hits = Physics.OverlapSphere(center, actionRange * 0.6f);

            for (int i = 0; i < hits.Length; i++)
            {
                ScalableMonsterAI monster = hits[i].GetComponentInParent<ScalableMonsterAI>();
                if (monster != null && monster.CurrentState.Value != MonsterState.Dead)
                {
                    bool hasSpear = _inventory != null && _inventory.HasItem("biolum_spear", 1);
                    float combatDamage = hasSpear ? actionDamage * 2.0f : actionDamage;
                    monster.TakeDamageServer(combatDamage, OwnerClientId);
                    return;
                }
            }

            for (int i = 0; i < hits.Length; i++)
            {
                HarvestableResourceNode node = hits[i].GetComponentInParent<HarvestableResourceNode>();
                if (node != null && !node.IsDepleted.Value)
                {
                    bool hasPickaxe = _inventory != null && _inventory.HasItem("coral_pickaxe", 1);
                    float harvestDamage = hasPickaxe ? actionDamage * 2.0f : actionDamage;
                    node.TakeHarvestHitServer(harvestDamage, _inventory);
                    return;
                }
            }
        }

        [ServerRpc]
        private void UseCoralTonicServerRpc()
        {
            if (CurrentHP.Value <= 0f || _inventory == null)
                return;

            if (!_inventory.RemoveItemServer("coral_tonic", 1))
                return;

            CurrentHP.Value = Mathf.Min(maxHP, CurrentHP.Value + 50f);
            CurrentStamina.Value = Mathf.Min(maxStamina, CurrentStamina.Value + 50f);
        }

        [ServerRpc]
        private void EmergencySavePlayerServerRpc()
        {
            SplitSaveManager.Instance?.SavePlayerByClientId(OwnerClientId, removeFromActiveCache: false);
        }

        [ServerRpc]
        private void TeleportToPositionServerRpc(Vector3 targetPosition)
        {
            if (_characterController != null) _characterController.enabled = false;
            transform.position = targetPosition;
            _verticalVelocity = 0f;
            if (_characterController != null) _characterController.enabled = true;
        }

        [ServerRpc]
        private void SyncMountStateServerRpc(Vector3 worldPos, float yaw, bool isMounted)
        {
            _isMountedOnRaft = isMounted;
            if (_characterController != null && _characterController.enabled == isMounted)
            {
                _characterController.enabled = !isMounted;
            }
            transform.position = worldPos;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _verticalVelocity = 0f;
        }

        [ServerRpc]
        private void ApplyBossAoEDamageServerRpc(float damage)
        {
            TakeDamageServer(damage);
        }

        [ClientRpc]
        private void PlayPlayerAttackClientRpc()
        {
            _animSync?.PlayAttack();
        }

        [ClientRpc]
        private void PlayPlayerHitClientRpc()
        {
            _animSync?.PlayHit();
        }

        #endregion

        #region Server Health & Save Export

        public void HealVitalsServer(float hpHeal, float staminaHeal)
        {
            if (!IsServer || !IsSpawned || CurrentHP.Value <= 0f)
                return;

            if (hpHeal > 0f)
                CurrentHP.Value = Mathf.Min(maxHP, CurrentHP.Value + hpHeal);

            if (staminaHeal > 0f)
                CurrentStamina.Value = Mathf.Min(maxStamina, CurrentStamina.Value + staminaHeal);
        }

        public void TakeDamageServer(float damage)
        {
            if (!IsServer || !IsSpawned || damage <= 0f)
                return;

            CurrentHP.Value = Mathf.Max(0f, CurrentHP.Value - damage);
            if (CurrentHP.Value > 0f)
            {
                PlayPlayerHitClientRpc();
            }
            else
            {
                // PvE friendly respawn on the coral beach
                _characterController.enabled = false;
                transform.position = respawnBeachPosition;
                _characterController.enabled = true;
                CurrentHP.Value = maxHP;
                CurrentStamina.Value = maxStamina;
                CurrentOxygen.Value = maxOxygen;
            }
        }

        public void ExportSaveData(PlayerSaveData saveData)
        {
            if (saveData == null)
                return;

            saveData.LastPosition = SerializableVector3.FromVector3(transform.position);
            saveData.LastRotationY = transform.eulerAngles.y;
            saveData.CurrentHP = CurrentHP.Value;
            saveData.CurrentStamina = CurrentStamina.Value;
            saveData.CurrentOxygen = CurrentOxygen.Value;

            if (_inventory != null)
            {
                _inventory.ExportToSave(saveData.Inventory);
            }

            if (_onboarding != null)
            {
                _onboarding.ExportToSave(saveData);
            }
        }

        #endregion
    }
}
