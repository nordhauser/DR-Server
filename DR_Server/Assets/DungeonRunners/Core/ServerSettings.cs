using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DungeonRunners.Networking;

namespace DungeonRunners.Core
{
    /// <summary>
    /// Runtime server configuration.
    /// 
    /// STATIC: server.cfg (plain text next to executable, edit with notepad)
    /// DYNAMIC: SQLite server_settings table (@set command, live changes)
    /// Priority: DB > .cfg > hardcoded default
    /// 
    /// Usage:
    ///   ServerSettings.Get("maxPlayers", 50)
    ///   ServerSettings.GetFloat("experienceMod", 5.0f)
    ///   ServerSettings.GetString("welcomeMessage", "Welcome!")
    ///   ServerSettings.Set("maxPlayers", "100")  // saves to DB, live
    ///   ServerSettings.Reload()                  // re-reads .cfg
    /// </summary>
    public static class ServerSettings
    {
        private static Dictionary<string, string> _cfgValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _dbValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded = false;
        private static string _cfgPath;

        public static void Load()
        {
            _loaded = true; // MUST be first to prevent recursion
            _cfgValues.Clear();
            _dbValues.Clear();

            _cfgPath = FindCfgPath();
            if (_cfgPath != null)
            {
                LoadCfgFile(_cfgPath);
                Debug.LogError($"[CONFIG] Loaded {_cfgValues.Count} settings from {_cfgPath}");
            }
            else
            {
                Debug.LogError("[CONFIG] server.cfg not found — using defaults");
            }

            LoadFromDatabase();
            QueueConnectionBridge.MaxPlayers = Get("maxPlayers", 50);
            LogAllSettings();
        }

        public static void Reload()
        {
            _cfgValues.Clear();
            if (_cfgPath != null)
            {
                LoadCfgFile(_cfgPath);
                Debug.LogError($"[CONFIG] Reloaded {_cfgValues.Count} settings from {_cfgPath}");
            }
            LoadFromDatabase();
            QueueConnectionBridge.MaxPlayers = Get("maxPlayers", 50);
            Debug.LogError($"[CONFIG] Reloaded. MaxPlayers={QueueConnectionBridge.MaxPlayers}");
        }

        // ═══ GETTERS — DB overrides .cfg, .cfg overrides default ═══

        private static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        public static string GetString(string key, string defaultValue = "")
        {
            EnsureLoaded();
            if (_dbValues.TryGetValue(key, out string dbVal)) return dbVal;
            if (_cfgValues.TryGetValue(key, out string cfgVal)) return cfgVal;
            return defaultValue;
        }

        public static int Get(string key, int defaultValue)
        {
            string val = GetString(key, null);
            if (val != null && int.TryParse(val, out int result)) return result;
            return defaultValue;
        }

        public static float GetFloat(string key, float defaultValue)
        {
            string val = GetString(key, null);
            if (val != null && float.TryParse(val,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
        }

        public static bool GetBool(string key, bool defaultValue)
        {
            string val = GetString(key, null);
            if (val == null) return defaultValue;
            val = val.ToLower().Trim();
            if (val == "true" || val == "1" || val == "yes") return true;
            if (val == "false" || val == "0" || val == "no") return false;
            return defaultValue;
        }

        // ═══ SET — writes to SQLite, live, persists across restarts ═══

        public static void Set(string key, string value)
        {
            _dbValues[key] = value;
            SaveToDatabase(key, value);
            Debug.LogError($"[CONFIG] Set '{key}' = '{value}' (saved to DB)");

            if (key.Equals("maxPlayers", StringComparison.OrdinalIgnoreCase))
                QueueConnectionBridge.MaxPlayers = Get("maxPlayers", 50);
        }

        // ═══ REMOVE — deletes from DB, falls back to .cfg ═══

        public static void Remove(string key)
        {
            _dbValues.Remove(key);
            RemoveFromDatabase(key);
            Debug.LogError($"[CONFIG] Removed DB override for '{key}'");

            if (key.Equals("maxPlayers", StringComparison.OrdinalIgnoreCase))
                QueueConnectionBridge.MaxPlayers = Get("maxPlayers", 50);
        }

        // ═══ LIST — all settings for admin display ═══

        public static Dictionary<string, (string value, string source)> GetAll()
        {
            EnsureLoaded();
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _cfgValues)
                result[kvp.Key] = (kvp.Value, "cfg");
            foreach (var kvp in _dbValues)
                result[kvp.Key] = (kvp.Value, "db");
            return result;
        }

        // ═══ .CFG FILE PARSER ═══

        private static void LoadCfgFile(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#")) continue;

                    int eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;

                    string key = line.Substring(0, eqIdx).Trim();
                    string value = line.Substring(eqIdx + 1).Trim();

                    // Strip inline comments — but only if # is preceded by whitespace
                    // so hex colors like #FFCC66 are preserved
                    for (int ci = 1; ci < value.Length; ci++)
                    {
                        if (value[ci] == '#' && char.IsWhiteSpace(value[ci - 1]))
                        {
                            value = value.Substring(0, ci).Trim();
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(key))
                        _cfgValues[key] = value;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] Error reading {path}: {ex.Message}");
            }
        }

