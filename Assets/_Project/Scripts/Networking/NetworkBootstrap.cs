using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Networking
{
    /// <summary>
    /// Entry-point bootstrap for both iOS Clients and Headless Linux Dedicated Server.
    /// Supports Dedicated Server auto-start, 10-player cap, Server Passcode protection,
    /// player coordinate restoration, and automatic client reconnection after mobile network drops.
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        public static NetworkBootstrap Instance { get; private set; }

        [Header("Server & World Configuration")]
        [SerializeField] private string gameplaySceneName = "01_CoralWorld";
        [SerializeField] private int maxPlayers = 10;
        [SerializeField] private ushort defaultPort = 7777;
        [SerializeField] private string defaultServerIp = "127.0.0.1";
        [SerializeField] private Vector3 defaultIslandSpawnPosition = new Vector3(0f, 2.5f, 0f);

        [Header("Security & Auto-Reconnect")]
        [Tooltip("Leave empty to allow passwordless connections; overridden by SERVER_PASSCODE env or -passcode CLI arg.")]
        [SerializeField] private string defaultServerPasscode = "1234";
        [SerializeField] private int maxAutoReconnectAttempts = 3;
        [SerializeField] private float reconnectDelaySeconds = 3.0f;

        private readonly Dictionary<ulong, string> _clientIdToUuid = new Dictionary<ulong, string>();
        private string _resolvedServerPasscode;

        private string _lastServerIp;
        private ushort _lastServerPort;
        private string _lastClientUuid;
        private string _lastClientPasscode;
        private bool _intentionalDisconnect;
        private bool _isReconnecting;
        private Coroutine _reconnectCoroutine;

        private const string PlayerUuidPrefsKey = "CORAL_PLAYER_UUID";

        public string DefaultServerPasscode => defaultServerPasscode;
        public bool IsReconnecting => _isReconnecting;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            _resolvedServerPasscode = ParsePasscodeArg(defaultServerPasscode);
        }

        private void Start()
        {
            ConfigureNetworkCallbacks();

            if (IsRunningAsDedicatedServer())
            {
                StartDedicatedServer();
            }
        }

        public static bool IsRunningAsDedicatedServer()
        {
#if UNITY_SERVER
            return true;
#else
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-dedicatedServer", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Equals("-server", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!Application.isEditor && args[i].Equals("-batchmode", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return !Application.isEditor && Application.isBatchMode;
#endif
        }

        private void ConfigureNetworkCallbacks()
        {
            if (NetworkManager.Singleton == null)
            {
                Debug.LogError("[NetworkBootstrap] NetworkManager.Singleton not found in 00_Bootstrap scene!");
                return;
            }

            NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
            NetworkManager.Singleton.ConnectionApprovalCallback = HandleConnectionApproval;
            NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        public void StartDedicatedServer()
        {
            _resolvedServerPasscode = ParsePasscodeArg(defaultServerPasscode);
            ushort port = ParsePortArg(defaultPort);
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport != null)
            {
                transport.SetConnectionData("0.0.0.0", port, "0.0.0.0");
            }

            SplitSaveManager.Instance?.LoadWorldSave();

            QualitySettings.vSyncCount = 0;
            NetworkManager.Singleton.NetworkConfig.TickRate = 30;

            bool started = NetworkManager.Singleton.StartServer();
            if (!started)
            {
                Debug.LogError("[NetworkBootstrap] Failed to start Dedicated Server!");
                return;
            }

            string pinNotice = string.IsNullOrEmpty(_resolvedServerPasscode) ? "OPEN" : "PASSCODE PROTECTED";
            Debug.Log($"[NetworkBootstrap] Dedicated Server listening on UDP 0.0.0.0:{port} (MaxPlayers={maxPlayers}, Security={pinNotice}).");
            NetworkManager.Singleton.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
        }

        /// <summary>
        /// Called by iOS/Editor Client UI Connect button to join the 24/7 VPS Dedicated Server.
        /// </summary>
        public void ConnectAsClient(
            string serverIp = null,
            ushort? port = null,
            string overridePlayerUuid = null,
            string passcode = null)
        {
            if (NetworkManager.Singleton == null)
                return;

            StopReconnectRoutine();
            _intentionalDisconnect = false;

            string ip = string.IsNullOrWhiteSpace(serverIp) ? defaultServerIp : serverIp.Trim();
            ushort targetPort = port ?? defaultPort;
            string uuid = string.IsNullOrWhiteSpace(overridePlayerUuid) ? GetOrCreateLocalPlayerUuid() : overridePlayerUuid.Trim();
            string pin = passcode ?? defaultServerPasscode;

            _lastServerIp = ip;
            _lastServerPort = targetPort;
            _lastClientUuid = uuid;
            _lastClientPasscode = pin;

            BeginClientConnection(ip, targetPort, uuid, pin);
        }

        public void DisconnectClientIntentionally()
        {
            _intentionalDisconnect = true;
            StopReconnectRoutine();

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                NetworkManager.Singleton.Shutdown();
            }
        }

        /// <summary>
        /// Convenience method for single-machine testing inside Unity Editor.
        /// </summary>
        public void StartLocalHostForTesting()
        {
            if (NetworkManager.Singleton == null)
                return;

            _intentionalDisconnect = false;
            _resolvedServerPasscode = defaultServerPasscode;
            SplitSaveManager.Instance?.LoadWorldSave();

            string uuid = GetOrCreateLocalPlayerUuid();
            NetworkManager.Singleton.NetworkConfig.ConnectionData = BuildConnectionPayload(uuid, _resolvedServerPasscode);

            if (NetworkManager.Singleton.StartHost())
            {
                NetworkManager.Singleton.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
            }
        }

        public string GetPlayerUuid(ulong clientId)
        {
            return _clientIdToUuid.TryGetValue(clientId, out string uuid) ? uuid : $"guest_{clientId}";
        }

        private void BeginClientConnection(string ip, ushort port, string uuid, string pin)
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport != null)
            {
                transport.SetConnectionData(ip, port);
            }

            NetworkManager.Singleton.NetworkConfig.ConnectionData = BuildConnectionPayload(uuid, pin);
            bool started = NetworkManager.Singleton.StartClient();
            Debug.Log($"[NetworkBootstrap] Connecting to {ip}:{port} as UUID={uuid} -> {(started ? "Started" : "Failed")}");
        }

        private void HandleConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            int currentCount = NetworkManager.Singleton.ConnectedClientsIds.Count;
            if (currentCount >= maxPlayers)
            {
                response.Approved = false;
                response.Reason = $"World is full ({maxPlayers}/{maxPlayers} players online).";
                response.Pending = false;
                return;
            }

            string playerUuid = "guest_" + request.ClientNetworkId;
            string providedPasscode = string.Empty;

            if (request.Payload != null && request.Payload.Length > 0)
            {
                string decoded = Encoding.UTF8.GetString(request.Payload).Trim();
                string[] parts = decoded.Split(new[] { '|' }, 2);
                if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    playerUuid = parts[0].Trim();
                }
                if (parts.Length >= 2)
                {
                    providedPasscode = parts[1].Trim();
                }
            }

            if (!string.IsNullOrEmpty(_resolvedServerPasscode) &&
                !string.Equals(providedPasscode, _resolvedServerPasscode, StringComparison.Ordinal))
            {
                response.Approved = false;
                response.Reason = "Invalid server passcode.";
                response.Pending = false;
                return;
            }

            _clientIdToUuid[request.ClientNetworkId] = playerUuid;

            PlayerSaveData playerSave = SplitSaveManager.Instance != null
                ? SplitSaveManager.Instance.LoadOrCreatePlayerSave(playerUuid, defaultIslandSpawnPosition)
                : new PlayerSaveData
                {
                    PlayerUUID = playerUuid,
                    LastPosition = SerializableVector3.FromVector3(defaultIslandSpawnPosition)
                };

            SplitSaveManager.Instance?.RegisterActivePlayerSave(request.ClientNetworkId, playerSave);

            response.Approved = true;
            response.CreatePlayerObject = true;
            response.Position = playerSave.LastPosition.ToVector3();
            response.Rotation = Quaternion.Euler(0f, playerSave.LastRotationY, 0f);
            response.Pending = false;
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (NetworkManager.Singleton != null &&
                !NetworkManager.Singleton.IsServer &&
                clientId == NetworkManager.Singleton.LocalClientId)
            {
                StopReconnectRoutine();
            }
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            _clientIdToUuid.Remove(clientId);

            if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer || _intentionalDisconnect)
                return;

            // Do not spam reconnect if server explicitly kicked/rejected (e.g. wrong passcode or full server)
            if (!string.IsNullOrEmpty(NetworkManager.Singleton.DisconnectReason))
            {
                Debug.LogWarning($"[NetworkBootstrap] Disconnected by server: {NetworkManager.Singleton.DisconnectReason}");
                return;
            }

            if (!_isReconnecting && !string.IsNullOrEmpty(_lastServerIp))
            {
                _reconnectCoroutine = StartCoroutine(AutoReconnectRoutine());
            }
        }

        private IEnumerator AutoReconnectRoutine()
        {
            _isReconnecting = true;

            for (int attempt = 1; attempt <= maxAutoReconnectAttempts; attempt++)
            {
                if (_intentionalDisconnect || NetworkManager.Singleton == null)
                    break;

                Debug.Log($"[NetworkBootstrap] Connection lost. Auto-reconnecting ({attempt}/{maxAutoReconnectAttempts}) in {reconnectDelaySeconds}s...");
                yield return new WaitForSecondsRealtime(reconnectDelaySeconds);

                if (_intentionalDisconnect || NetworkManager.Singleton == null)
                    break;

                while (NetworkManager.Singleton.ShutdownInProgress)
                {
                    yield return null;
                }

                if (NetworkManager.Singleton.IsConnectedClient)
                    break;

                BeginClientConnection(_lastServerIp, _lastServerPort, _lastClientUuid, _lastClientPasscode);

                float waitTime = 0f;
                while (waitTime < reconnectDelaySeconds + 2f)
                {
                    if (NetworkManager.Singleton.IsConnectedClient || _intentionalDisconnect)
                    {
                        _isReconnecting = false;
                        _reconnectCoroutine = null;
                        yield break;
                    }
                    waitTime += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            _isReconnecting = false;
            _reconnectCoroutine = null;
            Debug.LogWarning("[NetworkBootstrap] Auto-reconnect exhausted.");
        }

        private void StopReconnectRoutine()
        {
            _isReconnecting = false;
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
        }

        private static byte[] BuildConnectionPayload(string uuid, string passcode)
        {
            return Encoding.UTF8.GetBytes($"{uuid}|{passcode ?? string.Empty}");
        }

        private static string GetOrCreateLocalPlayerUuid()
        {
            string existing = PlayerPrefs.GetString(PlayerUuidPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            string rawId = !string.IsNullOrEmpty(SystemInfo.deviceUniqueIdentifier) &&
                           SystemInfo.deviceUniqueIdentifier != SystemInfo.unsupportedIdentifier
                ? SystemInfo.deviceUniqueIdentifier
                : Guid.NewGuid().ToString("N");

            string formatted = $"ios_{rawId}";
            PlayerPrefs.SetString(PlayerUuidPrefsKey, formatted);
            PlayerPrefs.Save();
            return formatted;
        }

        private static ushort ParsePortArg(ushort fallbackPort)
        {
            string envPort = Environment.GetEnvironmentVariable("SERVER_PORT");
            if (!string.IsNullOrEmpty(envPort) && ushort.TryParse(envPort, out ushort parsedEnv))
                return parsedEnv;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("-port", StringComparison.OrdinalIgnoreCase) &&
                    ushort.TryParse(args[i + 1], out ushort parsedArg))
                {
                    return parsedArg;
                }
            }

            return fallbackPort;
        }

        private static string ParsePasscodeArg(string fallbackPasscode)
        {
            string envPin = Environment.GetEnvironmentVariable("SERVER_PASSCODE");
            if (envPin != null)
                return envPin.Trim();

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("-passcode", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1].Trim();
                }
            }

            return fallbackPasscode != null ? fallbackPasscode.Trim() : string.Empty;
        }
    }
}
