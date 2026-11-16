using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

namespace CoralArchipelago.Gameplay.AI
{
    public enum MonsterState : byte
    {
        Dormant = 0,
        Patrol = 1,
        Chase = 2,
        Attack = 3,
        Dead = 4
    }

    /// <summary>
    /// Server-authoritative PvE Monster AI (e.g., Bioluminescent Reef Crab).
    /// Supports Dormant hibernation when 0 players are online, dynamic HP/DMG scaling via PvEDirector,
    /// and frozen-safe respawn timing via WorldTimeManager.TotalWorldHours.
    /// </summary>
    public class ScalableMonsterAI : NetworkBehaviour
    {
        [Header("Base Stats (1 Player)")]
        [SerializeField] private float baseMaxHP = 80f;
        [SerializeField] private float baseDamage = 12f;
        [SerializeField] private float moveSpeed = 3.5f;
        [SerializeField] private float aggroRadius = 14f;
        [SerializeField] private float loseAggroRadius = 22f;
        [SerializeField] private float attackRange = 2.2f;
        [SerializeField] private float attackCooldown = 1.6f;
        [SerializeField] private float patrolRadius = 8f;
        [SerializeField] private float respawnWorldHours = 1.0f;

        [Header("Visuals")]
        [SerializeField] private GameObject visualRoot;
        [SerializeField] private Collider monsterCollider;

        public readonly NetworkVariable<MonsterState> CurrentState = new NetworkVariable<MonsterState>(
            MonsterState.Dormant, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> CurrentHP = new NetworkVariable<float>(
            80f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> MaxHP = new NetworkVariable<float>(
            80f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NavMeshAgent _agent;
        private Vector3 _spawnOrigin;
        private Vector3 _patrolTarget;
        private float _currentDamage;
        private float _attackTimer;
        private float _patrolWaitTimer;
        private double _respawnAtWorldHours;
        private NetworkPlayerController _targetPlayer;
        private bool _isHibernating;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            if (visualRoot == null)
                visualRoot = gameObject;
            if (monsterCollider == null)
                monsterCollider = GetComponent<Collider>();

            _spawnOrigin = transform.position;
            _patrolTarget = _spawnOrigin;
            _currentDamage = baseDamage;
        }

        public override void OnNetworkSpawn()
        {
            CurrentState.OnValueChanged += HandleStateChanged;

            if (IsServer)
            {
                MaxHP.Value = baseMaxHP;
                CurrentHP.Value = baseMaxHP;
                _currentDamage = baseDamage;

                PvEDirector.Instance?.RegisterMonster(this);

                bool shouldSleep = ServerDormantController.Instance != null
                    ? ServerDormantController.Instance.IsDormant
                    : (WorldTimeManager.Instance != null && WorldTimeManager.Instance.IsPaused.Value);
                SetDormantServer(shouldSleep);
            }

            ApplyAliveVisuals(CurrentState.Value != MonsterState.Dead);
        }

        public override void OnNetworkDespawn()
        {
            CurrentState.OnValueChanged -= HandleStateChanged;
            if (IsServer)
            {
                PvEDirector.Instance?.UnregisterMonster(this);
            }
        }

        public void SetDormantServer(bool dormant)
        {
            if (!IsServer || !IsSpawned)
                return;

            _isHibernating = dormant;

            if (CurrentState.Value == MonsterState.Dead)
                return;

            if (dormant)
            {
                CurrentState.Value = MonsterState.Dormant;
                if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
                {
                    _agent.isStopped = true;
                }
            }
            else
            {
                if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
                {
                    _agent.isStopped = false;
                }
                CurrentState.Value = MonsterState.Patrol;
            }
        }

        /// <summary>
        /// Dynamically scales MaxHP and Attack Damage while keeping current HP percentage intact.
        /// </summary>
        public void ApplyDifficultyScalingServer(float hpMultiplier, float damageMultiplier)
        {
            if (!IsServer || !IsSpawned)
                return;

            float previousMax = Mathf.Max(1f, MaxHP.Value);
            float hpRatio = Mathf.Clamp01(CurrentHP.Value / previousMax);

            float newMax = baseMaxHP * Mathf.Max(1f, hpMultiplier);
            MaxHP.Value = newMax;

            if (CurrentState.Value != MonsterState.Dead)
            {
                CurrentHP.Value = Mathf.Max(1f, newMax * hpRatio);
            }

            _currentDamage = baseDamage * Mathf.Max(1f, damageMultiplier);
        }

        public void TakeDamageServer(float damage, ulong attackerClientId)
        {
            if (!IsServer || !IsSpawned || CurrentState.Value == MonsterState.Dead || damage <= 0f)
                return;

            CurrentHP.Value = Mathf.Max(0f, CurrentHP.Value - damage);

            bool killed = CurrentHP.Value <= 0f;
            PlayMonsterHitClientRpc(damage, killed);

            if (killed)
            {
                DieServer();
                return;
            }

            // Immediately aggro onto attacker if not currently chasing anyone
            if (_targetPlayer == null &&
                NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerClientId, out NetworkClient client) &&
                client.PlayerObject != null)
            {
                _targetPlayer = client.PlayerObject.GetComponent<NetworkPlayerController>();
                CurrentState.Value = MonsterState.Chase;
            }
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || _isHibernating)
                return;

            if (WorldTimeManager.Instance != null && WorldTimeManager.Instance.IsPaused.Value)
                return;

            if (CurrentState.Value == MonsterState.Dead)
            {
                if (WorldTimeManager.Instance != null &&
                    WorldTimeManager.Instance.TotalWorldHours.Value >= _respawnAtWorldHours)
                {
                    RespawnServer();
                }
                return;
            }

            if (_attackTimer > 0f)
                _attackTimer -= Time.deltaTime;

            switch (CurrentState.Value)
            {
                case MonsterState.Patrol:
                    UpdatePatrol();
                    break;
                case MonsterState.Chase:
                    UpdateChase();
                    break;
                case MonsterState.Attack:
                    UpdateAttack();
                    break;
            }
        }

