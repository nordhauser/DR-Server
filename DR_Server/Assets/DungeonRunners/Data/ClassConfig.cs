using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DungeonRunners.Data
{
    [Serializable]
    public class StartingInventoryItem
    {
        public string gcClass;
        public byte x;
        public byte y;
        public int count = 1;
    }

    [Serializable]
    public class ClassDefinition
    {
        public string displayName;
        public string description;
        public StartingEquipment startingEquipment;
        public List<string> startingSkills = new List<string>();
        public List<StartingInventoryItem> startingInventory = new List<StartingInventoryItem>();
    }

    [Serializable]
    public class StartingEquipment
    {
        public string weapon;
        public string armor;
        public string helmet;
        public string gloves;
        public string boots;
        // NEW SLOTS
        public string shoulders;
        public string shield;
        public string ring1;
        public string ring2;
        public string amulet;
        // Rarity per slot (keyed by slot name: "weapon", "armor", etc.)
        public Dictionary<string, int> slotRarity = new Dictionary<string, int>();
        // Fixed level per slot (keyed by slot name)
        public Dictionary<string, int> slotLevel = new Dictionary<string, int>();
    }

    [Serializable]
    public class ClassConfigData
    {
        public Dictionary<string, ClassDefinition> classes = new Dictionary<string, ClassDefinition>();
    }

    [Serializable]
    public class SavedInventoryItem
    {
        public string gcClass;
        public byte x;
        public byte y;
        public int count = 1;
        public uint buyPrice = 0;  // Store buy price for accurate sell calculation
        public int rarity = 0;     // ItemRarity enum value (0=Normal, 1=Superior, 2=Magical, 3=Rare, 4=Unique, 5=Mythic)
        public int storedLevel = -1; // Fixed item level (-1 = legacy/compute from GCClass)
    }

    [Serializable]
    public class SavedCharacter
    {
        public uint id;
        public string name;
        public uint accountId;
        public string accountName;
        public string className;
        public byte level;
        public uint experience;
        public uint gold;
        public StartingEquipment equipment;
        public List<string> skills = new List<string>();
        public List<SavedInventoryItem> inventory = new List<SavedInventoryItem>();
        public Vector3 position;
        public int zoneId;
        public int worldId;
        public string currentZoneName;
        public string avatarClass;
        public byte skin;
        public byte face;
        public byte faceFeature;
        public byte hair;
        public byte hairColor;

        // Quest tracking
        public List<SavedQuest> activeQuests = new List<SavedQuest>();
        public List<string> completedQuests = new List<string>();
        public List<string> unlockedCheckpoints = new List<string>();

        // Combat state (persists between sessions)
        public uint currentHP = 0;   // wire format (actual × 256), 0 = use max
        public uint currentMana = 0; // wire format, 0 = use max

        // Character stats (updated on equip/zone/login)
        public int maxHP = 0;
        public int maxMana = 0;
        public int statStrength = 0;
        public int statAgility = 0;
        public int statIntellect = 0;
        public int statEndurance = 0;
        public int lastRespecTime = 0;  // Unix timestamp, 15min cooldown between respecs
        public int respecCount = 0;     // Total number of respecs performed
        public int pvpWins = 0;         // PVP wins (not implemented yet)
        public int pvpRating = 0;       // PVP rating Fixed32 (not implemented yet)

        // Town portal (saved to DB, cleared on logoff, survives crashes)
        public string tpZone = "";
        public int tpZoneId = 0;
        public string tpTargetZone = "";
        public float tpPosX = 0, tpPosY = 0, tpPosZ = 0;

        // Skill levels — maps skill GCClass → trained level (default 1)
        // Updated when player trains at skill trainer
        // NOTE: Unity JsonUtility cannot serialize Dictionary — use List<SkillLevelEntry> instead
        public List<SkillLevelEntry> skillLevels = new List<SkillLevelEntry>();

        // Hotbar slot assignments — maps slot ID (0x64-0x6D) to skill GCClass
        // Updated when player drags skills on the hotbar
        public List<HotbarSlotEntry> hotbarSlots = new List<HotbarSlotEntry>();

        /// <summary>Get skill level by GC class name, returns 1 if not found.</summary>
        public int GetSkillLevel(string skillGcClass)
        {
            for (int i = 0; i < skillLevels.Count; i++)
                if (string.Equals(skillLevels[i].skill, skillGcClass, StringComparison.OrdinalIgnoreCase))
                    return skillLevels[i].level;
            return 1;
        }

        /// <summary>Set skill level by GC class name.</summary>
        public void SetSkillLevel(string skillGcClass, int level)
        {
            for (int i = 0; i < skillLevels.Count; i++)
            {
                if (string.Equals(skillLevels[i].skill, skillGcClass, StringComparison.OrdinalIgnoreCase))
                {
                    skillLevels[i] = new SkillLevelEntry { skill = skillGcClass, level = level };
                    return;
                }
            }
            skillLevels.Add(new SkillLevelEntry { skill = skillGcClass, level = level });
        }
    }

    [Serializable]
    public class SkillLevelEntry
    {
        public string skill;
        public int level = 1;
    }

    [Serializable]
    public class HotbarSlotEntry
    {
        public uint slot;       // 0x64 (100) through 0x6D (109) = hotbar slots 1-10
        public string skill;    // GCClass of the skill in this slot
    }

    [Serializable]
    public class SavedQuest
    {
        public string questId;
        public string questGiverId;
        public string acceptedAt;
        public List<SavedQuestObjective> objectives = new List<SavedQuestObjective>();
    }

    [Serializable]
    public class SavedQuestObjective
    {
        public string objectiveName;
        public string type;
        public string target;
        public string label;
        public int required;
        public int current;
    }

    [Serializable]
    public class CharacterStorageData
    {
        public List<SavedCharacter> characters = new List<SavedCharacter>();
    }

    public static class ClassConfig
    {
        private static ClassConfigData _classConfig;
        private static CharacterStorageData _characterStorage;
        private static bool _isLoaded = false;

        public static void Load()
        {
            if (_isLoaded) return;

            _classConfig = new ClassConfigData();
            _characterStorage = new CharacterStorageData();

            // ═══════════════════════════════════════════════════════════════
            // LOAD CLASS CONFIG — read directly from SQLite tables
            // ═══════════════════════════════════════════════════════════════
            try
            {
                _classConfig.classes = new Dictionary<string, ClassDefinition>();
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    using (var r = DungeonRunners.Database.GameDatabase.ExecuteReader(conn, "SELECT * FROM class_definitions"))
                    {
                        while (r.Read())
                        {
                            string cn = DungeonRunners.Database.GameDatabase.GetString(r, "class_name");
                            var def = new ClassDefinition
                            {
                                displayName = DungeonRunners.Database.GameDatabase.GetString(r, "display_name"),
                                description = DungeonRunners.Database.GameDatabase.GetString(r, "description"),
                                startingEquipment = new StartingEquipment
                                {
                                    weapon = DungeonRunners.Database.GameDatabase.GetString(r, "weapon"),
                                    armor = DungeonRunners.Database.GameDatabase.GetString(r, "armor"),
                                    helmet = DungeonRunners.Database.GameDatabase.GetString(r, "helmet"),
                                    gloves = DungeonRunners.Database.GameDatabase.GetString(r, "gloves"),
                                    boots = DungeonRunners.Database.GameDatabase.GetString(r, "boots"),
                                    shoulders = DungeonRunners.Database.GameDatabase.GetString(r, "shoulders"),
                                    shield = DungeonRunners.Database.GameDatabase.GetString(r, "shield"),
                                    ring1 = DungeonRunners.Database.GameDatabase.GetString(r, "ring1"),
                                    ring2 = DungeonRunners.Database.GameDatabase.GetString(r, "ring2"),
                                    amulet = DungeonRunners.Database.GameDatabase.GetString(r, "amulet")
                                },
                                startingSkills = new List<string>(),
                                startingInventory = new List<StartingInventoryItem>
                                {
                                    new StartingInventoryItem { gcClass = "items.consumables.Consumable_TownPortal", x = 0, y = 0 },
                                    new StartingInventoryItem { gcClass = "PotionPAL.HealthPotion_Noob", x = 1, y = 0, count = 20 },
                                    new StartingInventoryItem { gcClass = "PotionPAL.ManaPotion_Noob", x = 2, y = 0, count = 20 }
                                }
                            };
                            _classConfig.classes[cn] = def;
                        }
                    }
                    // Load starting skills
                    using (var r = DungeonRunners.Database.GameDatabase.ExecuteReader(conn, "SELECT class_name, skill_gc_type FROM class_starting_skills"))
                    {
                        while (r.Read())
                        {
                            string cn = r.GetString(0);
                            string skill = r.GetString(1);
                            if (_classConfig.classes.TryGetValue(cn, out var def))
                                def.startingSkills.Add(skill);
                        }
                    }
                }
                Debug.LogError($"[ClassConfig] Loaded {_classConfig.classes.Count} classes from SQLite");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClassConfig] SQLite error: {ex.Message}, using hardcoded defaults");
            }

            // Characters are now loaded from SQLite via CharacterRepository — skip JSON

            // ═══════════════════════════════════════════════════════════════
            // FALLBACK FOR CLASS DEFINITIONS (hardcoded defaults)
            // ═══════════════════════════════════════════════════════════════
            if (_classConfig == null || _classConfig.classes == null || _classConfig.classes.Count == 0)
            {
                Debug.LogError("[ClassConfig] Classes not loaded from JSON, using hardcoded defaults");
                _classConfig = new ClassConfigData();
                _classConfig.classes = new Dictionary<string, ClassDefinition>();

                _classConfig.classes["Fighter"] = new ClassDefinition
                {
                    displayName = "Fighter",
                    description = "Melee combat specialist",
                    startingEquipment = new StartingEquipment
                    {
                        weapon = "items.pal.1hmacepal.normal001",
                        armor = "scalearmor1pal.scalearmor1-1",
                        helmet = "",
                        gloves = "scalegloves1pal.scalegloves1-1",
                        boots = "scaleboots1pal.scaleboots1-1",
                        shoulders = "",
                        shield = "",
                        ring1 = "",
                        ring2 = "",
                        amulet = ""
                    },
                    startingSkills = new List<string> { "skills.generic.Butcher", "skills.generic.Stomp", "skills.generic.FighterClassPassive", "skills.generic.MeleeAttackSpeedModPassive" },
                    startingInventory = new List<StartingInventoryItem>
                    {
                        new StartingInventoryItem { gcClass = "items.consumables.Consumable_TownPortal", x = 0, y = 0 },
                        new StartingInventoryItem { gcClass = "PotionPAL.HealthPotion_Noob", x = 1, y = 0, count = 20 },
                        new StartingInventoryItem { gcClass = "PotionPAL.ManaPotion_Noob", x = 2, y = 0, count = 20 }
                    }
                };

                _classConfig.classes["Mage"] = new ClassDefinition
                {
                    displayName = "Mage",
                    description = "Ranged magic specialist",
                    startingEquipment = new StartingEquipment
                    {
                        weapon = "1hstaff1pal.1hstaff1-1",
                        armor = "items.pal.magebodypal.normal001",
                        helmet = "",
                        gloves = "items.pal.mageglovespal.normal002",
                        boots = "items.pal.magebootspal.normal002",
                        shoulders = "",
                        shield = "",
                        ring1 = "",
                        ring2 = "",
                        amulet = ""
                    },
                    startingSkills = new List<string> { "skills.generic.FireBolt", "skills.generic.ShadowLightning", "skills.generic.MageClassPassive", "skills.generic.MagicDamageModPassive" },
                    startingInventory = new List<StartingInventoryItem>
                    {
                        new StartingInventoryItem { gcClass = "items.consumables.Consumable_TownPortal", x = 0, y = 0 },
                        new StartingInventoryItem { gcClass = "PotionPAL.HealthPotion_Noob", x = 1, y = 0, count = 20 },
                        new StartingInventoryItem { gcClass = "PotionPAL.ManaPotion_Noob", x = 2, y = 0, count = 20 }
                    }
                };

                _classConfig.classes["Ranger"] = new ClassDefinition
                {
                    displayName = "Ranger",
                    description = "Ranged physical specialist",
                    startingEquipment = new StartingEquipment
                    {
                        weapon = "2hcrossbow1pal.2hcrossbow1-1",
                        armor = "leatherarmor1pal.leatherarmor1-1",
                        helmet = "",
                        gloves = "leathergloves1pal.leathergloves1-1",
                        boots = "leatherboots1pal.leatherboots1-1",
                        shoulders = "",
                        shield = "",
                        ring1 = "",
                        ring2 = "",
                        amulet = ""
                    },
                    startingSkills = new List<string> { "skills.generic.PoisonShot", "skills.generic.PoisonBlastRadius", "skills.generic.RangerClassPassive", "skills.generic.RangeAttackSpeedModPassive" },
                    startingInventory = new List<StartingInventoryItem>
                    {
                        new StartingInventoryItem { gcClass = "items.consumables.Consumable_TownPortal", x = 0, y = 0 },
                        new StartingInventoryItem { gcClass = "PotionPAL.HealthPotion_Noob", x = 1, y = 0, count = 20 },
                        new StartingInventoryItem { gcClass = "PotionPAL.ManaPotion_Noob", x = 2, y = 0, count = 20 }
                    }
                };

                Debug.LogError("[ClassConfig] ✅ Loaded 3 hardcoded class definitions");
            }

            if (_characterStorage == null)
            {
                _characterStorage = new CharacterStorageData();
            }

            _isLoaded = true;
        }

        public static ClassDefinition GetClassDefinition(string className)
        {
            if (!_isLoaded) Load();

            if (_classConfig.classes.TryGetValue(className, out var classDef))
            {
                return classDef;
            }

            Debug.LogError($"❌ Class '{className}' not found in configuration!");
            return null;
        }

        public static SavedCharacter GetCharacter(uint characterId)
        {
            if (!_isLoaded) Load();

            foreach (var character in _characterStorage.characters)
            {
                if (character.id == characterId)
                {
                    return character;
                }
            }

            return null;
        }

        public static SavedCharacter GetCharacterByName(string accountName, string characterName)
        {
            if (!_isLoaded) Load();

            foreach (var character in _characterStorage.characters)
            {
                if (character.name == characterName)
                {
                    return character;
                }
            }

            return null;
        }

        public static void SaveCharacter(SavedCharacter character)
        {
            if (!_isLoaded) Load();

            bool found = false;
            for (int i = 0; i < _characterStorage.characters.Count; i++)
            {
                if (_characterStorage.characters[i].id == character.id)
                {
                    _characterStorage.characters[i] = character;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                _characterStorage.characters.Add(character);
            }
        }

        public static SavedCharacter CreateNewCharacter(string name, string className, uint accountId, string accountName = "")
        {
            if (!_isLoaded) Load();

            var classDef = GetClassDefinition(className);
            if (classDef == null)
            {
                Debug.LogError($"❌ Cannot create character with invalid class: {className}");
                return null;
            }

            uint newId = 100;
            foreach (var existingChar in _characterStorage.characters)
            {
                if (existingChar.id >= newId)
                {
                    newId = existingChar.id + 1;
                }
            }

            // Copy starting inventory from class definition
            var startingInv = new List<SavedInventoryItem>();
            if (classDef.startingInventory != null)
            {
                foreach (var item in classDef.startingInventory)
                {
                    startingInv.Add(new SavedInventoryItem
                    {
                        gcClass = item.gcClass,
                        x = item.x,
                        y = item.y,
                        count = item.count > 0 ? item.count : 1
                    });
                }
            }

            var newCharacter = new SavedCharacter
            {
                id = newId,
                name = name,
                accountId = accountId,
                accountName = accountName,
                className = className,
                level = 1,
                experience = 0,
                gold = 100,
                equipment = new StartingEquipment
                {
                    weapon = classDef.startingEquipment.weapon,
                    armor = classDef.startingEquipment.armor,
                    helmet = classDef.startingEquipment.helmet,
                    gloves = classDef.startingEquipment.gloves,
                    boots = classDef.startingEquipment.boots,
                    shoulders = classDef.startingEquipment.shoulders ?? "",
                    shield = classDef.startingEquipment.shield ?? "",
                    ring1 = classDef.startingEquipment.ring1 ?? "",
                    ring2 = classDef.startingEquipment.ring2 ?? "",
                    amulet = classDef.startingEquipment.amulet ?? ""
                },
                skills = new List<string>(classDef.startingSkills),
                skillLevels = classDef.startingSkills.Select(s => new SkillLevelEntry { skill = s, level = 1 }).ToList(),
                inventory = startingInv,
                position = Vector3.zero,
                zoneId = 1,
                worldId = 1
            };

            SaveCharacter(newCharacter);
            return newCharacter;
        }

        public static bool DeleteCharacter(uint characterId)
        {
            if (!_isLoaded) Load();

            for (int i = 0; i < _characterStorage.characters.Count; i++)
            {
                if (_characterStorage.characters[i].id == characterId)
                {
                    _characterStorage.characters.RemoveAt(i);
                    Debug.Log($"Deleted character ID: {characterId}");
                    return true;
                }
            }

            return false;
        }

        public static List<SavedCharacter> GetAllCharacters()
        {
            if (!_isLoaded) Load();
            return _characterStorage?.characters ?? new List<SavedCharacter>();
        }

        public static List<SavedCharacter> GetCharactersForAccount(string accountName)
        {
            if (!_isLoaded) Load();
            var all = _characterStorage?.characters ?? new List<SavedCharacter>();
            var result = new List<SavedCharacter>();
            foreach (var c in all)
            {
                if (string.Equals(c.accountName, accountName, StringComparison.OrdinalIgnoreCase))
                    result.Add(c);
            }
            return result;
        }

        public static void ForceReload()
        {
            _isLoaded = false;
            _characterStorage = null;
            _classConfig = null;
            Load();
            Debug.LogError($"[ClassConfig] Force reloaded - {_characterStorage?.characters?.Count ?? 0} characters");
        }
    }
}
