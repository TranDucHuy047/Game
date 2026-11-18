using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoralArchipelago.Core
{
    /// <summary>
    /// Zero-manual-setup runtime bootstrapper that automatically attaches
    /// IslandGraphicsUpgrader (2000m x 2000m 16x16 chunks), IslandFastTravelAndRaftSystem,
    /// and IslandRelicAndVolcanoBossSystem whenever a gameplay world scene finishes loading.
    /// </summary>
    public static class CoralContinentSystemsBootstrapper
    {
        private static bool _hooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeOnSceneLoad()
        {
            if (!_hooked)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _hooked = true;
            }

            EnsureContinentSystemsPresent(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureContinentSystemsPresent(scene);
        }

        private static void EnsureContinentSystemsPresent(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            // Do not spawn 2000m world chunks or portals in the login/lobby Bootstrap scene
            if (scene.name.Contains("Bootstrap") && WorldTimeManager.Instance == null && GameObject.Find("WorldManagers") == null)
                return;

            global::IslandGraphicsUpgrader upgrader = Object.FindFirstObjectByType<global::IslandGraphicsUpgrader>();
            GameObject hostGo = upgrader != null ? upgrader.gameObject : GameObject.Find("IslandWorldUpgrader_2000m");

            if (hostGo == null)
            {
                // Only auto-create if this is genuinely a world scene
                if (WorldTimeManager.Instance == null && GameObject.Find("WorldManagers") == null && GameObject.Find("OceanSurface") == null)
                    return;

                hostGo = new GameObject("IslandWorldUpgrader_2000m");
            }

            if (hostGo.GetComponent<global::IslandGraphicsUpgrader>() == null)
                hostGo.AddComponent<global::IslandGraphicsUpgrader>();

            if (hostGo.GetComponent<global::IslandFastTravelAndRaftSystem>() == null)
                hostGo.AddComponent<global::IslandFastTravelAndRaftSystem>();

            if (hostGo.GetComponent<global::IslandRelicAndVolcanoBossSystem>() == null)
                hostGo.AddComponent<global::IslandRelicAndVolcanoBossSystem>();
        }
    }
}
