using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using CoralArchipelago.ClientIOS;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.Coop;
using CoralArchipelago.Gameplay.Player;
using CoralArchipelago.Networking;

/// <summary>
/// IslandFastTravelAndRaftSystem:
/// Open-world fast travel & sailing system across the 2000m x 2000m 5-Biome Coral Archipelago:
/// 1. 5 Ancient Waypoint Portals connecting Starter Coral Bay, Ancient Rainforest, Crystal Canyon,
///    Mangrove Swamp, and Volcanic Summit. Auto-unlocks within 8.5m, saves state, safely teleports
///    CharacterController/Server position, and refreshes destination Chunk Streaming immediately.
/// 2. Coastal Sailing Raft with 7 wooden logs, deck, mast & sail cloth, wave buoyancy,
///    22m/s sailing with keyboard or iOS Virtual Joystick, shore blocking, and Board/Disembark/Summon UI.
/// </summary>
[DisallowMultipleComponent]
public class IslandFastTravelAndRaftSystem : MonoBehaviour
{
    [Serializable]
    public class WaypointNode
    {
        public string id;
        public string displayName;
        public IslandGraphicsUpgrader.BiomeType biome;
        public Vector2 worldXZ;
        public Color portalGlowColor;
        public bool isUnlocked;

        [NonSerialized] public Vector3 worldPosition;
        [NonSerialized] public Transform portalRoot;
        [NonSerialized] public Transform spinningCore;
        [NonSerialized] public Material coreMaterial;
        [NonSerialized] public Light portalLight;
    }

    public static IslandFastTravelAndRaftSystem Instance { get; private set; }

    [Header("=== 1. 5 ANCIENT WAYPOINT PORTALS ===")]
    public float portalUnlockDistance = 8.5f;
    public float portalInteractDistance = 4.5f;
    public List<WaypointNode> waypoints = new List<WaypointNode>();

    [Header("=== 2. COASTAL SAILING RAFT ===")]
    public float raftMoveSpeed = 22f;
    public float raftTurnSpeed = 85f;
    public float raftInteractDistance = 5.0f;
    public GameObject customRaftPrefab;
    public GameObject customPortalPrefab;

    private bool _isHeadlessServer;
    private IslandGraphicsUpgrader _worldUpgrader;
    private Transform _playerTransform;
    private CharacterController _playerController;
    private Rigidbody _playerRigidbody;
    private NetworkPlayerController _networkPlayer;

    // Raft state
    private Transform _raftTransform;
    private bool _isSailingRaft;
    private WaypointNode _nearestPortal;
    private bool _showTeleportMenu;

