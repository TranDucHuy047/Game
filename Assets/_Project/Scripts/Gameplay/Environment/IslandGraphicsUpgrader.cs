using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.Building;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Environment;
using CoralArchipelago.Gameplay.Harvesting;
using CoralArchipelago.Gameplay.Inventory;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

/// <summary>
/// IslandGraphicsUpgrader (2.000m x 2.000m Open-World Continent - 5 Biomes & 16x16 Chunk Streaming):
/// - Generates a 2.000m x 2.000m continent divided into 16x16 = 256 chunks (125m x 125m per chunk).
/// - Keeps a 55m flat camp plateau around (0, 0) compatible with Altar, Chests, Floors, and Player Spawn.
/// - Divides the island into 5 distinct biomes: StarterCoralBay, AncientRainforest, CrystalCanyon,
///   MangroveSwamp, and VolcanicSummit (135m northern volcano + Ancient Summit Temple).
/// - Uses deterministic chunk seeds so all 10 iOS devices generate identical props with zero network bandwidth,
///   streams MeshColliders within 420m (including around connected players on Server/Host) and props within 260m,
///   enables GPU Instancing, and keeps Retina RenderScale = 1.0f under Nominal/Fair thermal states.
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
    public int chunkVertexResolution = 24;
    [Tooltip("Max elevation of Northern Volcanic Summit (meters).")]
    public float maxMountainHeight = 135f;
    [Tooltip("Radius around local player where biome props remain active (meters).")]
    public float propStreamingRadius = 260f;
    [Tooltip("Radius around players where terrain MeshColliders are enabled (meters).")]
    public float colliderStreamingRadius = 420f;
    public float waterLevel = -0.4f;
    public int worldSeed = 20261118;

    [Header("=== 3. SCENE / SHADER WIRING & RETINA iOS ===")]
    [SerializeField] private Transform existingOceanTransform;
    [SerializeField] private Material continentTerrainMaterial;
    [SerializeField] private Material oceanWaterMaterial;
    [SerializeField] private Material glowingCrystalMaterial;
    [Range(0f, 24f)] public float currentHour = 8.8f;
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
    private Material _rockMaterial;
    private Material _crystalMaterial;
    private Material _altarCoreMaterial;
    private Material _lampGlowMaterial;
    private Material _templePortalMaterial;
    private Light _directionalLight;

    // Shared procedural meshes reused across all 256 chunks with GPU instancing
    private Mesh _sharedTrunkMesh;
    private Mesh _sharedPalmFrondMesh;
    private Mesh _sharedJungleCanopyMesh;
    private Mesh _sharedCrystalShardMesh;
    private Mesh _sharedRockMesh;

    private Texture2D _sandAlbedo;
    private Texture2D _sandNormal;
    private Texture2D _barkNormal;
    private Texture2D _waterNormal;

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
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.High;
            QualitySettings.shadowDistance = 120f;
            QualitySettings.antiAliasing = 4;

            GenerateProceduralPBRTextures();
            BuildSharedMaterialsAndMeshes();
            SetupAtmosphereAndHorizonCamera();
            ForceSharpRetinaRenderScale();
        }

        // Build 16x16 terrain chunks & nearby colliders in Awake so Server/Host players never fall through frame 1
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
        UpdateOceanFollowPlayer(Time.time);

        if (lockRetinaRenderScale && Time.frameCount % 90 == 0)
        {
            ForceSharpRetinaRenderScale();
        }
    }

    // =========================================================================
    // 1. iOS RETINA 1.0 & 2.400M HORIZON CAMERA
    // =========================================================================
    private void ForceSharpRetinaRenderScale()
    {
        if (_isHeadlessServer)
            return;

        RenderPipelineAsset rpAsset = GraphicsSettings.currentRenderPipeline;
        if (rpAsset == null)
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

        Type urpType = rpAsset.GetType();
        PropertyInfo renderScaleProp = urpType.GetProperty("renderScale", BindingFlags.Public | BindingFlags.Instance);
        if (renderScaleProp != null && renderScaleProp.CanWrite)
        {
            renderScaleProp.SetValue(rpAsset, targetScale);
        }

        PropertyInfo hdrProp = urpType.GetProperty("supportsHDR", BindingFlags.Public | BindingFlags.Instance);
        if (hdrProp != null && hdrProp.CanWrite)
        {
            hdrProp.SetValue(rpAsset, true);
        }
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

        if (Camera.main != null)
        {
            Camera.main.allowHDR = true;
            Camera.main.allowMSAA = true;
            Camera.main.nearClipPlane = 0.2f;
            Camera.main.farClipPlane = 2400f;
        }
    }

    // =========================================================================
    // 2. SHARED PBR TEXTURES, GPU-INSTANCED MATERIALS & MESHES
    // =========================================================================
    private void GenerateProceduralPBRTextures()
    {
        _sandAlbedo = CreateNoiseAlbedoTexture(128, new Color(0.88f, 0.82f, 0.68f), 10f);
        _sandNormal = CreateNormalMapFromNoise(128, 14f, 2.1f);
        _barkNormal = CreateNormalMapFromNoise(128, 20f, 2.8f);
        _waterNormal = CreateNormalMapFromNoise(128, 10f, 1.6f);
    }

    private void BuildSharedMaterialsAndMeshes()
    {
        if (continentTerrainMaterial != null)
        {
            _terrainMaterial = new Material(continentTerrainMaterial) { enableInstancing = true };
        }
        else
        {
            Shader urpTerrainShader = Shader.Find("CoralArchipelago/URP/CoralIslandTerrainURP");
            if (urpTerrainShader != null)
            {
                _terrainMaterial = new Material(urpTerrainShader) { enableInstancing = true };
            }
            else
            {
                _terrainMaterial = CreatePBRMaterial(new Color(0.86f, 0.82f, 0.72f), 0.04f, 0.26f);
                _terrainMaterial.mainTexture = _sandAlbedo;
                _terrainMaterial.mainTextureScale = new Vector2(8f, 8f);
                ApplyNormalMap(_terrainMaterial, _sandNormal, 8f, 0.85f);
            }
        }

        if (_terrainMaterial.HasProperty("_UseVertexBiomeColor"))
        {
            _terrainMaterial.SetFloat("_UseVertexBiomeColor", 1.0f);
        }

        if (oceanWaterMaterial != null)
        {
            _oceanMaterial = oceanWaterMaterial;
            _oceanMaterial.enableInstancing = true;
        }
        else
        {
            Shader urpWaterShader = Shader.Find("CoralArchipelago/URP/CoralOceanWaterURP");
            if (urpWaterShader != null)
            {
                _oceanMaterial = new Material(urpWaterShader) { enableInstancing = true };
            }
            else
            {
                _oceanMaterial = CreatePBRMaterial(new Color(0.04f, 0.44f, 0.60f, 0.88f), 0.18f, 0.92f, transparent: true);
                ApplyNormalMap(_oceanMaterial, _waterNormal, 45f, 0.7f);
            }
        }

        _barkMaterial = CreatePBRMaterial(new Color(0.42f, 0.29f, 0.18f), 0.02f, 0.22f);
        ApplyNormalMap(_barkMaterial, _barkNormal, 5f, 1.0f);

        _palmLeafMaterial = CreatePBRMaterial(new Color(0.18f, 0.58f, 0.24f), 0.04f, 0.46f);
        _jungleLeafMaterial = CreatePBRMaterial(new Color(0.11f, 0.42f, 0.18f), 0.04f, 0.38f);
        _rockMaterial = CreatePBRMaterial(new Color(0.35f, 0.38f, 0.42f), 0.10f, 0.35f);
        ApplyNormalMap(_rockMaterial, _sandNormal, 6f, 1.1f);

        _crystalMaterial = glowingCrystalMaterial != null
            ? new Material(glowingCrystalMaterial) { enableInstancing = true }
            : CreatePBRMaterial(
                new Color(0.14f, 0.84f, 1.0f),
                0.35f,
                0.92f,
                emissionColor: new Color(0.08f, 0.88f, 1.0f) * 1.8f);

        _altarCoreMaterial = CreatePBRMaterial(
            new Color(0.16f, 0.98f, 0.82f),
            0.25f,
            0.95f,
            emissionColor: new Color(0.18f, 0.95f, 0.78f) * 2.2f);

        _lampGlowMaterial = CreatePBRMaterial(
            new Color(1.0f, 0.78f, 0.32f),
            0.1f,
            0.9f,
            emissionColor: new Color(1.0f, 0.72f, 0.28f) * 2.4f);

        _templePortalMaterial = CreatePBRMaterial(
            new Color(1.0f, 0.48f, 0.16f),
            0.3f,
            0.9f,
            emissionColor: new Color(1.0f, 0.38f, 0.08f) * 2.6f);

        _sharedTrunkMesh = CreateTaperedCylinderMesh(8, 0.28f, 0.14f, 3.4f);
        _sharedPalmFrondMesh = CreateCurvedPalmFrondMesh();
        _sharedJungleCanopyMesh = CreateTaperedCylinderMesh(9, 2.2f, 0.2f, 2.6f);
        _sharedCrystalShardMesh = CreateHexCrystalMesh(0.28f, 1.45f);
        _sharedRockMesh = CreateLowPolyRockMesh(0.85f);
    }

    // =========================================================================
    // 3. 2000M x 2000M CONTINENT (16x16 CHUNKS & 5 BIOMES)
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

        if (!_isHeadlessServer)
        {
            BuildNorthernSummitTempleLandmark(new Vector3(0f, EvaluateHeightOnly(0f, 480f), 480f));
        }
    }

    private void SetupGiantOceanSurface()
    {
        if (existingOceanTransform != null)
        {
            _oceanTransform = existingOceanTransform;
        }
        else
        {
            GameObject existingGo = GameObject.Find("OceanSurface");
            if (existingGo != null)
                _oceanTransform = existingGo.transform;
        }

        Mesh largeOceanMesh = GenerateSubdividedPlaneMesh(96, worldSize * 1.8f);

        if (_oceanTransform != null)
        {
            MeshFilter of = _oceanTransform.GetComponent<MeshFilter>();
            if (of == null) of = _oceanTransform.gameObject.AddComponent<MeshFilter>();
            of.sharedMesh = largeOceanMesh;

            MeshRenderer or = _oceanTransform.GetComponent<MeshRenderer>();
            if (or == null) or = _oceanTransform.gameObject.AddComponent<MeshRenderer>();
            if (or.sharedMaterial == null) or.sharedMaterial = _oceanMaterial;
            or.shadowCastingMode = ShadowCastingMode.Off;
        }
        else
        {
            GameObject oceanObj = new GameObject("Massive_Ocean_Surface");
            oceanObj.transform.SetParent(transform, false);
            oceanObj.transform.position = new Vector3(0f, waterLevel, 0f);
            _oceanTransform = oceanObj.transform;

            MeshFilter of = oceanObj.AddComponent<MeshFilter>();
            MeshRenderer or = oceanObj.AddComponent<MeshRenderer>();
            of.sharedMesh = largeOceanMesh;
            or.sharedMaterial = _oceanMaterial;
            or.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    /// <summary>
    /// Evaluates height and biome at (worldX, worldZ) across the 2000m x 2000m continent.
    /// Keeps a flat 55m camp plateau around (0, 0) compatible with Altar, Chests, Floors, and Spawn.
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

        // Smooth coastline Perlin warp disabled near starter camp center (<=55m) for guaranteed flat foundation
        float warpWeight = Mathf.SmoothStep(0.15f, 0.55f, normalizedRadius);
        float coastlineWarp = (Mathf.PerlinNoise(worldX * 0.0018f + 3.1f, worldZ * 0.0018f + 7.4f) - 0.5f) * 0.22f * warpWeight;
        float islandMask = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Max(0f, normalizedRadius + coastlineWarp), 2.15f));

        if (islandMask <= 0.05f)
        {
            finalHeight = -3.5f;
        }
        else
        {
            // Keep Starter Coral Bay camp plateau (radius 55m around 0,0) flat & smooth
            float starterFlatMask = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((distFromCenter - 55f) / 65f));

            float nLarge = Mathf.PerlinNoise(worldX * 0.0016f + 11.2f, worldZ * 0.0016f + 5.7f);
            float nMed = Mathf.PerlinNoise(worldX * 0.0045f + 23.4f, worldZ * 0.0045f + 19.1f) * 0.45f;
            float nDetail = Mathf.PerlinNoise(worldX * 0.015f + 41.0f, worldZ * 0.015f + 63.2f) * 0.15f;
            float baseElevation = (nLarge + nMed + nDetail) * 26f * starterFlatMask;

            float distNorthPeak = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(0f, 480f));
            float volcanoFactor = Mathf.Clamp01(1f - distNorthPeak / 390f);
            float volcanoHeight = Mathf.Pow(volcanoFactor, 1.85f) * peakHeight;

            float distEastCanyon = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(380f, 40f));
            float canyonFactor = Mathf.Clamp01(1f - distEastCanyon / 290f);
            float ridgeNoise = Mathf.Abs(Mathf.PerlinNoise(worldX * 0.008f, worldZ * 0.008f) - 0.5f) * 2f;
            float canyonHeight = canyonFactor * ridgeNoise * 48f * starterFlatMask;

            float distSwamp = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(260f, -260f));
            float swampFactor = Mathf.Clamp01(1f - distSwamp / 250f);

            finalHeight = (1.35f + baseElevation + volcanoHeight + canyonHeight) * islandMask - (swampFactor * 2.2f);
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

        if (distFromCenter < 140f || (finalHeight < 1.6f && sFac < 0.35f))
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
        Color coastalGrass = new Color(0.32f, 0.62f, 0.28f);
        Color deepJungle = new Color(0.14f, 0.42f, 0.18f);
        Color swampMud = new Color(0.28f, 0.35f, 0.22f);
        Color canyonSlate = new Color(0.30f, 0.44f, 0.56f);
        Color volcanicBasalt = new Color(0.24f, 0.23f, 0.26f);

        vertexColor = sandCol;
        if (finalHeight > 0.4f)
        {
            vertexColor = Color.Lerp(sandCol, coastalGrass, Mathf.Clamp01((finalHeight - 0.4f) / 3.5f));
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

        // Deterministic chunk seed keeps 10 iOS players synchronized without network traffic
        int chunkSeed = worldSeed ^ (chunk.coord.x * 73856093) ^ (chunk.coord.y * 19349663);
        System.Random rng = new System.Random(chunkSeed);

        const int propCount = 18;
        for (int i = 0; i < propCount; i++)
        {
            float lx = (float)rng.NextDouble() * chunkSize;
            float lz = (float)rng.NextDouble() * chunkSize;
            float wx = originX + lx;
            float wz = originZ + lz;

            // Keep central camp plateau clear (radius 32m around 0,0)
            if (wx * wx + wz * wz < 32f * 32f)
                continue;

            float wy = EvaluateHeightOnly(wx, wz);
            if (wy < waterLevel + 0.35f)
                continue;

            EvaluateBiomeAndHeight(wx, wz, out BiomeType biome, out _);
            Vector3 localPos = new Vector3(lx, wy, lz);
            float scale = 0.85f + (float)rng.NextDouble() * 0.55f;
            float rotY = (float)rng.NextDouble() * 360f;

            switch (biome)
            {
                case BiomeType.StarterCoralBay:
                    if (i % 3 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale, PickSeededPrefab(treePrefabs, rng), BuildFastPalmTree);
                    else if (i % 5 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 0.9f, null, BuildFastBoulder);
                    break;

                case BiomeType.AncientRainforest:
                    if (i % 4 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.45f, PickSeededPrefab(treePrefabs, rng), BuildFastJungleGiantTree);
                    else
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.1f, PickSeededPrefab(treePrefabs, rng), BuildFastPalmTree);
                    break;

                case BiomeType.CrystalCanyon:
                    if (i % 2 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.3f, PickSeededPrefab(crystalPrefabs, rng), BuildFastCrystalCluster);
                    else
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.25f, null, BuildFastBoulder);
                    break;

                case BiomeType.MangroveSwamp:
                    if (i % 2 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.15f, PickSeededPrefab(treePrefabs, rng), BuildFastJungleGiantTree);
                    else
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 0.8f, PickSeededPrefab(crystalPrefabs, rng), BuildFastCrystalCluster);
                    break;

                case BiomeType.VolcanicSummit:
                    if (i % 3 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.5f, null, BuildFastBoulder);
                    else if (i % 4 == 0)
                        SpawnChunkProp(root.transform, localPos, rotY, scale * 1.2f, PickSeededPrefab(crystalPrefabs, rng), BuildFastCrystalCluster);
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

            // Strip gameplay/network scripts from cosmetic streaming props so they never spawn duplicate NGO objects
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

    private void BuildFastPalmTree(Transform parent)
    {
        CreateMeshPart("PalmTrunk", parent, _sharedTrunkMesh, _barkMaterial);
        for (int f = 0; f < 6; f++)
        {
            GameObject frond = CreateMeshPart("Frond_" + f, parent, _sharedPalmFrondMesh, _palmLeafMaterial);
            frond.transform.localPosition = new Vector3(0f, 3.25f, 0f);
            frond.transform.localRotation = Quaternion.Euler(20f, f * 60f, 0f);
        }
    }

    private void BuildFastJungleGiantTree(Transform parent)
    {
        GameObject trunk = CreateMeshPart("JungleTrunk", parent, _sharedTrunkMesh, _barkMaterial);
        trunk.transform.localScale = new Vector3(1.5f, 1.4f, 1.5f);

        for (int c = 0; c < 3; c++)
        {
            GameObject canopy = CreateMeshPart("Canopy_" + c, parent, _sharedJungleCanopyMesh, _jungleLeafMaterial);
            canopy.transform.localPosition = new Vector3(0f, 3.2f + c * 1.15f, 0f);
            canopy.transform.localScale = Vector3.one * (1.25f - c * 0.22f);
        }
    }

    private void BuildFastCrystalCluster(Transform parent)
    {
        CreateMeshPart("CrystalRockBase", parent, _sharedRockMesh, _rockMaterial);
        for (int s = 0; s < 3; s++)
        {
            GameObject shard = CreateMeshPart("Shard_" + s, parent, _sharedCrystalShardMesh, _crystalMaterial);
            shard.transform.localPosition = new Vector3((s - 1) * 0.22f, 0.1f, (s % 2) * 0.18f);
            shard.transform.localRotation = Quaternion.Euler((s - 1) * 18f, s * 110f, (1 - s) * 14f);
            shard.transform.localScale = Vector3.one * (1.1f - s * 0.18f);
        }
    }

    private void BuildFastBoulder(Transform parent)
    {
        GameObject r = CreateMeshPart("Boulder", parent, _sharedRockMesh, _rockMaterial);
        r.transform.localScale = new Vector3(1.3f, 0.85f, 1.15f);
    }

    private void BuildNorthernSummitTempleLandmark(Vector3 summitPos)
    {
        GameObject temple = new GameObject("Northern_Summit_Ancient_Temple");
        temple.transform.SetParent(transform, false);
        temple.transform.position = summitPos;

        for (int p = 0; p < 6; p++)
        {
            float ang = p * 60f * Mathf.Deg2Rad;
            GameObject pillar = CreateMeshPart("TemplePillar_" + p, temple.transform, _sharedTrunkMesh, _rockMaterial);
            pillar.transform.localPosition = new Vector3(Mathf.Cos(ang) * 8f, 0f, Mathf.Sin(ang) * 8f);
            pillar.transform.localScale = new Vector3(2.8f, 3.2f, 2.8f);
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

    private void UpdateOceanFollowPlayer(float time)
    {
        if (_oceanMaterial != null && _oceanMaterial.HasProperty("_BumpMap"))
        {
            _oceanMaterial.SetTextureOffset("_BumpMap", new Vector2(time * 0.018f, time * 0.014f));
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
                    Mathf.Max(14f, Mathf.Abs(sunPitch)),
                    -38f + currentHour * 6f,
                    0f);

                Color noonColor = new Color(1.0f, 0.96f, 0.88f);
                Color sunsetColor = new Color(1.0f, 0.56f, 0.34f);
                Color nightMoonColor = new Color(0.24f, 0.42f, 0.78f);

                _directionalLight.color = dayFactor > 0.35f
                    ? Color.Lerp(sunsetColor, noonColor, (dayFactor - 0.35f) / 0.65f)
                    : Color.Lerp(nightMoonColor, sunsetColor, dayFactor / 0.35f);
                _directionalLight.intensity = Mathf.Lerp(0.42f, 1.45f, dayFactor);
            }

            RenderSettings.ambientSkyColor = Color.Lerp(new Color(0.38f, 0.69f, 0.96f), new Color(0.06f, 0.18f, 0.34f), nightFactor);
            RenderSettings.ambientEquatorColor = Color.Lerp(new Color(0.75f, 0.85f, 0.92f), new Color(0.05f, 0.14f, 0.26f), nightFactor);
            RenderSettings.ambientGroundColor = Color.Lerp(new Color(0.35f, 0.32f, 0.26f), new Color(0.03f, 0.06f, 0.11f), nightFactor);
            RenderSettings.fogColor = Color.Lerp(new Color(0.56f, 0.82f, 0.96f), new Color(0.03f, 0.09f, 0.18f), nightFactor);
        }

        float pulse = 0.86f + 0.22f * Mathf.Sin(Time.time * 2.3f);
        float glowPower = Mathf.Lerp(0.55f, 3.4f, nightFactor) * pulse;

        if (_crystalMaterial != null && _crystalMaterial.HasProperty("_EmissionColor"))
            _crystalMaterial.SetColor("_EmissionColor", new Color(0.08f, 0.88f, 1.0f) * glowPower);
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
            _nightPointLights[i].intensity = Mathf.Lerp(0.7f, 2.9f, nightFactor) * pulse;
        }
    }

    // =========================================================================
    // 6. SAFE UPGRADE FOR ANY LEFTOVER PRIMITIVE PLACEHOLDERS
    // =========================================================================
    public void UpgradeExistingPrimitivesInScene()
    {
        if (NetworkPlayerController.LocalPlayer != null)
        {
            _playerTransform = NetworkPlayerController.LocalPlayer.transform;
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
                ReplaceWithVisualModel(go, mr, playerVisualPrefab, BuildStylizedExplorerCharacterVisual);
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
            target.GetComponentInParent<NetworkPlayerController>() != null)
        {
            return true;
        }

        string rootName = target.root.name;
        return rootName.StartsWith("PF_") || rootName.Contains("Temple");
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
    // 7. PROCEDURAL VISUAL FALLBACKS (ALTAR, CHEST, LAMP, FLOOR, CHARACTERS)
    // =========================================================================
    private void BuildAncientAltarVisual(Transform parent)
    {
        for (int s = 0; s < 3; s++)
        {
            float rad = 1.9f - s * 0.38f;
            GameObject step = CreateMeshPart("AltarStep_" + s, parent, CreateTaperedCylinderMesh(12, rad, rad * 0.92f, 0.35f), _rockMaterial);
            step.transform.localPosition = new Vector3(0f, -0.85f + s * 0.34f, 0f);
        }

        for (int p = 0; p < 4; p++)
        {
            float ang = p * 90f * Mathf.Deg2Rad;
            GameObject pillar = CreateMeshPart("AltarPillar_" + p, parent, CreateTaperedCylinderMesh(8, 0.2f, 0.15f, 1.5f), _rockMaterial);
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

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(box.GetComponent<Collider>());
        box.transform.SetParent(parent, false);
        box.transform.localPosition = new Vector3(0f, -0.15f, 0f);
        box.transform.localScale = new Vector3(0.95f, 0.55f, 0.65f);
        box.GetComponent<MeshRenderer>().sharedMaterial = _barkMaterial;

        GameObject lockPlate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(lockPlate.GetComponent<Collider>());
        lockPlate.transform.SetParent(parent, false);
        lockPlate.transform.localPosition = new Vector3(0f, 0.05f, 0.34f);
        lockPlate.transform.localScale = new Vector3(0.16f, 0.24f, 0.06f);
        lockPlate.GetComponent<MeshRenderer>().sharedMaterial = goldTrimMat;
    }

    private void BuildBioluminescentLanternVisual(Transform parent)
    {
        GameObject post = CreateMeshPart("LanternPost", parent, CreateTaperedCylinderMesh(8, 0.1f, 0.08f, 1.3f), _rockMaterial);
        post.transform.localPosition = new Vector3(0f, -0.45f, 0f);

        GameObject core = CreateMeshPart("LanternBulb", parent, _sharedCrystalShardMesh, _lampGlowMaterial);
        core.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        core.transform.localScale = Vector3.one * 0.45f;

        AddBioGlowLight(parent, new Vector3(0f, 0.5f, 0f), new Color(1.0f, 0.76f, 0.36f), 8.0f);
    }

    private void BuildDetailedStoneWoodDeckVisual(Transform parent)
    {
        for (int i = -2; i <= 2; i++)
        {
            GameObject plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(plank.GetComponent<Collider>());
            plank.transform.SetParent(parent, false);
            plank.transform.localPosition = new Vector3(0f, 0f, i * 0.76f);
            plank.transform.localScale = new Vector3(3.8f, 0.22f, 0.70f);
            plank.GetComponent<MeshRenderer>().sharedMaterial = _barkMaterial;
        }
    }

    private void BuildStylizedExplorerCharacterVisual(Transform parent)
    {
        Material suitMat = CreatePBRMaterial(new Color(0.18f, 0.36f, 0.54f), 0.15f, 0.55f);
        Material skinMat = CreatePBRMaterial(new Color(0.92f, 0.74f, 0.62f), 0.02f, 0.35f);

        GameObject torso = CreateMeshPart("ExplorerTorso", parent, CreateTaperedCylinderMesh(10, 0.32f, 0.26f, 0.85f), suitMat);
        torso.transform.localPosition = new Vector3(0f, -0.25f, 0f);

        GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(head.GetComponent<Collider>());
        head.transform.SetParent(parent, false);
        head.transform.localPosition = new Vector3(0f, 0.45f, 0f);
        head.transform.localScale = new Vector3(0.46f, 0.48f, 0.46f);
        head.GetComponent<MeshRenderer>().sharedMaterial = skinMat;

        GameObject backpack = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(backpack.GetComponent<Collider>());
        backpack.transform.SetParent(parent, false);
        backpack.transform.localPosition = new Vector3(0f, 0.05f, -0.26f);
        backpack.transform.localScale = new Vector3(0.42f, 0.52f, 0.24f);
        backpack.GetComponent<MeshRenderer>().sharedMaterial = _barkMaterial;
    }

    private void BuildCoralGuardianMonsterVisual(Transform parent)
    {
        Material shellMat = CreatePBRMaterial(new Color(0.68f, 0.18f, 0.22f), 0.25f, 0.68f);
        GameObject carapace = CreateMeshPart("MonsterCarapace", parent, _sharedRockMesh, shellMat);
        carapace.transform.localPosition = Vector3.zero;
        carapace.transform.localScale = new Vector3(1.1f, 0.68f, 1.05f);
    }

    // =========================================================================
    // 8. SHARED MESH & MATERIAL BUILDERS (GPU INSTANCING ENABLED FOR METAL)
    // =========================================================================
    private GameObject CreateMeshPart(string name, Transform parent, Mesh mesh, Material mat)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mf.sharedMesh = mesh;
        mr.sharedMaterial = mat;
        return go;
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

    private Mesh CreateCurvedPalmFrondMesh()
    {
        Mesh m = new Mesh();
        const int segs = 5;
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();

        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            float z = t * 2.2f;
            float y = Mathf.Sin(t * Mathf.PI) * 0.35f - t * t * 0.45f;
            float width = Mathf.Sin(t * Mathf.PI) * 0.5f + 0.05f;
            verts.Add(new Vector3(-width, y, z));
            verts.Add(new Vector3(0f, y + 0.06f, z));
            verts.Add(new Vector3(width, y, z));
        }

        for (int i = 0; i < segs; i++)
        {
            int r0 = i * 3;
            int r1 = (i + 1) * 3;
            tris.Add(r0); tris.Add(r1); tris.Add(r0 + 1);
            tris.Add(r0 + 1); tris.Add(r1); tris.Add(r1 + 1);
            tris.Add(r0 + 1); tris.Add(r1 + 1); tris.Add(r0 + 2);
            tris.Add(r0 + 2); tris.Add(r1 + 1); tris.Add(r1 + 2);

            tris.Add(r0); tris.Add(r0 + 1); tris.Add(r1);
            tris.Add(r0 + 1); tris.Add(r1 + 1); tris.Add(r1);
            tris.Add(r0 + 1); tris.Add(r0 + 2); tris.Add(r1 + 1);
            tris.Add(r0 + 2); tris.Add(r1 + 2); tris.Add(r1 + 1);
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

    private Texture2D CreateNoiseAlbedoTexture(int size, Color baseCol, float scale)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float n = Mathf.PerlinNoise(x / (float)size * scale, y / (float)size * scale);
                Color c = Color.Lerp(baseCol * 0.9f, baseCol * 1.08f, n);
                c.a = 0.25f;
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        return tex;
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
        pl.intensity = 1.6f;
        pl.shadows = LightShadows.None;
        _nightPointLights.Add(pl);
    }
}
