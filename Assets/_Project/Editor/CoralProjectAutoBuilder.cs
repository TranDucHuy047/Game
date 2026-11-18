#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Animations;
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
using CoralArchipelago.Gameplay.Visuals;
using CoralArchipelago.Networking;
using CoralArchipelago.Persistence;
using CoralArchipelago.UI;
using Object = UnityEngine.Object;

namespace CoralArchipelago.EditorTools
{
    /// <summary>
    /// One-Click Unity Project Generator for Coral Archipelago.
    /// Integrates KayKit 3D models & animations, builds AnimatorControllers,
    /// generates procedural 3D Island Terrains, Gerstner Ocean Grid, Palm Trees & Crystal Clusters,
    /// configures Retina URP (RenderScale = 1.0, MSAA 4x, ACES + Bloom), and styles Glassmorphism HUD.
    /// </summary>
    public static class CoralProjectAutoBuilder
    {
        private const string MaterialsDir = "Assets/_Project/Materials";
        private const string MeshesDir = "Assets/_Project/Meshes";
        private const string AnimationsDir = "Assets/_Project/Animations";
        private const string NetworkPrefabsDir = "Assets/_Project/Prefabs/Network";
        private const string UiPrefabsDir = "Assets/_Project/Prefabs/UI";
        private const string UiSpritesDir = "Assets/_Project/UI/Sprites";
        private const string ConfigsDir = "Assets/_Project/Configs";
        private const string ScenesDir = "Assets/_Project/Scenes";
        private const string ResourcesDir = "Assets/_Project/Resources";

        private const string KayKitAdvDir = "Assets/Art/KayKit/Adventurers";
        private const string KayKitSkelDir = "Assets/Art/KayKit/Skeletons";
        private const string KayKitSpaceDir = "Assets/Art/KayKit/SpaceBase";
        private const string SoldierGlbPath = "Assets/Art/Characters/Soldier.glb";

        private static Font _cachedFont;

        [InitializeOnLoadMethod]
        private static void AutoGenerateInBatchModeIfNeeded()
        {
            if (!Application.isBatchMode)
                return;

            string cli = string.Join(" ", Environment.GetCommandLineArgs());
            if (cli.Contains("CoralProjectAutoBuilder."))
                return;

            if (EditorBuildSettings.scenes == null || EditorBuildSettings.scenes.Length == 0)
            {
                GenerateFullProjectSetupInternal(openBootstrapScene: false);
            }
        }

        [MenuItem("Coral Archipelago/One-Click Generate Scenes & Prefabs")]
        public static void GenerateFullProjectSetup()
        {
            GenerateFullProjectSetupInternal(openBootstrapScene: true);
        }

        [MenuItem("Coral Archipelago/Attach 2000m Continent + Portals + Boss Systems to Active Scene")]
        public static void AttachContinentSystemsToActiveScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
                return;

            GameObject hostGo = GameObject.Find("IslandWorldUpgrader_2000m");
            if (hostGo == null)
            {
                IslandGraphicsUpgrader existing = Object.FindFirstObjectByType<IslandGraphicsUpgrader>();
                hostGo = existing != null ? existing.gameObject : new GameObject("IslandWorldUpgrader_2000m");
            }

            if (hostGo.GetComponent<IslandGraphicsUpgrader>() == null)
                hostGo.AddComponent<IslandGraphicsUpgrader>();
            if (hostGo.GetComponent<IslandFastTravelAndRaftSystem>() == null)
                hostGo.AddComponent<IslandFastTravelAndRaftSystem>();
            if (hostGo.GetComponent<IslandRelicAndVolcanoBossSystem>() == null)
                hostGo.AddComponent<IslandRelicAndVolcanoBossSystem>();

            EditorSceneManager.MarkSceneDirty(activeScene);
            Selection.activeGameObject = hostGo;
            Debug.Log("[CoralProjectAutoBuilder] Attached IslandGraphicsUpgrader, IslandFastTravelAndRaftSystem & IslandRelicAndVolcanoBossSystem to active scene.");
        }

        /// <summary>
        /// CLI / GitHub Actions entry point to generate all upgraded 3D assets/scenes and export iOS Xcode.
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

            // 1. Materials
            Material terrainMat = CreateOrLoadTerrainMaterial("MAT_CoralIslandTerrain.mat");
            Material oceanMat = CreateOrLoadOceanMaterial("MAT_CoralOcean.mat");
            Material crystalMat = CreateOrLoadBiolumMaterial("MAT_GlowCrystal.mat", new Color(0.20f, 0.85f, 0.98f), new Color(0.22f, 2.35f, 3.0f), 0.14f);
            Material coralMat = CreateOrLoadBiolumMaterial("MAT_CoralBiolum.mat", new Color(0.92f, 0.36f, 0.48f), new Color(0.25f, 1.85f, 2.30f), 0.08f);
            Material palmBarkMat = CreateOrLoadLitMaterial("MAT_PalmBark.mat", new Color(0.46f, 0.28f, 0.16f), 0.15f);
            Material palmLeavesMat = CreateOrLoadFoliageWindMaterial("MAT_PalmLeaves.mat", new Color(0.16f, 0.54f, 0.20f), new Color(0.38f, 0.72f, 0.22f));

            Material advMat = CreateOrLoadTexturedLitMaterial("MAT_KayKit_Adventurer.mat", $"{KayKitAdvDir}/barbarian_texture.png");
            Material skelMat = CreateOrLoadTexturedLitMaterial("MAT_KayKit_Skeleton.mat", $"{KayKitSkelDir}/skeleton_texture.png");
            Material spaceMat = CreateOrLoadTexturedLitMaterial("MAT_KayKit_SpaceBase.mat", $"{KayKitSpaceDir}/spacebits_texture.png");

            // 2. Procedural 3D Meshes (3600m Ocean, Palm Tree, Crystal Cluster; 2000m Island Chunks built by IslandGraphicsUpgrader)
            Mesh oceanGridMesh = SaveMeshAsset(GenerateOceanGridMesh(3600f, 96), "Mesh_OceanGrid.asset");
            Mesh palmTrunkMesh = SaveMeshAsset(GeneratePalmTrunkMesh(), "Mesh_PalmTrunk.asset");
            Mesh palmCanopyMesh = SaveMeshAsset(GeneratePalmCanopyMesh(), "Mesh_PalmCanopy.asset");
            Mesh crystalClusterMesh = SaveMeshAsset(GenerateCrystalClusterMesh(), "Mesh_CrystalCluster.asset");

            // 3. Animator Controllers from Soldier.glb & KayKit FBX embedded clips
            EnsureSoldierGlbImportedAsMecanim();
            RuntimeAnimatorController playerAnimCtrl = CreateCharacterAnimatorController(
                "AC_SoldierPlayer", SoldierGlbPath, $"{KayKitAdvDir}/Barbarian.fbx");
            RuntimeAnimatorController monsterAnimCtrl = CreateCharacterAnimatorController(
                "AC_SkeletonMonster", $"{KayKitSkelDir}/Skeleton_Warrior.fbx");

            // 4. Glassmorphism UI Sprites (9-slice crisp Retina)
            Sprite glassCardSprite = CreateGlassRoundedSprite("SPR_GlassCard", 96, 22, new Color32(10, 22, 38, 178), new Color32(130, 232, 255, 115), 2);
            Sprite glassButtonSprite = CreateGlassRoundedSprite("SPR_GlassButton", 96, 24, new Color32(18, 42, 66, 205), new Color32(160, 242, 255, 140), 2);
            Sprite glassCircleSprite = CreateCircleSprite("SPR_GlassCircle", 128, new Color32(255, 255, 255, 58), new Color32(170, 245, 255, 165), 3);

            // 5. Network & Visual Prefabs (Replacing all CreatePrimitive placeholders)
            GameObject soldierVisualPrefab = CreateSoldierVisualPrefab(playerAnimCtrl, advMat ?? coralMat);
            GameObject playerPrefab = CreatePlayerPrefab(advMat, playerAnimCtrl, coralMat);
            GameObject floorPrefab = CreateSpaceFloorPrefab(spaceMat);
            GameObject lampPrefab = CreateSpaceLampPrefab(spaceMat, crystalMat);
            GameObject chestPrefab = CreateSpaceChestPrefab(spaceMat, crystalMat);
            GameObject woodNodePrefab = CreatePalmTreeNodePrefab(palmTrunkMesh, palmBarkMat, palmCanopyMesh, palmLeavesMat, spaceMat);
            GameObject crystalNodePrefab = CreateCrystalNodePrefab(crystalClusterMesh, crystalMat, spaceMat);
            GameObject monsterPrefab = CreateMonsterPrefab(skelMat, monsterAnimCtrl);

            NetworkPrefabsList netPrefabsList = CreateOrUpdateNetworkPrefabsList(
                playerPrefab, floorPrefab, lampPrefab, chestPrefab, woodNodePrefab, crystalNodePrefab, monsterPrefab);

            // 6. UI Prefabs
            InventorySlotButtonUI slotBtnPrefab = CreateInventorySlotButtonPrefab(glassButtonSprite);
            Button recipeBtnPrefab = CreateCraftRecipeButtonPrefab(glassButtonSprite);

            // 7. Build Scenes
            string bootstrapPath = BuildBootstrapScene(playerPrefab, netPrefabsList, glassCardSprite, glassButtonSprite);
            string worldPath = BuildCoralWorldScene(
                terrainMat, oceanMat, coralMat, crystalMat, spaceMat,
                oceanGridMesh, crystalClusterMesh,
                soldierVisualPrefab,
                floorPrefab, lampPrefab, chestPrefab,
                woodNodePrefab, crystalNodePrefab, monsterPrefab,
                slotBtnPrefab, recipeBtnPrefab,
                glassCardSprite, glassButtonSprite, glassCircleSprite);

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

