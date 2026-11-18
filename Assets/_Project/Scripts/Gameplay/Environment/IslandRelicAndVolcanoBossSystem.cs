using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

/// <summary>
/// IslandRelicAndVolcanoBossSystem (Auto-Bootstrap & Unity Editor Quick Test + 10-Player Co-op Raid Boss):
/// - Automatically initializes at runtime via [RuntimeInitializeOnLoadMethod(AfterSceneLoad)] even on an empty scene.
/// - Attaches IslandGraphicsUpgrader (2000m x 2000m 5 Biomes), IslandFastTravelAndRaftSystem, and
///   IslandRelicAndVolcanoBossSystem, spawning a WASD Explorer player if the scene lacks one.
/// - Places 5 HDR Ancient Relic Shrines across the 5 Biomes, tracks 0/5 -> 5/5 progress (logged to Camp Log),
///   scales Boss Ignis Leviathan MaxHP & Damage dynamically for 1-10 online players,
///   runs 3 combat phases with expanding red ground AoE Telegraph Rings, and includes an on-screen
///   "UNITY EDITOR QUICK TEST" toolbar.
/// </summary>
[DisallowMultipleComponent]
public class IslandRelicAndVolcanoBossSystem : MonoBehaviour
{
    public static IslandRelicAndVolcanoBossSystem Instance { get; private set; }

    private static bool _sceneHookRegistered;

    // =========================================================================
    // 0. RUNTIME AUTO-BOOTSTRAP ON PLAY (▶) IN UNITY EDITOR OR PLAYER
    // =========================================================================
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoBootstrapInUnityEditor()
    {
        if (!_sceneHookRegistered)
        {
            SceneManager.sceneLoaded += OnSceneLoadedAutoBootstrap;
            _sceneHookRegistered = true;
        }

        EnsureContinentSystemsActive(SceneManager.GetActiveScene());
    }

    private static void OnSceneLoadedAutoBootstrap(Scene scene, LoadSceneMode mode)
    {
        EnsureContinentSystemsActive(scene);
    }

    private static void EnsureContinentSystemsActive(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return;

        // Avoid spawning world chunks or explorer capsules inside the Login/Lobby Bootstrap scene
        if (scene.name.Contains("Bootstrap") && WorldTimeManager.Instance == null && GameObject.Find("WorldManagers") == null)
            return;

        IslandGraphicsUpgrader upgrader = FindFirstObjectByType<IslandGraphicsUpgrader>();
        GameObject hubObj;

        if (upgrader == null)
        {
            hubObj = GameObject.Find("IslandWorldUpgrader_2000m");
            if (hubObj == null)
                hubObj = new GameObject("CoralArchipelago_AutoSystemHub");

            upgrader = hubObj.AddComponent<IslandGraphicsUpgrader>();
        }
        else
        {
            hubObj = upgrader.gameObject;
        }

        IslandFastTravelAndRaftSystem fastTravel = FindFirstObjectByType<IslandFastTravelAndRaftSystem>();
        if (fastTravel == null)
        {
            fastTravel = hubObj.AddComponent<IslandFastTravelAndRaftSystem>();
        }

        IslandRelicAndVolcanoBossSystem relicBoss = FindFirstObjectByType<IslandRelicAndVolcanoBossSystem>();
        if (relicBoss == null)
        {
            relicBoss = hubObj.AddComponent<IslandRelicAndVolcanoBossSystem>();
        }

        if (!NetworkBootstrap.IsRunningAsDedicatedServer())
        {
            EnsurePlayableExplorerExists(upgrader);
        }
    }

    private static void EnsurePlayableExplorerExists(IslandGraphicsUpgrader upgrader)
    {
        CharacterController existingCC = FindFirstObjectByType<CharacterController>();
        GameObject taggedPlayer = null;
        try { taggedPlayer = GameObject.FindWithTag("Player"); } catch { /* ignore if tag missing */ }

        if (existingCC != null || taggedPlayer != null)
            return;

        // If Unity Netcode Host/Client already exists and is actively starting in world, don't duplicate
        if (Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsListening)
            return;

        GameObject playerGo = new GameObject("Player_Explorer_Auto");
        try { playerGo.tag = "Player"; } catch { /* ignore */ }

        CharacterController cc = playerGo.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.38f;
        cc.center = new Vector3(0f, 0.9f, 0f);

        float startY = upgrader != null
            ? upgrader.EvaluateHeightOnly(0f, -8f)
            : IslandGraphicsUpgrader.EvaluateWorldHeightStatic(0f, -8f);
        playerGo.transform.position = new Vector3(0f, startY, -8f);

        if (upgrader != null)
        {
            upgrader.AttachSoldierVisualToPlayer(playerGo);
        }

        playerGo.AddComponent<CoralArchipelago.Gameplay.Visuals.CharacterAnimationSync>();
        playerGo.AddComponent<EditorQuickTestPlayerMotor>();
    }

    [Serializable]
    public class AncientRelicData
    {
        public string relicId;
        public string relicName;
        public string biomeName;
        public Vector2 worldXZ;
        public Color hdrColor;
        public bool isCollected;

        [NonSerialized] public Vector3 worldPosition;
        [NonSerialized] public Transform shrineRoot;
        [NonSerialized] public Transform floatingRuneMesh;
        [NonSerialized] public Light runeLight;
    }

    public enum BossState
    {
        Dormant = 0,
        Summoning = 1,
        CombatPhase1 = 2,
        CombatPhase2 = 3,
        CombatPhase3 = 4,
        Defeated = 5
    }

