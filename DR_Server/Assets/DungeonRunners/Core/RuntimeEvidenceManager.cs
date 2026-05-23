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
                "[MONSTER-SPAWN-HEX]",
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
        public static bool RngOnlyMode = true;

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

        private static bool IsFocusedLog(string line)
        {
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
                "[Combat]",
                "[COMBAT",
                "[ATTACK]",
                "[ACTION",
                "[NPC]",
                "[DAMAGE]",
                "[GetNearest]",
                "[MON-DAMAGE]",
                "[MON-DAMAGE-SYNC]",
                "[MON-ATTACK]",
                "[MON-STATE]",
                "[MONSTER-DAMAGE]",
                "[MON-CONTACT]",
                "[MON-HP-TRUTH]",
                "[MON-REGEN]",
                "[MON-MANA-REGEN]",
                "[MON-SKILL]",
                "[MON-SKILL-CD]",
                "[MON-MOVE]",
                "[MON-MOVE-SEND]",
                "[MON-PRE-SUFFIX-COMBAT]",
                "[MON-HP-PRIMER]",
                "[DUNGEON-SNAPSHOT]",
                "[DUNGEON-SPAWN]",
                "[DUNGEON-TRANSFORM]",
                "[DUNGEON-PORTAL]",
                "[PROJECTILE-HIT]",
                "[PRE-SUFFIX-DUE-DRAIN]",
                "[RANGED-PROJECTILE]",
                "[RANGED-PROJECTILE-DUE]",
                "[PROJECTILE-ENTITY]",
                "[PLAYER-HP-TRUTH]",
                "[PLAYER-HP-SUFFIX]",
                "[PLAYER-HP-NATIVE",
                "[PLAYER-DAMAGE]",
                "[PLAYER-REGEN]",
                "[PLAYER-REGEN-COOLDOWN]",
                "[HP-PRESERVE]",
                "[SPAWN-HP-PRESERVE]",
                "[SPAWN-HP-FULL]",
                "[SPAWN-HP-REGEN]",
                "[SPAWN-XP]",
                "[ZONE-HP-PRESERVE]",
                "[ZONE-HP-FULL]",
                "[ZONE-HP-REGEN]",
                "[ZONE-HP-BOOTSTRAP]",
                "[PLAYERSTATE",
                "[ALLOC-STATS]",
                "[HP-FINAL]",
                "[PLAYER-HIT-DETAIL]",
                "[SPAWN]",
                "[SPAWN-SYNCH]",
                "[SPAWN-TRACK]",
                "[PACKET-2]",
                "[PACKET-3]",
                "[OP12]",
                "[RNG-AUDIT]",
                "[RNG-COMBAT]",
                "[RNG-SEED]",
                "[RNG-TRACE]",
                "[RUNTIME-SEED]",
                "[NATIVE-VALIDATION-CUTOFF]",
                "[LAYOUT-SEED]",
                "[UDP-RNG]",
                "[SPELL",
                "[LOCAL-MOVE-ACK]",
                "[SEND-COMPRESSEDA]",
                "[SYNC-SUFFIX",
                "[TAKEDAMAGE]",
                "[MANA]",
                "[MANA-0x52]",
                "[WEAPON-CYCLE]",
                "[SERVER-AGGRO]",
                "[SERVER-SHOUT]",
                "[AGGRO]",
                "[AGGRO-OBSERVE]",
                "[AI",
                "[AI-TRACE]",
                "[ROOM-RNG]",
                "[WANDER-RNG]",
                "[WANDER-AUDIT]",
                "[MazeSpawner]",
                "[ZoneSpawnManager]",
                "[BEHAVIOR]",
                "[DLL-HP]",
                "[DLL-HP-RAW]",
                "[GETSYNCH]",
                "[HP-SYNC]",
                "[SEND-UPDATE]",
                "[MOVE-SYNC]",
                "[ACTION-0x50-SYNC]",
                "[ACTION-SYNC]",
                "[HP-VERIFY]",
                "[SYNCH",
                "[REGEN]",
                "[COMBAT-TICK]",
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
                // Equip + inventory flow — needed to diagnose "ring disappeared
                // on equip". The filter blocks these from both server.log AND
                // Editor.log via FocusedUnityLogHandler, so they're hidden by
                // default. Enable explicitly for the next test pass.
                "[INVENTORY]",
                "[INV-TRACK]",
                "[INV-SLOT]",
                "[INV-VALIDATOR]",
                "[INV-RESTORE]",
                "[EQUIP",
                "[EQUIPMENT-INIT]",
                "[GIVE-STACKED]",
                "[GIVE-ON-ACCEPT-ITEM]",
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