        private static string FindCfgPath()
        {
            // Next to executable (built server)
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string path1 = Path.Combine(exeDir, "server.cfg");
            if (File.Exists(path1)) return path1;

            // One level up (Unity _Data folder)
            string parentDir = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar));
            if (parentDir != null)
            {
                string path2 = Path.Combine(parentDir, "server.cfg");
                if (File.Exists(path2)) return path2;
            }

            // Unity Assets folder (editor)
            string assetsPath = Path.Combine(Application.dataPath, "server.cfg");
            if (File.Exists(assetsPath)) return assetsPath;

            // Unity project root (one level above Assets)
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (projectRoot != null)
            {
                string projPath = Path.Combine(projectRoot, "server.cfg");
                if (File.Exists(projPath)) return projPath;
            }

            // Working directory
            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "server.cfg");
            if (File.Exists(cwdPath)) return cwdPath;

            return null;
        }

        // ═══ SQLITE — dynamic settings ═══

        private static void LoadFromDatabase()
        {
            try
            {
                using (var conn = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(conn,
                        @"CREATE TABLE IF NOT EXISTS server_settings (
                            key TEXT PRIMARY KEY NOT NULL,
                            value TEXT NOT NULL,
                            updated_at TEXT DEFAULT CURRENT_TIMESTAMP
                        )");

                    using (var reader = Database.GameDatabase.ExecuteReader(conn,
                        "SELECT key, value FROM server_settings"))
                    {
                        int count = 0;
                        while (reader.Read())
                        {
                            _dbValues[reader.GetString(0)] = reader.GetString(1);
                            count++;
                        }
                        if (count > 0)
                            Debug.LogError($"[CONFIG] Loaded {count} DB overrides");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] DB load error (non-fatal): {ex.Message}");
            }
        }

        private static void SaveToDatabase(string key, string value)
        {
            try
            {
                using (var conn = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(conn,
                        "INSERT OR REPLACE INTO server_settings (key, value, updated_at) VALUES (@key, @value, datetime('now'))",
                        ("@key", key), ("@value", value));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] DB save error: {ex.Message}");
            }
        }

        private static void RemoveFromDatabase(string key)
        {
            try
            {
                using (var conn = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(conn,
                        "DELETE FROM server_settings WHERE key = @key",
                        ("@key", key));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONFIG] DB remove error: {ex.Message}");
            }
        }

        private static void LogAllSettings()
        {
            Debug.LogError("[CONFIG] ═══════════════════════════════════════");
            Debug.LogError("[CONFIG] SERVER SETTINGS:");
            Debug.LogError($"[CONFIG]   serverName = {GetString("serverName", "Dungeon Runners")}");
            Debug.LogError($"[CONFIG]   maxPlayers = {Get("maxPlayers", 50)}");
            Debug.LogError($"[CONFIG]   startZone = {GetString("startZone", "tutorial")}");
            Debug.LogError($"[CONFIG]   experienceMod = {GetFloat("experienceMod", 5.0f)}");
            Debug.LogError($"[CONFIG]   xpMultiplier = {GetFloat("xpMultiplier", 1.0f)}");
            Debug.LogError($"[CONFIG]   enableFreePlayerXP = {GetBool("enableFreePlayerXP", true)}");
            Debug.LogError($"[CONFIG]   maxLevel = {Get("maxLevel", 100)}");
            Debug.LogError($"[CONFIG]   welcomeMessage = {GetString("welcomeMessage", "Welcome!")}");
            int dbCount = _dbValues.Count;
            if (dbCount > 0)
            {
                Debug.LogError($"[CONFIG]   DB overrides ({dbCount}):");
                foreach (var kvp in _dbValues)
                    Debug.LogError($"[CONFIG]     {kvp.Key} = {kvp.Value}");
            }
            Debug.LogError("[CONFIG] ═══════════════════════════════════════");
        }
    }
}