    [Header("=== 1. CẤU HÌNH 5 MẢNH CỔ NGỮ (5 BIOME RELICS) ===")]
    public float relicInteractRadius = 4.5f;
    public List<AncientRelicData> relics = new List<AncientRelicData>();

    [Header("=== 2. CẤU HÌNH BOSS ĐỈNH NÚI LỬA & CO GIÃN 1-10 NGƯỜI ===")]
    public Vector2 summitArenaXZ = new Vector2(0f, 480f);
    public float bossBaseMaxHP = 1500f;
    public float bossBaseDamage = 18f;
    [Range(1, 10)] public int currentOnlinePlayers = 1;
    public float playerAttackDamage = 75f;
    public float playerAttackRange = 9.5f;
    public float playerAttackCooldown = 0.45f;
    public GameObject customBossPrefab;

    private bool _isHeadlessServer;
    private bool _hasMobileHud;
    private BossState _bossState = BossState.Dormant;
    private float _bossCurrentHP;
    private float _bossScaledMaxHP;
    private float _bossScaledDamage;
    private Transform _bossRoot;
    private readonly List<Transform> _bossOrbitShards = new List<Transform>();
    private float _bossActionTimer;
    private float _lastPlayerAttackTime;

    private GameObject _telegraphRingObj;
    private Material _telegraphMaterial;
    private bool _isChargingAoE;
    private float _aoeTimer;
    private float _aoeDuration = 1.65f;
    private float _aoeRadius = 7.5f;
    private Vector3 _aoeTargetCenter;

    private IslandGraphicsUpgrader _worldUpgrader;
    private IslandFastTravelAndRaftSystem _fastTravelSystem;
    private Transform _playerTransform;
    private NetworkPlayerController _networkPlayer;
    private AncientRelicData _nearbyRelic;
    private bool _isNearSummitAltar;
    private string _bannerMessage = "";
    private float _bannerTimer;
    private bool _showEditorTestBar = true;

