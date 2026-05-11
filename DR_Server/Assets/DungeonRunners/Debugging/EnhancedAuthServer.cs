using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using DungeonRunners.Core;
using DungeonRunners.Utilities;
using DungeonRunners.Managers;
using DungeonRunners.Debugging;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// Enhanced Authentication server with comprehensive debugging for x32dbg analysis
    /// </summary>
    public class EnhancedAuthServer : MonoBehaviour
    {
        [SerializeField] private ServerConfig config;
        [SerializeField] private ConnectionTracer connectionTracer;

        private UnityTcpListener _listener;
        private List<EnhancedAuthConnection> _connections = new List<EnhancedAuthConnection>();

        void Start()
        {
            if (config == null)
            {
                Debug.LogError("ServerConfig not assigned!");
                return;
            }

            // Find connection tracer if not assigned
            if (connectionTracer == null)
            {
                connectionTracer = FindObjectOfType<ConnectionTracer>();
            }

            // Start listener
            _listener = new UnityTcpListener();
            _listener.OnClientConnected += OnClientConnected;
            _listener.Start(config.authServerIP, config.authServerPort);

            Debug.Log($"🚀 Enhanced Auth Server started on {config.authServerIP}:{config.authServerPort}");
            
            if (connectionTracer != null)
            {
                connectionTracer.LogAuthFlow("AUTH_START", $"Server listening on {config.authServerIP}:{config.authServerPort}");
            }
        }

        void OnDestroy()
        {
            _listener?.Stop();
            foreach (var conn in _connections)
            {
                conn.Disconnect();
            }
            _connections.Clear();
        }

        private void OnClientConnected(TcpClient client)
        {
            string clientEndpoint = client.Client.RemoteEndPoint.ToString();
            Debug.Log($"🔗 AUTH CLIENT CONNECTED: {clientEndpoint}");
            
            if (connectionTracer != null)
            {
                connectionTracer.LogForX32dbg("AUTH_CLIENT_CONNECT", clientEndpoint);
            }

            var connection = new EnhancedAuthConnection(client, config, connectionTracer);
            connection.OnDisconnected += () => OnConnectionDisconnected(connection);
            connection.StartReceiving(this);

            _connections.Add(connection);
        }

        private void OnConnectionDisconnected(EnhancedAuthConnection connection)
        {
            Debug.Log("🔌 Auth client disconnected");
            if (connectionTracer != null)
            {
                connectionTracer.LogForX32dbg("AUTH_CLIENT_DISCONNECT", "Client disconnected from auth server");
            }
            _connections.Remove(connection);
        }
    }

    public class EnhancedAuthConnection
    {
        private ClientConnection _client;
        private ServerConfig _config;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private string _username;
        private ConnectionTracer _tracer;

        public event Action OnDisconnected;

        public EnhancedAuthConnection(TcpClient client, ServerConfig config, ConnectionTracer tracer)
        {
            _tcpClient = client;
            _stream = client.GetStream();
            _config = config;
            _client = new ClientConnection(client);
            _tracer = tracer;

            _client.OnDisconnected += () => OnDisconnected?.Invoke();
        }

        public void StartReceiving(MonoBehaviour coroutineRunner)
        {
            SendGoWelcomePacket();
            coroutineRunner.StartCoroutine(ReceiveLoop());
        }

        public void Disconnect()
        {
            _client.Disconnect();
        }

        private void SendGoWelcomePacket()
        {
            byte[] welcomePacket = new byte[] { 3, 0, 0 };
            Debug.Log($"📤 SENT Go welcome packet: [3, 0, 0]");
            
            if (_tracer != null)
            {
                _tracer.LogPacket("SEND", welcomePacket, "Welcome Packet");
            }
            
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
                        Debug.Log($"📨 RECEIVED message type: 0x{msgType:X2}, bodyLen: {plainBody.Length - 1}");
                        
                        if (_tracer != null)
                        {
                            _tracer.LogPacket("RECV", plainBody, $"Auth Msg 0x{msgType:X2}");
                        }

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
                        if (_tracer != null)
                        {
                            _tracer.LogForX32dbg("UNKNOWN_MSG", $"0x{msgType:X2}");
                        }
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
                if (_tracer != null)
                {
                    _tracer.LogAuthFlow("LOGIN_START", "Processing login request");
                }

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

                Debug.Log($"🔐 Login attempt: {username}");
                
                if (_tracer != null)
                {
                    _tracer.LogForX32dbg("LOGIN_ATTEMPT", $"User:{username}");
                }

                // Store the username for later use
                _username = username;

                var session = SessionManager.Instance.CreateSession(username, password);
                uint accountId;
                if (session != null)
                {
                    accountId = session.AccountId;
                }
                else
                {
                    accountId = (uint)Math.Abs(username.GetHashCode() % 1000000);
                }

                SendGoLoginOk(accountId);
                SendGoServerList();
                Debug.Log($"📋 ServerListEx sent to {_config.gameServerIP}:{_config.gameServerPort}");
                
                if (_tracer != null)
                {
                    _tracer.LogAuthFlow("SERVER_LIST_SENT", $"1 server at {_config.gameServerIP}:{_config.gameServerPort}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Login error: {ex}");
            }
        }

        private void HandleAboutToPlay(byte[] data)
        {
            if (_tracer != null)
            {
                _tracer.LogAuthFlow("ABOUT_TO_PLAY", "Client requesting play token");
            }

            var reader = new ByteReader(data);
            reader.ReadByte();

            uint lo = reader.ReadUInt32();
            uint hi = reader.ReadUInt32();
            byte serverId = reader.ReadByte();

            Debug.Log($"🎮 AboutToPlay: lo=0x{lo:X8}, hi=0x{hi:X8}, serverId={serverId}");
            
            if (_tracer != null)
            {
                _tracer.LogForX32dbg("ABOUT_TO_PLAY", $"lo:0x{lo:X8} hi:0x{hi:X8} serverId:{serverId}");
            }

            uint playToken = (uint)DateTime.Now.Ticks ^ 0x12345678;
            // Use the actual stored username
            SessionManager.Instance.SetPlayToken(playToken, _username ?? "unknown");

            SendGoPlayOk(playToken, serverId);
            
            // CRITICAL: Notify tracer that PlayOk was sent
            if (_tracer != null)
            {
                _tracer.OnPlayOkSent(playToken, serverId);
            }
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

            WriteGoAuthMessage(0x03, writer.ToArray());
            Debug.Log($"✅ LoginOk sent, accountId={accountId}");
            
            if (_tracer != null)
            {
                _tracer.LogAuthFlow("LOGIN_OK", $"AccountId:{accountId}");
            }
        }

        private void SendGoServerList()
        {
            uint ipInt = IPToUInt32(_config.gameServerIP);

            var writer = new ByteWriter();
            byte serverId = 0x01;
            writer.WriteByte(0x01);
            writer.WriteByte(serverId);

            writer.WriteByte(serverId);
            writer.WriteUInt32(ipInt);
            writer.WriteUInt32((uint)_config.gameServerPort);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt16(0x0000);
            writer.WriteUInt16(1000);
            writer.WriteByte(0x01);

            WriteGoAuthMessage(0x04, writer.ToArray());
            Debug.Log($"📋 Server list sent with 1 server (ID 1)");
            
            if (_tracer != null)
            {
                _tracer.LogAuthFlow("SERVER_LIST", $"1 server at {_config.gameServerIP}:{_config.gameServerPort}");
            }
        }

        private void SendGoPlayOk(uint playToken, byte serverId)
        {
            var writer = new ByteWriter();
            writer.WriteUInt32(playToken);
            writer.WriteUInt32(0x5678DEFA);
            writer.WriteByte(serverId);

            WriteGoAuthMessage(0x07, writer.ToArray());
            Debug.Log($"🎮 PlayOk sent, token=0x{playToken:X8}, serverId={serverId}");
            
            if (_tracer != null)
            {
                _tracer.LogAuthFlow("PLAY_OK", $"Token:0x{playToken:X8} ServerId:{serverId}");
                _tracer.LogForX32dbg("PLAY_OK_DETAILS", $"Token:0x{playToken:X8} ServerId:{serverId} IP:{_config.gameServerIP} Port:{_config.gameServerPort}");
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
            
            if (_tracer != null)
            {
                _tracer.LogPacket("SEND", frame, $"Go Encrypted 0x{serverMsgType:X2}");
            }
            
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

        private static uint IPToUInt32(string ip)
        {
            var p = ip.Split('.');
            return (uint)(byte.Parse(p[0]) | (byte.Parse(p[1]) << 8) | (byte.Parse(p[2]) << 16) | (byte.Parse(p[3]) << 24));
        }
    }
}
