using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Environment;
using CoralArchipelago.Networking;

namespace CoralArchipelago.ClientIOS
{
    /// <summary>
    /// Live performance, thermal, tide, and PvE scaling diagnostics overlay.
    /// Includes host/editor simulator controls to verify 1-10 player scaling,
    /// night bioluminescence / tidal cave gates, and Apple thermal throttling on a single device.
    /// </summary>
    public class DiagnosticsAndTestOverlayUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject overlayRoot;
        [SerializeField] private Text diagnosticsText;
        [SerializeField] private Text toggleButtonLabel;
        [SerializeField] private float refreshIntervalSeconds = 0.35f;

        private float _fpsAccumulator;
        private int _fpsFrames;
        private float _currentFps;
        private float _refreshTimer;

        private void Awake()
        {
            if (NetworkBootstrap.IsRunningAsDedicatedServer())
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            _fpsAccumulator += Time.unscaledDeltaTime;
            _fpsFrames++;

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer < refreshIntervalSeconds)
                return;

            _currentFps = _fpsFrames / Mathf.Max(0.001f, _fpsAccumulator);
            _fpsAccumulator = 0f;
            _fpsFrames = 0;
            _refreshTimer = 0f;

            UpdateDiagnosticsLabel();
        }

        private void UpdateDiagnosticsLabel()
        {
            if (diagnosticsText == null)
                return;

            var thermalMgr = iOSGraphicsAndThermalManager.Instance;
            AppleThermalLevel thermal = thermalMgr != null ? thermalMgr.CurrentThermalLevel : AppleThermalLevel.Nominal;

            UniversalRenderPipelineAsset urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float renderScale = urp != null ? urp.renderScale : 1.0f;

            float hour = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.WorldTimeHours.Value : 8f;
            int day = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.CurrentDay.Value : 1;
            bool timePaused = WorldTimeManager.Instance != null && WorldTimeManager.Instance.IsPaused.Value;
            float glow = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.CurrentBioluminescenceIntensity : 0f;

            bool caveOpen = TidalCrystalCaveGate.PrimaryGate != null && TidalCrystalCaveGate.PrimaryGate.IsGateOpen;
            string caveStatus = TidalCrystalCaveGate.PrimaryGate != null
                ? (caveOpen ? "OPEN (Low Tide)" : "CLOSED (High Tide)")
                : "N/A";

            int players = PvEDirector.Instance != null ? PvEDirector.Instance.ActivePlayerCount.Value : 1;
            float hpMult = PvEDirector.Instance != null ? PvEDirector.Instance.CurrentHpMultiplier.Value : 1f;
            float dmgMult = PvEDirector.Instance != null ? PvEDirector.Instance.CurrentDamageMultiplier.Value : 1f;

            diagnosticsText.text =
                $"FPS: {_currentFps:0.0} | Thermal: {thermal} | RenderScale: {renderScale:0.00}\n" +
                $"World: Day {day} {hour:00.00}h {(timePaused ? "[FROZEN]" : "")} | BioGlow: {glow:0.00} | Cave: {caveStatus}\n" +
                $"PvE Scale ({players}/10): Monster HP x{hpMult:0.00} | DMG x{dmgMult:0.00}";
        }

        #region Simulator & Toggle Hooks (Host / Editor Testing)

        public void OnClickToggleOverlay()
        {
            if (overlayRoot == null)
                return;

            bool nextState = !overlayRoot.activeSelf;
            overlayRoot.SetActive(nextState);
            if (toggleButtonLabel != null)
            {
                toggleButtonLabel.text = nextState ? "Debug [-]" : "Debug [+]";
            }
        }

        public void OnClickSimulateAddPlayer()
        {
            int next = PvEDirector.Instance != null
                ? Mathf.Clamp(PvEDirector.Instance.ActivePlayerCount.Value + 1, 1, 10)
                : (global::IslandRelicAndVolcanoBossSystem.Instance != null
                    ? Mathf.Clamp(global::IslandRelicAndVolcanoBossSystem.Instance.currentOnlinePlayers + 1, 1, 10)
                    : 1);

            if (PvEDirector.Instance != null && PvEDirector.Instance.IsServer)
            {
                PvEDirector.Instance.RecalculateDifficultyServer(next);
            }

            global::IslandRelicAndVolcanoBossSystem.Instance?.RecalculateBossScaling(next);
        }

        public void OnClickSimulateRemovePlayer()
        {
            int next = PvEDirector.Instance != null
                ? Mathf.Clamp(PvEDirector.Instance.ActivePlayerCount.Value - 1, 1, 10)
                : (global::IslandRelicAndVolcanoBossSystem.Instance != null
                    ? Mathf.Clamp(global::IslandRelicAndVolcanoBossSystem.Instance.currentOnlinePlayers - 1, 1, 10)
                    : 1);

            if (PvEDirector.Instance != null && PvEDirector.Instance.IsServer)
            {
                PvEDirector.Instance.RecalculateDifficultyServer(next);
            }

            global::IslandRelicAndVolcanoBossSystem.Instance?.RecalculateBossScaling(next);
        }

        public void OnClickSkipThreeWorldHours()
        {
            if (WorldTimeManager.Instance == null || !WorldTimeManager.Instance.IsServer)
                return;

            WorldTimeManager.Instance.AdvanceHoursDebugServer(3.0f);
        }

        public void OnClickCycleSimulatedThermalLevel()
        {
            var thermalMgr = iOSGraphicsAndThermalManager.Instance;
            if (thermalMgr == null)
                return;

            int next = ((int)thermalMgr.CurrentThermalLevel + 1) % 4;
            thermalMgr.ApplyThermalProfile((AppleThermalLevel)next);
        }

        #endregion
    }
}