    private Material _shrineStoneMat;
    private Material _bossObsidianMat;
    private Material _bossMagmaCoreMat;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        _isHeadlessServer = NetworkBootstrap.IsRunningAsDedicatedServer();
        InitializeDefaultRelicsIfNeeded();
    }

    private void Start()
    {
        _worldUpgrader = IslandGraphicsUpgrader.Instance != null
            ? IslandGraphicsUpgrader.Instance
            : FindFirstObjectByType<IslandGraphicsUpgrader>();
        _fastTravelSystem = IslandFastTravelAndRaftSystem.Instance != null
            ? IslandFastTravelAndRaftSystem.Instance
            : FindFirstObjectByType<IslandFastTravelAndRaftSystem>();

        RecalculateBossScaling(currentOnlinePlayers);

        if (_isHeadlessServer)
            return;

        _hasMobileHud = GameObject.Find("MobileHUD") != null;
        BuildMaterials();
        SpawnAllRelicShrines();
        CreateTelegraphRingIndicator();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        SyncOnlinePlayerCountFromDirector();

        if (_isHeadlessServer)
            return;

        EnsurePlayerReference();
        if (_playerTransform == null)
            return;

        float time = Time.time;
        UpdateRelicShrinesAndProximity(time);
        UpdateSummitBossLogic(time);

        if (_bannerTimer > 0f)
        {
            _bannerTimer -= Time.deltaTime;
        }
    }

    private void SyncOnlinePlayerCountFromDirector()
    {
        if (PvEDirector.Instance != null && PvEDirector.Instance.IsSpawned)
        {
            int netPlayers = Mathf.Clamp(PvEDirector.Instance.ActivePlayerCount.Value, 1, 10);
            if (netPlayers != currentOnlinePlayers)
            {
                RecalculateBossScaling(netPlayers);
            }
        }
    }

    // =========================================================================
    // 1. KHỞI TẠO 5 ĐÀN TẾ CỔ NGỮ TẠI 5 QUẦN XÃ TRÊN ĐẢO 2000M x 2000M
    // =========================================================================
    private void InitializeDefaultRelicsIfNeeded()
    {
        if (relics != null && relics.Count == 5)
            return;

        relics = new List<AncientRelicData>
        {
            new AncientRelicData
            {
                relicId = "relic_coral",
                relicName = "Cổ Ngữ San Hô (Coral Rune)",
                biomeName = "Vịnh Căn Cứ Altar",
                worldXZ = new Vector2(-44f, 42f),
                hdrColor = new Color(0.12f, 0.96f, 0.84f),
                isCollected = PlayerPrefs.GetInt("RELIC_relic_coral", 0) == 1
            },
            new AncientRelicData
            {
                relicId = "relic_sylvan",
                relicName = "Cổ Ngữ Rừng Già (Sylvan Rune)",
                biomeName = "Rừng Già Nhiệt Đới",
                worldXZ = new Vector2(-355f, 102f),
                hdrColor = new Color(0.22f, 0.98f, 0.35f),
                isCollected = PlayerPrefs.GetInt("RELIC_relic_sylvan", 0) == 1
            },
            new AncientRelicData
            {
                relicId = "relic_astral",
                relicName = "Cổ Ngữ Tinh Tú (Astral Crystal Rune)",
                biomeName = "Hẻm Núi Pha Lê",
                worldXZ = new Vector2(382f, 52f),
                hdrColor = new Color(0.15f, 0.78f, 1.0f),
                isCollected = PlayerPrefs.GetInt("RELIC_relic_astral", 0) == 1
            },
            new AncientRelicData
            {
                relicId = "relic_tidal",
                relicName = "Cổ Ngữ Thủy Triều (Tidal Swamp Rune)",
                biomeName = "Đầm Lầy Ngập Mặn",
                worldXZ = new Vector2(262f, -258f),
                hdrColor = new Color(0.95f, 0.82f, 0.18f),
                isCollected = PlayerPrefs.GetInt("RELIC_relic_tidal", 0) == 1
            },
            new AncientRelicData
            {
                relicId = "relic_obsidian",
                relicName = "Cổ Ngữ Dung Nham (Obsidian Flame Rune)",
                biomeName = "Sườn Núi Lửa Cổ Đại",
                worldXZ = new Vector2(-22f, 415f),
                hdrColor = new Color(1.0f, 0.38f, 0.12f),
                isCollected = PlayerPrefs.GetInt("RELIC_relic_obsidian", 0) == 1
            }
        };
    }

    private void BuildMaterials()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        bool isURP = shader.name.Contains("Universal");

        _shrineStoneMat = new Material(shader) { enableInstancing = true };
        _bossObsidianMat = new Material(shader) { enableInstancing = true };
        _bossMagmaCoreMat = new Material(shader) { enableInstancing = true };
        _bossMagmaCoreMat.EnableKeyword("_EMISSION");

        if (isURP)
        {
            _shrineStoneMat.SetColor("_BaseColor", new Color(0.32f, 0.36f, 0.40f));
            _shrineStoneMat.SetFloat("_Smoothness", 0.4f);

            _bossObsidianMat.SetColor("_BaseColor", new Color(0.16f, 0.15f, 0.18f));
            _bossObsidianMat.SetFloat("_Metallic", 0.45f);
            _bossObsidianMat.SetFloat("_Smoothness", 0.65f);

            _bossMagmaCoreMat.SetColor("_BaseColor", new Color(1.0f, 0.42f, 0.12f));
            _bossMagmaCoreMat.SetColor("_EmissionColor", new Color(1.0f, 0.35f, 0.08f) * 3.2f);
        }
        else
        {
            _shrineStoneMat.color = new Color(0.32f, 0.36f, 0.40f);
            _bossObsidianMat.color = new Color(0.16f, 0.15f, 0.18f);
            _bossMagmaCoreMat.color = new Color(1.0f, 0.42f, 0.12f);
            _bossMagmaCoreMat.SetColor("_EmissionColor", new Color(1.0f, 0.35f, 0.08f) * 3.2f);
        }
    }

    private void SpawnAllRelicShrines()
    {
        GameObject container = new GameObject("World_Relic_Shrines");
        container.transform.SetParent(transform, false);

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        foreach (AncientRelicData r in relics)
        {
            float groundY = SampleGroundHeight(r.worldXZ.x, r.worldXZ.y);
            r.worldPosition = new Vector3(r.worldXZ.x, groundY, r.worldXZ.y);

            GameObject shrineGo = new GameObject("RelicShrine_" + r.relicId);
            shrineGo.transform.SetParent(container.transform, false);
            shrineGo.transform.position = r.worldPosition;
            r.shrineRoot = shrineGo.transform;

            GameObject basePedestal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(basePedestal.GetComponent<Collider>());
            basePedestal.transform.SetParent(shrineGo.transform, false);
            basePedestal.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            basePedestal.transform.localScale = new Vector3(1.6f, 0.45f, 1.6f);
            basePedestal.GetComponent<MeshRenderer>().sharedMaterial = _shrineStoneMat;

            GameObject runeObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(runeObj.GetComponent<Collider>());
            runeObj.name = "FloatingRelicRune";
            runeObj.transform.SetParent(shrineGo.transform, false);
            runeObj.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            runeObj.transform.localRotation = Quaternion.Euler(35f, 45f, 35f);
            runeObj.transform.localScale = new Vector3(0.65f, 0.65f, 0.65f);

            Material runeMat = new Material(shader) { enableInstancing = true };
            runeMat.EnableKeyword("_EMISSION");
            if (runeMat.HasProperty("_BaseColor"))
                runeMat.SetColor("_BaseColor", r.hdrColor);
            else
                runeMat.color = r.hdrColor;
            runeMat.SetColor("_EmissionColor", r.hdrColor * 2.8f);
            runeObj.GetComponent<MeshRenderer>().sharedMaterial = runeMat;
            r.floatingRuneMesh = runeObj.transform;

            GameObject lightGo = new GameObject("RelicLight");
            lightGo.transform.SetParent(shrineGo.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 1.8f, 0f);
            r.runeLight = lightGo.AddComponent<Light>();
            r.runeLight.type = LightType.Point;
            r.runeLight.color = r.hdrColor;
            r.runeLight.range = 12f;
            r.runeLight.intensity = r.isCollected ? 0.4f : 2.5f;

            if (r.isCollected)
            {
                r.floatingRuneMesh.gameObject.SetActive(false);
            }
        }
    }

    private void UpdateRelicShrinesAndProximity(float time)
    {
        Vector3 playerPos = _playerTransform.position;
        _nearbyRelic = null;

        foreach (AncientRelicData r in relics)
        {
            float y = SampleGroundHeight(r.worldXZ.x, r.worldXZ.y);
            r.worldPosition = new Vector3(r.worldXZ.x, y, r.worldXZ.y);
            if (r.shrineRoot != null)
            {
                r.shrineRoot.position = r.worldPosition;
            }

            if (!r.isCollected && r.floatingRuneMesh != null)
            {
                r.floatingRuneMesh.Rotate(Vector3.up, 75f * Time.deltaTime, Space.World);
                r.floatingRuneMesh.localPosition = new Vector3(0f, 1.65f + Mathf.Sin(time * 2.6f) * 0.16f, 0f);

                float dist = Vector3.Distance(playerPos, r.worldPosition);
                if (dist <= relicInteractRadius)
                {
                    _nearbyRelic = r;
                }
            }
        }

        float summitY = SampleGroundHeight(summitArenaXZ.x, summitArenaXZ.y);
        Vector3 summitPos = new Vector3(summitArenaXZ.x, summitY, summitArenaXZ.y);
        _isNearSummitAltar = Vector3.Distance(playerPos, summitPos) <= 18f;
    }

    public int GetCollectedRelicCount()
    {
        int count = 0;
        foreach (AncientRelicData r in relics)
        {
            if (r.isCollected) count++;
        }
        return count;
    }

    public void CollectRelic(AncientRelicData relic)
    {
        if (relic == null || relic.isCollected)
            return;

        relic.isCollected = true;
        PlayerPrefs.SetInt("RELIC_" + relic.relicId, 1);
        PlayerPrefs.Save();

        if (relic.floatingRuneMesh != null)
            relic.floatingRuneMesh.gameObject.SetActive(false);
        if (relic.runeLight != null)
            relic.runeLight.intensity = 0.4f;

        int total = GetCollectedRelicCount();
        RecordCampLogSafe($"Collected {relic.relicName} ({total}/5)");
        ShowBannerMessage($"Đã thu thập {relic.relicName}! Tiến độ Cổ Ngữ Toàn Server: {total}/5");
    }

    public void UnlockAllRelicsAndPortalsForTest()
    {
        foreach (AncientRelicData r in relics)
        {
            r.isCollected = true;
            if (r.floatingRuneMesh != null) r.floatingRuneMesh.gameObject.SetActive(false);
            if (r.runeLight != null) r.runeLight.intensity = 0.4f;
        }

        if (_fastTravelSystem != null && _fastTravelSystem.waypoints != null)
        {
            foreach (var wp in _fastTravelSystem.waypoints)
            {
                wp.isUnlocked = true;
            }
        }

        ShowBannerMessage("EDITOR TEST: Đã mở khóa đủ 5/5 Cổ Ngữ và toàn bộ 5 Cổng Dịch Chuyển!");
    }

    // =========================================================================
    // 2. CO GIÃN ĐỘ KHÓ (1-10 NGƯỜI) & TRIỆU HỒI BOSS ĐỈNH NÚI LỬA
    // =========================================================================
    public void RecalculateBossScaling(int onlinePlayers)
    {
        currentOnlinePlayers = Mathf.Clamp(onlinePlayers, 1, 10);
        float hpMultiplier = 1f + 0.45f * (currentOnlinePlayers - 1);
        float dmgMultiplier = 1f + 0.18f * (currentOnlinePlayers - 1);

        float ratio = _bossScaledMaxHP > 0f ? (_bossCurrentHP / _bossScaledMaxHP) : 1f;
        _bossScaledMaxHP = bossBaseMaxHP * hpMultiplier;
        _bossScaledDamage = bossBaseDamage * dmgMultiplier;

        if (_bossState != BossState.Dormant && _bossState != BossState.Defeated)
        {
            _bossCurrentHP = Mathf.Max(1f, _bossScaledMaxHP * ratio);
        }
    }

    public void SummonVolcanoBoss()
    {
        int playersOnline = PvEDirector.Instance != null && PvEDirector.Instance.IsSpawned
            ? Mathf.Max(1, PvEDirector.Instance.ActivePlayerCount.Value)
            : currentOnlinePlayers;

        RecalculateBossScaling(playersOnline);
        _bossCurrentHP = _bossScaledMaxHP;
        _bossState = BossState.CombatPhase1;
        _bossActionTimer = 2.5f;

        float summitY = SampleGroundHeight(summitArenaXZ.x, summitArenaXZ.y);
        Vector3 spawnPos = new Vector3(summitArenaXZ.x, summitY + 0.5f, summitArenaXZ.y);

        if (_bossRoot != null)
            Destroy(_bossRoot.gameObject);
        _bossOrbitShards.Clear();

        GameObject bossGo = new GameObject("Boss_Ignis_Leviathan");
        bossGo.transform.SetParent(transform, false);
        bossGo.transform.position = spawnPos;
        _bossRoot = bossGo.transform;

        if (customBossPrefab != null)
        {
            Instantiate(customBossPrefab, bossGo.transform);
        }
        else
        {
            BuildProceduralVolcanoBossModel(bossGo.transform);
        }

        RecordCampLogSafe($"Summoned Ignis Leviathan ({currentOnlinePlayers}/10 Players)");
        ShowBannerMessage($"BOSS XUẤT HIỆN: Hộ Vệ Núi Lửa Ignis Leviathan (HP: {_bossScaledMaxHP:0} | Co giãn {currentOnlinePlayers}/10 người)!");
    }

    private void BuildProceduralVolcanoBossModel(Transform parent)
    {
        GameObject torso = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        torso.name = "BossTorso";
        torso.transform.SetParent(parent, false);
        torso.transform.localPosition = new Vector3(0f, 2.2f, 0f);
        torso.transform.localScale = new Vector3(2.4f, 1.8f, 1.9f);
        torso.GetComponent<MeshRenderer>().sharedMaterial = _bossObsidianMat;

        GameObject core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(core.GetComponent<Collider>());
        core.name = "BossMagmaHeart";
        core.transform.SetParent(parent, false);
        core.transform.localPosition = new Vector3(0f, 2.4f, 0.85f);
        core.transform.localScale = new Vector3(1.1f, 1.1f, 0.7f);
        core.GetComponent<MeshRenderer>().sharedMaterial = _bossMagmaCoreMat;

        GameObject head = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(head.GetComponent<Collider>());
        head.transform.SetParent(parent, false);
        head.transform.localPosition = new Vector3(0f, 4.35f, 0.2f);
        head.transform.localScale = new Vector3(1.35f, 1.1f, 1.25f);
        head.GetComponent<MeshRenderer>().sharedMaterial = _bossObsidianMat;

        for (int i = 0; i < 4; i++)
        {
            GameObject shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(shard.GetComponent<Collider>());
            shard.name = "OrbitShieldShard_" + i;
            shard.transform.SetParent(parent, false);
            shard.transform.localScale = new Vector3(0.55f, 1.45f, 0.35f);
            shard.GetComponent<MeshRenderer>().sharedMaterial = _bossMagmaCoreMat;
            _bossOrbitShards.Add(shard.transform);
        }

        GameObject lightGo = new GameObject("BossMagmaLight");
        lightGo.transform.SetParent(parent, false);
        lightGo.transform.localPosition = new Vector3(0f, 3.2f, 0f);
        Light bl = lightGo.AddComponent<Light>();
        bl.type = LightType.Point;
        bl.color = new Color(1.0f, 0.42f, 0.12f);
        bl.range = 24f;
        bl.intensity = 3.5f;
    }

    // =========================================================================
    // 3. AI CHIẾN ĐẤU 3 GIAI ĐOẠN (3 PHASES) & VÒNG CẢNH BÁO AOE
    // =========================================================================
    private void CreateTelegraphRingIndicator()
    {
        _telegraphRingObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(_telegraphRingObj.GetComponent<Collider>());
        _telegraphRingObj.name = "Boss_AoE_Telegraph_Ring";
        _telegraphRingObj.transform.SetParent(transform, false);

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        bool isURP = shader.name.Contains("Universal");
        _telegraphMaterial = new Material(shader);
        _telegraphMaterial.EnableKeyword("_EMISSION");

        Color warnCol = new Color(1.0f, 0.22f, 0.08f, 0.55f);
        if (isURP)
        {
            _telegraphMaterial.SetColor("_BaseColor", warnCol);
            _telegraphMaterial.SetFloat("_Surface", 1f);
            _telegraphMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _telegraphMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _telegraphMaterial.SetInt("_ZWrite", 0);
            _telegraphMaterial.renderQueue = (int)RenderQueue.Transparent;
        }
        else
        {
            _telegraphMaterial.color = warnCol;
        }
        _telegraphMaterial.SetColor("_EmissionColor", new Color(1.0f, 0.25f, 0.05f) * 2.5f);

        _telegraphRingObj.GetComponent<MeshRenderer>().sharedMaterial = _telegraphMaterial;
        _telegraphRingObj.SetActive(false);
    }

    private void UpdateSummitBossLogic(float time)
    {
        if (_bossState == BossState.Dormant || _bossState == BossState.Defeated || _bossRoot == null)
            return;

        float hpPercent = _bossCurrentHP / Mathf.Max(1f, _bossScaledMaxHP);
        if (hpPercent <= 0.30f && _bossState != BossState.CombatPhase3)
        {
            _bossState = BossState.CombatPhase3;
            ShowBannerMessage("CẢNH BÁO: Ignis Leviathan bước vào Giai đoạn Cuồng Nộ (Phase 3)!");
        }
        else if (hpPercent <= 0.65f && _bossState == BossState.CombatPhase1)
        {
            _bossState = BossState.CombatPhase2;
            ShowBannerMessage("Ignis Leviathan kích hoạt Giáp Dung Nham (Phase 2)!");
        }

        float orbitSpeed = _bossState == BossState.CombatPhase3 ? 3.4f : 1.8f;
        for (int i = 0; i < _bossOrbitShards.Count; i++)
        {
            Transform shard = _bossOrbitShards[i];
            if (shard == null) continue;
            float ang = time * orbitSpeed + i * (Mathf.PI * 0.5f);
            shard.localPosition = new Vector3(Mathf.Cos(ang) * 2.6f, 2.3f + Mathf.Sin(time * 3f + i) * 0.25f, Mathf.Sin(ang) * 2.6f);
            shard.localRotation = Quaternion.Euler(15f, -ang * Mathf.Rad2Deg, 0f);
        }

        Vector3 playerPos = _playerTransform.position;
        Vector3 bossPos = _bossRoot.position;
        Vector3 toPlayer = playerPos - bossPos;
        toPlayer.y = 0f;
        float distToPlayer = toPlayer.magnitude;

        if (distToPlayer > 3.8f && distToPlayer < 45f && !_isChargingAoE)
        {
            Vector3 moveDir = toPlayer.normalized;
            float moveSpeed = _bossState == BossState.CombatPhase3 ? 5.2f : 3.4f;
            bossPos += moveDir * (moveSpeed * Time.deltaTime);
            bossPos.y = SampleGroundHeight(bossPos.x, bossPos.z) + 0.4f;
            _bossRoot.position = bossPos;
            _bossRoot.rotation = Quaternion.Slerp(_bossRoot.rotation, Quaternion.LookRotation(moveDir), 5f * Time.deltaTime);
        }

        if (_isChargingAoE)
        {
            _aoeTimer += Time.deltaTime;
            float progress = Mathf.Clamp01(_aoeTimer / _aoeDuration);
            float currentRadius = Mathf.Lerp(1.5f, _aoeRadius, progress);
            _telegraphRingObj.transform.position = _aoeTargetCenter + new Vector3(0f, 0.12f, 0f);
            _telegraphRingObj.transform.localScale = new Vector3(currentRadius * 2f, 0.06f, currentRadius * 2f);

            if (_aoeTimer >= _aoeDuration)
            {
                _isChargingAoE = false;
                _telegraphRingObj.SetActive(false);

                float playerDistToBlast = Vector2.Distance(
                    new Vector2(playerPos.x, playerPos.z),
                    new Vector2(_aoeTargetCenter.x, _aoeTargetCenter.z));

                if (playerDistToBlast <= _aoeRadius)
                {
                    _networkPlayer?.RequestBossAoEDamage(_bossScaledDamage);
                    CombatFeedbackManager.Instance?.TriggerHaptic(2);
                    ShowBannerMessage($"Trúng chiêu Địa Chấn Dung Nham! (-{_bossScaledDamage:0} HP) - Hãy chạy ra ngoài vòng đỏ!");
                }
            }
        }
        else
        {
            _bossActionTimer -= Time.deltaTime;
            if (_bossActionTimer <= 0f && distToPlayer <= 25f)
            {
                _isChargingAoE = true;
                _aoeTimer = 0f;
                _aoeDuration = _bossState == BossState.CombatPhase3 ? 1.2f : 1.65f;
                _aoeRadius = _bossState == BossState.CombatPhase1 ? 6.0f : 8.2f;
                float targetY = SampleGroundHeight(playerPos.x, playerPos.z);
                _aoeTargetCenter = new Vector3(playerPos.x, targetY, playerPos.z);
                _telegraphRingObj.SetActive(true);

                _bossActionTimer = _bossState == BossState.CombatPhase3 ? 3.0f : 4.5f;
            }
        }
    }

    public void TryPlayerAttackBossFromHUD()
    {
        if (_bossState == BossState.Dormant || _bossState == BossState.Defeated || _bossRoot == null || _playerTransform == null)
            return;

        if (Vector3.Distance(_playerTransform.position, _bossRoot.position) <= playerAttackRange)
        {
            PlayerAttackBoss();
        }
    }

    public void PlayerAttackBoss()
    {
        if (_bossState == BossState.Dormant || _bossState == BossState.Defeated || _bossRoot == null || _playerTransform == null)
            return;
        if (Time.time - _lastPlayerAttackTime < playerAttackCooldown)
            return;

        float dist = Vector3.Distance(_playerTransform.position, _bossRoot.position);
        if (dist > playerAttackRange)
        {
            ShowBannerMessage("Bạn đứng quá xa Boss! Hãy tiến lại gần hơn (< 9.5m).");
            return;
        }

        _lastPlayerAttackTime = Time.time;
        _bossCurrentHP = Mathf.Max(0f, _bossCurrentHP - playerAttackDamage);

        CombatFeedbackManager.Instance?.TriggerHaptic(1);
        CombatFeedbackManager.Instance?.PlayTargetPunch(_bossRoot);
        CombatFeedbackManager.Instance?.SpawnWorldPopup(
            _bossRoot.position + Vector3.up * 3.8f,
            $"-{playerAttackDamage:0}",
            new Color(1.0f, 0.55f, 0.20f));

        if (_bossCurrentHP <= 0f)
        {
            _bossState = BossState.Defeated;
            _isChargingAoE = false;
            if (_telegraphRingObj != null) _telegraphRingObj.SetActive(false);
            if (_bossRoot != null) Destroy(_bossRoot.gameObject, 0.5f);

            RecordCampLogSafe("VICTORY: Defeated Ignis Leviathan!");
            ShowBannerMessage("CHIẾN THẮNG! Nhóm của bạn đã hạ gục Hộ Vệ Đỉnh Núi Lửa Ignis Leviathan!");
        }
    }

    // =========================================================================
    // 4. GIAO DIỆN HUD & BẢNG "UNITY EDITOR QUICK TEST" TRỰC TIẾP TRÊN MÀN HÌNH
    // =========================================================================
    private void OnGUI()
    {
        if (_isHeadlessServer || _playerTransform == null)
            return;

        float guiScale = Mathf.Max(0.45f, Mathf.Min(Screen.width / 1920f, Screen.height / 1080f));
        float virtualW = Screen.width / guiScale;
        float virtualH = Screen.height / guiScale;

        Matrix4x4 prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(guiScale, guiScale, 1f));

        try
        {
            GUIStyle boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            GUIStyle btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold
            };

            int collectedCount = GetCollectedRelicCount();
            float trackerX = _hasMobileHud ? virtualW * 0.5f - 200f : 16f;
            float trackerY = _hasMobileHud ? 122f : 16f;
            Rect trackerRect = new Rect(trackerX, trackerY, _hasMobileHud ? 400f : 310f, 44f);
            GUI.Box(trackerRect, $"Cổ Ngữ Đảo Lớn: {collectedCount}/5 Mảnh\n(Đủ 5/5 lên Đỉnh Núi Lửa mở Boss)", boxStyle);

            // Place Quick Test bar on top-left in empty/untitled scene, or top-right below Debug card on MobileHUD
            float panelX = _hasMobileHud ? virtualW - 316f : 16f;
            float toggleY = _hasMobileHud ? 200f : 68f;
            float panelY = toggleY + 34f;

            if (GUI.Button(new Rect(panelX, toggleY, 300f, 30f), _showEditorTestBar ? "Thu gọn Bảng Test Nhanh [-]" : "Mở Bảng Test Nhanh [+]", btnStyle))
            {
                _showEditorTestBar = !_showEditorTestBar;
            }

            if (_showEditorTestBar)
            {
                Rect panelRect = new Rect(panelX, panelY, 300f, 240f);
                GUI.Box(panelRect, "UNITY EDITOR QUICK TEST");

                if (GUI.Button(new Rect(panelX + 10f, panelY + 30f, 280f, 34f), "1. Mở Khóa 5/5 Cổ Ngữ & 5 Cổng", btnStyle))
                {
                    UnlockAllRelicsAndPortalsForTest();
                }

                if (GUI.Button(new Rect(panelX + 10f, panelY + 70f, 280f, 34f), "2. Bay Lên Đỉnh Núi Lửa Ngay", btnStyle))
                {
                    UnlockAllRelicsAndPortalsForTest();
                    TeleportDirectToPosition(new Vector3(0f, SampleGroundHeight(0f, 468f) + 1.5f, 468f));
                    ShowBannerMessage("Đã dịch chuyển lên Đỉnh Núi Lửa Cổ Đại (Độ cao 135m)!");
                }

                if (GUI.Button(new Rect(panelX + 10f, panelY + 110f, 280f, 34f), "3. Gọi Boss Ignis Leviathan Ngay", btnStyle))
                {
                    UnlockAllRelicsAndPortalsForTest();
                    TeleportDirectToPosition(new Vector3(0f, SampleGroundHeight(0f, 472f) + 1.5f, 472f));
                    SummonVolcanoBoss();
                }

                if (GUI.Button(new Rect(panelX + 10f, panelY + 150f, 280f, 34f), "4. Đổi Ngày / Đêm BioGlow", btnStyle))
                {
                    ToggleDayNightForTest();
                }

                if (GUI.Button(new Rect(panelX + 10f, panelY + 190f, 136f, 34f), $"+1 Người ({currentOnlinePlayers}/10)", btnStyle))
                {
                    int next = Mathf.Clamp(currentOnlinePlayers + 1, 1, 10);
                    if (PvEDirector.Instance != null && PvEDirector.Instance.IsServer)
                        PvEDirector.Instance.RecalculateDifficultyServer(next);
                    RecalculateBossScaling(next);
                }
                if (GUI.Button(new Rect(panelX + 154f, panelY + 190f, 136f, 34f), "-1 Người", btnStyle))
                {
                    int next = Mathf.Clamp(currentOnlinePlayers - 1, 1, 10);
                    if (PvEDirector.Instance != null && PvEDirector.Instance.IsServer)
                        PvEDirector.Instance.RecalculateDifficultyServer(next);
                    RecalculateBossScaling(next);
                }
            }

            if (_bossState != BossState.Dormant && _bossState != BossState.Defeated)
            {
                float barW = 480f;
                float bossY = _hasMobileHud ? 172f : 48f;
                Rect bossBarBg = new Rect(virtualW * 0.5f - barW * 0.5f, bossY, barW, 38f);
                float hpRatio = Mathf.Clamp01(_bossCurrentHP / Mathf.Max(1f, _bossScaledMaxHP));
                GUI.Box(bossBarBg, $"IGNIS LEVIATHAN [{_bossState}] - HP: {_bossCurrentHP:0}/{_bossScaledMaxHP:0} ({hpRatio * 100f:0}%)", boxStyle);

                Rect atkRect = new Rect(virtualW * 0.5f - 165f, virtualH - 238f, 330f, 44f);
                if (GUI.Button(atkRect, $"Đánh Boss (-{playerAttackDamage:0} HP) [Phím Space]", btnStyle) || Input.GetKeyDown(KeyCode.Space))
                {
                    PlayerAttackBoss();
                }
            }

            if (_nearbyRelic != null && !_nearbyRelic.isCollected)
            {
                Rect collectRect = new Rect(virtualW * 0.5f - 210f, virtualH - 238f, 420f, 44f);
                if (GUI.Button(collectRect, $"Thu thập {_nearbyRelic.relicName}", btnStyle))
                {
                    CollectRelic(_nearbyRelic);
                }
            }

            if (_isNearSummitAltar && (_bossState == BossState.Dormant || _bossState == BossState.Defeated))
            {
                Rect summonRect = new Rect(virtualW * 0.5f - 230f, virtualH - 238f, 460f, 44f);
                if (collectedCount >= 5)
                {
                    if (GUI.Button(summonRect, "Đặt 5 Cổ Ngữ - Triệu Hồi Boss Ignis Leviathan!", btnStyle))
                    {
                        SummonVolcanoBoss();
                    }
                }
                else
                {
                    GUI.enabled = false;
                    GUI.Button(summonRect, $"Đền Thờ Núi Lửa: Cần đủ 5/5 Cổ Ngữ (Hiện có {collectedCount}/5)", btnStyle);
                    GUI.enabled = true;
                }
            }

            if (_bannerTimer > 0f && !string.IsNullOrEmpty(_bannerMessage))
            {
                float bannerY = _hasMobileHud ? 216f : 94f;
                Rect bannerRect = new Rect(virtualW * 0.5f - 280f, bannerY, 560f, 38f);
                GUI.Box(bannerRect, _bannerMessage, boxStyle);
            }
        }
        finally
        {
            GUI.matrix = prevMatrix;
        }
    }

    private void ToggleDayNightForTest()
    {
        if (WorldTimeManager.Instance != null && WorldTimeManager.Instance.IsServer)
        {
            WorldTimeManager.Instance.AdvanceHoursDebugServer(12.0f);
            return;
        }

        if (_worldUpgrader != null)
        {
            bool isNight = _worldUpgrader.currentHour > 19f || _worldUpgrader.currentHour < 5f;
            _worldUpgrader.currentHour = isNight ? 9.0f : 22.5f;
            _worldUpgrader.bioGlowIntensity = isNight ? 0f : 1f;
        }
    }

    private void TeleportDirectToPosition(Vector3 targetPos)
    {
        if (_playerTransform == null)
            return;

        if (_networkPlayer != null && _networkPlayer.IsSpawned)
        {
            _networkPlayer.RequestTeleportToPosition(targetPos);
        }
        else
        {
            CharacterController cc = _playerTransform.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            _playerTransform.position = targetPos;
            if (cc != null) cc.enabled = true;
        }

        // Immediately activate destination chunk MeshCollider so player doesn't fall through summit
        _worldUpgrader?.RefreshStreamingNow();
    }

    private void ShowBannerMessage(string msg)
    {
        _bannerMessage = msg;
        _bannerTimer = 5.0f;
    }

    private void EnsurePlayerReference()
    {
        if (NetworkPlayerController.LocalPlayer != null)
        {
            _networkPlayer = NetworkPlayerController.LocalPlayer;
            _playerTransform = _networkPlayer.transform;
            return;
        }

        if (_playerTransform != null)
            return;

        CharacterController cc = FindFirstObjectByType<CharacterController>();
        if (cc != null)
        {
            _playerTransform = cc.transform;
            _networkPlayer = cc.GetComponent<NetworkPlayerController>();
            return;
        }

        try
        {
            GameObject tagged = GameObject.FindWithTag("Player");
            if (tagged != null)
            {
                _playerTransform = tagged.transform;
                _networkPlayer = tagged.GetComponent<NetworkPlayerController>();
                return;
            }
        }
        catch { /* ignore */ }
    }

    private float SampleGroundHeight(float worldX, float worldZ)
    {
        if (_worldUpgrader != null)
            return _worldUpgrader.EvaluateHeightOnly(worldX, worldZ);

        if (Physics.Raycast(new Vector3(worldX, 300f, worldZ), Vector3.down, out RaycastHit hit, 500f))
            return hit.point.y;

        return IslandGraphicsUpgrader.EvaluateWorldHeightStatic(worldX, worldZ);
    }

    private static void RecordCampLogSafe(string message)
    {
        if (SharedCampLogManager.Instance == null || !SharedCampLogManager.Instance.IsSpawned)
            return;

        if (SharedCampLogManager.Instance.IsServer)
            SharedCampLogManager.Instance.RecordActivityServer(message);
        else
            SharedCampLogManager.Instance.PostPlayerNoteServerRpc(message);
    }
}

