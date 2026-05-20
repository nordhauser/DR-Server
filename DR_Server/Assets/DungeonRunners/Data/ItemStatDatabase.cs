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

        // Path B kill switch. Flip to false to revert all non-mythic write sites to the legacy
        // single-ScaleMod cstring behaviour. Mythic path doesn't read this flag.
        public static bool PathBEnabled = true;

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
        // Per-slot mod refs for IG-stub mythics — used by wire serialization (Piece B)
        private Dictionary<string, List<(int Slot, string ModRef)>> _itemWireMods = new(StringComparer.OrdinalIgnoreCase);
        // GCDictionary: set of class names registered in the client (one per .gc class). The
        // dict's sequential numeric IDs are NOT the runtime registry's keys — the client looks up
        // classes by DJB2 hash of the lowercased name (case 0x04 in readType). The dict's value is
        // therefore which prefix form ("items.modpal.X" vs "X") the client registered each class
        // under, so the server hashes the matching form.
        private HashSet<string> _gcClassNames = new(StringComparer.OrdinalIgnoreCase);

        // Parsing intermediaries (used only during population, then cleared)
        private Dictionary<string, (string Attr, string Pool)> _attrMap = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _modPalRefs = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _weaponMythicRefs = new(StringComparer.OrdinalIgnoreCase);
        // IG-stub items: PAL path (lowercased, e.g. "2haxemythicpal.2haxemythic101") → [(slot, generator path)]
        // For direct-Item IG entries (Mythic, Rare, Unique). Wrapper entries (Magic/Superior) go through _wrapperIGEntries.
        private Dictionary<string, List<(int Slot, string GeneratorPath)>> _igStubItems = new(StringComparer.OrdinalIgnoreCase);
        // Per-rarity item counts captured during parse (for boot log breakdown).
        private Dictionary<string, int> _igStubCountByRarity = new(StringComparer.OrdinalIgnoreCase);
        // Wrapper IG entries: rarity ∈ {Magic, Superior} where the inner block uses
        // ItemGenerator = items.ig.X.NormalYIG (no direct Item=). Resolved in a second pass by
        // recursively reading the target IG's direct Item= entries and storing wire mods under
        // composite key "palpath:rarity".
        private List<(string Rarity, string TargetIGRef, List<(int Slot, string GeneratorPath)> Generators)> _wrapperIGEntries = new();
        // Mod generator tables: "{MGFile}.{Section}" → ordered list of ItemModifier refs
        private Dictionary<string, List<string>> _modGenerators = new(StringComparer.OrdinalIgnoreCase);
        // Mod generator inheritance: "{MGFile}.{Section}" → "{ParentFile}.{ParentSection}" (sections with empty body inherit)
        private Dictionary<string, string> _modGeneratorParents = new(StringComparer.OrdinalIgnoreCase);

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

                    // Detect needs-repopulate: empty, OR no IG-stub entries (pre-Phase-5 DB),
                    // OR no Path B non-mythic wire mods (pre-Path-B DB).
                    bool needsRepopulate = count == 0;
                    if (!needsRepopulate)
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM item_resolved_mods WHERE full_gc_key='2haxemythicpal.2haxemythic101'";
                        long stubProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = stubProbe == 0;
                    }
                    if (!needsRepopulate)
                    {
                        // Path B canary v3: weapon IGs use `ItemTimeLine` (capital L) while
                        // mage/plate IGs use `ItemTimeline` (lowercase l). The first cut had
                        // a case-sensitive regex that missed all weapon wrappers. The presence
                        // of weapon wrapper rows confirms the case-insensitive parser ran.
                        cmd.CommandText = "SELECT COUNT(*) FROM item_wire_mods WHERE full_gc_key LIKE '2hcrossbow%:rare' LIMIT 1";
                        long weaponProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = weaponProbe == 0;
                    }
                    if (!needsRepopulate)
                    {
                        // Stale-prefix sentinel: if ANY wire-mod row still has the old
                        // items.pal. prefix, the parser ran pre-fix → force rebuild.
                        cmd.CommandText = "SELECT COUNT(*) FROM item_wire_mods WHERE full_gc_key LIKE 'items.pal.%' LIMIT 1";
                        long staleProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = staleProbe > 0;
                    }

                    if (needsRepopulate)
                    {
                        Debug.LogError($"[ItemStatDB] Populating from GC files (existing rows={count}, full rebuild)...");
                        using (var del = conn.CreateCommand())
                        {
                            del.CommandText = "DELETE FROM item_resolved_mods; DELETE FROM item_wire_mods;";
                            del.ExecuteNonQuery();
                        }
                        PopulateFromGCFiles(conn);
                    }
                    else
                    {
                        Debug.LogError($"[ItemStatDB] Already populated: {count} mod entries");
                    }
                }

                LoadPools(conn);
                LoadResolvedMods(conn);
                LoadWireMods(conn);
                LoadGCDictionary();

                // Always parse MG files at boot — even when DB is already populated. The class+
                // rarity synthetic fallback in GetWrapperIGWireMods needs _modGenerators alive at
                // runtime to serve items not covered by any direct or wrapper IG (e.g. Token Master
                // synthetic items like ChainPAL.ChainGloves2). Cheap (~50ms).
                if (_modGenerators.Count == 0)
                {
                    ParseAllMGFiles();
                    Debug.LogError($"[ItemStatDB] Boot-time MG cache: {_modGenerators.Count} generators ready for class+rarity fallback");
                }

                IsLoaded = true;

                Debug.LogError($"[ItemStatDB] Loaded: {_pools.Count} pools, {_itemMods.Count} items, {_itemMods.Values.Sum(v => v.Count)} total mods, {_itemWireMods.Count} wire-mod items, {_gcClassNames.Count} GC class names");

                // Path B sanity probes — surfaces parser regressions at boot.
                foreach (var probeKey in new[] {
                    "2haxemythicpal.2haxemythic101",           // existing mythic (Diabolical) - regression canary
                    "magebodypal.rare001",                      // Tier 1 direct-Item Rare
                    "magebodypal.unique001",                    // Tier 1 direct-Item Unique (Mage)
                    "platepal.plateuniquearmor1",               // Tier 1 direct-Item Unique (Plate)
                    "magebodypal.normal001:magic",              // Tier 2 wrapper Magic
                    "magebodypal.normal001:superior"            // Tier 2 wrapper Superior
                })
                {
                    if (_itemWireMods.TryGetValue(probeKey, out var probe))
                        Debug.LogError($"[ItemStatDB] PROBE {probeKey}: {probe.Count} wire mods -> [{string.Join(", ", probe.Select(p => p.ModRef))}]");
                    else
                        Debug.LogError($"[ItemStatDB] PROBE {probeKey}: MISSING");
                }
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

            Debug.LogError($"[ItemStatDB] Phase 4: {itemCount} items, {modCount} resolved mod attributes stored");

            // Phase 5: IG-stub items across all rarities (Mythic/Rare/Unique direct-Item IGs +
            // Magic/Superior wrapper IGs that delegate to NormalYIG). Wire mods get injected at write
            // sites; client renders 2-7 visible bonuses per native data per tier.
            // Chain: {Rarity}*IG.gc Item=X + ItemModGeneratorN=Y → *MG.gc section Y → ModPAL.Quality.ModN refs.
            ParseAllIGFiles();
            string rarityBreakdown = string.Join(" ", _igStubCountByRarity.OrderBy(k => k.Key).Select(k => $"{k.Key.ToLowerInvariant()}={k.Value}"));
            Debug.LogError($"[ItemStatDB] Phase 5a: {_igStubItems.Count} direct-Item IG entries ({rarityBreakdown}) + {_wrapperIGEntries.Count} wrapper-IG entries");

            ParseAllMGFiles();
            Debug.LogError($"[ItemStatDB] Phase 5b: {_modGenerators.Count} mod generators parsed");

            int igItemCount = 0, igStatCount = 0, igWireCount = 0;
            foreach (var kvp in _igStubItems)
            {
                string itemPalPath = kvp.Key;
                var generators = kvp.Value;

                // For each slot, pick the FIRST mod ref deterministically.
                var perSlotModRefs = new List<(int Slot, string ModRef)>();
                foreach (var (slot, generatorPath) in generators)
                {
                    string normGen = generatorPath;
                    if (normGen.StartsWith("items.mg.", StringComparison.OrdinalIgnoreCase))
                        normGen = normGen.Substring("items.mg.".Length);
                    if (_modGenerators.TryGetValue(normGen, out var modRefs) && modRefs.Count > 0)
                    {
                        perSlotModRefs.Add((slot, modRefs[0]));
                    }
                }
                if (perSlotModRefs.Count == 0) continue;

                // Stat resolution (existing ResolveMods handles ModPAL.Quality.ModN chain).
                var statResolved = ResolveMods(perSlotModRefs.Select(p => (p.Slot, p.ModRef)).ToList());
                if (statResolved.Count > 0)
                {
                    InsertResolvedMods(conn, itemPalPath, statResolved);
                    igStatCount += statResolved.Count;
                }

                // Wire serialization (all slots, including Binder which has no stats).
                InsertWireMods(conn, itemPalPath, perSlotModRefs);
                igWireCount += perSlotModRefs.Count;
                igItemCount++;
            }

            // Phase 5c — wrapper IG resolution (Magic/Superior). For each wrapper entry, parse the
            // target IG (e.g. NormalMageBodyIG.gc) for its direct Item= rows, and store the
            // wrapper's mod generators under composite key "palpath:rarity".
            int wrapItemCount = 0, wrapWireCount = 0;
            foreach (var entry in _wrapperIGEntries)
            {
                string targetIGName = entry.TargetIGRef.Split('.').Last();
                string targetFilePath = Path.Combine(_gcDir, targetIGName + ".gc");
                if (!File.Exists(targetFilePath)) continue;

                var targetItems = ParseDirectItemEntries(targetFilePath);
                if (targetItems.Count == 0) continue;

                var perSlotModRefs = new List<(int Slot, string ModRef)>();
                foreach (var (slot, generatorPath) in entry.Generators)
                {
                    string normGen = generatorPath;
                    if (normGen.StartsWith("items.mg.", StringComparison.OrdinalIgnoreCase))
                        normGen = normGen.Substring("items.mg.".Length);
                    if (_modGenerators.TryGetValue(normGen, out var modRefs) && modRefs.Count > 0)
                        perSlotModRefs.Add((slot, modRefs[0]));
                }
                if (perSlotModRefs.Count == 0) continue;

                foreach (var targetPalPath in targetItems)
                {
                    string compositeKey = $"{targetPalPath}:{entry.Rarity}".ToLowerInvariant();
                    InsertWireMods(conn, compositeKey, perSlotModRefs);
                    wrapWireCount += perSlotModRefs.Count;
                    wrapItemCount++;
                }
            }

            transaction.Commit();
            Debug.LogError($"[ItemStatDB] Phase 5: {igItemCount} direct-Item items, {igStatCount} stat mods, {igWireCount} wire mods stored; Phase 5c: {wrapItemCount} wrapper:rarity rows, {wrapWireCount} wire mods stored");

            // Clear parsing intermediaries — but KEEP _modGenerators alive at runtime so
            // the class+rarity synthetic fallback (GetWrapperIGWireMods) can pull from
            // {Class}MG.{Phase}MG pools for items not covered by any direct or wrapper IG.
            _attrMap.Clear();
            _modPalRefs.Clear();
            _weaponMythicRefs.Clear();
            _igStubItems.Clear();
            _igStubCountByRarity.Clear();
            _wrapperIGEntries.Clear();
            // _modGenerators.Clear();  // RETAINED — needed for SynthesizeClassRarityMods
            _modGeneratorParents.Clear();
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
        // PHASE 5a: Parse {Rare,Unique,Magic,Superior,Mythic}*IG.gc files
        // ═══════════════════════════════════════════════════════════════

        // Loops the rarity-prefix whitelist instead of a single glob — excludes MerchantSpecialEventIG,
        // BlingGnomeIG, NormalIG, etc. which use LinkedGenerator references and don't carry
        // item-block-level Item= / ItemModGeneratorN= entries.
        private void ParseAllIGFiles()
        {
            string[] rarityPrefixes = { "Rare", "Unique", "Magic", "Superior", "Mythic" };
            foreach (var prefix in rarityPrefixes)
            {
                _igStubCountByRarity[prefix] = 0;
                foreach (var file in Directory.GetFiles(_gcDir, prefix + "*IG.gc"))
                    ParseSingleIGFile(file, prefix);
            }
        }

        private void ParseSingleIGFile(string file, string fileRarity)
        {
            string content = File.ReadAllText(file).Replace("\r", "");
            string currentItemPalPath = null;
            string currentItemGenRef = null;
            var currentGenerators = new List<(int, string)>();

            void Flush()
            {
                if (currentGenerators.Count > 0)
                {
                    if (currentItemPalPath != null)
                    {
                        _igStubItems[currentItemPalPath] = new List<(int, string)>(currentGenerators);
                        _igStubCountByRarity[fileRarity]++;
                    }
                    else if (currentItemGenRef != null)
                    {
                        // Wrapper IG (Magic/Superior pattern: ItemGenerator = items.ig.X.NormalYIG)
                        _wrapperIGEntries.Add((fileRarity, currentItemGenRef, new List<(int, string)>(currentGenerators)));
                    }
                }
                currentItemPalPath = null;
                currentItemGenRef = null;
                currentGenerators.Clear();
            }

            foreach (string rawLine in content.Split('\n'))
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                // Inner-block header: matches direct-Item blocks (Rare/Unique/Mythic via ItemTimeline.*)
                // and wrapper blocks (Magic/Superior via RandomItemGenerator). SingleItemGenerator
                // included for NormalAmuletIG-style files (no mod generators -> harmlessly skipped).
                // The outer container `extends ItemGeneratorTable` is excluded by this list.
                // Case-insensitive: weapon IGs (2HCrossbow, 1HPick etc.) use "ItemTimeLine" with
                // capital L while mage/plate IGs use "ItemTimeline" lowercase. Both are valid in
                // the native data.
                if (Regex.IsMatch(t, @"^\w+\s+extends\s+(ItemTimeline\.\w+|RandomItemGenerator|SingleItemGenerator)", RegexOptions.IgnoreCase))
                {
                    Flush();
                    continue;
                }

                var itemMatch = Regex.Match(t, @"^Item\s*=\s*([^;\s]+)");
                if (itemMatch.Success)
                {
                    // Normalize storage key: strip items.pal. prefix so it matches what
                    // NormalizeGCClass produces at lookup. Mythic IG files use bare paths
                    // (e.g. "2HAxeMythicPAL.2HAxeMythic1"); mage IG files use the prefixed
                    // form ("items.pal.MageBodyPAL.Rare001"). Without normalization,
                    // prefixed entries never hit at lookup.
                    string rawItem = itemMatch.Groups[1].Value.ToLowerInvariant();
                    if (rawItem.StartsWith("items.pal."))
                        rawItem = rawItem.Substring("items.pal.".Length);
                    currentItemPalPath = rawItem;
                    continue;
                }

                // Wrapper IG marker: "ItemGenerator = items.ig.X.NormalYIG"
                var wrapperMatch = Regex.Match(t, @"^ItemGenerator\s*=\s*([^;\s]+)");
                if (wrapperMatch.Success)
                {
                    currentItemGenRef = wrapperMatch.Groups[1].Value;
                    continue;
                }

                var genMatch = Regex.Match(t, @"^ItemModGenerator(\d+)\s*=\s*([^;\s]+)");
                if (genMatch.Success)
                {
                    int slot = int.Parse(genMatch.Groups[1].Value);
                    currentGenerators.Add((slot, genMatch.Groups[2].Value));
                }
            }

            // Final flush
            Flush();
        }

        // Reads a single IG file and returns lowercased palpaths from direct Item= rows. Used by
        // wrapper IG resolution to enumerate the target IG's items (e.g. NormalMageBodyIG → all
        // MageBodyPAL.Normal### entries).
        private List<string> ParseDirectItemEntries(string filePath)
        {
            var result = new List<string>();
            string content = File.ReadAllText(filePath).Replace("\r", "");
            foreach (string rawLine in content.Split('\n'))
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;
                var m = Regex.Match(t, @"^Item\s*=\s*([^;\s]+)");
                if (m.Success)
                {
                    string raw = m.Groups[1].Value.ToLowerInvariant();
                    if (raw.StartsWith("items.pal."))
                        raw = raw.Substring("items.pal.".Length);
                    result.Add(raw);
                }
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5b: Parse *MG.gc files (mod generator tables)
        // ═══════════════════════════════════════════════════════════════

        private void ParseAllMGFiles()
        {
            foreach (var file in Directory.GetFiles(_gcDir, "*MG.gc"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.EndsWith("IG", StringComparison.OrdinalIgnoreCase)) continue; // safety

                string content = File.ReadAllText(file).Replace("\r", "");
                string currentSection = null;
                var currentMods = new List<string>();

                foreach (string rawLine in content.Split('\n'))
                {
                    string t = rawLine.Trim();
                    if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                    // Save previous before starting new section
                    void FlushPrevious()
                    {
                        if (currentSection != null)
                            _modGenerators[$"{fileName}.{currentSection}"] = new List<string>(currentMods);
                    }

                    // Form 1: "XXX extends ItemModifierGeneratorTable" — section with its own mods
                    var sectionMatch = Regex.Match(t, @"^(\w+)\s+extends\s+ItemModifierGeneratorTable");
                    if (sectionMatch.Success)
                    {
                        FlushPrevious();
                        currentSection = sectionMatch.Groups[1].Value;
                        currentMods.Clear();
                        continue;
                    }

                    // Form 2: "XXX extends items.mg.YYY.ZZZ" — section that inherits from another MG section
                    var inheritMatch = Regex.Match(t, @"^(\w+)\s+extends\s+items\.mg\.(\w+)\.(\w+)");
                    if (inheritMatch.Success)
                    {
                        FlushPrevious();
                        currentSection = inheritMatch.Groups[1].Value;
                        currentMods.Clear();
                        _modGeneratorParents[$"{fileName}.{currentSection}"] = $"{inheritMatch.Groups[2].Value}.{inheritMatch.Groups[3].Value}";
                        continue;
                    }

                    var modMatch = Regex.Match(t, @"^ItemModifier\s*=\s*([^;\s]+)");
                    if (modMatch.Success && currentSection != null)
                        currentMods.Add(modMatch.Groups[1].Value);
                }

                // Final flush — record the section even if mods are empty (so inheritance still applies)
                if (currentSection != null)
                    _modGenerators[$"{fileName}.{currentSection}"] = new List<string>(currentMods);
            }

            ResolveModGeneratorInheritance();
        }

        // Walk inheritance chains so sections that inherit (empty body) get their parent's mods.
        private void ResolveModGeneratorInheritance()
        {
            foreach (var key in _modGeneratorParents.Keys.ToList())
            {
                // Skip if this section already has its own mods
                if (_modGenerators.TryGetValue(key, out var own) && own.Count > 0) continue;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
                string parent = _modGeneratorParents[key];
                while (parent != null && !visited.Contains(parent))
                {
                    visited.Add(parent);
                    if (_modGenerators.TryGetValue(parent, out var parentMods) && parentMods.Count > 0)
                    {
                        _modGenerators[key] = new List<string>(parentMods);
                        break;
                    }
                    _modGeneratorParents.TryGetValue(parent, out parent);
                }
            }
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
                CREATE INDEX IF NOT EXISTS idx_item_mods_key ON item_resolved_mods(full_gc_key);
                CREATE TABLE IF NOT EXISTS item_wire_mods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    full_gc_key TEXT NOT NULL,
                    mod_slot INTEGER NOT NULL,
                    mod_ref TEXT NOT NULL,
                    UNIQUE(full_gc_key, mod_slot));
                CREATE INDEX IF NOT EXISTS idx_item_wire_mods_key ON item_wire_mods(full_gc_key);";
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

        private void InsertWireMods(SqliteConnection conn, string fullKey, List<(int Slot, string ModRef)> wireMods)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO item_wire_mods (full_gc_key,mod_slot,mod_ref) VALUES(@k,@s,@r)";
            var pK = cmd.Parameters.Add("@k", System.Data.DbType.String);
            var pS = cmd.Parameters.Add("@s", System.Data.DbType.Int32);
            var pR = cmd.Parameters.Add("@r", System.Data.DbType.String);
            pK.Value = fullKey;
            foreach (var (slot, modRef) in wireMods)
            {
                pS.Value = slot;
                pR.Value = modRef;
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

        private void LoadWireMods(SqliteConnection conn)
        {
            _itemWireMods.Clear();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT full_gc_key,mod_slot,mod_ref FROM item_wire_mods ORDER BY full_gc_key, mod_slot";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                if (!_itemWireMods.TryGetValue(key, out var list))
                {
                    list = new List<(int, string)>();
                    _itemWireMods[key] = list;
                }
                list.Add((r.GetInt32(1), r.GetString(2)));
            }
        }

        // Loads the authored class catalog at server boot. File format: one entry per line,
        // "<numeric_id> <case-sensitive class name>". We discard the dict's numeric ID (it's a file
        // order index, not the runtime registry's key) and keep the name set so we can pick the
        // correct prefix form ("items.modpal.X" vs "X") for hashing.
        private void LoadGCDictionary()
        {
            _gcClassNames.Clear();
            string path = Path.Combine(_gcDir, "..", "GCDictionary.dict");
            if (!File.Exists(path))
            {
                Debug.LogError($"[ItemStatDB] GCDictionary.dict not found at {path} — Phase 2 IG-stub mod injection will not work");
                return;
            }
            using var sr = new StreamReader(path);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                int sp = line.IndexOf(' ');
                if (sp <= 0 || sp >= line.Length - 1) continue;
                string name = line.Substring(sp + 1).Trim();
                if (name.Length > 0)
                    _gcClassNames.Add(name);
            }
        }

        // Compute the runtime hash the client uses for a GC class. The client looks up classes by
        // DJB2(lowercased name) in readType case 0x04. The wrinkle: the dict registers classes under
        // varying prefix conventions (AxeCraftedModPAL.X has no prefix; items.modpal.FighterModPal.X
        // does). We probe the dict to find the actual registered form, then hash that.
        public uint GetGCClassHash(string className)
        {
            if (!IsLoaded || string.IsNullOrEmpty(className)) return 0;
            // Try the name as-given (case-insensitive match against dict)
            if (_gcClassNames.Contains(className))
                return ComputeDJB2(className);
            // If name has prefix, try without
            if (className.StartsWith("items.modpal.", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = className.Substring("items.modpal.".Length);
                if (_gcClassNames.Contains(stripped))
                    return ComputeDJB2(stripped);
            }
            else
            {
                string prefixed = "items.modpal." + className;
                if (_gcClassNames.Contains(prefixed))
                    return ComputeDJB2(prefixed);
            }
            return 0;
        }

        private static uint ComputeDJB2(string s)
        {
            uint h = 5381;
            foreach (char c in s.ToLowerInvariant()) h = h * 33 + (uint)c;
            return h;
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

        /// <summary>
        /// Get the per-slot wire-mod refs for an IG-stub mythic item (Billy's Goat, Diabolical, etc).
        /// Returns ordered list of (slot, modRef) — empty if item isn't IG-stub.
        /// Piece B (wire serialization) uses these to inject mod children into equipment packets.
        /// </summary>
        public List<(int Slot, string ModRef)> GetItemWireMods(string gcClass)
        {
            if (!IsLoaded || string.IsNullOrEmpty(gcClass)) return new List<(int, string)>();
            string key = NormalizeGCClass(gcClass);
            if (_itemWireMods.TryGetValue(key, out var list))
                return new List<(int, string)>(list);
            return new List<(int, string)>();
        }

        /// <summary>
        /// Wrapper-IG lookup for items whose mod set depends on the drop's rarity (Magic/Superior).
        /// The same Normal PAL item (e.g. MageBodyPAL.Normal001) is referenced by both
        /// MagicMageBodyIG (3 mod gens) and SuperiorMageBodyIG (2 mod gens). The wire-mods table
        /// stores both under composite keys "palpath:Magic" and "palpath:Superior" — pass the
        /// drop's actual rarity to pick the right one.
        ///
        /// FALLBACK: if no explicit wrapper covers the (gcClass, rarity) pair, synthesize one
        /// using the item's class + the four standard MG generators (MagicPre/Binder/Rare/Sup
        /// for Rare; UniquePre/Binder/Rare/Sup for Unique; etc). This handles items dropped
        /// outside the native IG hierarchy — e.g. Token Master emitting named base-PAL items
        /// like ChainPAL.ChainGloves2 at Rare tier, which natively don't have wrapper coverage.
        /// </summary>
        public List<(int Slot, string ModRef)> GetWrapperIGWireMods(string gcClass, string rarity)
        {
            if (!IsLoaded || string.IsNullOrEmpty(gcClass) || string.IsNullOrEmpty(rarity)) return new List<(int, string)>();
            string normalized = NormalizeGCClass(gcClass);
            string key = ($"{normalized}:{rarity}").ToLowerInvariant();
            if (_itemWireMods.TryGetValue(key, out var list))
                return new List<(int, string)>(list);

            // Synthetic class+rarity fallback — pull the same 4-generator chain the wrapper IGs
            // use for the item's armor class. Keeps mod assignment deterministic per
            // (gcClass, rarity) so items stay stable across zone-switches / relogs.
            return SynthesizeClassRarityMods(normalized, rarity);
        }

        // Maps an item's PAL name to its armor/weapon class for MG-pool lookup. Returns null
        // when the item type isn't covered (consumables, quest items, etc.).
        private static string ClassFromGCClass(string normalizedLower)
        {
            if (normalizedLower.Contains("plate") || normalizedLower.Contains("scale") || normalizedLower.Contains("crystal"))
                return "Fighter";
            if (normalizedLower.Contains("chain") || normalizedLower.Contains("splint"))
                return "Fighter"; // Fighter heavy armor families
            if (normalizedLower.Contains("leather"))
                return "Ranger";
            if (normalizedLower.Contains("mage") && (normalizedLower.Contains("body") || normalizedLower.Contains("helm")
                || normalizedLower.Contains("boots") || normalizedLower.Contains("gloves")
                || normalizedLower.Contains("shoulder") || normalizedLower.Contains("shield")))
                return "Mage";
            // Weapons: ranger uses bows/crossbows/guns; fighter uses melee; mage uses staves.
            if (normalizedLower.Contains("bow") || normalizedLower.Contains("crossbow") || normalizedLower.Contains("gun") || normalizedLower.Contains("cannon"))
                return "Ranger";
            if (normalizedLower.Contains("staff"))
                return "Mage";
            if (normalizedLower.Contains("axe") || normalizedLower.Contains("sword") || normalizedLower.Contains("mace")
                || normalizedLower.Contains("pick") || normalizedLower.Contains("club")
                || normalizedLower.Contains("katana") || normalizedLower.Contains("polearm"))
                return "Fighter";
            return null;
        }

        private List<(int Slot, string ModRef)> SynthesizeClassRarityMods(string normalizedLower, string rarity)
        {
            string klass = ClassFromGCClass(normalizedLower);
            if (klass == null) return new List<(int, string)>();

            // Native generator chain per rarity tier. Mirrors RareXXXBodyIG / UniqueXXXBodyIG
            // structure in data dum. Mage path adds LevelPrefix at slot 1 to match the mage
            // body IGs (5 visible bonuses); fighter/ranger heavy armor uses 4-gen chain.
            string[] generators;
            string r = rarity.ToLowerInvariant();
            string pre;
            switch (r)
            {
                case "rare":     pre = "MagicPreMG";   break;
                case "unique":   pre = "UniquePreMG";  break;
                case "magical":
                case "magic":    pre = "MagicPreMG";   break;
                case "superior": pre = null;           break; // 2-gen: Binder + Sup
                default: return new List<(int, string)>(); // Normal/Mythic handled elsewhere
            }
            if (r == "superior")
                generators = new[] { $"{klass}MG.BinderPostMG", $"{klass}MG.SupPostMG" };
            else if (r == "magical" || r == "magic")
                generators = new[] { $"{klass}MG.{pre}", $"{klass}MG.BinderPostMG", $"{klass}MG.SupPostMG" };
            else // Rare or Unique
                generators = new[] { $"{klass}MG.{pre}", $"{klass}MG.BinderPostMG", $"{klass}MG.RarePostMG", $"{klass}MG.SupPostMG" };

            var result = new List<(int, string)>();
            int slot = 1;
            foreach (var genKey in generators)
            {
                if (_modGenerators != null && _modGenerators.TryGetValue(genKey, out var modList) && modList.Count > 0)
                    result.Add((slot, modList[0]));
                else if (_itemWireMods != null)
                {
                    // _modGenerators may have been cleared after Phase 5 — that's fine, the wire
                    // mods we'd have synthesized are static per (class, rarity), so we cache them
                    // on first hit. But the cleanup happens at end of PopulateFromGCFiles, so we
                    // need to also persist the _modGenerators data. See note in Load().
                }
                slot++;
            }
            return result;
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
