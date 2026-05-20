using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using DungeonRunners.Utilities;
using Org.BouncyCastle.Utilities;
using System.Linq;

namespace DungeonRunners.Data
{
    public static class GCObjectFactory
    {
        private static System.Random _random = new System.Random();

        public static GCObject NewPlayer(string name)
        {
            Debug.LogError($"[FACTORY] Creating player: {name}");
            var player = new GCObject
            {
                NativeClass = "Player",
                GCClass = "Player",
                Name = name,
                Properties = new List<GCObjectProperty>()
            };

            player.Properties.Add(new StringProperty { Name = "Name", Value = name });

            var extraData = new List<byte>();
            extraData.AddRange(Encoding.UTF8.GetBytes("plzwork1"));
            extraData.Add(0);
            extraData.AddRange(Encoding.UTF8.GetBytes("plzwork2"));
            extraData.Add(0);

            var idBytes = BitConverter.GetBytes((uint)0x05040302);
            extraData.AddRange(idBytes);
            extraData.AddRange(idBytes);

            extraData.Add(0);
            extraData.Add(0xAA);
            extraData.AddRange(Encoding.UTF8.GetBytes("Normal"));
            extraData.Add(0);
            extraData.Add(0x02);
            extraData.Add(0x00);
            extraData.AddRange(BitConverter.GetBytes((uint)0x05040302));
            player.ExtraData = extraData.ToArray();
            Debug.LogError($"[FACTORY] Player created with {player.ExtraData.Length} bytes extra data");
            return player;
        }

        // ============================================================================
        // WEAPON TYPE DETECTION METHOD
        // ============================================================================
        private static string GetWeaponNativeClass(string gcClass)
        {
            string lower = gcClass.ToLower();

            // SHIELDS: Are Armor type (go in offhand slot)
            if (lower.Contains("shield"))
            {
                Debug.LogError($"[WEAPON-DETECT] '{gcClass}' detected as SHIELD (Armor type)");
                return "Armor";
            }

            // RANGED WEAPONS: Only actual projectile weapons
            if (lower.Contains("gun") ||
                lower.Contains("bow") ||
                lower.Contains("crossbow") ||
                lower.Contains("rifle") ||
                lower.Contains("pistol") ||
                lower.Contains("blaster") ||
                lower.Contains("cannon") ||
                lower.Contains("launcher"))
            {
                Debug.LogError($"[WEAPON-DETECT] '{gcClass}' detected as RANGED weapon");
                return "RangedWeapon";
            }

            // MELEE WEAPONS: Everything else (including Staffs and Wands!)
            Debug.LogError($"[WEAPON-DETECT] '{gcClass}' detected as MELEE weapon");
            return "MeleeWeapon";
        }

