using System.Collections.Generic;
using System;
using UnityEngine;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Class passive data derived from game files (skills/generic/*ClassPassive.gc)
    /// The client auto-loads these passives based on avatar.GCClass
    /// Server must calculate the same HP bonus to stay in sync
    /// </summary>
    public static class ClassPassiveData
    {
        // Base stats (from avatar/base/avatar.gc)
        public const int BASE_ENDURANCE = 10;
        public const int BASE_INTELLECT = 10;
        public const int HERO_HEALTH_PER_LEVEL = 16;
        public const int HEALTH_PER_ENDURANCE = 25;
        public const float BASE_HP_PER_ENDURANCE = 1.0f;  // 100% baseline

        private static uint ClampWire(long wire)
        {
            if (wire <= 0) return 0;
            if (wire >= uint.MaxValue) return uint.MaxValue;
            return (uint)wire;
        }

        public static uint CalculateNativeHPWire(int level, int endurance, int healthPerEnduranceModPercent)
        {
            int nativeLevel = Math.Max(1, level);
            int nativeEndurance = Math.Max(1, endurance);
            int percent = Math.Max(0, 100 + healthPerEnduranceModPercent);
            int percentFixed = (int)(((long)percent * 0x10000L) / 0x6400L);
            long hpPerEnduranceFixed = ((long)HEALTH_PER_ENDURANCE * 256L * percentFixed) >> 8;
            long enduranceHP = (((long)nativeEndurance << 8) * hpPerEnduranceFixed) >> 16;
            long levelHP = (long)nativeLevel * HERO_HEALTH_PER_LEVEL;
            return ClampWire((enduranceHP + levelHP) * 256L);
        }

        /// <summary>
        /// Class passive definitions - derived from game files
        /// </summary>
        public static readonly Dictionary<string, ClassPassive> Passives = new Dictionary<string, ClassPassive>
        {
            // From skills/generic/FighterClassPassive.gc
            ["Fighter"] = new ClassPassive
            {
                PassiveSkillId = "skills.generic.FighterClassPassive",
                Profession = "skills.professions.Warrior",
                HealthPerEnduranceMod = 50,   // +50%
                EnduranceMod = -5,
                StrengthMod = 5,
                AgilityMod = 5,
                IntellectMod = -5,
                ManaPerIntellectMod = -25,
                RangeAttackSpeedMod = -10
            },

            // From skills/generic/MageClassPassive.gc
            ["Mage"] = new ClassPassive
            {
                PassiveSkillId = "skills.generic.MageClassPassive",
                Profession = "skills.professions.Warlock",
                HealthPerEnduranceMod = -25,  // -25%
                EnduranceMod = 5,
                StrengthMod = -5,
                AgilityMod = -5,
                IntellectMod = 5,
                ManaPerIntellectMod = 100,
                RangeAttackSpeedMod = 0
            },

            // From skills/generic/RangerClassPassive.gc
            ["Ranger"] = new ClassPassive
            {
                PassiveSkillId = "skills.generic.RangerClassPassive",
                Profession = "skills.professions.Ranger",
                HealthPerEnduranceMod = 10,   // +10%
                EnduranceMod = 5,
                StrengthMod = -5,
                AgilityMod = 5,
                IntellectMod = -5,
                ManaPerIntellectMod = -5,
                RangeAttackSpeedMod = 0
            }
        };

        /// <summary>
        /// Starting skills for each class - derived from avatar/classes/*StartingSkills.gc
        /// NOTE: Only active skills! Passives are auto-loaded by client based on avatar.GCClass
        /// </summary>
        public static readonly Dictionary<string, StartingSkillSet> StartingSkills = new Dictionary<string, StartingSkillSet>
        {
            // From avatar/classes/FighterStartingSkills.gc
            ["Fighter"] = new StartingSkillSet
            {
                ActiveSkills = new List<SkillEntry>
                {
                    new SkillEntry { SkillId = "skills.generic.Butcher", Level = 1, SlotId = 105 },
                    new SkillEntry { SkillId = "skills.generic.Stomp", Level = 1, SlotId = 100 }
                }
            },

            // From avatar/classes/WarlockStartingSkills.gc
            ["Mage"] = new StartingSkillSet
            {
                ActiveSkills = new List<SkillEntry>
                {
                    new SkillEntry { SkillId = "skills.generic.FireBolt", Level = 1, SlotId = 105 },
                    new SkillEntry { SkillId = "skills.generic.ShadowLightning", Level = 1, SlotId = 100 }
                }
            },

            // From avatar/classes/RangerStartingSkills.gc
            ["Ranger"] = new StartingSkillSet
            {
                ActiveSkills = new List<SkillEntry>
                {
                    new SkillEntry { SkillId = "skills.generic.PoisonShot", Level = 1, SlotId = 105 },
                    new SkillEntry { SkillId = "skills.generic.PoisonBlastRadius", Level = 1, SlotId = 100 }
                }
            }
        };

        /// <summary>
        /// Calculate HP bonus in wire format based on class passive.
        /// Values are native runtime anchors from EntitySynchInfo HP validation.
        /// </summary>
        public static int CalculateHPBonusWire(string className, int level = 1, int allocatedEndurance = 0)
        {
            int baseEndurance = BASE_ENDURANCE + Math.Max(0, allocatedEndurance);
            uint noPassiveHP = CalculateNativeHPWire(level, baseEndurance, 0);

            if (!Passives.TryGetValue(className, out ClassPassive passive))
            {
                Debug.LogWarning($"[ClassPassiveData] Unknown class '{className}', returning 0 HP bonus");
                return 0;
            }

            int passiveEndurance = Math.Max(1, baseEndurance + passive.EnduranceMod);
            uint passiveHP = CalculateNativeHPWire(level, passiveEndurance, passive.HealthPerEnduranceMod);
            long bonus = (long)passiveHP - noPassiveHP;
            if (bonus > int.MaxValue) bonus = int.MaxValue;
            if (bonus < int.MinValue) bonus = int.MinValue;
            Debug.LogError($"[ClassPassiveData] {className}: HP Bonus = {bonus} wire noPassive={noPassiveHP} passive={passiveHP} level={level} end={baseEndurance}->{passiveEndurance} hpeMod={passive.HealthPerEnduranceMod}");
            return (int)bonus;
        }

        /// <summary>
        /// Get the profession for a class
        /// </summary>
        public static string GetProfession(string className)
        {
            if (Passives.TryGetValue(className, out var passive))
            {
                return passive.Profession;
            }

            // Default to Warrior if unknown
            Debug.LogWarning($"[ClassPassiveData] Unknown class '{className}', defaulting to Warrior profession");
            return "skills.professions.Warrior";
        }

        /// <summary>
        /// Get starting active skills for a class
        /// </summary>
        public static List<string> GetStartingSkillIds(string className)
        {
            if (StartingSkills.TryGetValue(className, out var skillSet))
            {
                var ids = new List<string>();
                foreach (var skill in skillSet.ActiveSkills)
                {
                    ids.Add(skill.SkillId);
                }
                return ids;
            }

            Debug.LogWarning($"[ClassPassiveData] Unknown class '{className}', returning empty skill list");
            return new List<string>();
        }
    }

    /// <summary>
    /// Class passive modifier data
    /// </summary>
    public class ClassPassive
    {
        public string PassiveSkillId { get; set; }
        public string Profession { get; set; }
        public int HealthPerEnduranceMod { get; set; }  // Percentage modifier
        public int EnduranceMod { get; set; }
        public int StrengthMod { get; set; }
        public int AgilityMod { get; set; }
        public int IntellectMod { get; set; }
        public int ManaPerIntellectMod { get; set; }
        public int RangeAttackSpeedMod { get; set; }
    }

    /// <summary>
    /// Starting skill set for a class
    /// </summary>
    public class StartingSkillSet
    {
        public List<SkillEntry> ActiveSkills { get; set; } = new List<SkillEntry>();
    }

    /// <summary>
    /// Individual skill entry with metadata
    /// </summary>
    public class SkillEntry
    {
        public string SkillId { get; set; }
        public int Level { get; set; }
        public int SlotId { get; set; }
    }
}
