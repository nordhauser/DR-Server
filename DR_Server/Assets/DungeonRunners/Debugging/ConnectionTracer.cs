using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DungeonRunners.Debugging
{
    /// <summary>
    /// Advanced connection tracer for debugging client-server communication
    /// </summary>
    public class ConnectionTracer : MonoBehaviour
    {
        [Header("Network Monitoring")]
        [SerializeField] private bool enablePacketLogging = true;
        [SerializeField] private bool enableConnectionMonitoring = true;
        
        [Header("x32dbg Integration")]
        [SerializeField] private bool logForX32dbg = true;
        [SerializeField] private string clientProcessName = "DungeonRunners";
        
        private TcpListener _monitorListener;
        private bool _isMonitoring = false;
        
        void Start()
        {
            Debug.Log("🔍 ConnectionTracer Started - Monitoring network activity");
            
            if (enableConnectionMonitoring)
            {
                StartConnectionMonitoring();
            }
            
            if (logForX32dbg)
            {
                SetupX32dbgLogging();
            }
        }
        
        void OnDestroy()
        {
            StopConnectionMonitoring();
        }
        
        private void StartConnectionMonitoring()
        {
            try
            {
                // Monitor for any connection attempts to our game server port
                _monitorListener = new TcpListener(IPAddress.Any, 2604); // Port next to game server
                _monitorListener.Start();
                _isMonitoring = true;
                
                Debug.Log("🔍 Connection monitoring active on port 2604");
                Debug.Log("📊 x32dbg: Set breakpoints on connect() and WSAConnect()");
                
                StartCoroutine(MonitorConnections());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start connection monitoring: {ex.Message}");
            }
        }
        
        private void StopConnectionMonitoring()
        {
            _isMonitoring = false;
            _monitorListener?.Stop();
        }
        
        private IEnumerator MonitorConnections()
        {
            while (_isMonitoring)
            {
                if (_monitorListener.Pending())
                {
                    var client = _monitorListener.AcceptTcpClient();
                    Debug.Log($"🔍 DETECTED CONNECTION: {client.Client.RemoteEndPoint}");
                    
                    // This would be unexpected - log for analysis
                    LogForX32dbg("UNEXPECTED_CONNECTION", client.Client.RemoteEndPoint.ToString());
                }
                
                yield return new WaitForSeconds(0.1f);
            }
        }
        
        private void SetupX32dbgLogging()
        {
            Debug.Log("🛠️ x32dbg Integration Setup:");
            Debug.Log("1. Set breakpoints on: connect(), WSAConnect(), socket()");
            Debug.Log("2. Monitor function calls after PlayOk receipt");
            Debug.Log("3. Look for port 2603 connection attempts");
            Debug.Log("4. Check server list validation logic");
            
            // Log current server configuration for x32dbg reference
            var config = FindObjectOfType<DungeonRunners.Core.ServerConfig>();
            if (config != null)
            {
                LogForX32dbg("SERVER_CONFIG", $"Auth:{config.authServerIP}:{config.authServerPort} Game:{config.gameServerIP}:{config.gameServerPort}");
            }
        }
        
        public void LogForX32dbg(string eventType, string data)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logMessage = $"[x32dbg:{timestamp}] {eventType}: {data}";
            
            Debug.Log(logMessage);
            
            // Also write to file for x32dbg analysis
            System.IO.File.AppendAllText("connection_trace.log", $"{logMessage}\n");
        }
        
        public void LogPacket(string direction, byte[] data, string context)
        {
            if (!enablePacketLogging) return;
            
            string hex = BitConverter.ToString(data).Replace("-", " ");
            string logMessage = $"[PKT:{direction}] {context} ({data.Length} bytes): {hex}";
            
            Debug.Log(logMessage);
            LogForX32dbg("PACKET", $"{direction}:{context}:{data.Length}:{hex}");
        }
        
        public void LogAuthFlow(string step, string details)
        {
            string logMessage = $"[AUTH:{step}] {details}";
            Debug.Log(logMessage);
            LogForX32dbg("AUTH_FLOW", $"{step}:{details}");
        }
        
        /// <summary>
        /// Call this when PlayOk is sent to trace client behavior
        /// </summary>
        public void OnPlayOkSent(uint playToken, byte serverId)
        {
            LogAuthFlow("PLAYOK_SENT", $"Token:0x{playToken:X8} ServerId:{serverId}");
            LogForX32dbg("PLAYOK_SENT", $"Waiting for client connection to port 2603...");
            
            // Start monitoring for connection attempts
            StartCoroutine(MonitorForGameServerConnection());
        }
        
        private IEnumerator MonitorForGameServerConnection()
        {
            Debug.Log("⏱️ Monitoring for game server connection (30s timeout)...");
            
            for (int i = 0; i < 300; i++) // 30 seconds
            {
                // Check if any game server connections have been established
                var gameServer = FindObjectOfType<DungeonRunners.Networking.UnityGameServer>();
                if (gameServer != null)
                {
                    // This would need to be implemented in GameServer to expose connection count
                    Debug.Log($"🔍 Checking game server connections...");
                }
                
                yield return new WaitForSeconds(0.1f);
            }
            
            LogForX32dbg("CONNECTION_TIMEOUT", "Client did not connect to game server within 30 seconds");
            Debug.LogWarning("⚠️ Client failed to connect to game server after PlayOk - check x32dbg logs");
        }
    }
}