        private void UpdatePatrol()
        {
            NetworkPlayerController nearest = FindNearestLivingPlayer(aggroRadius);
            if (nearest != null)
            {
                _targetPlayer = nearest;
                CurrentState.Value = MonsterState.Chase;
                return;
            }

            float distToTarget = Vector3.Distance(transform.position, _patrolTarget);
            if (distToTarget <= 1.0f)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer >= 2.5f)
                {
                    _patrolWaitTimer = 0f;
                    Vector2 circle = Random.insideUnitCircle * patrolRadius;
                    _patrolTarget = _spawnOrigin + new Vector3(circle.x, 0f, circle.y);
                }
            }
            else
            {
                MoveTowards(_patrolTarget, moveSpeed * 0.65f);
            }
        }

        private void UpdateChase()
        {
            if (!IsValidTarget(_targetPlayer))
            {
                _targetPlayer = null;
                CurrentState.Value = MonsterState.Patrol;
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetPlayer.transform.position);
            if (dist > loseAggroRadius)
            {
                _targetPlayer = null;
                CurrentState.Value = MonsterState.Patrol;
                return;
            }

            if (dist <= attackRange)
            {
                StopMovement();
                CurrentState.Value = MonsterState.Attack;
                return;
            }

            MoveTowards(_targetPlayer.transform.position, moveSpeed);
        }

        private void UpdateAttack()
        {
            if (!IsValidTarget(_targetPlayer))
            {
                _targetPlayer = null;
                CurrentState.Value = MonsterState.Patrol;
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetPlayer.transform.position);
            if (dist > attackRange * 1.25f)
            {
                CurrentState.Value = MonsterState.Chase;
                return;
            }

            Vector3 lookDir = (_targetPlayer.transform.position - transform.position);
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 8f);
            }

            if (_attackTimer <= 0f)
            {
                _attackTimer = attackCooldown;
                _targetPlayer.TakeDamageServer(_currentDamage);
            }
        }

        private void MoveTowards(Vector3 destination, float speed)
        {
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            {
                _agent.isStopped = false;
                _agent.speed = speed;
                _agent.SetDestination(destination);
                return;
            }

            // Fallback movement if NavMesh has not been baked yet during early prototyping
            Vector3 flatTarget = new Vector3(destination.x, transform.position.y, destination.z);
            Vector3 dir = (flatTarget - transform.position);
            if (dir.sqrMagnitude > 0.01f)
            {
                dir.Normalize();
                transform.position += dir * (speed * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 8f);
            }
        }

        private void StopMovement()
        {
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
            }
        }

        private NetworkPlayerController FindNearestLivingPlayer(float maxRadius)
        {
            if (NetworkManager.Singleton == null)
                return null;

            NetworkPlayerController best = null;
            float bestDistSq = maxRadius * maxRadius;

            foreach (NetworkClient client in NetworkManager.Singleton.ConnectedClientsList)
            {
                if (client.PlayerObject == null)
                    continue;

                var player = client.PlayerObject.GetComponent<NetworkPlayerController>();
                if (!IsValidTarget(player))
                    continue;

                float dSq = (player.transform.position - transform.position).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = player;
                }
            }

            return best;
        }

        private static bool IsValidTarget(NetworkPlayerController player)
        {
            return player != null && player.IsSpawned && player.CurrentHP.Value > 0f;
        }

        private void DieServer()
        {
            _targetPlayer = null;
            StopMovement();
            CurrentState.Value = MonsterState.Dead;

            double nowHours = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.TotalWorldHours.Value : 0d;
            _respawnAtWorldHours = nowHours + respawnWorldHours;
        }

        private void RespawnServer()
        {
            transform.position = _spawnOrigin;
            CurrentHP.Value = MaxHP.Value;
            CurrentState.Value = _isHibernating ? MonsterState.Dormant : MonsterState.Patrol;
        }

        private void HandleStateChanged(MonsterState previous, MonsterState next)
        {
            ApplyAliveVisuals(next != MonsterState.Dead);
        }

        private void ApplyAliveVisuals(bool alive)
        {
            if (visualRoot != null && visualRoot != gameObject)
            {
                visualRoot.SetActive(alive);
            }
            else
            {
                foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = alive;
                }
            }

            if (monsterCollider != null)
            {
                monsterCollider.enabled = alive;
            }
        }

        [ClientRpc]
        private void PlayMonsterHitClientRpc(float damageTaken, bool killed)
        {
            if (CombatFeedbackManager.Instance == null)
                return;

            CombatFeedbackManager.Instance.TriggerHaptic(killed ? 2 : 1);
            CombatFeedbackManager.Instance.PlayTargetPunch(visualRoot != null ? visualRoot.transform : transform);
            CombatFeedbackManager.Instance.SpawnWorldPopup(
                transform.position + Vector3.up * 1.5f,
                killed ? $"-{damageTaken:0}!" : $"-{damageTaken:0}",
                killed ? new Color(1.0f, 0.35f, 0.30f) : new Color(1.0f, 0.65f, 0.35f));
        }
    }
}
