using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

/// <summary>
/// IslandRelicAndVolcanoBossSystem:
/// Shared 10-player co-op progression across the 2000m x 2000m 5-Biome Coral Archipelago:
/// - Places 5 HDR Ancient Relic Shrines across the 5 Biomes (progress shared to Camp Log & persisted).
/// - Unlocks summoning "Ignis Leviathan - Hộ Vệ Đỉnh Núi Lửa" at the Northern Volcanic Temple (0, y, 480)
///   once 5/5 relics are collected.
/// - Dynamically scales Boss MaxHP and Damage for 1-10 online players synced with PvEDirector & Debug UI:
///     MaxHP = 1500 * (1 + 0.45 * (onlinePlayers - 1))
///     Damage = 18 * (1 + 0.18 * (onlinePlayers - 1))
/// - Features 3 combat phases with expanding ground AoE Telegraph Rings and HUD Attack button integration.
/// </summary>
[DisallowMultipleComponent]
public class IslandRelicAndVolcanoBossSystem : MonoBehaviour
{
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

    public static IslandRelicAndVolcanoBossSystem Instance { get; private set; }

    [Header("=== 1. 5 BIOME RELIC SHRINES ===")]
    public float relicInteractRadius = 4.5f;
    public List<AncientRelicData> relics = new List<AncientRelicData>();

    [Header("=== 2. VOLCANIC SUMMIT BOSS & 1-10 PLAYER SCALING ===")]
    public Vector2 summitArenaXZ = new Vector2(0f, 480f);
    public float bossBaseMaxHP = 1500f;
    public float bossBaseDamage = 18f;
    [Range(1, 10)] public int currentOnlinePlayers = 1;
    public float playerAttackDamage = 45f;
    public float playerAttackRange = 8.5f;
    public float playerAttackCooldown = 0.55f;
    public GameObject customBossPrefab;

    private bool _isHeadlessServer;
    private BossState _bossState = BossState.Dormant;
    private float _bossCurrentHP;
    private float _bossScaledMaxHP;
    private float _bossScaledDamage;
    private Transform _bossRoot;
    private Transform _bossCoreVisual;
    private readonly List<Transform> _bossOrbitShards = new List<Transform>();
    private float _bossActionTimer;
    private float _lastPlayerAttackTime;

    // Telegraph AoE Ring state
    private GameObject _telegraphRingObj;
    private Material _telegraphMaterial;
    private bool _isChargingAoE;
    private float _aoeTimer;
    private float _aoeDuration = 1.65f;
    private float _aoeRadius = 7.5f;
    private Vector3 _aoeTargetCenter;

    private IslandGraphicsUpgrader _worldUpgrader;
    private Transform _playerTransform;
    private NetworkPlayerController _networkPlayer;
    private AncientRelicData _nearbyRelic;
    private bool _isNearSummitAltar;
    private string _bannerMessage = "";
    private float _bannerTimer;

    // Shared Materials & GUI styles
    private Material _shrineStoneMat;
    private Material _bossObsidianMat;
    private Material _bossMagmaCoreMat;
    private Texture2D _uiDarkBgTex;
    private Texture2D _uiAccentTex;
    private Texture2D _uiBossBarBgTex;
    private Texture2D _uiBossBarFillTex;
    private GUIStyle _boxStyle;
    private GUIStyle _btnStyle;
    private GUIStyle _labelStyle;

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

        RecalculateBossScaling(currentOnlinePlayers);

        if (_isHeadlessServer)
            return;

