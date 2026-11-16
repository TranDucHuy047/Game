using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using CoralArchipelago.Core;
using CoralArchipelago.Networking;
using CoralArchipelago.Persistence;

namespace CoralArchipelago.Gameplay.Coop
{
    public struct NetworkCampLogEntry : INetworkSerializable, IEquatable<NetworkCampLogEntry>
    {
        public FixedString32Bytes TimestampText;
        public FixedString128Bytes Message;

        public NetworkCampLogEntry(FixedString32Bytes timestampText, FixedString128Bytes message)
        {
            TimestampText = timestampText;
            Message = message;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref TimestampText);
            serializer.SerializeValue(ref Message);
        }

        public bool Equals(NetworkCampLogEntry other)
        {
            return TimestampText.Equals(other.TimestampText) && Message.Equals(other.Message);
        }

        public override bool Equals(object obj) => obj is NetworkCampLogEntry other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(TimestampText, Message);
    }

    /// <summary>
    /// Synchronized Shared Camp Activity Log & Message Board for asynchronous 10-player co-op.
    /// Records teammate deposits, builds, and Altar upgrades, and lets players leave notes for the next shift.
    /// </summary>
    public class SharedCampLogManager : NetworkBehaviour
    {
        public static SharedCampLogManager Instance { get; private set; }

        [SerializeField] private int maxLogEntries = 30;

        public readonly NetworkList<NetworkCampLogEntry> Logs = new NetworkList<NetworkCampLogEntry>(
            readPerm: NetworkVariableReadPermission.Everyone,
            writePerm: NetworkVariableWritePermission.Server);

        public event Action OnLogsUpdated;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            Logs.OnListChanged += HandleLogsChanged;

            if (IsServer && SplitSaveManager.Instance != null)
            {
                Logs.Clear();
                List<CampLogEntryRecord> saved = SplitSaveManager.Instance.CurrentWorldSave.CampLogs;
                for (int i = 0; i < saved.Count && i < maxLogEntries; i++)
                {
                    CampLogEntryRecord entry = saved[i];
                    if (entry != null && !string.IsNullOrEmpty(entry.Message))
                    {
                        Logs.Add(new NetworkCampLogEntry(entry.TimestampText ?? "", entry.Message));
                    }
                }
            }

            OnLogsUpdated?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            Logs.OnListChanged -= HandleLogsChanged;
        }

        private void HandleLogsChanged(NetworkListEvent<NetworkCampLogEntry> changeEvent)
        {
            OnLogsUpdated?.Invoke();
        }

        public void RecordActivityServer(string message)
        {
            if (!IsServer || !IsSpawned || string.IsNullOrWhiteSpace(message))
                return;

            int day = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.CurrentDay.Value : 1;
            float hour = WorldTimeManager.Instance != null ? WorldTimeManager.Instance.WorldTimeHours.Value : 8f;
            string ts = $"D{day} {Mathf.FloorToInt(hour):00}:{Mathf.FloorToInt((hour % 1f) * 60f):00}";

            string trimmedMsg = message.Length > 110 ? message.Substring(0, 110) : message;

            while (Logs.Count >= maxLogEntries)
            {
                Logs.RemoveAt(0);
            }

            Logs.Add(new NetworkCampLogEntry(ts, trimmedMsg));
            SplitSaveManager.Instance?.AppendCampLogRecord(ts, trimmedMsg);
        }

        [ServerRpc(RequireOwnership = false)]
        public void PostPlayerNoteServerRpc(FixedString128Bytes noteText, ServerRpcParams rpcParams = default)
        {
            string raw = noteText.ToString().Trim();
            if (string.IsNullOrEmpty(raw))
                return;

            ulong senderId = rpcParams.Receive.SenderClientId;
            string uuid = NetworkBootstrap.Instance != null
                ? NetworkBootstrap.Instance.GetPlayerUuid(senderId)
                : $"P{senderId}";

            string shortTag = uuid.Length > 6 ? uuid.Substring(uuid.Length - 4) : uuid;
            RecordActivityServer($"[Note #{shortTag}] {raw}");
        }

        public string BuildFormattedLogText(int maxDisplayCount = 4)
        {
            if (Logs.Count == 0)
                return "Camp Log: No activity yet.";

            var sb = new StringBuilder();
            int start = Mathf.Max(0, Logs.Count - maxDisplayCount);
            for (int i = start; i < Logs.Count; i++)
            {
                if (i > start) sb.Append('\n');
                sb.Append('[').Append(Logs[i].TimestampText).Append("] ").Append(Logs[i].Message);
            }
            return sb.ToString();
        }
    }
}
