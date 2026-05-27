using System;
using System.Collections.Generic;
using DungeonRunners.Data;
using UnityEngine;

namespace DungeonRunners.Combat
{
    // ═══════════════════════════════════════════════════════════════════════
    // MONSTER ATTACK DATA — Stage 1 of combat simulation
    //
    // Loads per-monster combat inputs (AttackSpeed, AttackRating, DefenseRating,
    // DamageMod, primary weapon Damage/Type, AttackStyle, AgroRange, etc.) from
    // the authored .gc files (per [[feedback-strictly-native-data]]), plus the
    // global combat multipliers from GlobalKnobs.gc.
    //
    // Stage 1 (this file) is DORMANT INFRASTRUCTURE. No live consumers yet.
    // Stage 2 (MonsterAttackController + MonsterDamageRoll) will query
    // TryGetProfile() on every aggro tick to drive the server-side combat
    // simulator.
    //
    // Authored data layout (per WORK/AUDIT_COMBAT/10_MONSTER_ATTACK_AUTHORING.md
    // + sample file `Database/gc/Abba_Labba_Melee_Grunt_Base.gc`):
    //
    //   SomeMob extends base.Summonable {
    //     Description {           // → UnitDesc fields
    //       AttackSpeed   = 0.8;
    //       AttackRating  = 0.25;
    //       DefenseRating = 0.25;
    //       DamageMod     = 0.25;
    //       MaxHealth     = 0.2;
    //       HealthRegen   = 0;
    //       FactionID     = 2;
    //       Speed         = 55;
    //     }
    //     Manipulators {
    //       PrimaryWeapon extends base.MeleeUnitWeapon {
    //         ID = 10;
    //         Description {
    //           Damage     = 1;
    //           DamageType = SLASHING;
    //         }
    //       }
    //     }
    //     Behavior {              // = MonsterBehavior2
    //       static Description {  // = MonsterBehavior2Desc
    //         AttackStyle = DEFENSIVE;
    //         AgroRange   = 150;
    //         ShoutRange  = 300;
    //         LeashRange  = 200;
    //       }
    //     }
    //   }
    //
    // Loading is LAZY — `TryGetProfile(gcType, out profile)` reads + caches on
    // first request, so Stage 1 boot cost is just GlobalKnobs caching. Stage 2
    // can opt to eagerly enumerate spawnable mobs at boot if validation telemetry
    // becomes useful, by iterating zone_world_entities or similar.
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class MonsterAttackProfile
    {
        // The gcType this profile was resolved from (after path-alias normalization)
        public string GCType;
        // The actual .gc node it resolved to (may differ if aliases matched)
        public string ResolvedName;

        // ── Description block (UnitDesc) ──
        public float AttackSpeed;        // mob-class multiplier, typical 0.5–1.5
        public float AttackRating;       // base AR scalar
        public float DefenseRating;      // base DR scalar
        public float DamageMod;          // mob-class damage scalar
        public float MaxHealth;          // mob-class HP scalar (combined with curve later)
        public float HealthRegen;        // per-mob regen mod
        public float Speed;              // run speed
        public int   FactionID;          // see AUDIT_COMBAT/07_AGGRO_TARGETING.md

        // ── Manipulators.PrimaryWeapon (resolved through inheritance) ──
        public int    WeaponID;
        public float  WeaponDamage;            // base damage (typical 1.0 for mob weapons)
        public string WeaponDamageType;        // SLASHING / BLUDGEONING / PIERCING / ...
        public float  WeaponDamageVolatility;  // ± volatility fraction (default 0.5 from MeleeUnitWeapon)
        public float  WeaponRange;             // attack range (default 5 for melee)
        public float  WeaponCoolDown;          // base cooldown sec
        public int    WeaponSpeed;             // attackSpeedMod256 source (100 = 1.0×)
        public string WeaponClass;             // HTH / 1HMELEE / 2HMELEE / RANGED / ...
        public bool   HasPrimaryWeapon;

        // ── Behavior.Description (MonsterBehavior2Desc) ──
        public string AttackStyle;       // AGGRESSIVE / DEFENSIVE / NEUTRAL
        public string IdleAction;        // FOLLOW / IDLE / WANDER / ...
        public int   AgroRange;
        public int   ShoutRange;
        public int   LeashRange;
        public bool  Leashed;

        public override string ToString() =>
            $"MonsterAttackProfile[{GCType}: AS={AttackSpeed} AR={AttackRating} DR={DefenseRating} DMod={DamageMod} HP={MaxHealth} " +
            $"wpn=[ID={WeaponID} dmg={WeaponDamage} type={WeaponDamageType} vol={WeaponDamageVolatility} " +
            $"range={WeaponRange} cd={WeaponCoolDown} spd={WeaponSpeed} class={WeaponClass}] " +
            $"style={AttackStyle} agro={AgroRange}]";
    }

    public sealed class MonsterAttackData
    {
        private static MonsterAttackData _instance;
        public static MonsterAttackData Instance => _instance ??= new MonsterAttackData();

