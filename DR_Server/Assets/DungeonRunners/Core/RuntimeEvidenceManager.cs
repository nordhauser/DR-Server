using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace DungeonRunners.Core
{
    public static class RuntimeEvidenceManager
    {
        private static readonly object LogLock = new object();
        private static bool _started;
        private static bool _logHooked;
        private static StreamWriter _logWriter;
        private static string _logPath;
        private static DateTime _lastLogFlushUtc = DateTime.MinValue;
        private static int _pendingLogLines;
        private static bool _unityFilterInstalled;
        private static ILogHandler _originalLogHandler;
        private static Mutex _singleInstanceMutex;
        private static bool _ownsSingleInstanceMutex;
        private static bool _shouldAbortStartup;

        public static bool ShouldAbortStartup => _shouldAbortStartup;

        public static void EnsureStarted()
        {
            if (_started) return;
            _started = true;

            if (TryRelaunchNormalizedRuntime())
            {
                _shouldAbortStartup = true;
                Application.quitting += Stop;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return;
            }

            if (!TryAcquireSingleInstanceMutex())
            {
                _shouldAbortStartup = true;
                WriteDuplicateLaunchNotice("mutex", 0);
                Application.quitting += Stop;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return;
            }

            bool otherRuntimeActive = IsOtherRuntimeProcessActive(out int otherRuntimePid);
            if (otherRuntimeActive && !IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_RELAUNCH_CHILD")))
            {
                _shouldAbortStartup = true;
                WriteDuplicateLaunchNotice("process", otherRuntimePid);
                ReleaseSingleInstanceMutex();
                Application.quitting += Stop;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return;
            }

            if (IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_EVIDENCE")))
                return;

            if (!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_FOCUSED_LOGS")))
                SetFocusedUnityLogFilter(!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_VERBOSE_EVIDENCE_LOGS")));

            if (!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_LOGS")))
                StartLogMirror();

            if (!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_WIRE_CAPTURE")))
                StartWireCapture();

            Application.quitting += Stop;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public static void Stop()
        {
            lock (LogLock)
            {
                if (_logHooked)
                {
                    Application.logMessageReceivedThreaded -= OnLogMessage;
                    _logHooked = false;
                }

                if (_logWriter != null)
                {
                    _logWriter.Flush();
                    _logWriter.Dispose();
                    _logWriter = null;
                }

                if (_unityFilterInstalled)
                {
                    UnityEngine.Debug.unityLogger.logHandler = _originalLogHandler;
                    _originalLogHandler = null;
                    _unityFilterInstalled = false;
                }

                ReleaseSingleInstanceMutex();
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Stop();
        }

        public static void SetFocusedUnityLogFilter(bool enabled)
        {
            lock (LogLock)
            {
                if (enabled)
                {
                    if (_unityFilterInstalled) return;
                    _originalLogHandler = UnityEngine.Debug.unityLogger.logHandler;
                    UnityEngine.Debug.unityLogger.logHandler = new FocusedUnityLogHandler(_originalLogHandler);
                    _unityFilterInstalled = true;
                }
                else
                {
                    if (!_unityFilterInstalled) return;
                    UnityEngine.Debug.unityLogger.logHandler = _originalLogHandler;
                    _originalLogHandler = null;
                    _unityFilterInstalled = false;
                }
            }
        }

        private static void StartLogMirror()
        {
            try
            {
                string logDir = ResolveClientLogsDir();
                int pid = Process.GetCurrentProcess().Id;
                _logPath = ResolveServerLogPath(logDir);
                string targetDir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                    Directory.CreateDirectory(targetDir);
                _logWriter = new StreamWriter(new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                _logWriter.AutoFlush = false;
                _lastLogFlushUtc = DateTime.UtcNow;
                _pendingLogLines = 0;
                _logHooked = true;
                Application.logMessageReceivedThreaded += OnLogMessage;
                WriteLogLine("[RUNTIME-EVIDENCE] pid=" + pid + " log=" + _logPath);
                UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Mirroring server log to " + _logPath);
                LogBuildBinding("mirror");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Server log mirror failed: " + ex.Message);
            }
        }

        public static void LogBuildBinding(string source)
        {
            string marker = string.IsNullOrWhiteSpace(source) ? "[BUILD-BINDING] " : "[BUILD-BINDING] source=" + source + " ";
            UnityEngine.Debug.LogError(marker + ResolveBuildBinding());
        }

        private static void StartWireCapture()
        {
            try
            {
                string repoRoot = ResolveRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture skipped: repo root was not found");
                    return;
                }

                string scriptPath = Path.Combine(repoRoot, "scripts", "start_packet_capture.ps1");
                if (!File.Exists(scriptPath))
                {
                    UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture skipped: " + scriptPath + " is missing");
                    return;
                }

                int pid = Process.GetCurrentProcess().Id;
                string args = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) + " -Single -Name server-auto -OwnerPid " + pid + " -StopWhenOwnerExits";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture failed: powershell did not start");
                        return;
                    }

                    if (!proc.WaitForExit(1000))
                    {
                        UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture launch requested for DR_Server.exe pid=" + pid + " name=server-auto path=" + Path.Combine(ResolveClientLogsDir(), "server-auto.pcapng"));
                        return;
                    }

                    if (proc.ExitCode != 0)
                    {
                        UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture failed immediately: exit=" + proc.ExitCode);
                        return;
                    }

                    UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture attached to DR_Server.exe pid=" + pid + " name=server-auto");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture failed: " + ex.Message);
            }
        }

        private static bool TryRelaunchNormalizedRuntime()
        {
            if (Application.isEditor)
                return false;
            if (IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_RELAUNCH")))
                return false;
            string[] args = Environment.GetCommandLineArgs();
            if (HasArg(args, "-batchmode") && HasArg(args, "-nographics") && HasArg(args, "-logFile"))
                return false;
            if (IsOtherRuntimeProcessActive(out int otherRuntimePid))
            {
                WriteDuplicateLaunchNotice("process", otherRuntimePid);
                return true;
            }
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return false;
                string logPath = ResolveServerLogPath(ResolveClientLogsDir());
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-batchmode -logFile " + Quote(logPath) + " -nographics",
                    WorkingDirectory = ResolveBuildDir(),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables["DR_SERVER_RELAUNCH_CHILD"] = "1";
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[RUNTIME-EVIDENCE] Normalized runtime relaunch failed: " + ex.Message);
                return false;
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool TryAcquireSingleInstanceMutex()
        {
            try
            {
                _singleInstanceMutex = new Mutex(false, "DungeonRunnersServerUnityRuntime");
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, false);
                if (!_ownsSingleInstanceMutex)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    return false;
                }
                return true;
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null)
                return;
            try
            {
                if (_ownsSingleInstanceMutex)
                    _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
            }
            try
            {
                _singleInstanceMutex.Dispose();
            }
            catch
            {
            }
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        private static void WriteDuplicateLaunchNotice(string reason, int otherPid)
        {
            string line = DateTime.Now.ToString("o") + " [Error] [RUNTIME-EVIDENCE] Duplicate DR_Server.exe launch aborted pid=" + Process.GetCurrentProcess().Id + " otherPid=" + otherPid + " reason=" + reason;
            try
            {
                string path = ResolveServerLogPath(ResolveClientLogsDir());
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
            UnityEngine.Debug.LogError(line);
        }

        private static bool IsOtherRuntimeProcessActive(out int otherPid)
        {
            otherPid = 0;
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    string currentPath = "";
                    try
                    {
                        currentPath = current.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                    }

                    string processName = !string.IsNullOrWhiteSpace(currentPath)
                        ? Path.GetFileNameWithoutExtension(currentPath)
                        : current.ProcessName;

                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            if (proc.Id == current.Id)
                                continue;

                            string otherPath = "";
                            try
                            {
                                otherPath = proc.MainModule?.FileName ?? "";
                            }
                            catch
                            {
                            }

                            if (string.IsNullOrWhiteSpace(currentPath) ||
                                string.Equals(otherPath, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                otherPid = proc.Id;
                                return true;
                            }
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (!ShouldMirrorLog(condition, type))
                return;

            string line = DateTime.Now.ToString("o") + " [" + type + "] " + condition;
            if (!string.IsNullOrWhiteSpace(stackTrace) && (type == LogType.Exception || type == LogType.Assert))
                line += Environment.NewLine + stackTrace;
            WriteLogLine(line);
        }

        private static string ResolveBuildBinding()
        {
            try
            {
                string buildDir = ResolveBuildDir();
                string buildInfoPath = Path.Combine(buildDir, "build_info.txt");
                if (File.Exists(buildInfoPath))
                    return File.ReadAllText(buildInfoPath).Replace("\r", " ").Replace("\n", " ").Trim();
                return "Runtime=unity BuildInfoMissing=" + buildInfoPath;
            }
            catch (Exception ex)
            {
                return "Runtime=unity BuildInfoError=" + ex.Message;
            }
        }

        private static string ResolveBuildDir()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (!string.IsNullOrWhiteSpace(dataPath))
                {
                    var dir = Directory.GetParent(dataPath);
                    if (dir != null) return dir.FullName;
                }
            }
            catch
            {
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static void WriteLogLine(string line)
        {
            lock (LogLock)
            {
                if (_logWriter == null) return;
                _logWriter.WriteLine(line);
                _pendingLogLines++;
                DateTime now = DateTime.UtcNow;
                if (_pendingLogLines >= 64 || (now - _lastLogFlushUtc).TotalSeconds >= 1.0)
                {
                    _logWriter.Flush();
                    _pendingLogLines = 0;
                    _lastLogFlushUtc = now;
                }
            }
        }

        private static bool ShouldMirrorLog(string condition, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Assert)
                return true;

            if (IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_VERBOSE_EVIDENCE_LOGS")))
                return true;

            if (string.IsNullOrWhiteSpace(condition))
                return false;

            string line = condition.TrimStart();
            if (IsImportantLog(line) || IsFocusedLog(line))
                return true;

            string[] noisyPrefixes =
            {
                "[UDP]",
                "[NEW-PKT]",
                "[OP",
                "[SIZE-CHECK]",
                "[CUMULATIVE-",
                "[PLAYER-SPAWN-HEX]",
                // "[MONSTER-SPAWN-HEX]", // moved to allowedPrefixes 2026-05-24 for spawn-body diagnostic
                "[DIAG-0x00]",
                "[PLAYER-STATE]",
                "[PACKET-",
                "[NPC-",
                "[Portal",
                "[SendZoneNPCs]",
                "[SendZonePortals]",
                "[SendZoneCheckpoints]",
                "[Checkpoint]",
                "[QUEST-",
                "[GNOME-",
                "[GCObject]",
                "[CHARLIST",
                "[ACTION-READ]",
                "[COMPONENT]",
                "[COMPONENT-ROUTE]",
                "[INVENTORY]",
                "[GROUP]",
                "[ROOM-RNG]",
                "[SPAWN",
                "[DROP-LOAD]",
                "[PASSIVE-",
                "[ClassPassiveData]",
                "[KNOBS]",
                "[MEMBER]",
                "[Combat] Registered",
                "[ZoneSpawnManager]",
                "[FOLLOW",
                "[MOTD]",
                "[WELCOME]",
                "[WriteItem]",
                "[EQUIP-WRITEINIT]",
                "[UDP-RAW]",
                "[UDP-FOLLOW]",
                "[UDP-SKILLS]",
                "[ENTITY-STREAM]",
                "[ENTITY-CH]",
                "[TICK] Starting",
                "[TICK] Using",
                "════",
                "0000",
                "=== OPERATION",
                "=== PACKET",
                "=== FULL",
                "FULL UNITY SPAWN",
                "✓ Operation"
            };

            foreach (string prefix in noisyPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return false;
        }

        private static bool IsImportantLog(string line)
        {
            return line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("CRITICAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Invalid ComponentID", StringComparison.OrdinalIgnoreCase) >= 0
                || line.StartsWith("[RUNTIME-EVIDENCE]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[BUILD-BINDING]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[DRLog]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("=== Dungeon Runners Server", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Server Version:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Max Players:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Auth Server component", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Game Server component", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG] Loaded", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG] Reloaded", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG] DB load error", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[SERVER]", StringComparison.OrdinalIgnoreCase);
        }

        // 2026-05-23 RNG-divergence diagnostic. When true, server.log only captures
        // RNG-trace + a few essential combat markers. Drops every other prefix so the
        // log is small enough to diff line-for-line against client x32dbg trace.
        // Flip back to false when done.
        public static bool RngOnlyMode = false;

        private static readonly string[] RngOnlyPrefixes =
        {
            "[RNG-TRACE]",
            "[RNG-SEED]",
            "[RNG-COMBAT]",
            "[RNG-AUDIT]",
            "[PLAYER-HIT-DETAIL]",
            "[MONSTER-DAMAGE]",
            "[ZONE-JOIN]",
            "[DUNGEON-PORTAL]",
            "[SPAWN-TRACK]",
            "[Combat] SPAWNED:",
            "[ROOM-RNG]",
            "[RUNTIME-SEED]",
        };

        // 2026-05-27 — S10g x32dbg parity capture session. Default ON so log
        // doesn't choke Unity during a long capture. Set FALSE when done.
        public static bool MobSwingCaptureMode = true;

        private static readonly string[] MobSwingCapturePrefixes =
        {
            "[MOB-SWING]",            // ★ the actual roll-by-roll diagnostic
            "[MOB-SWING-SKIP]",       // ★ S12 range gate: skipped because mob out of attack range
            "[MOB-CTRL-REGISTER]",    // ★ profile-missing failures on mob spawn (low volume)
            "[MOB-CTRL-DIAG]",        // ★ throttled controller state diagnostic (3s/per session)
            "[MOB-DAMAGE-APPLY]",     // ★ damage apply (gated off until flag flipped)
            "[SERVER-AGGRO]",         // ★ confirms aggro fires before swing
            "[ROOM-RNG]",             // ★ confirms RNG seed init
            "[RNG-SEED]",             // ★ seed transform diagnostic
            "[Combat] SPAWNED:",      // ★ mob identity for cross-ref
            "[KILL]",                 // mob death (low volume)
            "[KILL-XP]",              // mob kill XP (low volume)
            "[ZONE-IN]",              // zone entry context (low volume)
            "[PLAYER-HP-SUFFIX]",     // ★★★ S10j: outbound HP stamped into entity-update packets
            "[CLIENT-REPORT-FREQ]",   // ★★★ S10j: inbound HP from client (opcode 0x36 etc.)
            "[SYNC-SUFFIX-BLOCK]",    // ★ outbound HP rejected (popup-precursor diagnostic)
            "[SYNC-SUFFIX-UNRESOLVED]", // ★ outbound HP unresolved
            "[HP-SYNC]",              // ★ inbound HP from opcode 0x36
            "[HP-VERIFY]",            // ★ inbound HP rejected
            "[SIM-DELTA]",            // ★ server vs client HP delta when client reports
            "[POISON-SHOT-TICK]",     // ★★ S11.1: DOT damage ticks (per-tick mob HP changes)
            "[POISON-SHOT-MOD]",      // ★★ S11.1: poison modifier add/remove
            "[COMBAT-EVENT]",         // ★★ S11.1: canonical damage event log (mob HP changes)
            "[MON-HP-TRUTH]",         // ★ direct mob HP truth diagnostic
            "[MON-REGEN]",            // ★ mob regen ticks (HP can creep back up)
            "[MON-DEATH-LIFECYCLE]",  // ★ mob death state transitions
            "[MON-DEATH-STATE]",      // ★ mob death state clears

            // ── PA1 (chase mover unification) prefixes — added 2026-05-27 ──
            "[BOOT-PROBE]",           // PA1.5a: diagnose if ServerManager.Start reached the cutover block
            "[MOVER-CUTOVER]",        // Phase 6 wander mover cutover state
            "[CHASE-MOVER-CUTOVER]",  // PA1.4/PA1.6: chase mover state (boot + per-mob Mover provisioning)
            "[CHASE-MOVER-DIFF]",     // PA1.4: chase shadow-mode UnitMoverSim vs legacy delta
            "[MOVER-DIFF]",           // Phase 6 wander shadow-mode delta (companion to above)
            "[WANDER-SIM]",           // mob wander-sim registration confirmation
        };

        private static bool IsFocusedLog(string line)
        {
            if (MobSwingCaptureMode)
            {
                foreach (string prefix in MobSwingCapturePrefixes)
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            if (RngOnlyMode)
            {
                foreach (string prefix in RngOnlyPrefixes)
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            string[] allowedPrefixes =
            {
                // ─────────── minimal allowlist for 2026-05-25 HP-sync test session ───────────
                // Previous trim still wasn't enough — Unity froze on combat from log spam from
                // [MON-STATE] (584 lines), [MON-ATTACK] (560), [PLAYER-HP-TRUTH] (499), and the
                // redundant [MON-DAMAGE] / [PLAYER-DAMAGE] / [COMBAT-EVENT] triplet (139 ea).
                // This pass keeps ONLY what's needed to verify the FloorToInt fix works:
                //   [Combat]           — SPAWNED line for mob identity
                //   [COMBAT-EVENT]     — one line per attack with damage + RNG (the canonical
                //                        combat trace; the [MON-DAMAGE]/[PLAYER-DAMAGE] lines
                //                        duplicate this info)
                //   [HP-REFRESH-AUTO]  — Path C refresh confirmation
                //   [DMG-INPUT]        — gated, default silent
                //   [PUSHHP]/[PushHP]  — @pushhp admin command (1 line per use)
                //   [SETBONUS]         — @setdmgbonus admin command
                //   [ZONE*]            — zone transitions (low volume, useful for context)
                //   [SAVE]             — save events (low volume)
                //   [MerchantManager]/[Merchant]/[MERCHANT-REFRESH] — merchant flow (non-combat work)
                //   [QUEST-*]          — quest events (non-combat work)
                //   [RUNTIME-EVIDENCE] — boot banner via IsImportantLog
                //   [Error]/CRITICAL/FATAL — via IsImportantLog
                //
                // To re-enable any trimmed prefix temporarily, uncomment it and reload. The
                // single source-gated [COMBAT-TICK] also stays off behind VerboseCombatTick.
                // ───────────────────────────────────────────────────────────────────────────

                "[Combat]",                  // SPAWNED line gives mob identity for cross-ref
                // "[COMBAT]",               // trimmed 2026-05-25b: redundant
                "[COMBAT-EVENT]",            // ★ canonical per-attack trace with damage + RNG
                "[SIM-DELTA]",               // ★ Stage 2 combat-sim diagnostic — server vs client HP delta on client report
                "[MONSTER-ATTACK-DATA]",     // Stage 1 boot-time GlobalKnobs cache confirmation
                "[AGGRO]",                   // Stage 2/3 — client 0x09 aggro packet handler entry
                "[AGGRO-OBSERVE]",           // Stage 2/3 — server bailed on client aggro intent (range/contact check failed)
                "[PROXIMITY-TICK]",          // Stage 3 — per-tick summary of ProcessProximityAggro (throttled 1s)
                "[PROXIMITY-MISS]",          // Stage 3 — per-mob near-miss with gating reason (throttled 1s per mob+reason)
                "[PROXIMITY-AGGRO]",         // Stage 3 — successful proximity aggro acquisition
                "[WANDER-SIM]",              // Stage 3 — WanderSimulator entity registration (Debug.LogError unconditional)
                "[WANDER-RNG]",              // Stage 3 — wander state machine RNG consumption (gated by VerboseWander)
                "[WANDER-AUDIT]",            // Stage 3 — wander target picking + pathmap validation
                "[WANDER-MOVE]",             // Stage 3 — wander arrival event (mob's ClientX/Y updated)
                "[WANDER-SYNC]",             // Stage 3 — CombatManager.SyncMonsterWanderClientVisiblePosition fires
                "[PLAYER-DR-DETAIL]",        // Stage 3 triage — server's DR breakdown per monster swing (compare to character sheet)
                "[CHASE-TICK]",              // Stage 3 triage — chase position progress (throttled 1/sec per mob-target pair)
                "[CLIENT-REPORT-FREQ]",      // Stage 3 T1 — every entry to ObserveClientPlayerHP, measures Path X viability
                "[RANGED-PROJECTILE]",       // Stage 3 — projectile impact resolution (flightTicks, dueTick, delay) per player projectile shot
                "[TICK-RATE]",               // Stage 3 — measures actual server combat tick rate per second (unifying-cause investigation)
                "[PLAYER-POS]",              // Stage 3 — server's tracked player.PosX/Y per second (detects position-tracking lag)
                "[ALERT-SHOUT]",             // Stage 3 — shout propagation: aggro'd mob alerts neighbors within ShoutRange (two-stage aggro v2)
                "[LAYOUT-SEED-OVERRIDE]",    // 2026-05-27 — hypothesis test: zone-derived seed (client uses World* as DungeonGenerator seed)
                "[LAYOUT-SEED]",             // 2026-05-27 — normal layout seed assignment (random per-instance)
                "[FIXED32-SELFTEST]",        // 2026-05-27 — Phase 1 Fixed32 library validation at server boot
                "[MAZE-DUMP]",               // 2026-05-27 — Phase 2 dumps grid to disk for client diff
                "[MAZE-DIFF]",               // 2026-05-27 — Phase 2 diff tool output
                "[COBJ-SELFTEST]",           // 2026-05-27 — Phase 3 CobjParser validation at server boot
                "[COBJ-PARSE]",              // 2026-05-27 — Phase 3 cobj-file parser runtime events
                "[TILE-SELFTEST]",           // 2026-05-27 — Phase 3 TileLayoutLoader + Resolver validation at boot
                "[PATHMAP-BUILD]",           // 2026-05-27 — Phase 3 PathMap.BuildFromGeometry diagnostic line
                "[PATHMAPBUILD-SELFTEST]",   // 2026-05-27 — Phase 3 PathMapBuilder validation at boot
                "[PATHFINDER-SELFTEST]",     // 2026-05-27 — Phase 4 Pathfinder A* validation at boot
                "[MOVERSIM-SELFTEST]",       // 2026-05-27 — Phase 5 UnitMoverSim v0.5 validation at boot
                "[PATHFINDER-PARITY]",       // 2026-05-27 — Phase 4b client-capture comparison
                "[MOVER-DIFF]",              // 2026-05-27 — Phase 6 shadow-mode UnitMoverSim vs legacy WanderSim divergence
                "[MOVER-CUTOVER]",           // 2026-05-27 — Phase 6 cutover state changes
                "[CHASE-MOVER-DIFF]",        // 2026-05-27 — PA1.4: shadow-mode chase UnitMoverSim vs legacy float math
                "[CHASE-MOVER-CUTOVER]",     // 2026-05-27 — PA1.6: chase mover cutover state changes
                "[BOOT-PROBE]",              // 2026-05-27 — PA1.5: diagnostic to confirm ServerManager.Start path
                "[MONSTER-ATTACK-SELFTEST]", // 2026-05-27 — Section 10 task S10.2 validation
                "[MDC-SELFTEST]",            // 2026-05-27 — Section 10 task S10.3 MonsterDamageComputer validation
                "[MAC-SELFTEST]",            // 2026-05-27 — Section 10 task S10.4 MonsterAttackController validation
                "[MOB-SWING]",               // 2026-05-27 — Section 10 mob swing diagnostic for x32dbg comparison
                "[MOB-CTRL-REGISTER]",       // 2026-05-27 — S10d: profile-missing failures on mob spawn
                "[MOB-DAMAGE-APPLY]",        // 2026-05-27 — S10d: damage apply (only fires when flag flipped)
                "[MOVER]",                   // 2026-05-27 — Phase 5 runtime mover diagnostics
                // "[COMBAT-LIFECYCLE]",     // trimmed 2026-05-25b: not essential for value diag
                // "[ATTACK]",
                // "[ACTION",
                // "[NPC]",
                // "[DAMAGE]",
                // "[GetNearest]",
                // "[MON-DAMAGE]",           // trimmed 2026-05-25b: redundant with [COMBAT-EVENT] (139 lines)
                // "[MON-DAMAGE-SYNC]",
                // "[MON-ATTACK]",           // trimmed 2026-05-25b: 560 lines — attack lifecycle, not needed
                // "[MON-STATE]",            // trimmed 2026-05-25b: 584 lines — state transitions, source also gated
                // "[MONSTER-DAMAGE]",
                // "[MON-CONTACT]",
                // "[MON-HP-TRUTH]",
                // "[MON-SKILL]",
                // "[MON-SKILL-CD]",
                // "[MON-HP-PRIMER]",        // trimmed 2026-05-25b: 141 lines
                // "[DUNGEON-PORTAL]",
                // "[PROJECTILE-HIT]",
                // "[RANGED-PROJECTILE]",
                // "[RANGED-PROJECTILE-DUE]",
                // "[PROJECTILE-ENTITY]",
                // "[PLAYER-HP-TRUTH]",      // trimmed 2026-05-25b: 499 lines — HP truth tracking
                // "[PLAYER-HP-SUFFIX]",
                // "[PLAYER-HP-NATIVE",
                // "[PLAYER-DAMAGE]",        // trimmed 2026-05-25b: redundant with [COMBAT-EVENT]
                // "[HP-PRESERVE]",
                // "[SPAWN-HP-PRESERVE]",
                // "[SPAWN-HP-FULL]",
                // "[SPAWN-HP-REGEN]",
                // "[SPAWN-XP]",
                // "[ZONE-HP-PRESERVE]",
                // "[ZONE-HP-FULL]",
                // "[ZONE-HP-REGEN]",
                // "[ZONE-HP-BOOTSTRAP]",
                // "[PLAYERSTATE",
                // "[ALLOC-STATS]",
                // "[HP-FINAL]",
                // "[PLAYER-HIT-DETAIL]",
                // "[SPAWN]",                // trimmed 2026-05-25b: covered by [Combat] SPAWNED
                // "[SPAWN-SYNCH]",
                // "[SPAWN-TRACK]",
                "[PUSHHP]",                  // @pushhp admin (1 line per use)
                "[PushHP]",                  // @pushhp chat reply
                "[HP-REFRESH-AUTO]",         // ★ Path C refresh — verify auto-refresh fires
                "[DMG-INPUT]",               // gated, default silent
                "[SETBONUS]",                // @setdmgbonus admin
                "[KILL]",                    // ★ 2026-05-25: kill finalize + HP=0 death refresh send
                "[KILL-XP]",                 // 2026-05-25: XP awarded on kill
                "[KILL-FINALIZE]",           // 2026-05-25: kill flow entry
                "[KILL-DEDUP]",              // 2026-05-25: kill duplicate skipped
                "[KILL-ERROR]",              // 2026-05-25: errors in kill flow
                // "[SPELL",
                // "[TAKEDAMAGE]",           // trimmed 2026-05-25b: 78 lines — covered by [COMBAT-EVENT]
                // "[MANA]",
                // "[MANA-0x52]",
                // "[WEAPON-CYCLE]",
                "[SERVER-AGGRO]",   // 2026-05-25: aggro packet fire (Type 9), critical for state-4 prevention
                "[UDP-AGGRO9]",     // 2026-05-25: confirms aggro packet was actually sent over UDP
                // "[SERVER-SHOUT]",
                // "[AGGRO]",
                // "[AGGRO-OBSERVE]",
                // "[MazeSpawner]",
                // "[ZoneSpawnManager]",
                // "[DLL-HP]",
                // "[DLL-HP-RAW]",
                // "[HP-SYNC]",
                // "[ACTION-0x50-SYNC]",
                // "[ACTION-SYNC]",
                // "[HP-VERIFY]",
                // "[SYNCH",
                "[UDP-COMBAT",
                "[ZONE-INVULN]",
                "[ZONE-TRACK]",
                "[ZONE-IN]",
                "[CHESTS]",
                "[WORLD-ENTITIES]",
                "[INSTANCE]",
                "[MERCHANT-DIAG]",
                "[LOC]",
                "[POSSE]",
                "[POSSE-PROBE]",
                "[PVP]",
                "[PVP-DUEL]",
                "[PVP-MATCH]",
                "[DUELARENA]",
                "[GROUP-CH0B]",
                "[GROUP]",
                "[ADMIN]",
                "[ACCOUNT]",
                "[ItemStatDB]",
                "[WriteItem]",
                "[IG-INJECT]",
                "[INVENTORY-WRITEINIT]",
                "[WELL]",
                "[QUEST-AVAILABLE]",
                "[QUEST-ACCEPT]",
                "[QUEST-TURNIN]",
                "[QUEST-REWARDS]",
                "[QUEST-ITEM-REMOVE]",
                "[QUEST-QUERY]",
                "[QUEST-PROGRESS]",
                "[QUEST-ADD]",
                "[QUEST-COMPLETE]",
                "[QUEST-REMOVE]",
                "[DROP-RING]",
                "[DROP-AMULET]",
                "[DROP-WRITEINIT]",
                // Equip + inventory flow — trimmed 2026-05-25 because these flood
                // on login/zone-in (82+54+10+10+8 = ~160 lines per session) and
                // aren't needed for the HP-sync test path. Re-enable when
                // investigating inventory bugs again.
                // "[INVENTORY]",            // trimmed 2026-05-25
                // "[INV-TRACK]",            // trimmed 2026-05-25
                // "[INV-SLOT]",             // trimmed 2026-05-25
                // "[INV-VALIDATOR]",        // trimmed 2026-05-25
                // "[INV-RESTORE]",          // trimmed 2026-05-25
                // "[EQUIP",                 // trimmed 2026-05-25 — matches [EQUIP-CHAR], [EQUIPMENT-INIT], etc.
                // "[EQUIPMENT-INIT]",       // trimmed 2026-05-25
                // "[GIVE-STACKED]",         // trimmed 2026-05-25
                // "[GIVE-ON-ACCEPT-ITEM]",  // trimmed 2026-05-25
                "[STATE]",
                "[SAVE]",
                // Merchant refresh path — for "merchant timer ran out, no new
                // items spawned" diagnosis.
                "[MerchantManager]",
                "[Merchant]",
                "[MERCHANT-REFRESH]",
                "[REFRESH]",
                "[STACK-SEARCH]"
            };

            foreach (string prefix in allowedPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (line.StartsWith("[ZONE-JOIN]", StringComparison.OrdinalIgnoreCase))
            {
                return line.IndexOf("Zone:", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("ZoneJoin", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("FIRST LOGIN", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("LATE JOINER", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Room RNG seed", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("already spawned", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("transition", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Using zone ID", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (line.StartsWith("[ZONE]", StringComparison.OrdinalIgnoreCase))
            {
                return line.IndexOf("ZONE TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("CHECKPOINT TELEPORT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Sent DISCONNECT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Sent CONNECT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("CurrentZone", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Stopped tick coroutine", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Cleared message queue", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private sealed class FocusedUnityLogHandler : ILogHandler
        {
            private readonly ILogHandler _inner;

            public FocusedUnityLogHandler(ILogHandler inner)
            {
                _inner = inner;
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                _inner?.LogException(exception, context);
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                string condition = format;
                if (args != null && args.Length > 0)
                {
                    try
                    {
                        condition = string.Format(format, args);
                    }
                    catch
                    {
                        condition = format;
                    }
                }

                if (ShouldMirrorLog(condition, logType))
                    _inner?.LogFormat(logType, context, format, args);
            }
        }

        private static string ResolveClientLogsDir()
        {
            string overrideDir = Environment.GetEnvironmentVariable("DR_CLIENT_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;
            return @"C:\Dungeon Runners\Dungeon Runners Game\logs";
        }

        private static string ResolveServerLogPath(string fallbackLogDir)
        {
            string overridePath = Environment.GetEnvironmentVariable("DR_SERVER_LOG_FILE");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath;

            return Path.Combine(fallbackLogDir, "server.log");
        }

        private static string ResolveRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string script = Path.Combine(dir.FullName, "scripts", "start_packet_capture.ps1");
                if (File.Exists(script))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
