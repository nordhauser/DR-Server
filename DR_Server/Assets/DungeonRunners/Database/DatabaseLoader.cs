using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DungeonRunners.Core;
using Debug = UnityEngine.Debug;
using DungeonRunners.Managers;
using System.Text.RegularExpressions;
using Mono.Data.Sqlite;
using DB = DungeonRunners.Database.GameDatabase;

public static class DatabaseLoader
{
    public static string GetDatabaseFilePath(string filename)
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "DungeonRunners", "Database", filename);
#else
        return Path.Combine(Application.dataPath, "..", "Database", filename);
#endif
    }

    public static Dictionary<string, List<DungeonSpawnData>> DungeonSpawns = new Dictionary<string, List<DungeonSpawnData>>();
    public static List<ItemData> AllArmor = new List<ItemData>();
    public static List<ItemData> AllWeapons = new List<ItemData>();
    public static Dictionary<string, ItemData> ItemDatabase = new Dictionary<string, ItemData>();
    public static List<SkillData> Skills = new List<SkillData>();
    public static List<QuestData> Quests = new List<QuestData>();
    public static Dictionary<uint, QuestData> QuestsByHash = new Dictionary<uint, QuestData>();
    public static List<CheckpointData> Checkpoints = new List<CheckpointData>();
    public static List<NPCData> TownNPCs = new List<NPCData>();
    public static List<NPCData> TutorialNPCs = new List<NPCData>();
    public static List<NPCData> PvpNPCs = new List<NPCData>();
    public static List<CreatureData> Creatures = new List<CreatureData>();
    public static List<SummonData> Summons = new List<SummonData>();
    public static Dictionary<string, CreatureData> CreatureDatabase = new Dictionary<string, CreatureData>();
    public static Dictionary<string, SummonData> SummonDatabase = new Dictionary<string, SummonData>();
    public static List<ZonePortalData> ZonePortals = new List<ZonePortalData>();
    public static List<ZoneWaypointData> ZoneWaypoints = new List<ZoneWaypointData>();
    public static Dictionary<string, List<ZonePortalData>> PortalsByZone = new Dictionary<string, List<ZonePortalData>>();
    public static Dictionary<string, List<ZoneWaypointData>> WaypointsByZone = new Dictionary<string, List<ZoneWaypointData>>();
    public static List<ZoneCheckpointData> ZoneCheckpoints = new List<ZoneCheckpointData>();
    public static Dictionary<string, List<ZoneCheckpointData>> CheckpointsByZone = new Dictionary<string, List<ZoneCheckpointData>>();
    public static Dictionary<string, CheckpointData> CheckpointDatabase = new Dictionary<string, CheckpointData>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<ItemData>> Helmets = new Dictionary<string, List<ItemData>>();
    public static Dictionary<string, List<ItemData>> Armors = new Dictionary<string, List<ItemData>>();
    public static Dictionary<string, List<ItemData>> Gloves = new Dictionary<string, List<ItemData>>();
    public static Dictionary<string, List<ItemData>> Boots = new Dictionary<string, List<ItemData>>();
    public static Dictionary<string, List<ItemData>> MeleeWeapons = new Dictionary<string, List<ItemData>>();
    public static Dictionary<string, List<ItemData>> RangedWeapons = new Dictionary<string, List<ItemData>>();
    public static List<GeneralItemData> QuestItems = new List<GeneralItemData>();
    public static List<GeneralItemData> Relics = new List<GeneralItemData>();
    public static List<GeneralItemData> Rings = new List<GeneralItemData>();
    public static List<GeneralItemData> Amulets = new List<GeneralItemData>();
    public static List<GeneralItemData> Potions = new List<GeneralItemData>();
    public static List<GeneralItemData> Skillbooks = new List<GeneralItemData>();
    public static List<GeneralItemData> Keys = new List<GeneralItemData>();
    public static List<GeneralItemData> Scrolls = new List<GeneralItemData>();
    public static List<GeneralItemData> DungeonItems = new List<GeneralItemData>();
    public static List<GeneralItemData> Vouchers = new List<GeneralItemData>();
    public static List<GeneralItemData> ItemPacks = new List<GeneralItemData>();
    public static List<GeneralItemData> Consumables = new List<GeneralItemData>();
    public static Dictionary<string, GeneralItemData> GeneralItemDatabase = new Dictionary<string, GeneralItemData>(StringComparer.OrdinalIgnoreCase);
    public static List<MerchantData> Merchants = new List<MerchantData>();
    public static Dictionary<string, MerchantData> MerchantsByNpc = new Dictionary<string, MerchantData>(StringComparer.OrdinalIgnoreCase);

    // Quest item drops parsed from Q*.gc KillDropTrigger blocks.
    // Keyed by lowercased monster gc_type for fast O(1) lookup at kill time.
    // Each entry: list of (quest_id, item_gc_type, chance) tuples — the kill
    // handler walks these against the player's active quests and rolls each
    // matching one. Loaded from the quest_kill_drops table.
    public class QuestKillDropEntry
    {
        public string QuestId;
        public string ItemGcType;
        public int Chance; // 1-in-N denominator. Chance=20 → 5% per kill.
    }
    public static Dictionary<string, List<QuestKillDropEntry>> QuestKillDropsByMonster
        = new Dictionary<string, List<QuestKillDropEntry>>(StringComparer.OrdinalIgnoreCase);

    [Serializable] public class DungeonSpawnData { public string zoneName; public string gcType; public float posX; public float posY; public float posZ; public float heading; public string encounterGroupKey; public float encounterDifficulty = -1f; }

    public static void LoadAll()
    {
        Debug.Log("Loading ALL data direct from SQLite columns...");
        try
        {
            DB.Initialize();
            LoadSkills(); LoadQuests(); LoadCheckpoints();
            LoadNPCsForZone("town", TownNPCs); LoadNPCsForZone("tutorial", TutorialNPCs); LoadNPCsForZone("pvp_start", PvpNPCs);
            LoadZonePortals(); LoadZoneWaypoints(); LoadZoneCheckpoints();
            LoadCreatures(); LoadSummons();
            LoadGeneralItems(); LoadMerchants(); LoadDungeonSpawns();
            LoadQuestKillDrops();
            PathMapManager.Instance.LoadAllPathMaps();
            LoadEquipment("armor", AllArmor); LoadEquipment("weapons", AllWeapons);
            BuildEquipmentMappings();
            Debug.Log($"ALL LOADED: Skills:{Skills.Count} Quests:{Quests.Count} Creatures:{Creatures.Count} Weapons:{AllWeapons.Count} Armor:{AllArmor.Count} Merchants:{Merchants.Count} TownNPCs:{TownNPCs.Count} TutorialNPCs:{TutorialNPCs.Count} QuestKillDrops:{QuestKillDropsByMonster.Count}");
        }
        catch (Exception ex) { Debug.LogError($"CRITICAL LOAD ERROR: {ex.Message}\n{ex.StackTrace}"); }
    }

    private static void LoadSkills()
    {
        Skills.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM skills"))
            while (r.Read())
                Skills.Add(new SkillData
                {
                    id = DB.GetString(r, "gc_type"),
                    name = DB.GetString(r, "name"),
                    description = DB.GetString(r, "description"),
                    level = DB.GetInt(r, "level", 1),
                    experience = DB.GetInt(r, "experience"),
                    maxLevel = DB.GetInt(r, "max_level", 10),
                    attributes = new SkillAttributes
                    {
                        strength = DB.GetInt(r, "attr_strength"),
                        dexterity = DB.GetInt(r, "attr_dexterity"),
                        vitality = DB.GetInt(r, "attr_vitality"),
                        intelligence = DB.GetInt(r, "attr_intelligence"),
                        wisdom = DB.GetInt(r, "attr_wisdom"),
                        spirit = DB.GetInt(r, "attr_spirit"),
                        perception = DB.GetInt(r, "attr_perception"),
                        agility = DB.GetInt(r, "attr_agility")
                    }
                });
    }

    private static void LoadQuests()
    {
        Quests.Clear(); QuestsByHash.Clear();
        var objMap = new Dictionary<string, List<QuestObjective>>(StringComparer.OrdinalIgnoreCase);
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM quest_objective_templates"))
            while (r.Read())
            {
                string qid = DB.GetString(r, "quest_id");
                if (!objMap.ContainsKey(qid)) objMap[qid] = new List<QuestObjective>();
                objMap[qid].Add(new QuestObjective
                {
                    name = DB.GetString(r, "name"),
                    type = DB.GetString(r, "type"),
                    target = DB.GetString(r, "target"),
                    count = DB.GetInt(r, "required_count", 1),
                    label = DB.GetString(r, "label")
                });
            }
        var rwdMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM quest_reward_items"))
            while (r.Read())
            {
                string qid = DB.GetString(r, "quest_id");
                if (!rwdMap.ContainsKey(qid)) rwdMap[qid] = new List<string>();
                rwdMap[qid].Add(DB.GetString(r, "gc_type"));
            }
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM quests"))
            while (r.Read())
            {
                string qid = DB.GetString(r, "id");
                var q = new QuestData
                {
                    id = qid,
                    name = DB.GetString(r, "name"),
                    description = DB.GetString(r, "description"),
                    level = DB.GetInt(r, "level"),
                    maxLevel = DB.GetInt(r, "max_level"),
                    type = DB.GetString(r, "quest_type", "quest"),
                    faction = DB.GetString(r, "faction"),
                    npc = DB.GetString(r, "npc"),
                    npc2 = DB.GetString(r, "npc2"),
                    onAcceptItem = DB.GetString(r, "on_accept_item"),
                    requiredQuest = DB.GetString(r, "required_quest"),
                    followupQuest = DB.GetString(r, "followup_quest"),
                    uiZoneInfo = DB.GetString(r, "ui_zone_info"),
                    status = DB.GetString(r, "status", "available"),
                    hash = DB.GetUInt(r, "hash"),
                    tokenReward = DB.GetInt(r, "token_reward"),
                    cashReward = DB.GetFloat(r, "cash_reward"),
                    grantXPBuff = DB.GetInt(r, "grant_xp_buff") == 1,
                    repeatable = DB.GetInt(r, "repeatable") == 1,
                    baseClass = DB.GetString(r, "base_class", "Quest"),
                    rewardItemGenerator = DB.GetString(r, "reward_item_generator"),
                    numRewardItems = DB.GetInt(r, "num_reward_items"),
                    rewardItemDescription = DB.GetString(r, "reward_item_description"),
                    rewardIconGenerator = DB.GetString(r, "reward_icon_generator"),
                    rewardItemsSoulBound = DB.GetInt(r, "reward_items_soulbound") == 1,
                    rewardItemsNoSell = DB.GetInt(r, "reward_items_nosell") == 1,
                    rewardItemsDropped = DB.GetInt(r, "reward_items_dropped") == 1,
                    rewardItemsMemberOnly = DB.GetInt(r, "reward_items_member_only") == 1,
                    onAcceptSoulBound = DB.GetInt(r, "on_accept_soulbound") == 1,
                    minRepeatSeconds = DB.GetInt(r, "min_repeat_seconds"),
                    objectives = objMap.ContainsKey(qid) ? objMap[qid] : new List<QuestObjective>(),
                    rewards = new QuestRewards
                    {
                        experience = DB.GetInt(r, "reward_experience"),
                        gold = DB.GetInt(r, "reward_gold"),
                        items = rwdMap.ContainsKey(qid) ? rwdMap[qid] : new List<string>()
                    }
                };
                Quests.Add(q); if (q.hash != 0) QuestsByHash[q.hash] = q;
            }
    }

    private static void LoadCheckpoints()
    {
        Checkpoints.Clear(); CheckpointDatabase.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM checkpoints"))
            while (r.Read())
            {
                string cpName = DB.GetString(r, "name");
                var cp = new CheckpointData
                {
                    id = "world.checkpoints." + cpName.Replace(" ", ""),
                    name = cpName,
                    description = DB.GetString(r, "description"),
                    zone = DB.GetString(r, "zone"),
                    mapId = DB.GetString(r, "map_id"),
                    spawnPoint = DB.GetString(r, "spawn_point"),
                    position = new PositionData { x = DB.GetFloat(r, "pos_x"), y = DB.GetFloat(r, "pos_y"), z = DB.GetFloat(r, "pos_z") },
                    rotation = new PositionData { x = 0, y = 0, z = 0 },
                    order = DB.GetInt(r, "display_order"),
                    isActive = DB.GetInt(r, "is_active", 1) == 1,
                    levelRequirement = DB.GetInt(r, "level_requirement", 1),
                    unlockQuest = DB.GetString(r, "unlock_quest"),
                    image = DB.GetString(r, "image")
                };
                Checkpoints.Add(cp); CheckpointDatabase[cp.id] = cp;
            }
    }

    private static void LoadNPCsForZone(string zoneType, List<NPCData> target)
    {
        target.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM npcs WHERE zone_type=@z", ("@z", zoneType)))
            while (r.Read())
                target.Add(new NPCData
                {
                    gcType = DB.GetString(r, "gc_type"),
                    name = DB.GetString(r, "name"),
                    posX = DB.GetFloat(r, "pos_x"),
                    posY = DB.GetFloat(r, "pos_y"),
                    posZ = DB.GetFloat(r, "pos_z"),
                    heading = DB.GetFloat(r, "heading"),
                    hitPoints = DB.GetInt(r, "hit_points"),
                    manaPoints = DB.GetInt(r, "mana_points")
                });
    }

    private static void LoadZonePortals()
    {
        ZonePortals.Clear(); PortalsByZone.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM zone_portals"))
            while (r.Read())
            {
                var p = new ZonePortalData
                {
                    id = DB.GetInt(r, "id"),
                    zone = DB.GetString(r, "zone"),
                    name = DB.GetString(r, "name"),
                    gcType = DB.GetString(r, "gc_type"),
                    posX = DB.GetFloat(r, "pos_x"),
                    posY = DB.GetFloat(r, "pos_y"),
                    posZ = DB.GetFloat(r, "pos_z"),
                    heading = DB.GetFloat(r, "heading"),
                    width = DB.GetInt(r, "width"),
                    height = DB.GetInt(r, "height"),
                    targetZone = DB.GetString(r, "target_zone"),
                    spawnPoint = DB.GetString(r, "spawn_point"),
                    color = DB.GetUInt(r, "color")
                };
                ZonePortals.Add(p); string k = p.zone.ToLower();
                if (!PortalsByZone.ContainsKey(k)) PortalsByZone[k] = new List<ZonePortalData>();
                PortalsByZone[k].Add(p);
            }
    }

    private static void LoadZoneWaypoints()
    {
        ZoneWaypoints.Clear(); WaypointsByZone.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM zone_waypoints"))
            while (r.Read())
            {
                var w = new ZoneWaypointData
                {
                    id = DB.GetInt(r, "id"),
                    zone = DB.GetString(r, "zone"),
                    name = DB.GetString(r, "name"),
                    posX = DB.GetFloat(r, "pos_x"),
                    posY = DB.GetFloat(r, "pos_y"),
                    posZ = DB.GetFloat(r, "pos_z"),
                    heading = DB.GetFloat(r, "heading")
                };
                ZoneWaypoints.Add(w); string k = w.zone.ToLower();
                if (!WaypointsByZone.ContainsKey(k)) WaypointsByZone[k] = new List<ZoneWaypointData>();
                WaypointsByZone[k].Add(w);
            }
    }

    private static void LoadZoneCheckpoints()
    {
        ZoneCheckpoints.Clear(); CheckpointsByZone.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM zone_checkpoints"))
            while (r.Read())
            {
                var zc = new ZoneCheckpointData
                {
                    id = DB.GetInt(r, "id"),
                    zone = DB.GetString(r, "zone"),
                    name = DB.GetString(r, "name"),
                    gcType = DB.GetString(r, "gc_type"),
                    posX = DB.GetFloat(r, "pos_x"),
                    posY = DB.GetFloat(r, "pos_y"),
                    posZ = DB.GetFloat(r, "pos_z"),
                    heading = DB.GetFloat(r, "heading")
                };
                ZoneCheckpoints.Add(zc); string k = zc.zone.ToLower();
                if (!CheckpointsByZone.ContainsKey(k)) CheckpointsByZone[k] = new List<ZoneCheckpointData>();
                CheckpointsByZone[k].Add(zc);
            }
    }

    private static void LoadCreatures()
    {
        Creatures.Clear(); CreatureDatabase.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM creatures"))
            while (r.Read())
            {
                var cr = new CreatureData
                {
                    gcType = DB.GetString(r, "gc_type"),
                    behaviourType = DB.GetString(r, "behaviour_type"),
                    name = DB.GetString(r, "name"),
                    faction = DB.GetString(r, "faction"),
                    creatureType = DB.GetString(r, "creature_type"),
                    element = DB.GetString(r, "element"),
                    creatureDifficulty = DB.GetString(r, "creature_difficulty"),
                    hitPoints = DB.GetInt(r, "hit_points"),
                    manaPoints = DB.GetInt(r, "mana_points"),
                    baseDamage = DB.GetInt(r, "base_damage"),
                    speed = DB.GetString(r, "speed"),
                    attackRange = DB.GetString(r, "attack_range"),
                    attackRating = DB.GetString(r, "attack_rating"),
                    defenseRating = DB.GetString(r, "defense_rating"),
                    criticalChance = DB.GetString(r, "critical_chance"),
                    damageMod = DB.GetString(r, "damage_mod"),
                    divineResist = DB.GetString(r, "divine_resist"),
                    fireResist = DB.GetString(r, "fire_resist"),
                    iceResist = DB.GetString(r, "ice_resist"),
                    poisonResist = DB.GetString(r, "poison_resist"),
                    shadowResist = DB.GetString(r, "shadow_resist")
                };
                Creatures.Add(cr); CreatureDatabase[cr.gcType.ToLower()] = cr;
            }
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM creature_manipulators"))
            while (r.Read())
            {
                string gc = DB.GetString(r, "creature_gc_type").ToLower();
                if (CreatureDatabase.TryGetValue(gc, out var cr))
                {
                    if (cr.manipulators == null) cr.manipulators = new Dictionary<string, ManipulatorData>();
                    string slot = DB.GetString(r, "slot");
                    var m = new ManipulatorData { gcType = DB.GetString(r, "gc_type") };
                    m.properties["Equipable"] = DB.GetString(r, "equipable"); m.properties["SlotType"] = DB.GetString(r, "slot_type");
                    m.properties["WeaponClass"] = DB.GetString(r, "weapon_class"); m.properties["Range"] = DB.GetString(r, "weapon_range");
                    m.properties["CoolDown"] = DB.GetString(r, "cooldown"); m.properties["Damage"] = DB.GetString(r, "damage");
                    cr.manipulators[slot] = m;
                }
            }
        Debug.Log($"Loaded {Creatures.Count} creatures");
    }

    private static void LoadSummons()
    {
        Summons.Clear(); SummonDatabase.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM summons"))
            while (r.Read())
            {
                var s = new SummonData
                {
                    gcType = DB.GetString(r, "gc_type"),
                    behaviourType = DB.GetString(r, "behaviour_type"),
                    name = DB.GetString(r, "name"),
                    hitPoints = DB.GetInt(r, "hit_points"),
                    manaPoints = DB.GetInt(r, "mana_points"),
                    summonType = DB.GetString(r, "summon_type"),
                    element = DB.GetString(r, "element"),
                    description = DB.GetString(r, "description")
                };
                Summons.Add(s); SummonDatabase[s.gcType.ToLower()] = s;
            }
    }

    private static void LoadGeneralItems()
    {
        GeneralItemDatabase.Clear();
        var catMap = new Dictionary<string, List<GeneralItemData>> {
            {"quest_items",QuestItems},{"relics",Relics},{"rings",Rings},{"amulets",Amulets},
            {"potions",Potions},{"skillbooks",Skillbooks},{"keys",Keys},{"scrolls",Scrolls},
            {"dungeon_items",DungeonItems},{"vouchers",Vouchers},{"item_packs",ItemPacks},{"consumables",Consumables} };
        foreach (var kv in catMap) kv.Value.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM items"))
            while (r.Read())
            {
                var item = new GeneralItemData
                {
                    gcType = DB.GetString(r, "gc_type"),
                    baseType = DB.GetString(r, "base_type"),
                    Label = DB.GetString(r, "label"),
                    InventoryIcon = DB.GetString(r, "inventory_icon"),
                    GroundObject = DB.GetString(r, "ground_object"),
                    Stackable = DB.GetInt(r, "stackable") == 1,
                    InventoryWidth = DB.GetInt(r, "inventory_width", 1),
                    InventoryHeight = DB.GetInt(r, "inventory_height", 1),
                    modCount = DB.GetInt(r, "mod_count", 1),
                    DropLevel = DB.GetInt(r, "drop_level"),
                    LevelReq = DB.GetInt(r, "level_req")
                };
                string cat = DB.GetString(r, "category");
                if (catMap.ContainsKey(cat)) catMap[cat].Add(item);
                if (!string.IsNullOrEmpty(item.gcType)) GeneralItemDatabase[item.gcType.ToLower()] = item;
            }
    }

    private static void LoadMerchants()
    {
        Merchants.Clear(); MerchantsByNpc.Clear();
        var mMap = new Dictionary<int, MerchantData>();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM merchants"))
            while (r.Read())
            {
                int mid = DB.GetInt(r, "id");
                var m = new MerchantData
                {
                    npcGcType = DB.GetString(r, "npc_gc_type"),
                    merchantGcType = DB.GetString(r, "merchant_gc_type"),
                    inventories = new List<MerchantInventoryData>()
                };
                mMap[mid] = m; Merchants.Add(m); MerchantsByNpc[m.npcGcType] = m;
            }
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM merchant_inventories ORDER BY merchant_id,inv_id"))
            while (r.Read())
            {
                int mid = DB.GetInt(r, "merchant_id");
                if (!mMap.ContainsKey(mid)) continue;
                mMap[mid].inventories.Add(new MerchantInventoryData
                {
                    name = DB.GetString(r, "name"),
                    gcType = DB.GetString(r, "gc_type"),
                    id = DB.GetInt(r, "inv_id"),
                    label = DB.GetString(r, "label"),
                    staticContents = DB.GetInt(r, "static_contents", 1) == 1,
                    autoGenerateItems = DB.GetInt(r, "auto_generate") == 1,
                    itemGenerator = DB.GetString(r, "item_generator"),
                    minItemLevel = DB.GetInt(r, "min_item_level"),
                    maxItemLevel = DB.GetInt(r, "max_item_level"),
                    regenerateIntervalSeconds = DB.GetInt(r, "regen_seconds"),
                    width = DB.GetInt(r, "width", 10),
                    height = DB.GetInt(r, "height", 10),
                    items = new List<MerchantItemData>()
                });
            }
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM merchant_inventory_items ORDER BY merchant_id,inv_id"))
            while (r.Read())
            {
                int mid = DB.GetInt(r, "merchant_id"); int iid = DB.GetInt(r, "inv_id");
                if (!mMap.ContainsKey(mid)) continue;
                var inv = mMap[mid].inventories.Find(i => i.id == iid); if (inv == null) continue;
                inv.items.Add(new MerchantItemData
                {
                    gcType = DB.GetString(r, "item_gc_type"),
                    inventoryX = DB.GetInt(r, "inventory_x"),
                    inventoryY = DB.GetInt(r, "inventory_y"),
                    id = DB.GetInt(r, "item_slot_id"),
                    quantity = DB.GetInt(r, "quantity", 1)
                });
            }
        Debug.Log($"Loaded {Merchants.Count} merchants");
    }

    private static void LoadDungeonSpawns()
    {
        DungeonSpawns.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, "SELECT * FROM dungeon_spawns"))
            while (r.Read())
            {
                var s = new DungeonSpawnData
                {
                    zoneName = DB.GetString(r, "zone_name"),
                    gcType = DB.GetString(r, "gc_type"),
                    posX = DB.GetFloat(r, "pos_x"),
                    posY = DB.GetFloat(r, "pos_y"),
                    posZ = DB.GetFloat(r, "pos_z"),
                    heading = DB.GetFloat(r, "heading")
                };
                if (!DungeonSpawns.ContainsKey(s.zoneName)) DungeonSpawns[s.zoneName] = new List<DungeonSpawnData>();
                DungeonSpawns[s.zoneName].Add(s);
            }
    }

    // Load all quest item drop rules from quest_kill_drops table.
    // The table is populated by INSERT_quest_kill_drops.sql which was parsed
    // from KillDropTrigger blocks in the original Q*.gc files. Each row says
    // "if a player has quest_id active and kills monster_type, roll 1-in-chance
    // and on hit drop item_gc_type at the mob position." Index by lowercased
    // monster_type for fast O(1) lookup at kill time.
    private static void LoadQuestKillDrops()
    {
        QuestKillDropsByMonster.Clear();
        try
        {
            using (var c = DB.GetConnection())
            using (var r = DB.ExecuteReader(c,
                "SELECT quest_id, monster_type, item_gc_type, chance FROM quest_kill_drops"))
            {
                while (r.Read())
                {
                    string monster = DB.GetString(r, "monster_type");
                    if (string.IsNullOrEmpty(monster)) continue;
                    string key = monster.ToLowerInvariant();

                    if (!QuestKillDropsByMonster.TryGetValue(key, out var list))
                    {
                        list = new List<QuestKillDropEntry>();
                        QuestKillDropsByMonster[key] = list;
                    }
                    list.Add(new QuestKillDropEntry
                    {
                        QuestId = DB.GetString(r, "quest_id"),
                        ItemGcType = DB.GetString(r, "item_gc_type"),
                        Chance = DB.GetInt(r, "chance", 100),
                    });
                }
            }
            int totalRules = 0;
            foreach (var kvp in QuestKillDropsByMonster) totalRules += kvp.Value.Count;
            Debug.LogError($"[QuestKillDrops] Loaded {totalRules} drop rules across {QuestKillDropsByMonster.Count} distinct monster types");
        }
        catch (Exception ex)
        {
            // Table missing is non-fatal — quest item drops just won't fire
            Debug.LogError($"[QuestKillDrops] Could not load quest_kill_drops table: {ex.Message}");
        }
    }

    private static void LoadEquipment(string table, List<ItemData> target)
    {
        target.Clear();
        using (var c = DB.GetConnection()) using (var r = DB.ExecuteReader(c, $"SELECT * FROM [{table}]"))
            while (r.Read())
            {
                var item = new ItemData
                {
                    gcType = DB.GetString(r, "gc_type"),
                    name = DB.GetString(r, "label"),
                    description = DB.GetString(r, "description"),
                    goldValue = DB.GetFloat(r, "gold_value"),
                    defenseRating = DB.GetFloat(r, "defense_rating"),
                    damage = DB.GetFloat(r, "damage"),
                    range = DB.GetInt(r, "weapon_range"),
                    cooldown = DB.GetFloat(r, "cooldown", 1f),
                    slotType = DB.GetString(r, "slot_type"),
                    weaponClass = DB.GetString(r, "weapon_class"),
                    inventoryWidth = DB.GetInt(r, "inventory_width", 1),
                    inventoryHeight = DB.GetInt(r, "inventory_height", 1),
                    inventoryIcon = DB.GetString(r, "inventory_icon"),
                    groundObject = DB.GetString(r, "ground_object"),
                    equipable = DB.GetInt(r, "equipable", 1) == 1,
                    modCount = DB.GetInt(r, "mod_count", 1)
                };
                target.Add(item); ItemDatabase[item.gcType.ToLower()] = item;
            }
    }

    // ═══ UTILITY METHODS ═══

    public static int GetRingModSlotCount(string gcType) { var i = FindGeneralItem(gcType); return (i != null && i.modCount > 0) ? i.modCount : 1; }
    public static int GetAmuletModSlotCount(string gcType) { var i = FindGeneralItem(gcType); return (i != null && i.modCount > 0) ? i.modCount : 1; }
    public static string GetRingModifierClass(string gcType)
    {
        string l = gcType.ToLower(); if (l.Contains("mythic")) return "RingModPAL.Mythic.Mod1";
        if (l.Contains("rare")) return "RingModPAL.Rare.Mod1"; if (l.Contains("superior")) return "RingModPAL.Superior.Mod1";
        if (l.Contains("magic")) return "RingModPAL.Magic.Mod1"; return "RingModPAL.Mod1";
    }
    public static string GetAmuletModifierClass(string gcType)
    {
        string l = gcType.ToLower(); if (l.Contains("mythic")) return "AmuletModPAL.Mythic.Mod1";
        if (l.Contains("rare")) return "AmuletModPAL.Rare.Mod1"; if (l.Contains("superior")) return "AmuletModPAL.Superior.Mod1";
        if (l.Contains("magic")) return "AmuletModPAL.Magic.Mod1"; return "AmuletModPAL.Mod1";
    }

    public static GeneralItemData FindGeneralItem(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return null;
        if (GeneralItemDatabase.TryGetValue(gcType, out var i)) return i;
        if (gcType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
            if (GeneralItemDatabase.TryGetValue(gcType.Substring(10), out i)) return i;
        return null;
    }

    public static ItemData FindItem(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return null;
        string k = gcType.ToLower();
        if (ItemDatabase.TryGetValue(k, out ItemData item)) return item;
        if (k.StartsWith("items.pal.")) if (ItemDatabase.TryGetValue(k.Substring(10), out item)) return item;
        return null;
    }

    public static CreatureData FindCreature(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return null;
        CreatureDatabase.TryGetValue(gcType.ToLower(), out var cr); return cr;
    }

    public static List<CreatureData> GetCreaturesByFaction(string faction)
    {
        var r = new List<CreatureData>(); foreach (var c in Creatures)
            if (c.faction != null && c.faction.Equals(faction, StringComparison.OrdinalIgnoreCase)) r.Add(c); return r;
    }
    public static List<CreatureData> GetCreaturesByTier(string tier)
    {
        var r = new List<CreatureData>(); foreach (var c in Creatures)
            if (c.creatureDifficulty != null && c.creatureDifficulty.Equals(tier, StringComparison.OrdinalIgnoreCase)) r.Add(c); return r;
    }
    public static List<CreatureData> GetCreaturesByElement(string element)
    {
        var r = new List<CreatureData>(); foreach (var c in Creatures)
            if (c.element != null && c.element.Equals(element, StringComparison.OrdinalIgnoreCase)) r.Add(c); return r;
    }

    // ═══ EQUIPMENT MAPPING ═══
    private static void BuildEquipmentMappings()
    {
        Helmets.Clear(); Armors.Clear(); Gloves.Clear(); Boots.Clear(); MeleeWeapons.Clear(); RangedWeapons.Clear();
        foreach (var item in AllArmor)
        {
            string cat = GetItemCategory(item.gcType); string s = item.slotType ?? "0"; string l = item.gcType.ToLower();
            Dictionary<string, List<ItemData>> target = null;
            if (s == "1" || l.Contains("helm") || l.Contains("hat") || l.Contains("cap")) target = Helmets;
            else if (s == "6" || l.Contains("armor") || l.Contains("chest") || l.Contains("body") || l.Contains("vest")) target = Armors;
            else if (s == "3" || l.Contains("glove") || l.Contains("hand") || l.Contains("gauntlet")) target = Gloves;
            else if (s == "4" || l.Contains("boot") || l.Contains("foot") || l.Contains("feet")) target = Boots;
            if (target != null) { if (!target.ContainsKey(cat)) target[cat] = new List<ItemData>(); target[cat].Add(item); }
        }
        foreach (var item in AllWeapons)
        {
            string cat = GetItemCategory(item.gcType); string l = item.gcType.ToLower();
            if (item.weaponClass?.Contains("RANGED") == true || l.Contains("gun") || l.Contains("bow") || l.Contains("crossbow"))
            {
                if (!RangedWeapons.ContainsKey(cat)) RangedWeapons[cat] = new List<ItemData>(); RangedWeapons[cat].Add(item);
            }
            else { if (!MeleeWeapons.ContainsKey(cat)) MeleeWeapons[cat] = new List<ItemData>(); MeleeWeapons[cat].Add(item); }
        }
    }
    private static string GetItemCategory(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return "unknown"; string f = gcType.ToLower().Split('.')[0];
        if (f.EndsWith("pal")) f = f.Substring(0, f.Length - 3);
        while (f.Length > 0 && char.IsDigit(f[f.Length - 1])) f = f.Substring(0, f.Length - 1); return f;
    }

    // ═══ RING/AMULET MODIFIER METHODS ═══
    public static int GetRingModifierCount(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return 0;
        string baseKey = gcType.ToLower();
        int count = 0;
        for (int i = 1; i <= 10; i++)
            if (GeneralItemDatabase.ContainsKey($"{baseKey}.mod{i}")) count++;
        return count;
    }

    public static List<string> GetRingModifiers(string gcType)
    {
        var modifiers = new List<string>();
        if (string.IsNullOrEmpty(gcType)) { modifiers.Add("RingModPAL.Mod1"); return modifiers; }
        // GeneralItemDatabase stores keys without the `items.pal.` prefix
        // (e.g. `ringmythicpal.ringmythic15.mod1`). Callers pass either form
        // depending on context. Strip the prefix so the lookup hits the
        // actual rows instead of falling back to the wrong placeholder
        // count via the `ringmodpal.mod1` sentinel.
        string baseKey = gcType.ToLowerInvariant();
        if (baseKey.StartsWith("items.pal.")) baseKey = baseKey.Substring("items.pal.".Length);
        for (int i = 1; i <= 10; i++)
        {
            string modKey = $"{baseKey}.mod{i}";
            if (GeneralItemDatabase.ContainsKey(modKey)) modifiers.Add(modKey);
        }
        if (modifiers.Count == 0) modifiers.Add("ringmodpal.mod1");
        return modifiers;
    }

    public static List<string> GetAmuletModifiers(string gcType)
    {
        var modifiers = new List<string>();
        if (string.IsNullOrEmpty(gcType)) { modifiers.Add("AmuletModPAL.Mod1"); return modifiers; }
        // See GetRingModifiers — same prefix-strip rationale.
        string baseKey = gcType.ToLowerInvariant();
        if (baseKey.StartsWith("items.pal.")) baseKey = baseKey.Substring("items.pal.".Length);
        for (int i = 1; i <= 10; i++)
        {
            string modKey = $"{baseKey}.mod{i}";
            if (GeneralItemDatabase.ContainsKey(modKey)) modifiers.Add(modKey);
        }
        if (modifiers.Count == 0) modifiers.Add("amuletmodpal.mod1");
        return modifiers;
    }

    public static int GetAmuletModifierCount(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return 0;
        string baseKey = gcType.ToLower();
        int count = 0;
        for (int i = 1; i <= 10; i++)
            if (GeneralItemDatabase.ContainsKey($"{baseKey}.mod{i}")) count++;
        return count;
    }

    // ═══ HASH ═══
    public static uint ComputeDJB2Hash(string s)
    {
        uint h = 5381;
        foreach (char c in s.ToLowerInvariant()) h = h * 33 + (uint)c;
        return h;
    }

    private static void AddManualHashMapping(uint hash, string questId)
    {
        var quest = Quests.Find(q => q.id == questId);
        if (quest != null) QuestsByHash[hash] = quest;
    }

    // ═══ ZONE LOOKUP HELPERS ═══
    public static List<ZoneCheckpointData> GetCheckpointsForZone(string zoneName)
    {
        string key = zoneName.ToLower();
        if (CheckpointsByZone.TryGetValue(key, out var checkpoints)) return checkpoints;
        return new List<ZoneCheckpointData>();
    }

    public static ZoneCheckpointData GetCheckpointByGcType(string gcType)
    {
        if (string.IsNullOrEmpty(gcType) || CheckpointsByZone == null) return null;
        string searchKey = gcType.ToLower();
        foreach (var checkpointList in CheckpointsByZone.Values)
            foreach (var checkpoint in checkpointList)
                if (checkpoint.gcType.ToLower() == searchKey) return checkpoint;
        return null;
    }

    public static CheckpointData FindCheckpoint(string checkpointId)
    {
        if (string.IsNullOrEmpty(checkpointId)) return null;
        CheckpointDatabase.TryGetValue(checkpointId, out var cp);
        return cp;
    }

    public static List<ZonePortalData> GetPortalsForZone(string zoneName)
    {
        string key = zoneName.ToLower();
        if (PortalsByZone.TryGetValue(key, out var portals)) return portals;
        return new List<ZonePortalData>();
    }

    public static List<ZoneWaypointData> GetWaypointsForZone(string zoneName)
    {
        string key = zoneName.ToLower();
        if (WaypointsByZone.TryGetValue(key, out var waypoints)) return waypoints;
        return new List<ZoneWaypointData>();
    }

    // ═══ FIND SUMMON ═══
    public static SummonData FindSummon(string gcType)
    {
        if (string.IsNullOrEmpty(gcType)) return null;
        SummonDatabase.TryGetValue(gcType.ToLower(), out var summon);
        return summon;
    }

    // ═══ FULL VALIDATION ═══
    private static void ValidateDatabases()
    {
        int deprecatedCount = 0;
        int modCount = 0;
        foreach (var item in AllArmor)
        {
            if (item.gcType.ToLower().Contains("deprecated")) deprecatedCount++;
            if (Regex.IsMatch(item.gcType, @"\.Mod\d+$")) modCount++;
        }
        foreach (var item in AllWeapons)
        {
            if (item.gcType.ToLower().Contains("deprecated")) deprecatedCount++;
            if (Regex.IsMatch(item.gcType, @"\.Mod\d+$")) modCount++;
        }
        var allGeneralItems = new[] {
            ("QuestItems",QuestItems),("Relics",Relics),("Rings",Rings),("Amulets",Amulets),
            ("Potions",Potions),("Skillbooks",Skillbooks),("Keys",Keys),("Scrolls",Scrolls),
            ("DungeonItems",DungeonItems),("Vouchers",Vouchers),("ItemPacks",ItemPacks),("Consumables",Consumables) };
        foreach (var (categoryName, itemList) in allGeneralItems)
            foreach (var item in itemList)
                if (item.gcType.ToLower().Contains("deprecated")) deprecatedCount++;
        if (deprecatedCount > 0 || modCount > 0)
            Debug.LogError($"Database validation: {deprecatedCount} deprecated, {modCount} mod items");
        else
            Debug.Log("Database validation passed");
    }

    private static int GetTotalItems(Dictionary<string, List<ItemData>> categoryDict)
    {
        int total = 0;
        foreach (var kvp in categoryDict) total += kvp.Value.Count;
        return total;
    }

    // ═══ CLASS CONFIG — read from class_definitions table ═══
    public static string ReadGameFileContent(string category, string key = "config") { return null; }
}

