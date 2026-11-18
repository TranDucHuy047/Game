using UnityEngine;
using CoralArchipelago.Core;
using CoralArchipelago.Networking;

namespace CoralArchipelago.ClientIOS
{
    /// <summary>
    /// Client-side atmosphere controller for the Bioluminescent Coral Archipelago.
    /// Drives the daily tide cycle (exposing underwater coral/crystal caves at low tide),
    /// smooth day/night fog & ambient transitions, and thermal-aware night spore particle density.
    /// </summary>
    public class BioluminescenceEnvironmentController : MonoBehaviour
    {
        [Header("Ocean Tide Cycle")]
        [SerializeField] private Transform oceanSurfaceTransform;
        [SerializeField] private float baseSeaLevelY = -2.2f;
        [SerializeField] private float tideAmplitudeY = 0.05f;

        [Header("Day / Night Atmosphere")]
        [SerializeField] private Color dayFogColor = new Color(0.55f, 0.80f, 0.92f, 1f);
        [SerializeField] private Color nightFogColor = new Color(0.02f, 0.07f, 0.18f, 1f);
        [SerializeField] private float dayFogDensity = 0.00065f;
        [SerializeField] private float nightFogDensity = 0.00085f;
        [SerializeField] private Color dayAmbientColor = new Color(0.52f, 0.62f, 0.72f, 1f);
        [SerializeField] private Color nightAmbientColor = new Color(0.06f, 0.16f, 0.28f, 1f);

        [Header("Night Bioluminescent Particles")]
        [SerializeField] private ParticleSystem[] nightSporeSystems;
        [SerializeField] private float maxParticleRateMultiplier = 1.0f;

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                Destroy(gameObject);
                return;
            }
        }

        private void Start()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
        }

        private void Update()
        {
            float hourOfDay = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.WorldTimeHours.Value : 8f;
            float nightIntensity = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.CurrentBioluminescenceIntensity : 0f;

            UpdateTideLevel(hourOfDay);
            UpdateFogAndAmbient(nightIntensity);
            UpdateBioluminescentParticles(nightIntensity);
        }

        private void UpdateTideLevel(float hourOfDay)
        {
            if (oceanSurfaceTransform == null)
                return;

            float seaBase = global::IslandGraphicsUpgrader.Instance != null
                ? global::IslandGraphicsUpgrader.Instance.waterLevel
                : baseSeaLevelY;

            float nextY = WorldTimeManager.Instance != null
                ? WorldTimeManager.Instance.GetCurrentSeaLevelY()
                : seaBase + Mathf.Cos((hourOfDay / 12.0f) * Mathf.PI * 2f) * tideAmplitudeY;

            Vector3 pos = oceanSurfaceTransform.position;
            pos.y = nextY;
            oceanSurfaceTransform.position = pos;
        }

        private void UpdateFogAndAmbient(float nightIntensity)
        {
            float t = Mathf.Clamp01(nightIntensity);
            RenderSettings.fogColor = Color.Lerp(dayFogColor, nightFogColor, t);
            RenderSettings.fogDensity = Mathf.Lerp(dayFogDensity, nightFogDensity, t);
            RenderSettings.ambientLight = Color.Lerp(dayAmbientColor, nightAmbientColor, t);
        }

        private void UpdateBioluminescentParticles(float nightIntensity)
        {
            if (nightSporeSystems == null || nightSporeSystems.Length == 0)
                return;

            float thermalScale = GetThermalParticleMultiplier();
            float targetRate = Mathf.Clamp01(nightIntensity) * maxParticleRateMultiplier * thermalScale;

            for (int i = 0; i < nightSporeSystems.Length; i++)
            {
                ParticleSystem ps = nightSporeSystems[i];
                if (ps == null)
                    continue;

                var emission = ps.emission;
                emission.rateOverTimeMultiplier = targetRate;

                if (targetRate > 0.05f)
                {
                    if (!ps.isPlaying)
                        ps.Play();
                }
                else if (ps.isPlaying)
                {
                    ps.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }

        private static float GetThermalParticleMultiplier()
        {
            if (iOSGraphicsAndThermalManager.Instance == null)
                return 1.0f;

            switch (iOSGraphicsAndThermalManager.Instance.CurrentThermalLevel)
            {
                case AppleThermalLevel.Nominal: return 1.0f;
                case AppleThermalLevel.Fair: return 0.7f;
                case AppleThermalLevel.Serious: return 0.35f;
                case AppleThermalLevel.Critical: return 0.0f;
                default: return 1.0f;
            }
        }
    }
}
