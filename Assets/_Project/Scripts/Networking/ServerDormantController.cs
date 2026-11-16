using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Core;
using CoralArchipelago.Gameplay.AI;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Networking
{
    /// <summary>
    /// Controls Dedicated Server Dormant vs Active lifecycle.
    /// When 0 players are connected: flushes saves, freezes world time, hibernates AI & physics,
    /// and throttles server frame rate to 1Hz to keep VPS CPU near 0%.
    /// Wakes instantly to 30Hz when the first player joins.
    /// </summary>
    public class ServerDormantController : MonoBehaviour
    {
        public static ServerDormantController Instance { get; private set; }

        [Header("Server Tick Settings")]
        [SerializeField] private int activeServerTickRate = 30;
        [SerializeField] private int dormantServerFrameRate = 1;

        public bool IsDormant { get; private set; } = true;
        public int ActivePlayerCount { get; private set; }

        private bool _hooksRegistered;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            TryRegisterNetworkCallbacks();
        }

        private void Start()
        {
            TryRegisterNetworkCallbacks();
        }

        private void OnDisable()
        {
            UnregisterNetworkCallbacks();
        }

        public void TryRegisterNetworkCallbacks()
        {
            if (_hooksRegistered || NetworkManager.Singleton == null)
                return;

            NetworkManager.Singleton.OnServerStarted += HandleServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
            _hooksRegistered = true;
        }

        private void UnregisterNetworkCallbacks()
        {
            if (!_hooksRegistered || NetworkManager.Singleton == null)
                return;

            NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
            _hooksRegistered = false;
        }

        private void HandleServerStarted()
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            // Always start in Dormant state on dedicated server until the first player connects.
            // If running as Host (Editor / local listen server), local client connects right away.
            if (!NetworkManager.Singleton.IsHost)
            {
                EnterDormantMode();
            }
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            int count = NetworkManager.Singleton.ConnectedClientsIds.Count;
            ActivePlayerCount = count;

            if (IsDormant && count > 0)
            {
                ExitDormantMode();
            }

            PvEDirector.Instance?.RecalculateDifficultyServer(count);
            Debug.Log($"[ServerDormantController] Player connected (ClientId={clientId}). Online: {count}/10.");
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            // Save disconnecting player's state immediately before removing their cache
            SplitSaveManager.Instance?.SavePlayerByClientId(clientId, removeFromActiveCache: true);

            // NGO invokes OnClientDisconnectCallback before or while removing the client from ConnectedClientsIds
            int remaining = 0;
            IReadOnlyList<ulong> connected = NetworkManager.Singleton.ConnectedClientsIds;
            for (int i = 0; i < connected.Count; i++)
            {
                if (connected[i] != clientId)
                    remaining++;
            }

            ActivePlayerCount = remaining;
            PvEDirector.Instance?.RecalculateDifficultyServer(remaining);

            Debug.Log($"[ServerDormantController] Player disconnected (ClientId={clientId}). Remaining: {remaining}/10.");

            if (remaining <= 0 && !IsDormant)
            {
                EnterDormantMode();
            }
        }

        public void EnterDormantMode()
        {
            IsDormant = true;

            WorldTimeManager.Instance?.SetPausedServer(true);

            ScalableMonsterAI[] monsters = FindObjectsByType<ScalableMonsterAI>(FindObjectsSortMode.None);
            for (int i = 0; i < monsters.Length; i++)
            {
                monsters[i].SetDormantServer(true);
            }

            SplitSaveManager.Instance?.FlushAllSavesToDisk();

            Physics.simulationMode = SimulationMode.Script;

            bool isPureDedicatedServer = NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost;
            if (isPureDedicatedServer)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = dormantServerFrameRate;
            }

            Debug.Log($"[ServerDormantController] Entered DORMANT MODE ({(isPureDedicatedServer ? dormantServerFrameRate + "Hz" : "Host")} idle, time & physics frozen).");
        }

        public void ExitDormantMode()
        {
            IsDormant = false;

            bool isPureDedicatedServer = NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost;
            if (isPureDedicatedServer)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = activeServerTickRate;
            }
            Physics.simulationMode = SimulationMode.FixedUpdate;

            WorldTimeManager.Instance?.SetPausedServer(false);

            ScalableMonsterAI[] monsters = FindObjectsByType<ScalableMonsterAI>(FindObjectsSortMode.None);
            for (int i = 0; i < monsters.Length; i++)
            {
                monsters[i].SetDormantServer(false);
            }

            Debug.Log($"[ServerDormantController] Exited DORMANT MODE -> ACTIVE ({(isPureDedicatedServer ? activeServerTickRate + "Hz" : "Host 60 FPS")}).");
        }
    }
}