// ═══ DATA CLASSES ═══
[Serializable]
public class GeneralItemData
{
    public string gcType; public string baseType; public string Label;
    public string InventoryIcon; public string GroundObject; public bool Stackable;
    public int InventoryWidth = 1; public int InventoryHeight = 1; public int DropLevel; public int LevelReq; public int modCount;
}

[Serializable]
public class ItemData
{
    public string gcType; public string name; public string description;
    public float goldValue; public float defenseRating; public float damage; public int range; public float cooldown;
    public string slotType; public string weaponClass; public int inventoryWidth; public int inventoryHeight;
    public string inventoryIcon; public string groundObject; public bool equipable; public int modCount = 3; public int requiredLevel = 1;
    /// <summary>
    /// Get item level from GC class using PAL tier number (NOT suffix variant).
    /// GC format: items.pal.1HAxe2PAL.1HAxe2-5 → PAL tier=2 → level=11
    /// The suffix (-5) is a visual variant (1-12), the PAL number determines item level.
    /// Binary-verified: item level = (PAL_tier - 1) * 10 + 1
    /// </summary>
    public static int GetRequiredLevelFromGCClass(string gcClass)
    {
        if (string.IsNullOrEmpty(gcClass)) return 1;
        // Extract PAL tier: find digits immediately before LAST "PAL" (case-insensitive)
        // Must use LastIndexOf because gc_types start with "items.pal." which has "pal" too!
        int palIdx = gcClass.LastIndexOf("PAL", System.StringComparison.OrdinalIgnoreCase);
        if (palIdx > 0)
        {
            int numEnd = palIdx;
            int numStart = numEnd - 1;
            while (numStart >= 0 && char.IsDigit(gcClass[numStart]))
                numStart--;
            numStart++;
            if (numStart < numEnd)
            {
                string tierStr = gcClass.Substring(numStart, numEnd - numStart);
                if (int.TryParse(tierStr, out int palTier))
                {
                    if (palTier <= 1) return 1;
                    return (palTier - 1) * 10 + 1;
                }
            }
        }
        return 1;
    }
}