        // ============================================================================
        // SKILL TYPE DETECTION METHOD  
        // ============================================================================
        private static string GetSkillNativeClass(string skillId)
        {
            if (skillId.ToLower().Contains("passive") || skillId.ToLower().Contains("trait"))
                return "PassiveSkill";
            return "ActiveSkill";
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // HARDCODED LoadAvatar - KNOWN WORKING (from OLD project)
        // Use this as fallback if SavedCharacter version has issues
        // ════════════════════════════════════════════════════════════════════════════════
        /* public static GCObject LoadAvatar()
         {
             Debug.LogError("[FACTORY] LoadAvatar - BACKUP METHOD (HARDCODED)");

             var avatar = new GCObject
             {
                 NativeClass = "Avatar",
                 GCClass = "avatar.classes.FighterFemale",
                 Name = "avatar",
                 Properties = new List<GCObjectProperty>
                 {
                     new UInt32Property { Name = "Skin", Value = 0 },
                     new UInt32Property { Name = "Face", Value = 0 },
                     new UInt32Property { Name = "FaceFeature", Value = 0 },
                     new UInt32Property { Name = "Hair", Value = 0 },
                     new UInt32Property { Name = "HairColor", Value = 0 },
                     new UInt32Property { Name = "TotalWorldTime", Value = 10 },
                     new UInt32Property { Name = "LastKnownQueueLevel", Value = 0 },
                     new UInt32Property { Name = "HasBlingGnome", Value = 1 },
                     new UInt32Property { Name = "Level", Value = 100 },
                     new UInt32Property { Name = "HitPoints", Value = 1337 },
                     new UInt32Property { Name = "ManaPoints", Value = 1337 },
                     new UInt32Property { Name = "Experience", Value = 1337 },
                     new UInt32Property { Name = "AttributePoints", Value = 100 },
                     new UInt32Property { Name = "ReSpecTimer", Value = 0 },
                     new UInt32Property { Name = "StrengthPoints", Value = 100 },
                     new UInt32Property { Name = "AgilityPoints", Value = 100 },
                     new UInt32Property { Name = "ToughnessPoints", Value = 100 },
                     new UInt32Property { Name = "PowerPoints", Value = 100 },
                     new UInt32Property { Name = "MaxTotalAttributePool", Value = 100 },
                     new UInt32Property { Name = "PVPRating", Value = 1337 },
                 }
             };

             avatar.AddChild(NewModifiers());
             var manipulators = NewManipulators();
             avatar.AddChild(manipulators);
             avatar.AddChild(NewUnitBehavior());
             avatar.AddChild(NewSkills());
             var equipment = NewEquipment();
             avatar.AddChild(equipment);
             avatar.AddChild(NewUnitContainerWithSevenChildren());
             avatar.AddChild(NewAvatarMetrics());
             avatar.AddChild(NewDialogManager());

             int equipmentCount = PopulateEquipmentAndManipulatorsHardcoded(equipment, manipulators);
             int skillCount = PopulateManipulatorsWithAllSkills(manipulators);

             return avatar;
         }

         // HARDCODED equipment population - KNOWN WORKING
         private static int PopulateEquipmentAndManipulatorsHardcoded(GCObject equipment, GCObject manipulators)
         {
             Debug.LogError("[EQUIP-POPULATE] HARDCODED EQUIPMENT - MATCHING GO SERVER EXACTLY!");
             int count = 0;

             // 1. ARMOR (chest) - slot 6
             var armor = new GCObject
             {
                 NativeClass = "Armor",
                 GCClass = "leatherarmor1pal.leatherarmor1-4",
                 Name = null,
                 Properties = new List<GCObjectProperty>()
             };
             equipment.AddChild(armor);
             manipulators.AddChild(armor);
             count++;

             // 2. BOOTS - slot 7
             var boots = new GCObject
             {
                 NativeClass = "Armor",
                 GCClass = "plateboots3pal.plateboots3-4",
                 Name = null,
                 Properties = new List<GCObjectProperty>()
             };
             equipment.AddChild(boots);
             manipulators.AddChild(boots);
             count++;

             // 3. HELMET - slot 5
             var helmet = new GCObject
             {
                 NativeClass = "Armor",
                 GCClass = "scalehelm1pal.scalehelm1-3",
                 Name = null,
                 Properties = new List<GCObjectProperty>()
             };
             equipment.AddChild(helmet);
             manipulators.AddChild(helmet);
             count++;

             // 4. GLOVES - slot 2
             var gloves = new GCObject
             {
                 NativeClass = "Armor",
                 GCClass = "plategloves0pal.plategloves0-10",
                 Name = null,
                 Properties = new List<GCObjectProperty>()
             };
             equipment.AddChild(gloves);
             manipulators.AddChild(gloves);
             count++;

             // 5. WEAPON - slot 10
             var weapon = new GCObject
             {
                 NativeClass = "MeleeWeapon",
                 GCClass = "items.pal.1hswordpal.normal014_fire",
                 Name = null,
                 Properties = new List<GCObjectProperty>()
             };
             equipment.AddChild(weapon);
             manipulators.AddChild(weapon);
             count++;

             Debug.LogError($"[EQUIP-POPULATE] ✅ HARDCODED: {count}/5 items");
             return count;
         }*/

        // Load ALL skills from database - used by hardcoded LoadAvatar
        private static int PopulateManipulatorsWithAllSkills(GCObject manipulators)
        {
            int count = 0;

            for (int i = 0; i < DatabaseLoader.Skills.Count; i++)
            {
                var skill = DatabaseLoader.Skills[i];
                int hotbarSlot = i + 0x64;

                if (hotbarSlot >= 0x6D)
                {
                    hotbarSlot = i + 1;
                }

                if (hotbarSlot >= 0x64)
                {
                    var skillChild = new GCObject
                    {
                        NativeClass = "ActiveSkill",
                        GCClass = skill.id,
                        Name = null
                    };
                    manipulators.AddChild(skillChild);
                    count++;
                }
            }

            Debug.LogError($"[MANIP-SKILL] ✅ Added {count} skills from database");
            return count;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // 🔥 LoadAvatar with SavedCharacter parameter
        // Loads character from JSON configuration - FOR CHARACTER CREATION SYSTEM
        // ════════════════════════════════════════════════════════════════════════════════
        public static GCObject LoadAvatar(SavedCharacter character)
        {
            Debug.LogError("[FACTORY] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[FACTORY] LoadAvatar - Creating avatar for {character.name} ({character.className})");
            Debug.LogError($"[FACTORY] Character ID: {character.id}, Level: {character.level}");
            Debug.LogError("[FACTORY] 🔥 GO SERVER PATTERN: Equipment goes to BOTH Manipulators AND Equipment!");
            Debug.LogError("[FACTORY] ═══════════════════════════════════════════════════════════");

            // Get class-specific GCClass - use saved avatarClass if available (preserves gender)
            string avatarGCClass;
            if (!string.IsNullOrEmpty(character.avatarClass))
            {
                avatarGCClass = character.avatarClass;
                Debug.LogError($"[FACTORY] Using saved avatarClass: {avatarGCClass}");
            }
            else
            {
                avatarGCClass = GetAvatarGCClass(character.className);
                Debug.LogError($"[FACTORY] No saved avatarClass, using fallback: {avatarGCClass}");
            }

            var avatar = new GCObject
            {
                NativeClass = "Avatar",
                GCClass = avatarGCClass,
                Name = "avatar",
                Properties = new List<GCObjectProperty>
                {
                    new UInt32Property { Name = "Skin", Value = character.skin },
                    new UInt32Property { Name = "Face", Value = character.face },
                    new UInt32Property { Name = "FaceFeature", Value = character.faceFeature },
                    new UInt32Property { Name = "Hair", Value = character.hair },
                    new UInt32Property { Name = "HairColor", Value = character.hairColor },
                    new UInt32Property { Name = "TotalWorldTime", Value = 10 },
                    new UInt32Property { Name = "LastKnownQueueLevel", Value = 0 },
                    new UInt32Property { Name = "HasBlingGnome", Value = 1 },
                    new UInt32Property { Name = "Level", Value = character.level },
                    new UInt32Property { Name = "HitPoints", Value = 1337 },
                    new UInt32Property { Name = "ManaPoints", Value = 1337 },
                    new UInt32Property { Name = "Experience", Value = character.experience },
                    new UInt32Property { Name = "AttributePoints", Value = 100 },
                    new UInt32Property { Name = "ReSpecTimer", Value = 0 },
                    new UInt32Property { Name = "StrengthPoints", Value = 100 },
                    new UInt32Property { Name = "AgilityPoints", Value = 100 },
                    new UInt32Property { Name = "ToughnessPoints", Value = 100 },
                    new UInt32Property { Name = "PowerPoints", Value = 100 },
                    new UInt32Property { Name = "MaxTotalAttributePool", Value = 100 },
                    new UInt32Property { Name = "PVPRating", Value = 1337 },
                }
            };

            Debug.LogError("[FACTORY] Adding Modifiers");
            avatar.AddChild(NewModifiers());

            Debug.LogError("[FACTORY] Creating Manipulators");
            var manipulators = NewManipulators();
            avatar.AddChild(manipulators);

            Debug.LogError("[FACTORY] Adding UnitBehavior");
            avatar.AddChild(NewUnitBehavior());

            Debug.LogError("[FACTORY] Adding Skills");
            avatar.AddChild(NewSkills());

            Debug.LogError("[FACTORY] Creating Equipment");
            var equipment = NewEquipment();
            avatar.AddChild(equipment);

            Debug.LogError("[FACTORY] Adding UnitContainer with 7 children");
            avatar.AddChild(NewUnitContainerWithSevenChildren());

            Debug.LogError("[FACTORY] Adding AvatarMetrics");
            avatar.AddChild(NewAvatarMetrics());

            Debug.LogError("[FACTORY] Adding DialogManager");
            avatar.AddChild(NewDialogManager());

            // ═══════════════════════════════════════════════════════════════════════════════
            // 🔥 FIX: Equipment items go to BOTH Equipment AND Manipulators!
            // ═══════════════════════════════════════════════════════════════════════════════
            Debug.LogError("[FACTORY] ═══ POPULATING EQUIPMENT FROM CHARACTER DATA ═══");
            Debug.LogError($"[FACTORY] Loading equipment for {character.className}...");

            int equipmentCount = PopulateEquipmentFromCharacter(equipment, manipulators, character);
            Debug.LogError($"[FACTORY] ✅ Added {equipmentCount} equipment items from character data");
            // int equipmentCount = 0;
            // ═══════════════════════════════════════════════════════════════════════════════
            // 🔥 POPULATE SKILLS FROM CHARACTER DATA
            // ═══════════════════════════════════════════════════════════════════════════════
            Debug.LogError("[FACTORY] Adding skills from character data...");
            int skillCount = PopulateSkillsFromCharacter(manipulators, character);
            Debug.LogError($"[FACTORY] ✅ Added {skillCount} skills from character data");
            // 🔥 TEST: Disable skills to see if they're causing the issue
            // int skillCount = PopulateSkillsFromCharacter(manipulators, character);
            // Debug.LogError($"[FACTORY] ✅ Added {skillCount} skills from character data");
            //int skillCount = 0;
            // Debug.LogError($"[FACTORY] ⚠️ SKILLS DISABLED FOR TESTING");
            Debug.LogError($"[FACTORY] ✅ Manipulators complete: {manipulators.Children.Count} children total");
            Debug.LogError($"[FACTORY] ✅ Equipment complete: {equipment.Children.Count} children total");
            Debug.LogError($"[FACTORY] ✅ Avatar creation complete with {avatar.Children.Count} children");
            Debug.LogError($"[FACTORY] ✅ Character {character.name} ({character.className}) ready!");
            Debug.LogError("[FACTORY] ═══════════════════════════════════════════════════════════");

            return avatar;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // Get Avatar GCClass based on character class
        // ════════════════════════════════════════════════════════════════════════════════
        private static string GetAvatarGCClass(string className)
        {
            switch (className)
            {
                case "Fighter":
                    return "avatar.classes.FighterFemale";
                case "Mage":
                    return "avatar.classes.WarlockFemale";  // Maps to Warlock in game files
                case "Ranger":
                    return "avatar.classes.RangerFemale";
                default:
                    Debug.LogWarning($"[FACTORY] Unknown class '{className}', defaulting to Fighter");
                    return "avatar.classes.FighterFemale";
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // 🔥 FIXED: PopulateEquipmentFromCharacter
        // Loads equipment from SavedCharacter
        // Adds each item to BOTH Equipment AND Manipulators (GO SERVER PATTERN)
        // ════════════════════════════════════════════════════════════════════════════════
        private static int PopulateEquipmentFromCharacter(GCObject equipment, GCObject manipulators, SavedCharacter character)
        {
            Debug.LogError("[EQUIP-CHAR] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[EQUIP-CHAR] CHARACTER: {character.name} (ID: {character.id})");
            Debug.LogError($"[EQUIP-CHAR] CLASS: {character.className}");
            Debug.LogError($"[EQUIP-CHAR] EQUIPMENT DATA:");
            Debug.LogError($"[EQUIP-CHAR]   weapon: '{character.equipment?.weapon ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   armor: '{character.equipment?.armor ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   helmet: '{character.equipment?.helmet ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   gloves: '{character.equipment?.gloves ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   boots: '{character.equipment?.boots ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   shoulders: '{character.equipment?.shoulders ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   shield: '{character.equipment?.shield ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   ring1: '{character.equipment?.ring1 ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   ring2: '{character.equipment?.ring2 ?? "NULL"}'");
            Debug.LogError($"[EQUIP-CHAR]   amulet: '{character.equipment?.amulet ?? "NULL"}'");
            Debug.LogError("[EQUIP-CHAR] ═══════════════════════════════════════════════════════════");
            int count = 0;

            // Pull persisted rarity / level so the new GCObjects carry the same StoredRarity
            // and StoredLevel as character_equipment.rarity / stored_level. Default sentinel
            // is -1 (matches DB column default + GCObject default). Without this, first-login
            // construction strips the rarity → next save overwrites DB rarity with 0 → equipped
            // Token Master items render white-trash + Path B picks wrong-quality mods. This
            // dict-backed lookup is exactly the same shape SavePlayerInventory writes to.
            int RarityOf(string slot) {
                if (character.equipment?.slotRarity != null
                    && character.equipment.slotRarity.TryGetValue(slot, out int r)) return r;
                return -1;
            }
            int LevelOf(string slot) {
                if (character.equipment?.slotLevel != null
                    && character.equipment.slotLevel.TryGetValue(slot, out int l)) return l;
                return -1;
            }

            // WEAPON - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.weapon))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating weapon: {character.equipment.weapon}");
                var weapon = CreateEquipmentItem(character.equipment.weapon, RarityOf("weapon"), LevelOf("weapon"));
                if (weapon != null)
                {
                    // Set Manipulator ID property → writes to Item+0x68 (slot assignment for renderer)
                    // PrimaryWeaponSlot SlotID=10
                    weapon.Properties.Add(new UInt32Property { Name = "ID", Value = 10 });
                    equipment.AddChild(weapon);
                    manipulators.AddChild(weapon);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Weapon: {weapon.GCClass} ({weapon.NativeClass}) -> BOTH (ID=10)");
                    count++;
                }
            }

            // ARMOR - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.armor))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating armor: {character.equipment.armor}");
                var armor = CreateEquipmentItem(character.equipment.armor, RarityOf("armor"), LevelOf("armor"));
                if (armor != null)
                {
                    armor.Properties.Add(new UInt32Property { Name = "ID", Value = 6 });
                    equipment.AddChild(armor);
                    manipulators.AddChild(armor);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Armor: {armor.GCClass} ({armor.NativeClass}) -> BOTH (ID=6)");
                    count++;
                }
            }

            // HELMET - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.helmet))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating helmet: {character.equipment.helmet}");
                var helmet = CreateEquipmentItem(character.equipment.helmet, RarityOf("helmet"), LevelOf("helmet"));
                if (helmet != null)
                {
                    helmet.Properties.Add(new UInt32Property { Name = "ID", Value = 5 });
                    equipment.AddChild(helmet);
                    manipulators.AddChild(helmet);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Helmet: {helmet.GCClass} ({helmet.NativeClass}) -> BOTH (ID=5)");
                    count++;
                }
            }

            // GLOVES - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.gloves))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating gloves: {character.equipment.gloves}");
                var gloves = CreateEquipmentItem(character.equipment.gloves, RarityOf("gloves"), LevelOf("gloves"));
                if (gloves != null)
                {
                    gloves.Properties.Add(new UInt32Property { Name = "ID", Value = 2 });
                    equipment.AddChild(gloves);
                    manipulators.AddChild(gloves);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Gloves: {gloves.GCClass} ({gloves.NativeClass}) -> BOTH (ID=2)");
                    count++;
                }
            }

            // BOOTS - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.boots))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating boots: {character.equipment.boots}");
                var boots = CreateEquipmentItem(character.equipment.boots, RarityOf("boots"), LevelOf("boots"));
                if (boots != null)
                {
                    boots.Properties.Add(new UInt32Property { Name = "ID", Value = 7 });
                    equipment.AddChild(boots);
                    manipulators.AddChild(boots);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Boots: {boots.GCClass} ({boots.NativeClass}) -> BOTH (ID=7)");
                    count++;
                }
            }

            // SHOULDERS - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.shoulders))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating shoulders: {character.equipment.shoulders}");
                var shoulders = CreateEquipmentItem(character.equipment.shoulders, RarityOf("shoulders"), LevelOf("shoulders"));
                if (shoulders != null)
                {
                    shoulders.Properties.Add(new UInt32Property { Name = "ID", Value = 12 });
                    equipment.AddChild(shoulders);
                    manipulators.AddChild(shoulders);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Shoulders: {shoulders.GCClass} ({shoulders.NativeClass}) -> BOTH (ID=12)");
                    count++;
                }
            }