        private readonly Dictionary<string, MonsterAttackProfile> _profiles =
            new Dictionary<string, MonsterAttackProfile>(StringComparer.OrdinalIgnoreCase);

        public bool IsInitialized { get; private set; }

        // ── Global multipliers (cached from GlobalKnobs.gc at Init) ──
        // All values match the same name in the authored file — server reads,
        // never invents (see [[feedback-strictly-native-data]]).
        public int MonsterAttackSpeed { get; private set; }
        public int MonsterDamageMod { get; private set; }
        public int MonsterCriticalChance { get; private set; }
        public int MonsterStunMod { get; private set; }
        public int MonsterHealthRegen { get; private set; }
        public int MonsterPowerRegen { get; private set; }
        public int MonsterStunResist { get; private set; }
        public int MonsterMissChance { get; private set; }
        public int MonsterMissDamageMod { get; private set; }
        public int MonsterStunChance { get; private set; }

        public int HeroMissChance { get; private set; }
        public int HeroMissDamageMod { get; private set; }
        public int HeroStunChance { get; private set; }
        public int HeroCriticalChance { get; private set; }
        public int HeroStunResist { get; private set; }
        public int HeroStunMod { get; private set; }
        public int HeroAttackSpeed { get; private set; }
        public int HeroHealthRegen { get; private set; }
        public int HeroPowerRegen { get; private set; }

        public int WeaponDamagePerLevel { get; private set; }
        public int SkillDamagePerLevel { get; private set; }
        public float DPSModifier { get; private set; }

        public float MeleeDamagePerStrength { get; private set; }
        public float RangedDamagePerAgility { get; private set; }
        public float SkillDamagePerIntellect { get; private set; }
        public float DefenseRatingPerStrength { get; private set; }
        public float AttackRatingPerAgility { get; private set; }

        /// <summary>
        /// Caches GlobalKnobs.gc values. Per-mob profiles load lazily on first
        /// `TryGetProfile`. Call once after `GCDatabase.Instance.Load`.
        /// </summary>
        public void Init()
        {
            if (IsInitialized) return;

            var knobs = GCDatabase.Instance.GlobalKnobs;
            if (knobs == null)
            {
                Debug.LogError("[MONSTER-ATTACK-DATA] GlobalKnobs.gc not loaded — combat simulator multipliers will be zero. Call after GCDatabase.Load.");
                return;
            }

            MonsterAttackSpeed     = knobs.GetInt("MonsterAttackSpeed", 100);
            MonsterDamageMod       = knobs.GetInt("MonsterDamageMod", 0);
            MonsterCriticalChance  = knobs.GetInt("MonsterCriticalChance", 6);
            MonsterStunMod         = knobs.GetInt("MonsterStunMod", 100);
            MonsterHealthRegen     = knobs.GetInt("MonsterHealthRegen", 2);
            MonsterPowerRegen      = knobs.GetInt("MonsterPowerRegen", 2);
            MonsterStunResist      = knobs.GetInt("MonsterStunResist", 1);
            MonsterMissChance      = knobs.GetInt("MonsterMissChance", 100);
            MonsterMissDamageMod   = knobs.GetInt("MonsterMissDamageMod", 0);
            MonsterStunChance      = knobs.GetInt("MonsterStunChance", 50);

            HeroMissChance         = knobs.GetInt("HeroMissChance", 100);
            HeroMissDamageMod      = knobs.GetInt("HeroMissDamageMod", 0);
            HeroStunChance         = knobs.GetInt("HeroStunChance", 100);
            HeroCriticalChance     = knobs.GetInt("HeroCriticalChance", 3);
            HeroStunResist         = knobs.GetInt("HeroStunResist", 1);
            HeroStunMod            = knobs.GetInt("HeroStunMod", 100);
            HeroAttackSpeed        = knobs.GetInt("HeroAttackSpeed", 100);
            HeroHealthRegen        = knobs.GetInt("HeroHealthRegen", 2);
            HeroPowerRegen         = knobs.GetInt("HeroPowerRegen", 3);

            WeaponDamagePerLevel   = knobs.GetInt("WeaponDamagePerLevel", 10);
            SkillDamagePerLevel    = knobs.GetInt("SkillDamagePerLevel", 15);
            DPSModifier            = knobs.GetFloat("DPSModifier", 1.0f);

            MeleeDamagePerStrength    = knobs.GetFloat("MeleeDamagePerStrength", 2.3364f);
            RangedDamagePerAgility    = knobs.GetFloat("RangedDamagePerAgility", 2.124f);
            SkillDamagePerIntellect   = knobs.GetFloat("SkillDamagePerIntellect", 1.5f);
            DefenseRatingPerStrength  = knobs.GetFloat("DefenseRatingPerStrength", 14f);
            AttackRatingPerAgility    = knobs.GetFloat("AttackRatingPerAgility", 14f);

            IsInitialized = true;

            Debug.LogError(
                $"[MONSTER-ATTACK-DATA] GlobalKnobs cached. " +
                $"MonsterAttackSpeed={MonsterAttackSpeed} MonsterCriticalChance={MonsterCriticalChance} " +
                $"MonsterMissChance={MonsterMissChance} MonsterDamageMod={MonsterDamageMod} " +
                $"HeroCriticalChance={HeroCriticalChance} WeaponDamagePerLevel={WeaponDamagePerLevel} " +
                $"DPSModifier={DPSModifier:F2}");
        }

