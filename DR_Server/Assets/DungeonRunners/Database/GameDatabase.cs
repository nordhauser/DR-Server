using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Database
{
    /// <summary>
    /// SQLite database connection and helpers. NO JSON anywhere.
    /// All data read directly from proper columns by DatabaseLoader.
    /// </summary>
    public static class GameDatabase
    {
        private static string _dbPath;
        private static string _connectionString;
        private static bool _initialized = false;

        public static string DbPath => _dbPath;

        public static void Initialize()
        {
            if (_initialized) return;
#if UNITY_EDITOR
            _dbPath = Path.Combine(Application.dataPath, "DungeonRunners", "Database", "dungeon_runners.db");
#else
            _dbPath = Path.Combine(Application.dataPath, "..", "Database", "dungeon_runners.db");
#endif
            string dir = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _connectionString = $"URI=file:{_dbPath}";
            Debug.LogError($"[DB] SQLite path: {_dbPath}");
            CreatePlayerTables();
            _initialized = true;
            Debug.LogError("[DB] SQLite initialized — direct column reads, zero JSON");
        }

        public static SqliteConnection GetConnection()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = ON";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        private static void CreatePlayerTables()
        {
            using (var conn = GetConnection())
            {
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS accounts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    password_hash TEXT NOT NULL, salt TEXT NOT NULL, email TEXT DEFAULT '',
                    is_member INTEGER DEFAULT 0, is_banned INTEGER DEFAULT 0, is_admin INTEGER DEFAULT 0,
                    created_at TEXT DEFAULT (datetime('now')), last_login TEXT DEFAULT (datetime('now')))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS characters (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE, class_name TEXT NOT NULL DEFAULT 'Fighter',
                    avatar_class TEXT DEFAULT '', level INTEGER DEFAULT 1, experience INTEGER DEFAULT 0,
                    gold INTEGER DEFAULT 100, skin INTEGER DEFAULT 0, face INTEGER DEFAULT 0,
                    face_feature INTEGER DEFAULT 0, hair INTEGER DEFAULT 0, hair_color INTEGER DEFAULT 0,
                    current_zone TEXT DEFAULT 'tutorial', zone_id INTEGER DEFAULT 0,
                    position_x REAL DEFAULT 0, position_y REAL DEFAULT 0, position_z REAL DEFAULT 0,
                    current_hp INTEGER DEFAULT 0, current_mana INTEGER DEFAULT 0,
                    created_at TEXT DEFAULT (datetime('now')),
                    FOREIGN KEY (account_id) REFERENCES accounts(id))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS character_equipment (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    slot TEXT NOT NULL, gc_class TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, slot))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS character_inventory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    gc_class TEXT NOT NULL, slot_x INTEGER DEFAULT 0, slot_y INTEGER DEFAULT 0,
                    count INTEGER DEFAULT 1,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE)");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS character_skills (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    skill_gc_class TEXT NOT NULL, level INTEGER DEFAULT 1, hotbar_slot INTEGER DEFAULT -1,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, skill_gc_class))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS character_quests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    quest_id TEXT NOT NULL, quest_giver_id TEXT DEFAULT '',
                    accepted_at TEXT DEFAULT (datetime('now')), status TEXT DEFAULT 'active',
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, quest_id))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS quest_objectives (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    quest_id TEXT NOT NULL, objective_name TEXT NOT NULL,
                    type TEXT DEFAULT '', target TEXT DEFAULT '', label TEXT DEFAULT '',
                    required INTEGER DEFAULT 0, current INTEGER DEFAULT 0,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE)");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS completed_quests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    quest_id TEXT NOT NULL, completed_at TEXT DEFAULT (datetime('now')),
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, quest_id))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS character_checkpoints (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, character_id INTEGER NOT NULL,
                    checkpoint_id TEXT NOT NULL,
                    FOREIGN KEY (character_id) REFERENCES characters(id) ON DELETE CASCADE,
                    UNIQUE(character_id, checkpoint_id))");
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS character_modifiers (
                    character_id INTEGER NOT NULL,
                    gc_type TEXT NOT NULL,
                    modifier_id INTEGER NOT NULL,
                    level INTEGER DEFAULT 0,
                    power_level INTEGER DEFAULT 0,
                    duration_remaining INTEGER DEFAULT 0,
                    source_is_self INTEGER DEFAULT 1,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (character_id, modifier_id))");
                ExecuteNonQuery(conn, "PRAGMA journal_mode=WAL");
                ExecuteNonQuery(conn, "PRAGMA foreign_keys=ON");

                // Dropped items persistence table
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS dropped_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    zone TEXT NOT NULL,
                    zone_id INTEGER NOT NULL,
                    instance_id INTEGER DEFAULT 0,
                    gc_class TEXT NOT NULL,
                    native_class TEXT DEFAULT 'Armor',
                    pos_x REAL NOT NULL,
                    pos_y REAL NOT NULL,
                    pos_z REAL NOT NULL,
                    player_level INTEGER DEFAULT 1,
                    target_slot INTEGER DEFAULT -1,
                    preset_scale_mod TEXT DEFAULT '',
                    dropped_at TEXT DEFAULT (datetime('now')),
                    dropped_by TEXT DEFAULT '')");

                // Add town portal columns (safe to run multiple times)
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN tp_zone TEXT DEFAULT ''"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN tp_zone_id INTEGER DEFAULT 0"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN tp_target_zone TEXT DEFAULT ''"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN tp_pos_x REAL DEFAULT 0"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN tp_pos_y REAL DEFAULT 0"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN tp_pos_z REAL DEFAULT 0"); } catch { }

                // Add rarity columns for equip level check (safe to run multiple times)
                // -1 = legacy/unknown (will use name-based detection), 0-5 = ItemRarity enum
                try { ExecuteNonQuery(conn, "ALTER TABLE character_inventory ADD COLUMN rarity INTEGER DEFAULT -1"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE character_equipment ADD COLUMN rarity INTEGER DEFAULT -1"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE dropped_items ADD COLUMN rarity INTEGER DEFAULT -1"); } catch { }

                // Add stored_level columns for fixed item levels (mythic items don't scale)
                // -1 = legacy (compute from GCClass), >= 0 = permanent fixed level
                try { ExecuteNonQuery(conn, "ALTER TABLE character_inventory ADD COLUMN stored_level INTEGER DEFAULT -1"); } catch { }

                // Container ID for bank persistence: 11 (0x0B) = main inventory (default for all existing rows).
                // 12 (0x0C) = bank page 1; 14-19 (0x0E-0x13) = bank pages 2-7. Trade container 13 (0x0D) not persisted.
                try { ExecuteNonQuery(conn, "ALTER TABLE character_inventory ADD COLUMN container_id INTEGER DEFAULT 11"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE character_equipment ADD COLUMN stored_level INTEGER DEFAULT -1"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE dropped_items ADD COLUMN stored_level INTEGER DEFAULT -1"); } catch { }

                // Character stat tracking columns
                Debug.LogError("[DB-MIGRATE] Adding character stat columns...");
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN max_hp INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added max_hp"); } catch { Debug.LogError("[DB-MIGRATE] max_hp already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN max_mana INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added max_mana"); } catch { Debug.LogError("[DB-MIGRATE] max_mana already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN stat_strength INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added stat_strength"); } catch { Debug.LogError("[DB-MIGRATE] stat_strength already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN stat_agility INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added stat_agility"); } catch { Debug.LogError("[DB-MIGRATE] stat_agility already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN stat_intellect INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added stat_intellect"); } catch { Debug.LogError("[DB-MIGRATE] stat_intellect already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN stat_endurance INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added stat_endurance"); } catch { Debug.LogError("[DB-MIGRATE] stat_endurance already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN last_respec_time INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added last_respec_time"); } catch { Debug.LogError("[DB-MIGRATE] last_respec_time already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN respec_count INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added respec_count"); } catch { Debug.LogError("[DB-MIGRATE] respec_count already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN pvp_wins INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added pvp_wins"); } catch { Debug.LogError("[DB-MIGRATE] pvp_wins already exists"); }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN pvp_rating INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added pvp_rating"); } catch { Debug.LogError("[DB-MIGRATE] pvp_rating already exists"); }

                // Active session tracking for admin panel
                try { ExecuteNonQuery(conn, "ALTER TABLE accounts ADD COLUMN current_character_id INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added current_character_id"); } catch { }
                // Clear all sessions on startup (server just started, nobody is online)
                try { ExecuteNonQuery(conn, "UPDATE accounts SET current_character_id = 0"); } catch { }

                // Pending item grants from admin panel
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS pending_item_grants (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    character_id INTEGER NOT NULL,
                    gc_class TEXT NOT NULL,
                    count INTEGER DEFAULT 1,
                    width INTEGER DEFAULT 1,
                    height INTEGER DEFAULT 1,
                    rarity INTEGER DEFAULT 1,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                )");

                // Posses: created via /posse create, one row per posse. Member→posse link
                // lives on characters.posse_id.
                ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS posses (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    founder_character_id INTEGER NOT NULL,
                    founded_at TEXT DEFAULT (datetime('now')),
                    gold_paid INTEGER DEFAULT 0,
                    FOREIGN KEY (founder_character_id) REFERENCES characters(id))");
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN posse_id INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added posse_id"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN posse_join_cooldown INTEGER DEFAULT 0"); Debug.LogError("[DB-MIGRATE] Added posse_join_cooldown"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE characters ADD COLUMN posse_rank_id INTEGER DEFAULT 1"); Debug.LogError("[DB-MIGRATE] Added posse_rank_id"); } catch { }
                // Native posse rank scale is 1..10. Server explicitly stamps 10 for founders on /posse create
                // and 1 for new joiners on accept-invite. Don't blanket-rewrite existing rows.
                try { ExecuteNonQuery(conn, "ALTER TABLE posses ADD COLUMN motd TEXT DEFAULT ''"); Debug.LogError("[DB-MIGRATE] Added posses.motd"); } catch { }
                try { ExecuteNonQuery(conn, "ALTER TABLE posses ADD COLUMN description TEXT DEFAULT ''"); Debug.LogError("[DB-MIGRATE] Added posses.description"); } catch { }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SQL HELPERS — used by DatabaseLoader + other code
        // ═══════════════════════════════════════════════════════════
        public static void ExecuteNonQuery(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.name, p.value);
                cmd.ExecuteNonQuery();
            }
        }
        public static object ExecuteScalar(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.name, p.value);
                return cmd.ExecuteScalar();
            }
        }
        public static SqliteDataReader ExecuteReader(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var p in parameters) cmd.Parameters.AddWithValue(p.name, p.value);
            return cmd.ExecuteReader();
        }
        public static int GetInt(SqliteDataReader r, string col, int fallback = 0)
        {
            try { int o = r.GetOrdinal(col); return r.IsDBNull(o) ? fallback : r.GetInt32(o); }
            catch { return fallback; }
        }
        public static string GetString(SqliteDataReader r, string col, string fallback = "")
        {
            try { int o = r.GetOrdinal(col); return r.IsDBNull(o) ? fallback : r.GetString(o); }
            catch { return fallback; }
        }
        public static float GetFloat(SqliteDataReader r, string col, float fallback = 0f)
        {
            try { int o = r.GetOrdinal(col); return r.IsDBNull(o) ? fallback : (float)r.GetDouble(o); }
            catch { return fallback; }
        }
        public static uint GetUInt(SqliteDataReader r, string col, uint fallback = 0)
        {
            try { int o = r.GetOrdinal(col); return r.IsDBNull(o) ? fallback : (uint)r.GetInt64(o); }
            catch { return fallback; }
        }
    }
}