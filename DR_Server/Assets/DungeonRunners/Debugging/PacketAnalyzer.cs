using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DungeonRunners.Debugging
{
    /// <summary>
    /// Packet analyzer for comparing our server packets with expected formats
    /// </summary>
    public class PacketAnalyzer : MonoBehaviour
    {
        [Header("Analysis Settings")]
        [SerializeField] private bool enableDetailedLogging = true;
        [SerializeField] private bool savePacketDumps = true;

        private List<PacketRecord> _packetLog = new List<PacketRecord>();
        private string _logFilePath = "packet_analysis.log";

        void Start()
        {
            Debug.Log("🔬 PacketAnalyzer Started - Analyzing protocol compatibility");

            if (savePacketDumps)
            {
                ClearLogFile();
            }
        }

        /// <summary>
        /// Analyze a PlayOk packet for potential issues
        /// </summary>
        public void AnalyzePlayOkPacket(byte[] packetData, uint playToken, byte serverId)
        {
            Debug.Log($"🔬 Analyzing PlayOk packet - Token: 0x{playToken:X8}, ServerId: {serverId}");

            var analysis = new PacketAnalysis
            {
                PacketType = "PlayOk",
                Timestamp = DateTime.Now,
                RawData = packetData,
                PlayToken = playToken,
                ServerId = serverId
            };

            // Check packet structure
            AnalyzePacketStructure(analysis);

            // Check for common issues
            CheckPlayOkIssues(analysis);

            // Compare with expected format
            CompareWithExpectedPlayOk(analysis);

            // Log results
            LogAnalysisResults(analysis);

            // Save to file
            if (savePacketDumps)
            {
                SavePacketAnalysis(analysis);
            }
        }

        /// <summary>
        /// Analyze server list packet for format issues
        /// </summary>
        public void AnalyzeServerListPacket(byte[] packetData, string serverIP, ushort serverPort)
        {
            Debug.Log($"🔬 Analyzing ServerList packet - IP: {serverIP}, Port: {serverPort}");

            var analysis = new PacketAnalysis
            {
                PacketType = "ServerList",
                Timestamp = DateTime.Now,
                RawData = packetData,
                ServerIP = serverIP,
                ServerPort = serverPort
            };

            AnalyzePacketStructure(analysis);
            CheckServerListIssues(analysis);
            CompareWithExpectedServerList(analysis);
            LogAnalysisResults(analysis);

            if (savePacketDumps)
            {
                SavePacketAnalysis(analysis);
            }
        }

        private void AnalyzePacketStructure(PacketAnalysis analysis)
        {
            var data = analysis.RawData;

            // Basic structure analysis
            analysis.PacketLength = data.Length;
            analysis.HexDump = BitConverter.ToString(data).Replace("-", " ");

            // Look for common patterns
            analysis.HasValidHeader = data.Length >= 2;
            analysis.IsMultipleOf8 = (data.Length % 8) == 0;

            if (analysis.PacketType == "PlayOk")
            {
                // PlayOk should have specific structure
                analysis.HasPlayToken = data.Length >= 4;
                analysis.HasServerId = data.Length >= 5;
            }
            else if (analysis.PacketType == "ServerList")
            {
                // Server list should have multiple server entries
                analysis.ServerCount = data.Length >= 2 ? (byte)data[0] : (byte)0;
            }
        }

        private void CheckPlayOkIssues(PacketAnalysis analysis)
        {
            var issues = new List<string>();

            // Check 1: Packet size
            if (analysis.RawData.Length < 5)
            {
                issues.Add("PlayOk packet too small - minimum 5 bytes expected");
            }

            // Check 2: Play token format
            if (analysis.PlayToken == 0)
            {
                issues.Add("PlayToken is 0 - client may reject");
            }

            // Check 3: Server ID range
            if (analysis.ServerId > 3)
            {
                issues.Add($"ServerId {analysis.ServerId} outside expected range (0-3)");
            }

            // Check 4: Alignment
            if (!analysis.IsMultipleOf8)
            {
                issues.Add("Packet not 8-byte aligned - may cause decryption issues");
            }

            // Check 5: Check for common byte order issues
            if (analysis.RawData.Length >= 4)
            {
                uint tokenFromPacket = BitConverter.ToUInt32(analysis.RawData, 0);
                if (tokenFromPacket != analysis.PlayToken)
                {
                    issues.Add($"Token mismatch: Expected 0x{analysis.PlayToken:X8}, Found 0x{tokenFromPacket:X8}");
                }
            }

            analysis.Issues = issues;
        }

        private void CheckServerListIssues(PacketAnalysis analysis)
        {
            var issues = new List<string>();

            // Check 1: Server count
            if (analysis.ServerCount < 1 || analysis.ServerCount > 10)
            {
                issues.Add($"Unusual server count: {analysis.ServerCount}");
            }

            // Check 2: Minimum packet size
            int expectedMinSize = 2 + (analysis.ServerCount * 13); // 13 bytes per server entry
            if (analysis.RawData.Length < expectedMinSize)
            {
                issues.Add($"ServerList too small: expected {expectedMinSize}, got {analysis.RawData.Length}");
            }

            // Check 3: Server entry structure
            if (analysis.RawData.Length >= 2 + 13) // At least one server entry
            {
                // Check server ID sequence
                for (int i = 0; i < Math.Min((int)analysis.ServerCount, 4); i++)
                {
                    int offset = 2 + (i * 13);
                    if (offset + 1 < analysis.RawData.Length)
                    {
                        byte serverId = analysis.RawData[offset + 1]; // Usually second byte after count
                        if (serverId != i)
                        {
                            issues.Add($"Server ID mismatch at index {i}: expected {i}, got {serverId}");
                        }
                    }
                }
            }

            // Check 4: IP encoding
            try
            {
                uint ipInt = BitConverter.ToUInt32(analysis.RawData, 3); // Approximate position
                string ipFromPacket = $"{ipInt & 0xFF}.{(ipInt >> 8) & 0xFF}.{(ipInt >> 16) & 0xFF}.{(ipInt >> 24) & 0xFF}";
                if (ipFromPacket != analysis.ServerIP)
                {
                    issues.Add($"IP encoding mismatch: expected {analysis.ServerIP}, decoded {ipFromPacket}");
                }
            }
            catch
            {
                issues.Add("Failed to decode IP from packet");
            }

            analysis.Issues = issues;
        }

        private void CompareWithExpectedPlayOk(PacketAnalysis analysis)
        {
            var recommendations = new List<string>();

            // Based on Go server analysis, these are the expected patterns
            recommendations.Add("✅ PlayOk should be 9 bytes (4-byte token + 4-byte unknown + 1-byte serverId)");
            recommendations.Add("✅ Token should be non-zero and unique");
            recommendations.Add("✅ ServerId should match selected server (0-3)");
            recommendations.Add("✅ Second 4-byte value usually 0x5678DEFA in Go server");

            // Check if we match the pattern
            if (analysis.RawData.Length == 9)
            {
                recommendations.Add("✅ Packet size matches Go server");
            }
            else
            {
                recommendations.Add($"❌ Packet size {analysis.RawData.Length} doesn't match Go server (9 bytes)");
            }

            if (analysis.RawData.Length >= 8)
            {
                uint secondValue = BitConverter.ToUInt32(analysis.RawData, 4);
                if (secondValue == 0x5678DEFA)
                {
                    recommendations.Add("✅ Second dword matches Go server pattern");
                }
                else
                {
                    recommendations.Add($"❌ Second dword 0x{secondValue:X8} doesn't match Go server (0x5678DEFA)");
                }
            }

            analysis.Recommendations = recommendations;
        }

        private void CompareWithExpectedServerList(PacketAnalysis analysis)
        {
            var recommendations = new List<string>();

            recommendations.Add("✅ ServerList should start with count byte");
            recommendations.Add("✅ Each server entry should be 13 bytes");
            recommendations.Add("✅ Server IDs should be sequential (0,1,2,3)");
            recommendations.Add("✅ IP should be 32-bit integer in little-endian");
            recommendations.Add("✅ Port should be 32-bit integer (not 16-bit)");

            // Verify structure
            if (analysis.ServerCount == 4)
            {
                recommendations.Add("✅ Server count matches Go server");
            }
            else
            {
                recommendations.Add($"⚠️ Server count {analysis.ServerCount} differs from Go server (4)");
            }

            analysis.Recommendations = recommendations;
        }

        private void LogAnalysisResults(PacketAnalysis analysis)
        {
            Debug.Log($"🔬 {analysis.PacketType} Analysis Results:");
            Debug.Log($"   Length: {analysis.PacketLength} bytes");
            Debug.Log($"   Hex: {analysis.HexDump}");

            if (analysis.Issues.Count > 0)
            {
                Debug.LogWarning($"   ⚠️ Issues Found:");
                foreach (var issue in analysis.Issues)
                {
                    Debug.LogWarning($"      - {issue}");
                }
            }
            else
            {
                Debug.Log($"   ✅ No structural issues detected");
            }

            if (enableDetailedLogging && analysis.Recommendations.Count > 0)
            {
                Debug.Log($"   📋 Recommendations:");
                foreach (var rec in analysis.Recommendations)
                {
                    Debug.Log($"      {rec}");
                }
            }
        }

        private void SavePacketAnalysis(PacketAnalysis analysis)
        {
            try
            {
                using (var writer = new StreamWriter(_logFilePath, true))
                {
                    writer.WriteLine($"=== {analysis.PacketType} Analysis - {analysis.Timestamp:yyyy-MM-dd HH:mm:ss.fff} ===");
                    writer.WriteLine($"Length: {analysis.PacketLength} bytes");
                    writer.WriteLine($"Hex: {analysis.HexDump}");

                    if (analysis.Issues.Count > 0)
                    {
                        writer.WriteLine("Issues:");
                        foreach (var issue in analysis.Issues)
                        {
                            writer.WriteLine($"  - {issue}");
                        }
                    }

                    if (analysis.Recommendations.Count > 0)
                    {
                        writer.WriteLine("Recommendations:");
                        foreach (var rec in analysis.Recommendations)
                        {
                            writer.WriteLine($"  {rec}");
                        }
                    }

                    writer.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to save packet analysis: {ex.Message}");
            }
        }

        private void ClearLogFile()
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to clear log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a comprehensive report for debugging
        /// </summary>
        public void GenerateDebugReport()
        {
            Debug.Log("📊 Generating comprehensive debug report...");

            string reportPath = $"debug_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            try
            {
                using (var writer = new StreamWriter(reportPath))
                {
                    writer.WriteLine("DUNGEON RUNNERS CONNECTION DEBUG REPORT");
                    writer.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine();

                    writer.WriteLine("PACKET ANALYSIS SUMMARY:");
                    writer.WriteLine("========================");

                    foreach (var record in _packetLog)
                    {
                        writer.WriteLine($"Type: {record.PacketType}");
                        writer.WriteLine($"Time: {record.Timestamp:HH:mm:ss.fff}");
                        writer.WriteLine($"Size: {record.Data.Length} bytes");
                        writer.WriteLine($"Hex: {BitConverter.ToString(record.Data).Replace("-", " ")}");
                        writer.WriteLine();
                    }

                    writer.WriteLine("DEBUGGING RECOMMENDATIONS:");
                    writer.WriteLine("=========================");
                    writer.WriteLine("1. Check x32dbg logs for connect() calls after PlayOk");
                    writer.WriteLine("2. Verify PlayOk packet format matches Go server exactly");
                    writer.WriteLine("3. Confirm server list IP/port encoding is correct");
                    writer.WriteLine("4. Look for client-side validation failures");
                    writer.WriteLine("5. Monitor for additional authentication requirements");
                }

                Debug.Log($"📊 Debug report saved to: {reportPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to generate debug report: {ex.Message}");
            }
        }
    }

    [System.Serializable]
    public class PacketAnalysis
    {
        public string PacketType;
        public DateTime Timestamp;
        public byte[] RawData;
        public uint PlayToken;
        public byte ServerId;
        public string ServerIP;
        public ushort ServerPort;

        // Analysis results
        public int PacketLength;
        public string HexDump;
        public bool HasValidHeader;
        public bool IsMultipleOf8;
        public bool HasPlayToken;
        public bool HasServerId;
        public byte ServerCount;
        public List<string> Issues = new List<string>();
        public List<string> Recommendations = new List<string>();
    }

    [System.Serializable]
    public class PacketRecord
    {
        public string PacketType;
        public DateTime Timestamp;
        public byte[] Data;
        public string Description;
    }
}