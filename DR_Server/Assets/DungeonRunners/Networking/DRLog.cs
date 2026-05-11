// ═══════════════════════════════════════════════════════════════════════════════
// DRLog.cs — Centralized logging with per-category toggles
// ═══════════════════════════════════════════════════════════════════════════════
//
// Usage:
//   DRLog.Social("Sent roster");           // [SOCIAL] Sent roster
//   DRLog.Chat("Relayed to 3 players");    // [CHAT] Relayed to 3 players
//   DRLog.Net("Compressed 128 bytes");     // [NET] Compressed 128 bytes
//
// Toggle in chat:
//   @log                — show all category states
//   @log all on         — enable everything
//   @log all off        — disable everything (production mode)
//   @log social on      — enable social only
//   @log combat off     — disable combat
//   @log social combat  — toggle social and combat
//
// Toggle in code:
//   DRLog.Enable(DRLog.Cat.Social);
//   DRLog.Disable(DRLog.Cat.Combat);
//   DRLog.SetAll(false);  // production mode
//
// server.cfg:
//   enableDebugLog = false              — kill ALL logging (production)
//   logCategories = combat,inventory    — only these categories
//   logCategories = all                 — everything (default)
//   logCategories = none                — DRLog off, Unity errors still show
//
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Networking
{
    public static class DRLog
    {
        [Flags]
        public enum Cat
        {
            None = 0,
            Social = 1 << 0,   // Channel 0x0C, friends, ignore, who
            Chat = 1 << 1,   // Chat relay, messages
            Combat = 1 << 2,   // Damage, kills, XP, aggro
            Zone = 1 << 3,   // Zone transitions, spawns, mob spawning
            Net = 1 << 4,   // Packet hex dumps, compression, channels
            Entity = 1 << 5,   // Entity spawn/despawn, multiplayer sync
            Group = 1 << 6,   // Group/party system
            Quest = 1 << 7,   // Quest system
            Inventory = 1 << 8,   // Items, equipment, merchants
            Skill = 1 << 9,   // Skills, hotbar, trainer
            Admin = 1 << 10,  // Admin commands
            Auth = 1 << 11,  // Auth server, login
            All = ~0
        }

        // Default: everything on for development
        private static Cat _enabled = Cat.All;
        private static bool _initializedFromConfig = false;

        // Category name lookup for @log command
        private static readonly Dictionary<string, Cat> _nameMap = new Dictionary<string, Cat>(StringComparer.OrdinalIgnoreCase)
        {
            { "social",    Cat.Social },
            { "chat",      Cat.Chat },
            { "combat",    Cat.Combat },
            { "zone",      Cat.Zone },
            { "net",       Cat.Net },
            { "entity",    Cat.Entity },
            { "group",     Cat.Group },
            { "quest",     Cat.Quest },
            { "inventory", Cat.Inventory },
            { "inv",       Cat.Inventory },
            { "skill",     Cat.Skill },
            { "admin",     Cat.Admin },
            { "auth",      Cat.Auth },
            { "all",       Cat.All },
        };

        // ═══ Core log methods ═══
        // These bypass Debug.unityLogger.logEnabled so they work even when master is off.
        // This lets @log off silence all old code while @log social on still works.

        public static void Log(Cat cat, string tag, string msg)
        {
            if ((_enabled & cat) != 0)
            {
                bool wasEnabled = Debug.unityLogger.logEnabled;
                Debug.unityLogger.logEnabled = true;
                Debug.Log($"[{tag}] {msg}");
                Debug.unityLogger.logEnabled = wasEnabled;
            }
        }

        public static void Warn(Cat cat, string tag, string msg)
        {
            if ((_enabled & cat) != 0)
            {
                bool wasEnabled = Debug.unityLogger.logEnabled;
                Debug.unityLogger.logEnabled = true;
                Debug.LogWarning($"[{tag}] {msg}");
                Debug.unityLogger.logEnabled = wasEnabled;
            }
        }

        public static void Error(Cat cat, string tag, string msg)
        {
            if ((_enabled & cat) != 0)
            {
                bool wasEnabled = Debug.unityLogger.logEnabled;
                Debug.unityLogger.logEnabled = true;
                Debug.LogError($"[{tag}] {msg}");
                Debug.unityLogger.logEnabled = wasEnabled;
            }
        }

        // ═══ Convenience methods — one per category ═══

        public static void Social(string msg) => Warn(Cat.Social, "SOCIAL", msg);
        public static void Chat(string msg) => Log(Cat.Chat, "CHAT", msg);
        public static void Combat(string msg) => Log(Cat.Combat, "COMBAT", msg);
        public static void Zone(string msg) => Log(Cat.Zone, "ZONE", msg);
        public static void Net(string msg) => Log(Cat.Net, "NET", msg);
        public static void Entity(string msg) => Log(Cat.Entity, "ENTITY", msg);
        public static void Group(string msg) => Log(Cat.Group, "GROUP", msg);
        public static void Quest(string msg) => Log(Cat.Quest, "QUEST", msg);
        public static void Inv(string msg) => Log(Cat.Inventory, "INV", msg);
        public static void Skill(string msg) => Log(Cat.Skill, "SKILL", msg);
        public static void Admin(string msg) => Log(Cat.Admin, "ADMIN", msg);
        public static void Auth(string msg) => Log(Cat.Auth, "AUTH", msg);

        // ═══ Toggle API ═══

        public static void Enable(Cat cat) => _enabled |= cat;
        public static void Disable(Cat cat) => _enabled &= ~cat;
        public static void Toggle(Cat cat) => _enabled ^= cat;
        public static bool IsEnabled(Cat cat) => (_enabled & cat) != 0;

        public static void SetAll(bool on)
        {
            _enabled = on ? Cat.All : Cat.None;
        }

        /// <summary>
        /// Master kill switch — disables ALL Unity Debug output (Debug.Log, LogWarning, LogError).
        /// Affects everything, not just DRLog calls. Use for production.
        /// </summary>
        public static void SetMasterLog(bool on)
        {
            Debug.unityLogger.logEnabled = on;
        }

        /// <summary>
        /// Initialize logging state from server.cfg / ServerSettings.
        /// Call once at startup AFTER ServerSettings.Load().
        /// @reload re-calls this to pick up changes.
        ///
        /// server.cfg keys:
        ///   enableDebugLog = true/false       (master switch — matches your existing cfg key)
        ///   logCategories = all               (comma-separated: social,chat,combat,zone,net,entity,group,quest,inventory,skill,admin,auth,all,none)
        ///
        /// Examples:
        ///   enableDebugLog = false                         → all logging off (production)
        ///   enableDebugLog = true                          → all logging on (default)
        ///   logCategories = combat,inventory,zone          → only those three
        ///   logCategories = none                           → DRLog categories off, Unity still active
        ///   logCategories = all                            → everything on (default)
        /// </summary>
        public static void InitFromConfig()
        {
            _initializedFromConfig = true;

            bool masterEnabled = DungeonRunners.Core.ServerSettings.GetBool("enableDebugLog", true);
            if (!masterEnabled)
            {
                SetMasterLog(false);
                SetAll(false);
                Debug.LogError("[DRLog] Logging DISABLED by server.cfg (enableDebugLog=false)");
                return;
            }

            SetMasterLog(true);
            DungeonRunners.Core.RuntimeEvidenceManager.SetFocusedUnityLogFilter(
                DungeonRunners.Core.ServerSettings.GetBool("focusedDebugLog", true));

            string categories = DungeonRunners.Core.ServerSettings.GetString("logCategories", "all").Trim().ToLower();
            if (string.IsNullOrEmpty(categories) || categories == "all")
            {
                SetAll(true);
                Debug.LogError("[DRLog] Logging: ALL categories ON");
            }
            else if (categories == "none")
            {
                SetAll(false);
                Debug.LogError("[DRLog] Logging: ALL DRLog categories OFF");
            }
            else
            {
                // Parse comma-separated category list
                SetAll(false);
                string[] parts = categories.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    string trimmed = part.Trim();
                    if (_nameMap.TryGetValue(trimmed, out Cat cat))
                    {
                        Enable(cat);
                    }
                }
                Debug.LogError($"[DRLog] Logging categories from config: {categories}");
            }
        }

        // ═══ @log chat command handler ═══
        // Returns response string to send back to player
        public static string HandleLogCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                // Show current state
                return GetStatusString();
            }

            string[] parts = args.Trim().ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // @log off / @log on — master kill switch for ALL Unity logging
            if (parts.Length == 1 && parts[0] == "off")
            {
                SetMasterLog(false);
                SetAll(false);
                return "ALL logging DISABLED (master off)";
            }
            if (parts.Length == 1 && parts[0] == "on")
            {
                SetMasterLog(true);
                SetAll(true);
                return "ALL logging ENABLED (master on)";
            }

            // @log all on / @log all off — DRLog categories only
            if (parts.Length == 2 && parts[0] == "all")
            {
                if (parts[1] == "on") { SetAll(true); SetMasterLog(true); return "Logging: ALL ON"; }
                if (parts[1] == "off") { SetAll(false); return "DRLog categories OFF (Unity logging still active)"; }
            }

            // @log social on / @log combat off
            if (parts.Length == 2 && _nameMap.ContainsKey(parts[0]))
            {
                Cat cat = _nameMap[parts[0]];
                if (parts[1] == "on") { Enable(cat); return $"Logging: {parts[0]} ON"; }
                if (parts[1] == "off") { Disable(cat); return $"Logging: {parts[0]} OFF"; }
            }

            // @log social combat — toggle listed categories
            string result = "";
            foreach (var part in parts)
            {
                if (_nameMap.TryGetValue(part, out Cat cat))
                {
                    Toggle(cat);
                    result += $"{part}={IsEnabled(cat)} ";
                }
                else
                {
                    result += $"{part}=? ";
                }
            }

            return string.IsNullOrEmpty(result) ? GetStatusString() : $"Logging: {result.Trim()}";
        }

        private static string GetStatusString()
        {
            bool master = Debug.unityLogger.logEnabled;
            if (!master) return "Logging: MASTER OFF (type @log on to enable)";
            if (_enabled == Cat.All) return "Logging: ALL ON";
            if (_enabled == Cat.None) return "Logging: DRLog categories OFF (Unity still active — @log off to kill all)";

            var on = new List<string>();
            var off = new List<string>();
            foreach (var kvp in _nameMap)
            {
                if (kvp.Key == "all" || kvp.Key == "inv") continue; // skip aliases
                if ((_enabled & kvp.Value) != 0)
                    on.Add(kvp.Key);
                else
                    off.Add(kvp.Key);
            }
            return $"ON: {string.Join(", ", on)} | OFF: {string.Join(", ", off)}";
        }
    }
}
