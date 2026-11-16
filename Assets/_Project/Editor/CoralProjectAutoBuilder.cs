#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Building;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Crafting;
using CoralArchipelago.Gameplay.Environment;
using CoralArchipelago.Gameplay.Harvesting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Onboarding;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;
using CoralArchipelago.Persistence;
using CoralArchipelago.UI;

namespace CoralArchipelago.EditorTools
{
    /// <summary>
    /// One-Click Unity Project Generator for Coral Archipelago.
    /// Creates URP Bioluminescent Materials, all Network Prefabs, UI Prefabs,
    /// 00_Bootstrap.unity, 01_CoralWorld.unity, and registers them in Build Settings.
    /// </summary>
    public static class CoralProjectAutoBuilder
    {
        private const string MaterialsDir = "Assets/_Project/Materials";
        private const string NetworkPrefabsDir = "Assets/_Project/Prefabs/Network";
        private const string UiPrefabsDir = "Assets/_Project/Prefabs/UI";
        private const string ConfigsDir = "Assets/_Project/Configs";
        private const string ScenesDir = "Assets/_Project/Scenes";

        private static Font _cachedFont;

        [InitializeOnLoadMethod]
        private static void AutoGenerateInBatchModeIfNeeded()
        {
            if (Application.isBatchMode && (EditorBuildSettings.scenes == null || EditorBuildSettings.scenes.Length == 0))
            {
                GenerateFullProjectSetupInternal(openBootstrapScene: false);
            }
        }

        [MenuItem("Coral Archipelago/One-Click Generate Scenes & Prefabs")]
        public static void GenerateFullProjectSetup()
        {
            GenerateFullProjectSetupInternal(openBootstrapScene: true);
        }

        /// <summary>
        /// CLI / GitHub Actions entry point to generate project assets and build an iOS Xcode project.
        /// </summary>
        public static void BuildiOSXcodeFromCLI()
        {
            GenerateFullProjectSetupInternal(openBootstrapScene: false);
            iOSBuildConfigurator.ApplyRecommendediOSPlayerSettings();

            string outputPath = ParseCliArg("-customBuildPath", "Build/iOS_Xcode");
            string[] scenePaths = GetEnabledScenePaths();

            var options = new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = outputPath,
                target = BuildTarget.iOS,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[CoralProjectAutoBuilder] iOS Xcode build failed: {report.summary.result}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[CoralProjectAutoBuilder] iOS Xcode project exported to: {outputPath}");
        }

        /// <summary>
        /// CLI entry point to build the 24/7 Headless Linux Dedicated Server for Docker VPS.
        /// </summary>
        public static void BuildLinuxServerFromCLI()
        {
            GenerateFullProjectSetupInternal(openBootstrapScene: false);

            string outputPath = ParseCliArg("-customBuildPath", "Build/LinuxServer/CoralServer.x86_64");
            string[] scenePaths = GetEnabledScenePaths();

            var options = new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[CoralProjectAutoBuilder] Linux Dedicated Server build failed: {report.summary.result}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[CoralProjectAutoBuilder] Linux Dedicated Server exported to: {outputPath}");
        }

        private static void GenerateFullProjectSetupInternal(bool openBootstrapScene)
        {
            EnsureDirectories();
            EnsureDefaultUrpPipelineAsset();

            Material coralMat = CreateOrLoadCoralMaterial("MAT_CoralBiolum.mat", new Color(0.85f, 0.35f, 0.45f), new Color(0.15f, 1.8f, 2.4f));
            Material crystalMat = CreateOrLoadCoralMaterial("MAT_GlowCrystal.mat", new Color(0.25f, 0.85f, 0.95f), new Color(0.25f, 2.2f, 2.8f));
            Material oceanMat = CreateOrLoadOceanMaterial("MAT_CoralOcean.mat");
            Material sandMat = CreateOrLoadLitMaterial("MAT_IslandSand.mat", new Color(0.82f, 0.76f, 0.60f));
            Material woodMat = CreateOrLoadLitMaterial("MAT_CoralWood.mat", new Color(0.55f, 0.30f, 0.24f));
            Material crabMat = CreateOrLoadCoralMaterial("MAT_ReefCrab.mat", new Color(0.75f, 0.22f, 0.22f), new Color(1.8f, 0.35f, 0.20f));

            // Network Prefabs
            GameObject playerPrefab = CreatePlayerPrefab(coralMat);
            GameObject floorPrefab = CreateSimpleStructurePrefab("PF_CoralFloor", PrimitiveType.Cube, new Vector3(2.2f, 0.25f, 2.2f), woodMat);
            GameObject lampPrefab = CreateSimpleStructurePrefab("PF_GlowLamp", PrimitiveType.Cylinder, new Vector3(0.45f, 1.3f, 0.45f), crystalMat);
            GameObject chestPrefab = CreateSharedChestPrefab(woodMat, crystalMat);
            GameObject woodNodePrefab = CreateResourceNodePrefab("PF_CoralWoodNode", "coral_wood", PrimitiveType.Cylinder, new Vector3(0.75f, 1.8f, 0.75f), coralMat);
            GameObject crystalNodePrefab = CreateResourceNodePrefab("PF_GlowCrystalNode", "glow_crystal", PrimitiveType.Cube, new Vector3(0.95f, 1.4f, 0.95f), crystalMat);
            GameObject monsterPrefab = CreateMonsterPrefab(crabMat);

            NetworkPrefabsList netPrefabsList = CreateOrUpdateNetworkPrefabsList(
                playerPrefab, floorPrefab, lampPrefab, chestPrefab, woodNodePrefab, crystalNodePrefab, monsterPrefab);

            // UI Prefabs
            InventorySlotButtonUI slotBtnPrefab = CreateInventorySlotButtonPrefab();
            Button recipeBtnPrefab = CreateCraftRecipeButtonPrefab();

            // Build Scenes
            string bootstrapPath = BuildBootstrapScene(playerPrefab, netPrefabsList);
            string worldPath = BuildCoralWorldScene(
                sandMat, oceanMat, coralMat, crystalMat,
                floorPrefab, lampPrefab, chestPrefab,
                woodNodePrefab, crystalNodePrefab, monsterPrefab,
                slotBtnPrefab, recipeBtnPrefab);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(bootstrapPath, true),
                new EditorBuildSettingsScene(worldPath, true)
            };

