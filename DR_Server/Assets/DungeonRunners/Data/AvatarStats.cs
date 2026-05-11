using System;
using UnityEngine;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Avatar stats calculator based on actual game files (.gc data)
    /// All values extracted from PAL.zip/base.zip game data
    /// </summary>
    public static class AvatarStats
    {
        // ═══════════════════════════════════════════════════════════════════════════════
        // BASE VALUES FROM avatar/base/avatar.gc
        // ═══════════════════════════════════════════════════════════════════════════════

        /// <summary>Base HitPoints from avatar.gc: HitPoints = 51200 (wire format, 200 HP)</summary>
        public const uint BASE_HITPOINTS_WIRE = 51200;

        /// <summary>Base ManaPoints from avatar.gc: ManaPoints = 51200 (wire format, 200 Mana)</summary>
        public const uint BASE_MANAPOINTS_WIRE = 51200;

        /// <summary>Base stats from avatar.gc Description</summary>
        public const int BASE_STRENGTH = 10;
        public const int BASE_AGILITY = 10;
        public const int BASE_TOUGHNESS = 10;  // Called "Endurance" in passives
        public const int BASE_POWER = 10;      // Called "Intellect" in passives

        // ═══════════════════════════════════════════════════════════════════════════════
        // CLASS PASSIVE MODIFIERS FROM skills/generic/*.gc
        // ═══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fighter class passive from FighterClassPassive.gc
        /// Profession: skills.professions.Warrior
        /// </summary>
        public static class Fighter
        {
            public const int HEALTH_PER_ENDURANCE_MOD = 50;    // +50%
            public const int MANA_PER_INTELLECT_MOD = -25;     // -25%
            public const int STRENGTH_BONUS = 5;
            public const int AGILITY_BONUS = 5;
            public const int ENDURANCE_BONUS = -5;             // Penalty
            public const int INTELLECT_BONUS = -5;             // Penalty
            public const string PROFESSION = "skills.professions.Warrior";
        }

        /// <summary>
        /// Mage class passive from MageClassPassive.gc
        /// Profession: skills.professions.Warlock
        /// </summary>
        public static class Mage
        {
            public const int HEALTH_PER_ENDURANCE_MOD = -25;   // -25%
            public const int MANA_PER_INTELLECT_MOD = 100;     // +100%
            public const int STRENGTH_BONUS = -5;              // Penalty
            public const int AGILITY_BONUS = -5;               // Penalty
            public const int ENDURANCE_BONUS = 5;
            public const int INTELLECT_BONUS = 5;
            public const string PROFESSION = "skills.professions.Warlock";
        }

        /// <summary>
        /// Ranger class passive from RangerClassPassive.gc
        /// Profession: skills.professions.Ranger
        /// </summary>
        public static class Ranger
        {
            public const int HEALTH_PER_ENDURANCE_MOD = 10;    // +10%
            public const int MANA_PER_INTELLECT_MOD = -5;      // -5%
            public const int STRENGTH_BONUS = -5;              // Penalty
            public const int AGILITY_BONUS = 5;
            public const int ENDURANCE_BONUS = 5;
            public const int INTELLECT_BONUS = -5;             // Penalty
            public const string PROFESSION = "skills.professions.Ranger";
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // RPG SETTINGS - Base HP/Mana per stat point
        // These are the base values before class modifiers are applied
        // From WarriorCoreAttributeTrait: "for every point of Endurance you gain almost 38 Health"
        // ═══════════════════════════════════════════════════════════════════════════════

        /// <summary>Base HP gained per point of Toughness/Endurance (before modifiers)</summary>
        public const float HEALTH_PER_ENDURANCE_BASE = 38f;

        /// <summary>Base Mana gained per point of Power/Intellect (before modifiers)</summary>
        public const float MANA_PER_INTELLECT_BASE = 25f;

        /// <summary>HP gained per character level</summary>
        public const float HEALTH_PER_LEVEL = 10f;

        /// <summary>Mana gained per character level</summary>
        public const float MANA_PER_LEVEL = 5f;

        // ═══════════════════════════════════════════════════════════════════════════════
        // CALCULATED STATS
        // ═══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get effective stats for a character class
        /// </summary>
        public static CharacterStats GetEffectiveStats(string characterClass, int level = 1)
        {
            var stats = new CharacterStats
            {
                Level = level,
                CharacterClass = characterClass,

                // Start with base stats
                Strength = BASE_STRENGTH,
                Agility = BASE_AGILITY,
                Toughness = BASE_TOUGHNESS,
                Power = BASE_POWER,

                // Base modifiers (from class .gc files: HealthPerEnduranceMod = 1.0)
                HealthPerEnduranceMod = 100,  // 100% = 1.0
                ManaPerIntellectMod = 100     // 100% = 1.0
            };

            // Apply class-specific passive bonuses
            switch (characterClass)
            {
                case "Fighter":
                    stats.Strength += Fighter.STRENGTH_BONUS;
                    stats.Agility += Fighter.AGILITY_BONUS;
                    stats.Toughness += Fighter.ENDURANCE_BONUS;
                    stats.Power += Fighter.INTELLECT_BONUS;
                    stats.HealthPerEnduranceMod += Fighter.HEALTH_PER_ENDURANCE_MOD;
                    stats.ManaPerIntellectMod += Fighter.MANA_PER_INTELLECT_MOD;
                    stats.Profession = Fighter.PROFESSION;
                    break;

                case "Mage":
                    stats.Strength += Mage.STRENGTH_BONUS;
                    stats.Agility += Mage.AGILITY_BONUS;
                    stats.Toughness += Mage.ENDURANCE_BONUS;
                    stats.Power += Mage.INTELLECT_BONUS;
                    stats.HealthPerEnduranceMod += Mage.HEALTH_PER_ENDURANCE_MOD;
                    stats.ManaPerIntellectMod += Mage.MANA_PER_INTELLECT_MOD;
                    stats.Profession = Mage.PROFESSION;
                    break;

                case "Ranger":
                    stats.Strength += Ranger.STRENGTH_BONUS;
                    stats.Agility += Ranger.AGILITY_BONUS;
                    stats.Toughness += Ranger.ENDURANCE_BONUS;
                    stats.Power += Ranger.INTELLECT_BONUS;
                    stats.HealthPerEnduranceMod += Ranger.HEALTH_PER_ENDURANCE_MOD;
                    stats.ManaPerIntellectMod += Ranger.MANA_PER_INTELLECT_MOD;
                    stats.Profession = Ranger.PROFESSION;
                    break;

                default:
                    Debug.LogWarning($"[AvatarStats] Unknown class '{characterClass}', using Fighter defaults");
                    stats.Strength += Fighter.STRENGTH_BONUS;
                    stats.Agility += Fighter.AGILITY_BONUS;
                    stats.Toughness += Fighter.ENDURANCE_BONUS;
                    stats.Power += Fighter.INTELLECT_BONUS;
                    stats.HealthPerEnduranceMod += Fighter.HEALTH_PER_ENDURANCE_MOD;
                    stats.ManaPerIntellectMod += Fighter.MANA_PER_INTELLECT_MOD;
                    stats.Profession = Fighter.PROFESSION;
                    break;
            }

            // Calculate derived stats
            stats.CalculateDerivedStats();

            return stats;
        }

        /// <summary>
        /// Get the profession GCType for a character class
        /// </summary>
        public static string GetProfession(string characterClass)
        {
            switch (characterClass)
            {
                case "Fighter": return Fighter.PROFESSION;
                case "Mage": return Mage.PROFESSION;
                case "Ranger": return Ranger.PROFESSION;
                default: return Fighter.PROFESSION;
            }
        }
    }

    /// <summary>
    /// Character stats container with calculated values
    /// </summary>
    public class CharacterStats
    {
        // Identity
        public string CharacterClass { get; set; }
        public string Profession { get; set; }
        public int Level { get; set; }

        // Primary Stats (after class bonuses)
        public int Strength { get; set; }
        public int Agility { get; set; }
        public int Toughness { get; set; }  // Endurance
        public int Power { get; set; }       // Intellect

        // Modifiers (percentage, 100 = 100% = 1.0x)
        public int HealthPerEnduranceMod { get; set; }
        public int ManaPerIntellectMod { get; set; }

        // Calculated Values (in wire format * 256)
        public uint MaxHPWire { get; private set; }
        public uint MaxManaWire { get; private set; }
        public uint CurrentHPWire { get; private set; }
        public uint CurrentManaWire { get; private set; }

        // Human-readable values
        public float MaxHP => MaxHPWire / 256f;
        public float MaxMana => MaxManaWire / 256f;
        public float CurrentHP => CurrentHPWire / 256f;
        public float CurrentMana => CurrentManaWire / 256f;

        /// <summary>
        /// Calculate derived stats (HP, Mana) based on primary stats and modifiers
        /// </summary>
        public void CalculateDerivedStats()
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // HP CALCULATION
            // Formula: BaseHP + (Toughness * HealthPerEndurance * HealthPerEnduranceMod)
            //          + (Level * HealthPerLevel)
            // ═══════════════════════════════════════════════════════════════════════════

            float baseHP = AvatarStats.BASE_HITPOINTS_WIRE / 256f;  // 200 HP

            // HP from Toughness stat with modifier
            float hpFromStats = Toughness * AvatarStats.HEALTH_PER_ENDURANCE_BASE * (HealthPerEnduranceMod / 100f);

            // HP from level
            float hpFromLevel = (Level - 1) * AvatarStats.HEALTH_PER_LEVEL;

            float totalHP = baseHP + hpFromStats + hpFromLevel;
            MaxHPWire = (uint)(totalHP * 256);  // NO MASK - client compares exact values!
            CurrentHPWire = MaxHPWire;  // Start at full HP

            // ═══════════════════════════════════════════════════════════════════════════
            // MANA CALCULATION
            // Formula: BaseMana + (Power * ManaPerIntellect * ManaPerIntellectMod)
            //          + (Level * ManaPerLevel)
            // ═══════════════════════════════════════════════════════════════════════════

            float baseMana = AvatarStats.BASE_MANAPOINTS_WIRE / 256f;  // 200 Mana

            // Mana from Power stat with modifier
            float manaFromStats = Power * AvatarStats.MANA_PER_INTELLECT_BASE * (ManaPerIntellectMod / 100f);

            // Mana from level
            float manaFromLevel = (Level - 1) * AvatarStats.MANA_PER_LEVEL;

            float totalMana = baseMana + manaFromStats + manaFromLevel;
            MaxManaWire = (uint)(totalMana * 256);  // NO MASK - client compares exact values!
            CurrentManaWire = MaxManaWire;  // Start at full Mana

            Debug.LogError($"[CharacterStats] {CharacterClass} Level {Level}:");
            Debug.LogError($"  Stats: STR={Strength} AGI={Agility} TGH={Toughness} POW={Power}");
            Debug.LogError($"  Mods: HP/End={HealthPerEnduranceMod}% Mana/Int={ManaPerIntellectMod}%");
            Debug.LogError($"  HP: base={baseHP:F1} + stats={hpFromStats:F1} + level={hpFromLevel:F1} = {totalHP:F1} (wire=0x{MaxHPWire:X8})");
            Debug.LogError($"  Mana: base={baseMana:F1} + stats={manaFromStats:F1} + level={manaFromLevel:F1} = {totalMana:F1} (wire=0x{MaxManaWire:X8})");
        }

        /// <summary>
        /// Get HP value for Op12 (avatar init) - this is the BASE HP before client adds bonuses
        /// </summary>
        public uint GetOp12HP()
        {
            // For Op12, send the base HP that the client will add stat bonuses to
            // The client calculates: Op12HP + StatBonuses = FinalHP
            // So we send: MaxHP - StatBonuses = BaseHP

            float hpFromStats = Toughness * AvatarStats.HEALTH_PER_ENDURANCE_BASE * (HealthPerEnduranceMod / 100f);
            float hpFromLevel = (Level - 1) * AvatarStats.HEALTH_PER_LEVEL;
            float baseHP = MaxHP - hpFromStats - hpFromLevel;

            uint wireValue = (uint)(baseHP * 256);  // NO MASK!
            Debug.LogError($"[CharacterStats] Op12 HP: {baseHP:F1} (wire=0x{wireValue:X8})");
            return wireValue;
        }

        /// <summary>
        /// Get HP value for synch messages - this is the CALCULATED HP that client compares against
        /// </summary>
        public uint GetSynchHP()
        {
            // For synch, send the full calculated HP including all bonuses
            // This must match exactly what the client calculates
            Debug.LogError($"[CharacterStats] Synch HP: {MaxHP:F1} (wire=0x{MaxHPWire:X8})");
            return MaxHPWire;
        }

        /// <summary>
        /// Get Mana value for Op12 (avatar init)
        /// </summary>
        public uint GetOp12Mana()
        {
            float manaFromStats = Power * AvatarStats.MANA_PER_INTELLECT_BASE * (ManaPerIntellectMod / 100f);
            float manaFromLevel = (Level - 1) * AvatarStats.MANA_PER_LEVEL;
            float baseMana = MaxMana - manaFromStats - manaFromLevel;

            uint wireValue = (uint)(baseMana * 256);  // NO MASK!
            Debug.LogError($"[CharacterStats] Op12 Mana: {baseMana:F1} (wire=0x{wireValue:X8})");
            return wireValue;
        }

        /// <summary>
        /// Get Mana value for synch messages
        /// </summary>
        public uint GetSynchMana()
        {
            Debug.LogError($"[CharacterStats] Synch Mana: {MaxMana:F1} (wire=0x{MaxManaWire:X8})");
            return MaxManaWire;
        }
    }
}