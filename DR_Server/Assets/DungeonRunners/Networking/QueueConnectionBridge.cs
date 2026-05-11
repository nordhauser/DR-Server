using System;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class QueueConnectionBridge
    {
        private static readonly Dictionary<string, NetworkStream> _queueStreams
            = new Dictionary<string, NetworkStream>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _pendingQueueIPs
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Queue<(string username, TcpClient client)> _pendingQueueClients
            = new Queue<(string, TcpClient)>();

        private static readonly object _lock = new object();

        public static event Action<string> OnQueueStreamRegistered;

        // ═══ QUEUE TRACKING (binary-verified: server-side only) ═══
        public static int MaxPlayers = 1;
        private static int _currentPlayers = 0;

        public static int CurrentPlayers
        {
            get { lock (_lock) { return _currentPlayers; } }
        }

        public static bool HasCapacity
        {
            get { lock (_lock) { return _currentPlayers < MaxPlayers; } }
        }

        public static void PlayerConnected()
        {
            lock (_lock) { _currentPlayers++; }
            Debug.Log($"[QUEUE-BRIDGE] Player connected. Now: {_currentPlayers}/{MaxPlayers}");
        }

        public static void PlayerDisconnected()
        {
            lock (_lock) { if (_currentPlayers > 0) _currentPlayers--; }
            Debug.Log($"[QUEUE-BRIDGE] Player disconnected. Now: {_currentPlayers}/{MaxPlayers}");
        }

        // ═══ IP TRACKING ═══
        public static void ExpectQueueFromIP(string ip, string username)
        {
            lock (_lock)
            {
                _pendingQueueIPs[ip] = username;
                Debug.Log($"[QUEUE-BRIDGE] Expecting queue from IP {ip} for {username}");
            }
        }

        public static string CheckAndConsumeQueueIP(string ip)
        {
            lock (_lock)
            {
                if (_pendingQueueIPs.TryGetValue(ip, out string username))
                {
                    _pendingQueueIPs.Remove(ip);
                    return username;
                }
                return null;
            }
        }

        public static List<string> GetPendingIPs()
        {
            lock (_lock)
            {
                return new List<string>(_pendingQueueIPs.Keys);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // SOCIAL CHANNEL — Queue stream for channel 0x0C
        // ═══════════════════════════════════════════════════════════════
        // Binary-verified: UserManagerClient (ch 0x0C) sends outbound on the queue
        // connection's TChannelManager, NOT the game connection. After HandoffToGame,
        // the queue TCP stream stays alive and carries social messages using
        // [uint32 len][CompressedA 0x0A] framing.
        //
        // Flow:
        //   1. After HandoffToGame, client sends THCSockets 0x03 on queue stream
        //   2. Server responds 0x04 → connection is live for MessageService
        //   3. Client sends CompressedA 0x0A with channel 12 messages (add friend, etc.)
        //   4. Server responds via CompressedA 0x0A with channel 12 data (roster, who, etc.)
        //
        // All wrapped in queue transport framing: [uint32 len][payload]
        // ═══════════════════════════════════════════════════════════════

        // Per-user queue peer ID for CompressedA header
        private static readonly Dictionary<string, uint> _queuePeerIds
            = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        // Incoming social messages from queue stream → polled by UGS.Update()
        private static readonly Queue<(string username, byte messageType, byte[] data)> _incomingSocialQueue
            = new Queue<(string, byte, byte[])>();

        /// <summary>
        /// Register a queue stream with its peer ID for social message routing.
        /// Called after THCSockets 0x03/0x04 handshake completes post-HandoffToGame.
        /// </summary>
        public static void RegisterSocialStream(string username, NetworkStream stream, uint peerIdFromClient)
        {
            lock (_lock)
            {
                _queueStreams[username] = stream;
                _queuePeerIds[username] = peerIdFromClient;
                Debug.LogError($"[QUEUE-BRIDGE] Registered social stream for {username} peerId=0x{peerIdFromClient:X6}");
            }
            OnQueueStreamRegistered?.Invoke(username);
        }

        /// <summary>
        /// Enqueue an incoming social message from the queue read loop.
        /// Called from the AuthConnection coroutine when channel 12 data arrives.
        /// </summary>
        public static void EnqueueIncomingSocial(string username, byte messageType, byte[] data)
        {
            lock (_lock)
            {
                _incomingSocialQueue.Enqueue((username, messageType, data));
                Debug.LogError($"[QUEUE-BRIDGE] Enqueued incoming social for {username} type=0x{messageType:X2} len={data?.Length ?? 0}");
            }
        }

        /// <summary>
        /// Drain all pending incoming social messages.
        /// Called from UGS.Update() on the main thread.
        /// </summary>
        public static List<(string username, byte messageType, byte[] data)> DrainIncomingSocial()
        {
            lock (_lock)
            {
                if (_incomingSocialQueue.Count == 0)
                    return null;

                var list = new List<(string, byte, byte[])>();
                while (_incomingSocialQueue.Count > 0)
                    list.Add(_incomingSocialQueue.Dequeue());
                return list;
            }
        }

        /// <summary>
        /// Send a social response back to the client via the queue stream.
        /// Uses CompressedA (0x0A) framing wrapped in [uint32 len] transport.
        /// Binary-verified: queue transport uses length-prefixed frames throughout
        /// (netTCPOutConnection state 7 READY sends via encrypted framing, our
        /// no-op crypto means just [uint32 len][payload]).
        /// </summary>
        public static void SendOnQueueStream(string username, byte dest, byte messageType, byte[] innerData)
        {
            lock (_lock)
            {
                if (!_queueStreams.TryGetValue(username, out var stream))
                {
                    Debug.LogError($"[QUEUE-TX-SOCIAL] No queue stream for {username} — cannot send social");
                    return;
                }
                if (!_queuePeerIds.TryGetValue(username, out var peerId))
                {
                    Debug.LogError($"[QUEUE-TX-SOCIAL] No peerId for {username} — cannot send social");
                    return;
                }

                try
                {
                    byte[] compressed = ZlibUtil.Deflate(innerData);

                    // Build CompressedA payload:
                    // [0x0A][peerId 3b][bodyLen 4b][dest][msgType][0x00][uncompLen 4b][compressed...]
                    var pkt = new ByteWriter();
                    pkt.WriteByte(0x0A);
                    pkt.WriteByte((byte)(peerId & 0xFF));
                    pkt.WriteByte((byte)((peerId >> 8) & 0xFF));
                    pkt.WriteByte((byte)((peerId >> 16) & 0xFF));
                    pkt.WriteUInt32((uint)(compressed.Length + 7));  // bodyLen
                    pkt.WriteByte(dest);         // 0x01
                    pkt.WriteByte(messageType);  // 0x0F
                    pkt.WriteByte(0x00);         // padding
                    pkt.WriteUInt32((uint)innerData.Length);  // uncompressed length
                    for (int i = 0; i < compressed.Length; i++)
                        pkt.WriteByte(compressed[i]);
                    byte[] compressedA = pkt.ToArray();

                    // Wrap in queue transport framing: [uint32 len][payload]
                    var frame = new ByteWriter();
                    frame.WriteUInt32((uint)compressedA.Length);
                    for (int i = 0; i < compressedA.Length; i++)
                        frame.WriteByte(compressedA[i]);
                    byte[] frameBytes = frame.ToArray();

                    stream.Write(frameBytes, 0, frameBytes.Length);
                    stream.Flush();
                    Debug.LogError($"[QUEUE-TX-SOCIAL] Sent to {username}: inner={innerData.Length}b compressed={compressed.Length}b frame={frameBytes.Length}b");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[QUEUE-TX-SOCIAL] Error sending to {username}: {ex.Message}");
                    // Stream is dead — remove it
                    _queueStreams.Remove(username);
                    _queuePeerIds.Remove(username);
                }
            }
        }

        // ═══ STREAM TRACKING (legacy wrappers) ═══
        public static void RegisterQueueStream(string username, NetworkStream stream)
        {
            lock (_lock)
            {
                _queueStreams[username] = stream;
                Debug.Log($"[QUEUE-BRIDGE] Registered queue stream for {username}");
            }
            OnQueueStreamRegistered?.Invoke(username);
        }

        public static NetworkStream GetQueueStream(string username)
        {
            lock (_lock)
            {
                if (username != null && _queueStreams.TryGetValue(username, out var stream))
                    return stream;
                return null;
            }
        }

        public static bool HasQueueStream(string username)
        {
            lock (_lock)
            {
                return username != null && _queueStreams.ContainsKey(username);
            }
        }

        public static void RemoveQueueStream(string username)
        {
            lock (_lock)
            {
                if (username != null)
                {
                    if (_queueStreams.Remove(username))
                        Debug.Log($"[QUEUE-BRIDGE] Removed queue stream for {username}");
                    _queuePeerIds.Remove(username);
                }
            }
        }

        public static void EnqueueForAuthHandling(string username, TcpClient client)
        {
            lock (_lock)
            {
                _pendingQueueClients.Enqueue((username, client));
                Debug.Log($"[QUEUE-BRIDGE] Queued TcpClient for auth handling: {username}");
            }
        }

        public static (string username, TcpClient client)? DequeueForAuthHandling()
        {
            lock (_lock)
            {
                if (_pendingQueueClients.Count > 0)
                    return _pendingQueueClients.Dequeue();
                return null;
            }
        }
    }
}