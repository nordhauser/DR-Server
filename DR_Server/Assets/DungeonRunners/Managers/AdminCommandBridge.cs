using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Database;
using DungeonRunners.Networking;
using Debug = UnityEngine.Debug;

namespace DungeonRunners.Managers
{
    public class AdminCommandBridge
    {
        public static readonly AdminCommandBridge Instance = new AdminCommandBridge();

        private float _lastPollTime = 0;
        private const float POLL_INTERVAL = 2.0f;
        private float _lastMetricTime = 0;
        private const float METRIC_INTERVAL = 60.0f;

        public Action<RRConnection, string> SendSystemMessage;
        public Func<string, RRConnection> FindConnectionByName;
        public Func<Dictionary<int, RRConnection>> GetConnections;
        public Action<RRConnection, string> ChangeZone;

        public bool MaintenanceMode { get; private set; } = false;
        public float XPBoostMultiplier { get; private set; } = 1.0f;
        public float GoldBoostMultiplier { get; private set; } = 1.0f;
        public DateTime? XPBoostExpiry { get; private set; } = null;
        public DateTime? GoldBoostExpiry { get; private set; } = null;

        public void InitTables()
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS admin_commands (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, command TEXT NOT NULL, args TEXT,
                        created_at TEXT DEFAULT (datetime('now')), executed INTEGER DEFAULT 0, result TEXT)");
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS chat_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT DEFAULT (datetime('now')),
                        sender TEXT NOT NULL, channel TEXT DEFAULT 'say', message TEXT NOT NULL, zone TEXT)");
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS activity_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT DEFAULT (datetime('now')),
                        event_type TEXT NOT NULL, player TEXT, details TEXT, admin TEXT)");
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS server_metrics (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT DEFAULT (datetime('now')),
                        player_count INTEGER DEFAULT 0, uptime_seconds INTEGER DEFAULT 0)");
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS ban_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT DEFAULT (datetime('now')),
                        username TEXT NOT NULL, action TEXT NOT NULL, reason TEXT, admin TEXT)");
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS scheduled_announces (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, message TEXT NOT NULL,
                        color TEXT DEFAULT '#FF4444', interval_minutes INTEGER DEFAULT 30,
                        enabled INTEGER DEFAULT 1, last_sent TEXT)");
                    Debug.LogError("[ADMIN-BRIDGE] Tables initialized");
                }
            }
            catch (Exception ex) { Debug.LogError($"[ADMIN-BRIDGE] InitTables failed: {ex.Message}"); }
        }

        public void PollCommands(float time)
        {
            if (time - _lastPollTime >= POLL_INTERVAL)
            {
                _lastPollTime = time;
                ProcessPendingCommands();
                CheckScheduledAnnouncements();
                CheckBoostExpiry();
            }
            if (time - _lastMetricTime >= METRIC_INTERVAL)
            {
                _lastMetricTime = time;
                RecordMetrics(time);
            }
        }

        private void ProcessPendingCommands()
        {
            try
            {
                var cmds = new List<(int id, string command, string args)>();
                using (var conn = GameDatabase.GetConnection())
                {
                    using (var reader = GameDatabase.ExecuteReader(conn, "SELECT id, command, args FROM admin_commands WHERE executed = 0 ORDER BY id"))
                    {
                        while (reader.Read())
                            cmds.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
                    }
                }
                foreach (var cmd in cmds)
                {
                    string result = "ok";
                    try { result = ExecuteCommand(cmd.command, cmd.args); }
                    catch (Exception ex) { result = $"error: {ex.Message}"; }
                    using (var conn = GameDatabase.GetConnection())
                        GameDatabase.ExecuteNonQuery(conn, "UPDATE admin_commands SET executed = 1, result = @r WHERE id = @id",
                            ("@r", result), ("@id", cmd.id));
                }
            }
            catch (Exception ex) { Debug.LogError($"[ADMIN-BRIDGE] PollCommands error: {ex.Message}"); }
        }

        private string ExecuteCommand(string command, string argsJson)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(argsJson))
            {
                foreach (var pair in argsJson.Split('|'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) args[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
                }
            }
            string GetArg(string key, string def = "") => args.TryGetValue(key, out var v) ? v : def;

            switch (command.ToLower())
            {
                case "broadcast":
                    {
                        string msg = GetArg("message");
                        string color = GetArg("color", "#FF4444");
                        string effect = GetArg("effect", "glow");
                        string formatted = UnityGameServer.WrapChatColor($"[ANNOUNCE] {msg}", color, effect);
                        int sent = 0;
                        foreach (var kvp in GetConnections())
                        {
                            var c = kvp.Value;
                            if (c.IsConnected && !string.IsNullOrEmpty(c.LoginName))
                            { SendSystemMessage(c, formatted); sent++; }
                        }
                        LogActivity("broadcast", null, $"Message: {msg}", GetArg("admin", "panel"));
                        return $"sent to {sent} players";
                    }
                case "whisper":
                    {
                        string target = GetArg("player");
                        string msg = GetArg("message");
                        string color = GetArg("color", "#FFCC66");
                        var c = FindConnectionByName(target);
                        if (c == null) return $"player '{target}' not found";
                        SendSystemMessage(c, UnityGameServer.WrapChatColor($"[Admin] {msg}", color, ""));
                        LogActivity("whisper", target, $"Message: {msg}", GetArg("admin", "panel"));
                        return "sent";
                    }
                case "kick":
                    {
                        string target = GetArg("player");
                        string reason = GetArg("reason", "Kicked by admin");
                        var c = FindConnectionByName(target);
                        if (c == null) return $"player '{target}' not found or offline";
                        SendSystemMessage(c, $"[Server] You have been kicked: {reason}");
                        c.Disconnect();
                        LogActivity("kick", target, $"Reason: {reason}", GetArg("admin", "panel"));
                        return "kicked";
                    }
                case "ban":
                    {
                        string target = GetArg("player");
                        string reason = GetArg("reason", "Banned by admin");
                        string admin = GetArg("admin", "panel");
                        using (var conn = GameDatabase.GetConnection())
                            GameDatabase.ExecuteNonQuery(conn, "UPDATE accounts SET is_banned = 1 WHERE username = @n COLLATE NOCASE", ("@n", target));
                        LogBan(target, "ban", reason, admin);
                        LogActivity("ban", target, $"Reason: {reason}", admin);
                        var c = FindConnectionByName(target);
                        if (c != null) { SendSystemMessage(c, $"[Server] You have been banned: {reason}"); c.Disconnect(); }
                        return "banned" + (c != null ? " and kicked" : "");
                    }
                case "unban":
                    {
                        string target = GetArg("player");
                        string admin = GetArg("admin", "panel");
                        using (var conn = GameDatabase.GetConnection())
                            GameDatabase.ExecuteNonQuery(conn, "UPDATE accounts SET is_banned = 0 WHERE username = @n COLLATE NOCASE", ("@n", target));
                        LogBan(target, "unban", "Unbanned", admin);
                        LogActivity("unban", target, "Unbanned", admin);
                        return "unbanned";
                    }
                case "teleport":
                    {
                        string target = GetArg("player");
                        string zone = GetArg("zone");
                        var c = FindConnectionByName(target);
                        if (c == null) return $"player '{target}' not found or offline";
                        ChangeZone(c, zone);
                        LogActivity("teleport", target, $"Zone: {zone}", GetArg("admin", "panel"));
                        return $"teleported to {zone}";
                    }
                case "boost_xp":
                    {
                        float mult = float.Parse(GetArg("multiplier", "2.0"));
                        int minutes = int.Parse(GetArg("minutes", "60"));
                        XPBoostMultiplier = mult;
                        XPBoostExpiry = DateTime.Now.AddMinutes(minutes);
                        string msg = UnityGameServer.WrapChatColor($"[EVENT] {mult}x XP Boost active for {minutes} minutes!", "#FFD666", "glow");
                        foreach (var kvp in GetConnections())
                        { var c = kvp.Value; if (c.IsConnected && !string.IsNullOrEmpty(c.LoginName)) SendSystemMessage(c, msg); }
                        LogActivity("boost_xp", null, $"{mult}x for {minutes}min", GetArg("admin", "panel"));
                        return $"{mult}x XP for {minutes}min";
                    }
                case "boost_gold":
                    {
                        float mult = float.Parse(GetArg("multiplier", "2.0"));
                        int minutes = int.Parse(GetArg("minutes", "60"));
                        GoldBoostMultiplier = mult;
                        GoldBoostExpiry = DateTime.Now.AddMinutes(minutes);
                        string msg = UnityGameServer.WrapChatColor($"[EVENT] {mult}x Gold Boost active for {minutes} minutes!", "#FFD666", "glow");
                        foreach (var kvp in GetConnections())
                        { var c = kvp.Value; if (c.IsConnected && !string.IsNullOrEmpty(c.LoginName)) SendSystemMessage(c, msg); }
                        LogActivity("boost_gold", null, $"{mult}x for {minutes}min", GetArg("admin", "panel"));
                        return $"{mult}x Gold for {minutes}min";
                    }
                case "boost_stop":
                    {
                        string boostType = GetArg("type", "all");
                        if (boostType == "xp" || boostType == "all") { XPBoostMultiplier = 1.0f; XPBoostExpiry = null; }
                        if (boostType == "gold" || boostType == "all") { GoldBoostMultiplier = 1.0f; GoldBoostExpiry = null; }
                        string msg = UnityGameServer.WrapChatColor("[EVENT] Boost event has ended.", "#999999", "");
                        foreach (var kvp in GetConnections())
                        { var c = kvp.Value; if (c.IsConnected && !string.IsNullOrEmpty(c.LoginName)) SendSystemMessage(c, msg); }
                        LogActivity("boost_stop", null, boostType, GetArg("admin", "panel"));
                        return "boost stopped";
                    }
                case "maintenance":
                    {
                        bool enable = GetArg("enable", "true") == "true";
                        MaintenanceMode = enable;
                        if (enable)
                        {
                            string msg = UnityGameServer.WrapChatColor("[SERVER] Maintenance mode enabled. No new logins allowed.", "#FF4444", "glow");
                            foreach (var kvp in GetConnections())
                            { var c = kvp.Value; if (c.IsConnected && !string.IsNullOrEmpty(c.LoginName)) SendSystemMessage(c, msg); }
                        }
                        LogActivity("maintenance", null, enable ? "enabled" : "disabled", GetArg("admin", "panel"));
                        return enable ? "maintenance ON" : "maintenance OFF";
                    }
                default:
                    return $"unknown command: {command}";
            }
        }

        private void CheckBoostExpiry()
        {
            if (XPBoostExpiry.HasValue && DateTime.Now >= XPBoostExpiry.Value)
            { XPBoostMultiplier = 1.0f; XPBoostExpiry = null; }
            if (GoldBoostExpiry.HasValue && DateTime.Now >= GoldBoostExpiry.Value)
            { GoldBoostMultiplier = 1.0f; GoldBoostExpiry = null; }
        }

        private void CheckScheduledAnnouncements()
        {
            try
            {
                var toSend = new List<(int id, string message, string color)>();
                using (var conn = GameDatabase.GetConnection())
                {
                    using (var reader = GameDatabase.ExecuteReader(conn, "SELECT id, message, color, interval_minutes, last_sent FROM scheduled_announces WHERE enabled = 1"))
                    {
                        while (reader.Read())
                        {
                            int id = reader.GetInt32(0);
                            string msg = reader.GetString(1);
                            string color = reader.IsDBNull(2) ? "#FF4444" : reader.GetString(2);
                            int interval = reader.GetInt32(3);
                            string lastSent = reader.IsDBNull(4) ? "" : reader.GetString(4);
                            bool shouldSend = string.IsNullOrEmpty(lastSent);
                            if (!shouldSend && DateTime.TryParse(lastSent, out DateTime lastDt))
                                shouldSend = (DateTime.UtcNow - lastDt).TotalMinutes >= interval;
                            if (shouldSend) toSend.Add((id, msg, color));
                        }
                    }
                }
                foreach (var sa in toSend)
                {
                    string formatted = UnityGameServer.WrapChatColor(sa.message, sa.color, "");
                    foreach (var kvp in GetConnections())
                    { var c = kvp.Value; if (c.IsConnected && !string.IsNullOrEmpty(c.LoginName)) SendSystemMessage(c, formatted); }
                    using (var conn = GameDatabase.GetConnection())
                        GameDatabase.ExecuteNonQuery(conn, "UPDATE scheduled_announces SET last_sent = datetime('now') WHERE id = @id", ("@id", sa.id));
                }
            }
            catch { }
        }

        public void LogChat(string sender, string channel, string message, string zone)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn, "INSERT INTO chat_log (sender, channel, message, zone) VALUES (@s, @c, @m, @z)",
                        ("@s", sender), ("@c", channel), ("@m", message), ("@z", zone ?? ""));
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM chat_log WHERE id NOT IN (SELECT id FROM chat_log ORDER BY id DESC LIMIT 5000)");
                }
            }
            catch { }
        }

        public void LogActivity(string eventType, string player, string details, string admin = null)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                    GameDatabase.ExecuteNonQuery(conn, "INSERT INTO activity_log (event_type, player, details, admin) VALUES (@e, @p, @d, @a)",
                        ("@e", eventType), ("@p", player ?? ""), ("@d", details ?? ""), ("@a", admin ?? ""));
            }
            catch { }
        }

        public void LogBan(string username, string action, string reason, string admin)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                    GameDatabase.ExecuteNonQuery(conn, "INSERT INTO ban_log (username, action, reason, admin) VALUES (@u, @a, @r, @d)",
                        ("@u", username), ("@a", action), ("@r", reason ?? ""), ("@d", admin ?? ""));
            }
            catch { }
        }

        private void RecordMetrics(float uptime)
        {
            try
            {
                int playerCount = 0;
                foreach (var kvp in GetConnections())
                    if (kvp.Value.IsConnected && !string.IsNullOrEmpty(kvp.Value.LoginName)) playerCount++;
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn, "INSERT INTO server_metrics (player_count, uptime_seconds) VALUES (@p, @u)",
                        ("@p", playerCount), ("@u", (int)uptime));
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM server_metrics WHERE id NOT IN (SELECT id FROM server_metrics ORDER BY id DESC LIMIT 1440)");
                }
            }
            catch { }
        }
    }
}