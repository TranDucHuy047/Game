using UnityEngine;
using CoralArchipelago.Core;

namespace CoralArchipelago.Gameplay.Environment
{
    /// <summary>
    /// Controls an underground Crystal Cave entrance that opens automatically during Low Tide
    /// (04:30-07:30 and 16:30-19:30 in-game hours) driven by the synchronized WorldTimeManager.
    /// When 0 players are online and world time freezes, the gate stays locked in its exact state.
    /// </summary>
    public class TidalCrystalCaveGate : MonoBehaviour
    {
        public static TidalCrystalCaveGate PrimaryGate { get; private set; }

        [Header("Gate Visuals & Collider")]
        [SerializeField] private Transform gateVisualTransform;
        [SerializeField] private Collider gateCollider;
        [SerializeField] private Vector3 closedLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 openLocalPosition = new Vector3(0f, -4.5f, 0f);
        [SerializeField] private float moveSpeed = 2.5f;

        [Header("Low Tide Windows (24h Clock)")]
        [SerializeField] private float morningLowTideStart = 4.5f;
        [SerializeField] private float morningLowTideEnd = 7.5f;
        [SerializeField] private float eveningLowTideStart = 16.5f;
        [SerializeField] private float eveningLowTideEnd = 19.5f;

        public bool IsGateOpen { get; private set; }

        private void Awake()
        {
            if (PrimaryGate == null)
                PrimaryGate = this;

            if (gateVisualTransform == null)
                gateVisualTransform = transform;

            if (gateCollider == null)
                gateCollider = GetComponent<Collider>();
        }

        private void OnDestroy()
        {
            if (PrimaryGate == this)
                PrimaryGate = null;
        }

        private void Update()
        {
            float hour = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.WorldTimeHours.Value : 8f;
            IsGateOpen = IsLowTideHour(hour);

            Vector3 targetLocalPos = IsGateOpen ? openLocalPosition : closedLocalPosition;
            gateVisualTransform.localPosition = Vector3.Lerp(
                gateVisualTransform.localPosition,
                targetLocalPos,
                Time.deltaTime * moveSpeed);

            if (gateCollider != null)
            {
                bool physicallyClear = Vector3.Distance(gateVisualTransform.localPosition, openLocalPosition) < 0.75f;
                gateCollider.enabled = !(IsGateOpen && physicallyClear);
            }
        }

        public bool IsLowTideHour(float hourOfDay)
        {
            bool morningWindow = hourOfDay >= morningLowTideStart && hourOfDay <= morningLowTideEnd;
            bool eveningWindow = hourOfDay >= eveningLowTideStart && hourOfDay <= eveningLowTideEnd;
            return morningWindow || eveningWindow;
        }
    }
}
