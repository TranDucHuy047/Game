using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Networking;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Core
{
    /// <summary>
    /// Server-authoritative Day/Night and World Time controller.
    /// Automatically freezes when IsPaused is true (0 active players on Dedicated Server),
    /// and drives Bioluminescent Coral & Water shaders at night on iOS clients without dynamic point lights.
    /// </summary>
    public class WorldTimeManager : NetworkBehaviour
    {
        public static WorldTimeManager Instance { get; private set; }

        [Header("Cycle Configuration")]
        [Tooltip("Length of a full 24-hour in-game day in real-time minutes.")]
        [SerializeField] private float fullDayMinutes = 24f;
        [SerializeField] private float syncToSaveIntervalSeconds = 10f;

        [Header("Shared Tide Configuration")]
        [SerializeField] private float baseSeaLevelY = 0.0f;
        [SerializeField] private float tideAmplitudeY = 0.55f;

        [Header("Client Visuals (Optional in Headless Server)")]
        [SerializeField] private Light directionalSunLight;
        [SerializeField] private Gradient sunColorOverDay;
        [SerializeField] private AnimationCurve bioluminescenceNightCurve = new AnimationCurve(
            new Keyframe(0.0f, 1.0f),   // Midnight: max glow
            new Keyframe(0.22f, 0.9f),  // ~05:15 AM
            new Keyframe(0.28f, 0.0f),  // ~06:45 AM: daylight
            new Keyframe(0.72f, 0.0f),  // ~17:15 PM: dusk starts
            new Keyframe(0.80f, 1.0f),  // ~19:15 PM: bioluminescence peaks
            new Keyframe(1.0f, 1.0f)    // Midnight
        );

        public readonly NetworkVariable<float> WorldTimeHours = new NetworkVariable<float>(
            8.0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> CurrentDay = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<double> TotalWorldHours = new NetworkVariable<double>(
            8.0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> IsPaused = new NetworkVariable<bool>(
            true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private static readonly int GlobalBioluminescenceId = Shader.PropertyToID("_GlobalBioluminescence");
        private float _saveSyncTimer;

        public bool IsNightTime => WorldTimeHours.Value >= 18.0f || WorldTimeHours.Value < 6.0f;
        public float CurrentBioluminescenceIntensity { get; private set; }

        /// <summary>
        /// Synchronized ocean height shared by server oxygen checks and client water visuals.
        /// High tide peaks at 00:00 & 12:00; low tide peaks at 06:00 & 18:00.
        /// </summary>
        public float GetCurrentSeaLevelY()
        {
            float radians = (WorldTimeHours.Value / 12.0f) * Mathf.PI * 2f;
            return baseSeaLevelY + Mathf.Cos(radians) * tideAmplitudeY;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureDefaultDayNightGradient();
        }

        private void EnsureDefaultDayNightGradient()
        {
            if (sunColorOverDay != null && sunColorOverDay.colorKeys != null && sunColorOverDay.colorKeys.Length > 2)
                return;

            sunColorOverDay = new Gradient
            {
                colorKeys = new[]
                {
                    new GradientColorKey(new Color(0.24f, 0.42f, 0.78f), 0.00f), // Midnight moonlight
                    new GradientColorKey(new Color(1.00f, 0.62f, 0.30f), 0.24f), // Dawn golden-orange
                    new GradientColorKey(new Color(1.00f, 0.94f, 0.84f), 0.34f), // Morning tropical sun
                    new GradientColorKey(new Color(1.00f, 0.98f, 0.94f), 0.52f), // Clear noon
                    new GradientColorKey(new Color(1.00f, 0.42f, 0.34f), 0.74f), // Crimson-violet sunset
                    new GradientColorKey(new Color(0.22f, 0.40f, 0.76f), 0.83f), // Deep night blue
                    new GradientColorKey(new Color(0.24f, 0.42f, 0.78f), 1.00f)
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                }
            };
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                if (SplitSaveManager.Instance != null)
                {
                    WorldSaveData save = SplitSaveManager.Instance.CurrentWorldSave;
                    WorldTimeHours.Value = Mathf.Repeat(save.WorldTimeHours, 24f);
                    CurrentDay.Value = Mathf.Max(1, save.CurrentDay);
                    TotalWorldHours.Value = save.TotalWorldHours;
                }

                bool shouldPause = ServerDormantController.Instance != null && ServerDormantController.Instance.IsDormant;
                IsPaused.Value = shouldPause;
            }

            if (IsClient)
            {
                UpdateClientVisuals(WorldTimeHours.Value);
            }
        }

        private void Update()
        {
            if (!IsSpawned)
                return;

            if (IsServer && !IsPaused.Value)
            {
                AdvanceServerTime(Time.deltaTime);
            }

            if (IsClient)
            {
                UpdateClientVisuals(WorldTimeHours.Value);
            }
        }

        private void AdvanceServerTime(float deltaTime)
        {
            float hoursPerSecond = 24f / Mathf.Max(1f, fullDayMinutes * 60f);
            float deltaHours = deltaTime * hoursPerSecond;

            float nextHours = WorldTimeHours.Value + deltaHours;
            if (nextHours >= 24f)
            {
                nextHours -= 24f;
                CurrentDay.Value += 1;
            }

            WorldTimeHours.Value = nextHours;
            TotalWorldHours.Value += deltaHours;

            _saveSyncTimer += deltaTime;
            if (_saveSyncTimer >= syncToSaveIntervalSeconds)
            {
                _saveSyncTimer = 0f;
                SyncTimeToWorldSave();
            }
        }

        public void SetPausedServer(bool paused)
        {
            if (!IsServer || !IsSpawned)
                return;

            if (IsPaused.Value == paused)
                return;

            IsPaused.Value = paused;
            SyncTimeToWorldSave();
            Debug.Log($"[WorldTimeManager] World time {(paused ? "FROZEN (0 players)" : "RESUMED")} at Day {CurrentDay.Value}, {WorldTimeHours.Value:00.00}h.");
        }

        /// <summary>
        /// Server/Host-only debug helper to fast-forward world time for testing night bioluminescence,
        /// tidal cave gates, and node regrowth timers.
        /// </summary>
        public void AdvanceHoursDebugServer(float hours)
        {
            if (!IsServer || !IsSpawned || hours <= 0f)
                return;

            float totalDayHours = WorldTimeHours.Value + hours;
            int addedDays = Mathf.FloorToInt(totalDayHours / 24f);
            if (addedDays > 0)
            {
                CurrentDay.Value += addedDays;
            }

            WorldTimeHours.Value = Mathf.Repeat(totalDayHours, 24f);
            TotalWorldHours.Value += hours;
            SyncTimeToWorldSave();
            Debug.Log($"[WorldTimeManager] Debug skipped +{hours:0.0}h -> Day {CurrentDay.Value}, {WorldTimeHours.Value:00.00}h.");
        }

        public void SyncTimeToWorldSave()
        {
            if (SplitSaveManager.Instance == null)
                return;

            WorldSaveData save = SplitSaveManager.Instance.CurrentWorldSave;
            save.WorldTimeHours = WorldTimeHours.Value;
            save.CurrentDay = CurrentDay.Value;
            save.TotalWorldHours = TotalWorldHours.Value;
            SplitSaveManager.Instance.MarkWorldDirty();
        }

        private void UpdateClientVisuals(float hourOfDay)
        {
            float normalizedDay = Mathf.Clamp01(hourOfDay / 24f);
            float glow = bioluminescenceNightCurve.Evaluate(normalizedDay);
            CurrentBioluminescenceIntensity = glow;
            Shader.SetGlobalFloat(GlobalBioluminescenceId, glow);

            if (directionalSunLight != null)
            {
                bool isDay = hourOfDay >= 5.5f && hourOfDay <= 18.5f;
                float elevationAngle;
                if (isDay)
                {
                    float dayT = Mathf.InverseLerp(5.5f, 18.5f, hourOfDay);
                    elevationAngle = Mathf.Lerp(10f, 170f, dayT);
                    directionalSunLight.intensity = Mathf.Lerp(0.45f, 1.50f, Mathf.Sin(dayT * Mathf.PI));
                }
                else
                {
                    float nightHours = hourOfDay > 18.5f ? (hourOfDay - 18.5f) : (hourOfDay + 5.5f);
                    float nightT = Mathf.Clamp01(nightHours / 11.0f);
                    elevationAngle = Mathf.Lerp(28f, 148f, nightT);
                    directionalSunLight.intensity = 0.38f;
                }

                directionalSunLight.transform.rotation = Quaternion.Euler(elevationAngle, 165f, 0f);

                if (sunColorOverDay != null && sunColorOverDay.colorKeys.Length > 0)
                {
                    directionalSunLight.color = sunColorOverDay.Evaluate(normalizedDay);
                }
            }
        }
    }
}
