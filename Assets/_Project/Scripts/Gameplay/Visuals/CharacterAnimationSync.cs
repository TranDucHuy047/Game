using System.Collections.Generic;
using UnityEngine;
using CoralArchipelago.Networking;

namespace CoralArchipelago.Gameplay.Visuals
{
    /// <summary>
    /// Synchronizes skeletal Animator parameters (Speed, Attack, Hit, IsDead)
    /// across Player and Monster prefabs based on real-time world velocity and Network RPC events.
    /// Automatically disables itself on headless Dedicated Servers.
    /// </summary>
    public class CharacterAnimationSync : MonoBehaviour
    {
        [SerializeField] private Animator targetAnimator;
        [SerializeField] private float speedSmoothing = 10f;

        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int AttackHash = Animator.StringToHash("Attack");
        private static readonly int HitHash = Animator.StringToHash("Hit");
        private static readonly int IsDeadHash = Animator.StringToHash("IsDead");

        private readonly HashSet<int> _validParameterHashes = new HashSet<int>();
        private Vector3 _lastPosition;
        private float _smoothedSpeed;
        private bool _initialized;

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                enabled = false;
                return;
            }

            _lastPosition = transform.position;
        }

        private void Start()
        {
            ResolveAnimator();
            _lastPosition = transform.position;
        }

        private void Update()
        {
            if (!_initialized)
            {
                ResolveAnimator();
            }

            if (targetAnimator == null || !targetAnimator.isActiveAndEnabled)
                return;

            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            Vector3 delta = transform.position - _lastPosition;
            delta.y = 0f;
            float instantSpeed = delta.magnitude / dt;
            _lastPosition = transform.position;

            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, instantSpeed, dt * speedSmoothing);

            if (_validParameterHashes.Contains(SpeedHash))
            {
                targetAnimator.SetFloat(SpeedHash, _smoothedSpeed);
            }
        }

        public void PlayAttack()
        {
            ResolveAnimator();
            if (targetAnimator != null && _validParameterHashes.Contains(AttackHash))
            {
                targetAnimator.ResetTrigger(AttackHash);
                targetAnimator.SetTrigger(AttackHash);
            }
        }

        public void PlayHit()
        {
            ResolveAnimator();
            if (targetAnimator != null && _validParameterHashes.Contains(HitHash))
            {
                targetAnimator.ResetTrigger(HitHash);
                targetAnimator.SetTrigger(HitHash);
            }
        }

        public void SetDead(bool dead)
        {
            ResolveAnimator();
            if (targetAnimator != null && _validParameterHashes.Contains(IsDeadHash))
            {
                targetAnimator.SetBool(IsDeadHash, dead);
            }
        }

        public void RefreshTargetAnimator(Animator newAnimator = null)
        {
            if (newAnimator != null)
                targetAnimator = newAnimator;

            _initialized = false;
            ResolveAnimator();
        }

        private void ResolveAnimator()
        {
            if (targetAnimator == null)
            {
                targetAnimator = GetComponentInChildren<Animator>(true);
            }

            if (targetAnimator == null || _initialized)
                return;

            if (targetAnimator.runtimeAnimatorController == null || !targetAnimator.isInitialized || targetAnimator.layerCount <= 0)
                return;

            _validParameterHashes.Clear();
            foreach (AnimatorControllerParameter param in targetAnimator.parameters)
            {
                _validParameterHashes.Add(param.nameHash);
            }
            _initialized = true;
        }
    }
}
