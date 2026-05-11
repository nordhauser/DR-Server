using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Data.Sqlite;
using UnityEngine;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Comprehensive item stat database. Parses GC files to resolve the EXACT mods
    /// each mythic item has, then calculates stat values at any player level.
    /// 
    /// Resolution chain:
    ///   Item.ModN -> ModPAL.Quality.ModN -> [N]EnhancementsPAL.AttrName -> AttributesPAL -> Pool + Attribute
    /// 
    /// Tables in dungeon_runners.db:
    ///   stat_pools           - pool formulas (base + level scaling)
    ///   item_resolved_mods   - per-item resolved attributes with pool + value_mult
    /// </summary>
    public class ItemStatDatabase
    {
        private static ItemStatDatabase _instance;
        public static ItemStatDatabase Instance => _instance ??= new ItemStatDatabase();

        public bool IsLoaded { get; private set; }

        // ═══════════════════════════════════════════════════════════════
        // DATA STRUCTURES
        // ═══════════════════════════════════════════════════════════════

        struct PoolFormula { public float Base, Scale, Divisor; }
        struct ResolvedMod
        {
            public int ModSlot;
            public string Attribute;  // e.g., "MAX_HIT_POINTS"
            public string Pool;       // e.g., "MaxPointBonusPool"
            public float ValueMult;   // 1.0, 0.5, 0.33, 0.25
        }

        // Runtime lookup tables (loaded from DB)
        private Dictionary<string, PoolFormula> _pools = new();
        private Dictionary<string, List<ResolvedMod>> _itemMods = new(StringComparer.OrdinalIgnoreCase);

        // Parsing intermediaries (used only during population, then cleared)
        private Dictionary<string, (string Attr, string Pool)> _attrMap = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _modPalRefs = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _weaponMythicRefs = new(StringComparer.OrdinalIgnoreCase);

        private string _gcDir;

        // ═══════════════════════════════════════════════════════════════
        // LOAD
        // ═══════════════════════════════════════════════════════════════

        public void Load()
        {
            try
            {
#if UNITY_EDITOR
                _gcDir = Path.Combine(Application.dataPath, "DungeonRunners", "Database", "gc");
#else
                _gcDir = Path.Combine(Application.dataPath, "..", "Database", "gc");
#endif
                if (!Directory.Exists(_gcDir))
                {
                    Debug.LogError($"[ItemStatDB] GC directory not found: {_gcDir}");
                    return;
                }

                using var conn = Database.GameDatabase.GetConnection();
                CreateTables(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM item_resolved_mods";
                    long count = (long)cmd.ExecuteScalar();
                    if (count == 0)
                    {
                        Debug.LogError("[ItemStatDB] Populating from GC files...");
                        PopulateFromGCFiles(conn);
                    }
                    else
                    {
                        Debug.LogError($"[ItemStatDB] Already populated: {count} mod entries");
                    }
                }

                LoadPools(conn);
                LoadResolvedMods(conn);
                IsLoaded = true;

                Debug.LogError($"[ItemStatDB] Loaded: {_pools.Count} pools, {_itemMods.Count} items, {_itemMods.Values.Sum(v => v.Count)} total mods");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemStatDB] Load failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // POPULATION FROM GC FILES
        // ═══════════════════════════════════════════════════════════════

        private void PopulateFromGCFiles(SqliteConnection conn)
        {
            ParseAttributesPAL();
            Debug.LogError($"[ItemStatDB] Phase 1: {_attrMap.Count} attribute mappings");

            ParseAllModPALs();
            Debug.LogError($"[ItemStatDB] Phase 2: {_modPalRefs.Count} ModPAL references");

            ParseWeaponMythicModPAL();
            Debug.LogError($"[ItemStatDB] Phase 3: {_weaponMythicRefs.Count} weapon mythic references");

            int itemCount = 0, modCount = 0;
            using var transaction = conn.BeginTransaction();

            InsertPools(conn);

            // Parse named mythic items (*MythicPAL.gc, excluding *ModPAL.gc)
            foreach (var file in Directory.GetFiles(_gcDir, "*MythicPAL.gc"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.Contains("Mod")) continue;

                var items = ParseItemFile(file, fileName, false);
                foreach (var (itemName, mods) in items)
                {
                    string fullKey = $"{fileName}.{itemName}".ToLowerInvariant();
                    var resolved = ResolveMods(mods);
                    if (resolved.Count > 0)
                    {
                        InsertResolvedMods(conn, fullKey, resolved);
                        modCount += resolved.Count;
                        itemCount++;
                    }
                }
            }

            // Parse prebuilt items in *PAL.gc files
            foreach (var file in Directory.GetFiles(_gcDir, "*PAL.gc"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.Contains("Mod") || fileName.Contains("Enhancement") ||
                    fileName.Contains("Attribute") || fileName.Contains("Mythic") ||
                    fileName.Contains("Pool") || fileName.Contains("Weapon") && fileName.EndsWith("PAL")) continue;
                // More precise: only process files that actually contain MythicPreBuilt
                string peek = File.ReadAllText(file);
                if (!peek.Contains("MythicPreBuilt")) continue;

                var items = ParseItemFile(file, fileName, true);
                foreach (var (itemName, mods) in items)
                {
                    string fullKey = $"{fileName}.{itemName}".ToLowerInvariant();
                    var resolved = ResolveMods(mods);
                    if (resolved.Count > 0)
                    {
                        InsertResolvedMods(conn, fullKey, resolved);
                        modCount += resolved.Count;
                        itemCount++;
                    }
                }
            }

            transaction.Commit();
            Debug.LogError($"[ItemStatDB] Phase 4: {itemCount} items, {modCount} resolved mod attributes stored");

            // Clear parsing intermediaries
            _attrMap.Clear();
            _modPalRefs.Clear();
            _weaponMythicRefs.Clear();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 1: Parse AttributesPAL
        // ═══════════════════════════════════════════════════════════════

        private void ParseAttributesPAL()
        {
            string path = Path.Combine(_gcDir, "AttributesPAL.gc");
            if (!File.Exists(path)) { Debug.LogError("[ItemStatDB] AttributesPAL.gc not found!"); return; }

            string content = File.ReadAllText(path).Replace("\r", "");
            string currentName = null, currentPool = null;

            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();
                var m = Regex.Match(t, @"^(\w+)\s+extends\s+PoolTables\.(\w+)");
                if (m.Success) { currentName = m.Groups[1].Value; currentPool = m.Groups[2].Value; continue; }
                var m2 = Regex.Match(t, @"Attribute\s*=\s*(\w+);");
                if (m2.Success && currentName != null) { _attrMap[currentName] = (m2.Groups[1].Value, currentPool); currentName = null; }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: Parse all ModPAL files
        // ═══════════════════════════════════════════════════════════════

        private void ParseAllModPALs()
        {
            foreach (var file in Directory.GetFiles(_gcDir, "*ModPAL.gc"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName == "WeaponMythicModPAL") continue;
                ParseSingleModPAL(file, fileName);
            }
        }

        private void ParseSingleModPAL(string filePath, string fileName)
        {
            string content = File.ReadAllText(filePath).Replace("\r", "");
            string currentQuality = null;
            string pendingSection = null;

            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();

                // Detect quality section names (bare word on its own line, { comes next line)
                // e.g., "Superior" or "Rare" or "Magic" or "Unique"
                if (!t.Contains("extends") && !t.Contains("=") && !t.Contains("//") && !t.StartsWith("*"))
                {
                    // "{" alone on a line? Confirm previous pending section
                    if (t == "{" && pendingSection != null)
                    {
                        currentQuality = pendingSection;
                        pendingSection = null;
                        continue;
                    }

                    // "SectionName {" on same line
                    if (t.EndsWith("{") && !t.Contains("Description"))
                    {
                        string section = t.TrimEnd('{', ' ', '\t');
                        if (section.Length > 0 && section.Length < 30)
                            currentQuality = section;
                        continue;
                    }

                    // Bare word — might be a section name, save as pending
                    var sectionMatch = Regex.Match(t, @"^(\w+)$");
                    if (sectionMatch.Success && t.Length < 30 && !t.Contains("Description"))
                        pendingSection = t;
                }

                // Match mod lines
                var m = Regex.Match(t, @"^(Mod\d+)\s+extends\s+(\S+)");
                if (m.Success && currentQuality != null)
                {
                    string key = $"{fileName}|{currentQuality}|{m.Groups[1].Value}";
                    _modPalRefs[key] = m.Groups[2].Value.TrimEnd('{', ' ');
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: Parse WeaponMythicModPAL
        // ═══════════════════════════════════════════════════════════════

        private void ParseWeaponMythicModPAL()
        {
            string path = Path.Combine(_gcDir, "WeaponMythicModPAL.gc");
            if (!File.Exists(path)) return;

            string content = File.ReadAllText(path).Replace("\r", "");
            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();
                var m = Regex.Match(t, @"^(\w+)\s+extends\s+(\d*EnhancementsPAL\.\w+)");
                if (m.Success)
                    _weaponMythicRefs[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: Parse Item Files
        // ═══════════════════════════════════════════════════════════════

        private List<(string Name, List<(int Slot, string Ref)>)> ParseItemFile(string filePath, string fileName, bool prebuiltOnly)
        {
            var result = new List<(string, List<(int, string)>)>();
            string content = File.ReadAllText(filePath).Replace("\r", "");
            string[] lines = content.Split('\n');

            string currentItem = null;
            var currentMods = new List<(int, string)>();

            foreach (string rawLine in lines)
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                // Check for item/prebuilt start: "SomeName extends SomeParent"
                var itemMatch = Regex.Match(t, @"^(\w+)\s+extends\s+\S+");
                if (itemMatch.Success)
                {
                    string name = itemMatch.Groups[1].Value;
                    bool isPrebuilt = name.StartsWith("MythicPreBuilt", StringComparison.OrdinalIgnoreCase);
                    bool isNamedMythic = !isPrebuilt && !name.StartsWith("Mod") && !name.StartsWith("Description") &&
                        !name.StartsWith("One") && !name.StartsWith("Two") && !name.StartsWith("Three") &&
                        !name.StartsWith("Four") && !name.StartsWith("Five") && !name.StartsWith("static") &&
                        !name.StartsWith("Base") && name != fileName;

                    bool shouldParse = prebuiltOnly ? isPrebuilt : isNamedMythic;
                    if (shouldParse)
                    {
                        // Save previous item
                        if (currentItem != null && currentMods.Count > 0)
                            result.Add((currentItem, new List<(int, string)>(currentMods)));
                        currentItem = name;
                        currentMods.Clear();
                    }
                }

                // Collect Mod lines for current item
                if (currentItem != null)
                {
                    var modMatch = Regex.Match(t, @"^Mod(\d+)\s+extends\s+(\S+)");
                    if (modMatch.Success)
                    {
                        int slot = int.Parse(modMatch.Groups[1].Value);
                        string extendsRef = modMatch.Groups[2].Value.TrimEnd('{', ' ');
                        if (!extendsRef.Contains("ItemModifier"))
                            currentMods.Add((slot, extendsRef));
                    }
                }
            }

            // Save last item
            if (currentItem != null && currentMods.Count > 0)
                result.Add((currentItem, currentMods));

            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // MOD RESOLUTION
        // ═══════════════════════════════════════════════════════════════

        private List<ResolvedMod> ResolveMods(List<(int Slot, string Ref)> mods)
        {
            var result = new List<ResolvedMod>();

            foreach (var (slot, rawRef) in mods)
            {
                string modRef = rawRef.Replace("items.modpal.", "").Replace("items.pal.", "");
                string enhancementRef = ResolveToEnhancement(modRef);
                if (enhancementRef == null) continue;

                // Parse: "[N]EnhancementsPAL.SomeName"
                var enhMatch = Regex.Match(enhancementRef, @"(\d*)EnhancementsPAL\.(\w+)");
                if (!enhMatch.Success) continue;

                int enhCount = string.IsNullOrEmpty(enhMatch.Groups[1].Value) ? 1 : int.Parse(enhMatch.Groups[1].Value);
                string enhName = enhMatch.Groups[2].Value;
                float valueMult = enhCount > 0 ? 1.0f / enhCount : 1.0f;

                // Round to avoid float issues: 0.33 for 3, 0.25 for 4, 0.5 for 2
                if (enhCount == 3) valueMult = 0.33f;
                else if (enhCount == 4) valueMult = 0.25f;
                else if (enhCount == 2) valueMult = 0.5f;

                var attrNames = SplitEnhancementName(enhName, enhCount);
                foreach (string attrName in attrNames)
                {
                    if (_attrMap.TryGetValue(attrName, out var mapping))
                    {
                        result.Add(new ResolvedMod
                        {
                            ModSlot = slot,
                            Attribute = mapping.Attr,
                            Pool = mapping.Pool,
                            ValueMult = valueMult
                        });
                    }
                    else
                    {
                        Debug.LogWarning($"[ItemStatDB] Unknown attribute: {attrName} from {enhName}");
                    }
                }
            }

            return result;
        }

        private string ResolveToEnhancement(string modRef)
        {
            if (modRef.Contains("EnhancementsPAL")) return modRef;
            if (modRef.Contains("ItemModifier") || modRef.Contains("ProcModPAL")) return null;

            // WeaponMythicModPAL.SomeName
            if (modRef.StartsWith("WeaponMythicModPAL.", StringComparison.OrdinalIgnoreCase))
            {
                string name = modRef.Substring("WeaponMythicModPAL.".Length);
                return _weaponMythicRefs.TryGetValue(name, out string r) ? r : null;
            }

            // ModPAL.Quality.ModN (e.g., "AxeModPAL.Rare.Mod2")
            string[] parts = modRef.Split('.');
            if (parts.Length >= 3)
            {
                string key = $"{parts[0]}|{parts[1]}|{parts[2]}";
                if (_modPalRefs.TryGetValue(key, out string enhRef))
                    return enhRef.Contains("EnhancementsPAL") ? enhRef : ResolveToEnhancement(enhRef);
            }

            return null;
        }

        private List<string> SplitEnhancementName(string name, int expectedCount)
        {
            var result = new List<string>();
            if (expectedCount <= 1) { result.Add(name); return result; }

            // Split on '_' boundaries between known attribute names
            // Strategy: greedily match known attribute names from left to right
            string remaining = name;
            while (remaining.Length > 0 && result.Count < expectedCount)
            {
                bool found = false;
                // Try longest possible prefix that matches a known attribute
                for (int len = remaining.Length; len > 0; len--)
                {
                    string candidate = remaining.Substring(0, len);
                    if (_attrMap.ContainsKey(candidate))
                    {
                        result.Add(candidate);
                        remaining = remaining.Substring(len);
                        if (remaining.StartsWith("_")) remaining = remaining.Substring(1);
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Try splitting at next underscore
                    int idx = remaining.IndexOf('_');
                    if (idx > 0)
                    {
                        string part = remaining.Substring(0, idx);
                        if (_attrMap.ContainsKey(part)) result.Add(part);
                        remaining = remaining.Substring(idx + 1);
                    }
                    else
                    {
                        if (_attrMap.ContainsKey(remaining)) result.Add(remaining);
                        break;
                    }
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // DATABASE OPERATIONS
        // ═══════════════════════════════════════════════════════════════

        private void CreateTables(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS stat_pools (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    pool_name TEXT NOT NULL UNIQUE,
                    base_value REAL NOT NULL, scale REAL NOT NULL, divisor REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS item_resolved_mods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    full_gc_key TEXT NOT NULL,
                    mod_slot INTEGER NOT NULL,
                    attribute TEXT NOT NULL,
                    pool_name TEXT NOT NULL,
                    value_mult REAL NOT NULL,
                    UNIQUE(full_gc_key, mod_slot, attribute));
                CREATE INDEX IF NOT EXISTS idx_item_mods_key ON item_resolved_mods(full_gc_key);";
            cmd.ExecuteNonQuery();
        }

        private void InsertPools(SqliteConnection conn)
        {
            var pools = new (string N, float B, float S, float D)[] {
                ("MaxPointBonusPool",           50f,  7950f, 109f),
                ("PrimaryAttributeBonusPool",   20f,  1090f, 109f),
                ("DamageBonusPool",             10f,  3270f, 109f),
                ("DamageModPool",                5f,   545f, 109f),
                ("AttackRatingBonusPool",       10f,  1090f, 109f),
                ("AttackDefenseRatingModPool",   5f,   545f, 109f),
                ("DefenseRatingBonusPool",      10f,  1090f, 109f),
                ("DamageResistBonusPool",        5f,   545f, 109f),
                ("SpeedModPool",                 2f,   218f, 109f),
                ("SizeModPool",                  1f,   109f, 109f),
                ("BlockModPool",                 5f,   545f, 109f),
                ("CriticalHitModPool",           3f,   327f, 109f),
                ("StealBonusPool",               2f,   218f, 109f),
                ("RegenModPool",                 5f,   545f, 109f),
                ("StunBonusPool",                3f,   327f, 109f),
                ("DamageReflectBonusPool",       2f,   218f, 109f),
            };
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO stat_pools (pool_name,base_value,scale,divisor) VALUES(@n,@b,@s,@d)";
            var pN = cmd.Parameters.Add("@n", System.Data.DbType.String);
            var pB = cmd.Parameters.Add("@b", System.Data.DbType.Double);
            var pS = cmd.Parameters.Add("@s", System.Data.DbType.Double);
            var pD = cmd.Parameters.Add("@d", System.Data.DbType.Double);
            foreach (var p in pools) { pN.Value = p.N; pB.Value = p.B; pS.Value = p.S; pD.Value = p.D; cmd.ExecuteNonQuery(); }
        }

        private void InsertResolvedMods(SqliteConnection conn, string fullKey, List<ResolvedMod> mods)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO item_resolved_mods (full_gc_key,mod_slot,attribute,pool_name,value_mult) VALUES(@k,@s,@a,@p,@v)";
            var pK = cmd.Parameters.Add("@k", System.Data.DbType.String);
            var pS = cmd.Parameters.Add("@s", System.Data.DbType.Int32);
            var pA = cmd.Parameters.Add("@a", System.Data.DbType.String);
            var pP = cmd.Parameters.Add("@p", System.Data.DbType.String);
            var pV = cmd.Parameters.Add("@v", System.Data.DbType.Double);

            pK.Value = fullKey;
            foreach (var mod in mods)
            {
                pS.Value = mod.ModSlot; pA.Value = mod.Attribute; pP.Value = mod.Pool; pV.Value = mod.ValueMult;
                cmd.ExecuteNonQuery();
            }
        }

        private void LoadPools(SqliteConnection conn)
        {
            _pools.Clear();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pool_name,base_value,scale,divisor FROM stat_pools";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                _pools[r.GetString(0)] = new PoolFormula { Base = r.GetFloat(1), Scale = r.GetFloat(2), Divisor = r.GetFloat(3) };
        }

        private void LoadResolvedMods(SqliteConnection conn)
        {
            _itemMods.Clear();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT full_gc_key,mod_slot,attribute,pool_name,value_mult FROM item_resolved_mods";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                if (!_itemMods.TryGetValue(key, out var list)) { list = new List<ResolvedMod>(); _itemMods[key] = list; }
                list.Add(new ResolvedMod { ModSlot = r.GetInt32(1), Attribute = r.GetString(2), Pool = r.GetString(3), ValueMult = r.GetFloat(4) });
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // RUNTIME LOOKUP
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get ALL stat bonuses for a mythic item at a given player level.
        /// slotDivisor: weapons=8, armor varies by slot (use old formulas for armor).
        /// Returns dictionary of attribute -> calculated value.
        /// </summary>
        public Dictionary<string, int> GetItemStats(string gcClass, int playerLevel, int slotDivisor = 8)
        {
            var result = new Dictionary<string, int>();
            if (!IsLoaded || string.IsNullOrEmpty(gcClass)) return result;

            string key = NormalizeGCClass(gcClass);
            if (!_itemMods.TryGetValue(key, out var mods)) return result;

            int itemLevel = playerLevel + 3; // Mythic items are always player level + 3

            foreach (var mod in mods)
            {
                if (!_pools.TryGetValue(mod.Pool, out var pool)) continue;
                float poolValue = pool.Base + (itemLevel - 1) * pool.Scale / pool.Divisor;
                int bonus = (int)(poolValue * mod.ValueMult / slotDivisor);

                if (result.ContainsKey(mod.Attribute))
                    result[mod.Attribute] += bonus;
                else
                    result[mod.Attribute] = bonus;
            }

            return result;
        }

        /// <summary>
        /// Get HP-relevant stats: (directHP, endurance, mana).
        /// </summary>
        public (int hp, int endurance, int mana) GetItemHPStats(string gcClass, int playerLevel)
        {
            var stats = GetItemStats(gcClass, playerLevel);
            stats.TryGetValue("MAX_HIT_POINTS", out int hp);
            stats.TryGetValue("ENDURANCE", out int end);
            stats.TryGetValue("MAX_MANA_POINTS", out int mana);
            return (hp, end, mana);
        }

        /// <summary>Check if an item has resolved mods in the database.</summary>
        public bool HasItem(string gcClass)
        {
            if (!IsLoaded) return false;
            return _itemMods.ContainsKey(NormalizeGCClass(gcClass));
        }

        /// <summary>Get list of attribute names an item has (for logging).</summary>
        public List<string> GetItemAttributes(string gcClass)
        {
            string key = NormalizeGCClass(gcClass);
            if (_itemMods.TryGetValue(key, out var mods))
                return mods.Select(m => m.Attribute).Distinct().ToList();
            return new List<string>();
        }

        // ═══════════════════════════════════════════════════════════════
        // GC CLASS NORMALIZATION
        // ═══════════════════════════════════════════════════════════════

        private string NormalizeGCClass(string gcClass)
        {
            string lower = gcClass.ToLowerInvariant();
            if (lower.StartsWith("items.pal."))
                lower = lower.Substring("items.pal.".Length);
            return lower;
        }

        /// <summary>For external logging. Extracts the last part of a GC class.</summary>
        public static string ExtractPattern(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return "";
            string[] parts = gcClass.Split('.');
            return parts.Length > 0 ? parts[parts.Length - 1] : gcClass;
        }
    }
}
