using System;
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

            // PA1.5 diagnostic: prove this code path executes. If [BOOT-PROBE] appears in
            // server.log, ServerManager.Start did reach this line. If it does NOT appear,
            // some upstream silently early-returns (e.g., ShouldMirrorLog filter or
            // ShouldAbortStartup gate).
            Debug.LogError("[BOOT-PROBE] ServerManager.Start past line 45 (RuntimeEvidenceManager.LogBuildBinding done)");

            // Phase 6 cutover + PA1.4 chase shadow — hoisted ABOVE the self-tests so a
            // throwing self-test cannot silently skip these. (Earlier 2026-05-27 session
            // showed lines 49-67 producing zero output despite line 68 printing — likely
            // an uncaught exception in one of the Phase 1/3/4/5 self-tests. Wrap the
            // self-test block in try-catch so the cutover assignments always run.)
            DungeonRunners.Combat.WanderSimulator.UseNativeMoverSim = true;
            Debug.LogError("[MOVER-CUTOVER] UseNativeMoverSim ENABLED — mob positions driven by UnitMoverSim");
            DungeonRunners.Combat.CombatManager.EnableChaseMoverShadow = true;
            Debug.LogError("[CHASE-MOVER-CUTOVER] EnableChaseMoverShadow ENABLED — parallel chase mover, legacy still authoritative");

            // PA1.6 cutover (2026-05-27): mover authoritative for chase position.
            // Shadow data validated 21 samples median delta 0.10, max 2.00. PA1.5b
            // tightened arrive semantics so the max-2.00 boundary case is fixed.
            // Behavior validation pending in-game observation; revert by flipping
            // this back to false if mob chase regresses.
            DungeonRunners.Combat.CombatManager.UseUnitMoverForChase = true;
            Debug.LogError("[CHASE-MOVER-CUTOVER] UseUnitMoverForChase ENABLED — chase position driven by UnitMoverSim (legacy float math bypassed)");

            // Phase 1 (Option 1-full): validate Fixed32 implementation against reference
            // values before any phase-2+ code depends on it. Fast (~20ms); single-shot at boot.
            try
            {
                DungeonRunners.Utilities.Fixed32SelfTest.RunAll();
                DungeonRunners.Utilities.CobjParserSelfTest.RunAll();
                DungeonRunners.Utilities.TileLayoutSelfTest.RunAll();
                DungeonRunners.Utilities.PathMapBuilderSelfTest.RunAll();
                DungeonRunners.Core.PathfinderSelfTest.RunAll();
                DungeonRunners.Combat.UnitMoverSimSelfTest.RunAll();
                DungeonRunners.Core.PathfinderClientParityTest.RunAll();
                // C6 — lock in OnQueryApplyDamage semantics + roll-count / determinism
                // (touched 2026-05-28 to force Unity domain reload after first attempt cached old DLL)
                DungeonRunners.Combat.MonsterDamageComputerSelfTest.RunAll();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BOOT-PROBE] SelfTest threw — execution would have skipped subsequent lines. Type={ex.GetType().Name} Msg={ex.Message}\n{ex.StackTrace}");
            }
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