[Serializable] public class SkillData { public string id; public string name; public string description; public int level; public int experience; public int maxLevel; public SkillAttributes attributes; }
[Serializable] public class SkillAttributes { public int strength; public int dexterity; public int vitality; public int intelligence; public int wisdom; public int spirit; public int perception; public int agility; }
[Serializable] public class QuestData { public string id; public string name; public string description; public int level; public int maxLevel; public string type; public string faction; public string npc; public string npc2; public string onAcceptItem; public string baseClass; public int tokenReward; public float cashReward; public bool grantXPBuff; public bool repeatable; public string requiredQuest; public string followupQuest; public string uiZoneInfo; public List<QuestObjective> objectives; public QuestRewards rewards; public string status; public uint hash; public string rewardItemGenerator; public int numRewardItems; public string rewardItemDescription; public string rewardIconGenerator; public bool rewardItemsSoulBound; public bool rewardItemsNoSell; public bool rewardItemsDropped; public bool rewardItemsMemberOnly; public bool onAcceptSoulBound; public int minRepeatSeconds; }
[Serializable] public class QuestObjective { public string type; public string target; public int count; public bool completed; public string label; public string name; }
[Serializable] public class QuestRewards { public int experience; public int gold; public List<string> items; }
[Serializable] public class CheckpointData { public string id; public string name; public string description; public PositionData position; public PositionData rotation; public string mapId; public string zone; public int order; public string spawnPoint; public bool isActive; public int levelRequirement; public string unlockQuest; public string image; }
[Serializable] public class NPCData { public string name; public string gcType; public float posX; public float posY; public float posZ; public float heading; public int hitPoints; public int manaPoints; }
[Serializable] public class PositionData { public float x; public float y; public float z; }