/// <summary>
/// Third-person WASD + camera controller automatically attached when pressing Play in Unity Editor.
/// </summary>
public class EditorQuickTestPlayerMotor : MonoBehaviour
{
    public float moveSpeed = 9.5f;
    public float sprintMultiplier = 1.75f;
    public float gravity = -20f;

    private CharacterController _cc;
    private Animator _animator;
    private int _speedParamHash;
    private bool _hasSpeedParam;
    private float _verticalVelocity;
    private float _cameraYaw;

    private void Start()
    {
        _cc = GetComponent<CharacterController>();
        _speedParamHash = Animator.StringToHash("Speed");
        ResolveValidAnimator();

        if (Camera.main != null)
        {
            Quaternion yawRot = Quaternion.Euler(0f, _cameraYaw, 0f);
            Camera.main.transform.position = transform.position + yawRot * new Vector3(0f, 4.2f, -7.8f);
            Camera.main.transform.LookAt(transform.position + Vector3.up * 1.35f);
        }
    }

    private void ResolveValidAnimator()
    {
        _animator = GetComponentInChildren<Animator>(true);
        _hasSpeedParam = false;
        if (_animator == null)
            return;

        if (_animator.runtimeAnimatorController == null || !_animator.isInitialized || _animator.layerCount <= 0)
        {
            _animator.enabled = false;
            return;
        }

        foreach (AnimatorControllerParameter p in _animator.parameters)
        {
            if (p.nameHash == _speedParamHash)
            {
                _hasSpeedParam = true;
                break;
            }
        }
    }

