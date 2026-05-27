using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Builds a <see cref="MonsterUnitStats"/> from a <see cref="MonsterAttackProfile"/>
    /// + monster level + global knobs.
    ///
    /// <para>
    /// <b>v1 — APPROXIMATE.</b> The exact .gc-Description → unit-field caching formula
    /// is in the client's <c>Unit::readInit</c> (or similar) and hasn't been decompiled
    /// yet. This implementation uses sensible best-guess multipliers; divergence vs
    /// client will be visible in roll-by-roll x32dbg comparison and refined.
    /// </para>
    ///
    /// <para>
    /// Known approximations:
    /// <list type="bullet">
    /// <item>baseAR = round(AttackRating × level × WeaponDamagePerLevel)</item>
    /// <item>baseDR = round(DefenseRating × level × WeaponDamagePerLevel)</item>
    /// <item>baseDamageMod = round(DamageMod × 100) — fraction → percent</item>
    /// <item>BaseCriticalChance = GlobalKnobs.MonsterCriticalChance</item>
    /// <item>CritMultiplier = 150 (1.5×) — placeholder</item>
    /// <item>BlockChance = 0 (mobs don't block)</item>
    /// <item>All per-style fields default 0 (vanilla mobs have no special bonuses)</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class MonsterUnitStatsBuilder
    {
        public static MonsterUnitStats Build(MonsterAttackProfile profile, int level,
            float damageModOverride = float.NaN, float attackRatingOverride = float.NaN,
            float defenseRatingOverride = float.NaN)
        {
            var data = MonsterAttackData.Instance;
            // S10g: prefer caller-supplied overrides (from Monster.DamageMod/Monster.AttackRating
            // / Monster.DefenseRating set during CombatManager.SpawnMonster — they walk the
            // rank-suffix override chain (e.g. melee01.rank1 → AR=0.15) which the profile
            // doesn't see). See AUDIT_COMBAT/10d.
            float effectiveDamageMod = float.IsNaN(damageModOverride) ? profile.DamageMod : damageModOverride;
            float effectiveAttackRating = float.IsNaN(attackRatingOverride) ? profile.AttackRating : attackRatingOverride;
            float effectiveDefenseRating = float.IsNaN(defenseRatingOverride) ? profile.DefenseRating : defenseRatingOverride;

            // S10e 2026-05-27: discriminator IS the curve lookup key (per asm trace at
            // 0x0050FA85 — MOVZX ECX,byte ptr [ESP+0x14] then SHL ECX,0x8). Disc=2 for
            // all standard mobs.
            const byte discriminator = 2;

            var stats = new MonsterUnitStats
            {
                Level = level,

                // attackStyle: byte 1 for melee basic (MeleeUnitWeapon).
                // TODO when Property*Weapon*Desc*Style decompiled — for now, default melee.
                AttackStyle = ResolveAttackStyle(profile),

                WeaponDamageType = ResolveDamageTypeCode(profile.WeaponDamageType),

                Discriminator = discriminator,

                // Base attacker stats — S10e: use the actual MonsterAttackRating curve from
                // Tables.gc, not the level-linear approximation. Captured pup (rank1 AR=0.15)
                // gives baseAR = (38 × curve(disc=2)=102362) >> 16 = 59 ≈ 60 ✓
                BaseAttackRating = MonsterCurves.ComputeBaseAR(effectiveAttackRating, discriminator),
                BaseAttackRatingMod = 0,
                BaseDamageMod = ComputeBaseDamageMod(effectiveDamageMod),  // 10d: exact transform
                // 10d: monsters cache as 0 per captured pup (despite .gc CritChance=1.25).
                // RPGSettings monster-disc scalar likely small. Set 0 directly.
                BaseCriticalChance = 0,
                CritMultiplier = 150,  // placeholder 1.5× — TODO confirm via decompile
                BaseDamageBonus = 0,

                // Base defensive (mob → not used when mob is attacker, but tracked for completeness)
                BaseDefenseRating = MonsterCurves.ComputeBaseDR(effectiveDefenseRating, discriminator),
                BaseDefenseRatingMod = 0,
                BlockChance = 0,

                // Per-style cached fields all 0 (vanilla mobs have no weapon mods)
                MeleeAR = 0, MeleeARMod = 0, MeleeDamageBonus = 0, MeleeDamageMod = 0,
                MeleeCritChance = 0, MeleeDefenseRating = 0, MeleeDefenseRatingMod = 0,
                RangedAR = 0, RangedARMod = 0, RangedDamageBonus = 0, RangedDamageMod = 0,
                RangedCritChance = 0, RangedDefenseRating = 0, RangedDefenseRatingMod = 0,
                Style5AR = 0, Style5ARMod = 0, Style5DamageBonus = 0, Style5DamageMod = 0, Style5CritChance = 0,
                Style6AR = 0, Style6ARMod = 0, Style6DamageBonus = 0, Style6DamageMod = 0, Style6CritChance = 0,

                DamageModScale = 256,

                // Weapon stats
                WeaponDamageFixed = (int)System.Math.Round(profile.WeaponDamage * 256f),         // .gc Damage as Fixed32
                WeaponDamagePerLevel = data.WeaponDamagePerLevel,                                  // ECX factor (GlobalKnobs)
                WeaponVolatilityFixed = (int)System.Math.Round(profile.WeaponDamageVolatility * 256f),
            };
            return stats;
        }

        private static byte ResolveAttackStyle(MonsterAttackProfile profile)
        {
            // From the WeaponClass + DamageType combination. Vanilla MeleeUnitWeapon = HTH → 1.
            // 1HMelee / 2HMelee also melee. Ranged → 3.
            var cls = profile.WeaponClass ?? "";
            if (cls.IndexOf("RANGED", System.StringComparison.OrdinalIgnoreCase) >= 0
                || cls.IndexOf("BOW", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 3;
            return 1;  // default melee basic
        }

        private static byte ResolveDamageTypeCode(string damageType)
        {
            // From the .gc DamageType enum names. Order matches client's switch in
            // computeDamageMod (cases 0..7).
            if (string.IsNullOrEmpty(damageType)) return 0;
            switch (damageType.ToUpperInvariant())
            {
                case "SLASHING":   return 0;
                case "PIERCING":   return 1;
                case "BLUDGEONING":return 2;
                case "FIRE":       return 3;
                case "COLD":       return 4;
                case "LIGHTNING":  return 5;
                case "HOLY":       return 6;
                case "DARK":
                case "SHADOW":     return 7;
                default:           return 0;
            }
        }


        // Exact transform per UnitDesc::getDamageMod @ 0x0050FBF0 (see AUDIT_COMBAT/10d):
        //   cached = ((authored_Fixed32 - 256) × 25600) >> 16  ≈ (authored - 1.0) × 100
        //
        // 2026-05-27 update — S10g session: server's CombatManager.SpawnMonster log shows
        // `Monster.DamageMod = 0.50` for the pup at spawn time, matching the client's
        // captured [UnitDesc+0xDC]=128. So the "hidden scaling" S10f was looking for
        // already happens INSIDE the DR Reborn data path (DamageMod is loaded with the
        // 0.5 value already, NOT 1.0 from the .gc inheritance chain). Therefore we apply
        // the formula DIRECTLY to MonsterAttackProfile.DamageMod with no pre-scale —
        // adding a 0.5× pre-scale would double the halving.
        private static int ComputeBaseDamageMod(float authoredDamageMod)
        {
            int authoredFixed = Mathf.RoundToInt(authoredDamageMod * 256f);
            if (authoredFixed - 256 == 256) return 0;  // 2.0 special-case in client
            long delta = (long)(authoredFixed - 256);
            return (int)((delta * 25600L) >> 16);
        }

    }

    /// <summary>
    /// Builds a <see cref="PlayerUnitStats"/> for use as the damage target.
    /// v1 stub — pulls from <see cref="CombatPlayer"/> if available, else uses sensible defaults
    /// (level-1 unarmored hero).
    /// </summary>
    public static class PlayerUnitStatsBuilder
    {
        public static PlayerUnitStats Build(CombatPlayer player, int level = 1)
        {
            // TODO: pull from player's actual gear/stats once we have a stat-system mirror.
            // For v1, a level-1 unarmored hero with sensible defaults.
            return new PlayerUnitStats
            {
                BaseDefenseRating = 10 * level,    // approximation
                BaseDefenseRatingMod = 0,
                BlockChance = 0,                   // no shield default
                Discriminator = 0,
                MeleeDefenseRating = 0,
                MeleeDefenseRatingMod = 0,
                RangedDefenseRating = 0,
                RangedDefenseRatingMod = 0,
            };
        }
    }
}
