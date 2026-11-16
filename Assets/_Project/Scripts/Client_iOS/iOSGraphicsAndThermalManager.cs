using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using CoralArchipelago.Networking;

namespace CoralArchipelago.ClientIOS
{
    public enum AppleThermalLevel
    {
        Nominal = 0,
        Fair = 1,
        Serious = 2,
        Critical = 3
    }

    /// <summary>
    /// Apple Silicon (iPhone) Graphics & Thermal Protection Manager.
    /// Runs at 60 FPS with 75% URP RenderScale (for MetalFX Upscaling) under Nominal heat,
    /// and dynamically scales down renderScale, shadowDistance, and VFX density if iOS reports Fair/Serious/Critical heat.
    /// </summary>
    public class iOSGraphicsAndThermalManager : MonoBehaviour
    {
        public static iOSGraphicsAndThermalManager Instance { get; private set; }

        [Header("Polling & Frame Rate")]
        [SerializeField] private float pollIntervalSeconds = 5f;
        [SerializeField] private int nominalTargetFps = 60;

        [Header("URP Render Scale Tiers (Combine with MetalFX in URP Asset)")]
        [SerializeField] private float nominalRenderScale = 0.75f;
        [SerializeField] private float fairRenderScale = 0.68f;
        [SerializeField] private float seriousRenderScale = 0.60f;
        [SerializeField] private float criticalRenderScale = 0.50f;

        [Header("Shadow Distance Tiers")]
        [SerializeField] private float nominalShadowDistance = 55f;
        [SerializeField] private float fairShadowDistance = 40f;
        [SerializeField] private float seriousShadowDistance = 24f;

        private static readonly int ThermalVfxMultiplierId = Shader.PropertyToID("_ThermalVFXMultiplier");

        public AppleThermalLevel CurrentThermalLevel { get; private set; } = AppleThermalLevel.Nominal;

        private float _pollTimer;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int _GetiOSThermalState();
#endif

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                Destroy(gameObject);
                return;
            }

            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            QualitySettings.vSyncCount = 0;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            ApplyThermalProfile(ReadNativeThermalLevel(), force: true);
        }

        private void Update()
        {
            _pollTimer += Time.unscaledDeltaTime;
            if (_pollTimer < pollIntervalSeconds)
                return;

            _pollTimer = 0f;
            AppleThermalLevel level = ReadNativeThermalLevel();
            if (level != CurrentThermalLevel)
            {
                ApplyThermalProfile(level, force: false);
            }
        }

        private AppleThermalLevel ReadNativeThermalLevel()
        {
#if UNITY_IOS && !UNITY_EDITOR
            int raw = Mathf.Clamp(_GetiOSThermalState(), 0, 3);
            return (AppleThermalLevel)raw;
#else
            return CurrentThermalLevel;
#endif
        }

        public void ApplyThermalProfile(AppleThermalLevel level, bool force = false)
        {
            if (!force && level == CurrentThermalLevel)
                return;

            CurrentThermalLevel = level;
            UniversalRenderPipelineAsset urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

            switch (level)
            {
                case AppleThermalLevel.Nominal:
                    Application.targetFrameRate = nominalTargetFps;
                    Shader.SetGlobalFloat(ThermalVfxMultiplierId, 1.0f);
                    if (urp != null)
                    {
                        urp.renderScale = nominalRenderScale;
                        urp.shadowDistance = nominalShadowDistance;
                    }
                    break;

                case AppleThermalLevel.Fair:
                    Application.targetFrameRate = nominalTargetFps;
                    Shader.SetGlobalFloat(ThermalVfxMultiplierId, 0.75f);
                    if (urp != null)
                    {
                        urp.renderScale = fairRenderScale;
                        urp.shadowDistance = fairShadowDistance;
                    }
                    break;

                case AppleThermalLevel.Serious:
                    Application.targetFrameRate = 45;
                    Shader.SetGlobalFloat(ThermalVfxMultiplierId, 0.45f);
                    if (urp != null)
                    {
                        urp.renderScale = seriousRenderScale;
                        urp.shadowDistance = seriousShadowDistance;
                    }
                    break;

                case AppleThermalLevel.Critical:
                    Application.targetFrameRate = 30;
                    Shader.SetGlobalFloat(ThermalVfxMultiplierId, 0.2f);
                    if (urp != null)
                    {
                        urp.renderScale = criticalRenderScale;
                        urp.shadowDistance = 0f;
                    }
                    break;
            }

            Debug.Log($"[iOSThermalManager] Applied Thermal Profile: {level} (TargetFPS={Application.targetFrameRate}, RenderScale={(urp != null ? urp.renderScale : 1f):0.00})");
        }
    }
}