        /// <summary>
        /// Lazy lookup of a per-mob attack profile by gcType. Resolves the mob's
        /// flattened .gc node (with inheritance) and extracts combat fields.
        /// Caches results so subsequent queries are O(1).
        ///
        /// Returns false if the gcType cannot be resolved OR if the resolved node
        /// has no Description block at all (e.g. it's a path that's not actually
        /// a unit — caller should treat that as "no combat profile available").
        /// </summary>
        public bool TryGetProfile(string gcType, out MonsterAttackProfile profile)
        {
            profile = null;
            if (string.IsNullOrEmpty(gcType)) return false;

            if (_profiles.TryGetValue(gcType, out profile))
                return profile != null;

            var node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            if (node == null)
            {
                _profiles[gcType] = null;  // negative cache
                return false;
            }

            var desc = node.GetChild("Description");
            if (desc == null)
            {
                _profiles[gcType] = null;
                return false;
            }

            var built = new MonsterAttackProfile
            {
                GCType        = gcType,
                ResolvedName  = node.Name,

                AttackSpeed   = desc.GetFloat("AttackSpeed", 1.0f),
                AttackRating  = desc.GetFloat("AttackRating", 0f),
                DefenseRating = desc.GetFloat("DefenseRating", 0f),
                DamageMod     = desc.GetFloat("DamageMod", 0f),
                MaxHealth     = desc.GetFloat("MaxHealth", 0f),
                HealthRegen   = desc.GetFloat("HealthRegen", 0f),
                Speed         = desc.GetFloat("Speed", 0f),
                FactionID     = desc.GetInt("FactionID", 0),
            };

            var manips = node.GetChild("Manipulators");
            var primary = manips?.GetChild("PrimaryWeapon");
            if (primary != null)
            {
                built.HasPrimaryWeapon = true;
                built.WeaponID = primary.GetInt("ID", 0);

                // The PrimaryWeapon block has its own `extends base.MeleeUnitWeapon` (or
                // RangedUnitWeapon etc.) — FlattenInheritance only handles the top-level
                // mob node's parent chain, so we resolve the weapon's own parent here to
                // pick up inherited Description fields (DamageVolatility, Range, CoolDown,
                // WeaponClass, WeaponSpeed).
                var weaponMerged = primary;
                if (!string.IsNullOrEmpty(primary.Extends))
                {
                    var resolvedWeapon = GCDatabase.Instance.ResolveWithInheritance(primary.Extends);
                    if (resolvedWeapon != null)
                    {
                        // Manual merge: parent fields, then child overrides
                        weaponMerged = new GCNode
                        {
                            Name = primary.Name,
                            Extends = primary.Extends,
                        };
                        var parentDesc = resolvedWeapon.GetChild("Description");
                        var primaryDesc = primary.GetChild("Description");
                        var mergedDesc = new GCNode { Name = "Description" };
                        if (parentDesc != null)
                            foreach (var kvp in parentDesc.Properties) mergedDesc.Properties[kvp.Key] = kvp.Value;
                        if (primaryDesc != null)
                            foreach (var kvp in primaryDesc.Properties) mergedDesc.Properties[kvp.Key] = kvp.Value;
                        weaponMerged.Children["Description"] = mergedDesc;
                    }
                }

                var wpnDesc = weaponMerged.GetChild("Description");
                if (wpnDesc != null)
                {
                    built.WeaponDamage           = wpnDesc.GetFloat("Damage", 1.0f);
                    built.WeaponDamageType       = wpnDesc.GetString("DamageType", "");
                    built.WeaponDamageVolatility = wpnDesc.GetFloat("DamageVolatility", 0.5f);
                    built.WeaponRange            = wpnDesc.GetFloat("Range", 5f);
                    built.WeaponCoolDown         = wpnDesc.GetFloat("CoolDown", 1.75f);
                    built.WeaponSpeed            = wpnDesc.GetInt("WeaponSpeed", 100);
                    built.WeaponClass            = wpnDesc.GetString("WeaponClass", "");
                }
            }

            var beh = node.GetChild("Behavior");
            var behDesc = beh?.GetChild("Description");
            if (behDesc != null)
            {
                built.AttackStyle = behDesc.GetString("AttackStyle", "");
                built.IdleAction  = behDesc.GetString("IdleAction", "");
                built.AgroRange   = behDesc.GetInt("AgroRange", 0);
                built.ShoutRange  = behDesc.GetInt("ShoutRange", 0);
                built.LeashRange  = behDesc.GetInt("LeashRange", 0);
                built.Leashed     = behDesc.GetBool("Leashed", false);
            }

            _profiles[gcType] = built;
            profile = built;
            return true;
        }

        /// <summary>Number of profiles cached so far (positive + negative entries).</summary>
        public int CachedProfileCount => _profiles.Count;
    }
}