public static class MonsterHealthTable
{
    private const float LEVEL_1_HP = 60.5f; private const float LEVEL_100_HP = 5452f; private const float SLOPE = (LEVEL_100_HP - LEVEL_1_HP) / 99f;
    private static readonly Dictionary<string, float> DifficultyModifiers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { { "FODDER", 0.5f }, { "RECRUIT", 1f }, { "VETERAN", 2f }, { "WARMONGER", 2.5f }, { "CHAMPION", 4f }, { "HERO", 7f }, { "DUNGEON_BOSS", 8f }, { "BOSS", 8f } };
    public static float GetBaseHP(int level) { level = Math.Max(1, Math.Min(100, level)); return LEVEL_1_HP + (level - 1) * SLOPE; }
    public static float GetDifficultyModifier(string d) { if (string.IsNullOrEmpty(d)) return 1f; return DifficultyModifiers.TryGetValue(d, out float m) ? m : 1f; }
    public static int CalculateHP(int level, string diff) { return (int)(GetBaseHP(level) * GetDifficultyModifier(diff)); }
    public static uint CalculateHPWire(int level, string diff, float mod = 1f) { return (uint)(CalculateHP(level, diff) * mod * 256); }
}

[Serializable]
public class CreatureData
{
    public string gcType; public string behaviourType; public string name; public string faction; public string creatureType;
    public string element; public string creatureDifficulty; public string tier => creatureDifficulty;
    public string speed; public string attackRange; public int hitPoints; public int manaPoints; public int baseDamage; public float maxHealth = 1f;
    public string attackRating; public string defenseRating; public string damageMod; public string criticalChance;
    public string divineResist; public string fireResist; public string iceResist; public string poisonResist; public string shadowResist;
    [NonSerialized] public Dictionary<string, ManipulatorData> manipulators;
    public float GetFloat(string val, float fb = 0f) { if (string.IsNullOrEmpty(val)) return fb; return float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : fb; }
    public float AttackRatingF => GetFloat(attackRating, 1f); public float DefenseRatingF => GetFloat(defenseRating, 1f);
    public float DamageModF => GetFloat(damageMod, 1f); public float CritChanceF => GetFloat(criticalChance, 0f);
    public float DivineResistF => GetFloat(divineResist, 0f); public float FireResistF => GetFloat(fireResist, 0f);
    public float IceResistF => GetFloat(iceResist, 0f); public float PoisonResistF => GetFloat(poisonResist, 0f);
    public float ShadowResistF => GetFloat(shadowResist, 0f);
}

[Serializable] public class ManipulatorData { public string gcType; public Dictionary<string, string> properties; public ManipulatorData() { properties = new Dictionary<string, string>(); } }
[Serializable] public class SummonData { public string gcType; public string behaviourType; public string name; public int hitPoints; public int manaPoints; public string summonType; public string element; public string description; }
[Serializable] public class ZonePortalData { public int id; public string zone; public string name; public string gcType; public float posX; public float posY; public float posZ; public float heading; public int width; public int height; public string targetZone; public string spawnPoint; public uint color; }
[Serializable] public class ZoneWaypointData { public int id; public string zone; public string name; public float posX; public float posY; public float posZ; public float heading; }
[Serializable] public class ZoneCheckpointData { public int id; public string zone; public string name; public string gcType; public float posX; public float posY; public float posZ; public float heading; }
[Serializable] public class DRClassChildGroup { public string name; public DRChildEntity[] entities; public string gcType; }
[Serializable] public class DRChildEntity { public string @extends; public Dictionary<string, string> properties; public Dictionary<string, DRChildGroup> children; }
[Serializable] public class DRChildGroup { public DRChildEntity[] entities; }