            iOSBuildConfigurator.ApplyRecommendediOSPlayerSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (openBootstrapScene)
            {
                EditorSceneManager.OpenScene(bootstrapPath);
            }

            Debug.Log("[CoralProjectAutoBuilder] Successfully generated URP Asset, Materials, Prefabs, 00_Bootstrap.unity, and 01_CoralWorld.unity!");
        }

        private static void EnsureDefaultUrpPipelineAsset()
        {
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset)
                return;

            string rendererPath = $"{ConfigsDir}/URP-Mobile-Renderer.asset";
            UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, rendererPath);
            }

            string urpPath = $"{ConfigsDir}/URP-Mobile-High.asset";
            UniversalRenderPipelineAsset urpAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(urpPath);
            if (urpAsset == null)
            {
                urpAsset = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(urpAsset, urpPath);
            }

            urpAsset.renderScale = 0.75f;
            urpAsset.supportsCameraDepthTexture = true;
            urpAsset.supportsHDR = true;
            urpAsset.shadowDistance = 55f;
            urpAsset.msaaSampleCount = 2;
            EditorUtility.SetDirty(urpAsset);

            GraphicsSettings.defaultRenderPipeline = urpAsset;
            QualitySettings.renderPipeline = urpAsset;
        }

        private static string[] GetEnabledScenePaths()
        {
            var list = new List<string>();
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (s != null && s.enabled && !string.IsNullOrEmpty(s.path))
                    list.Add(s.path);
            }
            return list.ToArray();
        }

        private static string ParseCliArg(string flag, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return fallback;
        }

        private static void EnsureDirectories()
        {
            string[] dirs = { MaterialsDir, NetworkPrefabsDir, UiPrefabsDir, ConfigsDir, ScenesDir };
            for (int i = 0; i < dirs.Length; i++)
            {
                if (!Directory.Exists(dirs[i]))
                    Directory.CreateDirectory(dirs[i]);
            }
        }

        private static Font GetUiFont()
        {
            if (_cachedFont != null)
                return _cachedFont;

            _cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_cachedFont == null)
                _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _cachedFont;
        }

        #region Materials

        private static Material CreateOrLoadCoralMaterial(string fileName, Color baseCol, Color glowCol)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("CoralArchipelago/URP/CoralBioluminescentLit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseCol);
            if (mat.HasProperty("_GlowColor")) mat.SetColor("_GlowColor", glowCol);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadOceanMaterial(string fileName)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("CoralArchipelago/URP/CoralOceanWaterURP");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadLitMaterial(string fileName, Color color)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        #endregion

        #region Network & UI Prefabs

        private static GameObject CreatePlayerPrefab(Material visualMat)
        {
            string path = $"{NetworkPrefabsDir}/PlayerAvatar.prefab";
            var root = new GameObject("PlayerAvatar");

            var cc = root.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, 1.0f, 0f);
            cc.height = 2.0f;
            cc.radius = 0.42f;

            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();
            root.AddComponent<PlayerInventory>();
            root.AddComponent<PlayerOnboardingController>();
            root.AddComponent<NetworkPlayerController>();

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.name = "BodyVisual";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            body.GetComponent<Renderer>().sharedMaterial = visualMat;

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateSimpleStructurePrefab(string name, PrimitiveType primitive, Vector3 scale, Material mat)
        {
            string path = $"{NetworkPrefabsDir}/{name}.prefab";
            GameObject root = GameObject.CreatePrimitive(primitive);
            root.name = name;
            root.transform.localScale = scale;
            root.GetComponent<Renderer>().sharedMaterial = mat;
            root.AddComponent<NetworkObject>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateSharedChestPrefab(Material woodMat, Material trimMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_SharedChest.prefab";
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = "PF_SharedChest";
            root.transform.localScale = new Vector3(1.2f, 0.85f, 0.85f);
            root.GetComponent<Renderer>().sharedMaterial = woodMat;

            GameObject glowStrip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(glowStrip.GetComponent<Collider>());
            glowStrip.name = "BiolumTrim";
            glowStrip.transform.SetParent(root.transform, false);
            glowStrip.transform.localPosition = new Vector3(0f, 0.48f, 0f);
            glowStrip.transform.localScale = new Vector3(1.04f, 0.14f, 1.04f);
            glowStrip.GetComponent<Renderer>().sharedMaterial = trimMat;

            root.AddComponent<NetworkObject>();
            root.AddComponent<SharedChestInventory>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateResourceNodePrefab(
            string name,
            string itemId,
            PrimitiveType primitive,
            Vector3 scale,
            Material mat)
        {
            string path = $"{NetworkPrefabsDir}/{name}.prefab";
            var root = new GameObject(name);
            root.AddComponent<NetworkObject>();

            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, scale.y * 0.5f, 0f);
            col.size = scale;

            GameObject visual = GameObject.CreatePrimitive(primitive);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.name = "Visual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = new Vector3(0f, scale.y * 0.5f, 0f);
            visual.transform.localScale = scale;
            visual.GetComponent<Renderer>().sharedMaterial = mat;

            var node = root.AddComponent<HarvestableResourceNode>();
            var so = new SerializedObject(node);
            so.FindProperty("resourceItemId").stringValue = itemId;
            so.FindProperty("visualRoot").objectReferenceValue = visual;
            so.FindProperty("nodeCollider").objectReferenceValue = col;
            so.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateMonsterPrefab(Material crabMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_ReefCrabMonster.prefab";
            var root = new GameObject("PF_ReefCrabMonster");
            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();

            SphereCollider col = root.AddComponent<SphereCollider>();
            col.center = new Vector3(0f, 0.55f, 0f);
            col.radius = 0.75f;

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.6f;
            agent.height = 1.1f;

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.name = "CrabBody";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            visual.transform.localScale = new Vector3(1.3f, 0.75f, 1.1f);
            visual.GetComponent<Renderer>().sharedMaterial = crabMat;

            var ai = root.AddComponent<ScalableMonsterAI>();
            var so = new SerializedObject(ai);
            so.FindProperty("visualRoot").objectReferenceValue = visual;
            so.FindProperty("monsterCollider").objectReferenceValue = col;
            so.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static NetworkPrefabsList CreateOrUpdateNetworkPrefabsList(params GameObject[] prefabs)
        {
            string path = $"{ConfigsDir}/DefaultNetworkPrefabs.asset";
            NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(path);
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                AssetDatabase.CreateAsset(list, path);
            }

            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] != null && !list.Contains(prefabs[i]))
                {
                    list.Add(new NetworkPrefab { Prefab = prefabs[i] });
                }
            }

            EditorUtility.SetDirty(list);
            return list;
        }

        private static InventorySlotButtonUI CreateInventorySlotButtonPrefab()
        {
            string path = $"{UiPrefabsDir}/PF_InventorySlotButton.prefab";
            var root = new GameObject("PF_InventorySlotButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(InventorySlotButtonUI));
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(100f, 100f);

            Text label = CreateUiText("ItemLabelText", root.transform, "", 15, TextAnchor.MiddleCenter, Color.white);
            StretchRect(label.rectTransform, 4f, 22f, 4f, 4f);

            Text amount = CreateUiText("AmountText", root.transform, "", 16, TextAnchor.LowerRight, new Color(1f, 0.95f, 0.55f));
            StretchRect(amount.rectTransform, 4f, 4f, 6f, 4f);

            var comp = root.GetComponent<InventorySlotButtonUI>();
            var so = new SerializedObject(comp);
            so.FindProperty("slotButton").objectReferenceValue = root.GetComponent<Button>();
            so.FindProperty("slotBackground").objectReferenceValue = root.GetComponent<Image>();
            so.FindProperty("itemLabelText").objectReferenceValue = label;
            so.FindProperty("amountText").objectReferenceValue = amount;
            so.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved.GetComponent<InventorySlotButtonUI>();
        }

        private static Button CreateCraftRecipeButtonPrefab()
        {
            string path = $"{UiPrefabsDir}/PF_CraftRecipeButton.prefab";
            var root = new GameObject("PF_CraftRecipeButton", typeof(RectTransform), typeof(Image), typeof(Button));
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(460f, 54f);
            root.GetComponent<Image>().color = new Color(0.14f, 0.28f, 0.38f, 0.95f);

            Text label = CreateUiText("LabelText", root.transform, "Recipe", 18, TextAnchor.MiddleLeft, Color.white);
            StretchRect(label.rectTransform, 14f, 4f, 14f, 4f);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved.GetComponent<Button>();
        }

        #endregion

        #region Scenes

        private static string BuildBootstrapScene(GameObject playerPrefab, NetworkPrefabsList netPrefabsList)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.GetComponent<Camera>().backgroundColor = new Color(0.03f, 0.08f, 0.16f);
            camGo.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;

            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            var utp = nmGo.AddComponent<UnityTransport>();
            if (nm.NetworkConfig == null)
                nm.NetworkConfig = new NetworkConfig();

            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.ConnectionApproval = true;
            nm.NetworkConfig.EnableSceneManagement = true;
            nm.NetworkConfig.PlayerPrefab = playerPrefab;
            nm.NetworkConfig.TickRate = 30;
            foreach (NetworkPrefab np in netPrefabsList.PrefabList)
            {
                nm.NetworkConfig.Prefabs.Add(np);
            }
            EditorUtility.SetDirty(nm);

            var serverMgrs = new GameObject("ServerManagers");
            serverMgrs.AddComponent<SplitSaveManager>();
            serverMgrs.AddComponent<ServerDormantController>();
            serverMgrs.AddComponent<NetworkBootstrap>();

            var clientMgrs = new GameObject("ClientManagers");
            clientMgrs.AddComponent<iOSGraphicsAndThermalManager>();
            clientMgrs.AddComponent<CombatFeedbackManager>();

            CreateEventSystemIfMissing();

            // Lobby Canvas
            Canvas lobbyCanvas = CreateScreenCanvas("ClientLobbyCanvas");
            var touchUi = lobbyCanvas.gameObject.AddComponent<MobileTouchInputUI>();

            GameObject panel = CreatePanel("LobbyPanel", lobbyCanvas.transform, new Color(0.05f, 0.12f, 0.22f, 0.88f));
            RectTransform panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(560f, 380f);

            Text title = CreateUiText("Title", panel.transform, "CORAL ARCHIPELAGO (Async PvE)", 26, TextAnchor.MiddleCenter, new Color(0.35f, 0.95f, 1f));
            SetRect(title.rectTransform, 0f, 130f, 500f, 45f);

            InputField ipInput = CreateInputField("ServerIpInput", panel.transform, "127.0.0.1", "VPS IP Address...");
            SetRect(ipInput.GetComponent<RectTransform>(), 0f, 65f, 420f, 44f);

            InputField pinInput = CreateInputField("PasscodeInput", panel.transform, "1234", "Server Passcode...");
            SetRect(pinInput.GetComponent<RectTransform>(), 0f, 10f, 420f, 44f);

            Button connectBtn = CreateButton("ConnectButton", panel.transform, "Connect to VPS", new Color(0.12f, 0.55f, 0.68f), touchUi.OnClickConnectServer);
            SetRect(connectBtn.GetComponent<RectTransform>(), 0f, -55f, 360f, 52f);

            Button hostBtn = CreateButton("HostTestButton", panel.transform, "Start Local Host (Test)", new Color(0.18f, 0.62f, 0.42f), touchUi.OnClickStartHostTest);
            SetRect(hostBtn.GetComponent<RectTransform>(), 0f, -120f, 360f, 52f);

            var touchSo = new SerializedObject(touchUi);
            touchSo.FindProperty("serverIpInput").objectReferenceValue = ipInput;
            touchSo.FindProperty("passcodeInput").objectReferenceValue = pinInput;
            touchSo.ApplyModifiedPropertiesWithoutUndo();

            string scenePath = $"{ScenesDir}/00_Bootstrap.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            return scenePath;
        }

        private static string BuildCoralWorldScene(
            Material sandMat,
            Material oceanMat,
            Material coralMat,
            Material crystalMat,
            GameObject floorPrefab,
            GameObject lampPrefab,
            GameObject chestPrefab,
            GameObject woodNodePrefab,
            GameObject crystalNodePrefab,
            GameObject monsterPrefab,
            InventorySlotButtonUI slotBtnPrefab,
            Button recipeBtnPrefab)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera & Sun Light
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(0f, 6f, -9f);

            var lightGo = new GameObject("Directional Light", typeof(Light));
            Light sun = lightGo.GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(48f, 170f, 0f);

            // Ocean & Islands
            GameObject ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Object.DestroyImmediate(ocean.GetComponent<Collider>());
            ocean.name = "OceanSurface";
            ocean.transform.position = Vector3.zero;
            ocean.transform.localScale = new Vector3(30f, 1f, 30f);
            ocean.GetComponent<Renderer>().sharedMaterial = oceanMat;

            CreateIslandPlatform("MainCoralIsland", new Vector3(0f, 0.5f, 0f), new Vector3(34f, 2.2f, 34f), sandMat);
            CreateIslandPlatform("CrystalReefIsland", new Vector3(28f, 0.3f, 18f), new Vector3(18f, 2.0f, 18f), sandMat);

            // Tidal Crystal Cave Gate
            GameObject caveGateGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            caveGateGo.name = "TidalCrystalCaveGate";
            caveGateGo.transform.position = new Vector3(14f, 1.2f, 9f);
            caveGateGo.transform.localScale = new Vector3(4.5f, 4.0f, 1.5f);
            caveGateGo.GetComponent<Renderer>().sharedMaterial = crystalMat;
            var gateComp = caveGateGo.AddComponent<TidalCrystalCaveGate>();
            var gateSo = new SerializedObject(gateComp);
            gateSo.FindProperty("gateVisualTransform").objectReferenceValue = caveGateGo.transform;
            gateSo.FindProperty("gateCollider").objectReferenceValue = caveGateGo.GetComponent<Collider>();
            gateSo.ApplyModifiedPropertiesWithoutUndo();

            // WorldManagers (Networked)
            var worldMgrs = new GameObject("WorldManagers");
            worldMgrs.AddComponent<NetworkObject>();
            var timeMgr = worldMgrs.AddComponent<WorldTimeManager>();
            var timeSo = new SerializedObject(timeMgr);
            timeSo.FindProperty("directionalSunLight").objectReferenceValue = sun;
            timeSo.ApplyModifiedPropertiesWithoutUndo();

            worldMgrs.AddComponent<PvEDirector>();
            var buildMgr = worldMgrs.AddComponent<NetworkBuildingManager>();
            WireBuildingCatalog(buildMgr, floorPrefab, lampPrefab, chestPrefab);
            worldMgrs.AddComponent<NetworkCraftingManager>();
            worldMgrs.AddComponent<SharedCampLogManager>();

            // Island Core Altar (Camp Center)
            GameObject altarGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            altarGo.name = "IslandCoreAltar";
            altarGo.transform.position = new Vector3(-4f, 2.3f, 4f);
            altarGo.transform.localScale = new Vector3(2.2f, 1.4f, 2.2f);
            altarGo.GetComponent<Renderer>().sharedMaterial = crystalMat;
            altarGo.AddComponent<NetworkObject>();
            altarGo.AddComponent<IslandCoreAltar>();

            // Starter Camp Shared Chest
            GameObject starterChest = (GameObject)PrefabUtility.InstantiatePrefab(chestPrefab);
            starterChest.transform.position = new Vector3(-1.5f, 2.02f, 4.5f);
            var chestSo = new SerializedObject(starterChest.GetComponent<SharedChestInventory>());
            chestSo.FindProperty("sceneFallbackChestGuid").stringValue = "starter_camp_chest_01";
            chestSo.ApplyModifiedPropertiesWithoutUndo();

            // Resource Nodes & Monsters
            Vector3[] woodPositions =
            {
                new Vector3(5f, 1.6f, 4f), new Vector3(-8f, 1.6f, -3f),
                new Vector3(7f, 1.6f, -7f), new Vector3(-10f, 1.6f, 8f),
                new Vector3(3f, 1.6f, 11f)
            };
            for (int i = 0; i < woodPositions.Length; i++)
            {
                GameObject n = (GameObject)PrefabUtility.InstantiatePrefab(woodNodePrefab);
                n.transform.position = woodPositions[i];
            }

            Vector3[] crystalPositions =
            {
                new Vector3(11f, 1.6f, -2f), new Vector3(-4f, 1.6f, -11f),
                new Vector3(26f, 1.3f, 16f), new Vector3(30f, 1.3f, 20f)
            };
            for (int i = 0; i < crystalPositions.Length; i++)
            {
                GameObject n = (GameObject)PrefabUtility.InstantiatePrefab(crystalNodePrefab);
                n.transform.position = crystalPositions[i];
            }

            Vector3[] monsterPositions =
            {
                new Vector3(10f, 1.6f, 10f),
                new Vector3(-11f, 1.6f, -10f),
                new Vector3(27f, 1.3f, 19f)
            };
            for (int i = 0; i < monsterPositions.Length; i++)
            {
                GameObject m = (GameObject)PrefabUtility.InstantiatePrefab(monsterPrefab);
                m.transform.position = monsterPositions[i];
            }

            // Environment Controller
            var envGo = new GameObject("EnvironmentVisuals");
            var envCtrl = envGo.AddComponent<BioluminescenceEnvironmentController>();
            var envSo = new SerializedObject(envCtrl);
            envSo.FindProperty("oceanSurfaceTransform").objectReferenceValue = ocean.transform;
            envSo.ApplyModifiedPropertiesWithoutUndo();

            // 3D Waypoint Arrow
            GameObject arrowGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(arrowGo.GetComponent<Collider>());
            arrowGo.name = "WaypointArrow3D";
            arrowGo.transform.localScale = new Vector3(0.22f, 0.14f, 0.75f);
            arrowGo.GetComponent<Renderer>().sharedMaterial = coralMat;

            CreateEventSystemIfMissing();
            BuildMobileHudCanvas(arrowGo.transform, slotBtnPrefab, recipeBtnPrefab);

            string scenePath = $"{ScenesDir}/01_CoralWorld.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            return scenePath;
        }

        private static void CreateIslandPlatform(string name, Vector3 pos, Vector3 scale, Material mat)
        {
            GameObject island = GameObject.CreatePrimitive(PrimitiveType.Cube);
            island.name = name;
            island.transform.position = pos;
            island.transform.localScale = scale;
            island.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void WireBuildingCatalog(
            NetworkBuildingManager mgr,
            GameObject floorPrefab,
            GameObject lampPrefab,
            GameObject chestPrefab)
        {
            var so = new SerializedObject(mgr);
            SerializedProperty listProp = so.FindProperty("buildables");
            listProp.ClearArray();

            AddBuildableEntry(listProp, 0, "coral_floor", floorPrefab, "coral_wood", 2, 100f);
            AddBuildableEntry(listProp, 1, "glow_lamp", lampPrefab, "glow_crystal", 2, 80f);
            AddBuildableEntry(listProp, 2, "shared_chest", chestPrefab, "coral_wood", 4, 150f);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddBuildableEntry(
            SerializedProperty listProp,
            int index,
            string prefabId,
            GameObject prefab,
            string costId,
            int costAmt,
            float hp)
        {
            listProp.InsertArrayElementAtIndex(index);
            SerializedProperty elem = listProp.GetArrayElementAtIndex(index);
            elem.FindPropertyRelative("PrefabId").stringValue = prefabId;
            elem.FindPropertyRelative("Prefab").objectReferenceValue = prefab;
            elem.FindPropertyRelative("CostItemId").stringValue = costId;
            elem.FindPropertyRelative("CostAmount").intValue = costAmt;
            elem.FindPropertyRelative("MaxHP").floatValue = hp;
        }

        private static void BuildMobileHudCanvas(
            Transform waypointArrow,
            InventorySlotButtonUI slotBtnPrefab,
            Button recipeBtnPrefab)
        {
            Canvas hud = CreateScreenCanvas("MobileHUD");
            var touchUi = hud.gameObject.AddComponent<MobileTouchInputUI>();
            var invGridUi = hud.gameObject.AddComponent<InventoryGridUI>();
            var craftUi = hud.gameObject.AddComponent<CraftingPanelUI>();
            var tutorialUi = hud.gameObject.AddComponent<OnboardingTutorialUI>();
            var diagUi = hud.gameObject.AddComponent<DiagnosticsAndTestOverlayUI>();

            // Top-center status bar + Camp log
            Text statusText = CreateUiText("HudStatusText", hud.transform, "Connecting...", 18, TextAnchor.UpperCenter, Color.white);
            SetAnchorTopCenter(statusText.rectTransform, 0f, -14f, 820f, 52f);

            Text campLogText = CreateUiText("CampLogText", hud.transform, "Camp Log...", 14, TextAnchor.UpperLeft, new Color(0.75f, 0.94f, 1f));
            SetAnchorTopLeft(campLogText.rectTransform, 18f, -185f, 440f, 95f);

            // Virtual Joystick (Bottom-Left)
            GameObject joyBg = CreatePanel("JoystickBg", hud.transform, new Color(1f, 1f, 1f, 0.18f));
            RectTransform joyBgRt = joyBg.GetComponent<RectTransform>();
            joyBgRt.anchorMin = joyBgRt.anchorMax = Vector2.zero;
            joyBgRt.anchoredPosition = new Vector2(140f, 140f);
            joyBgRt.sizeDelta = new Vector2(160f, 160f);

            GameObject joyHandle = CreatePanel("JoystickHandle", joyBg.transform, new Color(0.3f, 0.9f, 1f, 0.65f));
            joyHandle.GetComponent<RectTransform>().sizeDelta = new Vector2(72f, 72f);

            // Action Buttons (Right side)
            CreateBottomRightBtn("BtnAttack", hud.transform, "Attack/Harvest", -110f, 100f, new Vector2(170f, 64f), new Color(0.82f, 0.34f, 0.28f), touchUi.OnClickAttackOrHarvest);
            CreateBottomRightBtn("BtnSprint", hud.transform, "Sprint", -110f, 178f, new Vector2(150f, 46f), new Color(0.25f, 0.55f, 0.72f), touchUi.OnClickToggleSprint);
            CreateBottomRightBtn("BtnBag", hud.transform, "Bag / Chest (I)", -300f, 100f, new Vector2(165f, 54f), new Color(0.22f, 0.48f, 0.65f), touchUi.OnClickToggleInventoryModal);
            CreateBottomRightBtn("BtnCraft", hud.transform, "Craft (C)", -300f, 165f, new Vector2(165f, 54f), new Color(0.24f, 0.60f, 0.46f), touchUi.OnClickToggleCraftingModal);
            CreateBottomRightBtn("BtnTonic", hud.transform, "Use Tonic", -300f, 230f, new Vector2(165f, 46f), new Color(0.18f, 0.68f, 0.52f), touchUi.OnClickUseCoralTonic);
            CreateBottomRightBtn("BtnBuildFloor", hud.transform, "Build Floor", -480f, 100f, new Vector2(150f, 46f), new Color(0.45f, 0.38f, 0.30f), touchUi.OnClickBuildFloor);
            CreateBottomRightBtn("BtnBuildLamp", hud.transform, "Build Lamp", -480f, 154f, new Vector2(150f, 46f), new Color(0.18f, 0.65f, 0.78f), touchUi.OnClickBuildGlowLamp);
            CreateBottomRightBtn("BtnBuildChest", hud.transform, "Build Chest", -480f, 208f, new Vector2(150f, 46f), new Color(0.55f, 0.40f, 0.25f), touchUi.OnClickBuildSharedChest);
            CreateBottomRightBtn("BtnDemolish", hud.transform, "Demolish", -480f, 262f, new Vector2(150f, 46f), new Color(0.60f, 0.26f, 0.26f), touchUi.OnClickDemolishNearestStructure);
            CreateBottomRightBtn("BtnAltar", hud.transform, "Upgrade Altar", -110f, 240f, new Vector2(170f, 48f), new Color(0.32f, 0.42f, 0.80f), touchUi.OnClickUpgradeIslandAltar);

            // Camp Note Input (Bottom Center)
            InputField noteInput = CreateInputField("CampNoteInput", hud.transform, "", "Leave note for camp...");
            RectTransform noteRt = noteInput.GetComponent<RectTransform>();
            noteRt.anchorMin = noteRt.anchorMax = new Vector2(0.5f, 0f);
            noteRt.anchoredPosition = new Vector2(-60f, 38f);
            noteRt.sizeDelta = new Vector2(300f, 42f);

            Button postNoteBtn = CreateButton("BtnPostNote", hud.transform, "Post Note", new Color(0.22f, 0.52f, 0.62f), touchUi.OnClickPostCampNote);
            RectTransform postRt = postNoteBtn.GetComponent<RectTransform>();
            postRt.anchorMin = postRt.anchorMax = new Vector2(0.5f, 0f);
            postRt.anchoredPosition = new Vector2(160f, 38f);
            postRt.sizeDelta = new Vector2(120f, 42f);

            var touchSo = new SerializedObject(touchUi);
            touchSo.FindProperty("joystickBackground").objectReferenceValue = joyBgRt;
            touchSo.FindProperty("joystickHandle").objectReferenceValue = joyHandle.GetComponent<RectTransform>();
            touchSo.FindProperty("hudStatusText").objectReferenceValue = statusText;
            touchSo.FindProperty("campLogText").objectReferenceValue = campLogText;
            touchSo.FindProperty("campNoteInput").objectReferenceValue = noteInput;
            touchSo.ApplyModifiedPropertiesWithoutUndo();

            // Onboarding Tutorial Panel (Top-Left)
            GameObject tutPanel = CreatePanel("TutorialPanelRoot", hud.transform, new Color(0.05f, 0.14f, 0.24f, 0.85f));
            RectTransform tutRt = tutPanel.GetComponent<RectTransform>();
            SetAnchorTopLeft(tutRt, 18f, -18f, 440f, 155f);

            Text tutTitle = CreateUiText("StepTitleText", tutPanel.transform, "Tutorial", 18, TextAnchor.UpperLeft, new Color(0.4f, 0.95f, 1f));
            SetRect(tutTitle.rectTransform, -45f, 48f, 320f, 32f);
            Text tutDesc = CreateUiText("StepDescriptionText", tutPanel.transform, "...", 15, TextAnchor.UpperLeft, Color.white);
            SetRect(tutDesc.rectTransform, 0f, -2f, 410f, 65f);
            Text tutProg = CreateUiText("StepProgressText", tutPanel.transform, "0/1", 15, TextAnchor.LowerLeft, new Color(1f, 0.9f, 0.45f));
            SetRect(tutProg.rectTransform, -90f, -52f, 220f, 28f);
            Button skipBtn = CreateButton("BtnSkipTutorial", tutPanel.transform, "Skip", new Color(0.45f, 0.25f, 0.25f), tutorialUi.OnClickSkipTutorial);
            SetRect(skipBtn.GetComponent<RectTransform>(), 165f, 52f, 80f, 32f);

            var tutSo = new SerializedObject(tutorialUi);
            tutSo.FindProperty("tutorialPanelRoot").objectReferenceValue = tutPanel;
            tutSo.FindProperty("stepTitleText").objectReferenceValue = tutTitle;
            tutSo.FindProperty("stepDescriptionText").objectReferenceValue = tutDesc;
            tutSo.FindProperty("stepProgressText").objectReferenceValue = tutProg;
            tutSo.FindProperty("worldWaypointArrow").objectReferenceValue = waypointArrow;
            tutSo.ApplyModifiedPropertiesWithoutUndo();

            // Diagnostics & Simulator Overlay (Top-Right)
            GameObject diagRoot = CreatePanel("DiagnosticsOverlayRoot", hud.transform, new Color(0.04f, 0.08f, 0.14f, 0.78f));
            SetAnchorTopRight(diagRoot.GetComponent<RectTransform>(), -18f, -18f, 480f, 145f);
            Text diagText = CreateUiText("DiagnosticsText", diagRoot.transform, "FPS: 60", 14, TextAnchor.UpperLeft, new Color(0.8f, 1f, 0.85f));
            SetRect(diagText.rectTransform, 0f, 24f, 455f, 75f);

            Button simAdd = CreateButton("BtnSimAddP", diagRoot.transform, "+1 Player", new Color(0.2f, 0.45f, 0.55f), diagUi.OnClickSimulateAddPlayer);
            SetRect(simAdd.GetComponent<RectTransform>(), -170f, -45f, 105f, 36f);
            Button simSub = CreateButton("BtnSimSubP", diagRoot.GetComponent<RectTransform>(), "-1 Player", new Color(0.2f, 0.45f, 0.55f), diagUi.OnClickSimulateRemovePlayer);
            SetRect(simSub.GetComponent<RectTransform>(), -58f, -45f, 105f, 36f);
            Button simSkip = CreateButton("BtnSimTime", diagRoot.transform, "Skip +3h", new Color(0.45f, 0.38f, 0.18f), diagUi.OnClickSkipThreeWorldHours);
            SetRect(simSkip.GetComponent<RectTransform>(), 56f, -45f, 105f, 36f);
            Button simHeat = CreateButton("BtnSimHeat", diagRoot.transform, "Cycle Heat", new Color(0.58f, 0.25f, 0.22f), diagUi.OnClickCycleSimulatedThermalLevel);
            SetRect(simHeat.GetComponent<RectTransform>(), 168f, -45f, 105f, 36f);

            var diagSo = new SerializedObject(diagUi);
            diagSo.FindProperty("overlayRoot").objectReferenceValue = diagRoot;
            diagSo.FindProperty("diagnosticsText").objectReferenceValue = diagText;
            diagSo.ApplyModifiedPropertiesWithoutUndo();

            // Inventory & Shared Chest Modal
            BuildInventoryModal(hud.transform, invGridUi, slotBtnPrefab);

            // Crafting Modal
            BuildCraftingModal(hud.transform, craftUi, recipeBtnPrefab);
        }

        private static void BuildInventoryModal(Transform canvas, InventoryGridUI invGridUi, InventorySlotButtonUI slotBtnPrefab)
        {
            GameObject modal = CreatePanel("InventoryModalRoot", canvas, new Color(0.05f, 0.10f, 0.18f, 0.94f));
            modal.GetComponent<RectTransform>().sizeDelta = new Vector2(1180f, 620f);

            Text header = CreateUiText("HeaderTitleText", modal.transform, "Personal Bag <-> Shared Chest", 22, TextAnchor.MiddleLeft, Color.white);
            SetRect(header.rectTransform, -150f, 270f, 760f, 40f);

            Button modeBtn = CreateButton("BtnTransferMode", modal.transform, "Transfer: x1", new Color(0.2f, 0.5f, 0.65f), invGridUi.OnClickCycleTransferMode);
            SetRect(modeBtn.GetComponent<RectTransform>(), 340f, 270f, 170f, 42f);

            Button closeBtn = CreateButton("BtnCloseInventory", modal.transform, "X", new Color(0.65f, 0.22f, 0.22f), invGridUi.CloseModal);
            SetRect(closeBtn.GetComponent<RectTransform>(), 520f, 270f, 64f, 42f);

            GameObject playerGrid = CreateGridContainer("PlayerGridContainer", modal.transform, new Vector2(-300f, -20f), new Vector2(540f, 480f), 5);
            GameObject chestPanel = CreatePanel("ChestAvailablePanel", modal.transform, new Color(0.08f, 0.15f, 0.24f, 0.85f));
            SetRect(chestPanel.GetComponent<RectTransform>(), 290f, -20f, 560f, 500f);

            GameObject chestGrid = CreateGridContainer("ChestGridContainer", chestPanel.transform, Vector2.zero, new Vector2(540f, 480f), 5);
            Text noChestHint = CreateUiText("NoChestHint", modal.transform, "Walk within 4.5m of a Shared Chest to deposit/withdraw.", 20, TextAnchor.MiddleCenter, new Color(0.8f, 0.85f, 0.9f));
            SetRect(noChestHint.rectTransform, 290f, -20f, 460f, 120f);

            var so = new SerializedObject(invGridUi);
            so.FindProperty("modalRoot").objectReferenceValue = modal;
            so.FindProperty("playerGridContainer").objectReferenceValue = playerGrid.transform;
            so.FindProperty("chestGridContainer").objectReferenceValue = chestGrid.transform;
            so.FindProperty("chestAvailablePanel").objectReferenceValue = chestPanel;
            so.FindProperty("noChestHintObject").objectReferenceValue = noChestHint.gameObject;
            so.FindProperty("slotButtonPrefab").objectReferenceValue = slotBtnPrefab;
            so.FindProperty("headerTitleText").objectReferenceValue = header;
            so.FindProperty("transferModeButtonText").objectReferenceValue = modeBtn.GetComponentInChildren<Text>();
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildCraftingModal(Transform canvas, CraftingPanelUI craftUi, Button recipeBtnPrefab)
        {
            GameObject modal = CreatePanel("CraftingModalRoot", canvas, new Color(0.05f, 0.12f, 0.20f, 0.94f));
            modal.GetComponent<RectTransform>().sizeDelta = new Vector2(580f, 480f);

            Text hint = CreateUiText("NearbyChestHintText", modal.transform, "Co-op Crafting", 17, TextAnchor.MiddleLeft, new Color(0.45f, 0.92f, 1f));
            SetRect(hint.rectTransform, -45f, 200f, 440f, 40f);

            Button closeBtn = CreateButton("BtnCloseCrafting", modal.transform, "X", new Color(0.65f, 0.22f, 0.22f), craftUi.CloseModal);
            SetRect(closeBtn.GetComponent<RectTransform>(), 235f, 200f, 56f, 40f);

            var listGo = new GameObject("RecipeListContainer", typeof(RectTransform), typeof(VerticalLayoutGroup));
            listGo.transform.SetParent(modal.transform, false);
            SetRect(listGo.GetComponent<RectTransform>(), 0f, -10f, 500f, 300f);
            var vlg = listGo.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 10f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = false;
            vlg.childControlWidth = false;

            Text feedback = CreateUiText("FeedbackText", modal.transform, "", 18, TextAnchor.MiddleCenter, Color.white);
            SetRect(feedback.rectTransform, 0f, -200f, 500f, 36f);

            var so = new SerializedObject(craftUi);
            so.FindProperty("modalRoot").objectReferenceValue = modal;
            so.FindProperty("recipeListContainer").objectReferenceValue = listGo.transform;
            so.FindProperty("recipeButtonPrefab").objectReferenceValue = recipeBtnPrefab;
            so.FindProperty("nearbyChestHintText").objectReferenceValue = hint;
            so.FindProperty("feedbackText").objectReferenceValue = feedback;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        #endregion

        #region UI Primitive Helpers

        private static void CreateEventSystemIfMissing()
        {
            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            }
        }

        private static Canvas CreateScreenCanvas(string name)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        private static GameObject CreatePanel(string name, Transform parent, Color bgColor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = bgColor;
            return go;
        }

        private static GameObject CreateGridContainer(string name, Transform parent, Vector2 pos, Vector2 size, int cols)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(GridLayoutGroup));
            go.transform.SetParent(parent, false);
            SetRect(go.GetComponent<RectTransform>(), pos.x, pos.y, size.x, size.y);

            var grid = go.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(96f, 96f);
            grid.spacing = new Vector2(8f, 8f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = cols;
            return go;
        }

        private static Text CreateUiText(string name, Transform parent, string text, int fontSize, TextAnchor align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = GetUiFont();
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = color;
            return t;
        }

        private static Button CreateButton(string name, Transform parent, string label, Color bgColor, UnityAction onClickAction)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = bgColor;

            Text t = CreateUiText("Label", go.transform, label, 16, TextAnchor.MiddleCenter, Color.white);
            StretchRect(t.rectTransform, 4f, 4f, 4f, 4f);

            Button btn = go.GetComponent<Button>();
            if (onClickAction != null)
            {
                UnityEventTools.AddPersistentListener(btn.onClick, onClickAction);
            }
            return btn;
        }

        private static void CreateBottomRightBtn(
            string name,
            Transform parent,
            string label,
            float offsetX,
            float offsetY,
            Vector2 size,
            Color bg,
            UnityAction action)
        {
            Button btn = CreateButton(name, parent, label, bg, action);
            RectTransform rt = btn.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(offsetX, offsetY);
            rt.sizeDelta = size;
        }

        private static InputField CreateInputField(string name, Transform parent, string initialText, string placeholderText)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.08f, 0.14f, 0.22f, 0.95f);

            Text ph = CreateUiText("Placeholder", go.transform, placeholderText, 16, TextAnchor.MiddleLeft, new Color(0.6f, 0.7f, 0.8f, 0.6f));
            StretchRect(ph.rectTransform, 10f, 4f, 10f, 4f);

            Text txt = CreateUiText("Text", go.transform, "", 16, TextAnchor.MiddleLeft, Color.white);
            txt.supportRichText = false;
            StretchRect(txt.rectTransform, 10f, 4f, 10f, 4f);

            var field = go.GetComponent<InputField>();
            field.textComponent = txt;
            field.placeholder = ph;
            field.text = initialText;
            return field;
        }

        private static void SetRect(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void StretchRect(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static void SetAnchorTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void SetAnchorTopCenter(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void SetAnchorTopRight(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        #endregion
    }
}
#endif
