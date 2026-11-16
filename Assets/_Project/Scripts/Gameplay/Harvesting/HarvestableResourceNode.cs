using System;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Harvesting
{
    /// <summary>
    /// Server-authoritative harvestable resource node (e.g., Coral Wood Tree or Bioluminescent Crystal).
    /// Respawn timer strictly tracks WorldTimeManager.TotalWorldHours so regrowth freezes when 0 players are online.
    /// </summary>
    public class HarvestableResourceNode : NetworkBehaviour
    {
        [Header("Resource Configuration")]
        [SerializeField] private string nodeUniqueId;
        [SerializeField] private string resourceItemId = "coral_wood";
        [SerializeField] private int yieldPerDepletion = 3;
        [SerializeField] private float maxHealth = 30f;
        [Tooltip("In-game world hours before this node regrows (frozen when 0 players are online).")]
        [SerializeField] private float respawnWorldHours = 2.0f;

        [Header("Scene References")]
        [SerializeField] private GameObject visualRoot;
        [SerializeField] private Collider nodeCollider;

        public readonly NetworkVariable<bool> IsDepleted = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CurrentHealth = new NetworkVariable<float>(
            30f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public string ResourceItemId => resourceItemId;

        private double _respawnAtWorldHours;

        private void Awake()
        {
            if (string.IsNullOrEmpty(nodeUniqueId))
            {
                Vector3 p = transform.position;
                nodeUniqueId = $"{resourceItemId}_{Mathf.RoundToInt(p.x * 10)}_{Mathf.RoundToInt(p.y * 10)}_{Mathf.RoundToInt(p.z * 10)}";
            }

            if (visualRoot == null)
                visualRoot = gameObject;

            if (nodeCollider == null)
                nodeCollider = GetComponent<Collider>();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrEmpty(nodeUniqueId) && !Application.isPlaying)
            {
                nodeUniqueId = $"{resourceItemId}_{Guid.NewGuid().ToString("N").Substring(0, 10)}";
            }
        }
#endif

        public override void OnNetworkSpawn()
        {
            IsDepleted.OnValueChanged += HandleDepletedChanged;

            if (IsServer)
            {
                CurrentHealth.Value = maxHealth;

                if (SplitSaveManager.Instance != null &&
                    SplitSaveManager.Instance.TryGetHarvestedNodeRespawn(nodeUniqueId, out double savedRespawnHours))
                {
                    double nowHours = WorldTimeManager.Instance != null
                        ? WorldTimeManager.Instance.TotalWorldHours.Value
                        : 0d;

                    if (nowHours < savedRespawnHours)
                    {
                        _respawnAtWorldHours = savedRespawnHours;
                        IsDepleted.Value = true;
                    }
                    else
                    {
                        SplitSaveManager.Instance.ClearHarvestedNode(nodeUniqueId);
                        IsDepleted.Value = false;
                    }
                }
            }

            ApplyVisualState(IsDepleted.Value);
        }

        public override void OnNetworkDespawn()
        {
            IsDepleted.OnValueChanged -= HandleDepletedChanged;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || !IsDepleted.Value)
                return;

            if (WorldTimeManager.Instance == null || WorldTimeManager.Instance.IsPaused.Value)
                return;

            if (WorldTimeManager.Instance.TotalWorldHours.Value >= _respawnAtWorldHours)
            {
                RespawnNodeServer();
            }
        }

        public void TakeHarvestHitServer(float damage, PlayerInventory harvesterInventory)
        {
            if (!IsServer || !IsSpawned || IsDepleted.Value || damage <= 0f)
                return;

            float nextHp = CurrentHealth.Value - damage;
            if (nextHp > 0f)
            {
                CurrentHealth.Value = nextHp;
                PlayHitFeedbackClientRpc(damage, 0);
                return;
            }

            CurrentHealth.Value = 0f;
            IsDepleted.Value = true;

            double currentWorldHours = WorldTimeManager.Instance != null
                ? WorldTimeManager.Instance.TotalWorldHours.Value
                : 0d;

            _respawnAtWorldHours = currentWorldHours + respawnWorldHours;
            SplitSaveManager.Instance?.RecordHarvestedNode(nodeUniqueId, _respawnAtWorldHours);

            if (harvesterInventory != null)
            {
                harvesterInventory.AddItemServer(resourceItemId, yieldPerDepletion);
            }

            PlayHitFeedbackClientRpc(damage, yieldPerDepletion);
        }

        private void RespawnNodeServer()
        {
            CurrentHealth.Value = maxHealth;
            IsDepleted.Value = false;
            SplitSaveManager.Instance?.ClearHarvestedNode(nodeUniqueId);
        }

        private void HandleDepletedChanged(bool previousValue, bool newValue)
        {
            ApplyVisualState(newValue);
        }

        private void ApplyVisualState(bool depleted)
        {
            if (visualRoot != null && visualRoot != gameObject)
            {
                visualRoot.SetActive(!depleted);
            }
            else
            {
                foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = !depleted;
                }
            }

            if (nodeCollider != null)
            {
                nodeCollider.enabled = !depleted;
            }
        }

        [ClientRpc]
        private void PlayHitFeedbackClientRpc(float damageTaken, int yieldGranted)
        {
            if (CombatFeedbackManager.Instance == null)
                return;

            CombatFeedbackManager.Instance.TriggerHaptic(yieldGranted > 0 ? 1 : 0);
            CombatFeedbackManager.Instance.PlayTargetPunch(visualRoot != null ? visualRoot.transform : transform);

            string popupText = yieldGranted > 0
                ? $"+{yieldGranted} {resourceItemId}"
                : $"-{damageTaken:0}";
            Color popupColor = yieldGranted > 0
                ? new Color(0.28f, 0.96f, 0.86f)
                : new Color(1.0f, 0.85f, 0.45f);

            CombatFeedbackManager.Instance.SpawnWorldPopup(
                transform.position + Vector3.up * 1.4f,
                popupText,
                popupColor);
        }
    }
}
