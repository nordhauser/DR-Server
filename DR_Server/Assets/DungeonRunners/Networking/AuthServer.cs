using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using DungeonRunners.Core;
using DungeonRunners.Utilities;
using DungeonRunners.Managers;
//using Org.BouncyCastle.Crypto.Engines;
//using Org.BouncyCastle.Crypto.Parameters;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// Authentication server for Dungeon Runners - FINAL VERSION
    /// </summary>
    public class AuthServer : MonoBehaviour
    {
        [SerializeField] private ServerConfig config;

        private UnityTcpListener _listener;
        private UnityTcpListener _queueListener;  // Queue server on port 2606
        private List<AuthConnection> _connections = new List<AuthConnection>();

        // Queue detection: username of player expecting a queue connection
        // Set synchronously before TCP write, checked on next accept
        private static string _pendingQueueUser = null;
        private static uint _pendingQueueToken = 0;
        private static string _pendingQueueAdvertisedServerIP = null;

        void Start()
        {
            if (config == null)
            {
                Debug.LogError("ServerConfig not assigned!");
                return;
            }

            // Override ScriptableObject values from server.cfg (no rebuild needed)
            config.authServerIP = ServerSettings.GetString("authIP", config.authServerIP);
            config.authServerPort = ServerSettings.Get("authPort", config.authServerPort);
            config.gameServerIP = ServerSettings.GetString("gameIP", config.gameServerIP);
            config.gameServerPort = ServerSettings.Get("gamePort", config.gameServerPort);
            int queuePort = ServerSettings.Get("queuePort", 2606);

            _listener = new UnityTcpListener();
            _listener.OnClientConnected += OnClientConnected;
            _listener.Start(config.authServerIP, config.authServerPort);

            Debug.Log($"✅ Auth Server started on {config.authServerIP}:{config.authServerPort}");

            // Queue listener
            _queueListener = new UnityTcpListener();
            _queueListener.OnClientConnected += OnQueueClientConnected;
            _queueListener.Start(config.authServerIP, queuePort);
            Debug.Log($"✅ Queue Server started on {config.authServerIP}:{queuePort}");
        }

        void OnDestroy()
        {
            _listener?.Stop();
            _queueListener?.Stop();

            foreach (var conn in _connections)
            {
                conn.Disconnect();
            }
            _connections.Clear();
        }

        void Update()
        {
            // Bridge polling DISABLED — port 2606 listener handles queue connections directly.
            // The bridge caused duplicate QueueReceiveAndHandoff: one on the game-port connection
            // (wrong) and one on the real queue connection (right). Session 50 fix.
        }

        /// <summary>
        /// Set by AuthConnection.SendGoHandoffToQueue before TCP write.
        /// Consumed by OnClientConnected when queue connection arrives.
        /// </summary>
        public static void SetPendingQueueUser(string username, uint token = 0, string advertisedServerIP = null)
        {
            _pendingQueueUser = username;
            _pendingQueueToken = token;
            _pendingQueueAdvertisedServerIP = advertisedServerIP;
        }

        private void OnClientConnected(TcpClient client)
        {
            // Auth port ONLY handles auth connections — queue goes to port 2606
            Debug.Log($"✅ Auth client connected: {client.Client.RemoteEndPoint}");

            var connection = new AuthConnection(client, config);
            connection.OnDisconnected += () => OnConnectionDisconnected(connection);
            connection.StartReceiving(this);

            _connections.Add(connection);
        }

        private void OnConnectionDisconnected(AuthConnection connection)
        {
            Debug.Log("Auth client disconnected");
            _connections.Remove(connection);
        }

        private void OnQueueClientConnected(TcpClient client)
        {
            // Queue port 2606 — ALWAYS a queue connection
            if (string.IsNullOrWhiteSpace(_pendingQueueUser))
            {
                Debug.LogError($"[QUEUE] Rejected unexpected queue connection from {client.Client.RemoteEndPoint}");
                try { client.Close(); } catch { }
                return;
            }

            string queueUser = _pendingQueueUser ?? "UNKNOWN";
            uint queueToken = _pendingQueueToken;
            string queueAdvertisedServerIP = _pendingQueueAdvertisedServerIP;
            _pendingQueueUser = null;
            _pendingQueueToken = 0;
            _pendingQueueAdvertisedServerIP = null;
            Debug.LogError($"[QUEUE] ✅ Queue connection on port 2606 for '{queueUser}' from {client.Client.RemoteEndPoint}");

            var queueConn = new AuthConnection(client, config);
            queueConn.SetSessionToken(queueToken);
            queueConn.SetAdvertisedServerIP(queueAdvertisedServerIP);
            queueConn.StartQueueMode(this, queueUser);
            _connections.Add(queueConn);
        }
    }

    public class AuthConnection
    {
        private ClientConnection _client;
        private ServerConfig _config;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private string _username; // Store the logged-in username
        private uint _sessionToken; // Store session token for HandoffToGame
        private string _advertisedServerIP;

        public void SetSessionToken(uint token)
        {
            _sessionToken = token;
        }

        public void SetAdvertisedServerIP(string advertisedServerIP)
        {
            if (!string.IsNullOrWhiteSpace(advertisedServerIP))
                _advertisedServerIP = advertisedServerIP;
        }
        private MonoBehaviour _coroutineRunner; // For delayed sends
        private bool _handoffSent = false; // Guard against duplicate AboutToPlay

        public event Action OnDisconnected;

        public AuthConnection(TcpClient client, ServerConfig config)
        {
            _tcpClient = client;
            _stream = client.GetStream();
            _config = config;
            _client = new ClientConnection(client);
            _advertisedServerIP = ResolveAdvertisedServerIP(client, config.gameServerIP);
            Debug.LogError($"[AUTH-NET] remote={GetEndpointIP(client?.Client?.RemoteEndPoint) ?? "unknown"} local={GetEndpointIP(client?.Client?.LocalEndPoint) ?? "unknown"} configured={config.gameServerIP} advertised={_advertisedServerIP}");

            _client.OnDisconnected += () => OnDisconnected?.Invoke();
        }

        public void StartReceiving(MonoBehaviour coroutineRunner)
        {
            _coroutineRunner = coroutineRunner;

            // Send Go welcome packet - this is what the working Go server does
            SendGoWelcomePacket();

            // Start receiving encrypted packets
            coroutineRunner.StartCoroutine(ReceiveLoop());
        }

        public void Disconnect()
        {
            _client.Disconnect();
        }

        private void SendGoWelcomePacket()
        {
            byte[] welcomePacket = new byte[] { 3, 0, 0 };
            Debug.Log($"📤 Sent Go welcome packet: [3, 0, 0]");
            _stream.Write(welcomePacket, 0, welcomePacket.Length);
        }

        private IEnumerator ReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            bool hasError = false;

            while (_tcpClient.Connected && !hasError)
            {
                int headerBytesRead = 0;
                while (headerBytesRead < 2 && _tcpClient.Connected)
                {
                    if (!_stream.DataAvailable)
                    {
                        yield return new WaitForSeconds(0.01f);
                        continue;
                    }

                    int bytesRead = _stream.Read(buffer, headerBytesRead, 2 - headerBytesRead);
                    if (bytesRead == 0)
                    {
                        hasError = true;
                        break;
                    }
                    headerBytesRead += bytesRead;
                }

                if (hasError || headerBytesRead < 2) break;

                ushort totalLen = (ushort)(buffer[0] | (buffer[1] << 8));
                if (totalLen < 4)
                {
                    Debug.LogWarning($"Bad frame length: {totalLen}");
                    break;
                }
                if (totalLen > buffer.Length)
                {
                    Debug.LogWarning($"Bad frame length too large: {totalLen}");
                    break;
                }

                int bytesToRead = totalLen - 2;
                int bytesReadTotal = 0;

                while (bytesReadTotal < bytesToRead && _tcpClient.Connected)
                {
                    if (!_stream.DataAvailable)
                    {
                        yield return new WaitForSeconds(0.01f);
                        continue;
                    }

                    int bytesRead = _stream.Read(buffer, 2 + bytesReadTotal, bytesToRead - bytesReadTotal);
                    if (bytesRead == 0)
                    {
                        hasError = true;
                        break;
                    }
                    bytesReadTotal += bytesRead;
                }

                if (hasError || bytesReadTotal < bytesToRead) break;

                byte[] rest = new byte[bytesReadTotal];
                Array.Copy(buffer, 2, rest, 0, bytesReadTotal);

                try
                {
                    byte[] plainBody = DecryptGoBlowfishEndian(rest, bytesReadTotal);

                    if (plainBody.Length > 0)
                    {
                        byte msgType = plainBody[0];
                        Debug.Log($"🔍 Received message type: 0x{msgType:X2}, bodyLen: {plainBody.Length - 1}");

                        ProcessMessage(msgType, plainBody);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Process error: {ex.Message}");
                    hasError = true;
                }
            }

            Debug.Log("Receive loop ended");
            OnDisconnected?.Invoke();
        }

        private byte[] DecryptGoBlowfishEndian(byte[] encryptedData, int encryptedSize)
        {
            try
            {
                int numBlocks = encryptedSize / 8;
                byte[] decrypted = new byte[encryptedSize];

                var blowfish = new BlowfishEncryption(_config.blowfishKey);

                for (int i = 0; i < numBlocks; i++)
                {
                    int start = i * 8;
                    byte[] block = new byte[8];
                    Array.Copy(encryptedData, start, block, 0, 8);

                    uint v1 = BitConverter.ToUInt32(block, 0);
                    uint v2 = BitConverter.ToUInt32(block, 4);

                    byte[] bigEndianBlock = new byte[8];
                    bigEndianBlock[0] = (byte)((v1 >> 24) & 0xFF);
                    bigEndianBlock[1] = (byte)((v1 >> 16) & 0xFF);
                    bigEndianBlock[2] = (byte)((v1 >> 8) & 0xFF);
                    bigEndianBlock[3] = (byte)(v1 & 0xFF);
                    bigEndianBlock[4] = (byte)((v2 >> 24) & 0xFF);
                    bigEndianBlock[5] = (byte)((v2 >> 16) & 0xFF);
                    bigEndianBlock[6] = (byte)((v2 >> 8) & 0xFF);
                    bigEndianBlock[7] = (byte)(v2 & 0xFF);

                    byte[] decryptedBlock = blowfish.Decrypt(bigEndianBlock);

                    uint ev1 = (uint)((decryptedBlock[0] << 24) | (decryptedBlock[1] << 16) | (decryptedBlock[2] << 8) | decryptedBlock[3]);
                    uint ev2 = (uint)((decryptedBlock[4] << 24) | (decryptedBlock[5] << 16) | (decryptedBlock[6] << 8) | decryptedBlock[7]);

                    byte[] littleEndianBlock = new byte[8];
                    littleEndianBlock[0] = (byte)(ev1 & 0xFF);
                    littleEndianBlock[1] = (byte)((ev1 >> 8) & 0xFF);
                    littleEndianBlock[2] = (byte)((ev1 >> 16) & 0xFF);
                    littleEndianBlock[3] = (byte)(ev1 >> 24);
                    littleEndianBlock[4] = (byte)(ev2 & 0xFF);
                    littleEndianBlock[5] = (byte)((ev2 >> 8) & 0xFF);
                    littleEndianBlock[6] = (byte)((ev2 >> 16) & 0xFF);
                    littleEndianBlock[7] = (byte)(ev2 >> 24);

                    Array.Copy(littleEndianBlock, 0, decrypted, start, 8);
                }

                return decrypted;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Decrypt error: {ex.Message}");
                return new byte[0];
            }
        }

        private void ProcessMessage(byte msgType, byte[] data)
        {
            try
            {
                switch (msgType)
                {
                    case 0x00:
                        HandleLogin(data);
                        break;

                    case 0x02:
                        HandleAboutToPlay(data);
                        break;

                    case 0x05:
                        HandleServerListExt();
                        break;

                    default:
                        Debug.LogWarning($"❌ Unknown message: 0x{msgType:X2}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"ProcessMessage error: {ex.Message}");
            }
        }

        private void HandleLogin(byte[] data)
        {
            try
            {
                if (data.Length < 31)
                {
                    Debug.LogWarning("Login data too short");
                    return;
                }

                var reader = new ByteReader(data);
                reader.ReadByte();

                byte[] loginBlock = reader.ReadBytes(24);
                byte[] tail6 = reader.ReadBytes(6);

                (string username, string password) = DecodeLogin(loginBlock, tail6);

                Debug.Log($"🔑 Login attempt: {username}");

                // Store the username for later use
                _username = username;
                _advertisedServerIP = ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP);

                // Get or create account in SQLite — no password check (matches original server)
                Database.GameDatabase.Initialize();
                uint accountId = Database.AccountRepository.GetAccountId(username);
                if (accountId == 0)
                {
                    // First login — auto-create account
                    accountId = Database.AccountRepository.CreateAccount(username, password);
                    Debug.Log($"🆕 Created account '{username}' (ID: {accountId})");
                }
                else
                {
                    Debug.Log($"✅ Found account '{username}' (ID: {accountId})");
                }

                // Ban check — send BlockedAccount packet if banned
                if (IsAccountBanned(username))
                {
                    Debug.LogError($"⛔ Account '{username}' is BANNED — sending BlockedAccount");
                    SendGoBlockedAccount();
                    return;
                }

                SendGoLoginOk(accountId);
                SendGoServerList();
                Debug.Log($"📋 ServerListEx sent to {_advertisedServerIP}:{_config.gameServerPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Login error: {ex}");
            }
        }

        private void HandleAboutToPlay(byte[] data)
        {
            var reader = new ByteReader(data);
            reader.ReadByte();

            uint lo = reader.ReadUInt32();
            uint hi = reader.ReadUInt32();
            byte serverId = reader.ReadByte();

            Debug.Log($"🎮 AboutToPlay: lo=0x{lo:X8}, hi=0x{hi:X8}, serverId={serverId}");

            uint sessionToken = (uint)DateTime.Now.Ticks ^ 0x12345678;
            _sessionToken = sessionToken;
            Core.GlobalSessions.Store(sessionToken, _username ?? "unknown");
            SessionManager.Instance.SetPlayToken(sessionToken, _username ?? "unknown");

            Debug.Log($"🎫 Generated session token 0x{sessionToken:X8} for user '{_username}'");

            // CRITICAL TIMING: Set queue username BEFORE sending anything!
            AuthServer.SetPendingQueueUser(_username, sessionToken, _advertisedServerIP);
            Debug.Log($"🔗 Queue username set for {_username}");

            // PDB-VERIFIED Session 50:
            //   - 0x0F was "Unknown msg type: 15" — NOT registered
            //   - 0x0C = linACHandoffToQueuePacket — the CORRECT type for HandoffToQueue!
            //   - PlayOk (0x07) causes IMMEDIATE TCP close → can't use it with queue
            //   - HandoffToQueue (0x0C) should trigger RecvHandoffToQueueMsg@0x61BE00
            //     which calls ConnectToQueue → connects to server list entry as queue client
            //   - Server list must point to queue port 2606
            SendGoHandoffToQueue(sessionToken, serverId);
            Debug.Log($"🔗 HandoffToQueue (0x0C) sent for {_username}, serverId={serverId}");
        }

        private void HandleServerListExt()
        {
            SendGoServerList();
        }

        private void SendGoLoginOk(uint accountId)
        {
            var writer = new ByteWriter();
            writer.WriteUInt32(0xFFEEFFEE);
            writer.WriteUInt32(0xAABBAABB);
            writer.WriteUInt32(0xDDCCDDCC);
            writer.WriteUInt32(0xBBCCBBCC);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0xFFFFFFFF);
            writer.WriteUInt32(0xFFFFFFFF);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            writer.WriteByte(0x01);
            writer.WriteByte(0x01);
            Debug.Log($"🎮 PlayOk packet: {BitConverter.ToString(writer.ToArray())}");
            WriteGoAuthMessage(0x03, writer.ToArray());
            Debug.Log($"✅ LoginOk sent, accountId={accountId}");
        }

        private bool IsAccountBanned(string username)
        {
            try
            {
                using (var conn = Database.GameDatabase.GetConnection())
                {
                    object result = Database.GameDatabase.ExecuteScalar(conn,
                        "SELECT is_banned FROM accounts WHERE username = @u",
                        ("@u", username));
                    if (result != null && result != DBNull.Value)
                        return Convert.ToInt32(result) != 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTH] Ban check error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Send linACBlockedAccountPacket (wire type 0x02) — triggers "Blocked!" popup on client.
        /// Binary: RecvBlockedAccount@DRAuthClient @ 0x61B640 → ShowBlockedPopup@LoginUI @ 0x44F4B0
        /// </summary>
        private void SendGoBlockedAccount()
        {
            var writer = new ByteWriter();
            writer.WriteByte(0x00);  // reason code (0 = generic block)
            WriteGoAuthMessage(0x02, writer.ToArray());
            Debug.LogError($"⛔ BlockedAccount packet sent (type 0x02)");
        }

        private void SendGoServerList()
        {
            string advertisedIP = string.IsNullOrWhiteSpace(_advertisedServerIP)
                ? ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP)
                : _advertisedServerIP;
            uint ipInt = IPToUInt32(advertisedIP);

            var writer = new ByteWriter();
            byte serverId = 0x01;
            writer.WriteByte(0x01);
            writer.WriteByte(serverId);

            // Server list → queue port 2606.
            // HandoffToQueue (0x0C) uses serverId to look up server list entry.
            // Client calls ConnectToQueue with the IP:port from the entry.
            uint queuePort = (uint)ServerSettings.Get("queuePort", 2606);
            writer.WriteByte(serverId);
            writer.WriteUInt32(ipInt);
            writer.WriteUInt32(queuePort);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt16((ushort)Math.Max(0, QueueConnectionBridge.CurrentPlayers));
            writer.WriteUInt16((ushort)Math.Max(1, QueueConnectionBridge.MaxPlayers));
            writer.WriteByte(0x01);

            WriteGoAuthMessage(0x04, writer.ToArray());
            Debug.Log($"📋 Server list sent with 1 server (ID 1) → {advertisedIP}:{queuePort}");
        }

        private void SendGoPlayOk(uint playToken, byte serverId)
        {
            var writer = new ByteWriter();
            writer.WriteUInt32(playToken);
            writer.WriteUInt32(0x5678DEFA);
            writer.WriteByte(serverId);

            byte[] payload = writer.ToArray();
            Debug.Log($"🎮 PlayOk payload bytes: {BitConverter.ToString(payload).Replace("-", " ")}");
            Debug.Log($"🎮 PlayOk payload length: {payload.Length} bytes");
            Debug.Log($"🎮 PlayOk - Token: 0x{playToken:X8}, UID: 0x5678DEFA, ServerID: {serverId}");

            WriteGoAuthMessage(0x07, payload);
            Debug.Log($"🎮 PlayOk sent, token=0x{playToken:X8}, serverId={serverId}");
        }

        private void SendGoHandoffToQueue(uint playToken, byte serverId)
        {
            // PDB-VERIFIED: linACHandoffToQueuePacket ctor sets type = 0x0C (12)
            // TTD proved 0x0F → "Unknown msg type: 15" — that was WRONG
            //
            // Unserialize@0x798DB0 reads 3 fields:
            //   uint32 → +0x10 (param1 to RecvHandoffToQueueMsg)
            //   uint32 → +0x14 (param2)
            //   byte   → +0x18 (param3)
            //
            // MsgHandoffToQueue@0x799A10 passes these to:
            //   RecvHandoffToQueueMsg(this, [ebx+0x10], [ebx+0x14], [ebx+0x18])

            var writer = new ByteWriter();
            writer.WriteUInt32(playToken);       // field1: playToken
            writer.WriteUInt32(0x5678DEFA);      // field2: uid
            writer.WriteByte(serverId);          // field3: serverId

            byte[] payload = writer.ToArray();
            Debug.Log($"🔗 HandoffToQueue payload (type=0x0C): {BitConverter.ToString(payload)}");

            WriteGoAuthMessage(0x0C, payload); // 0x0C = linACHandoffToQueuePacket type ID!
            Debug.Log($"🔗 HandoffToQueue sent, serverId={serverId} for {_username}");
        }

        /// <summary>
        /// Queue mode: THCSockets protocol (same wire format as game server)
        /// NOT Go auth encryption — queue uses netTCPConnection + msgRouter
        /// </summary>
        public void StartQueueMode(MonoBehaviour coroutineRunner, string username)
        {
            _username = username;
            if (string.IsNullOrWhiteSpace(_advertisedServerIP))
                _advertisedServerIP = ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP);
            // NO Go welcome! Queue uses THCSockets, not Go auth protocol.
            coroutineRunner.StartCoroutine(QueueReceiveAndHandoff());
        }

        private uint _queuePeerId = 0;

        private IEnumerator QueueReceiveAndHandoff()
        {
            byte[] buffer = new byte[4096];

            // ══════════════════════════════════════════════════════════════
            // TTD + PDB VERIFIED Queue Protocol (Session 50):
            //
            // LAYER 1 — Transport crypto handshake (netTCPOutConnection):
            //   1. Server sends: [uint32=36][keyExSz=8][pubSz=8][secSz=8][keyEx][pub][sec]
            //   2. Client responds: [uint32=12][keyExSz=8]["PUBKEY12"]
            //   3. State goes 0→4→5→6
            //   4. Server sends: [uint32=6]["ENC OK"]
            //   5. State 6→7 (READY!) → 0x03 handshake fires
            //
            // LAYER 2 — THCSockets session handshake:
            //   Client sends 0x03 [peerId 3b]
            //   Server sends 0x04 [peerId 3b] [pad 4b]
            //   → QueueMsgConnectOk → OnConnectedToQueue → auth state 6→7
            //
            // LAYER 3 — Queue application messages (CompressedA 0x0A):
            //   Client sends login (type 0x07): [uint32 token][uint32 uid]
            //   Server sends PositionInQueue (type 0x0D): [uint32 pos][uint32 waitMs]
            //   Server sends HandoffToGame (type 0x0E): [uint32 IP][uint32 port][uint32 token][uint32 uid]
            // ══════════════════════════════════════════════════════════════

            try
            {
                _tcpClient.NoDelay = true;
                Debug.LogError($"[QUEUE] Connection ready, starting protocol...");
            }
            catch (Exception ex) { Debug.LogError($"[QUEUE] Socket config error: {ex.Message}"); }

            // ─── LAYER 1: Transport crypto handshake ───
            // TTD VERIFIED: Client transport goes state 0→4 on connect.
            // State 4 WAITS for server crypto data. SERVER MUST SEND FIRST!
            // Format: [uint32 keyExchangeSize][uint32 pubKeySize][uint32 secKeySize][keyExchData...]
            // State 4 handler checks data > 15 bytes → our old 12 zeros were rejected!
            Debug.LogError($"[QUEUE] LAYER 1: Sending crypto init (server sends first)...");
            {
                // TTD VERIFIED: state 4 checks pubKeySize > 0 at 0x78AF0B
                // AND secKeySize > 0 at 0x78AF2B — "bad prime" when secKeySz=0!
                var payload = new ByteWriter();
                payload.WriteUInt32(8);     // keyExchangeSize = 8
                payload.WriteUInt32(8);     // publicKeySize = 8
                payload.WriteUInt32(8);     // secretKeySize = 8 (MUST BE > 0!)
                // 8 bytes server key exchange data
                payload.WriteByte(0x53); payload.WriteByte(0x52);
                payload.WriteByte(0x56); payload.WriteByte(0x4B);
                payload.WriteByte(0x45); payload.WriteByte(0x59);
                payload.WriteByte(0x30); payload.WriteByte(0x31);
                // 8 bytes server public key data
                payload.WriteByte(0x50); payload.WriteByte(0x55);
                payload.WriteByte(0x42); payload.WriteByte(0x4B);
                payload.WriteByte(0x45); payload.WriteByte(0x59);
                payload.WriteByte(0x53); payload.WriteByte(0x56);
                // 8 bytes server secret key data
                payload.WriteByte(0x53); payload.WriteByte(0x45);
                payload.WriteByte(0x43); payload.WriteByte(0x4B);
                payload.WriteByte(0x45); payload.WriteByte(0x59);
                payload.WriteByte(0x53); payload.WriteByte(0x56);
                byte[] payloadBytes = payload.ToArray(); // 36 bytes

                // Length-prefixed frame: [uint32 payloadLen][payload]
                var frame = new ByteWriter();
                frame.WriteUInt32((uint)payloadBytes.Length); // 20
                for (int i = 0; i < payloadBytes.Length; i++)
                    frame.WriteByte(payloadBytes[i]);
                byte[] frameBytes = frame.ToArray(); // 40 bytes total (4 len + 36 payload)

                _stream.Write(frameBytes, 0, frameBytes.Length);
                _stream.Flush();
                Debug.LogError($"[QUEUE-TX] Crypto init ({frameBytes.Length}b): {BitConverter.ToString(frameBytes)}");
            }

            // Wait for client's crypto response
            float elapsed = 0f;
            bool gotCrypto = false;
            while (elapsed < 10f && !gotCrypto)
            {
                bool hasData = false;
                try { hasData = _stream != null && _tcpClient.Connected && _stream.DataAvailable; } catch { }

                if (hasData)
                {
                    int bytesRead = 0;
                    try { bytesRead = _stream.Read(buffer, 0, buffer.Length); } catch { }

                    if (bytesRead > 0)
                    {
                        byte[] raw = new byte[bytesRead];
                        Array.Copy(buffer, raw, bytesRead);
                        Debug.LogError($"[QUEUE-RX] Crypto response: {bytesRead}b: {BitConverter.ToString(raw)}");
                        Debug.LogError($"[QUEUE] Crypto exchange complete");

                        // State 5 sends client's crypto response back, transitions to state 6.
                        // State 6 waits for "ENC OK" confirmation (6 bytes, length-prefixed).
                        // TTD: state 0→4→5→6, stuck at 6 waiting for this.
                        yield return new UnityEngine.WaitForSeconds(0.1f);
                        {
                            byte[] encOk = System.Text.Encoding.ASCII.GetBytes("ENC OK");
                            var confirmFrame = new ByteWriter();
                            confirmFrame.WriteUInt32((uint)encOk.Length); // 6
                            for (int i = 0; i < encOk.Length; i++)
                                confirmFrame.WriteByte(encOk[i]);
                            byte[] confirmBytes = confirmFrame.ToArray(); // 10 bytes
                            _stream.Write(confirmBytes, 0, confirmBytes.Length);
                            _stream.Flush();
                            Debug.LogError($"[QUEUE-TX] ENC OK ({confirmBytes.Length}b): {BitConverter.ToString(confirmBytes)}");
                        }

                        gotCrypto = true;
                    }
                }

                if (!gotCrypto)
                {
                    if ((int)(elapsed * 10) % 50 == 0 && elapsed > 0)
                        Debug.LogError($"[QUEUE] Still waiting for crypto response... {elapsed:F0}s");
                    elapsed += 0.05f;
                    yield return new UnityEngine.WaitForSeconds(0.05f);
                }
            }

            if (!gotCrypto)
            {
                Debug.LogError($"[QUEUE] TIMEOUT waiting for crypto response");
                yield break;
            }

            // ─── LAYER 2: Queue messages (length-prefixed, NO THCSockets!) ───
            // TTD VERIFIED: After "ENC OK", client sends queue login directly as:
            //   [uint32 frameLen=9][byte type=0x07][uint32 token][uint32 uid]
            // NO 0x03/0x04 THCSockets! All msgs use [uint32 len][payload] framing.

            Debug.LogError($"[QUEUE] LAYER 2: Waiting for queue login...");
            elapsed = 0f;
            bool gotLogin = false;
            while (elapsed < 10f && !gotLogin)
            {
                bool hasData = false;
                try { hasData = _stream != null && _tcpClient.Connected && _stream.DataAvailable; } catch { }

                if (hasData)
                {
                    int bytesRead = 0;
                    try { bytesRead = _stream.Read(buffer, 0, buffer.Length); } catch { }

                    if (bytesRead > 0)
                    {
                        byte[] raw = new byte[bytesRead];
                        Array.Copy(buffer, raw, bytesRead);
                        Debug.LogError($"[QUEUE-RX] Login: {bytesRead}b: {BitConverter.ToString(raw)}");

                        if (bytesRead >= 5)
                        {
                            uint frameLen = BitConverter.ToUInt32(raw, 0);
                            byte msgType = raw[4];
                            Debug.LogError($"[QUEUE-RX] frameLen={frameLen} msgType=0x{msgType:X2}");
                            if (msgType == 0x07 && bytesRead >= 13)
                            {
                                uint token = BitConverter.ToUInt32(raw, 5);
                                uint uid = BitConverter.ToUInt32(raw, 9);
                                Debug.LogError($"[QUEUE-RX] Queue login OK: token=0x{token:X8} uid=0x{uid:X8}");
                            }
                        }
                        gotLogin = true;
                    }
                }

                if (!gotLogin)
                {
                    elapsed += 0.05f;
                    yield return new UnityEngine.WaitForSeconds(0.05f);
                }
            }

            if (!gotLogin)
                Debug.LogError($"[QUEUE] No login received — continuing");

            // ═══ QUEUE WAITING LOOP (binary-verified: server controls timing) ═══
            // Client displays position from PositionInQueue(0x0D) and waits for HandoffToGame(0x0E)
            // RecvPositionInQueueMsg format: Server(%u) Level(%u) Size(%u) — Size = our position value
            yield return new UnityEngine.WaitForSeconds(0.1f);
            float waitTime = 0f;
            while (true)
            {
                // Check if server has capacity
                if (QueueConnectionBridge.HasCapacity)
                {
                    // Reserve slot IMMEDIATELY so next player sees correct count
                    QueueConnectionBridge.PlayerConnected();
                    SendQueuePosition(1, 0);
                    Debug.LogError($"[QUEUE] Server has capacity ({QueueConnectionBridge.CurrentPlayers}/{QueueConnectionBridge.MaxPlayers}) — handing off {_username}");
                    break;
                }

                // Server full — position is 1 (single-queue, not tracking multiple yet)
                SendQueuePosition(1, (uint)(waitTime * 1000));
                Debug.LogError($"[QUEUE] Server full ({QueueConnectionBridge.CurrentPlayers}/{QueueConnectionBridge.MaxPlayers}) — {_username} waiting ({waitTime:F0}s)");

                // Wait 2 seconds then re-check (fast enough to catch disconnect)
                for (int w = 0; w < 20; w++)
                {
                    yield return new UnityEngine.WaitForSeconds(0.1f);
                    waitTime += 0.1f;

                    // Check if connection dropped
                    try { if (!_tcpClient.Connected) yield break; } catch { yield break; }

                    // Re-check capacity every 100ms so player gets in fast when slot opens
                    if (QueueConnectionBridge.HasCapacity)
                        break;
                }
            }

            // Send HandoffToGame (type 0x0E): [uint32 IP][uint32 port][uint32 token][uint32 uid]
            yield return new UnityEngine.WaitForSeconds(0.3f);
            {
                // Flag the client IP so game server knows to do crypto handshake
                try
                {
                    var ep = (System.Net.IPEndPoint)_tcpClient.Client.RemoteEndPoint;
                    string clientIP = ep.Address.IsIPv4MappedToIPv6
                        ? ep.Address.MapToIPv4().ToString()
                        : ep.Address.ToString();
                    QueueConnectionBridge.ExpectQueueFromIP(clientIP, _username);
                    Debug.LogError($"[QUEUE] Flagged IP {clientIP} for queue user {_username}");
                }
                catch (Exception ex) { Debug.LogError($"[QUEUE] Flag IP error: {ex.Message}"); }

                string advertisedIP = string.IsNullOrWhiteSpace(_advertisedServerIP)
                    ? ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP)
                    : _advertisedServerIP;
                uint ipLE = IPToUInt32(advertisedIP);
                uint port = (uint)_config.gameServerPort;

                var inner = new ByteWriter();
                inner.WriteByte(0x0E);      // message type
                inner.WriteUInt32(ipLE);    // game server IP
                inner.WriteUInt32(port);    // game server port
                inner.WriteUInt32(_sessionToken);  // playToken — must match GlobalSessions
                Debug.LogError($"[QUEUE-TX] HandoffToGame token=0x{_sessionToken:X8}");
                inner.WriteUInt32(0);       // uid
                byte[] innerBytes = inner.ToArray();
                var frame = new ByteWriter();
                frame.WriteUInt32((uint)innerBytes.Length);
                for (int i = 0; i < innerBytes.Length; i++)
                    frame.WriteByte(innerBytes[i]);
                byte[] frameBytes = frame.ToArray();
                _stream.Write(frameBytes, 0, frameBytes.Length);
                _stream.Flush();
                Debug.LogError($"[QUEUE-TX] HandoffToGame ({frameBytes.Length}b): {BitConverter.ToString(frameBytes)} → {advertisedIP}:{_config.gameServerPort}");
            }

            // ═══════════════════════════════════════════════════════════════
            // POST-HANDOFF: Queue connection is done
            // ═══════════════════════════════════════════════════════════════
            // Binary-verified (Session 53): UserManagerClient::start @ 0x601B10
            // registers at GatewayClient's TChannelManager slot 3 (push 3 @ 0x601B71).
            // Social messages flow through the GAME TCP connection on CompressedA
            // channel 3, NOT through the queue connection.
            // The queue connection's only purpose is the waiting room + handoff.
            // ═══════════════════════════════════════════════════════════════

            // Brief wait for any trailing data, then close
            yield return new UnityEngine.WaitForSeconds(1.0f);
            try
            {
                if (_stream != null && _tcpClient.Connected && _stream.DataAvailable)
                {
                    int extra = _stream.Read(buffer, 0, buffer.Length);
                    if (extra > 0)
                    {
                        byte[] raw = new byte[extra];
                        Array.Copy(buffer, raw, extra);
                        Debug.LogError($"[QUEUE] Post-handoff trailing data ({extra}b): {BitConverter.ToString(raw, 0, Math.Min(extra, 40))}");
                    }
                }
            }
            catch (Exception ex) { Debug.LogError($"[QUEUE] Post-handoff read error: {ex.Message}"); }
            Debug.LogError($"[QUEUE] Queue connection complete for {_username}");
        }

        /// <summary>
        /// Send a message on the queue TCP connection using CompressedA (0x0A) framing.
        /// CRITICAL: messageType in the CompressedA header is what the queue dispatch
        /// at 0x61B640 reads to route messages. type==1 = ConnectOk, type!=1 = other.
        /// </summary>
        private void SendQueueCompressedA(byte queueMessageType, byte[] innerData)
        {
            try
            {
                byte[] compressed = DungeonRunners.Utilities.ZlibUtil.Deflate(innerData);

                var writer = new ByteWriter();
                writer.WriteByte(0x0A);                              // CompressedA type
                writer.WriteByte((byte)(_queuePeerId & 0xFF));
                writer.WriteByte((byte)((_queuePeerId >> 8) & 0xFF));
                writer.WriteByte((byte)((_queuePeerId >> 16) & 0xFF));
                writer.WriteUInt32((uint)(compressed.Length + 7));    // bodyLen
                writer.WriteByte(0x00);                              // channel
                writer.WriteByte(queueMessageType);                  // messageType — routing key!
                writer.WriteByte(0x00);                              // padding
                writer.WriteUInt32((uint)innerData.Length);           // uncompressed length
                for (int i = 0; i < compressed.Length; i++)
                    writer.WriteByte(compressed[i]);

                byte[] frame = writer.ToArray();
                Debug.LogError($"[QUEUE-TX] CompressedA msgType={queueMessageType} frame ({frame.Length}b): {BitConverter.ToString(frame)}");
                _stream.Write(frame, 0, frame.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QUEUE-TX] Error: {ex.Message}");
            }
        }



        private void WriteGoAuthMessage(byte serverMsgType, byte[] payload)
        {
            byte[] message = new byte[payload.Length + 1];
            message[0] = serverMsgType;
            Array.Copy(payload, 0, message, 1, payload.Length);

            int length = message.Length;
            int remainder = length % 8;
            if (remainder != 0)
            {
                int padding = 8 - remainder;
                byte[] padded = new byte[length + padding];
                Array.Copy(message, padded, length);
                message = padded;
                length += padding;
            }

            uint checksum = 0;
            for (int i = 0; i < length; i += 4)
            {
                uint value = (uint)(message[i] | (message[i + 1] << 8) | (message[i + 2] << 16) | (message[i + 3] << 24));
                checksum ^= value;
            }

            byte[] finalData = new byte[length + 8];
            Array.Copy(message, finalData, length);

            finalData[length] = (byte)(checksum & 0xFF);
            finalData[length + 1] = (byte)((checksum >> 8) & 0xFF);
            finalData[length + 2] = (byte)((checksum >> 16) & 0xFF);
            finalData[length + 3] = (byte)((checksum >> 24) & 0xFF);

            byte[] encrypted = EncryptGoBlowfishEndian(finalData, finalData.Length);

            int packetLength = encrypted.Length + 2;
            byte[] frame = new byte[packetLength];
            frame[0] = (byte)(packetLength & 0xFF);
            frame[1] = (byte)((packetLength >> 8) & 0xFF);
            Array.Copy(encrypted, 0, frame, 2, encrypted.Length);

            Debug.Log($"📤 Sent Go encrypted 0x{serverMsgType:X2}, frameLen={frame.Length}");
            _stream.Write(frame, 0, frame.Length);
        }

        private byte[] EncryptGoBlowfishEndian(byte[] data, int length)
        {
            try
            {
                int numBlocks = length / 8;
                byte[] encrypted = new byte[length];

                var blowfish = new BlowfishEncryption(_config.blowfishKey);

                for (int i = 0; i < numBlocks; i++)
                {
                    int start = i * 8;
                    byte[] block = new byte[8];
                    Array.Copy(data, start, block, 0, 8);

                    uint v1 = BitConverter.ToUInt32(block, 0);
                    uint v2 = BitConverter.ToUInt32(block, 4);

                    byte[] bigEndianBlock = new byte[8];
                    bigEndianBlock[0] = (byte)((v1 >> 24) & 0xFF);
                    bigEndianBlock[1] = (byte)((v1 >> 16) & 0xFF);
                    bigEndianBlock[2] = (byte)((v1 >> 8) & 0xFF);
                    bigEndianBlock[3] = (byte)(v1 & 0xFF);
                    bigEndianBlock[4] = (byte)((v2 >> 24) & 0xFF);
                    bigEndianBlock[5] = (byte)((v2 >> 16) & 0xFF);
                    bigEndianBlock[6] = (byte)((v2 >> 8) & 0xFF);
                    bigEndianBlock[7] = (byte)(v2 & 0xFF);

                    byte[] encryptedBlock = blowfish.Encrypt(bigEndianBlock);

                    uint ev1 = (uint)((encryptedBlock[0] << 24) | (encryptedBlock[1] << 16) | (encryptedBlock[2] << 8) | encryptedBlock[3]);
                    uint ev2 = (uint)((encryptedBlock[4] << 24) | (encryptedBlock[5] << 16) | (encryptedBlock[6] << 8) | encryptedBlock[7]);

                    byte[] littleEndianBlock = new byte[8];
                    littleEndianBlock[0] = (byte)(ev1 & 0xFF);
                    littleEndianBlock[1] = (byte)((ev1 >> 8) & 0xFF);
                    littleEndianBlock[2] = (byte)((ev1 >> 16) & 0xFF);
                    littleEndianBlock[3] = (byte)(ev1 >> 24);
                    littleEndianBlock[4] = (byte)(ev2 & 0xFF);
                    littleEndianBlock[5] = (byte)((ev2 >> 8) & 0xFF);
                    littleEndianBlock[6] = (byte)((ev2 >> 16) & 0xFF);
                    littleEndianBlock[7] = (byte)(ev2 >> 24);

                    Array.Copy(littleEndianBlock, 0, encrypted, start, 8);
                }

                return encrypted;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Encrypt error: {ex.Message}");
                return new byte[0];
            }
        }

        private (string user, string pass) DecodeLogin(byte[] block24, byte[] tail6)
        {
            var des = new DESEncryption(_config.desKey);
            byte[] outbuf = des.Decrypt(block24);

            byte[] all = new byte[24 + 6];
            Array.Copy(outbuf, 0, all, 0, Math.Min(outbuf.Length, 24));
            Array.Copy(tail6, 0, all, 24, 6);

            string user = Encoding.ASCII.GetString(all, 0, 14).TrimEnd('\0');
            string pass = Encoding.ASCII.GetString(all, 14, 16).TrimEnd('\0');
            return (user, pass);
        }

        private void SendQueuePosition(uint position, uint waitMs)
        {
            try
            {
                var inner = new ByteWriter();
                inner.WriteByte(0x0D);
                inner.WriteUInt32(position);
                inner.WriteUInt32(waitMs);
                byte[] innerBytes = inner.ToArray();
                var frame = new ByteWriter();
                frame.WriteUInt32((uint)innerBytes.Length);
                for (int i = 0; i < innerBytes.Length; i++)
                    frame.WriteByte(innerBytes[i]);
                byte[] frameBytes = frame.ToArray();
                _stream.Write(frameBytes, 0, frameBytes.Length);
                _stream.Flush();
                Debug.LogError($"[QUEUE-TX] PositionInQueue pos={position} wait={waitMs}ms");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QUEUE-TX] PositionInQueue error: {ex.Message}");
            }
        }

        private static uint IPToUInt32(string ip)
        {
            var p = ip.Split('.');
            return (uint)(byte.Parse(p[0]) | (byte.Parse(p[1]) << 8) | (byte.Parse(p[2]) << 16) | (byte.Parse(p[3]) << 24));
        }

        private static string ResolveAdvertisedServerIP(TcpClient client, string configuredIP)
        {
            string remoteIP = GetEndpointIP(client?.Client?.RemoteEndPoint);
            if (IsLoopbackIPv4(remoteIP))
                return "127.0.0.1";

            string configured = NormalizeIPv4(configuredIP);
            if (IsConcreteIPv4(configured))
            {
                if (!IsPrivateIPv4(configured) || IsAssignedLocalIPv4(configured))
                    return configured;
            }

            string localIP = GetEndpointIP(client?.Client?.LocalEndPoint);
            if (IsConcreteIPv4(localIP))
                return localIP;

            if (IsConcreteIPv4(configured))
                return configured;

            return "127.0.0.1";
        }

        private static string GetEndpointIP(System.Net.EndPoint endpoint)
        {
            try
            {
                var ep = endpoint as System.Net.IPEndPoint;
                if (ep == null)
                    return null;
                var address = ep.Address;
                if (address.IsIPv4MappedToIPv6)
                    address = address.MapToIPv4();
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return address.ToString();
                if (System.Net.IPAddress.IsLoopback(address))
                    return "127.0.0.1";
            }
            catch { }
            return null;
        }

        private static string NormalizeIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return null;
            if (!System.Net.IPAddress.TryParse(ip.Trim(), out var address))
                return null;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return null;
            return address.ToString();
        }

        private static bool IsConcreteIPv4(string ip)
        {
            return !string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0";
        }

        private static bool IsLoopbackIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;
            return ip == "127.0.0.1" || ip.StartsWith("127.", StringComparison.Ordinal);
        }

        private static bool IsPrivateIPv4(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip, out var address))
                return false;
            byte[] b = address.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        private static bool IsAssignedLocalIPv4(string ip)
        {
            try
            {
                foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    var a = address;
                    if (a.IsIPv4MappedToIPv6)
                        a = a.MapToIPv4();
                    if (a.AddressFamily == AddressFamily.InterNetwork && string.Equals(a.ToString(), ip, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            return IsLoopbackIPv4(ip);
        }
    }
}
