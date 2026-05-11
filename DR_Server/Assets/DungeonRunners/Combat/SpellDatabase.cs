using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Complete skill/spell database — ALL 65 skills from skillsALL.json.
    /// Damage data extracted from decompressed finalconf.json (149MB GC database).
    /// 
    /// Spell damage formula (mirrors melee but uses different knobs):
    ///   baseDamage = (SkillDamagePerLevel(15) * level) + (SkillDamagePerIntellect(1.5) * Intellect(10))
    ///   scaled = baseDamage * spell.DamageMod
    ///   spread = scaled * spell.DamageVolatility
    ///   damage = RNG in [scaled - spread, scaled + spread]
    ///
    /// Weapon skills (Butcher, Cleave) use the MELEE formula instead:
    ///   baseDamage = (WeaponDamagePerLevel(10) * level) + (MeleeDamagePerStrength(2.3364) * Strength)
    ///
    /// Ranged skills use RangedDamagePerAgility(2.124) scaling.
    ///
    /// Base damage effect inheritance from GC:
    ///   skills.generic.base.Shadow.DamageEffect  -> AT=MAGIC, DT=SHADOW, DMod=0.50, DVol=0.25
    ///   skills.generic.base.Fire.DamageEffect    -> AT=MAGIC, DT=FIRE,   DMod=0.50, DVol=0.25
    ///   skills.generic.base.Divine.DamageEffect   -> AT=MAGIC, DT=DIVINE, DMod=0.50, DVol=0.25
    ///   skills.generic.base.Ice.DamageEffect      -> AT=MAGIC, DT=ICE,    DMod=0.50, DVol=0.25
    ///   skills.generic.base.Poison.DamageEffect   -> AT=MAGIC, DT=POISON, DMod=0.50, DVol=0.25
    ///   skills.generic.base.Poison.RangedDamageEffect -> AT=RANGED, DT=POISON (ranger arrows)
    /// </summary>
    public static class SpellDatabase
    {
        private static Dictionary<string, SpellData> _spells;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _spells = new Dictionary<string, SpellData>(StringComparer.OrdinalIgnoreCase);

            // =====================================================================
            // SHADOW SCHOOL -- Warlock/Mage offensive spells
            // =====================================================================

            Register("ShadowLightning", new SpellData
            {
                SkillId = "skills.generic.ShadowLightning",
                DisplayName = "Shadow Lightning",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.SHADOW,
                DamageMod = 0.80f,
                DamageVolatility = 0.75f,
                CriticalChance = 0.25f,
                Cooldown = 1.5f,
                Range = 50,
                ManaCostMod = 7.0f,
                IsChainSpell = true,
                NumChains = 5,
                ChainRange = 75,
                MaxSkillLevel = 20,
                RequiredLevel = 3,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("ShadowLightningKnockdown", new SpellData
            {
                SkillId = "skills.generic.ShadowLightningKnockdown",
                DisplayName = "Shadow Word: Smackdown",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.SHADOW,
                DamageMod = 0.10f,
                DamageVolatility = 0.10f,
                CriticalChance = 0.50f,
                Cooldown = 15.0f,
                Range = 50,
                ManaCostMod = 4.0f,
                HasKnockdown = true,
                MaxSkillLevel = 20,
                RequiredLevel = 3,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("ShadowBolt", new SpellData
            {
                SkillId = "skills.generic.ShadowBolt",
                DisplayName = "Shadow Bolt",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.SHADOW,
                DamageMod = 0.30f,
                DamageVolatility = 0.15f,
                Cooldown = 0f,
                Range = 60,
                ManaCostMod = 5.75f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("ShadowBlossom", new SpellData
            {
                SkillId = "skills.generic.ShadowBlossom",
                DisplayName = "Shadow Blossom",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.SHADOW,
                DamageMod = 0.50f,
                DamageVolatility = 0.30f,
                Cooldown = 3.0f,
                Range = 70,
                ManaCostMod = 1.0f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("ShadowRage", new SpellData
            {
                SkillId = "skills.generic.ShadowRage",
                DisplayName = "Shadow Rage",
                AttackType = AttackType.MELEE,
                DamageType = DamageElement.SHADOW,
                DamageMod = 0.12f,
                DamageVolatility = 0.10f,
                CriticalChance = 0.25f,
                Cooldown = 50.0f,
                ManaCostMod = 4.35f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("ShadowTendrils", new SpellData
            {
                SkillId = "skills.generic.ShadowTendrils",
                DisplayName = "Shadow Tendrils",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.SHADOW,
                DamageMod = 0.20f,
                DamageVolatility = 0.25f,
                Cooldown = 20.0f,
                ManaCostMod = 9.0f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            // =====================================================================
            // FIRE SCHOOL -- Mage offensive spells
            // =====================================================================

            Register("FireBolt", new SpellData
            {
                SkillId = "skills.generic.FireBolt",
                DisplayName = "Fire Bolt",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.75f,
                DamageVolatility = 0.60f,
                Cooldown = 0f,
                Range = 300,
                ManaCostMod = 3.0f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FireCone", new SpellData
            {
                SkillId = "skills.generic.FireCone",
                DisplayName = "Fire Cone",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.16f,
                DamageVolatility = 0.70f,
                CriticalChance = 0.10f,
                Cooldown = 0f,
                Range = 23,
                ManaCostMod = 8.45f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FireCurseShot", new SpellData
            {
                SkillId = "skills.generic.FireCurseShot",
                DisplayName = "Fire Curse Shot",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.35f,
                DamageVolatility = 0.50f,
                Cooldown = 7.0f,
                Range = 176,
                ManaCostMod = 3.75f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FireMeleeSummon", new SpellData
            {
                SkillId = "skills.generic.FireMeleeSummon",
                DisplayName = "Fire Melee Summon",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.80f,
                DamageVolatility = 0.50f,
                CriticalChance = 1.0f,
                Cooldown = 1.5f,
                Range = 12,
                ManaCostMod = 1.0f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FireRing", new SpellData
            {
                SkillId = "skills.generic.FireRing",
                DisplayName = "Fire Ring",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.55f,
                DamageVolatility = 0.75f,
                Cooldown = 8.0f,
                Range = 60,
                ManaCostMod = 10.0f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FireShot", new SpellData
            {
                SkillId = "skills.generic.FireShot",
                DisplayName = "Fire Shot",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.80f,
                DamageVolatility = 0.65f,
                Cooldown = 1.0f,
                Range = 176,
                ManaCostMod = 1.85f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FireTrail", new SpellData
            {
                // Extends PoisonTrail -- fire DoT trail, dual-element
                SkillId = "skills.generic.FireTrail",
                DisplayName = "Fire Trail",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 0.09f,
                DamageVolatility = 0.10f,
                CriticalChance = 0.01f,
                Cooldown = 18.0f,
                ManaCostMod = 8.6f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("HellFire", new SpellData
            {
                SkillId = "skills.generic.HellFire",
                DisplayName = "Hellfire",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.FIRE,
                DamageMod = 1.0f,
                DamageVolatility = 0.25f,
                Cooldown = 0f,
                Range = 100,
                ManaCostMod = 15.0f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });
            // Alias -- skillsALL.json uses "HellFire" but code may reference "Hellfire"
            if (_spells.TryGetValue("HellFire", out var hf)) _spells["Hellfire"] = hf;

            // =====================================================================
            // ICE SCHOOL -- Mage offensive spells
            // =====================================================================

            Register("IceBolt", new SpellData
            {
                SkillId = "skills.generic.IceBolt",
                DisplayName = "Ice Bolt",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.ICE,
                DamageMod = 0.70f,
                DamageVolatility = 0.40f,
                Cooldown = 0f,
                Range = 30,
                ManaCostMod = 4.0f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            Register("IceMultibolt", new SpellData
            {
                // Inherits base Ice.DamageEffect -- no overrides in GC
                SkillId = "skills.generic.IceMultibolt",
                DisplayName = "Ice Multi-Bolt",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.ICE,
                DamageMod = 0.50f,
                DamageVolatility = 0.25f,
                Cooldown = 0f,
                Range = 30,
                ManaCostMod = 6.2f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });
            if (_spells.TryGetValue("IceMultibolt", out var imb)) _spells["IceMultiBolt"] = imb;

            Register("IceShot", new SpellData
            {
                SkillId = "skills.generic.IceShot",
                DisplayName = "Ice Shot",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.ICE,
                DamageMod = 0.50f,
                DamageVolatility = 0.40f,
                CriticalChance = 0.25f,
                Cooldown = 1.0f,
                Range = 176,
                ManaCostMod = 1.8f,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("IceTargetedBurst", new SpellData
            {
                SkillId = "skills.generic.IceTargetedBurst",
                DisplayName = "Ice Targeted Burst",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.ICE,
                DamageMod = 0.25f,
                DamageVolatility = 0.75f,
                Cooldown = 12.0f,
                Range = 90,
                ManaCostMod = 10.0f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Offensive
            });

            // =====================================================================
            // DIVINE SCHOOL -- Fighter offensive spells
            // =====================================================================

            Register("Charge", new SpellData
            {
                SkillId = "skills.generic.Charge",
                DisplayName = "Charge",
                AttackType = AttackType.MELEE,
                DamageType = DamageElement.DIVINE,
                DamageMod = 1.15f,
                DamageVolatility = 0.25f,
                Cooldown = 7.0f,
                Range = 20,
                ManaCostMod = 1.5f,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("DivineIntervention", new SpellData
            {
                SkillId = "skills.generic.DivineIntervention",
                DisplayName = "Divine Intervention",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.DIVINE,
                DamageMod = 0.65f,
                DamageVolatility = 0.65f,
                Cooldown = 17.0f,
                ManaCostMod = 14.8f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("DivineMeleeAttack", new SpellData
            {
                SkillId = "skills.generic.DivineMeleeAttack",
                DisplayName = "Divine Melee Attack",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.DIVINE,
                DamageMod = 0.65f,
                DamageVolatility = 0.40f,
                CriticalChance = 0.25f,
                Cooldown = 5.0f,
                Range = 12,
                ManaCostMod = 1.0f,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("DivineRay", new SpellData
            {
                SkillId = "skills.generic.DivineRay",
                DisplayName = "Divine Ray",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.DIVINE,
                DamageMod = 0.425f,
                DamageVolatility = 0.40f,
                CriticalChance = 0.50f,
                Cooldown = 1.0f,
                Range = 130,
                ManaCostMod = 4.0f,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FearMeleeAttack", new SpellData
            {
                SkillId = "skills.generic.FearMeleeAttack",
                DisplayName = "Fear Melee Attack",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.DIVINE,
                DamageMod = 0.10f,
                DamageVolatility = 0.30f,
                CriticalChance = 0.25f,
                Cooldown = 5.0f,
                Range = 12,
                ManaCostMod = 1.25f,
                HasFear = true,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("Stomp", new SpellData
            {
                SkillId = "skills.generic.Stomp",
                DisplayName = "Stomp",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.DIVINE,
                DamageMod = 0.15f,
                DamageVolatility = 0.40f,
                CriticalChance = 0.75f,
                Cooldown = 20.0f,
                ManaCostMod = 1.5f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Offensive,
                NumTargetsMin = 6f,
                NumTargetsMax = 22f,
                NumTargetsInc = 0.8f,
                AoERadius = 50f
            });

            // =====================================================================
            // POISON SCHOOL -- Ranger offensive spells
            // =====================================================================

            Register("PoisonShot", new SpellData
            {
                SkillId = "skills.generic.PoisonShot",
                DisplayName = "Poison Shot",
                AttackType = AttackType.RANGED,
                DamageType = DamageElement.POISON,
                DamageMod = 0.43f,
                DamageVolatility = 0.30f,
                CriticalChance = 0.25f,
                Cooldown = 1.0f,
                Range = 176,
                ManaCostMod = 1.0f,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("PlagueShot", new SpellData
            {
                SkillId = "skills.generic.PlagueShot",
                DisplayName = "Plague Shot",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.POISON,
                DamageMod = 0.30f,
                DamageVolatility = 0.30f,
                Cooldown = 5.0f,
                Range = 90,
                ManaCostMod = 5.0f,
                IsChainSpell = true,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("NoxiousShot", new SpellData
            {
                SkillId = "skills.generic.NoxiousShot",
                DisplayName = "Noxious Shot",
                AttackType = AttackType.RANGED,
                DamageType = DamageElement.POISON,
                DamageMod = 0.33f,
                DamageVolatility = 0.30f,
                CriticalChance = 0.33f,
                Cooldown = 3.0f,
                Range = 176,
                ManaCostMod = 2.0f,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("PenetrateKnockdownShot", new SpellData
            {
                SkillId = "skills.generic.PenetrateKnockdownShot",
                DisplayName = "Penetrate Knockdown Shot",
                AttackType = AttackType.RANGED,
                DamageType = DamageElement.POISON,
                DamageMod = 0.10f,
                DamageVolatility = 0.20f,
                CriticalChance = 0.25f,
                Cooldown = 15.0f,
                Range = 176,
                ManaCostMod = 3.0f,
                HasKnockdown = true,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("PoisonBlastRadius", new SpellData
            {
                SkillId = "skills.generic.PoisonBlastRadius",
                DisplayName = "Poison Blast Radius",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.POISON,
                DamageMod = 0.30f,
                DamageVolatility = 0.30f,
                CriticalChance = 0.25f,
                Cooldown = 10.0f,
                ManaCostMod = 1.4f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive,
                NumTargetsMin = 4f,
                NumTargetsMax = 20f,
                NumTargetsInc = 0.8f,
                AoERadius = 30f
            });

            Register("PoisonTrail", new SpellData
            {
                SkillId = "skills.generic.PoisonTrail",
                DisplayName = "Poison Trail",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.POISON,
                DamageMod = 0.30f,
                DamageVolatility = 0.45f,
                CriticalChance = 0.25f,
                Cooldown = 18.0f,
                ManaCostMod = 8.6f,
                IsAoE = true,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Offensive
            });

            Register("FearShot", new SpellData
            {
                // CC skill -- no direct damage in GC, applies Fear debuff
                SkillId = "skills.generic.FearShot",
                DisplayName = "Fear Shot",
                AttackType = AttackType.RANGED,
                DamageType = DamageElement.PHYSICAL,
                DamageMod = 0f,
                Cooldown = 7.0f,
                Range = 176,
                ManaCostMod = 2.0f,
                HasFear = true,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.CrowdControl
            });

            // =====================================================================
            // WEAPON SKILLS -- Use melee/weapon damage formula, not spell formula
            // These extend SpellWeaponDamageEffect in GC
            // =====================================================================

            Register("Butcher", new SpellData
            {
                SkillId = "skills.generic.Butcher",
                DisplayName = "Butcher",
                AttackType = AttackType.MELEE,
                DamageType = DamageElement.PHYSICAL,
                DamageMod = 1.0f,
                Cooldown = 5.0f,
                Range = 12,
                ManaCostMod = 0.75f,
                IsWeaponSkill = true,
                MaxSkillLevel = 20,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.WeaponSkill,
                SkillDamageModMin = -60,
                SkillDamageModMax = 300,
                SkillDamageModInc = 5
            });

            Register("Cleave", new SpellData
            {
                SkillId = "skills.generic.Cleave",
                DisplayName = "Cleave",
                AttackType = AttackType.MELEE,
                DamageType = DamageElement.PHYSICAL,
                DamageMod = 1.0f,
                Cooldown = 5.0f,
                Range = 5,
                ManaCostMod = 1.8f,
                IsWeaponSkill = true,
                IsAoE = true,
                MaxSkillLevel = 15,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.WeaponSkill,
                SkillDamageModMin = -75,
                SkillDamageModMax = 0,
                SkillDamageModInc = 10,
                NumTargetsMin = 3.5f,
                NumTargetsMax = 8f,
                NumTargetsInc = 0.5f
            });

            // =====================================================================
            // DEBUFFS -- Apply modifiers, no direct damage
            // =====================================================================

            Register("Blight", new SpellData
            {
                SkillId = "skills.generic.Blight",
                DisplayName = "Blight",
                AttackType = AttackType.MAGIC,
                DamageType = DamageElement.POISON,
                DamageMod = 0f,
                Cooldown = 45.0f,
                ManaCostMod = 2.0f,
                MaxSkillLevel = 20,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Debuff
            });

            // =====================================================================
            // HEALING / UTILITY -- Self-targeted support skills
            // =====================================================================

            Register("HealSelf", new SpellData
            {
                SkillId = "skills.generic.HealSelf",
                DisplayName = "Heal Self",
                Cooldown = 80.0f,
                ManaCostMod = 4.52f,
                MaxSkillLevel = 20,
                SkillCategory = SkillCategory.Heal
            });

            Register("ManaSelf", new SpellData
            {
                SkillId = "skills.generic.ManaSelf",
                DisplayName = "Mana Self",
                Cooldown = 80.0f,
                ManaCostMod = 0.52f,
                MaxSkillLevel = 20,
                SkillCategory = SkillCategory.Utility
            });

            Register("ManaShield", new SpellData
            {
                SkillId = "skills.generic.ManaShield",
                DisplayName = "Mana Shield",
                Cooldown = 30.0f,
                ManaCostMod = 25.26f,
                MaxSkillLevel = 20,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Utility
            });

            Register("Sprint", new SpellData
            {
                SkillId = "skills.generic.Sprint",
                DisplayName = "Sprint",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Utility
            });

            Register("Teleport", new SpellData
            {
                SkillId = "skills.generic.Teleport",
                DisplayName = "Teleport",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Utility
            });

            Register("TownPortal", new SpellData
            {
                SkillId = "skills.generic.TownPortal",
                DisplayName = "Town Portal",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Utility
            });

            // =====================================================================
            // SUMMON SKILLS -- Spawn companion entities
            // =====================================================================

            Register("SummonBlingGnome", new SpellData
            {
                SkillId = "skills.generic.SummonBlingGnome",
                DisplayName = "Summon Bling Gnome",
                Cooldown = 45.0f,
                ManaCostMod = 0f,
                MaxSkillLevel = 1,
                SkillCategory = SkillCategory.Passive
            });

            Register("SummonMonsterBait", new SpellData
            {
                SkillId = "skills.generic.SummonMonsterBait",
                DisplayName = "Summon Monster Bait",
                Cooldown = 45.0f,
                Range = 100,
                ManaCostMod = 6.0f,
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Summon
            });

            Register("SummonSnowman", new SpellData
            {
                SkillId = "skills.generic.SummonSnowman",
                DisplayName = "Summon Snowman",
                Cooldown = 60.0f,
                ManaCostMod = 10.25f,
                DamageType = DamageElement.ICE,
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Summon
            });

            Register("SummonWolf", new SpellData
            {
                SkillId = "skills.generic.SummonWolf",
                DisplayName = "Summon Wolf",
                Cooldown = 1.0f,
                ManaCostMod = 5.0f,
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Summon
            });

            // =====================================================================
            // PASSIVE SKILLS -- Always active, no cast
            // Resist passives, class passives
            // =====================================================================

            Register("DivineResistPassive", new SpellData
            {
                SkillId = "skills.generic.DivineResistPassive",
                DisplayName = "Divine Resist",
                MaxSkillLevel = 10,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Passive
            });
            Register("FearResistModPassive", new SpellData
            {
                SkillId = "skills.generic.FearResistModPassive",
                DisplayName = "Fear Resist",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });
            Register("FighterClassPassive", new SpellData
            {
                SkillId = "skills.generic.FighterClassPassive",
                DisplayName = "Fighter Class Passive",
                MaxSkillLevel = 10,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Passive
            });
            Register("FireResistPassive", new SpellData
            {
                SkillId = "skills.generic.FireResistPassive",
                DisplayName = "Fire Resist",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });
            Register("IceResistPassive", new SpellData
            {
                SkillId = "skills.generic.IceResistPassive",
                DisplayName = "Ice Resist",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });
            Register("MageClassPassive", new SpellData
            {
                SkillId = "skills.generic.MageClassPassive",
                DisplayName = "Mage Class Passive",
                MaxSkillLevel = 10,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Passive
            });
            Register("MagicDamageModPassive", new SpellData
            {
                SkillId = "skills.generic.MagicDamageModPassive",
                DisplayName = "Magic Damage Mod",
                MaxSkillLevel = 10,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Passive
            });
            Register("MeleeAttackRatingModPassive", new SpellData
            {
                SkillId = "skills.generic.MeleeAttackRatingModPassive",
                DisplayName = "Melee Attack Rating",
                MaxSkillLevel = 10,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Passive
            });
            Register("MeleeAttackSpeedModPassive", new SpellData
            {
                SkillId = "skills.generic.MeleeAttackSpeedModPassive",
                DisplayName = "Melee Attack Speed",
                MaxSkillLevel = 10,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Passive
            });
            Register("MonsterBaitHealthModPassive", new SpellData
            {
                SkillId = "skills.generic.MonsterBaitHealthModPassive",
                DisplayName = "Monster Bait Health",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });
            Register("PoisonResistPassive", new SpellData
            {
                SkillId = "skills.generic.PoisonResistPassive",
                DisplayName = "Poison Resist",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });
            Register("RangeAttackSpeedModPassive", new SpellData
            {
                SkillId = "skills.generic.RangeAttackSpeedModPassive",
                DisplayName = "Ranged Attack Speed",
                MaxSkillLevel = 10,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Passive
            });
            Register("RangerClassPassive", new SpellData
            {
                SkillId = "skills.generic.RangerClassPassive",
                DisplayName = "Ranger Class Passive",
                MaxSkillLevel = 10,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Passive
            });
            Register("ShadowResistPassive", new SpellData
            {
                SkillId = "skills.generic.ShadowResistPassive",
                DisplayName = "Shadow Resist",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });
            Register("SummonerClassPassive", new SpellData
            {
                SkillId = "skills.generic.SummonerClassPassive",
                DisplayName = "Summoner Class Passive",
                MaxSkillLevel = 10,
                SkillCategory = SkillCategory.Passive
            });

            // =====================================================================
            // TRAIT PASSIVES -- Class-specific attribute/speed traits
            // =====================================================================

            Register("RangerCoreAttributeTrait", new SpellData
            {
                SkillId = "skills.generic.RangerCoreAttributeTrait",
                DisplayName = "Ranger Core Attribute",
                MaxSkillLevel = 10,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Passive
            });
            Register("RangerRangedSpeedTrait", new SpellData
            {
                SkillId = "skills.generic.RangerRangedSpeedTrait",
                DisplayName = "Ranger Ranged Speed",
                MaxSkillLevel = 10,
                ProfessionType = "RANGER",
                SkillCategory = SkillCategory.Passive
            });
            Register("WarlockCoreAttributeTrait", new SpellData
            {
                SkillId = "skills.generic.WarlockCoreAttributeTrait",
                DisplayName = "Warlock Core Attribute",
                MaxSkillLevel = 10,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Passive
            });
            Register("WarlockSpellDamageTrait", new SpellData
            {
                SkillId = "skills.generic.WarlockSpellDamageTrait",
                DisplayName = "Warlock Spell Damage",
                MaxSkillLevel = 10,
                ProfessionType = "MAGE",
                SkillCategory = SkillCategory.Passive
            });
            Register("WarriorCoreAttributeTrait", new SpellData
            {
                SkillId = "skills.generic.WarriorCoreAttributeTrait",
                DisplayName = "Warrior Core Attribute",
                MaxSkillLevel = 10,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Passive
            });
            Register("WarriorMeleeSpeedTrait", new SpellData
            {
                SkillId = "skills.generic.WarriorMeleeSpeedTrait",
                DisplayName = "Warrior Melee Speed",
                MaxSkillLevel = 10,
                ProfessionType = "FIGHTER",
                SkillCategory = SkillCategory.Passive
            });

            _initialized = true;
            int offensive = 0, weapon = 0, utility = 0, passive = 0, summon = 0;
            var seen = new HashSet<string>();
            foreach (var kvp in _spells)
            {
                if (!seen.Add(kvp.Value.ShortName)) continue;
                switch (kvp.Value.SkillCategory)
                {
                    case SkillCategory.Offensive: offensive++; break;
                    case SkillCategory.WeaponSkill: weapon++; break;
                    case SkillCategory.Passive: passive++; break;
                    case SkillCategory.Summon: summon++; break;
                    default: utility++; break;
                }
            }
            Debug.LogError($"[SpellDB] Initialized: {seen.Count} skills total -- {offensive} offensive, {weapon} weapon, {utility} utility/heal/cc/debuff, {summon} summon, {passive} passive");
        }

        private static void Register(string shortName, SpellData data)
        {
            data.ShortName = shortName;
            _spells[shortName] = data;
            if (!string.IsNullOrEmpty(data.SkillId))
                _spells[data.SkillId] = data;
        }

        /// <summary>Lookup spell by short name or full GC id.</summary>
        public static SpellData GetSpell(string name)
        {
            if (!_initialized) Initialize();
            if (string.IsNullOrEmpty(name)) return null;
            _spells.TryGetValue(name, out var data);
            return data;
        }

        /// <summary>Get all registered spells (unique).</summary>
        public static IEnumerable<SpellData> GetAllSpells()
        {
            if (!_initialized) Initialize();
            var seen = new HashSet<string>();
            foreach (var kvp in _spells)
            {
                if (seen.Add(kvp.Value.ShortName))
                    yield return kvp.Value;
            }
        }

        /// <summary>Get all offensive spells that deal damage.</summary>
        public static IEnumerable<SpellData> GetOffensiveSpells()
        {
            foreach (var spell in GetAllSpells())
            {
                if ((spell.SkillCategory == SkillCategory.Offensive ||
                     spell.SkillCategory == SkillCategory.WeaponSkill) && spell.DamageMod > 0)
                    yield return spell;
            }
        }

        /// <summary>Check if a skill deals damage.</summary>
        public static bool IsDamageSkill(string name)
        {
            var spell = GetSpell(name);
            if (spell == null) return false;
            return (spell.SkillCategory == SkillCategory.Offensive ||
                    spell.SkillCategory == SkillCategory.WeaponSkill) && spell.DamageMod > 0;
        }

        /// <summary>Get first offensive spell for a class.</summary>
        public static SpellData GetSpellForClass(string className)
        {
            if (!_initialized) Initialize();
            string prof = className?.ToUpper() switch
            {
                "FIGHTER" => "FIGHTER",
                "MAGE" => "MAGE",
                "WARLOCK" => "MAGE",
                "RANGER" => "RANGER",
                _ => "MAGE"
            };
            foreach (var kvp in _spells)
            {
                if (kvp.Value.ProfessionType == prof &&
                    kvp.Value.SkillCategory == SkillCategory.Offensive &&
                    kvp.Value.DamageMod > 0)
                    return kvp.Value;
            }
            return null;
        }
    }

    public class SpellData
    {
        public string ShortName;
        public string SkillId;
        public string DisplayName;
        public AttackType AttackType;
        public DamageElement DamageType;
        public float DamageMod;
        public float DamageVolatility;
        public float CriticalChance;
        public float Cooldown;
        public int Range;
        public float ManaCostMod;
        public int MaxSkillLevel;
        public int RequiredLevel;
        public string ProfessionType;
        public SkillCategory SkillCategory;
        public bool IsAoE;
        public bool IsChainSpell;
        public int NumChains;
        public int ChainRange;
        public bool IsWeaponSkill;
        public bool HasKnockdown;
        public bool HasFear;
        public bool HasSlow;

        // ── Weapon skill level scaling (from GC SpellWeaponDamageEffect) ──
        // skillMod = (100 + DamageModMin + skillLevel * DamageModInc) / 100
        // capped so that (DamageModMin + skillLevel * DamageModInc) <= DamageModMax
        // DamageModMax=0 means uncapped
        public int SkillDamageModMin;   // e.g. -60 for Butcher
        public int SkillDamageModMax;   // e.g. 300 for Butcher (0 = uncapped)
        public int SkillDamageModInc;   // e.g. 5 for Butcher

        // ── AoE target caps (from GC NumTargets fields) ──
        // maxTargets = (int)(NumTargetsMin + skillLevel * NumTargetsInc), capped at NumTargetsMax
        // 0 = unlimited
        public float NumTargetsMin;
        public float NumTargetsMax;
        public float NumTargetsInc;
        public float AoERadius;         // Override from GC RadiusMin (0 = use Range)
    }

    public enum AttackType { MELEE, MAGIC, RANGED }
    public enum DamageElement { PHYSICAL, DIVINE, FIRE, ICE, POISON, SHADOW }
    public enum SkillCategory
    {
        Offensive,      // Direct damage spells (FireBolt, ShadowLightning, etc.)
        WeaponSkill,    // Melee weapon abilities (Butcher, Cleave) -- use melee damage formula
        CrowdControl,   // CC skills (FearShot) -- no direct damage
        Debuff,         // Debuffs (Blight) -- apply modifiers
        Heal,           // Healing (HealSelf)
        Utility,        // Movement/portal/shield (Sprint, Teleport, TownPortal, ManaShield, ManaSelf)
        Summon,         // Summon companion (SummonWolf, SummonSnowman, etc.)
        Passive         // Always-on passives and traits
    }
}
