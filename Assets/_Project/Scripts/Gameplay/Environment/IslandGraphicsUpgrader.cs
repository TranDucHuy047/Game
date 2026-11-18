using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Environment;
using CoralArchipelago.Gameplay.Harvesting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Gameplay.Visuals;
using CoralArchipelago.Networking;

/// <summary>
/// IslandGraphicsUpgrader (2.000m x 2.000m 5-Biome Continent, Central Lagoon Scenography & URP Post-Processing):
/// - Uses custom HLSL shaders: IslandTerrainSplatURP (4-layer Height+Slope+Multi-Freq Noise Splatting),
///   TropicalLagoonWaterURP (Turquoise #12D6DF -> Deep #064273 Depth Fade, Shoreline Foam, Sky Fresnel, Sun Glint),
///   and TropicalFoliageWindURP (Vertex Wind Animation + Soft Shadows).
/// - Carves a Central Turquoise Lagoon (Z = 10..180m) spanned by a walkable Natural Stone Arch Bridge,
///   framed by twin 8m-18m Mossy Cliff Ridges (West: Ancient Altar & Waterfall; East: HDR Blue Crystals),
///   dense segmented palm trees (11 drooping fronds) + understory ferns/grass/moss boulders.
/// - Enables URP Post-Processing (ACES Tonemapping, Bloom 0.85/1.1/0.7, ColorAdjustments, Vignette)
///   and 4-cascade Soft Shadows + Depth Texture automatically on any scene.
/// </summary>
[DisallowMultipleComponent]
public class IslandGraphicsUpgrader : MonoBehaviour
{
    public enum BiomeType
    {
        StarterCoralBay = 0,
        AncientRainforest = 1,
        CrystalCanyon = 2,
        MangroveSwamp = 3,
        VolcanicSummit = 4
    }

    public static IslandGraphicsUpgrader Instance { get; private set; }

    [Header("=== 1. PREFAB 3D ASSETS ===")]
    public GameObject[] treePrefabs;
    public GameObject[] crystalPrefabs;
    public GameObject altarPrefab;
    public GameObject chestPrefab;
    public GameObject lampPrefab;
    public GameObject floorPrefab;
    public GameObject playerVisualPrefab;
    public GameObject monsterVisualPrefab;

    [Header("=== 2. CONTINENT 2000M x 2000M & CHUNK STREAMING ===")]
    [Tooltip("Total open-world map width/length in meters.")]
    public float worldSize = 2000f;
    [Tooltip("Chunks per axis (16x16 = 256 chunks, 125m x 125m each).")]
    public int chunkGridCount = 16;
    [Tooltip("Vertex grid resolution per chunk.")]
    public int chunkVertexResolution = 28;
    [Tooltip("Max elevation of Northern Volcanic Summit (meters).")]
    public float maxMountainHeight = 135f;
    [Tooltip("Radius around local player where biome props remain active (meters).")]
    public float propStreamingRadius = 260f;
    [Tooltip("Radius around players where terrain MeshColliders are enabled (meters).")]
    public float colliderStreamingRadius = 420f;
    public float waterLevel = -2.2f;
    public int worldSeed = 20261118;

    [Header("=== 3. SCENE / SHADER WIRING & RETINA iOS ===")]
    [SerializeField] private Transform existingOceanTransform;
    [SerializeField] private Material continentTerrainMaterial;
    [SerializeField] private Material oceanWaterMaterial;
    [SerializeField] private Material glowingCrystalMaterial;
    [Range(0f, 24f)] public float currentHour = 9.4f;
    [Range(0f, 1f)] public float bioGlowIntensity = 0f;
    public bool lockRetinaRenderScale = true;

    private class WorldChunk
    {
        public Vector2Int coord;
        public Vector3 worldCenter;
        public BiomeType dominantBiome;
        public GameObject terrainObject;
        public MeshCollider terrainCollider;
        public GameObject propsRoot;
        public bool propsGenerated;
    }

    private readonly Dictionary<Vector2Int, WorldChunk> _chunks = new Dictionary<Vector2Int, WorldChunk>();
    private readonly List<Light> _nightPointLights = new List<Light>();
    private readonly HashSet<int> _upgradedInstanceIds = new HashSet<int>();
    private readonly List<Vector3> _colliderFocusPositions = new List<Vector3>(12);

    private bool _isHeadlessServer;
    private Transform _playerTransform;
    private Transform _oceanTransform;
    private Material _terrainMaterial;
    private Material _oceanMaterial;
    private Material _barkMaterial;
    private Material _palmLeafMaterial;
    private Material _jungleLeafMaterial;
    private Material _fernLeafMaterial;
    private Material _rockMaterial;
    private Material _mossRockMaterial;
    private Material _crystalMaterial;
    private Material _altarCoreMaterial;
    private Material _lampGlowMaterial;
    private Material _templePortalMaterial;
    private Material _waterfallMaterial;
    private Light _directionalLight;

    // Shared procedural meshes reused across all 256 chunks with GPU instancing
    private Mesh _sharedTrunkMesh;
    private Mesh _sharedPalmFrondMesh;
    private Mesh _sharedJungleCanopyMesh;
    private Mesh _sharedFernClusterMesh;
    private Mesh _sharedGrassTuftMesh;
    private Mesh _sharedCrystalShardMesh;
    private Mesh _sharedRockMesh;

    private Texture2D _sandNormal;
    private Texture2D _barkNormal;
    private Texture2D _mistSpriteTex;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _isHeadlessServer = NetworkBootstrap.IsRunningAsDedicatedServer();

        if (!_isHeadlessServer)
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            QualitySettings.shadows = UnityEngine.ShadowQuality.All;
            QualitySettings.shadowResolution = UnityEngine.ShadowResolution.High;
            QualitySettings.shadowDistance = 140f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.antiAliasing = 4;