    private void Update()
    {
        if (_cc == null || !_cc.enabled)
            return;

        if (_animator == null)
            ResolveValidAnimator();

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        if (Input.GetMouseButton(1))
        {
            _cameraYaw += Input.GetAxis("Mouse X") * 4.2f;
        }
        else if (Mathf.Abs(h) > 0.05f && Mathf.Abs(v) < 0.05f)
        {
            _cameraYaw += h * 95f * Time.deltaTime;
        }

        Quaternion yawRot = Quaternion.Euler(0f, _cameraYaw, 0f);
        Vector3 moveDir = yawRot * new Vector3(h, 0f, v);
        if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

        float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f);
        float horizontalSpeed = moveDir.magnitude * speed;

        if (_cc.isGrounded && _verticalVelocity < 0f)
        {
            _verticalVelocity = -2f;
        }
        _verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = moveDir * speed + Vector3.up * _verticalVelocity;
        _cc.Move(velocity * Time.deltaTime);

        if (moveDir.sqrMagnitude > 0.01f)
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), 10f * Time.deltaTime);
        }

        if (_animator != null && _animator.isActiveAndEnabled && _hasSpeedParam)
        {
            _animator.SetFloat(_speedParamHash, horizontalSpeed, 0.08f, Time.deltaTime);
        }

        if (Camera.main != null)
        {
            Vector3 desiredCamPos = transform.position + yawRot * new Vector3(0f, 4.2f, -7.8f);
            Camera.main.transform.position = Vector3.Lerp(Camera.main.transform.position, desiredCamPos, 12f * Time.deltaTime);
            Camera.main.transform.LookAt(transform.position + Vector3.up * 1.35f);
        }
    }
}
