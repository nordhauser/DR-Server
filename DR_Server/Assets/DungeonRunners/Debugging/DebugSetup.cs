using UnityEngine;

namespace DungeonRunners.Debugging
{
    /// <summary>
    /// Automatic setup script for debugging tools
    /// Add this to your main server scene to enable comprehensive debugging
    /// </summary>
    public class DebugSetup : MonoBehaviour
    {
        [Header("Debug Components")]
        [SerializeField] private bool enableConnectionTracer = true;
        [SerializeField] private bool enablePacketAnalyzer = true;
        [SerializeField] private bool useEnhancedAuthServer = true;
        
        [Header("Configuration")]
        [SerializeField] private bool startDebuggingOnAwake = true;
        
        void Awake()
        {
            if (startDebuggingOnAwake)
            {
                SetupDebugging();
            }
        }

        void Start()
        {
            Debug.Log("🛠️ Dungeon Runners Debug Setup Started");
            Debug.Log("📋 This script will automatically configure all debugging tools");
            Debug.Log("🎯 Target: Identify why client doesn't connect to game server after PlayOk");
        }

        public void SetupDebugging()
        {
            // Setup ConnectionTracer
            if (enableConnectionTracer)
            {
                SetupConnectionTracer();
            }

            // Setup PacketAnalyzer
            if (enablePacketAnalyzer)
            {
                SetupPacketAnalyzer();
            }

            // Replace AuthServer with Enhanced version
            if (useEnhancedAuthServer)
            {
                SetupEnhancedAuthServer();
            }

            Debug.Log("✅ Debug setup complete! Ready for x32dbg analysis.");
            PrintDebugInstructions();
        }

        private void SetupConnectionTracer()
        {
            var tracer = FindObjectOfType<ConnectionTracer>();
            if (tracer == null)
            {
                GameObject tracerObj = new GameObject("ConnectionTracer");
                tracer = tracerObj.AddComponent<ConnectionTracer>();
            }

            Debug.Log("✅ ConnectionTracer configured");
        }

        private void SetupPacketAnalyzer()
        {
            var analyzer = FindObjectOfType<PacketAnalyzer>();
            if (analyzer == null)
            {
                GameObject analyzerObj = new GameObject("PacketAnalyzer"); 
                analyzer = analyzerObj.AddComponent<PacketAnalyzer>();
            }

            Debug.Log("✅ PacketAnalyzer configured");
        }

        private void SetupEnhancedAuthServer()
        {
            // Find existing AuthServer and replace it
            var existingAuthServer = FindObjectOfType<DungeonRunners.Networking.AuthServer>();
            if (existingAuthServer != null)
            {
                Debug.LogWarning("⚠️ Found existing AuthServer - disabling it");
                existingAuthServer.enabled = false;
            }

            // Find or create EnhancedAuthServer
            var enhancedAuthServer = FindObjectOfType<DungeonRunners.Networking.EnhancedAuthServer>();
            if (enhancedAuthServer == null)
            {
                GameObject authServerObj = new GameObject("EnhancedAuthServer");
                enhancedAuthServer = authServerObj.AddComponent<DungeonRunners.Networking.EnhancedAuthServer>();
                
                // Copy configuration from existing server if available
                if (existingAuthServer != null)
                {
                    // Note: You'll need to manually assign the ServerConfig in the inspector
                    Debug.Log("📋 Please assign ServerConfig to EnhancedAuthServer in the inspector");
                }
            }

            Debug.Log("✅ EnhancedAuthServer configured");
        }

        private void PrintDebugInstructions()
        {
            Debug.Log("🎯 DEBUGGING INSTRUCTIONS:");
            Debug.Log("1. Start both servers (Auth on 2110, Game on 2603)");
            Debug.Log("2. Launch x32dbg with DungeonRunners.exe");
            Debug.Log("3. Set breakpoints on: connect(), WSAConnect(), socket()");
            Debug.Log("4. Connect client and monitor authentication flow");
            Debug.Log("5. Watch for 'PlayOk sent' message in Unity console");
            Debug.Log("6. Check if client ever calls connect() to port 2603");
            Debug.Log("7. Analyze connection_trace.log and packet_analysis.log");
            
            Debug.Log("📁 Generated Files:");
            Debug.Log("- connection_trace.log (x32dbg integration)");
            Debug.Log("- packet_analysis.log (detailed packet analysis)");
            Debug.Log("- debug_report_[timestamp].txt (comprehensive report)");
            
            Debug.Log("🔍 Key Success Indicators:");
            Debug.Log("✅ Auth: Login, ServerList, PlayOk work perfectly");
            Debug.Log("❌ MISSING: Game server connection after PlayOk");
            Debug.Log("🎯 GOAL: Identify why client doesn't call connect(10.0.0.140, 2603)");
        }
    }
}