            GenerateProceduralPBRTextures();
            BuildSharedMaterialsAndMeshes();
            SetupAtmosphereAndHorizonCamera();
            ForceSharpRetinaRenderScale();
        }

        // Build 16x16 terrain chunks, Far Lagoon, Arch Bridge, Cliffs & Colliders before players move
        BuildMassiveChunkedWorld();
    }

    private void Start()
    {
        if (!_isHeadlessServer)
        {
            SetupAtmosphereAndHorizonCamera();
            ForceSharpRetinaRenderScale();
            UpgradeExistingPrimitivesInScene();
        }

        SnapAllSceneObjectsToGround();
        DisableInvalidAnimatorsInScene();
        UpdateChunkStreamingAndUpgradePrimitives();
        InvokeRepeating(nameof(UpdateChunkStreamingAndUpgradePrimitives), 0.25f, 0.40f);
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Update()
    {
        if (_isHeadlessServer)
            return;

        UpdateDayNightAndBioGlow();
        UpdateOceanAndWaterfallAnimation(Time.time);

        if (lockRetinaRenderScale && Time.frameCount % 90 == 0)
        {
            ForceSharpRetinaRenderScale();
        }
    }

    // =========================================================================
    // 1. URP RETINA 1.0, 4-CASCADE SOFT SHADOWS & ACES + BLOOM POST-PROCESSING
    // =========================================================================
    private void ForceSharpRetinaRenderScale()
    {
        if (_isHeadlessServer)
            return;

        float targetScale = 1.0f;
        if (iOSGraphicsAndThermalManager.Instance != null)
        {
            AppleThermalLevel thermal = iOSGraphicsAndThermalManager.Instance.CurrentThermalLevel;
            if (thermal == AppleThermalLevel.Serious || thermal == AppleThermalLevel.Critical)
            {
                targetScale = 0.80f;
            }
        }

        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
        {
            urp.renderScale = targetScale;
            urp.supportsHDR = true;
            urp.supportsCameraDepthTexture = true;
            urp.msaaSampleCount = 4;
            urp.shadowDistance = 140f;
            urp.shadowCascadeCount = 4;
            return;
        }

        RenderPipelineAsset rpAsset = GraphicsSettings.currentRenderPipeline;
        if (rpAsset == null)
            return;

        Type urpType = rpAsset.GetType();
        PropertyInfo renderScaleProp = urpType.GetProperty("renderScale", BindingFlags.Public | BindingFlags.Instance);
        if (renderScaleProp != null && renderScaleProp.CanWrite)
            renderScaleProp.SetValue(rpAsset, targetScale);
    }

    private void SetupAtmosphereAndHorizonCamera()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.00075f;
        RenderSettings.ambientMode = AmbientMode.Trilight;

        if (_directionalLight == null)
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light l in lights)
            {
                if (l != null && l.type == LightType.Directional)
                {
                    _directionalLight = l;
                    break;
                }
            }
        }

        if (_directionalLight == null)
        {
            GameObject sunObj = new GameObject("Sun_DirectionalLight");
            _directionalLight = sunObj.AddComponent<Light>();
            _directionalLight.type = LightType.Directional;
        }

        _directionalLight.shadows = LightShadows.Soft;
        _directionalLight.shadowStrength = 0.82f;
        _directionalLight.shadowBias = 0.04f;
        _directionalLight.shadowNormalBias = 0.35f;

        if (Camera.main != null)
        {
            Camera cam = Camera.main;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 2400f;

            UniversalAdditionalCameraData camData = cam.GetComponent<UniversalAdditionalCameraData>();
            if (camData == null)
                camData = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();

            camData.renderPostProcessing = true;
            camData.requiresDepthOption = CameraOverrideOption.On;
            camData.requiresDepthTexture = true;
        }

        EnsureRuntimeGlobalPostProcessVolume();
    }

    private void EnsureRuntimeGlobalPostProcessVolume()
    {
        Volume existingVolume = FindFirstObjectByType<Volume>();
        if (existingVolume == null)
        {
            GameObject volGo = new GameObject("GlobalPostProcessVolume");
            volGo.transform.SetParent(transform, false);
            existingVolume = volGo.AddComponent<Volume>();
        }

        existingVolume.isGlobal = true;
        existingVolume.priority = 10f;

        VolumeProfile profile = existingVolume.sharedProfile;
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            existingVolume.sharedProfile = profile;
        }

        if (!profile.TryGet(out Tonemapping tonemapping))
            tonemapping = profile.Add<Tonemapping>(true);
        tonemapping.mode.Override(TonemappingMode.ACES);

        if (!profile.TryGet(out Bloom bloom))
            bloom = profile.Add<Bloom>(true);
        bloom.threshold.Override(0.85f);
        bloom.intensity.Override(1.1f);
        bloom.scatter.Override(0.7f);

        if (!profile.TryGet(out ColorAdjustments colorAdj))
            colorAdj = profile.Add<ColorAdjustments>(true);
        colorAdj.postExposure.Override(0.15f);
        colorAdj.contrast.Override(18f);
        colorAdj.saturation.Override(14f);

        if (!profile.TryGet(out Vignette vignette))
            vignette = profile.Add<Vignette>(true);
        vignette.intensity.Override(0.22f);
        vignette.smoothness.Override(0.35f);
    }

    // =========================================================================
    // 2. HLSL URP SHADERS, PROCEDURAL NORMALS & GPU-INSTANCED MATERIALS
    // =========================================================================
    private void GenerateProceduralPBRTextures()
    {
        _sandNormal = CreateNormalMapFromNoise(128, 14f, 2.1f);
        _barkNormal = CreateNormalMapFromNoise(128, 22f, 3.2f);

        // Soft circular alpha texture for waterfall mist droplets
        const int sz = 32;
        _mistSpriteTex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        float c = (sz - 1) * 0.5f;
        for (int y = 0; y < sz; y++)
        {
            for (int x = 0; x < sz; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) / c;
                float a = Mathf.Clamp01(1f - d * d);
                _mistSpriteTex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        }
        _mistSpriteTex.Apply();
    }

    private void BuildSharedMaterialsAndMeshes()
    {
        // Terrain: Always prefer IslandTerrainSplatURP for 4-layer Height+Slope+Multi-Freq Noise splatting
        Shader splatShader = Shader.Find("CoralArchipelago/URP/IslandTerrainSplatURP")
            ?? Shader.Find("CoralArchipelago/URP/CoralIslandTerrainURP");

        if (continentTerrainMaterial != null && continentTerrainMaterial.shader.name.Contains("IslandTerrainSplatURP"))
        {
            _terrainMaterial = new Material(continentTerrainMaterial) { enableInstancing = true };
        }
        else if (splatShader != null)
        {
            _terrainMaterial = new Material(splatShader) { enableInstancing = true };
        }
        else
        {
            _terrainMaterial = CreatePBRMaterial(new Color(0.86f, 0.82f, 0.72f), 0.04f, 0.26f);
        }

        // Moss-topped Cracked Rock material using same IslandTerrainSplatURP with low sand cutoff so flat boulder/bridge tops grow moss!
        if (splatShader != null)
        {
            _mossRockMaterial = new Material(splatShader) { enableInstancing = true };
            if (_mossRockMaterial.HasProperty("_WetSandHeight")) _mossRockMaterial.SetFloat("_WetSandHeight", -20f);
            if (_mossRockMaterial.HasProperty("_DrySandMaxHeight")) _mossRockMaterial.SetFloat("_DrySandMaxHeight", -10f);
            if (_mossRockMaterial.HasProperty("_HighRockHeight")) _mossRockMaterial.SetFloat("_HighRockHeight", 180f);
            if (_mossRockMaterial.HasProperty("_SlopeThreshold")) _mossRockMaterial.SetFloat("_SlopeThreshold", 0.68f);
            if (_mossRockMaterial.HasProperty("_UseVertexBiomeTint")) _mossRockMaterial.SetFloat("_UseVertexBiomeTint", 0.0f);
        }
        else
        {
            _mossRockMaterial = CreatePBRMaterial(new Color(0.28f, 0.34f, 0.28f), 0.08f, 0.35f);
        }

        // Lagoon Water: Always prefer TropicalLagoonWaterURP (#12D6DF shallow turquoise -> #064273 deep ocean + shoreline foam)
        Shader lagoonShader = Shader.Find("CoralArchipelago/URP/TropicalLagoonWaterURP")
            ?? Shader.Find("CoralArchipelago/URP/CoralOceanWaterURP");

        if (oceanWaterMaterial != null && oceanWaterMaterial.shader.name.Contains("TropicalLagoonWaterURP"))
        {
            _oceanMaterial = oceanWaterMaterial;
        }
        else if (lagoonShader != null)
        {
            _oceanMaterial = new Material(lagoonShader);
        }
        else
        {
            _oceanMaterial = CreatePBRMaterial(new Color(0.07f, 0.84f, 0.88f, 0.82f), 0.18f, 0.92f, transparent: true);
        }

        if (_oceanMaterial != null)
        {
            _oceanMaterial.enableInstancing = true;
            if (_oceanMaterial.HasProperty("_WaveAmplitude")) _oceanMaterial.SetFloat("_WaveAmplitude", 0.03f);
            if (_oceanMaterial.HasProperty("_SpecularStrength")) _oceanMaterial.SetFloat("_SpecularStrength", 0.18f);
        }

        // Foliage shaders with GPU vertex wind animation
        Shader windShader = Shader.Find("CoralArchipelago/URP/TropicalFoliageWindURP");
        _palmLeafMaterial = CreateFoliageWindMaterial(windShader, new Color(0.16f, 0.52f, 0.19f), new Color(0.38f, 0.72f, 0.22f), 2.1f, 0.22f);
        _jungleLeafMaterial = CreateFoliageWindMaterial(windShader, new Color(0.10f, 0.38f, 0.14f), new Color(0.24f, 0.58f, 0.18f), 1.6f, 0.16f);
        _fernLeafMaterial = CreateFoliageWindMaterial(windShader, new Color(0.14f, 0.48f, 0.16f), new Color(0.34f, 0.68f, 0.22f), 2.6f, 0.12f);

        _barkMaterial = CreatePBRMaterial(new Color(0.36f, 0.24f, 0.14f), 0.02f, 0.18f);
        ApplyNormalMap(_barkMaterial, _barkNormal, 6f, 1.4f);

        _rockMaterial = CreatePBRMaterial(new Color(0.30f, 0.33f, 0.37f), 0.10f, 0.34f);
        ApplyNormalMap(_rockMaterial, _sandNormal, 6f, 1.2f);

        _crystalMaterial = glowingCrystalMaterial != null
            ? new Material(glowingCrystalMaterial) { enableInstancing = true }
            : CreatePBRMaterial(
                new Color(0.14f, 0.84f, 1.0f),
                0.35f,
                0.92f,
                emissionColor: new Color(0.12f, 1.15f, 1.65f) * 2.2f);

        _altarCoreMaterial = CreatePBRMaterial(
            new Color(0.16f, 0.98f, 0.82f),
            0.25f,
            0.95f,
            emissionColor: new Color(0.18f, 1.15f, 0.95f) * 2.4f);

        _lampGlowMaterial = CreatePBRMaterial(
            new Color(1.0f, 0.78f, 0.32f),
            0.1f,
            0.9f,
            emissionColor: new Color(1.0f, 0.78f, 0.32f) * 2.4f);

        _templePortalMaterial = CreatePBRMaterial(
            new Color(1.0f, 0.48f, 0.16f),
            0.3f,
            0.9f,
            emissionColor: new Color(1.0f, 0.42f, 0.10f) * 2.8f);

        _waterfallMaterial = CreatePBRMaterial(
            new Color(0.45f, 0.92f, 1.0f, 0.78f),
            0.05f,
            0.88f,
            transparent: true,
            emissionColor: new Color(0.20f, 0.75f, 0.90f) * 0.9f);

        // Shared detailed procedural meshes
        _sharedTrunkMesh = CreateSegmentedCurvedPalmTrunkMesh(10, 0.26f, 0.12f, 4.4f);
        _sharedPalmFrondMesh = CreateDetailedPalmFrondMesh();
        _sharedJungleCanopyMesh = CreateTaperedCylinderMesh(9, 2.3f, 0.25f, 2.7f);
        _sharedFernClusterMesh = CreateFernClusterMesh();
        _sharedGrassTuftMesh = CreateGrassTuftMesh();
        _sharedCrystalShardMesh = CreateHexCrystalMesh(0.28f, 1.50f);
        _sharedRockMesh = CreateLowPolyRockMesh(0.88f);
    }

    private Material CreateFoliageWindMaterial(Shader windShader, Color baseCol, Color tipCol, float speed, float amplitude)
    {
        if (windShader != null)
        {
            Material m = new Material(windShader) { enableInstancing = true };
            m.SetColor("_BaseColor", baseCol);
            m.SetColor("_TipColor", tipCol);
            m.SetFloat("_WindSpeed", speed);
            m.SetFloat("_WindAmplitude", amplitude);
            return m;
        }
        return CreatePBRMaterial(baseCol, 0.04f, 0.40f);
    }

    // =========================================================================
    // 3. 2000M x 2000M CONTINENT + CENTRAL LAGOON & SCENOGRAPHY
    // =========================================================================
    private void BuildMassiveChunkedWorld()
    {
        if (!_isHeadlessServer)
        {
            SetupGiantOceanSurface();
        }

        float chunkSize = worldSize / chunkGridCount;
        float halfWorld = worldSize * 0.5f;
        float colliderRadiusSq = colliderStreamingRadius * colliderStreamingRadius;

        GameObject chunksRoot = new GameObject("World_Terrain_Chunks_2000m");
        chunksRoot.transform.SetParent(transform, false);

        for (int cz = 0; cz < chunkGridCount; cz++)
        {
            for (int cx = 0; cx < chunkGridCount; cx++)
            {
                Vector2Int coord = new Vector2Int(cx, cz);
                float originX = -halfWorld + cx * chunkSize;
                float originZ = -halfWorld + cz * chunkSize;
                Vector3 center = new Vector3(originX + chunkSize * 0.5f, 0f, originZ + chunkSize * 0.5f);

                EvaluateBiomeAndHeight(center.x, center.z, out BiomeType centerBiome, out _);

                GameObject chunkGo = new GameObject($"Chunk_{cx}_{cz}_{centerBiome}");
                chunkGo.transform.SetParent(chunksRoot.transform, false);
                chunkGo.transform.position = new Vector3(originX, 0f, originZ);

                Mesh chunkMesh = GenerateChunkTerrainMesh(originX, originZ, chunkSize, chunkVertexResolution);

                if (!_isHeadlessServer)
                {
                    MeshFilter mf = chunkGo.AddComponent<MeshFilter>();
                    MeshRenderer mr = chunkGo.AddComponent<MeshRenderer>();
                    mf.sharedMesh = chunkMesh;
                    mr.sharedMaterial = _terrainMaterial;
                }

                MeshCollider mc = chunkGo.AddComponent<MeshCollider>();
                bool initialColliderActive = (center.x * center.x + center.z * center.z) <= colliderRadiusSq;
                mc.enabled = initialColliderActive;
                mc.sharedMesh = chunkMesh;

                _chunks[coord] = new WorldChunk
                {
                    coord = coord,
                    worldCenter = center,
                    dominantBiome = centerBiome,
                    terrainObject = chunkGo,
                    terrainCollider = mc,
                    propsGenerated = false
                };
            }
        }

        // Build Central Lagoon Natural Stone Arch Bridge (collider runs on both server and client!)
        BuildNaturalStoneArchBridge();

        if (!_isHeadlessServer)
        {
            BuildCentralLagoonScenographyLandmarks();
            BuildNorthernSummitTempleLandmark(new Vector3(0f, EvaluateHeightOnly(0f, 480f), 480f));
        }

        SnapAllSceneObjectsToGround();
    }

    private void SetupGiantOceanSurface()
    {
        if (existingOceanTransform != null)
        {
            _oceanTransform = existingOceanTransform;
        }
        else
        {
            GameObject existingGo = GameObject.Find("OceanSurface") ?? GameObject.Find("Massive_Ocean_Surface");
            if (existingGo != null)
                _oceanTransform = existingGo.transform;
        }

        // Remove any duplicate ocean surfaces in Hierarchy so only ONE OceanSurface exists
        MeshRenderer[] allRenderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        for (int i = 0; i < allRenderers.Length; i++)
        {
            MeshRenderer r = allRenderers[i];
            if (r == null || r.transform == _oceanTransform)
                continue;

            string lowerName = r.gameObject.name.ToLowerInvariant();
            bool isWaterShader = r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                                 r.sharedMaterial.shader.name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0;
            if (lowerName == "oceansurface" || lowerName == "massive_ocean_surface" || isWaterShader)
            {
                if (_oceanTransform == null)
                {
                    _oceanTransform = r.transform;
                }
                else
                {
                    Destroy(r.gameObject);
                }
            }
        }

        Mesh largeOceanMesh = GenerateSubdividedPlaneMesh(120, worldSize * 1.8f);

        if (_oceanTransform == null)
        {
            GameObject oceanObj = new GameObject("OceanSurface");
            _oceanTransform = oceanObj.transform;
        }

        _oceanTransform.position = new Vector3(0f, waterLevel, 0f);
        MeshFilter of = _oceanTransform.GetComponent<MeshFilter>() ?? _oceanTransform.gameObject.AddComponent<MeshFilter>();
        of.sharedMesh = largeOceanMesh;

        MeshRenderer or = _oceanTransform.GetComponent<MeshRenderer>() ?? _oceanTransform.gameObject.AddComponent<MeshRenderer>();
        or.sharedMaterial = _oceanMaterial;
        or.shadowCastingMode = ShadowCastingMode.Off;
    }

    private void BuildNaturalStoneArchBridge()
    {
        // Connects South Lagoon Shore (Z = 72m) to North Lagoon Peninsula (Z = 148m) across far lagoon (Z = 75..145m)
        const float startZ = 72f;
        const float endZ = 148f;
        const int lengthSegs = 28;
        const float halfWidth = 2.75f;

        GameObject bridgeGo = new GameObject("Natural_Stone_Arch_Bridge");
        bridgeGo.transform.SetParent(transform, false);
        bridgeGo.transform.position = Vector3.zero;

        Mesh bridgeMesh = CreateStoneArchBridgeMesh(startZ, endZ, lengthSegs, halfWidth);
        MeshCollider mc = bridgeGo.AddComponent<MeshCollider>();
        mc.sharedMesh = bridgeMesh;

        if (!_isHeadlessServer)
        {
            MeshFilter mf = bridgeGo.AddComponent<MeshFilter>();
            MeshRenderer mr = bridgeGo.AddComponent<MeshRenderer>();
            mf.sharedMesh = bridgeMesh;
            mr.sharedMaterial = _mossRockMaterial;

            // Stone abutments & arch piers standing in lagoon water so Shoreline Foam rims their bases
            float[] pierZs = { 90f, 110f, 130f };
            for (int i = 0; i < pierZs.Length; i++)
            {
                float pz = pierZs[i];
                for (int side = -1; side <= 1; side += 2)
                {
                    GameObject pier = CreateMeshPart($"BridgePier_{i}_{side}", bridgeGo.transform, _sharedRockMesh, _mossRockMaterial);
                    pier.transform.localPosition = new Vector3(side * 2.5f, waterLevel - 0.3f, pz);
                    pier.transform.localScale = new Vector3(1.6f, 4.4f, 2.0f);
                }
            }

            // Rustic stone parapet boulders & lanterns at bridge ends
            for (int zStep = 78; zStep <= 142; zStep += 16)
            {
                float archT = Mathf.InverseLerp(startZ, endZ, zStep);
                float deckY = SampleBridgeDeckHeight(archT, zStep);
                for (int side = -1; side <= 1; side += 2)
                {
                    GameObject railRock = CreateMeshPart($"BridgeParapet_{zStep}_{side}", bridgeGo.transform, _sharedRockMesh, _mossRockMaterial);
                    railRock.transform.localPosition = new Vector3(side * 2.4f, deckY + 0.15f, zStep);
                    railRock.transform.localRotation = Quaternion.Euler(0f, zStep * 17f + side * 35f, 0f);
                    railRock.transform.localScale = new Vector3(0.62f, 0.48f, 0.85f);
                }
            }
        }
    }

    private float SampleBridgeDeckHeight(float t, float z)
    {
        float southY = EvaluateHeightOnly(0f, 72f);
        float northY = EvaluateHeightOnly(0f, 148f);
        float baseline = Mathf.Lerp(southY, northY, t);
        float archLift = Mathf.Sin(t * Mathf.PI) * 4.4f;
        return baseline + archLift;
    }

    private Mesh CreateStoneArchBridgeMesh(float startZ, float endZ, int segs, float halfW)
    {
        Mesh m = new Mesh { name = "NaturalStoneArchBridge" };
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var cols = new List<Color>();
        var tris = new List<int>();

        // 4 vertices per cross section: top-left, top-right, bottom-left, bottom-right
        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            float z = Mathf.Lerp(startZ, endZ, t);
            float topY = SampleBridgeDeckHeight(t, z);
            float thickness = Mathf.Lerp(1.6f, 0.95f, Mathf.Sin(t * Mathf.PI));
            float botY = topY - thickness;
            float curveW = halfW + Mathf.Sin(t * Mathf.PI * 3f) * 0.25f;

            verts.Add(new Vector3(-curveW, topY, z));
            verts.Add(new Vector3(curveW, topY, z));
            verts.Add(new Vector3(-curveW * 1.15f, botY, z));
            verts.Add(new Vector3(curveW * 1.15f, botY, z));

            for (int k = 0; k < 4; k++)
            {
                uvs.Add(new Vector2(k * 0.33f, t * 8f));
                cols.Add(new Color(0.28f, 0.34f, 0.28f, 1f));
            }
        }

        for (int i = 0; i < segs; i++)
        {
            int c0 = i * 4;
            int c1 = (i + 1) * 4;

            // Top walkable surface (facing +Y)
            tris.Add(c0 + 0); tris.Add(c1 + 0); tris.Add(c0 + 1);
            tris.Add(c0 + 1); tris.Add(c1 + 0); tris.Add(c1 + 1);

            // Left cliff side wall
            tris.Add(c0 + 2); tris.Add(c1 + 2); tris.Add(c0 + 0);
            tris.Add(c0 + 0); tris.Add(c1 + 2); tris.Add(c1 + 0);

            // Right cliff side wall
            tris.Add(c0 + 1); tris.Add(c1 + 1); tris.Add(c0 + 3);
            tris.Add(c0 + 3); tris.Add(c1 + 1); tris.Add(c1 + 3);

            // Underside arch ceiling
            tris.Add(c0 + 3); tris.Add(c1 + 3); tris.Add(c0 + 2);
            tris.Add(c0 + 2); tris.Add(c1 + 3); tris.Add(c1 + 2);
        }

        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetColors(cols);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private void BuildCentralLagoonScenographyLandmarks()
    {
        GameObject scenoRoot = new GameObject("Central_Lagoon_Scenography");
        scenoRoot.transform.SetParent(transform, false);

        // 1. East Cliff HDR Blue Crystal Outcrops (Right Cliff Wall overlooking Far Lagoon Z=75..145m)
        Vector2[] eastCrystalCoords =
        {
            new Vector2(41f, 84f),
            new Vector2(44f, 96f),
            new Vector2(47f, 108f),
            new Vector2(46f, 120f),
            new Vector2(43f, 132f)
        };
        for (int i = 0; i < eastCrystalCoords.Length; i++)
        {
            float cx = eastCrystalCoords[i].x;
            float cz = eastCrystalCoords[i].y;
            float cy = EvaluateHeightOnly(cx, cz);

            GameObject cluster = new GameObject("EastCliff_CrystalOutcrop_" + i);
            cluster.transform.SetParent(scenoRoot.transform, false);
            cluster.transform.position = new Vector3(cx, cy - 0.3f, cz);
            cluster.transform.rotation = Quaternion.Euler(0f, i * 48f, -12f);
            cluster.transform.localScale = Vector3.one * (2.1f + (i % 3) * 0.55f);
            BuildFastCrystalCluster(cluster.transform);

            if (i % 2 == 0)
            {
                AddBioGlowLight(cluster.transform, new Vector3(-0.6f, 1.4f, 0f), new Color(0.14f, 0.86f, 1.0f), 16f);
            }
        }

        // 2. West Cliff Ancient Rune Altar & Cascading Tropical Waterfall (Left Cliff Wall at Z=98..112m)
        float altarCliffX = -46f;
        float altarCliffZ = 98f;
        float altarCliffY = EvaluateHeightOnly(altarCliffX, altarCliffZ);

        GameObject westShrine = new GameObject("WestCliff_Ancient_Rune_Altar");
        westShrine.transform.SetParent(scenoRoot.transform, false);
        westShrine.transform.position = new Vector3(altarCliffX, altarCliffY, altarCliffZ);

        for (int p = 0; p < 4; p++)
        {
            float a = p * 90f * Mathf.Deg2Rad;
            GameObject obelisk = CreateMeshPart($"CliffRunePillar_{p}", westShrine.transform, _sharedTrunkMesh, _mossRockMaterial);
            obelisk.transform.localPosition = new Vector3(Mathf.Cos(a) * 2.6f, -0.4f, Mathf.Sin(a) * 2.6f);
            obelisk.transform.localScale = new Vector3(1.5f, 0.9f, 1.5f);
        }
        GameObject runeCore = CreateMeshPart("CliffAltarRuneCore", westShrine.transform, _sharedCrystalShardMesh, _altarCoreMaterial);
        runeCore.transform.localPosition = new Vector3(0f, 1.8f, 0f);
        runeCore.transform.localScale = Vector3.one * 1.45f;
        AddBioGlowLight(westShrine.transform, new Vector3(0f, 2.4f, 0f), new Color(0.18f, 0.96f, 0.85f), 18f);

        // Cascading Waterfall pouring off West Cliff into the Far Turquoise Lagoon
        BuildWestCliffWaterfall(scenoRoot.transform, new Vector3(-40f, EvaluateHeightOnly(-40f, 112f), 112f), new Vector3(-24f, waterLevel, 112f));

        // 3. Lush Starter Camp Palm & Fern Grove around the Dry Sandy Camp (radius 65m)
        Vector3[] starterPalmPositions =
        {
            new Vector3(-16f, 0f, -14f),
            new Vector3(-24f, 0f, -6f),
            new Vector3(-19f, 0f, 6f),
            new Vector3(15f, 0f, -15f),
            new Vector3(23f, 0f, -5f),
            new Vector3(18f, 0f, 8f),
            new Vector3(-26f, 0f, 26f),
            new Vector3(26f, 0f, 28f),
            new Vector3(-18f, 0f, 44f),
            new Vector3(20f, 0f, 46f)
        };

        GameObject groveRoot = new GameObject("StarterBeach_Palm_Fern_Grove");
        groveRoot.transform.SetParent(scenoRoot.transform, false);
        for (int i = 0; i < starterPalmPositions.Length; i++)
        {
            Vector3 p = starterPalmPositions[i];
            float y = EvaluateHeightOnly(p.x, p.z);
            if (y < waterLevel + 0.5f) continue;

            GameObject palm = new GameObject($"HeroPalmTree_{i}");
            palm.transform.SetParent(groveRoot.transform, false);
            palm.transform.position = new Vector3(p.x, y, p.z);
            palm.transform.rotation = Quaternion.Euler(0f, i * 67f + 22f, 0f);
            palm.transform.localScale = Vector3.one * (0.95f + (i % 3) * 0.14f);
            BuildFastPalmTree(palm.transform);
        }

        // Scatter extra fern clusters & mossy rocks across the Starter Sandy Camp
        System.Random rng = new System.Random(worldSeed ^ 90125);
        for (int i = 0; i < 42; i++)
        {
            float sx = Mathf.Lerp(-44f, 44f, (float)rng.NextDouble());
            float sz = Mathf.Lerp(-28f, 52f, (float)rng.NextDouble());
            // Keep immediate spawn path (0, -8) & central bridge path clear
            if (Mathf.Abs(sx) < 5f && sz > -14f && sz < 10f) continue;

            float sy = EvaluateHeightOnly(sx, sz);
            if (sy < waterLevel + 0.5f) continue;

            GameObject deco = new GameObject($"BeachUnderstory_{i}");
            deco.transform.SetParent(groveRoot.transform, false);
            deco.transform.position = new Vector3(sx, sy, sz);
            deco.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

            if (i % 3 == 0)
            {
                GameObject boulder = CreateMeshPart("BeachMossRock", deco.transform, _sharedRockMesh, _mossRockMaterial);
                float rScale = 0.7f + (float)rng.NextDouble() * 0.9f;
                boulder.transform.localScale = new Vector3(rScale * 1.25f, rScale * 0.72f, rScale * 1.1f);
            }
            else
            {
                GameObject fern = CreateMeshPart("BeachFern", deco.transform, _sharedFernClusterMesh, _fernLeafMaterial);
                fern.transform.localScale = Vector3.one * (0.85f + (float)rng.NextDouble() * 0.65f);
            }
        }

        MarkHierarchyUpgraded(scenoRoot);
    }

    private void BuildWestCliffWaterfall(Transform parent, Vector3 topEdge, Vector3 poolSplash)
    {
        GameObject wfRoot = new GameObject("WestCliff_Tropical_Waterfall");
        wfRoot.transform.SetParent(parent, false);

        Vector3 mid = (topEdge + poolSplash) * 0.5f;
        Vector3 delta = poolSplash - topEdge;
        float length = delta.magnitude;

        GameObject sheet = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(sheet.GetComponent<Collider>());
        sheet.name = "WaterfallStream";
        sheet.transform.SetParent(wfRoot.transform, false);
        sheet.transform.position = mid + Vector3.up * 0.4f;
        sheet.transform.rotation = Quaternion.LookRotation(-new Vector3(delta.x, 0f, delta.z).normalized, Vector3.up) * Quaternion.Euler(28f, 0f, 0f);
        sheet.transform.localScale = new Vector3(4.2f, length * 1.08f, 1f);
        sheet.GetComponent<MeshRenderer>().sharedMaterial = _waterfallMaterial;

        // White foam mist ParticleSystem at splash pool
        GameObject mistGo = new GameObject("WaterfallSplashMist", typeof(ParticleSystem));
        mistGo.transform.SetParent(wfRoot.transform, false);
        mistGo.transform.position = poolSplash + Vector3.up * 0.35f;

        ParticleSystem ps = mistGo.GetComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 1.6f;
        main.startSpeed = 2.4f;
        main.startSize = 1.8f;
        main.startColor = new Color(0.82f, 0.97f, 1.0f, 0.52f);
        main.maxParticles = 45;

        var emission = ps.emission;
        emission.rateOverTime = 22f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 2.8f;

        ParticleSystemRenderer pr = mistGo.GetComponent<ParticleSystemRenderer>();
        Shader partShader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
        if (partShader != null)
        {
            Material pMat = new Material(partShader);
            if (_mistSpriteTex != null && pMat.HasProperty("_BaseMap")) pMat.SetTexture("_BaseMap", _mistSpriteTex);
            if (_mistSpriteTex != null && pMat.HasProperty("_MainTex")) pMat.SetTexture("_MainTex", _mistSpriteTex);
            pr.sharedMaterial = pMat;
        }
    }

    /// <summary>
    /// Evaluates height and biome at (worldX, worldZ) across the 2000m x 2000m continent.
    /// Carves the Central Turquoise Lagoon (Z = 10..180m) framed by West/East 8m-18m Mossy Cliff Ridges
    /// while preserving dry flat sand at Player Spawn & Starter Camp (Z = -28..+8m).
    /// </summary>
    public static void EvaluateWorldBiomeAndHeightStatic(
        float worldX,
        float worldZ,
        float worldSize,
        float maxMountainHeight,
        out BiomeType biome,
        out Color vertexColor)
    {
        EvaluateWorldCoreStatic(worldX, worldZ, worldSize, maxMountainHeight, true, out _, out biome, out vertexColor);
    }

    public static float EvaluateWorldHeightStatic(
        float worldX,
        float worldZ,
        float worldSize = 2000f,
        float maxMountainHeight = 135f)
    {
        EvaluateWorldCoreStatic(worldX, worldZ, worldSize, maxMountainHeight, false, out float finalHeight, out _, out _);
        return finalHeight;
    }

    public void EvaluateBiomeAndHeight(float worldX, float worldZ, out BiomeType biome, out Color vertexColor)
    {
        EvaluateWorldCoreStatic(worldX, worldZ, worldSize, maxMountainHeight, true, out _, out biome, out vertexColor);
    }

    public float EvaluateHeightOnly(float worldX, float worldZ)
    {
        EvaluateWorldCoreStatic(worldX, worldZ, worldSize, maxMountainHeight, false, out float h, out _, out _);
        return h;
    }

    private static float SmoothStepRange(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / Mathf.Max(0.0001f, edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static void EvaluateWorldCoreStatic(
        float worldX,
        float worldZ,
        float mapSize,
        float peakHeight,
        bool evaluateVisuals,
        out float finalHeight,
        out BiomeType biome,
        out Color vertexColor)
    {
        float half = mapSize * 0.5f;
        float distFromCenter = Mathf.Sqrt(worldX * worldX + worldZ * worldZ);
        float normalizedRadius = distFromCenter / (half * 0.92f);

        float warpWeight = SmoothStepRange(0.15f, 0.55f, normalizedRadius);
        float coastlineWarp = (Mathf.PerlinNoise(worldX * 0.0018f + 3.1f, worldZ * 0.0018f + 7.4f) - 0.5f) * 0.22f * warpWeight;
        float islandMask = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Max(0f, normalizedRadius + coastlineWarp), 2.15f));

        if (islandMask <= 0.05f)
        {
            finalHeight = -4.5f;
        }
        else
        {
            // Main camp dry white-gold sand plateau within 65m radius of (0,0): strictly +1.2m to +3.5m (>= 3.4m above waterLevel = -2.2f)
            float campNoise = Mathf.PerlinNoise(worldX * 0.032f + 13.7f, worldZ * 0.032f + 29.3f);
            float plateauHeight = Mathf.Clamp(
                Mathf.Lerp(2.45f, 1.55f, Mathf.Clamp01(distFromCenter / 65f)) + (campNoise - 0.5f) * 0.85f,
                1.2f,
                3.5f);

            float outerBlend = SmoothStepRange(65f, 88f, distFromCenter);
            float starterFlatMask = SmoothStepRange(65f, 120f, distFromCenter);

            float nLarge = Mathf.PerlinNoise(worldX * 0.0016f + 11.2f, worldZ * 0.0016f + 5.7f);
            float nMed = Mathf.PerlinNoise(worldX * 0.0045f + 23.4f, worldZ * 0.0045f + 19.1f) * 0.45f;
            float nDetail = Mathf.PerlinNoise(worldX * 0.015f + 41.0f, worldZ * 0.015f + 63.2f) * 0.15f;
            float baseElevation = (nLarge + nMed + nDetail) * 26f * starterFlatMask;

            float beachNoise = Mathf.PerlinNoise(worldX * 0.045f + 5.1f, worldZ * 0.045f + 8.3f) * 1.15f;

            // Far Turquoise Lagoon: ONLY carved at Z = 75m..145m, never overlapping the 65m camp radius around (0,0)
            float zLagoonBlend = SmoothStepRange(75f, 84f, worldZ) * (1f - SmoothStepRange(136f, 145f, worldZ));
            float lagoonCurveX = Mathf.Sin(worldZ * 0.032f) * 5f;
            float lagoonWidth = 30f + (Mathf.PerlinNoise(worldZ * 0.03f, 14.2f) - 0.5f) * 6f;
            float xDistLagoon = Mathf.Abs(worldX - lagoonCurveX);
            float lagoonOuterMask = (1f - SmoothStepRange(lagoonWidth - 9f, lagoonWidth, xDistLagoon)) * zLagoonBlend;

            float radialCampGuard = SmoothStepRange(66f, 75f, distFromCenter);
            float northPeninsulaMask = SmoothStepRange(134f, 144f, worldZ) * (1f - SmoothStepRange(9f, 20f, Mathf.Abs(worldX)));
            float lagoonCarveMask = Mathf.Clamp01(lagoonOuterMask * radialCampGuard * (1f - northPeninsulaMask * 0.90f));

            // Twin 9.5m-18m Mossy Cliff Ridges flanking Left (West) and Right (East) sides of the Far Lagoon (Z = 75..145m)
            float cliffZBand = SmoothStepRange(72f, 86f, worldZ) * (1f - SmoothStepRange(134f, 152f, worldZ));
            float cliffXBand = SmoothStepRange(30f, 44f, Mathf.Abs(worldX)) * (1f - SmoothStepRange(72f, 98f, Mathf.Abs(worldX)));
            float cliffRoughNoise = Mathf.PerlinNoise(worldX * 0.045f + 77f, worldZ * 0.045f + 33f);
            float framingCliffHeight = cliffZBand * cliffXBand * Mathf.Lerp(9.5f, 18.0f, cliffRoughNoise) * radialCampGuard;

            // Northern Volcanic Summit (0, +480)
            float distNorthPeak = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(0f, 480f));
            float volcanoFactor = Mathf.Clamp01(1f - distNorthPeak / 390f);
            float volcanoHeight = Mathf.Pow(volcanoFactor, 1.85f) * peakHeight;

            // Eastern Crystal Canyon (+380, +40)
            float distEastCanyon = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(380f, 40f));
            float canyonFactor = Mathf.Clamp01(1f - distEastCanyon / 290f);
            float ridgeNoise = Mathf.Abs(Mathf.PerlinNoise(worldX * 0.008f, worldZ * 0.008f) - 0.5f) * 2f;
            float canyonHeight = canyonFactor * ridgeNoise * 48f * starterFlatMask;

            // Southeastern Mangrove Swamp (+260, -260)
            float distSwamp = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(260f, -260f));
            float swampFactor = Mathf.Clamp01(1f - distSwamp / 250f);

            float rawLandHeight = (1.65f + beachNoise * starterFlatMask + baseElevation + framingCliffHeight + volcanoHeight + canyonHeight) * islandMask
                                - (swampFactor * 2.2f * starterFlatMask);
            float landHeight = Mathf.Lerp(plateauHeight, rawLandHeight, outerBlend);
            float lagoonFloorY = -4.2f + (Mathf.PerlinNoise(worldX * 0.06f, worldZ * 0.06f) - 0.5f) * 0.5f;
            finalHeight = Mathf.Lerp(landHeight, lagoonFloorY, lagoonCarveMask);
        }

        if (!evaluateVisuals)
        {
            biome = BiomeType.StarterCoralBay;
            vertexColor = Color.white;
            return;
        }

        float dNorth = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(0f, 480f));
        float vFac = Mathf.Clamp01(1f - dNorth / 390f);
        float dCanyon = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(380f, 40f));
        float cFac = Mathf.Clamp01(1f - dCanyon / 290f);
        float dSwamp = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(260f, -260f));
        float sFac = Mathf.Clamp01(1f - dSwamp / 250f);

        if (distFromCenter < 150f || (finalHeight < 1.6f && sFac < 0.35f))
        {
            biome = BiomeType.StarterCoralBay;
        }
        else if (vFac > 0.32f || finalHeight > 42f)
        {
            biome = BiomeType.VolcanicSummit;
        }
        else if (cFac > 0.35f)
        {
            biome = BiomeType.CrystalCanyon;
        }
        else if (sFac > 0.35f)
        {
            biome = BiomeType.MangroveSwamp;
        }
        else
        {
            biome = BiomeType.AncientRainforest;
        }

        Color sandCol = new Color(0.92f, 0.84f, 0.65f);
        Color coastalGrass = new Color(0.24f, 0.58f, 0.24f);
        Color deepJungle = new Color(0.14f, 0.42f, 0.18f);
        Color swampMud = new Color(0.28f, 0.35f, 0.22f);
        Color canyonSlate = new Color(0.30f, 0.44f, 0.56f);
        Color volcanicBasalt = new Color(0.20f, 0.19f, 0.22f);

        vertexColor = sandCol;
        if (finalHeight > 1.8f)
        {
            vertexColor = Color.Lerp(sandCol, coastalGrass, Mathf.Clamp01((finalHeight - 1.8f) / 3.5f));
        }

        switch (biome)
        {
            case BiomeType.AncientRainforest:
                vertexColor = Color.Lerp(vertexColor, deepJungle, 0.72f);
                break;
            case BiomeType.MangroveSwamp:
                vertexColor = Color.Lerp(vertexColor, swampMud, 0.75f);
                break;
            case BiomeType.CrystalCanyon:
                vertexColor = Color.Lerp(vertexColor, canyonSlate, 0.78f);
                break;
            case BiomeType.VolcanicSummit:
                vertexColor = Color.Lerp(vertexColor, volcanicBasalt, Mathf.Clamp01((finalHeight - 18f) / 45f));
                break;
        }
    }

    private Mesh GenerateChunkTerrainMesh(float originX, float originZ, float chunkSize, int res)
    {
        Mesh mesh = new Mesh();
        int vCount = (res + 1) * (res + 1);
        Vector3[] verts = new Vector3[vCount];
        Vector2[] uvs = new Vector2[vCount];
        Color[] cols = new Color[vCount];
        int[] tris = new int[res * res * 6];

        int idx = 0;
        for (int z = 0; z <= res; z++)
        {
            for (int x = 0; x <= res; x++)
            {
                float lx = (x / (float)res) * chunkSize;
                float lz = (z / (float)res) * chunkSize;
                float wx = originX + lx;
                float wz = originZ + lz;

                EvaluateWorldCoreStatic(wx, wz, worldSize, maxMountainHeight, !_isHeadlessServer, out float wy, out _, out Color c);

                verts[idx] = new Vector3(lx, wy, lz);
                uvs[idx] = new Vector2(wx * 0.04f, wz * 0.04f);
                cols[idx] = c;
                idx++;
            }
        }

        int t = 0;
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
            {
                int i = z * (res + 1) + x;
                tris[t++] = i;
                tris[t++] = i + res + 1;
                tris[t++] = i + 1;

                tris[t++] = i + 1;
                tris[t++] = i + res + 1;
                tris[t++] = i + res + 2;
            }
        }

        mesh.vertices = verts;
        mesh.uv = uvs;
        if (!_isHeadlessServer)
        {
            mesh.colors = cols;
        }
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // =========================================================================
    // 4. DETERMINISTIC CHUNK STREAMING (PROPS <= 260m, COLLIDERS <= 420m)
    // =========================================================================
    public void RefreshStreamingNow()
    {
        UpdateChunkStreamingAndUpgradePrimitives();
    }

    private void UpdateChunkStreamingAndUpgradePrimitives()
    {
        if (!_isHeadlessServer)
        {
            UpgradeExistingPrimitivesInScene();
        }

        RefreshColliderFocusPositions();
        Vector3 localFocusPos = GetFocusPlayerPosition();
        float propRadiusSq = propStreamingRadius * propStreamingRadius;
        float colliderRadiusSq = colliderStreamingRadius * colliderStreamingRadius;

        foreach (KeyValuePair<Vector2Int, WorldChunk> kv in _chunks)
        {
            WorldChunk chunk = kv.Value;

            bool shouldEnableCollider = false;
            for (int i = 0; i < _colliderFocusPositions.Count; i++)
            {
                Vector3 fp = _colliderFocusPositions[i];
                float dx = chunk.worldCenter.x - fp.x;
                float dz = chunk.worldCenter.z - fp.z;
                if (dx * dx + dz * dz <= colliderRadiusSq)
                {
                    shouldEnableCollider = true;
                    break;
                }
            }

            if (chunk.terrainCollider != null && chunk.terrainCollider.enabled != shouldEnableCollider)
            {
                chunk.terrainCollider.enabled = shouldEnableCollider;
            }

            if (_isHeadlessServer)
                continue;

            float ldx = chunk.worldCenter.x - localFocusPos.x;
            float ldz = chunk.worldCenter.z - localFocusPos.z;
            bool shouldShowProps = (ldx * ldx + ldz * ldz) <= propRadiusSq;

            if (shouldShowProps)
            {
                if (!chunk.propsGenerated)
                {
                    GenerateBiomePropsForChunk(chunk);
                }
                if (chunk.propsRoot != null && !chunk.propsRoot.activeSelf)
                {
                    chunk.propsRoot.SetActive(true);
                }
            }
            else if (chunk.propsRoot != null && chunk.propsRoot.activeSelf)
            {
                chunk.propsRoot.SetActive(false);
            }
        }
    }

    private void RefreshColliderFocusPositions()
    {
        _colliderFocusPositions.Clear();

        if (_isHeadlessServer || (Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsServer))
        {
            NetworkPlayerController[] allPlayers = FindObjectsByType<NetworkPlayerController>(FindObjectsSortMode.None);
            for (int i = 0; i < allPlayers.Length; i++)
            {
                if (allPlayers[i] != null && allPlayers[i].IsSpawned)
                {
                    _colliderFocusPositions.Add(allPlayers[i].transform.position);
                }
            }
        }

        if (_colliderFocusPositions.Count == 0)
        {
            _colliderFocusPositions.Add(GetFocusPlayerPosition());
        }
    }

    private void GenerateBiomePropsForChunk(WorldChunk chunk)
    {
        chunk.propsGenerated = true;
        GameObject root = new GameObject("BiomeProps");
        root.transform.SetParent(chunk.terrainObject.transform, false);
        root.transform.localPosition = Vector3.zero;
        chunk.propsRoot = root;

        float chunkSize = worldSize / chunkGridCount;
        float halfWorld = worldSize * 0.5f;
        float originX = -halfWorld + chunk.coord.x * chunkSize;
        float originZ = -halfWorld + chunk.coord.y * chunkSize;

        int chunkSeed = worldSeed ^ (chunk.coord.x * 73856093) ^ (chunk.coord.y * 19349663);
        System.Random rng = new System.Random(chunkSeed);

        const int propCount = 26;
        for (int i = 0; i < propCount; i++)
        {
            float lx = (float)rng.NextDouble() * chunkSize;
            float lz = (float)rng.NextDouble() * chunkSize;
            float wx = originX + lx;
            float wz = originZ + lz;

            // Keep central camp plateau & starter beach hero clearing unblocked
            if (wx * wx + wz * wz < 26f * 26f)
                continue;

            float wy = EvaluateHeightOnly(wx, wz);
            if (wy < waterLevel + 0.45f)
                continue;

            EvaluateBiomeAndHeight(wx, wz, out BiomeType biome, out _);
            Vector3 localPos = new Vector3(lx, wy, lz);
            float scale = 0.85f + (float)rng.NextDouble() * 0.55f;
            float rotY = (float)rng.NextDouble() * 360f;

            switch (biome)
            {
                case BiomeType.StarterCoralBay:
                    if (i % 4 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale, PickSeededPrefab(treePrefabs, rng), BuildFastPalmTree);
                    else if (i % 4 == 1)
                        SpawnChunkProp(root.transform, localPos, rotY, scale, null, BuildUnderstoryFernClump);
                    else if (i % 6 == 2)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 0.95f, null, BuildFastBoulder);
                    break;

                case BiomeType.AncientRainforest:
                    if (i % 4 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.4f, PickSeededPrefab(treePrefabs, rng), BuildFastJungleGiantTree);
                    else if (i % 4 == 1)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.1f, PickSeededPrefab(treePrefabs, rng), BuildFastPalmTree);
                    else
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.15f, null, BuildUnderstoryFernClump);
                    break;

                case BiomeType.CrystalCanyon:
                    if (i % 2 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.35f, PickSeededPrefab(crystalPrefabs, rng), BuildFastCrystalCluster);
                    else
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.25f, null, BuildFastBoulder);
                    break;

                case BiomeType.MangroveSwamp:
                    if (i % 3 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.15f, PickSeededPrefab(treePrefabs, rng), BuildFastJungleGiantTree);
                    else if (i % 3 == 1)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 0.95f, null, BuildUnderstoryFernClump);
                    else
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 0.85f, PickSeededPrefab(crystalPrefabs, rng), BuildFastCrystalCluster);
                    break;

                case BiomeType.VolcanicSummit:
                    if (i % 3 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.55f, null, BuildFastBoulder);
                    else if (i % 4 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.25f, PickSeededPrefab(crystalPrefabs, rng), BuildFastCrystalCluster);
                    break;
            }
        }

        MarkHierarchyUpgraded(root);
    }

    private void SpawnChunkProp(Transform parent, Vector3 localPos, float rotY, float scale, GameObject prefab, Action<Transform> fallbackBuilder)
    {
        GameObject prop = new GameObject("EnvProp");
        prop.transform.SetParent(parent, false);
        prop.transform.localPosition = localPos;
        prop.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);
        prop.transform.localScale = Vector3.one * scale;

        if (prefab != null)
        {
            GameObject p = Instantiate(prefab, prop.transform);
            p.transform.localPosition = Vector3.zero;
            p.transform.localRotation = Quaternion.identity;
            StripNonVisualComponentsFromProp(p);
        }
        else
        {
            fallbackBuilder?.Invoke(prop.transform);
        }
    }

    private static void StripNonVisualComponentsFromProp(GameObject root)
    {
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
        {
            Destroy(c);
        }

        MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null)
                Destroy(behaviours[i]);
        }
    }

    // 10-segment curved palm trunk + 11 drooping wind-animated fronds + dense fern/grass/rock understory
    private void BuildFastPalmTree(Transform parent)
    {
        CreateMeshPart("PalmTrunk", parent, _sharedTrunkMesh, _barkMaterial);

        // Crown top offset matches CreateSegmentedCurvedPalmTrunkMesh tip (~(0.55, 4.4, 0.25))
        Vector3 crownAnchor = new Vector3(0.55f, 4.35f, 0.25f);

        // Outer crown (7 drooping arched fronds)
        for (int f = 0; f < 7; f++)
        {
            GameObject frond = CreateMeshPart("FrondOuter_" + f, parent, _sharedPalmFrondMesh, _palmLeafMaterial);
            frond.transform.localPosition = crownAnchor;
            frond.transform.localRotation = Quaternion.Euler(24f, f * (360f / 7f), 0f);
            frond.transform.localScale = new Vector3(1.05f, 1.0f, 1.12f);
        }

        // Inner upper crown (4 lifted young fronds -> total 11 fronds swaying in wind)
        for (int f = 0; f < 4; f++)
        {
            GameObject frond = CreateMeshPart("FrondInner_" + f, parent, _sharedPalmFrondMesh, _palmLeafMaterial);
            frond.transform.localPosition = crownAnchor + Vector3.up * 0.14f;
            frond.transform.localRotation = Quaternion.Euler(-4f, f * 90f + 45f, 0f);
            frond.transform.localScale = new Vector3(0.85f, 0.90f, 0.86f);
        }

        // Dense base understory: ferns, grass tufts & coastal mossy boulder under every palm tree
        GameObject ferns = CreateMeshPart("BaseFernCluster", parent, _sharedFernClusterMesh, _fernLeafMaterial);
        ferns.transform.localPosition = new Vector3(0.18f, -0.05f, 0.22f);
        ferns.transform.localScale = Vector3.one * 0.95f;

        GameObject grass = CreateMeshPart("BaseGrassTuft", parent, _sharedGrassTuftMesh, _fernLeafMaterial);
        grass.transform.localPosition = new Vector3(-0.35f, -0.04f, -0.25f);

        GameObject baseRock = CreateMeshPart("BaseMossBoulder", parent, _sharedRockMesh, _mossRockMaterial);
        baseRock.transform.localPosition = new Vector3(-0.55f, -0.10f, 0.35f);
        baseRock.transform.localScale = new Vector3(0.62f, 0.44f, 0.54f);
    }

    private void BuildUnderstoryFernClump(Transform parent)
    {
        CreateMeshPart("FernClump", parent, _sharedFernClusterMesh, _fernLeafMaterial);
        GameObject tuft = CreateMeshPart("GrassTuft", parent, _sharedGrassTuftMesh, _fernLeafMaterial);
        tuft.transform.localPosition = new Vector3(0.35f, 0f, -0.25f);
    }

    private void BuildFastJungleGiantTree(Transform parent)
    {
        GameObject trunk = CreateMeshPart("JungleTrunk", parent, _sharedTrunkMesh, _barkMaterial);
        trunk.transform.localScale = new Vector3(1.6f, 1.45f, 1.6f);

        for (int c = 0; c < 3; c++)
        {
            GameObject canopy = CreateMeshPart("Canopy_" + c, parent, _sharedJungleCanopyMesh, _jungleLeafMaterial);
            canopy.transform.localPosition = new Vector3(0.55f, 3.8f + c * 1.25f, 0.25f);
            canopy.transform.localScale = Vector3.one * (1.35f - c * 0.24f);
        }

        BuildUnderstoryFernClump(parent);
    }

    private void BuildFastCrystalCluster(Transform parent)
    {
        CreateMeshPart("CrystalRockBase", parent, _sharedRockMesh, _mossRockMaterial);
        for (int s = 0; s < 3; s++)
        {
            GameObject shard = CreateMeshPart("Shard_" + s, parent, _sharedCrystalShardMesh, _crystalMaterial);
            shard.transform.localPosition = new Vector3((s - 1) * 0.22f, 0.12f, (s % 2) * 0.18f);
            shard.transform.localRotation = Quaternion.Euler((s - 1) * 18f, s * 110f, (1 - s) * 14f);
            shard.transform.localScale = Vector3.one * (1.15f - s * 0.18f);
        }
    }

    private void BuildFastBoulder(Transform parent)
    {
        GameObject r = CreateMeshPart("MossyBoulder", parent, _sharedRockMesh, _mossRockMaterial);
        r.transform.localScale = new Vector3(1.35f, 0.85f, 1.18f);
    }

    private void BuildNorthernSummitTempleLandmark(Vector3 summitPos)
    {
        GameObject temple = new GameObject("Northern_Summit_Ancient_Temple");
        temple.transform.SetParent(transform, false);
        temple.transform.position = summitPos;

        for (int p = 0; p < 6; p++)
        {
            float ang = p * 60f * Mathf.Deg2Rad;
            GameObject pillar = CreateMeshPart("TemplePillar_" + p, temple.transform, _sharedTrunkMesh, _mossRockMaterial);
            pillar.transform.localPosition = new Vector3(Mathf.Cos(ang) * 8f, 0f, Mathf.Sin(ang) * 8f);
            pillar.transform.localScale = new Vector3(2.8f, 2.5f, 2.8f);
        }

        GameObject core = CreateMeshPart("SummitPortalCore", temple.transform, _sharedCrystalShardMesh, _templePortalMaterial);
        core.transform.localPosition = new Vector3(0f, 6.5f, 0f);
        core.transform.localScale = Vector3.one * 3.5f;

        AddBioGlowLight(temple.transform, new Vector3(0f, 8f, 0f), new Color(1.0f, 0.45f, 0.15f), 35f);
        MarkHierarchyUpgraded(temple);
    }

    private Vector3 GetFocusPlayerPosition()
    {
        if (NetworkPlayerController.LocalPlayer != null)
        {
            _playerTransform = NetworkPlayerController.LocalPlayer.transform;
            return _playerTransform.position;
        }
        if (_playerTransform != null) return _playerTransform.position;
        if (Camera.main != null) return Camera.main.transform.position;
        return Vector3.zero;
    }

    private void UpdateOceanAndWaterfallAnimation(float time)
    {
        if (_waterfallMaterial != null && _waterfallMaterial.HasProperty("_BaseMap"))
        {
            _waterfallMaterial.SetTextureOffset("_BaseMap", new Vector2(0f, -time * 1.4f));
        }
    }

    // =========================================================================
    // 5. DAY / NIGHT & BIOGLOW SYNCHRONIZATION
    // =========================================================================
    private void UpdateDayNightAndBioGlow()
    {
        bool managedByWorldTime = WorldTimeManager.Instance != null;
        if (managedByWorldTime)
        {
            currentHour = WorldTimeManager.Instance.WorldTimeHours.Value;
            bioGlowIntensity = WorldTimeManager.Instance.CurrentBioluminescenceIntensity;
        }

        float dayFactor = Mathf.Clamp01(Mathf.Sin((currentHour - 6f) / 12f * Mathf.PI));
        float nightFactor = Mathf.Max(1f - dayFactor, bioGlowIntensity);

        if (!managedByWorldTime)
        {
            if (_directionalLight != null)
            {
                float sunPitch = Mathf.Lerp(-25f, 62f, dayFactor);
                _directionalLight.transform.rotation = Quaternion.Euler(
                    Mathf.Max(18f, Mathf.Abs(sunPitch)),
                    -38f + currentHour * 6f,
                    0f);

                Color noonColor = new Color(1.0f, 0.96f, 0.88f);
                Color sunsetColor = new Color(1.0f, 0.56f, 0.34f);
                Color nightMoonColor = new Color(0.24f, 0.42f, 0.78f);

                _directionalLight.color = dayFactor > 0.35f
                    ? Color.Lerp(sunsetColor, noonColor, (dayFactor - 0.35f) / 0.65f)
                    : Color.Lerp(nightMoonColor, sunsetColor, dayFactor / 0.35f);
                _directionalLight.intensity = Mathf.Lerp(0.45f, 1.38f, dayFactor);
            }

            RenderSettings.ambientSkyColor = Color.Lerp(new Color(0.38f, 0.69f, 0.96f), new Color(0.06f, 0.18f, 0.34f), nightFactor);
            RenderSettings.ambientEquatorColor = Color.Lerp(new Color(0.75f, 0.85f, 0.92f), new Color(0.05f, 0.14f, 0.26f), nightFactor);
            RenderSettings.ambientGroundColor = Color.Lerp(new Color(0.32f, 0.28f, 0.24f), new Color(0.03f, 0.06f, 0.11f), nightFactor);
            RenderSettings.fogColor = Color.Lerp(new Color(0.56f, 0.84f, 0.96f), new Color(0.03f, 0.09f, 0.18f), nightFactor);
            Shader.SetGlobalFloat("_GlobalBioluminescence", nightFactor);
        }

        float pulse = 0.86f + 0.22f * Mathf.Sin(Time.time * 2.3f);
        float glowPower = Mathf.Lerp(0.75f, 3.4f, nightFactor) * pulse;

        if (_crystalMaterial != null && _crystalMaterial.HasProperty("_EmissionColor"))
            _crystalMaterial.SetColor("_EmissionColor", new Color(0.10f, 0.92f, 1.0f) * glowPower);
        if (_altarCoreMaterial != null && _altarCoreMaterial.HasProperty("_EmissionColor"))
            _altarCoreMaterial.SetColor("_EmissionColor", new Color(0.18f, 0.95f, 0.78f) * (glowPower * 1.15f));
        if (_lampGlowMaterial != null && _lampGlowMaterial.HasProperty("_EmissionColor"))
            _lampGlowMaterial.SetColor("_EmissionColor", new Color(1.0f, 0.72f, 0.28f) * Mathf.Lerp(0.8f, 3.6f, nightFactor));

        for (int i = _nightPointLights.Count - 1; i >= 0; i--)
        {
            if (_nightPointLights[i] == null)
            {
                _nightPointLights.RemoveAt(i);
                continue;
            }
            _nightPointLights[i].intensity = Mathf.Lerp(0.8f, 3.0f, nightFactor) * pulse;
        }
    }

    // =========================================================================
    // 6. SAFE UPGRADE FOR STANDALONE PRIMITIVES & CHARACTER PROPORTIONS
    // =========================================================================
    public void UpgradeExistingPrimitivesInScene()
    {
        if (NetworkPlayerController.LocalPlayer != null)
        {
            _playerTransform = NetworkPlayerController.LocalPlayer.transform;
        }

        // Ensure every player controller (QuickTest or Networked) receives Soldier.glb visual even without a root Capsule MeshRenderer
        foreach (CharacterController cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
        {
            if (cc == null) continue;
            string n = cc.gameObject.name.ToLowerInvariant();
            bool isPlayer = n.Contains("player") || cc.CompareTag("Player") ||
                            cc.GetComponent<NetworkPlayerController>() != null ||
                            cc.GetComponent<EditorQuickTestPlayerMotor>() != null;
            if (isPlayer)
            {
                _playerTransform = cc.transform;
                AttachSoldierVisualToPlayer(cc.gameObject);
            }
        }

        MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

        foreach (MeshRenderer mr in renderers)
        {
            if (mr == null || !mr.enabled) continue;
            GameObject go = mr.gameObject;
            int id = go.GetInstanceID();
            if (_upgradedInstanceIds.Contains(id)) continue;

            if (go.transform.IsChildOf(transform) || IsAlreadyUpgradedKayKitHierarchy(go.transform))
            {
                _upgradedInstanceIds.Add(id);
                AdjustKayKitProportionIfPresent(go.transform.root);
                continue;
            }

            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            string meshName = mf.sharedMesh.name;
            string objName = go.name.ToLowerInvariant();
            Vector3 scale = go.transform.lossyScale;

            if (objName.Contains("ocean") || go.transform == _oceanTransform)
            {
                _upgradedInstanceIds.Add(id);
                continue;
            }

            if (objName.Contains("player") || meshName == "Capsule")
            {
                _playerTransform = go.transform;
                AttachSoldierVisualToPlayer(go);
            }
            else if (objName.Contains("tree") || objName.Contains("wood") || (meshName == "Cylinder" && scale.y > scale.x * 1.4f))
            {
                ReplaceWithVisualModel(go, mr, PickRandomPrefab(treePrefabs), BuildFastPalmTree);
            }
            else if (objName.Contains("altar") || (meshName == "Cylinder" && scale.x >= 1.8f))
            {
                ReplaceWithVisualModel(go, mr, altarPrefab, BuildAncientAltarVisual);
            }
            else if (objName.Contains("crystal") || objName.Contains("ore"))
            {
                ReplaceWithVisualModel(go, mr, PickRandomPrefab(crystalPrefabs), BuildFastCrystalCluster);
            }
            else if (objName.Contains("chest") || objName.Contains("storage"))
            {
                ReplaceWithVisualModel(go, mr, chestPrefab, BuildDetailedWoodenChestVisual);
            }
            else if (objName.Contains("lamp") || objName.Contains("torch"))
            {
                ReplaceWithVisualModel(go, mr, lampPrefab, BuildBioluminescentLanternVisual);
            }
            else if (objName.Contains("floor") || objName.Contains("foundation") || (meshName == "Cube" && scale.y < 0.6f && scale.x >= 1.8f))
            {
                ReplaceWithVisualModel(go, mr, floorPrefab, BuildDetailedStoneWoodDeckVisual);
            }
            else if (objName.Contains("monster") || objName.Contains("enemy") || objName.Contains("crab") || meshName == "Sphere")
            {
                ReplaceWithVisualModel(go, mr, monsterVisualPrefab, BuildCoralGuardianMonsterVisual);
            }
            else if (meshName == "Cube")
            {
                ReplaceWithVisualModel(go, mr, PickRandomPrefab(crystalPrefabs), BuildFastCrystalCluster);
            }
        }

        DisableInvalidAnimatorsInScene();
    }

    public void SnapAllSceneObjectsToGround()
    {
        float minDryTreeHeight = Mathf.Max(waterLevel + 0.5f, 1.2f);
        var seenIds = new HashSet<int>();

        // 1. All HarvestableResourceNode (PF_CoralWoodNode, PF_GlowCrystalNode)
        HarvestableResourceNode[] nodes = FindObjectsByType<HarvestableResourceNode>(FindObjectsSortMode.None);
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] == null) continue;
            seenIds.Add(nodes[i].gameObject.GetInstanceID());
            SnapSingleSceneObjectToGround(nodes[i].gameObject, minDryTreeHeight, i);
        }

        // 2. Altar, Chests, Cave Gates, Monsters
        foreach (IslandCoreAltar altar in FindObjectsByType<IslandCoreAltar>(FindObjectsSortMode.None))
        {
            if (altar != null && seenIds.Add(altar.gameObject.GetInstanceID()))
                SnapTransformToGround(altar.transform);
        }
        foreach (SharedChestInventory chest in FindObjectsByType<SharedChestInventory>(FindObjectsSortMode.None))
        {
            if (chest != null && seenIds.Add(chest.gameObject.GetInstanceID()))
                SnapTransformToGround(chest.transform);
        }
        foreach (TidalCrystalCaveGate gate in FindObjectsByType<TidalCrystalCaveGate>(FindObjectsSortMode.None))
        {
            if (gate != null && seenIds.Add(gate.gameObject.GetInstanceID()))
                SnapTransformToGround(gate.transform);
        }
        foreach (ScalableMonsterAI monster in FindObjectsByType<ScalableMonsterAI>(FindObjectsSortMode.None))
        {
            if (monster != null && seenIds.Add(monster.gameObject.GetInstanceID()))
                SnapTransformToGround(monster.transform);
        }

        // 3. Fallback search by object name in case prefabs were stripped or renamed
        Transform[] allTransforms = FindObjectsByType<Transform>(FindObjectsSortMode.None);
        for (int i = 0; i < allTransforms.Length; i++)
        {
            Transform t = allTransforms[i];
            if (t == null || t.parent != null) continue;
            if (seenIds.Contains(t.gameObject.GetInstanceID())) continue;

            string n = t.name;
            if (n.StartsWith("PF_CoralWoodNode", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("PF_GlowCrystalNode", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("IslandCoreAltar", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("PF_SharedChest", StringComparison.OrdinalIgnoreCase))
            {
                seenIds.Add(t.gameObject.GetInstanceID());
                SnapSingleSceneObjectToGround(t.gameObject, minDryTreeHeight, i);
            }
        }
    }

    private void SnapSingleSceneObjectToGround(GameObject go, float minDryHeight, int indexHint)
    {
        if (go == null) return;
        Vector3 pos = go.transform.position;
        bool isWoodTree = go.name.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          go.name.IndexOf("Palm", StringComparison.OrdinalIgnoreCase) >= 0;

        float groundY = EvaluateHeightOnly(pos.x, pos.z);
        if (isWoodTree && groundY < minDryHeight)
        {
            Vector2 dryXZ = FindDryLandSpotNear(pos.x, pos.z, minDryHeight, indexHint);
            pos.x = dryXZ.x;
            pos.z = dryXZ.y;
            groundY = EvaluateHeightOnly(pos.x, pos.z);
        }

        go.transform.position = new Vector3(pos.x, groundY, pos.z);

        if (isWoodTree && !_isHeadlessServer)
        {
            EnsureWoodNodePalmVisualVisible(go);
        }
    }

    private void SnapTransformToGround(Transform t)
    {
        if (t == null) return;
        Vector3 p = t.position;
        float gy = EvaluateHeightOnly(p.x, p.z);
        t.position = new Vector3(p.x, gy, p.z);
    }

    private Vector2 FindDryLandSpotNear(float origX, float origZ, float minHeight, int seedOffset)
    {
        for (int step = 0; step < 36; step++)
        {
            float angle = ((step + seedOffset) * 37f) * Mathf.Deg2Rad;
            float radius = Mathf.Lerp(14f, 52f, (step % 12) / 11f);
            float cx = Mathf.Cos(angle) * radius;
            float cz = Mathf.Sin(angle) * radius;
            if (Mathf.Abs(cx) < 6f && cz > -14f && cz < 10f)
                continue;

            if (EvaluateHeightOnly(cx, cz) >= minHeight)
                return new Vector2(cx, cz);
        }

        return new Vector2(18f + (seedOffset % 4) * 4f, -12f + (seedOffset % 3) * 5f);
    }

    private void EnsureWoodNodePalmVisualVisible(GameObject woodGo)
    {
        if (woodGo == null) return;
        if (!woodGo.activeSelf) woodGo.SetActive(true);

        Transform visualT = woodGo.transform.Find("PalmTreeVisual");
        if (visualT == null)
        {
            var node = woodGo.GetComponent<HarvestableResourceNode>();
            if (node != null && node.transform.childCount > 0)
                visualT = node.transform.GetChild(0);
        }

        bool hasVisibleMesh = false;
        foreach (MeshRenderer mr in woodGo.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr == null) continue;
            if (!mr.gameObject.activeSelf) mr.gameObject.SetActive(true);
            if (mr.enabled) hasVisibleMesh = true;
        }

        if (!hasVisibleMesh)
        {
            GameObject holder = visualT != null ? visualT.gameObject : new GameObject("PalmTreeVisual");
            if (holder.transform.parent != woodGo.transform)
                holder.transform.SetParent(woodGo.transform, false);
            holder.transform.localPosition = Vector3.zero;
            holder.transform.localRotation = Quaternion.identity;
            BuildFastPalmTree(holder.transform);
            MarkHierarchyUpgraded(holder);
        }
    }

    public void DisableInvalidAnimatorsInScene()
    {
        Animator[] animators = FindObjectsByType<Animator>(FindObjectsSortMode.None);
        for (int i = 0; i < animators.Length; i++)
        {
            Animator anim = animators[i];
            if (anim == null) continue;

            bool invalid = anim.runtimeAnimatorController == null || !anim.isInitialized || anim.layerCount <= 0;
            if (invalid && anim.enabled)
            {
                anim.enabled = false;
            }
        }
    }

    public GameObject AttachSoldierVisualToPlayer(GameObject playerTarget, MeshRenderer optionalPrimitiveRenderer = null)
    {
        if (playerTarget == null)
            return null;

        _upgradedInstanceIds.Add(playerTarget.GetInstanceID());

        if (optionalPrimitiveRenderer != null)
            optionalPrimitiveRenderer.enabled = false;

        MeshRenderer rootMr = playerTarget.GetComponent<MeshRenderer>();
        if (rootMr != null)
            rootMr.enabled = false;

        Transform existingVisual = playerTarget.transform.Find("SoldierVisual");
        if (existingVisual != null)
        {
            MarkHierarchyUpgraded(existingVisual.gameObject);
            return existingVisual.gameObject;
        }

        // If PlayerAvatar prefab already includes a working Soldier/Adventurer Animator child, don't stack another one
        Animator existingAnim = playerTarget.GetComponentInChildren<Animator>(true);
        if (existingAnim != null && existingAnim.gameObject != playerTarget &&
            existingAnim.runtimeAnimatorController != null && existingAnim.layerCount > 0)
        {
            MarkHierarchyUpgraded(existingAnim.gameObject);
            return existingAnim.gameObject;
        }

        GameObject prefab = playerVisualPrefab != null
            ? playerVisualPrefab
            : Resources.Load<GameObject>("SoldierPlayerVisual");

        GameObject spawned = null;
        if (prefab != null)
        {
            spawned = Instantiate(prefab, playerTarget.transform);
            spawned.name = "SoldierVisual";

            float groundBottomOffset = 0f;
            CharacterController cc = playerTarget.GetComponent<CharacterController>();
            if (cc != null)
                groundBottomOffset = cc.center.y - cc.height * 0.5f;

            spawned.transform.localPosition = new Vector3(0f, groundBottomOffset, 0f);
            spawned.transform.localRotation = Quaternion.identity;

            foreach (Collider col in spawned.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            foreach (Unity.Netcode.NetworkObject no in spawned.GetComponentsInChildren<Unity.Netcode.NetworkObject>(true))
                Destroy(no);

            Animator anim = spawned.GetComponentInChildren<Animator>(true);
            if (anim != null)
            {
                if (anim.runtimeAnimatorController == null || !anim.isInitialized || anim.layerCount <= 0)
                {
                    anim.enabled = false;
                }
                else
                {
                    CharacterAnimationSync animSync = playerTarget.GetComponent<CharacterAnimationSync>();
                    if (animSync == null)
                        animSync = playerTarget.AddComponent<CharacterAnimationSync>();
                    animSync.RefreshTargetAnimator(anim);
                }
            }

            MarkHierarchyUpgraded(spawned);
        }

        return spawned;
    }

    private static void AdjustKayKitProportionIfPresent(Transform root)
    {
        if (root == null) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (string.Equals(child.name, "head", StringComparison.OrdinalIgnoreCase))
            {
                if (child.localScale.x > 0.90f)
                    child.localScale = new Vector3(0.78f, 0.78f, 0.78f);
            }
            else if (string.Equals(child.name, "EquippedTool", StringComparison.OrdinalIgnoreCase))
            {
                if (child.localScale.x > 0.90f)
                    child.localScale = new Vector3(0.75f, 0.75f, 0.75f);
            }
        }
    }

    private static bool IsAlreadyUpgradedKayKitHierarchy(Transform target)
    {
        if (target == null)
            return true;

        if (target.name == "WaypointArrow3D")
            return true;

        if (target.GetComponentInParent<Animator>() != null ||
            target.GetComponentInParent<HarvestableResourceNode>() != null ||
            target.GetComponentInParent<IslandCoreAltar>() != null ||
            target.GetComponentInParent<SharedChestInventory>() != null ||
            target.GetComponentInParent<TidalCrystalCaveGate>() != null ||
            target.GetComponentInParent<NetworkPlayerController>() != null ||
            target.GetComponentInParent<IslandFastTravelAndRaftSystem>() != null ||
            target.GetComponentInParent<IslandRelicAndVolcanoBossSystem>() != null)
        {
            return true;
        }

        string rootName = target.root.name;
        return rootName.StartsWith("PF_") ||
               rootName.Contains("Temple") ||
               rootName.Contains("Portal") ||
               rootName.Contains("Raft") ||
               rootName.Contains("Relic") ||
               rootName.Contains("Boss");
    }

    private void ReplaceWithVisualModel(GameObject target, MeshRenderer originalRenderer, GameObject customPrefab, Action<Transform> proceduralFallbackBuilder)
    {
        _upgradedInstanceIds.Add(target.GetInstanceID());
        originalRenderer.enabled = false;

        GameObject visualHolder = new GameObject("HD_VisualModel");
        visualHolder.transform.SetParent(target.transform, false);
        visualHolder.transform.localPosition = Vector3.zero;
        visualHolder.transform.localRotation = Quaternion.identity;

        Vector3 parentScale = target.transform.localScale;
        visualHolder.transform.localScale = new Vector3(
            1f / Mathf.Max(0.01f, parentScale.x),
            1f / Mathf.Max(0.01f, parentScale.y),
            1f / Mathf.Max(0.01f, parentScale.z));

        if (customPrefab != null)
        {
            GameObject spawned = Instantiate(customPrefab, visualHolder.transform);
            spawned.transform.localPosition = Vector3.zero;
            spawned.transform.localRotation = Quaternion.identity;
            StripNonVisualComponentsFromProp(spawned);
            MarkHierarchyUpgraded(spawned);
        }
        else
        {
            proceduralFallbackBuilder?.Invoke(visualHolder.transform);
            MarkHierarchyUpgraded(visualHolder);
        }
    }

    private void MarkHierarchyUpgraded(GameObject root)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            _upgradedInstanceIds.Add(t.gameObject.GetInstanceID());
        }
    }

    private GameObject PickSeededPrefab(GameObject[] list, System.Random rng)
    {
        if (list == null || list.Length == 0) return null;
        return list[rng.Next(0, list.Length)];
    }

    private GameObject PickRandomPrefab(GameObject[] list)
    {
        if (list == null || list.Length == 0) return null;
        return list[UnityEngine.Random.Range(0, list.Length)];
    }

    // =========================================================================
    // 7. BALANCED CHARACTER PROPORTIONS & STRUCTURES
    // =========================================================================
    private void BuildAncientAltarVisual(Transform parent)
    {
        for (int s = 0; s < 3; s++)
        {
            float rad = 1.9f - s * 0.38f;
            GameObject step = CreateMeshPart("AltarStep_" + s, parent, CreateTaperedCylinderMesh(12, rad, rad * 0.92f, 0.35f), _mossRockMaterial);
            step.transform.localPosition = new Vector3(0f, -0.85f + s * 0.34f, 0f);
        }

        for (int p = 0; p < 4; p++)
        {
            float ang = p * 90f * Mathf.Deg2Rad;
            GameObject pillar = CreateMeshPart("AltarPillar_" + p, parent, CreateTaperedCylinderMesh(8, 0.2f, 0.15f, 1.5f), _mossRockMaterial);
            pillar.transform.localPosition = new Vector3(Mathf.Cos(ang) * 1.15f, -0.1f, Mathf.Sin(ang) * 1.15f);
        }

        GameObject core = CreateMeshPart("AltarEnergyCore", parent, _sharedCrystalShardMesh, _altarCoreMaterial);
        core.transform.localPosition = new Vector3(0f, 0.55f, 0f);
        core.transform.localScale = Vector3.one * 1.35f;

        AddBioGlowLight(parent, new Vector3(0f, 1.2f, 0f), new Color(0.18f, 0.95f, 0.82f), 10.0f);
    }

    private void BuildDetailedWoodenChestVisual(Transform parent)
    {
        Material goldTrimMat = CreatePBRMaterial(new Color(0.85f, 0.68f, 0.24f), 0.82f, 0.76f);

        GameObject box = CreateMeshPart("ChestBody", parent, CreateBoxMesh(0.95f, 0.55f, 0.65f), _barkMaterial);
        box.transform.localPosition = new Vector3(0f, -0.15f, 0f);

        GameObject lockPlate = CreateMeshPart("ChestLock", parent, CreateBoxMesh(0.16f, 0.24f, 0.06f), goldTrimMat);
        lockPlate.transform.localPosition = new Vector3(0f, 0.05f, 0.34f);
    }

    private void BuildBioluminescentLanternVisual(Transform parent)
    {
        GameObject post = CreateMeshPart("LanternPost", parent, CreateTaperedCylinderMesh(8, 0.1f, 0.08f, 1.3f), _mossRockMaterial);
        post.transform.localPosition = new Vector3(0f, -0.45f, 0f);

        GameObject core = CreateMeshPart("LanternBulb", parent, _sharedCrystalShardMesh, _lampGlowMaterial);
        core.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        core.transform.localScale = Vector3.one * 0.45f;

        AddBioGlowLight(parent, new Vector3(0f, 0.5f, 0f), new Color(1.0f, 0.76f, 0.36f), 8.0f);
    }

    private void BuildDetailedStoneWoodDeckVisual(Transform parent)
    {
        Mesh plankMesh = CreateBoxMesh(3.8f, 0.22f, 0.70f);
        for (int i = -2; i <= 2; i++)
        {
            GameObject plank = CreateMeshPart("DeckPlank_" + i, parent, plankMesh, _barkMaterial);
            plank.transform.localPosition = new Vector3(0f, 0f, i * 0.76f);
        }
    }

    private static Mesh CreateBoxMesh(float w, float h, float d)
    {
        float hx = w * 0.5f, hy = h * 0.5f, hz = d * 0.5f;
        Mesh m = new Mesh { name = "ProceduralBox" };
        m.vertices = new[]
        {
            new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
            new Vector3(hx, -hy, hz), new Vector3(-hx, -hy, hz), new Vector3(-hx, hy, hz), new Vector3(hx, hy, hz),
            new Vector3(-hx, hy, -hz), new Vector3(hx, hy, -hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz),
            new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, -hy, -hz), new Vector3(-hx, -hy, -hz),
            new Vector3(-hx, -hy, hz), new Vector3(-hx, -hy, -hz), new Vector3(-hx, hy, -hz), new Vector3(-hx, hy, hz),
            new Vector3(hx, -hy, -hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(hx, hy, -hz)
        };
        m.triangles = new[]
        {
            0,2,1, 0,3,2, 4,6,5, 4,7,6,
            8,10,9, 8,11,10, 12,14,13, 12,15,14,
            16,18,17, 16,19,18, 20,22,21, 20,23,22
        };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private void BuildCoralGuardianMonsterVisual(Transform parent)
    {
        Material shellMat = CreatePBRMaterial(new Color(0.68f, 0.18f, 0.22f), 0.25f, 0.68f);
        GameObject carapace = CreateMeshPart("MonsterCarapace", parent, _sharedRockMesh, shellMat);
        carapace.transform.localPosition = Vector3.zero;
        carapace.transform.localScale = new Vector3(1.1f, 0.68f, 1.05f);
    }

    // =========================================================================
    // 8. SEGMENTED PALM TRUNKS, 11-FROND PALMS, FERNS & PROCEDURAL MESHES
    // =========================================================================
    private GameObject CreateMeshPart(string name, Transform parent, Mesh mesh, Material mat)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mf.sharedMesh = mesh;
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.On;
        return go;
    }

    private Mesh CreateSegmentedCurvedPalmTrunkMesh(int segments, float bottomRadius, float topRadius, float totalHeight)
    {
        Mesh m = new Mesh { name = "SegmentedCurvedPalmTrunk" };
        const int sides = 9;
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        for (int s = 0; s <= segments; s++)
        {
            float t = s / (float)segments;
            float y = t * totalHeight;
            float bendX = Mathf.Sin(t * Mathf.PI * 0.62f) * 0.55f;
            float bendZ = t * t * 0.25f;
            // Subtle bark ring flare on alternating segment joints
            float barkRingBoost = (s % 2 == 1) ? 1.10f : 0.94f;
            float radius = Mathf.Lerp(bottomRadius, topRadius, t) * barkRingBoost;

            for (int i = 0; i <= sides; i++)
            {
                float a = (i / (float)sides) * Mathf.PI * 2f;
                verts.Add(new Vector3(bendX + Mathf.Cos(a) * radius, y, bendZ + Mathf.Sin(a) * radius));
                uvs.Add(new Vector2(i / (float)sides, t * 4f));
            }
        }

        int stride = sides + 1;
        for (int s = 0; s < segments; s++)
        {
            for (int i = 0; i < sides; i++)
            {
                int b0 = s * stride + i;
                int b1 = b0 + 1;
                int t0 = b0 + stride;
                int t1 = t0 + 1;

                tris.Add(b0); tris.Add(t0); tris.Add(b1);
                tris.Add(b1); tris.Add(t0); tris.Add(t1);
            }
        }

        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh CreateDetailedPalmFrondMesh()
    {
        Mesh m = new Mesh { name = "DetailedPalmFrond" };
        const int segs = 7;
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            float z = t * 2.55f;
            float y = Mathf.Sin(t * Mathf.PI * 0.72f) * 0.42f - t * t * 0.95f;
            float halfW = Mathf.Sin(Mathf.Clamp01(t * 0.88f + 0.12f) * Mathf.PI) * 0.48f;

            verts.Add(new Vector3(-halfW, y - 0.10f, z));
            verts.Add(new Vector3(0f, y + 0.05f, z));
            verts.Add(new Vector3(halfW, y - 0.10f, z));

            uvs.Add(new Vector2(0f, t));
            uvs.Add(new Vector2(0.5f, t));
            uvs.Add(new Vector2(1f, t));
        }

        for (int i = 0; i < segs; i++)
        {
            int r0 = i * 3;
            int r1 = (i + 1) * 3;
            tris.Add(r0); tris.Add(r1); tris.Add(r0 + 1);
            tris.Add(r0 + 1); tris.Add(r1); tris.Add(r1 + 1);
            tris.Add(r0 + 1); tris.Add(r1 + 1); tris.Add(r0 + 2);
            tris.Add(r0 + 2); tris.Add(r1 + 1); tris.Add(r1 + 2);
        }

        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh CreateFernClusterMesh()
    {
        Mesh m = new Mesh { name = "TropicalFernCluster" };
        const int blades = 7;
        const int segs = 3;
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        for (int b = 0; b < blades; b++)
        {
            float yaw = b * (Mathf.PI * 2f / blades) + 0.3f;
            Vector3 dir = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            int baseIdx = verts.Count;

            for (int s = 0; s <= segs; s++)
            {
                float t = s / (float)segs;
                float dist = t * (0.85f + (b % 2) * 0.25f);
                float h = Mathf.Sin(t * Mathf.PI * 0.78f) * 0.65f + t * 0.15f;
                float w = Mathf.Sin(Mathf.Clamp01(t * 0.85f + 0.15f) * Mathf.PI) * 0.22f;

                Vector3 spine = dir * dist + Vector3.up * h;
                verts.Add(spine - right * w);
                verts.Add(spine + Vector3.up * 0.04f);
                verts.Add(spine + right * w);

                uvs.Add(new Vector2(0f, t));
                uvs.Add(new Vector2(0.5f, t));
                uvs.Add(new Vector2(1f, t));
            }

            for (int s = 0; s < segs; s++)
            {
                int r0 = baseIdx + s * 3;
                int r1 = r0 + 3;
                tris.Add(r0); tris.Add(r1); tris.Add(r0 + 1);
                tris.Add(r0 + 1); tris.Add(r1); tris.Add(r1 + 1);
                tris.Add(r0 + 1); tris.Add(r1 + 1); tris.Add(r0 + 2);
                tris.Add(r0 + 2); tris.Add(r1 + 1); tris.Add(r1 + 2);
            }
        }

        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh CreateGrassTuftMesh()
    {
        Mesh m = new Mesh { name = "TropicalGrassTuft" };
        const int blades = 8;
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        for (int b = 0; b < blades; b++)
        {
            float yaw = b * (Mathf.PI * 2f / blades);
            float rad = 0.08f + (b % 3) * 0.10f;
            float h = 0.45f + (b % 2) * 0.22f;
            Vector3 center = new Vector3(Mathf.Cos(yaw) * rad, 0f, Mathf.Sin(yaw) * rad);
            Vector3 right = new Vector3(-Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) * 0.07f;
            Vector3 tip = center + new Vector3(Mathf.Cos(yaw) * 0.22f, h, Mathf.Sin(yaw) * 0.22f);

            int baseIdx = verts.Count;
            verts.Add(center - right);
            verts.Add(center + right);
            verts.Add(tip);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(0.5f, 1f));

            tris.Add(baseIdx); tris.Add(baseIdx + 2); tris.Add(baseIdx + 1);
        }

        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh GenerateSubdividedPlaneMesh(int res, float size)
    {
        Mesh m = new Mesh { name = "MassiveOceanMesh" };
        m.indexFormat = IndexFormat.UInt32;
        int vCount = (res + 1) * (res + 1);
        Vector3[] verts = new Vector3[vCount];
        Vector2[] uvs = new Vector2[vCount];
        int[] tris = new int[res * res * 6];

        int idx = 0;
        for (int z = 0; z <= res; z++)
        {
            for (int x = 0; x <= res; x++)
            {
                float u = x / (float)res;
                float v = z / (float)res;
                verts[idx] = new Vector3((u - 0.5f) * size, 0f, (v - 0.5f) * size);
                uvs[idx] = new Vector2(u * 30f, v * 30f);
                idx++;
            }
        }

        int t = 0;
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
            {
                int i = z * (res + 1) + x;
                tris[t++] = i;
                tris[t++] = i + res + 1;
                tris[t++] = i + 1;
                tris[t++] = i + 1;
                tris[t++] = i + res + 1;
                tris[t++] = i + res + 2;
            }
        }

        m.vertices = verts;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh CreateTaperedCylinderMesh(int segments, float bottomRadius, float topRadius, float height)
    {
        Mesh m = new Mesh();
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();

        for (int i = 0; i <= segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            float c = Mathf.Cos(a);
            float s = Mathf.Sin(a);
            verts.Add(new Vector3(c * bottomRadius, 0f, s * bottomRadius));
            verts.Add(new Vector3(c * topRadius, height, s * topRadius));
        }

        for (int i = 0; i < segments; i++)
        {
            int b0 = i * 2;
            int t0 = b0 + 1;
            int b1 = b0 + 2;
            int t1 = b0 + 3;
            tris.Add(b0); tris.Add(t0); tris.Add(b1);
            tris.Add(b1); tris.Add(t0); tris.Add(t1);
        }

        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh CreateHexCrystalMesh(float radius, float height)
    {
        Mesh m = new Mesh();
        const int sides = 6;
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();

        float shoulderH = height * 0.72f;
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            verts.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
            verts.Add(new Vector3(Mathf.Cos(a) * (radius * 0.85f), shoulderH, Mathf.Sin(a) * (radius * 0.85f)));
        }

        int tipIndex = verts.Count;
        verts.Add(new Vector3(0f, height, 0f));

        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            int b0 = i * 2;
            int t0 = b0 + 1;
            int b1 = next * 2;
            int t1 = b1 + 1;
            tris.Add(b0); tris.Add(t0); tris.Add(b1);
            tris.Add(b1); tris.Add(t0); tris.Add(t1);
            tris.Add(t0); tris.Add(tipIndex); tris.Add(t1);
        }

        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private Mesh CreateLowPolyRockMesh(float radius)
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Mesh src = temp.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] v = src.vertices;
        int[] t = src.triangles;
        DestroyImmediate(temp);

        for (int i = 0; i < v.Length; i++)
        {
            Vector3 dir = v[i].normalized;
            float n = Mathf.PerlinNoise(dir.x * 2.5f + 3.1f, dir.z * 2.5f + 5.7f);
            v[i] = dir * radius * Mathf.Lerp(0.72f, 1.18f, n);
            if (v[i].y < 0f) v[i].y *= 0.45f;
        }

        Mesh rock = new Mesh { vertices = v, triangles = t };
        rock.RecalculateNormals();
        rock.RecalculateBounds();
        return rock;
    }

    private Texture2D CreateNormalMapFromNoise(int size, float scale, float bumpStrength)
    {
        Texture2D normalTex = new Texture2D(size, size, TextureFormat.RGBA32, true, true);
        normalTex.wrapMode = TextureWrapMode.Repeat;
        normalTex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float hL = Mathf.PerlinNoise(((x - 1 + size) % size) / (float)size * scale, y / (float)size * scale);
                float hR = Mathf.PerlinNoise(((x + 1) % size) / (float)size * scale, y / (float)size * scale);
                float hD = Mathf.PerlinNoise(x / (float)size * scale, ((y - 1 + size) % size) / (float)size * scale);
                float hU = Mathf.PerlinNoise(x / (float)size * scale, ((y + 1) % size) / (float)size * scale);
                Vector3 n = new Vector3((hL - hR) * bumpStrength, (hD - hU) * bumpStrength, 1f).normalized;
                normalTex.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f));
            }
        }
        normalTex.Apply();
        return normalTex;
    }

    private Material CreatePBRMaterial(Color baseColor, float metallic, float smoothness, bool transparent = false, Color emissionColor = default)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        bool isURP = shader != null;
        if (!isURP) shader = Shader.Find("Standard");

        Material mat = new Material(shader)
        {
            enableInstancing = true
        };

        if (isURP)
        {
            mat.SetColor("_BaseColor", baseColor);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            if (transparent)
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = (int)RenderQueue.Transparent;
            }
        }
        else
        {
            mat.color = baseColor;
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Glossiness", smoothness);
        }

        if (emissionColor != default)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emissionColor);
        }

        return mat;
    }

    private void ApplyNormalMap(Material mat, Texture2D normalMap, float tiling, float scale)
    {
        if (mat == null || normalMap == null) return;
        mat.EnableKeyword("_NORMALMAP");
        if (mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", normalMap);
            mat.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
        }
        if (mat.HasProperty("_BumpScale"))
        {
            mat.SetFloat("_BumpScale", scale);
        }
    }

    private void AddBioGlowLight(Transform parent, Vector3 localPos, Color color, float range)
    {
        GameObject lightObj = new GameObject("BioGlow_PointLight");
        lightObj.transform.SetParent(parent, false);
        lightObj.transform.localPosition = localPos;
        Light pl = lightObj.AddComponent<Light>();
        pl.type = LightType.Point;
        pl.color = color;
        pl.range = range;
        pl.intensity = 1.8f;
        pl.shadows = LightShadows.None;
        _nightPointLights.Add(pl);
    }
}