        BuildMaterials();
        SpawnAllRelicShrines();
        CreateTelegraphRingIndicator();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        if (_uiDarkBgTex != null) Destroy(_uiDarkBgTex);
        if (_uiAccentTex != null) Destroy(_uiAccentTex);
        if (_uiBossBarBgTex != null) Destroy(_uiBossBarBgTex);
        if (_uiBossBarFillTex != null) Destroy(_uiBossBarFillTex);
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
    // 1. 5 ANCIENT RELIC SHRINES ACROSS 5 BIOMES
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
                worldXZ = new Vector2(24f, 22f),
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
        _isNearSummitAltar = Vector3.Distance(playerPos, summitPos) <= 16f;
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
        string msg = $"Collected {relic.relicName} ({total}/5)";
        RecordCampLogSafe(msg);
        ShowBannerMessage($"Đã thu thập {relic.relicName}! Tiến độ Cổ Ngữ Toàn Server: {total}/5");
    }

    // =========================================================================
    // 2. 1-10 PLAYER SCALING & VOLCANIC BOSS SUMMONING
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
        if (_bossState != BossState.Dormant && _bossState != BossState.Defeated)
            return;

        if (GetCollectedRelicCount() < 5)
            return;

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
        _bossCoreVisual = core.transform;

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
    // 3. 3-PHASE BOSS COMBAT & TELEGRAPH AOE RING
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

        Color warnCol = new Color(1.0f, 0.18f, 0.06f, 0.55f);
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
            if (shard == null)
                continue;

            float ang = time * orbitSpeed + i * (Mathf.PI * 0.5f);
            shard.localPosition = new Vector3(Mathf.Cos(ang) * 2.6f, 2.3f + Mathf.Sin(time * 3f + i) * 0.25f, Mathf.Sin(ang) * 2.6f);
            shard.localRotation = Quaternion.Euler(15f, -ang * Mathf.Rad2Deg, 0f);
        }

        if (_bossCoreVisual != null)
        {
            float pulseScale = 1.0f + 0.14f * Mathf.Sin(time * (_bossState == BossState.CombatPhase3 ? 8f : 4f));
            _bossCoreVisual.localScale = new Vector3(1.1f * pulseScale, 1.1f * pulseScale, 0.7f * pulseScale);
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

    /// <summary>
    /// Invoked automatically when player taps the HUD Attack/Harvest button within 8.5m of Boss.
    /// </summary>
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
            ShowBannerMessage("Bạn đứng quá xa Boss! Hãy tiến lại gần hơn (< 8.5m).");
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
    // 4. RESPONSIVE NON-OVERLAPPING HUD OVERLAY (1920x1080 VIRTUAL SCALE)
    // =========================================================================
    private void EnsureGuiStyles()
    {
        if (_boxStyle != null && _btnStyle != null && _labelStyle != null)
            return;

        _uiDarkBgTex = new Texture2D(1, 1);
        _uiDarkBgTex.SetPixel(0, 0, new Color(0.05f, 0.12f, 0.22f, 0.84f));
        _uiDarkBgTex.Apply();

        _uiAccentTex = new Texture2D(1, 1);
        _uiAccentTex.SetPixel(0, 0, new Color(0.74f, 0.24f, 0.16f, 0.94f));
        _uiAccentTex.Apply();

        _uiBossBarBgTex = new Texture2D(1, 1);
        _uiBossBarBgTex.SetPixel(0, 0, new Color(0.18f, 0.04f, 0.04f, 0.90f));
        _uiBossBarBgTex.Apply();

        _uiBossBarFillTex = new Texture2D(1, 1);
        _uiBossBarFillTex.SetPixel(0, 0, new Color(0.96f, 0.30f, 0.12f, 0.95f));
        _uiBossBarFillTex.Apply();

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _boxStyle.normal.background = _uiDarkBgTex;
        _boxStyle.normal.textColor = new Color(0.88f, 0.97f, 1.0f);

        _btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 17,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _btnStyle.normal.background = _uiAccentTex;
        _btnStyle.normal.textColor = Color.white;

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _labelStyle.normal.background = null;
        _labelStyle.normal.textColor = Color.white;
    }

    private void OnGUI()
    {
        if (_isHeadlessServer || _playerTransform == null)
            return;

        EnsureGuiStyles();

        float guiScale = Mathf.Max(0.4f, Mathf.Min(Screen.width / 1920f, Screen.height / 1080f));
        float virtualW = Screen.width / guiScale;
        float virtualH = Screen.height / guiScale;

        Matrix4x4 prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(guiScale, guiScale, 1f));

        try
        {
            // 1. Relic Progress Tracker centered right under CompassBarCard (clear of Top-Left Tutorial & StatusPill)
            int collectedCount = GetCollectedRelicCount();
            Rect trackerRect = new Rect(virtualW * 0.5f - 210f, 122f, 420f, 42f);
            GUI.Box(trackerRect, $"Cổ Ngữ Đảo Lớn: {collectedCount}/5 Mảnh (Đủ 5/5 lên Núi Lửa mở Boss)", _boxStyle);

            // 2. Boss Health Bar right under Relic Tracker
            if (_bossState != BossState.Dormant && _bossState != BossState.Defeated)
            {
                float barW = 500f;
                Rect bossBarBg = new Rect(virtualW * 0.5f - barW * 0.5f, 170f, barW, 38f);
                float hpRatio = Mathf.Clamp01(_bossCurrentHP / Mathf.Max(1f, _bossScaledMaxHP));

                GUI.DrawTexture(bossBarBg, _uiBossBarBgTex);
                Rect fillRect = new Rect(bossBarBg.x + 2f, bossBarBg.y + 2f, (barW - 4f) * hpRatio, bossBarBg.height - 4f);
                GUI.DrawTexture(fillRect, _uiBossBarFillTex);
                GUI.Label(
                    bossBarBg,
                    $"IGNIS LEVIATHAN [{_bossState}] - HP: {_bossCurrentHP:0}/{_bossScaledMaxHP:0} (DMG: {_bossScaledDamage:0})",
                    _labelStyle);

                if (_bossRoot != null && Vector3.Distance(_playerTransform.position, _bossRoot.position) <= playerAttackRange)
                {
                    Rect atkRect = new Rect(virtualW * 0.5f - 170f, virtualH - 238f, 340f, 44f);
                    if (GUI.Button(atkRect, $"Tấn Công Boss (-{playerAttackDamage:0} HP)", _btnStyle))
                    {
                        PlayerAttackBoss();
                    }
                }
            }

            // 3. Collect Relic Shrine button (above Portal/Raft buttons, zero overlap)
            if (_nearbyRelic != null && !_nearbyRelic.isCollected)
            {
                Rect collectRect = new Rect(virtualW * 0.5f - 220f, virtualH - 238f, 440f, 44f);
                if (GUI.Button(collectRect, $"Thu thập {_nearbyRelic.relicName}", _btnStyle))
                {
                    CollectRelic(_nearbyRelic);
                }
            }

            // 4. Summon Volcano Boss button at Northern Summit Temple
            if (_isNearSummitAltar && (_bossState == BossState.Dormant || _bossState == BossState.Defeated))
            {
                Rect summonRect = new Rect(virtualW * 0.5f - 250f, virtualH - 238f, 500f, 46f);
                if (collectedCount >= 5)
                {
                    if (GUI.Button(summonRect, "Đặt 5 Cổ Ngữ - Triệu Hồi Boss Ignis Leviathan!", _btnStyle))
                    {
                        SummonVolcanoBoss();
                    }
                }
                else
                {
                    GUI.enabled = false;
                    GUI.Button(summonRect, $"Đền Thờ Núi Lửa: Cần đủ 5/5 Cổ Ngữ (Hiện có {collectedCount}/5)", _btnStyle);
                    GUI.enabled = true;
                }
            }

            // 5. Event Banner below Boss Bar / Tracker
            if (_bannerTimer > 0f && !string.IsNullOrEmpty(_bannerMessage))
            {
                float bannerY = (_bossState != BossState.Dormant && _bossState != BossState.Defeated) ? 214f : 170f;
                Rect bannerRect = new Rect(virtualW * 0.5f - 300f, bannerY, 600f, 40f);
                GUI.Box(bannerRect, _bannerMessage, _boxStyle);
            }
        }
        finally
        {
            GUI.matrix = prevMatrix;
        }
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

        GameObject tagged = GameObject.FindWithTag("Player");
        if (tagged != null)
        {
            _playerTransform = tagged.transform;
            _networkPlayer = tagged.GetComponent<NetworkPlayerController>();
        }
    }

    private float SampleGroundHeight(float worldX, float worldZ)
    {
        if (_worldUpgrader != null)
            return _worldUpgrader.EvaluateHeightOnly(worldX, worldZ);

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
