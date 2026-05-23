using UnityEngine;
using DungeonRunners.Networking;

namespace DungeonRunners.Core
{
    /// <summary>
    /// Main server manager - attach this to a GameObject in your scene
    /// </summary>
    public class ServerManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private ServerConfig serverConfig;

        [Header("Server Components")]
        [SerializeField] private bool startAuthServer = true;
        [SerializeField] private bool startGameServer = true;

        private AuthServer _authServer;
        private UnityGameServer _gameServer;

        void Awake()
        {
            RuntimeEvidenceManager.EnsureStarted();
            if (RuntimeEvidenceManager.ShouldAbortStartup)
            {
                enabled = false;
                Application.Quit();
                return;
            }
            var dispatcher = MainThreadDispatcher.Instance;
        }

        void Start()
        {
            if (RuntimeEvidenceManager.ShouldAbortStartup)
                return;

            if (serverConfig == null)
            {
                Debug.LogError("ServerConfig not assigned! Please create a ServerConfig asset and assign it.");
                return;
            }

            Debug.Log("=== Dungeon Runners Server Starting ===");
            RuntimeEvidenceManager.LogBuildBinding("startup");
            Debug.Log($"Server Version: {serverConfig.serverVersion}");
            Debug.Log($"Max Players: {ServerSettings.Get("maxPlayers", serverConfig.maxPlayers)}");

            if (startAuthServer)
            {
                StartAuthServer();
            }

            if (startGameServer)
            {
                StartGameServer();
            }

            Debug.Log("=== Server Initialization Complete ===");
        }

        private void StartAuthServer()
        {
            GameObject authGO = new GameObject("AuthServer");
            authGO.transform.SetParent(transform);
            _authServer = authGO.AddComponent<AuthServer>();

            // Use reflection to set the config field
            var field = typeof(AuthServer).GetField("config",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_authServer, serverConfig);

            Debug.Log($"Auth Server component created on port {serverConfig.authServerPort}");
        }

        private void StartGameServer()
        {
            GameObject gameGO = new GameObject("GameServer");
            gameGO.transform.SetParent(transform);
            _gameServer = gameGO.AddComponent<UnityGameServer>();

            // Use reflection to set the config field
            var field = typeof(UnityGameServer).GetField("config",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_gameServer, serverConfig);

            Debug.Log($"Game Server component created on port {serverConfig.gameServerPort}");
        }

        void OnApplicationQuit()
        {
            Debug.Log("=== Dungeon Runners Server Shutting Down ===");
        }
    }
}