            // SHIELD / OFF-HAND - BOTH Equipment AND Manipulators
            if (!string.IsNullOrEmpty(character.equipment.shield))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating shield: {character.equipment.shield}");
                var shield = CreateEquipmentItem(character.equipment.shield, RarityOf("shield"), LevelOf("shield"));
                if (shield != null)
                {
                    // Dual-wield: weapon in shield slot needs TargetSlot=11 for in-game spawn (OP4/OP5)
                    if (shield.NativeClass == "MeleeWeapon" || shield.NativeClass == "RangedWeapon")
                    {
                        shield.TargetSlot = 11;
                        Debug.LogError($"[EQUIP-CHAR] ✓ Dual-wield off-hand: {shield.GCClass} → TargetSlot=11");
                    }
                    // Set Manipulator ID = 11 (SecondaryWeaponSlot) for char select display
                    // This writes to Item+0x68 which tells the renderer to attach to left hand
                    shield.Properties.Add(new UInt32Property { Name = "ID", Value = 11 });
                    equipment.AddChild(shield);
                    manipulators.AddChild(shield);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Shield/Off-hand: {shield.GCClass} ({shield.NativeClass}) -> BOTH (ID=11)");
                    count++;
                }
            }

            // RING1 - BOTH Equipment AND Manipulators (separate instances)
            if (!string.IsNullOrEmpty(character.equipment.ring1))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating ring1: {character.equipment.ring1}");
                var ring1ForEquip = CreateEquipmentItem(character.equipment.ring1, RarityOf("ring1"), LevelOf("ring1"));
                var ring1ForManip = CreateEquipmentItem(character.equipment.ring1, RarityOf("ring1"), LevelOf("ring1"));
                if (ring1ForEquip != null && ring1ForManip != null)
                {
                    ring1ForEquip.Properties.Add(new UInt32Property { Name = "ID", Value = 3 });
                    ring1ForManip.Properties.Add(new UInt32Property { Name = "ID", Value = 3 });
                    equipment.AddChild(ring1ForEquip);
                    manipulators.AddChild(ring1ForManip);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Ring1: {ring1ForEquip.GCClass} ({ring1ForEquip.NativeClass}) -> BOTH (Slot 3)");
                    count++;
                }
            }

            // RING2 - BOTH Equipment AND Manipulators (separate instances)
            if (!string.IsNullOrEmpty(character.equipment.ring2))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating ring2: {character.equipment.ring2}");
                var ring2ForEquip = CreateEquipmentItem(character.equipment.ring2, RarityOf("ring2"), LevelOf("ring2"));
                var ring2ForManip = CreateEquipmentItem(character.equipment.ring2, RarityOf("ring2"), LevelOf("ring2"));
                if (ring2ForEquip != null && ring2ForManip != null)
                {
                    ring2ForEquip.TargetSlot = 4;
                    ring2ForManip.TargetSlot = 4;
                    ring2ForEquip.Properties.Add(new UInt32Property { Name = "ID", Value = 4 });
                    ring2ForManip.Properties.Add(new UInt32Property { Name = "ID", Value = 4 });

                    equipment.AddChild(ring2ForEquip);
                    manipulators.AddChild(ring2ForManip);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Ring2: {ring2ForEquip.GCClass} ({ring2ForEquip.NativeClass}) -> BOTH (Slot 4)");
                    count++;
                }
            }

            // AMULET - BOTH Equipment AND Manipulators (separate instances)
            if (!string.IsNullOrEmpty(character.equipment.amulet))
            {
                Debug.LogError($"[EQUIP-CHAR] Creating amulet: {character.equipment.amulet}");
                var amuletForEquip = CreateEquipmentItem(character.equipment.amulet, RarityOf("amulet"), LevelOf("amulet"));
                var amuletForManip = CreateEquipmentItem(character.equipment.amulet, RarityOf("amulet"), LevelOf("amulet"));
                if (amuletForEquip != null && amuletForManip != null)
                {
                    amuletForEquip.Properties.Add(new UInt32Property { Name = "ID", Value = 1 });
                    amuletForManip.Properties.Add(new UInt32Property { Name = "ID", Value = 1 });

                    equipment.AddChild(amuletForEquip);
                    manipulators.AddChild(amuletForManip);
                    Debug.LogError($"[EQUIP-CHAR] ✓ Amulet: {amuletForEquip.GCClass} ({amuletForEquip.NativeClass}) -> BOTH (Slot 1)");
                    count++;
                }
            }

            Debug.LogError($"[EQUIP-CHAR] ✅ Equipment loading complete: {count}/10 items -> BOTH components");
            Debug.LogError("[EQUIP-CHAR] ═══════════════════════════════════════════════════════════");
            return count;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // PopulateSkillsFromCharacter
        // Loads skills from SavedCharacter's skill list
        // ════════════════════════════════════════════════════════════════════════════════
        private static int PopulateSkillsFromCharacter(GCObject manipulators, SavedCharacter character)
        {
            Debug.LogError("[SKILLS-CHAR] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[SKILLS-CHAR] Loading skills for {character.className}");
            Debug.LogError($"[SKILLS-CHAR] Character has {character.skills.Count} skills");
            Debug.LogError("[SKILLS-CHAR] ═══════════════════════════════════════════════════════════");

            int count = 0;

            foreach (var skillId in character.skills)
            {
                string nativeClass = GetSkillNativeClass(skillId);
                // PassiveSkills CANNOT go in Manipulators — readInit crashes with
                // "GCClassRegistry::readType Invalid type tag" when it encounters PassiveSkill.
                // Passives are written in Op10 (Skills component) for skill book/trainer.
                // Hotbar placement for passives is restored via post-spawn Add packets.
                if (nativeClass == "PassiveSkill")
                {
                    Debug.LogError($"[SKILLS-CHAR] ⏭️ Skipping passive from Manipulators: {skillId} (Op10 + post-spawn hotbar)");
                    continue;
                }
                Debug.LogError($"[SKILLS-CHAR] Creating skill: {skillId}");
                var skillObj = new GCObject
                {
                    NativeClass = nativeClass,
                    GCClass = skillId,
                    Name = null
                };
                manipulators.AddChild(skillObj);
                Debug.LogError($"[SKILLS-CHAR] ✓ Added skill: {skillId}");
                count++;
            }

            Debug.LogError($"[SKILLS-CHAR] ✅ Skills loading complete: {count} skills added");
            Debug.LogError("[SKILLS-CHAR] ═══════════════════════════════════════════════════════════");
            return count;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // CreateEquipmentItem
        // Creates a GCObject for an equipment item with proper NativeClass detection.
        // storedRarity / storedLevel come from character_equipment.rarity / stored_level
        // (LoadEquipment reads them into StartingEquipment.slotRarity / slotLevel). Without
        // them, the first-login spawn re-creates equipment GCObjects with StoredRarity=-1,
        // then GetEffectiveRarity falls through to DetectRarityFromGCClass which returns 0
        // for items whose name doesn't contain "rare"/"unique"/"mythic" (e.g.
        // LeatherPAL.LeatherArmor2 from Token Master). The next save overwrites the DB
        // rarity with 0 — locking in "white trash" rendering forever across sessions.
        // Path B (mod injection) also keys its mod selection on rarity, so a -1 store also
        // produces the wrong-quality mods on equipped tooltips.
        // ════════════════════════════════════════════════════════════════════════════════
        private static GCObject CreateEquipmentItem(string gcClass, int storedRarity = -1, int storedLevel = -1)
        {
            if (string.IsNullOrEmpty(gcClass))
            {
                Debug.LogError("[CREATE-ITEM] ❌ gcClass is null or empty!");
                return null;
            }
            Debug.LogError($"[CREATE-ITEM] Creating item: {gcClass}");
            // Detect proper NativeClass
            string nativeClass = "Armor";  // Default for armor pieces
            string gcLower = gcClass.ToLower();

            // 🔥 CRITICAL: Check rings/amulets FIRST - "RingUnique" contains "gun"!
            if (gcLower.Contains("ring"))
            {
                nativeClass = "Item";
                Debug.LogError($"[CREATE-ITEM] Detected as Item (Ring)");
            }
            else if (gcLower.Contains("amulet"))
            {
                nativeClass = "Item";
                Debug.LogError($"[CREATE-ITEM] Detected as Item (Amulet)");
            }
            else if (gcLower.Contains("sword") || gcLower.Contains("axe") || gcLower.Contains("mace") ||
                gcLower.Contains("staff") || gcLower.Contains("wand") || gcLower.Contains("dagger") ||
                gcLower.Contains("pick") || gcLower.Contains("club") || gcLower.Contains("katana") ||
                gcLower.Contains("polearm"))
            {
                nativeClass = "MeleeWeapon";
                Debug.LogError($"[CREATE-ITEM] Detected as MeleeWeapon");
            }
            else if (gcLower.Contains("bow") || gcLower.Contains("gun") || gcLower.Contains("crossbow") || gcLower.Contains("cannon"))
            {
                nativeClass = "RangedWeapon";
                Debug.LogError($"[CREATE-ITEM] Detected as RangedWeapon");
            }
            else
            {
                Debug.LogError($"[CREATE-ITEM] Detected as Armor");
            }
            // Verify item exists in database
            if (nativeClass == "Item")
            {
                // Rings/Amulets are in GeneralItemDatabase
                var generalItem = DatabaseLoader.FindGeneralItem(gcClass);
                if (generalItem == null)
                {
                    Debug.LogWarning($"[CREATE-ITEM] ⚠️ Ring/Amulet not found in GeneralItemDatabase: {gcClass}");
                }
                else
                {
                    Debug.LogError($"[CREATE-ITEM] ✓ Found in GeneralItemDatabase: {generalItem.Label}");
                }
            }
            else
            {
                // Armor/Weapons are in regular item database
                var itemData = DatabaseLoader.FindItem(gcClass);
                if (itemData == null)
                {
                    Debug.LogWarning($"[CREATE-ITEM] ⚠️ Item not found in database: {gcClass}, creating anyway");
                }
                else
                {
                    Debug.LogError($"[CREATE-ITEM] ✓ Found in database: {itemData.name}");
                }
            }
            var result = new GCObject
            {
                NativeClass = nativeClass,
                GCClass = gcClass,
                Name = null,
                Properties = new List<GCObjectProperty>(),
                StoredRarity = storedRarity,
                StoredLevel = storedLevel
            };
            Debug.LogError($"[CREATE-ITEM] ✅ Created GCObject: {result.GCClass} as {result.NativeClass} storedRarity={storedRarity} storedLevel={storedLevel}");
            return result;
        }

        public static GCObject NewModifiers()
        {
            return new GCObject
            {
                NativeClass = "Modifiers",
                GCClass = "Modifiers",
                Name = null
            };
        }

        public static GCObject NewManipulators()
        {
            return new GCObject
            {
                NativeClass = "Manipulators",
                GCClass = "Manipulators",
                Name = null
            };
        }

        public static GCObject NewUnitBehavior()
        {
            return new GCObject
            {
                NativeClass = "UnitBehavior",
                GCClass = "avatar.base.UnitBehavior",
                Name = null
            };
        }

        public static GCObject NewSkills()
        {
            return new GCObject
            {
                NativeClass = "Skills",
                GCClass = "avatar.base.skills",
                Name = null
            };
        }

        public static GCObject NewEquipment()
        {
            Debug.LogError("[FACTORY] Creating Equipment component (empty - will be populated)");
            var equipment = new GCObject
            {
                NativeClass = "Equipment",
                GCClass = "avatar.base.Equipment",
                Name = null
            };

            Debug.LogError("[FACTORY] ✅ Equipment created (empty, items added by PopulateEquipmentFromCharacter)");
            return equipment;
        }

        public static GCObject NewUnitContainerWithSevenChildren()
        {
            var unitContainer = new GCObject
            {
                NativeClass = "UnitContainer",
                GCClass = "UnitContainer",
                Name = null
            };

            // Add the 9 inventory children (Inventory + Trade + 7 bank pages, matching avatar.gc DefaultBankObject..DefaultBankObject7)
            var childNames = new[] { "Inventory", "TradeInventory", "Bank1", "Bank2", "Bank3", "Bank4", "Bank5", "Bank6", "Bank7" };
            var childGCClasses = new[] { "avatar.base.Inventory", "avatar.base.TradeInventory", "avatar.base.Bank", "avatar.base.Bank2", "avatar.base.Bank3", "avatar.base.Bank4", "avatar.base.Bank5", "avatar.base.Bank6", "avatar.base.Bank7" };

            for (int i = 0; i < 9; i++)
            {
                var child = new GCObject
                {
                    NativeClass = "Inventory",
                    GCClass = childGCClasses[i],
                    Name = null
                };
                unitContainer.AddChild(child);
            }

            // Create a Manipulator object (NOT a weapon!)
            var manipulator = new GCObject
            {
                NativeClass = "Manipulator",
                GCClass = "Manipulator",
                Name = null
            };

            // Serialize the Manipulator as ExtraData
            var writer = new LEWriter();
            try
            {
                manipulator.WriteFullGCObject(writer);
                unitContainer.ExtraData = writer.ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FACTORY] UnitContainer manipulator serialization failed: {ex.Message}");
                unitContainer.ExtraData = new byte[0];
            }

            return unitContainer;
        }

        public static GCObject NewAvatarMetrics()
        {
            var avatarMetrics = new GCObject
            {
                NativeClass = "AvatarMetrics",
                GCClass = "AvatarMetrics",
                Name = null
            };

            var extraData = new List<byte>();

            for (int i = 0; i < 5; i++)
                extraData.AddRange(BitConverter.GetBytes((uint)0));

            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));

            for (int i = 0; i < 5; i++)
            {
                extraData.AddRange(BitConverter.GetBytes((ulong)0));
            }

            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));

            avatarMetrics.ExtraData = extraData.ToArray();

            return avatarMetrics;
        }

        public static GCObject LoadMinimalAvatar()
        {
            Debug.LogError("[FACTORY] ═══════════════════════════════════════════════════════════");
            Debug.LogError("[FACTORY] LoadMinimalAvatar - Creating EMPTY avatar for creation UI");
            Debug.LogError("[FACTORY] NO EQUIPMENT - Client should trigger character creation");
            Debug.LogError("[FACTORY] ═══════════════════════════════════════════════════════════");

            var avatar = new GCObject
            {
                NativeClass = "Avatar",
                GCClass = "avatar.classes.FighterFemale",
                Name = "avatar",
                Properties = new List<GCObjectProperty>
                {
                    new UInt32Property { Name = "Skin", Value = 0 },
                    new UInt32Property { Name = "Face", Value = 0 },
                    new UInt32Property { Name = "FaceFeature", Value = 0 },
                    new UInt32Property { Name = "Hair", Value = 0 },
                    new UInt32Property { Name = "HairColor", Value = 0 },
                    new UInt32Property { Name = "TotalWorldTime", Value = 0 },
                    new UInt32Property { Name = "LastKnownQueueLevel", Value = 0 },
                    new UInt32Property { Name = "HasBlingGnome", Value = 0 },
                    new UInt32Property { Name = "Level", Value = 1 },
                    new UInt32Property { Name = "HitPoints", Value = 100 },
                    new UInt32Property { Name = "ManaPoints", Value = 100 },
                    new UInt32Property { Name = "Experience", Value = 0 },
                    new UInt32Property { Name = "AttributePoints", Value = 0 },
                    new UInt32Property { Name = "ReSpecTimer", Value = 0 },
                    new UInt32Property { Name = "StrengthPoints", Value = 0 },
                    new UInt32Property { Name = "AgilityPoints", Value = 0 },
                    new UInt32Property { Name = "ToughnessPoints", Value = 0 },
                    new UInt32Property { Name = "PowerPoints", Value = 0 },
                    new UInt32Property { Name = "MaxTotalAttributePool", Value = 0 },
                    new UInt32Property { Name = "PVPRating", Value = 0 },
                }
            };

            Debug.LogError("[FACTORY] Adding Modifiers");
            avatar.AddChild(NewModifiers());

            Debug.LogError("[FACTORY] Adding EMPTY Manipulators (no equipment)");
            avatar.AddChild(NewManipulators());

            Debug.LogError("[FACTORY] Adding UnitBehavior");
            avatar.AddChild(NewUnitBehavior());

            Debug.LogError("[FACTORY] Adding Skills");
            avatar.AddChild(NewSkills());

            Debug.LogError("[FACTORY] Adding EMPTY Equipment (no items)");
            avatar.AddChild(NewEquipment());

            Debug.LogError("[FACTORY] Adding UnitContainer");
            avatar.AddChild(NewUnitContainerWithSevenChildren());

            Debug.LogError("[FACTORY] Adding AvatarMetrics");
            avatar.AddChild(NewAvatarMetrics());

            Debug.LogError("[FACTORY] Adding DialogManager");
            avatar.AddChild(NewDialogManager());

            Debug.LogError($"[FACTORY] ✅ MINIMAL avatar complete: {avatar.Children.Count} components, NO EQUIPMENT");
            Debug.LogError("[FACTORY] ═══════════════════════════════════════════════════════════");

            return avatar;
        }

        public static GCObject NewDialogManager()
        {
            return new GCObject
            {
                NativeClass = "DialogManager",
                GCClass = "DialogManager",
                Name = null
            };
        }

        public static GCObject NewProcModifier()
        {
            return new GCObject
            {
                NativeClass = "ProcModifier",
                GCClass = "ProcModifier",
                Name = null
            };
        }
    }
}