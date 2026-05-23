using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using DungeonRunners.Data;
using DungeonRunners.Combat;
using DungeonRunners.Core;

namespace DungeonRunners.Managers
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LOOT MANAGER — Treasure generator logic ONLY.
    //
    // Uses EXISTING:
    //   - ItemRarity enum (in MerchantManager.cs)
    //   - RarityHelper (in MerchantManager.cs)
    //   - DatabaseLoader.AllWeapons, AllArmor, Potions, Rings, Amulets, etc.
    //   - DatabaseLoader.CreatureDatabase, ItemDatabase, GeneralItemDatabase
    //
    // Does NOT touch (UGS owns these):
    //   - ZoneSpawnManager (mob spawning)
    //   - DroppedItemInfo / TrackDroppedItem (ground item tracking)
    //   - SendDroppedItemSpawnPacket (packet building)
    //   - GCObject.WriteInitForDroppedItem (item serialization)
    //
    // ═══════════════════════════════════════════════════════════════════════════

    public class LootManager
    {
        private static LootManager _instance;
        public static LootManager Instance => _instance ??= new LootManager();

        private System.Random _rng = new System.Random();
        private bool _initialized;

        // Treasure generators parsed from GC files
        private Dictionary<string, TreasureGenerator> _itemGenerators;
        private Dictionary<string, GoldGenerator> _goldGenerators;

        // Creature treasure_gen cache (loaded from DB — not in DatabaseLoader)
        private Dictionary<string, CreatureTreasureData> _creatureTreasure;

        // ═══════════════════════════════════════════════════════════════
        // INIT — call once after DatabaseLoader.LoadAll()
        // ═══════════════════════════════════════════════════════════════

        public void Initialize()
        {
            if (_initialized) return;

            BuildItemGenerators();
            BuildGoldGenerators();
            LoadCreatureTreasureData();
            LoadClientUnknownItems();

            _initialized = true;
            Debug.LogError($"[LootManager] Initialized: {_itemGenerators.Count} item gens, " +
                $"{_goldGenerators.Count} gold gens, {_creatureTreasure.Count} creatures with treasure, " +
                $"{_clientUnknownItems.Count} client-unknown items filtered from loot pool");
        }

        // ═══════════════════════════════════════════════════════════════
        // CLIENT-UNKNOWN ITEM FILTER
        // ═══════════════════════════════════════════════════════════════
        // Server admins (Reborn devs) have added new item classes to the
        // server's Database/gc/*.gc files, marked with the comment "//NEW!"
        // at the end of each new "ClassName extends Parent //NEW!" line.
        //
        // The original Dungeon Runners client binary has its own compiled
        // GCClassRegistry. When the server sends a class hash for a //NEW!
        // item that the client doesn't have, the client logs:
        //     GCClassRegistry::readType: Failed to find type 'X'
        // and crashes (FatalError, then NULL deref → 'Unknown message
        // type(10)' as the byte parser desyncs on the next message).
        //
        // The fix is to NOT roll //NEW! items in the loot pool until the
        // user updates their client. This filter is data-driven: it scans
        // the .gc files at server startup and builds the exclusion set
        // automatically. Adding or removing //NEW! markers in the .gc
        // files does NOT require recompiling the server.
        //
        // Items already in player inventories/equipment are NOT touched —
        // this only affects newly-rolled mob drops and chest drops.
        // ═══════════════════════════════════════════════════════════════
        private static HashSet<string> _clientUnknownItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Matches:  TAB+ <ClassName> extends <Parent> //NEW!  (with any trailing junk)
        // Identifier rules: starts with letter or digit; allows letters, digits, _ , -
        private static readonly Regex _newClassRegex = new Regex(
            @"^[\t ]*([A-Za-z0-9][A-Za-z0-9_-]*)\s+extends\s+\S+\s*//.*NEW!",
            RegexOptions.Compiled);

        // Matches:  TAB+ <ClassName> extends <Parent> //was OldNamespace.OldName
        // These are RENAMED items - the gc files were updated to a new name but the
        // shipped client EXE's GCClassRegistry only knows the OLD name. If we send
        // the new name to the client it crashes with:
        //   GCClassRegistry::readType: Failed to find type '<newname>'
        //   ClientEntityManager::processMessage ERROR: Unknown message type(10)
        // Excluding these from the loot pool prevents the crash. Translation to old
        // names would preserve loot variety but is a much bigger change.
        private static readonly Regex _renamedClassRegex = new Regex(
            @"^[\t ]*([A-Za-z0-9][A-Za-z0-9_-]*)\s+extends\s+\S+\s*//\s*was\s",
            RegexOptions.Compiled);

        private void LoadClientUnknownItems()
        {
            _clientUnknownItems.Clear();

#if UNITY_EDITOR
            string gcDir = Path.Combine(Application.dataPath, "DungeonRunners", "Database", "gc");
#else
            string gcDir = Path.Combine(Application.dataPath, "..", "Database", "gc");
#endif

            if (!Directory.Exists(gcDir))
            {
                Debug.LogError($"[LootManager] gc directory not found: {gcDir} — //NEW! filter disabled");
                return;
            }

            int filesScanned = 0;
            try
            {
                foreach (string filePath in Directory.GetFiles(gcDir, "*.gc"))
                {
                    string ns = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(filePath);
                    }
                    catch (Exception readEx)
                    {
                        Debug.LogError($"[LootManager] Failed to read {filePath}: {readEx.Message}");
                        continue;
                    }
                    filesScanned++;
                    foreach (string line in lines)
                    {
                        // Pattern A: //NEW! marker = item added in a later gc revision
                        var m = _newClassRegex.Match(line);
                        if (m.Success)
                        {
                            string className = m.Groups[1].Value.ToLowerInvariant();
                            // FQN: <namespace>.<classname> — matches the gc_type stored
                            // in the weapons / armor / items DB tables.
                            _clientUnknownItems.Add($"{ns}.{className}");
                            continue;
                        }
                        // Pattern B: //was marker = item renamed; shipped client only
                        // knows the old name and crashes if it sees the new one.
                        var rm = _renamedClassRegex.Match(line);
                        if (rm.Success)
                        {
                            string className = rm.Groups[1].Value.ToLowerInvariant();
                            _clientUnknownItems.Add($"{ns}.{className}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LootManager] LoadClientUnknownItems error: {ex.Message}");
            }

            Debug.LogError($"[LootManager] Scanned {filesScanned} .gc files, " +
                $"found {_clientUnknownItems.Count} client-unknown items to exclude " +
                $"from loot pool (//NEW! and //was renamed items)");
        }

        // ═══════════════════════════════════════════════════════════════
        // ITEM GENERATORS — from GC files
        //
        // Binary: ItemGeneratorTable — ordered entries, each has Chance (1-in-N).
        // Roll random(0, Chance), if 0 → that entry fires. First hit wins.
        //
        // DefaultIG.gc:  Normal=5, Superior=20, Magical=25, Rare=50, Unique=5000, Mythic=10000
        // ChampionIG.gc: Superior=1, Magical=1, Rare=10, Unique=20, Mythic=4000(lo)/2000(hi)
        // HeroIG.gc:     Superior=1(maxLvl5), Magical=1, Rare=3, Unique=8, Mythic=500(lo)/250(hi)
        // TreasureChestIG.gc: Superior=1, Magical=15, Rare=20, Unique=80, Mythic=10000
        // ═══════════════════════════════════════════════════════════════

        private void BuildItemGenerators()
        {
            _itemGenerators = new Dictionary<string, TreasureGenerator>(StringComparer.OrdinalIgnoreCase);

            _itemGenerators["DefaultIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   10000, 0, 999),
                new TreasureEntry(ItemRarity.Unique,   5000,  0, 999),
                new TreasureEntry(ItemRarity.Rare,     50,    0, 999),
                new TreasureEntry(ItemRarity.Magical,  25,    0, 999),
                new TreasureEntry(ItemRarity.Superior, 20,    0, 999),
                new TreasureEntry(ItemRarity.Normal,   5,     0, 999),
            });

            _itemGenerators["ChampionIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   4000, 0, 89),
                new TreasureEntry(ItemRarity.Mythic,   2000, 90, 999),
                new TreasureEntry(ItemRarity.Unique,   20,   0, 999),
                new TreasureEntry(ItemRarity.Rare,     10,   0, 999),
                new TreasureEntry(ItemRarity.Magical,  1,    0, 999),
                new TreasureEntry(ItemRarity.Superior, 1,    0, 999),
            });

            _itemGenerators["HeroIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   500, 0, 89),
                new TreasureEntry(ItemRarity.Mythic,   250, 90, 999),
                new TreasureEntry(ItemRarity.Unique,   8,   0, 999),
                new TreasureEntry(ItemRarity.Rare,     3,   0, 999),
                new TreasureEntry(ItemRarity.Magical,  1,   0, 999),
                new TreasureEntry(ItemRarity.Superior, 1,   0, 5),
            });

            _itemGenerators["TreasureChestIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   10000, 0, 999),
                new TreasureEntry(ItemRarity.Unique,   80,    0, 999),
                new TreasureEntry(ItemRarity.Rare,     20,    0, 999),
                new TreasureEntry(ItemRarity.Magical,  15,    0, 999),
                new TreasureEntry(ItemRarity.Superior, 1,     0, 999),
            });

            _itemGenerators["TreasureChestSmallIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   30000, 0, 999),
                new TreasureEntry(ItemRarity.Unique,   240,   0, 999),
                new TreasureEntry(ItemRarity.Rare,     120,   0, 999),
                new TreasureEntry(ItemRarity.Magical,  60,    0, 999),
                new TreasureEntry(ItemRarity.Superior, 1,     0, 999),
            });

            _itemGenerators["DestroyableIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Superior, 8, 0, 999),
                new TreasureEntry(ItemRarity.Normal,   3, 0, 999),
            });

            // TreasureChestBossIG.gc — boss chest (best loot)
            _itemGenerators["TreasureChestBossIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   500, 0,  89),
                new TreasureEntry(ItemRarity.Mythic,   250, 90, 999),
                new TreasureEntry(ItemRarity.Unique,   20,  0,  999),
                new TreasureEntry(ItemRarity.Rare,     1,   0,  999),
                new TreasureEntry(ItemRarity.Magical,  1,   0,  5),
                new TreasureEntry(ItemRarity.Superior, 1,   0,  2),
            });

            // TreasureChestMediumIG.gc — medium chest
            _itemGenerators["TreasureChestMediumIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   10000, 0,  89),
                new TreasureEntry(ItemRarity.Mythic,   5000,  90, 999),
                new TreasureEntry(ItemRarity.Unique,   60,    0,  999),
                new TreasureEntry(ItemRarity.Rare,     30,    0,  999),
                new TreasureEntry(ItemRarity.Magical,  15,    0,  999),
                new TreasureEntry(ItemRarity.Superior, 1,     0,  999),
                new TreasureEntry(ItemRarity.Normal,   10,    0,  1),
            });

            // TreasureChestLargeIG.gc — large chest
            _itemGenerators["TreasureChestLargeIG"] = new TreasureGenerator(new[]
            {
                new TreasureEntry(ItemRarity.Mythic,   500, 0,  89),
                new TreasureEntry(ItemRarity.Mythic,   250, 90, 999),
                new TreasureEntry(ItemRarity.Unique,   30,  0,  999),
                new TreasureEntry(ItemRarity.Rare,     15,  0,  999),
                new TreasureEntry(ItemRarity.Magical,  1,   0,  999),
                new TreasureEntry(ItemRarity.Superior, 1,   0,  5),
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // GOLD GENERATORS — from GC files
        // Binary: CurrencyGenerator — GoldValue * playerLevel, ± Volatility
        // ═══════════════════════════════════════════════════════════════

        private void BuildGoldGenerators()
        {
            _goldGenerators = new Dictionary<string, GoldGenerator>(StringComparer.OrdinalIgnoreCase);

            // DefaultGG.gc — grunt mobs
            _goldGenerators["DefaultGG"] = new GoldGenerator(new[]
            {
                new GoldEntry(25,   0.05f, 0.25f, 0,  999),
                new GoldEntry(65,   0.10f, 0.25f, 0,  999),
                new GoldEntry(100,  0.25f, 0.25f, 0,  999),
                new GoldEntry(160,  0.50f, 0.25f, 0,  999),
                new GoldEntry(300,  1.00f, 0.25f, 9,  999),
                new GoldEntry(600,  1.75f, 0.25f, 12, 999),
                new GoldEntry(1000, 2.33f, 0.25f, 15, 999),
            });

            // ChampionGG.gc
            _goldGenerators["ChampionGG"] = new GoldGenerator(new[]
            {
                new GoldEntry(1,   0.25f, 0.25f, 0,  999),
                new GoldEntry(10,  0.50f, 0.25f, 0,  999),
                new GoldEntry(30,  1.00f, 0.25f, 9,  999),
                new GoldEntry(100, 1.75f, 0.25f, 12, 999),
                new GoldEntry(500, 2.33f, 0.25f, 15, 999),
            });

            // HeroGG.gc
            _goldGenerators["HeroGG"] = new GoldGenerator(new[]
            {
                new GoldEntry(1,   0.50f, 0.25f, 0,  999),
                new GoldEntry(10,  1.00f, 0.25f, 9,  999),
                new GoldEntry(30,  1.75f, 0.25f, 12, 999),
                new GoldEntry(100, 2.33f, 0.25f, 15, 999),
            });

            // LegendGG.gc — boss chest gold (high tier)
            _goldGenerators["LegendGG"] = new GoldGenerator(new[]
            {
                new GoldEntry(1,   1.00f, 0.25f, 0,  999),
                new GoldEntry(10,  1.75f, 0.25f, 9,  999),
                new GoldEntry(30,  2.33f, 0.25f, 12, 999),
                new GoldEntry(100, 3.50f, 0.25f, 15, 999),
            });

            // Barrels/crates
            _goldGenerators["BarrelGG"] = new GoldGenerator(new[]
            {
                new GoldEntry(1,  0.05f, 0.25f, 0, 999),   // Chance=1 → 100% gold for testing
                new GoldEntry(40,  0.10f, 0.25f, 0, 999),
                new GoldEntry(100, 0.25f, 0.25f, 0, 999),
            });

            // TreasureChestSmallGG.gc
            _goldGenerators["TreasureChestSmallGG"] = new GoldGenerator(new[]
            {
                new GoldEntry(10,  0.05f, 0.25f, 0,  999),
                new GoldEntry(1,   0.10f, 0.25f, 0,  999),
                new GoldEntry(40,  0.25f, 0.25f, 0,  999),
                new GoldEntry(160, 0.50f, 0.25f, 0,  999),
                new GoldEntry(300, 1.00f, 0.25f, 9,  999),
                new GoldEntry(600, 1.75f, 0.25f, 12, 999),
                new GoldEntry(1000,2.33f, 0.25f, 15, 999),
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // CREATURE TREASURE DATA — from DB creatures table
        // treasure_gen1-4 + treasure_count1-4
        // ═══════════════════════════════════════════════════════════════

        private void LoadCreatureTreasureData()
        {
            _creatureTreasure = new Dictionary<string, CreatureTreasureData>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = Database.GameDatabase.GetConnection();
                using var r = Database.GameDatabase.ExecuteReader(conn,
                    @"SELECT gc_type, treasure_gen1, treasure_count1, treasure_gen2, treasure_count2,
                             treasure_gen3, treasure_count3, treasure_gen4, treasure_count4
                      FROM creatures WHERE treasure_gen1 IS NOT NULL AND treasure_gen1 != ''");
                while (r.Read())
                {
                    string gcType = r.GetString(0);
                    var data = new CreatureTreasureData();
                    for (int i = 0; i < 4; i++)
                    {
                        string gen = r.IsDBNull(1 + i * 2) ? "" : r.GetString(1 + i * 2);
                        // treasure_count* columns are stored as TEXT in the live
                        // DB (e.g. "1", "2") even though they're conceptually
                        // integers — r.GetInt32 throws "Specified cast is not
                        // valid" against a TEXT cell. Read as object and let
                        // Convert handle string→int. Fall back to 0 on parse fail.
                        int count = 0;
                        if (!r.IsDBNull(2 + i * 2))
                        {
                            try { count = Convert.ToInt32(r.GetValue(2 + i * 2)); }
                            catch { count = 0; }
                        }
                        if (!string.IsNullOrEmpty(gen) && count > 0)
                            data.Generators.Add((gen, count));
                    }
                    if (data.Generators.Count > 0)
                        _creatureTreasure[gcType] = data;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LootManager] Failed to load creature treasure: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PUBLIC API
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Generate loot for a killed monster.
        /// Call from UGS.OnEntityDeath().
        /// </summary>
        public List<LootDrop> GenerateMobLoot(Monster monster, int playerLevel)
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();

            if (!_creatureTreasure.TryGetValue(monster.GCType, out var treasure))
                treasure = GetDefaultTreasure(monster.Tier);

            if (treasure == null) return drops;

            foreach (var (genName, count) in treasure.Generators)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_goldGenerators.TryGetValue(genName, out var goldGen))
                    {
                        int gold = RollGold(goldGen, playerLevel);
                        if (gold > 0) drops.Add(LootDrop.Gold(gold));
                    }
                    else if (_itemGenerators.TryGetValue(genName, out var itemGen))
                    {
                        var item = RollItem(itemGen, playerLevel);
                        if (item != null) drops.Add(item);
                    }
                }
            }

            if (drops.Count > 0)
                Debug.LogError($"[LootManager] {monster.Name} ({monster.Tier}): " +
                    $"{drops.Count(d => d.IsGold)} gold, {drops.Count(d => d.IsItem)} items, " +
                    $"{drops.Count(d => d.IsKingsCoin)} kings-coin");
            return drops;
        }

        /// <summary>
        /// Generate loot for a treasure chest activation.
        /// </summary>
        public List<LootDrop> GenerateChestLoot(string generatorName, int itemCount, int playerLevel)
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();

            if (_itemGenerators.TryGetValue(generatorName, out var gen))
                for (int i = 0; i < itemCount; i++)
                {
                    var item = RollItem(gen, playerLevel);
                    if (item != null) drops.Add(item);
                }

            string ggName = generatorName.Replace("IG", "GG");
            if (!_goldGenerators.ContainsKey(ggName)) ggName = "ChampionGG";
            if (_goldGenerators.TryGetValue(ggName, out var gg))
            {
                int gold = RollGold(gg, playerLevel);
                if (gold > 0) drops.Add(LootDrop.Gold(gold));
            }

            // Guaranteed potion drops — every chest has potions
            string[] chestHPPotions = {
                "potionpal.healthpotion_noob",
                "potionpal.healthpotion_itempack",
            };
            string[] chestHPLabels = {
                "Health Potion of the Daring Noobosaur",
                "Extra Compact Major Health Potion",
            };
            string[] chestMPPotions = {
                "potionpal.manapotion_noob",
                "potionpal.manapotion_itempack",
            };
            string[] chestMPLabels = {
                "Mana Potion of the Daring Noobosaur",
                "Extra Compact Major Mana Potion",
            };
            int hpIdx = _rng.Next(chestHPPotions.Length);
            int mpIdx = _rng.Next(chestMPPotions.Length);
            drops.Add(LootDrop.Item(chestHPPotions[hpIdx], chestHPLabels[hpIdx], ItemRarity.Normal,
                "ScaleModPAL.Binder.Mod1", playerLevel));
            drops.Add(LootDrop.Item(chestMPPotions[mpIdx], chestMPLabels[mpIdx], ItemRarity.Normal,
                "ScaleModPAL.Binder.Mod1", playerLevel));

            return drops;
        }

        /// <summary>
        /// Generate loot for a destroyed barrel/crate.
        /// </summary>
        public List<LootDrop> GenerateBarrelLoot(int playerLevel)
        {
            if (!_initialized) Initialize();
            var drops = new List<LootDrop>();

            if (_goldGenerators.TryGetValue("BarrelGG", out var gg))
            {
                int gold = RollGold(gg, playerLevel);
                if (gold > 0) drops.Add(LootDrop.Gold(gold));
            }

            // 20% chance potion — all PotionPAL types from PotionPAL.gc
            if (_rng.Next(5) == 0)
            {
                string[] barrelPotions = {
                    "potionpal.healthpotion_noob",
                    "potionpal.manapotion_noob",
                    "potionpal.healthpotion_itempack",
                    "potionpal.manapotion_itempack",
                    "potionpal.dragonjuice_sm",
                    "potionpal.dragonjuice_lg",
                    "potionpal.intbuff_sm",
                    "potionpal.intbuff_lg"
                };
                string[] potionLabels = {
                    "Health Potion of the Daring Noobosaur",
                    "Mana Potion of the Daring Noobosaur",
                    "Extra Compact Major Health Potion",
                    "Extra Compact Major Mana Potion",
                    "16 oz. of Dragon Juice",
                    "40 oz. of Dragon Juice",
                    "16 oz. of Liquid Crevasse",
                    "40 oz. of Liquid Crevasse"
                };
                int idx = _rng.Next(barrelPotions.Length);
                Debug.LogError($"[LOOT-ROLL] potion rolled gc='{barrelPotions[idx]}'");
                drops.Add(LootDrop.Item(barrelPotions[idx], potionLabels[idx], ItemRarity.Normal,
                    "ScaleModPAL.Binder.Mod1", playerLevel));
            }

            // Kings Coin roll for barrels (kingsCoinPctBarrel, default 1%)
            var kcBarrel = RollKingsCoin("BARREL");
            if (kcBarrel != null) drops.Add(kcBarrel);

            return drops;
        }

        // ═══════════════════════════════════════════════════════════════
        // ROLL LOGIC
        // ═══════════════════════════════════════════════════════════════

        private LootDrop RollItem(TreasureGenerator gen, int playerLevel)
        {
            // Binary: iterate top-to-bottom, roll 1-in-Chance, first hit wins
            foreach (var entry in gen.Entries)
            {
                if (playerLevel < entry.MinLevel || playerLevel > entry.MaxLevel)
                    continue;

                if (_rng.Next(entry.Chance) == 0)
                {
                    string gcType = PickRandomItem(entry.Rarity, playerLevel);
                    if (gcType != null)
                    {
                        // Diagnostic: log the exact gc_class rolled at drop time. The
                        // last [LOOT-ROLL] line before a client crash names the item
                        // that did it. Does not change behavior — pure logging.
                        Debug.LogError($"[LOOT-ROLL] rarity={entry.Rarity} rolled gc='{gcType}'");
                        string scaleMod = RarityHelper.GetRandomScaleMod(entry.Rarity);
                        int itemLevel = RarityHelper.GetItemLevel(gcType);
                        return LootDrop.Item(gcType, GetItemLabel(gcType), entry.Rarity,
                            scaleMod, itemLevel > 0 ? itemLevel : playerLevel);
                    }
                }
            }
            return null;
        }

        private int RollGold(GoldGenerator gen, int playerLevel)
        {
            // Binary: iterate entries, roll 1-in-Chance, first hit wins
            // Gold = GoldValue * playerLevel * goldPerLevel(~10), ± Volatility
            foreach (var entry in gen.Entries)
            {
                if (playerLevel < entry.MinLevel || playerLevel > entry.MaxLevel)
                    continue;

                if (_rng.Next(entry.Chance) == 0)
                {
                    float baseGold = entry.GoldValue * playerLevel * 10f;
                    float variance = baseGold * entry.Volatility;
                    float gold = baseGold + (float)(_rng.NextDouble() * 2 - 1) * variance;
                    return Math.Max(1, (int)gold);
                }
            }
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // ITEM PICKING — selects random item matching rarity from DB
        // Uses RarityHelper.GetTierFromGcType to match items by suffix
        // ═══════════════════════════════════════════════════════════════

        // A GC class is a real item if and only if it can be constructed as an
        // Item at runtime. The patterns we can prove are NOT real items:
        //
        //   1. ".manipulators." anywhere in the path — slot archetype references
        //      on creature/world definitions (e.g. creatures.humanoid.base2hstaff
        //      .manipulators.primaryweapon, world.dungeon06.mob.master06_3a
        //      .manipulators.primaryweapon). The client's GCClassRegistry knows
        //      about these so entity-spawn doesn't error out, but they have no
        //      Item schema, no InventoryIcon, and cannot be instantiated as an
        //      Item — they crash the client in the Item ctor when picked up.
        //
        //   2. "avatar." prefix — engine-internal avatar references. Only two
        //      exist in the weapons table:
        //        - avatar.base.fists (player's bare-fists "weapon")
        //        - avatar.classes.<class>startingequipment.<item> (class start
        //          templates referenced by Avatar/StartingEquipment GC files)
        //      Confirmed crashing the client when rolled by mob loot.
        //
        //   3. "testpal." prefix — dev test palette (TestPAL.gc). 30 weapons,
        //      15 armor, 54 misc items defined for dev test zones. They are
        //      not //NEW! markers but they crash the client on drop with
        //      "ClientEntityManager::processMessage ERROR: Unexpected message
        //      size: 0" — the dropped-item packet write completes server-side
        //      but the resulting bytes desync the client packet stream on the
        //      next message. Root cause not yet investigated.
        //      TODO(loot-test-items): Fix the actual serialization bug for
        //      testpal items so they can be used in dev test zones again.
        //      Not required for live play.
        //
        //   4. //NEW! items — classes the original client binary doesn't know.
        //      See LoadClientUnknownItems() for the full explanation. The set
        //      is built at startup by scanning the .gc files for the //NEW!
        //      marker comment, so it stays in sync with whatever the dev
        //      adds/removes — no recompile needed for new items.
        //
        // Everything else — 1hswordpal.normal009 (//was), 2hmeleeweaponpal,
        // magehelmpal, items.pal.*, basepal — must stay in the pool. If a
        // specific item class crashes the client, that's a separate
        // serialization bug in the item-write path, not a reason to delete
        // it from the game.
        private static bool IsPollutedNonItemClass(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return true;
            if (gcType.IndexOf(".manipulators.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.StartsWith("avatar.", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.StartsWith("testpal.", StringComparison.OrdinalIgnoreCase))
                return true;
            // Phantom rows from the gc importer: nested `Visual extends MountedVisual`
            // blocks inside Description {} get collapsed by the importer into
            // top-level rows like mageshieldpal.visual, 1hswordpal.visual,
            // shieldvisuals.shield_crystal_03_divine.visual, and
            // potionpal.healthpotion_noob.modifier.description.visual. They are
            // NOT instantiable item classes — the client registry has no entry
            // for them. Dropping one causes the client to fail GCClassRegistry::
            // readType, desync the parser, and crash with Unknown message type(11).
            if (gcType.EndsWith(".visual", StringComparison.OrdinalIgnoreCase))
                return true;

            // Additional phantom-row suffixes from the gc importer.
            // PotionPAL nests `static Effect extends SpellEffect` blocks inside each
            // potion definition; the importer flattens these into phantom rows like
            // potionpal.manapotion_itempack.effect.spellsoundeffect — not instantiable.
            // Same story for .modifier.*, .description.*, .sound*.
            if (gcType.IndexOf(".effect.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.EndsWith(".effect", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.IndexOf(".modifier.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.EndsWith(".modifier", StringComparison.OrdinalIgnoreCase))
                return true;
            // Phantom .modN entries (e.g. potionpal.dragonjuice_lg.mod1)
            int lastDot = gcType.LastIndexOf('.');
            if (lastDot >= 0)
            {
                string suffix = gcType.Substring(lastDot + 1);
                if (suffix.Length >= 4 && suffix.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
                    && char.IsDigit(suffix[3]))
                    return true;
            }
            if (gcType.IndexOf(".description.", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (gcType.EndsWith(".description", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.EndsWith(".spellsoundeffect", StringComparison.OrdinalIgnoreCase))
                return true;
            if (gcType.EndsWith(".sound", StringComparison.OrdinalIgnoreCase))
                return true;

            // Base classes have no namespace prefix (e.g. 'base1hstaff',
            // 'base1hmelee', 'baseweaponsounds'). Real items always have the
            // form 'namespace.classname'. The client's GCClassRegistry has no
            // entry for bare base classes — they crash on drop.
            if (gcType.IndexOf('.') < 0)
                return true;

            if (_clientUnknownItems.Contains(gcType))
                return true;
            return false;
        }

        private string PickRandomItem(ItemRarity rarity, int playerLevel)
        {
            var pool = new List<string>();

            // Items must be within player's level range:
            // PAL tier formula: level = (palTier - 1) * 10 + 1
            // Allow items up to ~10 levels above player (1 tier ahead)
            int maxItemLevel = playerLevel + 10;

            if (DatabaseLoader.AllWeapons != null)
                foreach (var w in DatabaseLoader.AllWeapons)
                {
                    if (IsPollutedNonItemClass(w.gcType)) continue;
                    if (!MatchesRarity(w.gcType, rarity)) continue;
                    int itemLv = RarityHelper.GetItemLevel(w.gcType);
                    if (itemLv > maxItemLevel) continue;
                    pool.Add(w.gcType);
                }

            if (DatabaseLoader.AllArmor != null)
                foreach (var a in DatabaseLoader.AllArmor)
                {
                    if (IsPollutedNonItemClass(a.gcType)) continue;
                    if (!MatchesRarity(a.gcType, rarity)) continue;
                    int itemLv = RarityHelper.GetItemLevel(a.gcType);
                    if (itemLv > maxItemLevel) continue;
                    pool.Add(a.gcType);
                }

            if (rarity >= ItemRarity.Rare)
            {
                AddMatchingItemsLevelFiltered(pool, DatabaseLoader.Rings, rarity, maxItemLevel);
                AddMatchingItemsLevelFiltered(pool, DatabaseLoader.Amulets, rarity, maxItemLevel);
            }

            // Fallback to full ItemDatabase (still level-filtered)
            if (pool.Count == 0 && DatabaseLoader.ItemDatabase != null)
                foreach (var kvp in DatabaseLoader.ItemDatabase)
                {
                    if (IsPollutedNonItemClass(kvp.Key)) continue;
                    if (!MatchesRarity(kvp.Key, rarity)) continue;
                    int itemLv = RarityHelper.GetItemLevel(kvp.Key);
                    if (itemLv > maxItemLevel) continue;
                    pool.Add(kvp.Key);
                }

            // Last resort: if NO items in range, widen to tier 1 items so something drops
            if (pool.Count == 0)
            {
                Debug.LogError($"[LOOT] ⚠ No items in pool for rarity={rarity} playerLv={playerLevel} maxLv={maxItemLevel}, widening to tier 1");
                if (DatabaseLoader.AllWeapons != null)
                    foreach (var w in DatabaseLoader.AllWeapons)
                    {
                        if (IsPollutedNonItemClass(w.gcType)) continue;
                        if (MatchesRarity(w.gcType, rarity) && RarityHelper.GetItemLevel(w.gcType) <= 11)
                            pool.Add(w.gcType);
                    }
                if (DatabaseLoader.AllArmor != null)
                    foreach (var a in DatabaseLoader.AllArmor)
                    {
                        if (IsPollutedNonItemClass(a.gcType)) continue;
                        if (MatchesRarity(a.gcType, rarity) && RarityHelper.GetItemLevel(a.gcType) <= 11)
                            pool.Add(a.gcType);
                    }
            }

            return pool.Count > 0 ? pool[_rng.Next(pool.Count)] : null;
        }

        private void AddMatchingItemsLevelFiltered(List<string> pool, List<GeneralItemData> items, ItemRarity rarity, int maxItemLevel)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (!MatchesRarity(item.gcType, rarity)) continue;
                int itemLv = RarityHelper.GetItemLevel(item.gcType);
                if (itemLv > maxItemLevel) continue;
                pool.Add(item.gcType);
            }
        }

        /// <summary>
        /// Match GC type to target rarity using existing RarityHelper.
        /// </summary>
        private bool MatchesRarity(string gcType, ItemRarity target)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            if (target == ItemRarity.Mythic) return RarityHelper.IsMythicPALItem(gcType);
            if (RarityHelper.IsMythicPALItem(gcType)) return false;
            return RarityHelper.GetRarityFromTier(RarityHelper.GetTierFromGcType(gcType)) == target;
        }

        private string GetItemLabel(string gcType)
        {
            if (DatabaseLoader.ItemDatabase?.TryGetValue(gcType, out var item) == true)
                return item.name ?? gcType;
            if (DatabaseLoader.GeneralItemDatabase?.TryGetValue(gcType, out var gi) == true)
                return gi.Label ?? gcType;
            return gcType;
        }

        // ═══════════════════════════════════════════════════════════════
        // DEFAULT TREASURE — fallback by tier when no treasure_gen in DB
        // ═══════════════════════════════════════════════════════════════

        private CreatureTreasureData GetDefaultTreasure(string tier) => (tier?.ToUpperInvariant()) switch
        {
            "HERO" or "BOSS" or "DUNGEON_BOSS" => new CreatureTreasureData
            { Generators = { ("HeroGG", 2), ("HeroIG", 1), ("DefaultIG", 2) } },
            "CHAMPION" => new CreatureTreasureData
            { Generators = { ("ChampionGG", 2), ("ChampionIG", 1), ("DefaultIG", 1) } },
            "VETERAN" or "WARMONGER" => new CreatureTreasureData
            { Generators = { ("DefaultIG", 1), ("DefaultGG", 2) } },
            _ => new CreatureTreasureData
            { Generators = { ("DefaultGG", 1) } },
        };

        // ===========================================================================
        // KING'S COIN DROP ROLL
        //
        // KingsCoinIG.gc is an authored item-generator table. Mob loot must reference
        // that generator through creature treasure data; there is no native/authored
        // evidence for an extra tier-wide mob roll. This helper remains for the
        // explicit BARREL path until destroyable loot is fully authored.
        // ===========================================================================
        public LootDrop RollKingsCoin(string tier)
        {
            int pct;
            switch ((tier ?? "").ToUpperInvariant())
            {
                case "HERO":
                case "BOSS":
                case "DUNGEON_BOSS":
                    pct = ServerSettings.Get("kingsCoinPctHero", 10);
                    break;
                case "CHAMPION":
                    pct = ServerSettings.Get("kingsCoinPctChampion", 5);
                    break;
                case "VETERAN":
                case "WARMONGER":
                    pct = ServerSettings.Get("kingsCoinPctVeteran", 2);
                    break;
                case "BARREL":
                    pct = ServerSettings.Get("kingsCoinPctBarrel", 1);
                    break;
                default:
                    pct = ServerSettings.Get("kingsCoinPctGrunt", 1);
                    break;
            }
            if (pct <= 0) return null;
            if (_rng.Next(100) >= pct) return null;

            int count = ServerSettings.Get("kingsCoinDropAmount", 1);
            if (count < 1) count = 1;
            Debug.LogError($"[LOOT-KC] tier={tier} pct={pct}% rolled +{count} Kings Coin(s)");
            return LootDrop.KingsCoin(count);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DATA STRUCTURES
    // ═══════════════════════════════════════════════════════════════════════════

    public class LootDrop
    {
        public bool IsGold;
        public int GoldAmount;
        public bool IsKingsCoin;        // Direct-to-inventory currency drop (QuestItemPAL.Token)
        public int KingsCoinCount;      // Number of coins for this drop
        public bool IsItem => !IsGold && !IsKingsCoin;
        public string GCType;
        public string Label;
        public ItemRarity Rarity;
        public string ScaleMod;
        public int ItemLevel;

        public static LootDrop Gold(int amount) =>
            new LootDrop { IsGold = true, GoldAmount = amount };

        public static LootDrop Item(string gcType, string label, ItemRarity rarity, string scaleMod, int level) =>
            new LootDrop { GCType = gcType, Label = label, Rarity = rarity, ScaleMod = scaleMod, ItemLevel = level };

        // Stacked currency drop. UGS routes via GiveStackedItem("QuestItemPAL.Token", count, 100).
        public static LootDrop KingsCoin(int count) =>
            new LootDrop { IsKingsCoin = true, KingsCoinCount = count };
    }

    public class TreasureEntry
    {
        public ItemRarity Rarity;
        public int Chance;    // 1-in-N (lower = more common)
        public int MinLevel;
        public int MaxLevel;
        public TreasureEntry(ItemRarity r, int c, int min, int max)
        { Rarity = r; Chance = c; MinLevel = min; MaxLevel = max; }
    }

    public class TreasureGenerator
    {
        public TreasureEntry[] Entries;
        public TreasureGenerator(TreasureEntry[] entries) { Entries = entries; }
    }

    public class GoldEntry
    {
        public int Chance;
        public float GoldValue;
        public float Volatility;
        public int MinLevel;
        public int MaxLevel;
        public GoldEntry(int c, float gv, float v, int min, int max)
        { Chance = c; GoldValue = gv; Volatility = v; MinLevel = min; MaxLevel = max; }
    }

    public class GoldGenerator
    {
        public GoldEntry[] Entries;
        public GoldGenerator(GoldEntry[] entries) { Entries = entries; }
    }

    public class CreatureTreasureData
    {
        public List<(string gen, int count)> Generators = new();
    }
}