    // Shared Materials & GUI styles
    private Material _stoneMaterial;
    private Material _woodLogMaterial;
    private Material _sailClothMaterial;
    private Texture2D _uiDarkBgTex;
    private Texture2D _uiAccentTex;
    private GUIStyle _btnStyle;
    private GUIStyle _boxStyle;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        _isHeadlessServer = NetworkBootstrap.IsRunningAsDedicatedServer();
        InitializeDefaultWaypointsIfNeeded();
    }

    private void Start()
    {
        _worldUpgrader = IslandGraphicsUpgrader.Instance != null
            ? IslandGraphicsUpgrader.Instance
            : FindFirstObjectByType<IslandGraphicsUpgrader>();

        if (_isHeadlessServer)
            return;

        BuildMaterials();
        SpawnAllWaypointPortals();
        SpawnCoastalSailingRaft();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        if (_uiDarkBgTex != null) Destroy(_uiDarkBgTex);
        if (_uiAccentTex != null) Destroy(_uiAccentTex);
    }

    private void Update()
    {
        if (_isHeadlessServer)
            return;

        EnsurePlayerReference();
        if (_playerTransform == null)
            return;

        float time = Time.time;
        AnimatePortalsAndCheckProximity(time);
        UpdateRaftBuoyancyAndControl(time);
    }

    // =========================================================================
    // 1. 5 WAYPOINT NODES FOR THE 2000M x 2000M 5-BIOME CONTINENT
    // =========================================================================
    private void InitializeDefaultWaypointsIfNeeded()
    {
        if (waypoints != null && waypoints.Count > 0)
            return;

        waypoints = new List<WaypointNode>
        {
            new WaypointNode
            {
                id = "wp_starter_bay",
                displayName = "1. Vịnh Căn Cứ Altar (Starter Bay)",
                biome = IslandGraphicsUpgrader.BiomeType.StarterCoralBay,
                worldXZ = new Vector2(-12f, 4f),
                portalGlowColor = new Color(0.15f, 0.96f, 0.82f),
                isUnlocked = true
            },
            new WaypointNode
            {
                id = "wp_rainforest",
                displayName = "2. Rừng Già Nhiệt Đới (Ancient Rainforest)",
                biome = IslandGraphicsUpgrader.BiomeType.AncientRainforest,
                worldXZ = new Vector2(-340f, 85f),
                portalGlowColor = new Color(0.25f, 0.95f, 0.38f),
                isUnlocked = PlayerPrefs.GetInt("WP_UNLOCK_wp_rainforest", 0) == 1
            },
            new WaypointNode
            {
                id = "wp_crystal_canyon",
                displayName = "3. Hẻm Núi Pha Lê (Crystal Canyon)",
                biome = IslandGraphicsUpgrader.BiomeType.CrystalCanyon,
                worldXZ = new Vector2(365f, 40f),
                portalGlowColor = new Color(0.12f, 0.82f, 1.0f),
                isUnlocked = PlayerPrefs.GetInt("WP_UNLOCK_wp_crystal_canyon", 0) == 1
            },
            new WaypointNode
            {
                id = "wp_mangrove_swamp",
                displayName = "4. Đầm Lầy Ngập Mặn (Mangrove Swamp)",
                biome = IslandGraphicsUpgrader.BiomeType.MangroveSwamp,
                worldXZ = new Vector2(245f, -245f),
                portalGlowColor = new Color(0.58f, 0.92f, 0.22f),
                isUnlocked = PlayerPrefs.GetInt("WP_UNLOCK_wp_mangrove_swamp", 0) == 1
            },
            new WaypointNode
            {
                id = "wp_volcanic_summit",
                displayName = "5. Đỉnh Núi Lửa Cổ Đại (Volcanic Summit)",
                biome = IslandGraphicsUpgrader.BiomeType.VolcanicSummit,
                worldXZ = new Vector2(0f, 465f),
                portalGlowColor = new Color(1.0f, 0.45f, 0.14f),
                isUnlocked = PlayerPrefs.GetInt("WP_UNLOCK_wp_volcanic_summit", 0) == 1
            }
        };
    }

    private void BuildMaterials()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        bool isURP = shader.name.Contains("Universal");

        _stoneMaterial = new Material(shader) { enableInstancing = true };
        _woodLogMaterial = new Material(shader) { enableInstancing = true };
        _sailClothMaterial = new Material(shader) { enableInstancing = true };

        if (isURP)
        {
            _stoneMaterial.SetColor("_BaseColor", new Color(0.36f, 0.40f, 0.45f));
            _stoneMaterial.SetFloat("_Smoothness", 0.35f);

            _woodLogMaterial.SetColor("_BaseColor", new Color(0.45f, 0.29f, 0.16f));
            _woodLogMaterial.SetFloat("_Smoothness", 0.28f);

            _sailClothMaterial.SetColor("_BaseColor", new Color(0.95f, 0.92f, 0.84f));
            _sailClothMaterial.SetFloat("_Smoothness", 0.18f);
        }
        else
        {
            _stoneMaterial.color = new Color(0.36f, 0.40f, 0.45f);
            _woodLogMaterial.color = new Color(0.45f, 0.29f, 0.16f);
            _sailClothMaterial.color = new Color(0.95f, 0.92f, 0.84f);
        }
    }

    // =========================================================================
    // 2. ANCIENT WAYPOINT PORTALS & FAST TRAVEL
    // =========================================================================
    private void SpawnAllWaypointPortals()
    {
        GameObject portalsContainer = new GameObject("Ancient_Waypoint_Portals");
        portalsContainer.transform.SetParent(transform, false);

        foreach (WaypointNode wp in waypoints)
        {
            float groundY = SampleGroundHeight(wp.worldXZ.x, wp.worldXZ.y);
            wp.worldPosition = new Vector3(wp.worldXZ.x, groundY, wp.worldXZ.y);

            GameObject portalGo = new GameObject("Portal_" + wp.id);
            portalGo.transform.SetParent(portalsContainer.transform, false);
            portalGo.transform.position = wp.worldPosition;
            wp.portalRoot = portalGo.transform;

            if (customPortalPrefab != null)
            {
                Instantiate(customPortalPrefab, portalGo.transform);
            }
            else
            {
                BuildProceduralPortalVisual(wp, portalGo.transform);
            }

            RefreshPortalUnlockVisual(wp);
        }
    }

    private void BuildProceduralPortalVisual(WaypointNode wp, Transform parent)
    {
        for (int i = 0; i < 2; i++)
        {
            GameObject step = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(step.GetComponent<Collider>());
            step.name = "PortalPedestal_" + i;
            step.transform.SetParent(parent, false);
            step.transform.localPosition = new Vector3(0f, 0.18f + i * 0.26f, 0f);
            float r = 3.2f - i * 0.55f;
            step.transform.localScale = new Vector3(r, 0.18f, r);
            step.GetComponent<MeshRenderer>().sharedMaterial = _stoneMaterial;
        }

        for (int side = -1; side <= 1; side += 2)
        {
            GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(pillar.GetComponent<Collider>());
            pillar.name = "RunePillar_" + side;
            pillar.transform.SetParent(parent, false);
            pillar.transform.localPosition = new Vector3(side * 1.15f, 1.85f, 0f);
            pillar.transform.localScale = new Vector3(0.42f, 2.9f, 0.42f);
            pillar.GetComponent<MeshRenderer>().sharedMaterial = _stoneMaterial;
        }

        GameObject archTop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(archTop.GetComponent<Collider>());
        archTop.name = "PortalArchLintel";
        archTop.transform.SetParent(parent, false);
        archTop.transform.localPosition = new Vector3(0f, 3.35f, 0f);
        archTop.transform.localScale = new Vector3(3.1f, 0.45f, 0.52f);
        archTop.GetComponent<MeshRenderer>().sharedMaterial = _stoneMaterial;

        GameObject core = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(core.GetComponent<Collider>());
        core.name = "WaypointEnergyCore";
        core.transform.SetParent(parent, false);
        core.transform.localPosition = new Vector3(0f, 1.85f, 0f);
        core.transform.localRotation = Quaternion.Euler(45f, 45f, 0f);
        core.transform.localScale = new Vector3(0.75f, 1.15f, 0.75f);

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        wp.coreMaterial = new Material(shader) { enableInstancing = true };
        wp.coreMaterial.EnableKeyword("_EMISSION");
        core.GetComponent<MeshRenderer>().sharedMaterial = wp.coreMaterial;
        wp.spinningCore = core.transform;

        GameObject lightGo = new GameObject("WaypointLight");
        lightGo.transform.SetParent(parent, false);
        lightGo.transform.localPosition = new Vector3(0f, 2.1f, 0f);
        wp.portalLight = lightGo.AddComponent<Light>();
        wp.portalLight.type = LightType.Point;
        wp.portalLight.range = 14f;
        wp.portalLight.shadows = LightShadows.None;
    }

    private void RefreshPortalUnlockVisual(WaypointNode wp)
    {
        if (wp.coreMaterial == null)
            return;

        if (wp.isUnlocked)
        {
            if (wp.coreMaterial.HasProperty("_BaseColor"))
                wp.coreMaterial.SetColor("_BaseColor", wp.portalGlowColor);
            else
                wp.coreMaterial.color = wp.portalGlowColor;

            wp.coreMaterial.SetColor("_EmissionColor", wp.portalGlowColor * 2.5f);
            if (wp.portalLight != null)
            {
                wp.portalLight.color = wp.portalGlowColor;
                wp.portalLight.intensity = 2.4f;
            }
        }
        else
        {
            Color dimCol = new Color(0.32f, 0.35f, 0.38f);
            if (wp.coreMaterial.HasProperty("_BaseColor"))
                wp.coreMaterial.SetColor("_BaseColor", dimCol);
            else
                wp.coreMaterial.color = dimCol;

            wp.coreMaterial.SetColor("_EmissionColor", wp.portalGlowColor * 0.25f);
            if (wp.portalLight != null)
            {
                wp.portalLight.color = wp.portalGlowColor;
                wp.portalLight.intensity = 0.4f;
            }
        }
    }

    private void AnimatePortalsAndCheckProximity(float time)
    {
        Vector3 playerPos = _playerTransform.position;
        _nearestPortal = null;
        float closestDist = float.MaxValue;

        foreach (WaypointNode wp in waypoints)
        {
            float currentGroundY = SampleGroundHeight(wp.worldXZ.x, wp.worldXZ.y);
            wp.worldPosition = new Vector3(wp.worldXZ.x, currentGroundY, wp.worldXZ.y);
            if (wp.portalRoot != null)
            {
                wp.portalRoot.position = wp.worldPosition;
            }

            if (wp.spinningCore != null)
            {
                float speed = wp.isUnlocked ? 65f : 18f;
                wp.spinningCore.Rotate(Vector3.up, speed * Time.deltaTime, Space.World);
                wp.spinningCore.localPosition = new Vector3(0f, 1.85f + Mathf.Sin(time * 2.4f) * 0.14f, 0f);
            }

            float dist = Vector3.Distance(playerPos, wp.worldPosition);

            if (!wp.isUnlocked && dist <= portalUnlockDistance)
            {
                wp.isUnlocked = true;
                PlayerPrefs.SetInt("WP_UNLOCK_" + wp.id, 1);
                PlayerPrefs.Save();
                RefreshPortalUnlockVisual(wp);
                RecordCampLogSafe($"Unlocked Portal: {wp.displayName}");
            }

            if (dist <= portalInteractDistance && dist < closestDist)
            {
                closestDist = dist;
                _nearestPortal = wp;
            }
        }

        if (_nearestPortal == null)
        {
            _showTeleportMenu = false;
        }
    }

    public void TeleportPlayerToWaypoint(WaypointNode targetWp)
    {
        if (targetWp == null || !targetWp.isUnlocked || _playerTransform == null)
            return;

        if (_isSailingRaft)
        {
            DisembarkRaft();
        }

        float destY = SampleGroundHeight(targetWp.worldXZ.x, targetWp.worldXZ.y);
        Vector3 safeLandingPos = new Vector3(targetWp.worldXZ.x, destY + 1.15f, targetWp.worldXZ.y - 2.6f);

        // Move player FIRST, then refresh Chunk Streaming so destination MeshCollider is immediately active
        if (_networkPlayer != null && _networkPlayer.IsSpawned)
        {
            _networkPlayer.RequestTeleportToPosition(safeLandingPos);
        }
        else
        {
            if (_playerController != null) _playerController.enabled = false;
            if (_playerRigidbody != null) _playerRigidbody.velocity = Vector3.zero;
            _playerTransform.position = safeLandingPos;
            if (_playerController != null) _playerController.enabled = true;
        }

        _worldUpgrader?.RefreshStreamingNow();
        _showTeleportMenu = false;
    }

    // =========================================================================
    // 3. COASTAL SAILING RAFT (BUOYANCY + 22M/S + TOUCH JOYSTICK)
    // =========================================================================
    private void SpawnCoastalSailingRaft()
    {
        float waterY = GetActiveWaterLevel();
        Vector3 dockPos = new Vector3(16f, waterY + 0.08f, -26f);

        GameObject raftGo = new GameObject("Coastal_Sailing_Raft");
        raftGo.transform.SetParent(transform, false);
        raftGo.transform.position = dockPos;
        _raftTransform = raftGo.transform;

        if (customRaftPrefab != null)
        {
            Instantiate(customRaftPrefab, raftGo.transform);
        }
        else
        {
            BuildProceduralSailingRaftVisual(raftGo.transform);
        }
    }

    private void BuildProceduralSailingRaftVisual(Transform parent)
    {
        for (int i = -3; i <= 3; i++)
        {
            GameObject log = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(log.GetComponent<Collider>());
            log.name = "RaftLog_" + i;
            log.transform.SetParent(parent, false);
            log.transform.localPosition = new Vector3(i * 0.44f, 0f, 0f);
            log.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            log.transform.localScale = new Vector3(0.42f, 2.2f, 0.42f);
            log.GetComponent<MeshRenderer>().sharedMaterial = _woodLogMaterial;
        }

        GameObject deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(deck.GetComponent<Collider>());
        deck.name = "RaftDeck";
        deck.transform.SetParent(parent, false);
        deck.transform.localPosition = new Vector3(0f, 0.22f, 0f);
        deck.transform.localScale = new Vector3(2.4f, 0.12f, 2.8f);
        deck.GetComponent<MeshRenderer>().sharedMaterial = _woodLogMaterial;

        GameObject mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(mast.GetComponent<Collider>());
        mast.name = "RaftMast";
        mast.transform.SetParent(parent, false);
        mast.transform.localPosition = new Vector3(0f, 2.1f, 0.35f);
        mast.transform.localScale = new Vector3(0.16f, 2.0f, 0.16f);
        mast.GetComponent<MeshRenderer>().sharedMaterial = _woodLogMaterial;

        GameObject sail = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(sail.GetComponent<Collider>());
        sail.name = "RaftSail";
        sail.transform.SetParent(parent, false);
        sail.transform.localPosition = new Vector3(0f, 2.45f, 0.52f);
        sail.transform.localScale = new Vector3(2.3f, 2.2f, 0.08f);
        sail.GetComponent<MeshRenderer>().sharedMaterial = _sailClothMaterial;
    }

    private void UpdateRaftBuoyancyAndControl(float time)
    {
        if (_raftTransform == null)
            return;

        float waterY = GetActiveWaterLevel();
        Vector3 pos = _raftTransform.position;

        float waveBob = Mathf.Sin(time * 2.1f + pos.x * 0.15f) * 0.08f;
        float pitchRock = Mathf.Sin(time * 1.7f) * 2.2f;
        float rollRock = Mathf.Cos(time * 1.4f) * 2.5f;

        if (_isSailingRaft && _playerTransform != null)
        {
            float moveInput = Input.GetAxis("Vertical");
            float turnInput = Input.GetAxis("Horizontal");

            if (MobileTouchInputUI.Instance != null)
            {
                Vector2 touchMove = MobileTouchInputUI.Instance.MoveVector;
                if (touchMove.sqrMagnitude > 0.01f)
                {
                    moveInput = touchMove.y;
                    turnInput = touchMove.x;
                }
            }

            moveInput = Mathf.Clamp(moveInput, -1f, 1f);
            turnInput = Mathf.Clamp(turnInput, -1f, 1f);

            _raftTransform.Rotate(0f, turnInput * raftTurnSpeed * Time.deltaTime, 0f, Space.World);

            Vector3 forward = _raftTransform.forward;
            forward.y = 0f;
            forward.Normalize();

            Vector3 candidatePos = pos + forward * (moveInput * raftMoveSpeed * Time.deltaTime);
            float terrainHeightAhead = SampleGroundHeight(candidatePos.x, candidatePos.z);

            if (terrainHeightAhead <= waterY + 0.65f)
            {
                pos.x = candidatePos.x;
                pos.z = candidatePos.z;
            }

            pos.y = waterY + 0.08f + waveBob;
            _raftTransform.position = pos;

            Vector3 euler = _raftTransform.eulerAngles;
            _raftTransform.rotation = Quaternion.Euler(pitchRock, euler.y, rollRock);

            Vector3 seatPos = _raftTransform.position + new Vector3(0f, 1.15f, -0.35f);
            if (_networkPlayer != null && _networkPlayer.IsSpawned)
            {
                _networkPlayer.SyncExternalMountPosition(seatPos, euler.y, true);
            }
            else
            {
                if (_playerController != null) _playerController.enabled = false;
                _playerTransform.position = seatPos;
                _playerTransform.rotation = Quaternion.Euler(0f, euler.y, 0f);
            }
        }
        else
        {
            pos.y = waterY + 0.08f + waveBob;
            _raftTransform.position = pos;
        }
    }

    public void BoardRaft()
    {
        if (_raftTransform == null || _playerTransform == null)
            return;

        _isSailingRaft = true;
        _showTeleportMenu = false;

        Vector3 seatPos = _raftTransform.position + new Vector3(0f, 1.15f, -0.35f);
        float yaw = _raftTransform.eulerAngles.y;
        if (_networkPlayer != null && _networkPlayer.IsSpawned)
        {
            _networkPlayer.SyncExternalMountPosition(seatPos, yaw, true);
        }
        else if (_playerController != null)
        {
            _playerController.enabled = false;
        }
    }

    public void DisembarkRaft()
    {
        if (!_isSailingRaft || _playerTransform == null)
            return;

        _isSailingRaft = false;

        Vector3 exitPos = _raftTransform.position + _raftTransform.right * 2.8f;
        float groundY = SampleGroundHeight(exitPos.x, exitPos.z);
        float waterY = GetActiveWaterLevel();
        exitPos.y = Mathf.Max(groundY, waterY) + 1.15f;

        if (_networkPlayer != null && _networkPlayer.IsSpawned)
        {
            _networkPlayer.SyncExternalMountPosition(exitPos, _raftTransform.eulerAngles.y, false);
        }
        else
        {
            _playerTransform.position = exitPos;
            if (_playerController != null) _playerController.enabled = true;
        }

        _worldUpgrader?.RefreshStreamingNow();
    }

    public void SummonRaftToNearestShoreline()
    {
        if (_playerTransform == null || _raftTransform == null)
            return;

        float waterY = GetActiveWaterLevel();
        Vector3 forward = _playerTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 spawnPos = _playerTransform.position + forward * 4.5f;
        spawnPos.y = waterY + 0.08f;
        _raftTransform.position = spawnPos;
    }

    // =========================================================================
    // 4. RESPONSIVE NON-OVERLAPPING ON-SCREEN HUD (1920x1080 VIRTUAL SCALE)
    // =========================================================================
    private void EnsureGuiStyles()
    {
        if (_btnStyle != null && _boxStyle != null)
            return;

        _uiDarkBgTex = new Texture2D(1, 1);
        _uiDarkBgTex.SetPixel(0, 0, new Color(0.05f, 0.12f, 0.22f, 0.88f));
        _uiDarkBgTex.Apply();

        _uiAccentTex = new Texture2D(1, 1);
        _uiAccentTex.SetPixel(0, 0, new Color(0.12f, 0.42f, 0.62f, 0.94f));
        _uiAccentTex.Apply();

        _btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _btnStyle.normal.background = _uiAccentTex;
        _btnStyle.normal.textColor = Color.white;

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _boxStyle.normal.background = _uiDarkBgTex;
        _boxStyle.normal.textColor = new Color(0.88f, 0.97f, 1.0f);
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
            // 1. Portal interact button (above CampNoteInput & Raft button, zero overlap)
            if (_nearestPortal != null && !_isSailingRaft)
            {
                Rect portalBanner = new Rect(virtualW * 0.5f - 230f, virtualH - 186f, 460f, 44f);
                if (GUI.Button(portalBanner, $"Cổng Cổ Đại: {_nearestPortal.displayName}", _btnStyle))
                {
                    _showTeleportMenu = !_showTeleportMenu;
                }
            }

            // 2. Fast Travel Waypoint Picker Modal
            if (_showTeleportMenu)
            {
                float boxW = 540f;
                float boxH = 340f;
                Rect menuRect = new Rect(virtualW * 0.5f - boxW * 0.5f, virtualH * 0.5f - boxH * 0.5f, boxW, boxH);
                GUI.Box(menuRect, "CHỌN CỔNG DỊCH CHUYỂN CỔ ĐẠI (2000M x 2000M)", _boxStyle);

                for (int i = 0; i < waypoints.Count; i++)
                {
                    WaypointNode wp = waypoints[i];
                    Rect rowRect = new Rect(menuRect.x + 24f, menuRect.y + 48f + i * 48f, boxW - 48f, 40f);
                    GUI.enabled = wp.isUnlocked;
                    string status = wp.isUnlocked ? "Dịch chuyển ngay" : "Chưa khám phá";
                    if (GUI.Button(rowRect, $"{wp.displayName} [{status}]", _btnStyle))
                    {
                        TeleportPlayerToWaypoint(wp);
                    }
                }
                GUI.enabled = true;

                if (GUI.Button(new Rect(menuRect.x + boxW * 0.5f - 90f, menuRect.y + boxH - 46f, 180f, 36f), "Đóng", _btnStyle))
                {
                    _showTeleportMenu = false;
                }
            }

            // 3. Raft Board / Disembark / Summon controls (above CampNoteInput, clear of bottom-left joystick)
            if (_raftTransform != null)
            {
                float distToRaft = Vector3.Distance(_playerTransform.position, _raftTransform.position);
                float waterY = GetActiveWaterLevel();
                bool nearShoreline = _playerTransform.position.y <= waterY + 2.2f;

                if (_isSailingRaft)
                {
                    Rect sailRect = new Rect(virtualW * 0.5f - 200f, virtualH - 134f, 400f, 44f);
                    if (GUI.Button(sailRect, "Đang Lướt Bè (22m/s) - Bấm Để Xuống Bè", _btnStyle))
                    {
                        DisembarkRaft();
                    }
                }
                else if (distToRaft <= raftInteractDistance)
                {
                    Rect boardRect = new Rect(virtualW * 0.5f - 190f, virtualH - 134f, 380f, 44f);
                    if (GUI.Button(boardRect, "Lên Bè Gỗ Lướt Sóng (Board Raft)", _btnStyle))
                    {
                        BoardRaft();
                    }
                }
                else if (nearShoreline)
                {
                    Rect summonRect = new Rect(260f, virtualH - 134f, 230f, 42f);
                    if (GUI.Button(summonRect, "Gọi Bè Gỗ Tới Bờ", _btnStyle))
                    {
                        SummonRaftToNearestShoreline();
                    }
                }
            }
        }
        finally
        {
            GUI.matrix = prevMatrix;
        }
    }

    // =========================================================================
    // 5. HELPERS
    // =========================================================================
    private void EnsurePlayerReference()
    {
        if (NetworkPlayerController.LocalPlayer != null)
        {
            _networkPlayer = NetworkPlayerController.LocalPlayer;
            _playerTransform = _networkPlayer.transform;
            _playerController = _networkPlayer.GetComponent<CharacterController>();
            _playerRigidbody = _networkPlayer.GetComponent<Rigidbody>();
            return;
        }

        if (_playerTransform != null)
            return;

        CharacterController cc = FindFirstObjectByType<CharacterController>();
        if (cc != null)
        {
            _playerTransform = cc.transform;
            _playerController = cc;
            _playerRigidbody = cc.GetComponent<Rigidbody>();
            _networkPlayer = cc.GetComponent<NetworkPlayerController>();
            return;
        }

        GameObject tagged = GameObject.FindWithTag("Player");
        if (tagged != null)
        {
            _playerTransform = tagged.transform;
            _playerController = tagged.GetComponent<CharacterController>();
            _playerRigidbody = tagged.GetComponent<Rigidbody>();
            _networkPlayer = tagged.GetComponent<NetworkPlayerController>();
        }
    }

    private float GetActiveWaterLevel()
    {
        if (WorldTimeManager.Instance != null)
            return WorldTimeManager.Instance.GetCurrentSeaLevelY();

        return _worldUpgrader != null ? _worldUpgrader.waterLevel : -0.4f;
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
