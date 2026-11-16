using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace CoralArchipelago.Gameplay.AI
{
    /// <summary>
    /// Dynamic PvE Difficulty Director.
    /// Scales all active monsters' MaxHP and Attack Damage smoothly whenever players join or leave (1..10 players),
    /// preserving each monster's current HP percentage.
    /// </summary>
    public class PvEDirector : NetworkBehaviour
    {
        public static PvEDirector Instance { get; private set; }

        [Header("Scaling Formula Parameters")]
        [Tooltip("Extra HP multiplier added per additional online player (0.65 = +65% per player).")]
        [SerializeField] private float hpScalePerExtraPlayer = 0.65f;

        [Tooltip("Extra Damage multiplier added per additional online player (0.15 = +15% per player).")]
        [SerializeField] private float damageScalePerExtraPlayer = 0.15f;

        public readonly NetworkVariable<int> ActivePlayerCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CurrentHpMultiplier = new NetworkVariable<float>(
            1.0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CurrentDamageMultiplier = new NetworkVariable<float>(
            1.0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly List<ScalableMonsterAI> _registeredMonsters = new List<ScalableMonsterAI>();

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
            if (IsServer && NetworkManager.Singleton != null)
            {
                RecalculateDifficultyServer(NetworkManager.Singleton.ConnectedClientsIds.Count);
            }
        }

        public void RegisterMonster(ScalableMonsterAI monster)
        {
            if (monster == null || _registeredMonsters.Contains(monster))
                return;

            _registeredMonsters.Add(monster);

            if (IsServer && IsSpawned)
            {
                monster.ApplyDifficultyScalingServer(CurrentHpMultiplier.Value, CurrentDamageMultiplier.Value);
            }
        }

        public void UnregisterMonster(ScalableMonsterAI monster)
        {
            _registeredMonsters.Remove(monster);
        }

        public void RecalculateDifficultyServer(int onlinePlayers)
        {
            if (!IsServer || !IsSpawned)
                return;

            int clampedCount = Mathf.Clamp(onlinePlayers, 0, 10);
            int extraPlayers = Mathf.Max(0, clampedCount - 1);

            float hpMult = 1.0f + (hpScalePerExtraPlayer * extraPlayers);
            float dmgMult = 1.0f + (damageScalePerExtraPlayer * extraPlayers);

            ActivePlayerCount.Value = clampedCount;
            CurrentHpMultiplier.Value = hpMult;
            CurrentDamageMultiplier.Value = dmgMult;

            for (int i = _registeredMonsters.Count - 1; i >= 0; i--)
            {
                if (_registeredMonsters[i] == null)
                {
                    _registeredMonsters.RemoveAt(i);
                    continue;
                }
                _registeredMonsters[i].ApplyDifficultyScalingServer(hpMult, dmgMult);
            }

            Debug.Log($"[PvEDirector] Scaled PvE difficulty for {clampedCount} player(s): HP x{hpMult:0.00}, DMG x{dmgMult:0.00}");
        }
    }
}