            Debug.Log("[CoralProjectAutoBuilder] Upgraded all KayKit 3D models, AnimatorControllers, 3D Island Terrain, Gerstner Ocean, Retina Post-Processing & Glassmorphism HUD!");
        }

        private static void EnsureDirectories()
        {
            string[] dirs =
            {
                MaterialsDir, MeshesDir, AnimationsDir,
                NetworkPrefabsDir, UiPrefabsDir, UiSpritesDir, ConfigsDir, ScenesDir, ResourcesDir
            };
            for (int i = 0; i < dirs.Length; i++)
            {
                if (!Directory.Exists(dirs[i]))
                    Directory.CreateDirectory(dirs[i]);
            }
        }

        private static void EnsureDefaultUrpPipelineAsset()
        {
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

            // Retina sharp defaults + 4 Shadow Cascades
            urpAsset.renderScale = 1.0f;
            urpAsset.supportsCameraDepthTexture = true;
            urpAsset.supportsHDR = true;
            urpAsset.shadowDistance = 140f;
            urpAsset.shadowCascadeCount = 4;
            urpAsset.msaaSampleCount = 4;
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

        private static Font GetUiFont()
        {
            if (_cachedFont != null)
                return _cachedFont;

            _cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_cachedFont == null)
                _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _cachedFont;
        }

        #region Materials & Shaders

        private static Material CreateOrLoadTerrainMaterial(string fileName)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("CoralArchipelago/URP/IslandTerrainSplatURP");
            if (shader == null) shader = Shader.Find("CoralArchipelago/URP/CoralIslandTerrainURP");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            Material mat = GetOrCreateMaterialAsset(path, shader);
            if (mat.HasProperty("_WetSandHeight")) mat.SetFloat("_WetSandHeight", -1.0f);
            if (mat.HasProperty("_DrySandMaxHeight")) mat.SetFloat("_DrySandMaxHeight", 3.6f);
            if (mat.HasProperty("_UseVertexBiomeTint")) mat.SetFloat("_UseVertexBiomeTint", 0.32f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadOceanMaterial(string fileName)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("CoralArchipelago/URP/TropicalLagoonWaterURP");
            if (shader == null) shader = Shader.Find("CoralArchipelago/URP/CoralOceanWaterURP");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            Material mat = GetOrCreateMaterialAsset(path, shader);
            if (mat.HasProperty("_WaveAmplitude")) mat.SetFloat("_WaveAmplitude", 0.03f);
            if (mat.HasProperty("_SpecularStrength")) mat.SetFloat("_SpecularStrength", 0.18f);
            if (mat.HasProperty("_WaveFrequency")) mat.SetFloat("_WaveFrequency", 0.24f);
            if (mat.HasProperty("_WaveSpeed")) mat.SetFloat("_WaveSpeed", 0.55f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadFoliageWindMaterial(string fileName, Color baseCol, Color tipCol)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("CoralArchipelago/URP/TropicalFoliageWindURP");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            Material mat = GetOrCreateMaterialAsset(path, shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseCol);
            if (mat.HasProperty("_TipColor")) mat.SetColor("_TipColor", tipCol);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadBiolumMaterial(string fileName, Color baseCol, Color glowCol, float dayGlowFloor)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("CoralArchipelago/URP/CoralBioluminescentLit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            Material mat = GetOrCreateMaterialAsset(path, shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseCol);
            if (mat.HasProperty("_GlowColor")) mat.SetColor("_GlowColor", glowCol);
            if (mat.HasProperty("_DayGlowFloor")) mat.SetFloat("_DayGlowFloor", dayGlowFloor);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadLitMaterial(string fileName, Color color, float smoothness)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            Material mat = GetOrCreateMaterialAsset(path, shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateOrLoadTexturedLitMaterial(string fileName, string textureAssetPath)
        {
            string path = $"{MaterialsDir}/{fileName}";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            Material mat = GetOrCreateMaterialAsset(path, shader);
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.22f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material GetOrCreateMaterialAsset(string path, Shader shader)
        {
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
            return mat;
        }

        #endregion

        #region Procedural 3D Meshes (Island Terrain, Ocean Grid, Palm Tree, Crystal Cluster)

        private static Mesh SaveMeshAsset(Mesh mesh, string fileName)
        {
            string path = $"{MeshesDir}/{fileName}";
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static float EvaluateMainIslandHeight(float x, float z)
        {
            return EvaluateIslandHeightCore(x, z, 76f, 14f, 6.2f, 11);
        }

        private static float EvaluateCrystalIslandHeight(float worldX, float worldZ)
        {
            return EvaluateIslandHeightCore(worldX - 34f, worldZ - 22f, 38f, 7f, 4.4f, 47);
        }

        private static float EvaluateIslandHeightCore(
            float localX,
            float localZ,
            float size,
            float flatCenterRadius,
            float maxHillHeight,
            int seed)
        {
            float maxRadius = size * 0.48f;
            float dist = Mathf.Sqrt(localX * localX + localZ * localZ);
            float normDist = dist / Mathf.Max(1f, maxRadius);

            // Coastline irregularity
            float angle = Mathf.Atan2(localZ, localX);
            float coastWarp = 1.0f + 0.12f * Mathf.Sin(angle * 3f + seed) + 0.07f * Mathf.Cos(angle * 5f - seed * 0.5f);
            float r = normDist / coastWarp;

            if (r >= 1.15f)
                return -4.2f;

            // Smooth drop off into deep ocean near edges
            float shoreFalloff = 1.0f - Mathf.SmoothStep(0.62f, 1.04f, r);
            float baseHeight = Mathf.Lerp(-3.8f, 1.60f, shoreFalloff);

            // Flat camp plateau around center (r_dist <= flatCenterRadius)
            float hillBlend = Mathf.SmoothStep(flatCenterRadius, flatCenterRadius + 10f, dist) * (1.0f - Mathf.SmoothStep(0.65f, 0.92f, r));
            float n1 = Mathf.PerlinNoise((localX + seed * 17f) * 0.055f + 50f, (localZ + seed * 31f) * 0.055f + 50f);
            float n2 = Mathf.PerlinNoise((localX - seed * 11f) * 0.13f + 120f, (localZ + seed * 19f) * 0.13f + 120f);
            float ridge = Mathf.Pow(n1 * 0.72f + n2 * 0.28f, 1.25f) * maxHillHeight;

            return baseHeight + ridge * hillBlend;
        }

        private static Mesh GenerateIslandMesh(
            string name,
            float size,
            int resolution,
            float flatCenterRadius,
            float maxHillHeight,
            int seed)
        {
            int vertsPerSide = resolution + 1;
            var vertices = new Vector3[vertsPerSide * vertsPerSide];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[resolution * resolution * 6];

            float half = size * 0.5f;
            float step = size / resolution;

            for (int z = 0; z < vertsPerSide; z++)
            {
                for (int x = 0; x < vertsPerSide; x++)
                {
                    int idx = z * vertsPerSide + x;
                    float wx = -half + x * step;
                    float wz = -half + z * step;
                    float wy = EvaluateIslandHeightCore(wx, wz, size, flatCenterRadius, maxHillHeight, seed);
                    vertices[idx] = new Vector3(wx, wy, wz);
                    uvs[idx] = new Vector2((float)x / resolution, (float)z / resolution);
                }
            }

            int tri = 0;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int i0 = z * vertsPerSide + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + vertsPerSide;
                    int i3 = i2 + 1;

                    triangles[tri++] = i0;
                    triangles[tri++] = i2;
                    triangles[tri++] = i1;
                    triangles[tri++] = i1;
                    triangles[tri++] = i2;
                    triangles[tri++] = i3;
                }
            }

            var mesh = new Mesh
            {
                name = name,
                indexFormat = vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                vertices = vertices,
                uv = uvs,
                triangles = triangles
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GenerateOceanGridMesh(float size, int resolution)
        {
            int vertsPerSide = resolution + 1;
            var vertices = new Vector3[vertsPerSide * vertsPerSide];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[resolution * resolution * 6];

            float half = size * 0.5f;
            float step = size / resolution;

            for (int z = 0; z < vertsPerSide; z++)
            {
                for (int x = 0; x < vertsPerSide; x++)
                {
                    int idx = z * vertsPerSide + x;
                    vertices[idx] = new Vector3(-half + x * step, 0f, -half + z * step);
                    uvs[idx] = new Vector2((float)x / resolution, (float)z / resolution);
                }
            }

            int tri = 0;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int i0 = z * vertsPerSide + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + vertsPerSide;
                    int i3 = i2 + 1;

                    triangles[tri++] = i0;
                    triangles[tri++] = i2;
                    triangles[tri++] = i1;
                    triangles[tri++] = i1;
                    triangles[tri++] = i2;
                    triangles[tri++] = i3;
                }
            }

            var mesh = new Mesh
            {
                name = "OceanGrid",
                indexFormat = vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                vertices = vertices,
                uv = uvs,
                triangles = triangles
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GeneratePalmTrunkMesh()
        {
            const int segments = 9;
            const int sides = 7;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            float totalHeight = 4.2f;
            for (int s = 0; s <= segments; s++)
            {
                float t = (float)s / segments;
                float y = t * totalHeight;
                float bendX = Mathf.Sin(t * Mathf.PI * 0.65f) * 0.55f;
                float bendZ = t * t * 0.30f;
                float radius = Mathf.Lerp(0.24f, 0.11f, t);

                for (int i = 0; i < sides; i++)
                {
                    float ang = (i / (float)sides) * Mathf.PI * 2f;
                    verts.Add(new Vector3(bendX + Mathf.Cos(ang) * radius, y, bendZ + Mathf.Sin(ang) * radius));
                    uvs.Add(new Vector2(i / (float)sides, t));
                }
            }

            for (int s = 0; s < segments; s++)
            {
                int rowA = s * sides;
                int rowB = (s + 1) * sides;
                for (int i = 0; i < sides; i++)
                {
                    int next = (i + 1) % sides;
                    tris.Add(rowA + i);
                    tris.Add(rowB + i);
                    tris.Add(rowA + next);

                    tris.Add(rowA + next);
                    tris.Add(rowB + i);
                    tris.Add(rowB + next);
                }
            }

            var mesh = new Mesh { name = "PalmTrunk", vertices = verts.ToArray(), uv = uvs.ToArray(), triangles = tris.ToArray() };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GeneratePalmCanopyMesh()
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            Vector3 crownPos = new Vector3(0.49f, 4.18f, 0.30f);
            const int frondCount = 7;
            const int frondSegs = 5;

            for (int f = 0; f < frondCount; f++)
            {
                float yaw = (f / (float)frondCount) * Mathf.PI * 2f + 0.25f;
                float elevationLift = (f % 2 == 0) ? 0.55f : 0.32f;
                Vector3 dir = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));
                Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;

                int baseVert = verts.Count;
                for (int s = 0; s <= frondSegs; s++)
                {
                    float t = (float)s / frondSegs;
                    float len = t * 2.25f;
                    float archY = Mathf.Sin(t * Mathf.PI * 0.72f) * elevationLift - t * t * 0.85f;
                    float halfW = Mathf.Sin(Mathf.Clamp01(t * 0.9f + 0.1f) * Mathf.PI) * 0.42f;

                    Vector3 spine = crownPos + dir * len + Vector3.up * archY;
                    verts.Add(spine - right * halfW + Vector3.down * 0.08f);
                    verts.Add(spine + Vector3.up * 0.06f);
                    verts.Add(spine + right * halfW + Vector3.down * 0.08f);
                    uvs.Add(new Vector2(0f, t));
                    uvs.Add(new Vector2(0.5f, t));
                    uvs.Add(new Vector2(1f, t));
                }

                for (int s = 0; s < frondSegs; s++)
                {
                    int r0 = baseVert + s * 3;
                    int r1 = r0 + 3;
                    // Left half (double-sided for underside visibility)
                    AddDoubleSidedQuad(tris, r0, r0 + 1, r1 + 1, r1);
                    // Right half
                    AddDoubleSidedQuad(tris, r0 + 1, r0 + 2, r1 + 2, r1 + 1);
                }
            }

            var mesh = new Mesh { name = "PalmCanopy", vertices = verts.ToArray(), uv = uvs.ToArray(), triangles = tris.ToArray() };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GenerateCrystalClusterMesh()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();

            AddHexCrystalSpire(verts, tris, Vector3.zero, Quaternion.Euler(4f, 10f, -5f), 0.32f, 1.55f);
            AddHexCrystalSpire(verts, tris, new Vector3(0.32f, 0f, 0.18f), Quaternion.Euler(18f, 45f, -22f), 0.22f, 1.10f);
            AddHexCrystalSpire(verts, tris, new Vector3(-0.30f, 0f, 0.22f), Quaternion.Euler(-16f, -35f, 24f), 0.20f, 0.98f);
            AddHexCrystalSpire(verts, tris, new Vector3(0.08f, 0f, -0.34f), Quaternion.Euler(-25f, 15f, -10f), 0.19f, 0.92f);
            AddHexCrystalSpire(verts, tris, new Vector3(-0.22f, 0f, -0.24f), Quaternion.Euler(14f, 120f, 28f), 0.16f, 0.78f);

            var mesh = new Mesh { name = "CrystalCluster", vertices = verts.ToArray(), triangles = tris.ToArray() };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddHexCrystalSpire(
            List<Vector3> verts,
            List<int> tris,
            Vector3 offset,
            Quaternion rot,
            float radius,
            float height)
        {
            const int sides = 6;
            int baseIdx = verts.Count;
            float bodyHeight = height * 0.72f;

            for (int i = 0; i < sides; i++)
            {
                float ang = (i / (float)sides) * Mathf.PI * 2f;
                Vector3 p = new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
                verts.Add(offset + rot * p);
            }
            for (int i = 0; i < sides; i++)
            {
                float ang = (i / (float)sides) * Mathf.PI * 2f;
                Vector3 p = new Vector3(Mathf.Cos(ang) * (radius * 0.85f), bodyHeight, Mathf.Sin(ang) * (radius * 0.85f));
                verts.Add(offset + rot * p);
            }

            int tipIdx = verts.Count;
            verts.Add(offset + rot * new Vector3(0f, height, 0f));

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                // Side quad
                tris.Add(baseIdx + i);
                tris.Add(baseIdx + sides + i);
                tris.Add(baseIdx + next);

                tris.Add(baseIdx + next);
                tris.Add(baseIdx + sides + i);
                tris.Add(baseIdx + sides + next);

                // Tip cap triangle
                tris.Add(baseIdx + sides + i);
                tris.Add(tipIdx);
                tris.Add(baseIdx + sides + next);
            }
        }

        private static void AddDoubleSidedQuad(List<int> tris, int a, int b, int c, int d)
        {
            tris.Add(a); tris.Add(b); tris.Add(c);
            tris.Add(a); tris.Add(c); tris.Add(d);
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(a); tris.Add(d); tris.Add(c);
        }

        #endregion

        #region Animator Controllers & KayKit Model Helpers

        private static RuntimeAnimatorController CreateCharacterAnimatorController(
            string ctrlName,
            string primaryModelPath,
            string secondaryModelPath = null)
        {
            string ctrlPath = $"{AnimationsDir}/{ctrlName}.controller";
            var clips = new List<AnimationClip>();

            CollectModelAnimationClips(primaryModelPath, clips);
            if (clips.Count == 0 && !string.IsNullOrEmpty(secondaryModelPath))
            {
                CollectModelAnimationClips(secondaryModelPath, clips);
            }

            Debug.Log($"[CoralProjectAutoBuilder] {ctrlName}: collected {clips.Count} clips from {primaryModelPath}");

            AnimationClip idleClip = ExtractStandaloneClip(ctrlName, "Idle", FindClipByKeywords(clips, "Idle_A", "Idle", "Combat_Idle"), loop: true);
            AnimationClip walkClip = ExtractStandaloneClip(ctrlName, "Walk", FindClipByKeywords(clips, "Walk", "Walking_A", "Walking_B"), loop: true);
            AnimationClip runClip = ExtractStandaloneClip(ctrlName, "Run", FindClipByKeywords(clips, "Run", "Running_A", "Running_B"), loop: true);
            AnimationClip attackClip = ExtractStandaloneClip(ctrlName, "Attack", FindClipByKeywords(clips, "1H_Melee_Attack_Chop", "1H_Melee_Attack_Slice", "Chop", "Attack", "Run"), loop: false);
            AnimationClip hitClip = ExtractStandaloneClip(ctrlName, "Hit", FindClipByKeywords(clips, "Hit_A", "Hit_B", "Hit", "Idle"), loop: false);
            AnimationClip deathClip = ExtractStandaloneClip(ctrlName, "Death", FindClipByKeywords(clips, "Death_A", "Death_B", "Death", "Idle"), loop: false);

            if (AssetDatabase.LoadAssetAtPath<Object>(ctrlPath) != null)
            {
                AssetDatabase.DeleteAsset(ctrlPath);
                AssetDatabase.SaveAssets();
            }

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            if (controller.layers == null || controller.layers.Length == 0)
            {
                controller.AddLayer("Base Layer");
            }

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("IsDead", AnimatorControllerParameterType.Bool);

            AnimatorStateMachine sm = controller.layers[0].stateMachine;
            AnimatorState idleState = sm.AddState("Idle");
            idleState.motion = idleClip;
            sm.defaultState = idleState;

            AnimatorState walkState = sm.AddState("Walk");
            walkState.motion = walkClip != null ? walkClip : idleClip;

            AnimatorState runState = sm.AddState("Run");
            runState.motion = runClip != null ? runClip : walkState.motion;

            AnimatorState attackState = sm.AddState("Attack");
            attackState.motion = attackClip != null ? attackClip : idleClip;

            AnimatorState hitState = sm.AddState("Hit");
            hitState.motion = hitClip != null ? hitClip : idleClip;

            AnimatorState deathState = sm.AddState("Death");
            deathState.motion = deathClip != null ? deathClip : idleClip;

            // Locomotion transitions
            AddParamTransition(idleState, walkState, AnimatorConditionMode.Greater, 0.25f, "Speed", 0.12f);
            AddParamTransition(walkState, idleState, AnimatorConditionMode.Less, 0.18f, "Speed", 0.15f);
            AddParamTransition(walkState, runState, AnimatorConditionMode.Greater, 10.5f, "Speed", 0.12f);
            AddParamTransition(runState, walkState, AnimatorConditionMode.Less, 9.8f, "Speed", 0.12f);

            // Attack & Hit AnyState triggers
            AnimatorStateTransition anyToAttack = sm.AddAnyStateTransition(attackState);
            anyToAttack.AddCondition(AnimatorConditionMode.If, 0f, "Attack");
            anyToAttack.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
            anyToAttack.hasExitTime = false;
            anyToAttack.duration = 0.08f;

            AnimatorStateTransition attackToIdle = attackState.AddTransition(idleState);
            attackToIdle.hasExitTime = true;
            attackToIdle.exitTime = 0.88f;
            attackToIdle.duration = 0.12f;

            AnimatorStateTransition anyToHit = sm.AddAnyStateTransition(hitState);
            anyToHit.AddCondition(AnimatorConditionMode.If, 0f, "Hit");
            anyToHit.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
            anyToHit.hasExitTime = false;
            anyToHit.duration = 0.06f;

            AnimatorStateTransition hitToIdle = hitState.AddTransition(idleState);
            hitToIdle.hasExitTime = true;
            hitToIdle.exitTime = 0.85f;
            hitToIdle.duration = 0.12f;

            AnimatorStateTransition anyToDeath = sm.AddAnyStateTransition(deathState);
            anyToDeath.AddCondition(AnimatorConditionMode.If, 0f, "IsDead");
            anyToDeath.canTransitionToSelf = false;
            anyToDeath.hasExitTime = false;
            anyToDeath.duration = 0.10f;

            AnimatorStateTransition deathToIdle = deathState.AddTransition(idleState);
            deathToIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
            deathToIdle.hasExitTime = false;
            deathToIdle.duration = 0.12f;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ctrlPath, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ctrlPath);
        }

        private static void EnsureSoldierGlbImportedAsMecanim()
        {
            if (!File.Exists(SoldierGlbPath))
                return;

            AssetDatabase.ImportAsset(SoldierGlbPath, ImportAssetOptions.ForceSynchronousImport);
            AssetImporter importer = AssetImporter.GetAtPath(SoldierGlbPath);
            if (importer == null)
                return;

            var so = new SerializedObject(importer);
            SerializedProperty animProp = so.FindProperty("importSettings.animationMethod") ?? so.FindProperty("animationMethod");
            if (animProp != null && animProp.propertyType == SerializedPropertyType.Enum && animProp.intValue != 2)
            {
                animProp.intValue = 2; // 2 = Mecanim in glTFast AnimationMethod
                so.ApplyModifiedPropertiesWithoutUndo();
                importer.SaveAndReimport();
                Debug.Log("[CoralProjectAutoBuilder] Configured Soldier.glb glTFast animationMethod = Mecanim.");
            }
        }

        private static void CollectModelAnimationClips(string modelPath, List<AnimationClip> clips)
        {
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                return;

            Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(modelPath);
            if (allAssets == null)
                return;

            for (int i = 0; i < allAssets.Length; i++)
            {
                if (allAssets[i] is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    clips.Add(clip);
                }
            }
        }

        private static AnimationClip FindClipByKeywords(List<AnimationClip> clips, params string[] keywords)
        {
            for (int k = 0; k < keywords.Length; k++)
            {
                string kw = keywords[k];
                for (int i = 0; i < clips.Count; i++)
                {
                    if (string.Equals(clips[i].name, kw, StringComparison.OrdinalIgnoreCase))
                        return clips[i];
                }
            }

            for (int k = 0; k < keywords.Length; k++)
            {
                string kw = keywords[k];
                for (int i = 0; i < clips.Count; i++)
                {
                    if (clips[i].name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        return clips[i];
                }
            }

            return clips.Count > 0 ? clips[0] : null;
        }

        private static AnimationClip ExtractStandaloneClip(string prefix, string stateName, AnimationClip sourceClip, bool loop)
        {
            if (sourceClip == null)
                return null;

            string clipPath = $"{AnimationsDir}/{prefix}_{stateName}.anim";
            AnimationClip copy = Object.Instantiate(sourceClip);
            copy.name = $"{prefix}_{stateName}";
            copy.legacy = false;

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(copy);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(copy, settings);

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) != null)
            {
                AssetDatabase.DeleteAsset(clipPath);
            }
            AssetDatabase.CreateAsset(copy, clipPath);
            return copy;
        }

        private static void AddParamTransition(
            AnimatorState from,
            AnimatorState to,
            AnimatorConditionMode mode,
            float threshold,
            string paramName,
            float duration)
        {
            AnimatorStateTransition tr = from.AddTransition(to);
            tr.AddCondition(mode, threshold, paramName);
            tr.hasExitTime = false;
            tr.duration = duration;
        }

        private static GameObject InstantiateKayKitModel(string fbxPath, Transform parent, Material mat, bool keepRootAnimator = false)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (asset == null)
            {
                Debug.LogWarning($"[CoralProjectAutoBuilder] Model missing at {fbxPath}.");
                GameObject empty = new GameObject("MissingModel");
                empty.transform.SetParent(parent, false);
                return empty;
            }

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            StripCollidersRecursive(inst);
            ApplyMaterialRecursive(inst, mat);
            if (!keepRootAnimator)
            {
                StripEmptyAnimators(inst);
            }
            return inst;
        }

        private static GameObject InstantiateSoldierModel(Transform parent, RuntimeAnimatorController animCtrl, Material fallbackMat)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierGlbPath);
            if (asset == null && File.Exists(SoldierGlbPath))
            {
                AssetDatabase.ImportAsset(SoldierGlbPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierGlbPath);
            }

            if (asset == null)
            {
                Debug.LogWarning($"[CoralProjectAutoBuilder] Soldier.glb missing at {SoldierGlbPath}, falling back to KayKit Barbarian.");
                GameObject fallback = InstantiateKayKitModel($"{KayKitAdvDir}/Barbarian.fbx", parent, fallbackMat, keepRootAnimator: true);
                Animator fbAnim = fallback.GetComponentInChildren<Animator>(true) ?? fallback.AddComponent<Animator>();
                fbAnim.runtimeAnimatorController = animCtrl;
                fbAnim.applyRootMotion = false;
                StripEmptyAnimators(fallback);
                return fallback;
            }

            GameObject inst = PrefabUtility.IsPartOfPrefabAsset(asset)
                ? (GameObject)PrefabUtility.InstantiatePrefab(asset, parent)
                : Object.Instantiate(asset, parent);
            inst.name = "SoldierModel";
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            StripCollidersRecursive(inst);
            foreach (Animation legacyAnim in inst.GetComponentsInChildren<Animation>(true))
            {
                Object.DestroyImmediate(legacyAnim);
            }

            Animator animator = inst.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = inst.AddComponent<Animator>();
            animator.runtimeAnimatorController = animCtrl;
            animator.applyRootMotion = false;

            foreach (Animator extra in inst.GetComponentsInChildren<Animator>(true))
            {
                if (extra != animator && extra.runtimeAnimatorController == null)
                    Object.DestroyImmediate(extra);
            }

            return inst;
        }

        private static void StripEmptyAnimators(GameObject root)
        {
            if (root == null) return;
            foreach (Animator a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a != null && a.runtimeAnimatorController == null)
                    Object.DestroyImmediate(a);
            }
        }

        private static void AttachWeaponToHandBone(
            GameObject characterRoot,
            string weaponFbxPath,
            Material mat,
            Vector3 localOffset,
            Vector3 localEuler)
        {
            GameObject weaponAsset = AssetDatabase.LoadAssetAtPath<GameObject>(weaponFbxPath);
            if (weaponAsset == null || characterRoot == null)
                return;

            Transform handBone = FindChildRecursive(characterRoot.transform, "handslot.r", "hand.r", "RightHand", "Hand_R");
            if (handBone == null)
                handBone = characterRoot.transform;

            GameObject weaponInst = (GameObject)PrefabUtility.InstantiatePrefab(weaponAsset, handBone);
            weaponInst.name = "EquippedTool";
            weaponInst.transform.localPosition = localOffset;
            weaponInst.transform.localRotation = Quaternion.Euler(localEuler);
            StripCollidersRecursive(weaponInst);
            StripEmptyAnimators(weaponInst);
            ApplyMaterialRecursive(weaponInst, mat);
        }

        private static Transform FindChildRecursive(Transform root, params string[] candidateNames)
        {
            for (int c = 0; c < candidateNames.Length; c++)
            {
                Transform exact = FindSingleNameRecursive(root, candidateNames[c]);
                if (exact != null)
                    return exact;
            }
            return null;
        }

        private static Transform FindSingleNameRecursive(Transform current, string targetName)
        {
            if (string.Equals(current.name, targetName, StringComparison.OrdinalIgnoreCase))
                return current;

            for (int i = 0; i < current.childCount; i++)
            {
                Transform found = FindSingleNameRecursive(current.GetChild(i), targetName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void StripCollidersRecursive(GameObject root)
        {
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(c);
            }
        }

        private static void ApplyMaterialRecursive(GameObject root, Material mat)
        {
            if (mat == null)
                return;

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }

        #endregion

        #region Network & UI Prefabs

        private static GameObject CreateSoldierVisualPrefab(RuntimeAnimatorController animCtrl, Material fallbackMat)
        {
            string path = $"{ResourcesDir}/SoldierPlayerVisual.prefab";
            var root = new GameObject("SoldierPlayerVisual");
            InstantiateSoldierModel(root.transform, animCtrl, fallbackMat);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreatePlayerPrefab(Material advMat, RuntimeAnimatorController animCtrl, Material fallbackMat)
        {
            string path = $"{NetworkPrefabsDir}/PlayerAvatar.prefab";
            var root = new GameObject("PlayerAvatar");

            var cc = root.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.height = 1.8f;
            cc.radius = 0.38f;

            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();
            root.AddComponent<PlayerInventory>();
            root.AddComponent<PlayerOnboardingController>();
            var animSync = root.AddComponent<CharacterAnimationSync>();
            root.AddComponent<NetworkPlayerController>();

            GameObject model = InstantiateSoldierModel(root.transform, animCtrl, advMat ?? fallbackMat);
            Animator animator = model.GetComponentInChildren<Animator>(true);

            var animSo = new SerializedObject(animSync);
            animSo.FindProperty("targetAnimator").objectReferenceValue = animator;
            animSo.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static void AdjustKayKitPlayerProportions(GameObject modelRoot)
        {
            if (modelRoot == null)
                return;

            Transform head = FindChildRecursive(modelRoot.transform, "head", "Head");
            if (head != null)
                head.localScale = Vector3.one * 0.78f;

            Transform tool = FindChildRecursive(modelRoot.transform, "EquippedTool");
            if (tool != null)
                tool.localScale = Vector3.one * 0.75f;
        }

        private static GameObject CreateSpaceFloorPrefab(Material spaceMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_CoralFloor.prefab";
            var root = new GameObject("PF_CoralFloor");
            root.AddComponent<NetworkObject>();

            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.12f, 0f);
            col.size = new Vector3(2.8f, 0.25f, 2.8f);

            GameObject model = InstantiateKayKitModel($"{KayKitSpaceDir}/landingpad_small.fbx", root.transform, spaceMat);
            model.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateSpaceLampPrefab(Material spaceMat, Material crystalMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_GlowLamp.prefab";
            var root = new GameObject("PF_GlowLamp");
            root.AddComponent<NetworkObject>();

            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 1.0f, 0f);
            col.size = new Vector3(0.8f, 2.0f, 0.8f);

            GameObject model = InstantiateKayKitModel($"{KayKitSpaceDir}/lights.fbx", root.transform, spaceMat);
            model.transform.localScale = new Vector3(0.9f, 0.9f, 0.9f);

            GameObject orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(orb.GetComponent<Collider>());
            orb.name = "BiolumBeacon";
            orb.transform.SetParent(root.transform, false);
            orb.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            orb.transform.localScale = new Vector3(0.42f, 0.42f, 0.42f);
            orb.GetComponent<Renderer>().sharedMaterial = crystalMat;

            var lightGo = new GameObject("WarmBeaconLight", typeof(Light));
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 1.85f, 0f);
            Light pt = lightGo.GetComponent<Light>();
            pt.type = LightType.Point;
            pt.color = new Color(0.28f, 0.95f, 0.90f);
            pt.range = 11f;
            pt.intensity = 2.0f;
            pt.shadows = LightShadows.None;

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateSpaceChestPrefab(Material spaceMat, Material crystalMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_SharedChest.prefab";
            var root = new GameObject("PF_SharedChest");
            root.AddComponent<NetworkObject>();

            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.6f, 0f);
            col.size = new Vector3(1.5f, 1.2f, 1.2f);

            GameObject model = InstantiateKayKitModel($"{KayKitSpaceDir}/cargo_A.fbx", root.transform, spaceMat);
            model.transform.localScale = new Vector3(0.85f, 0.85f, 0.85f);

            GameObject indicator = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(indicator.GetComponent<Collider>());
            indicator.name = "ChestGlowSeal";
            indicator.transform.SetParent(root.transform, false);
            indicator.transform.localPosition = new Vector3(0f, 0.95f, 0f);
            indicator.transform.localScale = new Vector3(0.55f, 0.10f, 0.55f);
            indicator.GetComponent<Renderer>().sharedMaterial = crystalMat;

            root.AddComponent<SharedChestInventory>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreatePalmTreeNodePrefab(
            Mesh trunkMesh,
            Material barkMat,
            Mesh canopyMesh,
            Material leavesMat,
            Material rockMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_CoralWoodNode.prefab";
            var root = new GameObject("PF_CoralWoodNode");
            root.AddComponent<NetworkObject>();

            CapsuleCollider col = root.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0.2f, 2.0f, 0.1f);
            col.radius = 0.55f;
            col.height = 4.2f;

            var visual = new GameObject("PalmTreeVisual");
            visual.transform.SetParent(root.transform, false);

            var trunkGo = new GameObject("PalmTrunk", typeof(MeshFilter), typeof(MeshRenderer));
            trunkGo.transform.SetParent(visual.transform, false);
            trunkGo.GetComponent<MeshFilter>().sharedMesh = trunkMesh;
            trunkGo.GetComponent<MeshRenderer>().sharedMaterial = barkMat;

            var canopyGo = new GameObject("PalmCanopy", typeof(MeshFilter), typeof(MeshRenderer));
            canopyGo.transform.SetParent(visual.transform, false);
            canopyGo.GetComponent<MeshFilter>().sharedMesh = canopyMesh;
            canopyGo.GetComponent<MeshRenderer>().sharedMaterial = leavesMat;

            GameObject baseRock = InstantiateKayKitModel($"{KayKitSpaceDir}/rock_A.fbx", visual.transform, rockMat);
            baseRock.transform.localPosition = new Vector3(-0.35f, -0.08f, 0.25f);
            baseRock.transform.localScale = new Vector3(0.5f, 0.45f, 0.5f);

            var node = root.AddComponent<HarvestableResourceNode>();
            var so = new SerializedObject(node);
            so.FindProperty("resourceItemId").stringValue = "coral_wood";
            so.FindProperty("visualRoot").objectReferenceValue = visual;
            so.FindProperty("nodeCollider").objectReferenceValue = col;
            so.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateCrystalNodePrefab(Mesh crystalMesh, Material crystalMat, Material rockMat)
        {
            string path = $"{NetworkPrefabsDir}/PF_GlowCrystalNode.prefab";
            var root = new GameObject("PF_GlowCrystalNode");
            root.AddComponent<NetworkObject>();

            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.85f, 0f);
            col.size = new Vector3(1.3f, 1.7f, 1.3f);

            var visual = new GameObject("CrystalClusterVisual");
            visual.transform.SetParent(root.transform, false);

            var crystalGo = new GameObject("HexCrystals", typeof(MeshFilter), typeof(MeshRenderer));
            crystalGo.transform.SetParent(visual.transform, false);
            crystalGo.GetComponent<MeshFilter>().sharedMesh = crystalMesh;
            crystalGo.GetComponent<MeshRenderer>().sharedMaterial = crystalMat;

            GameObject baseRocks = InstantiateKayKitModel($"{KayKitSpaceDir}/rocks_A.fbx", visual.transform, rockMat);
            baseRocks.transform.localPosition = new Vector3(0f, -0.10f, 0f);
            baseRocks.transform.localScale = new Vector3(0.65f, 0.55f, 0.65f);

            var ptGo = new GameObject("CrystalBioLight", typeof(Light));
            ptGo.transform.SetParent(visual.transform, false);
            ptGo.transform.localPosition = new Vector3(0f, 1.15f, 0f);
            Light pt = ptGo.GetComponent<Light>();
            pt.type = LightType.Point;
            pt.color = new Color(0.18f, 0.92f, 1.0f);
            pt.range = 7.5f;
            pt.intensity = 1.6f;
            pt.shadows = LightShadows.None;

            var node = root.AddComponent<HarvestableResourceNode>();
            var so = new SerializedObject(node);
            so.FindProperty("resourceItemId").stringValue = "glow_crystal";
            so.FindProperty("visualRoot").objectReferenceValue = visual;
            so.FindProperty("nodeCollider").objectReferenceValue = col;
            so.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static GameObject CreateMonsterPrefab(Material skelMat, RuntimeAnimatorController animCtrl)
        {
            string path = $"{NetworkPrefabsDir}/PF_ReefCrabMonster.prefab";
            var root = new GameObject("PF_ReefCrabMonster");
            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();

            CapsuleCollider col = root.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 1.0f, 0f);
            col.radius = 0.55f;
            col.height = 2.0f;

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.55f;
            agent.height = 2.0f;

            var animSync = root.AddComponent<CharacterAnimationSync>();

            var visual = new GameObject("SkeletonGuardianVisual");
            visual.transform.SetParent(root.transform, false);

            GameObject model = InstantiateKayKitModel($"{KayKitSkelDir}/Skeleton_Warrior.fbx", visual.transform, skelMat, keepRootAnimator: true);
            model.name = "SkeletonWarriorModel";

            Animator animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = animCtrl;
            animator.applyRootMotion = false;
            StripEmptyAnimators(model);

            var animSo = new SerializedObject(animSync);
            animSo.FindProperty("targetAnimator").objectReferenceValue = animator;
            animSo.ApplyModifiedPropertiesWithoutUndo();

            AttachWeaponToHandBone(
                model,
                $"{KayKitSkelDir}/Skeleton_Blade.fbx",
                skelMat,
                Vector3.zero,
                Vector3.zero);

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

        private static InventorySlotButtonUI CreateInventorySlotButtonPrefab(Sprite glassBtnSprite)
        {
            string path = $"{UiPrefabsDir}/PF_InventorySlotButton.prefab";
            var root = new GameObject("PF_InventorySlotButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(InventorySlotButtonUI));
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(100f, 100f);

            Image bg = root.GetComponent<Image>();
            ApplySlicedSprite(bg, glassBtnSprite, new Color(0.14f, 0.25f, 0.38f, 0.85f));

            Text label = CreateUiText("ItemLabelText", root.transform, "", 15, TextAnchor.MiddleCenter, Color.white);
            StretchRect(label.rectTransform, 4f, 22f, 4f, 4f);

            Text amount = CreateUiText("AmountText", root.transform, "", 16, TextAnchor.LowerRight, new Color(1f, 0.95f, 0.55f));
            StretchRect(amount.rectTransform, 4f, 4f, 6f, 4f);

            var comp = root.GetComponent<InventorySlotButtonUI>();
            var so = new SerializedObject(comp);
            so.FindProperty("slotButton").objectReferenceValue = root.GetComponent<Button>();
            so.FindProperty("slotBackground").objectReferenceValue = bg;
            so.FindProperty("itemLabelText").objectReferenceValue = label;
            so.FindProperty("amountText").objectReferenceValue = amount;
            so.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved.GetComponent<InventorySlotButtonUI>();
        }

        private static Button CreateCraftRecipeButtonPrefab(Sprite glassBtnSprite)
        {
            string path = $"{UiPrefabsDir}/PF_CraftRecipeButton.prefab";
            var root = new GameObject("PF_CraftRecipeButton", typeof(RectTransform), typeof(Image), typeof(Button));
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(480f, 56f);

            Image bg = root.GetComponent<Image>();
            ApplySlicedSprite(bg, glassBtnSprite, new Color(0.12f, 0.34f, 0.46f, 0.92f));

            Text label = CreateUiText("LabelText", root.transform, "Recipe", 17, TextAnchor.MiddleLeft, Color.white);
            StretchRect(label.rectTransform, 16f, 4f, 16f, 4f);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved.GetComponent<Button>();
        }

        #endregion

        #region Scenes & Lighting / Post-Processing

        private static string BuildBootstrapScene(
            GameObject playerPrefab,
            NetworkPrefabsList netPrefabsList,
            Sprite glassCardSprite,
            Sprite glassBtnSprite)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.GetComponent<Camera>().backgroundColor = new Color(0.02f, 0.07f, 0.14f);
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

            // Glassmorphism Lobby Canvas
            Canvas lobbyCanvas = CreateScreenCanvas("ClientLobbyCanvas");
            var touchUi = lobbyCanvas.gameObject.AddComponent<MobileTouchInputUI>();

            GameObject panel = CreateGlassPanel("LobbyPanel", lobbyCanvas.transform, glassCardSprite, new Color(0.06f, 0.14f, 0.25f, 0.88f));
            RectTransform panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(580f, 400f);

            Text title = CreateUiText("Title", panel.transform, "CORAL ARCHIPELAGO", 28, TextAnchor.MiddleCenter, new Color(0.36f, 0.96f, 1.0f));
            SetRect(title.rectTransform, 0f, 140f, 520f, 46f);

            InputField ipInput = CreateInputField("ServerIpInput", panel.transform, "127.0.0.1", "VPS IP Address...", glassBtnSprite);
            SetRect(ipInput.GetComponent<RectTransform>(), 0f, 70f, 440f, 46f);

            InputField pinInput = CreateInputField("PasscodeInput", panel.transform, "1234", "Server Passcode...", glassBtnSprite);
            SetRect(pinInput.GetComponent<RectTransform>(), 0f, 12f, 440f, 46f);

            Button connectBtn = CreateGlassButton("ConnectButton", panel.transform, "Connect to VPS", glassBtnSprite, new Color(0.14f, 0.56f, 0.72f, 0.95f), touchUi.OnClickConnectServer);
            SetRect(connectBtn.GetComponent<RectTransform>(), 0f, -56f, 380f, 54f);

            Button hostBtn = CreateGlassButton("HostTestButton", panel.transform, "Start Local Host (Test)", glassBtnSprite, new Color(0.16f, 0.64f, 0.44f, 0.95f), touchUi.OnClickStartHostTest);
            SetRect(hostBtn.GetComponent<RectTransform>(), 0f, -124f, 380f, 54f);

            var touchSo = new SerializedObject(touchUi);
            touchSo.FindProperty("serverIpInput").objectReferenceValue = ipInput;
            touchSo.FindProperty("passcodeInput").objectReferenceValue = pinInput;
            touchSo.ApplyModifiedPropertiesWithoutUndo();

            string scenePath = $"{ScenesDir}/00_Bootstrap.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            return scenePath;
        }

        private static string BuildCoralWorldScene(
            Material terrainMat,
            Material oceanMat,
            Material coralMat,
            Material crystalMat,
            Material spaceMat,
            Mesh oceanGridMesh,
            Mesh crystalClusterMesh,
            GameObject soldierVisualPrefab,
            GameObject floorPrefab,
            GameObject lampPrefab,
            GameObject chestPrefab,
            GameObject woodNodePrefab,
            GameObject crystalNodePrefab,
            GameObject monsterPrefab,
            InventorySlotButtonUI slotBtnPrefab,
            Button recipeBtnPrefab,
            Sprite glassCardSprite,
            Sprite glassBtnSprite,
            Sprite glassCircleSprite)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera with Post-Processing & 2400m open-world horizon
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(0f, 6f, -9f);
            Camera mainCam = camGo.GetComponent<Camera>();
            mainCam.allowHDR = true;
            mainCam.allowMSAA = true;
            mainCam.nearClipPlane = 0.2f;
            mainCam.farClipPlane = 2400f;
            var camData = camGo.GetComponent<UniversalAdditionalCameraData>();
            if (camData == null)
                camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.requiresDepthOption = CameraOverrideOption.On;
            camData.requiresDepthTexture = true;
            camData.antialiasing = AntialiasingMode.None;

            // Directional Light (Sun / Moon controlled smoothly by WorldTimeManager)
            var lightGo = new GameObject("Directional Light", typeof(Light));
            Light sun = lightGo.GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.45f;
            sun.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(48f, 165f, 0f);

            // Skybox & Global Post-Processing Volume (ACES + Bloom)
            SetupTropicalSkybox();
            SetupGlobalPostProcessingVolume();

            // Single 3600m Ocean Surface at waterLevel = -2.2f
            var oceanGo = new GameObject("OceanSurface", typeof(MeshFilter), typeof(MeshRenderer));
            oceanGo.GetComponent<MeshFilter>().sharedMesh = oceanGridMesh;
            oceanGo.GetComponent<MeshRenderer>().sharedMaterial = oceanMat;
            oceanGo.transform.position = new Vector3(0f, -2.2f, 0f);

            // 2.000m x 2.000m Continent Generator (16x16 Chunks, 5 Biomes, Deterministic Streaming)
            var continentGo = new GameObject("IslandWorldUpgrader_2000m");
            var islandUpgrader = continentGo.AddComponent<IslandGraphicsUpgrader>();
            var upgraderSo = new SerializedObject(islandUpgrader);
            upgraderSo.FindProperty("existingOceanTransform").objectReferenceValue = oceanGo.transform;
            upgraderSo.FindProperty("continentTerrainMaterial").objectReferenceValue = terrainMat;
            upgraderSo.FindProperty("oceanWaterMaterial").objectReferenceValue = oceanMat;
            upgraderSo.FindProperty("glowingCrystalMaterial").objectReferenceValue = crystalMat;
            upgraderSo.FindProperty("playerVisualPrefab").objectReferenceValue = soldierVisualPrefab;
            upgraderSo.FindProperty("waterLevel").floatValue = -2.2f;
            upgraderSo.FindProperty("altarPrefab").objectReferenceValue = null;
            upgraderSo.FindProperty("chestPrefab").objectReferenceValue = chestPrefab;
            upgraderSo.FindProperty("lampPrefab").objectReferenceValue = lampPrefab;
            upgraderSo.FindProperty("floorPrefab").objectReferenceValue = floorPrefab;
            upgraderSo.ApplyModifiedPropertiesWithoutUndo();
            continentGo.AddComponent<IslandFastTravelAndRaftSystem>();
            continentGo.AddComponent<IslandRelicAndVolcanoBossSystem>();

            // Tidal Crystal Cave Gate positioned near starter lagoon coast
            float gateGroundY = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(18f, 11f);
            GameObject caveGateGo = InstantiateKayKitModel($"{KayKitSpaceDir}/structure_low.fbx", null, spaceMat);
            caveGateGo.name = "TidalCrystalCaveGate";
            caveGateGo.transform.position = new Vector3(18f, gateGroundY, 11f);
            caveGateGo.transform.rotation = Quaternion.Euler(0f, 35f, 0f);
            caveGateGo.transform.localScale = new Vector3(1.6f, 1.8f, 1.2f);
            BoxCollider gateCol = caveGateGo.AddComponent<BoxCollider>();
            gateCol.center = new Vector3(0f, 1.5f, 0f);
            gateCol.size = new Vector3(3.4f, 3.2f, 2.2f);
            var gateComp = caveGateGo.AddComponent<TidalCrystalCaveGate>();
            var gateSo = new SerializedObject(gateComp);
            gateSo.FindProperty("gateVisualTransform").objectReferenceValue = caveGateGo.transform;
            gateSo.FindProperty("gateCollider").objectReferenceValue = gateCol;
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

            // Island Core Altar (KayKit drill_structure + Glowing Crystal Spire + Warm/Cyan Point Light)
            float altarY = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(-4.5f, 4.5f);
            var altarRoot = new GameObject("IslandCoreAltar");
            altarRoot.transform.position = new Vector3(-4.5f, altarY, 4.5f);
            altarRoot.AddComponent<NetworkObject>();
            altarRoot.AddComponent<IslandCoreAltar>();

            BoxCollider altarCol = altarRoot.AddComponent<BoxCollider>();
            altarCol.center = new Vector3(0f, 1.6f, 0f);
            altarCol.size = new Vector3(2.8f, 3.2f, 2.8f);

            GameObject altarModel = InstantiateKayKitModel($"{KayKitSpaceDir}/drill_structure.fbx", altarRoot.transform, spaceMat);
            altarModel.transform.localScale = new Vector3(0.95f, 0.95f, 0.95f);

            var altarCrystalGo = new GameObject("AltarCoreCrystal", typeof(MeshFilter), typeof(MeshRenderer));
            altarCrystalGo.transform.SetParent(altarRoot.transform, false);
            altarCrystalGo.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            altarCrystalGo.transform.localScale = new Vector3(0.85f, 0.85f, 0.85f);
            altarCrystalGo.GetComponent<MeshFilter>().sharedMesh = crystalClusterMesh;
            altarCrystalGo.GetComponent<MeshRenderer>().sharedMaterial = crystalMat;

            var altarLightGo = new GameObject("AltarAuraLight", typeof(Light));
            altarLightGo.transform.SetParent(altarRoot.transform, false);
            altarLightGo.transform.localPosition = new Vector3(0f, 3.0f, 0f);
            Light altarLight = altarLightGo.GetComponent<Light>();
            altarLight.type = LightType.Point;
            altarLight.color = new Color(0.24f, 0.95f, 0.90f);
            altarLight.range = 18f;
            altarLight.intensity = 2.8f;
            altarLight.shadows = LightShadows.None;

            // Starter Camp Shared Chest sitting cleanly on flat camp ground
            float chestY = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(-1.5f, 4.5f);
            GameObject starterChest = (GameObject)PrefabUtility.InstantiatePrefab(chestPrefab);
            starterChest.transform.position = new Vector3(-1.5f, chestY, 4.5f);
            var chestSo = new SerializedObject(starterChest.GetComponent<SharedChestInventory>());
            chestSo.FindProperty("sceneFallbackChestGuid").stringValue = "starter_camp_chest_01";
            chestSo.ApplyModifiedPropertiesWithoutUndo();

            // Harvestable Tropical Palm Trees across Starter Bay & Ancient Rainforest
            Vector2[] woodCoords =
            {
                new Vector2(6f, 5f),
                new Vector2(-9f, -4f),
                new Vector2(9f, -9f),
                new Vector2(-12f, 10f),
                new Vector2(4f, 14f),
                new Vector2(14f, -4f),
                new Vector2(-180f, 35f),
                new Vector2(-210f, 65f)
            };
            for (int i = 0; i < woodCoords.Length; i++)
            {
                float wx = woodCoords[i].x;
                float wz = woodCoords[i].y;
                float wy = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(wx, wz);
                GameObject n = (GameObject)PrefabUtility.InstantiatePrefab(woodNodePrefab);
                n.transform.position = new Vector3(wx, wy, wz);
                n.transform.rotation = Quaternion.Euler(0f, i * 58f, 0f);
            }

            // Harvestable BioGlow Crystal Clusters across Starter Bay, Crystal Canyon, Mangrove Swamp & Volcanic Trail
            Vector2[] crystalCoords =
            {
                new Vector2(13f, -2f),
                new Vector2(-5f, -15f),
                new Vector2(-16f, 3f),
                new Vector2(32f, 20f),
                new Vector2(37f, 25f),
                new Vector2(375f, 38f),
                new Vector2(390f, 52f),
                new Vector2(255f, -255f)
            };
            for (int i = 0; i < crystalCoords.Length; i++)
            {
                float cx = crystalCoords[i].x;
                float cz = crystalCoords[i].y;
                float cy = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(cx, cz);
                GameObject n = (GameObject)PrefabUtility.InstantiatePrefab(crystalNodePrefab);
                n.transform.position = new Vector3(cx, cy, cz);
                n.transform.rotation = Quaternion.Euler(0f, i * 72f, 0f);
            }

            // Skeleton Guardian Monsters across starter zone & outer biomes
            Vector2[] monsterCoords =
            {
                new Vector2(12f, 12f),
                new Vector2(-13f, -12f),
                new Vector2(34f, 22f),
                new Vector2(370f, 42f),
                new Vector2(0f, 455f)
            };
            for (int i = 0; i < monsterCoords.Length; i++)
            {
                float mx = monsterCoords[i].x;
                float mz = monsterCoords[i].y;
                float my = IslandGraphicsUpgrader.EvaluateWorldHeightStatic(mx, mz);
                GameObject m = (GameObject)PrefabUtility.InstantiatePrefab(monsterPrefab);
                m.transform.position = new Vector3(mx, my, mz);
            }

            // Environment Controller (Ocean Tide + Day/Night Fog + Biolum Spore Particles)
            var envGo = new GameObject("EnvironmentVisuals");
            var envCtrl = envGo.AddComponent<BioluminescenceEnvironmentController>();
            var envSo = new SerializedObject(envCtrl);
            envSo.FindProperty("oceanSurfaceTransform").objectReferenceValue = oceanGo.transform;
            envSo.ApplyModifiedPropertiesWithoutUndo();

            // 3D Waypoint Arrow
            GameObject arrowGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(arrowGo.GetComponent<Collider>());
            arrowGo.name = "WaypointArrow3D";
            arrowGo.transform.localScale = new Vector3(0.22f, 0.14f, 0.75f);
            arrowGo.GetComponent<Renderer>().sharedMaterial = coralMat;

            CreateEventSystemIfMissing();
            BuildMobileHudCanvas(
                arrowGo.transform,
                slotBtnPrefab,
                recipeBtnPrefab,
                glassCardSprite,
                glassBtnSprite,
                glassCircleSprite);

            string scenePath = $"{ScenesDir}/01_CoralWorld.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            return scenePath;
        }

        private static void CreateTerrainObject(string name, Mesh mesh, Material mat, Vector3 position)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            go.transform.position = position;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.GetComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void SetupTropicalSkybox()
        {
            Shader skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader == null)
                return;

            string skyPath = $"{MaterialsDir}/MAT_CoralTropicalSky.mat";
            Material skyMat = GetOrCreateMaterialAsset(skyPath, skyShader);
            if (skyMat.HasProperty("_SunSize")) skyMat.SetFloat("_SunSize", 0.04f);
            if (skyMat.HasProperty("_AtmosphereThickness")) skyMat.SetFloat("_AtmosphereThickness", 0.85f);
            if (skyMat.HasProperty("_SkyTint")) skyMat.SetColor("_SkyTint", new Color(0.26f, 0.62f, 0.88f));
            if (skyMat.HasProperty("_GroundColor")) skyMat.SetColor("_GroundColor", new Color(0.06f, 0.18f, 0.28f));
            if (skyMat.HasProperty("_Exposure")) skyMat.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(skyMat);

            RenderSettings.skybox = skyMat;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.45f, 0.72f, 0.90f);
            RenderSettings.ambientEquatorColor = new Color(0.28f, 0.48f, 0.56f);
            RenderSettings.ambientGroundColor = new Color(0.14f, 0.22f, 0.28f);
        }

        private static void SetupGlobalPostProcessingVolume()
        {
            string profilePath = $"{ConfigsDir}/VP_CoralWorldPostProcess.asset";
            VolumeProfile existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            if (existing != null)
            {
                AssetDatabase.DeleteAsset(profilePath);
            }

            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);

            Tonemapping tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);
            AssetDatabase.AddObjectToAsset(tonemapping, profile);

            Bloom bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(0.85f);
            bloom.intensity.Override(1.1f);
            bloom.scatter.Override(0.7f);
            AssetDatabase.AddObjectToAsset(bloom, profile);

            ColorAdjustments colorAdj = profile.Add<ColorAdjustments>(true);
            colorAdj.postExposure.Override(0.15f);
            colorAdj.saturation.Override(14f);
            colorAdj.contrast.Override(18f);
            AssetDatabase.AddObjectToAsset(colorAdj, profile);

            Vignette vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.22f);
            vignette.smoothness.Override(0.35f);
            AssetDatabase.AddObjectToAsset(vignette, profile);

            EditorUtility.SetDirty(profile);

            var volGo = new GameObject("GlobalPostProcessVolume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 1f;
            vol.sharedProfile = profile;
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
            Button recipeBtnPrefab,
            Sprite glassCardSprite,
            Sprite glassBtnSprite,
            Sprite glassCircleSprite)
        {
            Canvas hud = CreateScreenCanvas("MobileHUD");
            var touchUi = hud.gameObject.AddComponent<MobileTouchInputUI>();
            var invGridUi = hud.gameObject.AddComponent<InventoryGridUI>();
            var craftUi = hud.gameObject.AddComponent<CraftingPanelUI>();
            var tutorialUi = hud.gameObject.AddComponent<OnboardingTutorialUI>();
            var diagUi = hud.gameObject.AddComponent<DiagnosticsAndTestOverlayUI>();
            var mapUi = hud.gameObject.AddComponent<WorldMinimapAndCompassUI>();

            // Top-Center Glassmorphism Vitals & Status Pill
            GameObject statusCard = CreateGlassPanel("StatusPillCard", hud.transform, glassCardSprite, new Color(0.05f, 0.11f, 0.20f, 0.72f));
            SetAnchorTopCenter(statusCard.GetComponent<RectTransform>(), 0f, -12f, 780f, 64f);
            Text statusText = CreateUiText("HudStatusText", statusCard.transform, "Connecting...", 16, TextAnchor.MiddleCenter, new Color(0.94f, 0.98f, 1.0f));
            StretchRect(statusText.rectTransform, 18f, 6f, 18f, 6f);

            // Top-Center Glassmorphism Compass Bar
            GameObject compassCard = CreateGlassPanel("CompassBarCard", hud.transform, glassCardSprite, new Color(0.04f, 0.12f, 0.22f, 0.70f));
            SetAnchorTopCenter(compassCard.GetComponent<RectTransform>(), 0f, -82f, 620f, 34f);
            Text compassText = CreateUiText("CompassHeadingText", compassCard.transform, "N 000\u00b0 | Ahead: Altar Camp (0m)", 14, TextAnchor.MiddleCenter, new Color(0.54f, 0.96f, 1.0f));
            StretchRect(compassText.rectTransform, 14f, 3f, 14f, 3f);

            // Top-Left Onboarding Tutorial Card
            GameObject tutPanel = CreateGlassPanel("TutorialPanelRoot", hud.transform, glassCardSprite, new Color(0.05f, 0.13f, 0.24f, 0.76f));
            SetAnchorTopLeft(tutPanel.GetComponent<RectTransform>(), 16f, -14f, 430f, 152f);

            Text tutTitle = CreateUiText("StepTitleText", tutPanel.transform, "Tutorial", 17, TextAnchor.UpperLeft, new Color(0.42f, 0.96f, 1.0f));
            SetRect(tutTitle.rectTransform, -42f, 46f, 320f, 30f);
            Text tutDesc = CreateUiText("StepDescriptionText", tutPanel.transform, "...", 14, TextAnchor.UpperLeft, new Color(0.92f, 0.96f, 1.0f));
            SetRect(tutDesc.rectTransform, 0f, -4f, 398f, 64f);
            Text tutProg = CreateUiText("StepProgressText", tutPanel.transform, "0/1", 14, TextAnchor.LowerLeft, new Color(1.0f, 0.90f, 0.45f));
            SetRect(tutProg.rectTransform, -90f, -52f, 220f, 26f);
            Button skipBtn = CreateGlassButton("BtnSkipTutorial", tutPanel.transform, "Skip", glassBtnSprite, new Color(0.55f, 0.24f, 0.28f, 0.85f), tutorialUi.OnClickSkipTutorial);
            SetRect(skipBtn.GetComponent<RectTransform>(), 160f, 50f, 78f, 30f);

            var tutSo = new SerializedObject(tutorialUi);
            tutSo.FindProperty("tutorialPanelRoot").objectReferenceValue = tutPanel;
            tutSo.FindProperty("stepTitleText").objectReferenceValue = tutTitle;
            tutSo.FindProperty("stepDescriptionText").objectReferenceValue = tutDesc;
            tutSo.FindProperty("stepProgressText").objectReferenceValue = tutProg;
            tutSo.FindProperty("worldWaypointArrow").objectReferenceValue = waypointArrow;
            tutSo.ApplyModifiedPropertiesWithoutUndo();

            // Top-Left Camp Log Frosted Card
            GameObject campLogCard = CreateGlassPanel("CampLogCard", hud.transform, glassCardSprite, new Color(0.04f, 0.11f, 0.20f, 0.68f));
            SetAnchorTopLeft(campLogCard.GetComponent<RectTransform>(), 16f, -176f, 430f, 96f);
            Text campLogText = CreateUiText("CampLogText", campLogCard.transform, "Camp Log...", 13, TextAnchor.UpperLeft, new Color(0.76f, 0.95f, 1.0f));
            StretchRect(campLogText.rectTransform, 14f, 10f, 14f, 10f);

            // Top-Left Glassmorphism 2000m Open-World Minimap
            GameObject minimapCard = CreateGlassPanel("MinimapCard", hud.transform, glassCardSprite, new Color(0.04f, 0.11f, 0.20f, 0.74f));
            SetAnchorTopLeft(minimapCard.GetComponent<RectTransform>(), 16f, -280f, 216f, 248f);

            Text minimapHeader = CreateUiText("MinimapTitleText", minimapCard.transform, "WORLD MAP 2000m", 12, TextAnchor.MiddleCenter, new Color(0.55f, 0.94f, 1.0f));
            SetRect(minimapHeader.rectTransform, 0f, 110f, 196f, 20f);

            var minimapViewportGo = new GameObject("MinimapViewport", typeof(RectTransform), typeof(RawImage), typeof(Mask));
            minimapViewportGo.transform.SetParent(minimapCard.transform, false);
            SetRect(minimapViewportGo.GetComponent<RectTransform>(), 0f, 4f, 184f, 184f);
            RawImage minimapRaw = minimapViewportGo.GetComponent<RawImage>();
            minimapRaw.color = Color.white;
            minimapViewportGo.GetComponent<Mask>().showMaskGraphic = true;

            RectTransform[] landmarkPins = new RectTransform[5];
            landmarkPins[0] = CreateMinimapPin(minimapViewportGo.transform, "Pin_AltarCamp", "Altar", new Color(0.24f, 0.96f, 0.90f, 1f), glassCircleSprite);
            landmarkPins[1] = CreateMinimapPin(minimapViewportGo.transform, "Pin_Rainforest", "Forest", new Color(0.32f, 0.88f, 0.36f, 1f), glassCircleSprite);
            landmarkPins[2] = CreateMinimapPin(minimapViewportGo.transform, "Pin_CrystalCanyon", "Crystal", new Color(0.30f, 0.80f, 1.00f, 1f), glassCircleSprite);
            landmarkPins[3] = CreateMinimapPin(minimapViewportGo.transform, "Pin_MangroveSwamp", "Swamp", new Color(0.68f, 0.88f, 0.38f, 1f), glassCircleSprite);
            landmarkPins[4] = CreateMinimapPin(minimapViewportGo.transform, "Pin_VolcanicTemple", "Volcano", new Color(1.00f, 0.52f, 0.18f, 1f), glassCircleSprite);

            GameObject playerPinGo = CreateGlassPanel("PlayerMarkerPin", minimapViewportGo.transform, glassCircleSprite, new Color(1.0f, 0.96f, 0.36f, 1f));
            RectTransform playerPinRt = playerPinGo.GetComponent<RectTransform>();
            playerPinRt.sizeDelta = new Vector2(14f, 14f);

            Text coordsBiomeText = CreateUiText("CoordsAndBiomeText", minimapCard.transform, "(0, 0) | Altar Coral Bay", 11, TextAnchor.MiddleCenter, new Color(0.90f, 0.96f, 1.0f));
            SetRect(coordsBiomeText.rectTransform, 0f, -108f, 200f, 22f);

            var mapSo = new SerializedObject(mapUi);
            mapSo.FindProperty("minimapMapImage").objectReferenceValue = minimapRaw;
            mapSo.FindProperty("playerMarkerRect").objectReferenceValue = playerPinRt;
            SerializedProperty pinsProp = mapSo.FindProperty("landmarkMarkerRects");
            pinsProp.arraySize = landmarkPins.Length;
            for (int i = 0; i < landmarkPins.Length; i++)
            {
                pinsProp.GetArrayElementAtIndex(i).objectReferenceValue = landmarkPins[i];
            }
            mapSo.FindProperty("coordsAndBiomeText").objectReferenceValue = coordsBiomeText;
            mapSo.FindProperty("compassHeadingText").objectReferenceValue = compassText;
            mapSo.ApplyModifiedPropertiesWithoutUndo();

            // Virtual Joystick (Bottom-Left Glass Circle)
            GameObject joyBg = CreateGlassPanel("JoystickBg", hud.transform, glassCircleSprite, new Color(0.75f, 0.94f, 1.0f, 0.24f));
            RectTransform joyBgRt = joyBg.GetComponent<RectTransform>();
            joyBgRt.anchorMin = joyBgRt.anchorMax = Vector2.zero;
            joyBgRt.anchoredPosition = new Vector2(145f, 145f);
            joyBgRt.sizeDelta = new Vector2(172f, 172f);

            GameObject joyHandle = CreateGlassPanel("JoystickHandle", joyBg.transform, glassCircleSprite, new Color(0.32f, 0.92f, 1.0f, 0.65f));
            joyHandle.GetComponent<RectTransform>().sizeDelta = new Vector2(76f, 76f);

            // Bottom-Right Glassmorphism Action Cluster
            CreateBottomRightGlassBtn("BtnAttack", hud.transform, "Attack", glassCircleSprite, -110f, 118f, new Vector2(124f, 124f), new Color(0.88f, 0.34f, 0.30f, 0.86f), touchUi.OnClickAttackOrHarvest);
            CreateBottomRightGlassBtn("BtnSprint", hud.transform, "Sprint", glassBtnSprite, -110f, 214f, new Vector2(126f, 46f), new Color(0.22f, 0.56f, 0.78f, 0.85f), touchUi.OnClickToggleSprint);
            CreateBottomRightGlassBtn("BtnAltar", hud.transform, "Altar Lv+", glassBtnSprite, -110f, 270f, new Vector2(126f, 46f), new Color(0.30f, 0.44f, 0.84f, 0.85f), touchUi.OnClickUpgradeIslandAltar);

            CreateBottomRightGlassBtn("BtnBag", hud.transform, "Bag (I)", glassBtnSprite, -258f, 92f, new Vector2(138f, 52f), new Color(0.16f, 0.48f, 0.68f, 0.86f), touchUi.OnClickToggleInventoryModal);
            CreateBottomRightGlassBtn("BtnCraft", hud.transform, "Craft (C)", glassBtnSprite, -258f, 152f, new Vector2(138f, 52f), new Color(0.18f, 0.62f, 0.50f, 0.86f), touchUi.OnClickToggleCraftingModal);
            CreateBottomRightGlassBtn("BtnTonic", hud.transform, "Tonic", glassBtnSprite, -258f, 212f, new Vector2(138f, 48f), new Color(0.16f, 0.68f, 0.56f, 0.86f), touchUi.OnClickUseCoralTonic);

            CreateBottomRightGlassBtn("BtnBuildFloor", hud.transform, "+ Floor", glassBtnSprite, -408f, 92f, new Vector2(132f, 46f), new Color(0.42f, 0.36f, 0.30f, 0.84f), touchUi.OnClickBuildFloor);
            CreateBottomRightGlassBtn("BtnBuildLamp", hud.transform, "+ Lamp", glassBtnSprite, -408f, 146f, new Vector2(132f, 46f), new Color(0.16f, 0.62f, 0.76f, 0.84f), touchUi.OnClickBuildGlowLamp);
            CreateBottomRightGlassBtn("BtnBuildChest", hud.transform, "+ Chest", glassBtnSprite, -408f, 200f, new Vector2(132f, 46f), new Color(0.52f, 0.38f, 0.24f, 0.84f), touchUi.OnClickBuildSharedChest);
            CreateBottomRightGlassBtn("BtnDemolish", hud.transform, "Demolish", glassBtnSprite, -408f, 254f, new Vector2(132f, 44f), new Color(0.58f, 0.24f, 0.26f, 0.84f), touchUi.OnClickDemolishNearestStructure);

            // Camp Note Input Bar (Bottom Center Glassmorphism)
            InputField noteInput = CreateInputField("CampNoteInput", hud.transform, "", "Leave note for camp...", glassBtnSprite);
            RectTransform noteRt = noteInput.GetComponent<RectTransform>();
            noteRt.anchorMin = noteRt.anchorMax = new Vector2(0.5f, 0f);
            noteRt.anchoredPosition = new Vector2(-60f, 36f);
            noteRt.sizeDelta = new Vector2(290f, 42f);

            Button postNoteBtn = CreateGlassButton("BtnPostNote", hud.transform, "Send", glassBtnSprite, new Color(0.18f, 0.54f, 0.68f, 0.90f), touchUi.OnClickPostCampNote);
            RectTransform postRt = postNoteBtn.GetComponent<RectTransform>();
            postRt.anchorMin = postRt.anchorMax = new Vector2(0.5f, 0f);
            postRt.anchoredPosition = new Vector2(145f, 36f);
            postRt.sizeDelta = new Vector2(105f, 42f);

            var touchSo = new SerializedObject(touchUi);
            touchSo.FindProperty("joystickBackground").objectReferenceValue = joyBgRt;
            touchSo.FindProperty("joystickHandle").objectReferenceValue = joyHandle.GetComponent<RectTransform>();
            touchSo.FindProperty("hudStatusText").objectReferenceValue = statusText;
            touchSo.FindProperty("campLogText").objectReferenceValue = campLogText;
            touchSo.FindProperty("campNoteInput").objectReferenceValue = noteInput;
            touchSo.ApplyModifiedPropertiesWithoutUndo();

            // Top-Right Collapsible Diagnostics & Simulator Overlay
            Button toggleDiagBtn = CreateGlassButton("BtnToggleDebug", hud.transform, "Debug [-]", glassBtnSprite, new Color(0.08f, 0.18f, 0.30f, 0.82f), diagUi.OnClickToggleOverlay);
            SetAnchorTopRight(toggleDiagBtn.GetComponent<RectTransform>(), -16f, -12f, 118f, 36f);

            GameObject diagRoot = CreateGlassPanel("DiagnosticsOverlayRoot", hud.transform, glassCardSprite, new Color(0.04f, 0.10f, 0.18f, 0.78f));
            SetAnchorTopRight(diagRoot.GetComponent<RectTransform>(), -16f, -54f, 465f, 136f);
            Text diagText = CreateUiText("DiagnosticsText", diagRoot.transform, "FPS: 60", 13, TextAnchor.UpperLeft, new Color(0.80f, 1.0f, 0.88f));
            SetRect(diagText.rectTransform, 0f, 22f, 438f, 72f);

            Button simAdd = CreateGlassButton("BtnSimAddP", diagRoot.transform, "+1 Player", glassBtnSprite, new Color(0.16f, 0.44f, 0.58f, 0.9f), diagUi.OnClickSimulateAddPlayer);
            SetRect(simAdd.GetComponent<RectTransform>(), -165f, -44f, 102f, 34f);
            Button simSub = CreateGlassButton("BtnSimSubP", diagRoot.transform, "-1 Player", glassBtnSprite, new Color(0.16f, 0.44f, 0.58f, 0.9f), diagUi.OnClickSimulateRemovePlayer);
            SetRect(simSub.GetComponent<RectTransform>(), -55f, -44f, 102f, 34f);
            Button simSkip = CreateGlassButton("BtnSimTime", diagRoot.transform, "Skip +3h", glassBtnSprite, new Color(0.48f, 0.38f, 0.16f, 0.9f), diagUi.OnClickSkipThreeWorldHours);
            SetRect(simSkip.GetComponent<RectTransform>(), 55f, -44f, 102f, 34f);
            Button simHeat = CreateGlassButton("BtnSimHeat", diagRoot.transform, "Heat Tier", glassBtnSprite, new Color(0.56f, 0.24f, 0.24f, 0.9f), diagUi.OnClickCycleSimulatedThermalLevel);
            SetRect(simHeat.GetComponent<RectTransform>(), 165f, -44f, 102f, 34f);

            var diagSo = new SerializedObject(diagUi);
            diagSo.FindProperty("overlayRoot").objectReferenceValue = diagRoot;
            diagSo.FindProperty("diagnosticsText").objectReferenceValue = diagText;
            diagSo.FindProperty("toggleButtonLabel").objectReferenceValue = toggleDiagBtn.GetComponentInChildren<Text>();
            diagSo.ApplyModifiedPropertiesWithoutUndo();

            BuildInventoryModal(hud.transform, invGridUi, slotBtnPrefab, glassCardSprite, glassBtnSprite);
            BuildCraftingModal(hud.transform, craftUi, recipeBtnPrefab, glassCardSprite, glassBtnSprite);
        }

        private static void BuildInventoryModal(
            Transform canvas,
            InventoryGridUI invGridUi,
            InventorySlotButtonUI slotBtnPrefab,
            Sprite glassCardSprite,
            Sprite glassBtnSprite)
        {
            GameObject modal = CreateGlassPanel("InventoryModalRoot", canvas, glassCardSprite, new Color(0.05f, 0.11f, 0.20f, 0.92f));
            modal.GetComponent<RectTransform>().sizeDelta = new Vector2(1180f, 620f);

            Text header = CreateUiText("HeaderTitleText", modal.transform, "Personal Bag <-> Shared Chest", 22, TextAnchor.MiddleLeft, Color.white);
            SetRect(header.rectTransform, -150f, 270f, 760f, 40f);

            Button modeBtn = CreateGlassButton("BtnTransferMode", modal.transform, "Transfer: x1", glassBtnSprite, new Color(0.18f, 0.50f, 0.68f, 0.95f), invGridUi.OnClickCycleTransferMode);
            SetRect(modeBtn.GetComponent<RectTransform>(), 340f, 270f, 170f, 42f);

            Button closeBtn = CreateGlassButton("BtnCloseInventory", modal.transform, "X", glassBtnSprite, new Color(0.66f, 0.22f, 0.24f, 0.95f), invGridUi.CloseModal);
            SetRect(closeBtn.GetComponent<RectTransform>(), 520f, 270f, 64f, 42f);

            GameObject playerGrid = CreateGridContainer("PlayerGridContainer", modal.transform, new Vector2(-300f, -20f), new Vector2(540f, 480f), 5);
            GameObject chestPanel = CreateGlassPanel("ChestAvailablePanel", modal.transform, glassCardSprite, new Color(0.08f, 0.16f, 0.26f, 0.85f));
            SetRect(chestPanel.GetComponent<RectTransform>(), 290f, -20f, 560f, 500f);

            GameObject chestGrid = CreateGridContainer("ChestGridContainer", chestPanel.transform, Vector2.zero, new Vector2(540f, 480f), 5);
            Text noChestHint = CreateUiText("NoChestHint", modal.transform, "Walk within 4.5m of a Shared Chest to deposit/withdraw.", 20, TextAnchor.MiddleCenter, new Color(0.8f, 0.88f, 0.95f));
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

        private static void BuildCraftingModal(
            Transform canvas,
            CraftingPanelUI craftUi,
            Button recipeBtnPrefab,
            Sprite glassCardSprite,
            Sprite glassBtnSprite)
        {
            GameObject modal = CreateGlassPanel("CraftingModalRoot", canvas, glassCardSprite, new Color(0.05f, 0.12f, 0.22f, 0.92f));
            modal.GetComponent<RectTransform>().sizeDelta = new Vector2(600f, 480f);

            Text hint = CreateUiText("NearbyChestHintText", modal.transform, "Co-op Crafting", 17, TextAnchor.MiddleLeft, new Color(0.45f, 0.94f, 1.0f));
            SetRect(hint.rectTransform, -45f, 200f, 450f, 40f);

            Button closeBtn = CreateGlassButton("BtnCloseCrafting", modal.transform, "X", glassBtnSprite, new Color(0.66f, 0.22f, 0.24f, 0.95f), craftUi.CloseModal);
            SetRect(closeBtn.GetComponent<RectTransform>(), 240f, 200f, 56f, 40f);

            var listGo = new GameObject("RecipeListContainer", typeof(RectTransform), typeof(VerticalLayoutGroup));
            listGo.transform.SetParent(modal.transform, false);
            SetRect(listGo.GetComponent<RectTransform>(), 0f, -10f, 520f, 300f);
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

        #region Glassmorphism UI Sprites & Primitives

        private static Sprite CreateGlassRoundedSprite(
            string fileName,
            int size,
            int cornerRadius,
            Color32 fillColor,
            Color32 borderColor,
            int borderThickness)
        {
            string pngPath = $"{UiSpritesDir}/{fileName}.png";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            float half = (size - 1) * 0.5f;
            float maxExtent = half - cornerRadius;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x - half) - maxExtent, 0f);
                    float dy = Mathf.Max(Mathf.Abs(y - half) - maxExtent, 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) - cornerRadius;

                    if (dist > 1.0f)
                    {
                        pixels[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float aa = Mathf.Clamp01(1.0f - dist);
                    bool isBorder = dist > -(borderThickness + 0.5f);
                    Color32 baseCol = isBorder ? borderColor : fillColor;

                    // Subtle top-down frosted sheen
                    float verticalSheen = (float)y / size;
                    byte r = (byte)Mathf.Clamp(baseCol.r + verticalSheen * 18f, 0, 255);
                    byte g = (byte)Mathf.Clamp(baseCol.g + verticalSheen * 22f, 0, 255);
                    byte b = (byte)Mathf.Clamp(baseCol.b + verticalSheen * 26f, 0, 255);
                    byte a = (byte)Mathf.RoundToInt(baseCol.a * aa);

                    pixels[y * size + x] = new Color32(r, g, b, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
            int border = cornerRadius + borderThickness + 2;
            TextureImporter importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spriteBorder = new Vector4(border, border, border, border);
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
        }

        private static Sprite CreateCircleSprite(
            string fileName,
            int size,
            Color32 fillColor,
            Color32 borderColor,
            int borderThickness)
        {
            string pngPath = $"{UiSpritesDir}/{fileName}.png";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            float center = (size - 1) * 0.5f;
            float radius = center - 1.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) - radius;

                    if (dist > 1.0f)
                    {
                        pixels[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float aa = Mathf.Clamp01(1.0f - dist);
                    Color32 baseCol = dist > -(borderThickness + 0.5f) ? borderColor : fillColor;
                    pixels[y * size + x] = new Color32(baseCol.r, baseCol.g, baseCol.b, (byte)Mathf.RoundToInt(baseCol.a * aa));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
            TextureImporter importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
        }

        private static void ApplySlicedSprite(Image img, Sprite sprite, Color tintColor)
        {
            img.color = tintColor;
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            }
        }

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

        private static GameObject CreateGlassPanel(string name, Transform parent, Sprite sprite, Color tintColor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            ApplySlicedSprite(go.GetComponent<Image>(), sprite, tintColor);
            return go;
        }

        private static RectTransform CreateMinimapPin(
            Transform viewport,
            string name,
            string shortLabel,
            Color pinColor,
            Sprite circleSprite)
        {
            GameObject pin = CreateGlassPanel(name, viewport, circleSprite, pinColor);
            RectTransform rt = pin.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(10f, 10f);

            Text label = CreateUiText("Label", pin.transform, shortLabel, 9, TextAnchor.LowerCenter, pinColor);
            RectTransform labelRt = label.rectTransform;
            labelRt.anchoredPosition = new Vector2(0f, -9f);
            labelRt.sizeDelta = new Vector2(54f, 14f);
            return rt;
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

        private static Button CreateGlassButton(
            string name,
            Transform parent,
            string label,
            Sprite sprite,
            Color tintColor,
            UnityAction onClickAction)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            ApplySlicedSprite(go.GetComponent<Image>(), sprite, tintColor);

            Text t = CreateUiText("Label", go.transform, label, 15, TextAnchor.MiddleCenter, Color.white);
            StretchRect(t.rectTransform, 6f, 4f, 6f, 4f);

            Button btn = go.GetComponent<Button>();
            if (onClickAction != null)
            {
                UnityEventTools.AddPersistentListener(btn.onClick, onClickAction);
            }
            return btn;
        }

        private static void CreateBottomRightGlassBtn(
            string name,
            Transform parent,
            string label,
            Sprite sprite,
            float offsetX,
            float offsetY,
            Vector2 size,
            Color tintColor,
            UnityAction action)
        {
            Button btn = CreateGlassButton(name, parent, label, sprite, tintColor, action);
            RectTransform rt = btn.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(offsetX, offsetY);
            rt.sizeDelta = size;
        }

        private static InputField CreateInputField(
            string name,
            Transform parent,
            string initialText,
            string placeholderText,
            Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            ApplySlicedSprite(go.GetComponent<Image>(), sprite, new Color(0.06f, 0.13f, 0.22f, 0.88f));

            Text ph = CreateUiText("Placeholder", go.transform, placeholderText, 15, TextAnchor.MiddleLeft, new Color(0.65f, 0.78f, 0.88f, 0.6f));
            StretchRect(ph.rectTransform, 14f, 4f, 14f, 4f);

            Text txt = CreateUiText("Text", go.transform, "", 15, TextAnchor.MiddleLeft, Color.white);
            txt.supportRichText = false;
            StretchRect(txt.rectTransform, 14f, 4f, 14f, 4f);

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

        [MenuItem("Coral Archipelago/Verify Custom Shaders Compilation")]
        public static void VerifyCustomShaders()
        {
            string[] shaderPaths =
            {
                "Assets/_Project/Shaders/IslandTerrainSplatURP.shader",
                "Assets/_Project/Shaders/TropicalLagoonWaterURP.shader",
                "Assets/_Project/Shaders/TropicalFoliageWindURP.shader"
            };

            bool anyError = false;
            Scene tempScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var camGo = GameObject.Find("Main Camera");
            Camera cam = camGo != null ? camGo.GetComponent<Camera>() : new GameObject("TempCam").AddComponent<Camera>();
            var rt = new RenderTexture(128, 128, 24, RenderTextureFormat.DefaultHDR);
            cam.targetTexture = rt;
            cam.transform.position = new Vector3(0f, 3f, -8f);
            cam.transform.LookAt(Vector3.zero);
            var camData = cam.GetComponent<UniversalAdditionalCameraData>() ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.requiresDepthOption = CameraOverrideOption.On;
            camData.requiresDepthTexture = true;

            for (int i = 0; i < shaderPaths.Length; i++)
            {
                Shader sh = AssetDatabase.LoadAssetAtPath<Shader>(shaderPaths[i]);
                if (sh == null)
                {
                    Debug.LogError($"[ShaderVerify] Missing shader at {shaderPaths[i]}");
                    anyError = true;
                    continue;
                }

                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                quad.transform.position = new Vector3((i - 1) * 2.5f, 0f, 0f);
                var mr = quad.GetComponent<MeshRenderer>();
                var mat = new Material(sh);
                mr.sharedMaterial = mat;

                cam.Render();

                int msgCount = ShaderUtil.GetShaderMessageCount(sh);
                ShaderMessage[] msgs = ShaderUtil.GetShaderMessages(sh);
                if (ShaderUtil.ShaderHasError(sh))
                    anyError = true;

                if (msgs != null)
                {
                    foreach (ShaderMessage m in msgs)
                    {
                        if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                        {
                            anyError = true;
                            Debug.LogError($"[ShaderVerify] {sh.name} line {m.line}: {m.message}");
                        }
                        else
                        {
                            Debug.LogWarning($"[ShaderVerify] {sh.name} warning line {m.line}: {m.message}");
                        }
                    }
                }

                Debug.Log($"[ShaderVerify] {sh.name} -> hasError={ShaderUtil.ShaderHasError(sh)}, messages={msgCount}");
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(quad);
            }

            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            EditorSceneManager.OpenScene($"{ScenesDir}/01_CoralWorld.unity", OpenSceneMode.Single);

            if (anyError)
                throw new InvalidOperationException("One or more custom URP shaders failed compilation.");
        }

        #endregion
    }
}
#endif
