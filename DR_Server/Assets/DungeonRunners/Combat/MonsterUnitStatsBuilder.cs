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
    /// <item>baseAR = MonsterCurves.ComputeBaseAR via PvE CurveTable + linear interp
    ///   (curve anchors in MonsterCurves.cs; see B1 follow-up note there)</item>
    /// <item>baseDR = MonsterCurves.ComputeBaseDR (same shape as AR)</item>
    /// <item>baseDamageMod = ComputeBaseDamageMod via exact UnitDesc::getDamageMod
    ///   transform (see comment at the helper body)</item>
    /// <item>BaseCriticalChance = ComputeBaseCriticalChance from .gc CriticalChance
    ///   × RPGSettings.MonsterCriticalChance &gt;&gt; 16 (B3 — yields 0 for current mobs)</item>
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

            // S10e 2026-05-27: discriminator IS the curve lookup key (asm 0x0050FA85: MOVZX ECX,byte ptr
            // [ESP+0x14]; SHL ECX,0x8). Stat-parity 2026-05-30: that key is Unit[+0x314] = the byte the spawn
            // sends = monster.Level (CombatPackets.WriteUnitReadInit writes `level` right after unitFlags).
            // Live x64dbg read confirmed [+0x314]=1 for a level-1 mob. The old hardcoded 2 queried the curve at
            // level 2.0 for EVERY mob → overstated a level-1 mob's AR as 99 vs the correct 25 (~4×) → major
            // over-hit. Use the same level the spawn writes (clamped ≥1 like the spawn's `if(lvl==0)lvl=1`).
            byte discriminator = (byte)(level <= 0 ? 1 : level);

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
                // B3 (2026-05-28 plan vivid-marinating-pixel): compute from authored
                // .gc CritChance via the client's formula instead of hardcoding 0.
                // For current mobs (CritChance ≤ ~1.25) the result is still 0 because
                // (1.25 × 256 × 6) >> 16 = 0, matching the captured pup cache value.
                // For future high-CritChance mobs the formula yields a positive value.
                BaseCriticalChance = ComputeBaseCriticalChance(profile.CritChance),
                CritMultiplier = 200,  // client default 2.0× — Unit[+0x118], Ghidra @0x005099CA (was 150 placeholder)
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
        public static int ComputeBaseDamageMod(float authoredDamageMod)
        {
            int authoredFixed = Mathf.RoundToInt(authoredDamageMod * 256f);
            if (authoredFixed - 256 == 256) return 0;  // 2.0 special-case in client
            long delta = (long)(authoredFixed - 256);
            return (int)((delta * 25600L) >> 16);
        }

        /// <summary>
        /// Client formula: <c>cachedCritChance = (authoredCritChance_Fixed32 × RPGSettings.MonsterCriticalChance) &gt;&gt; 16</c>.
        /// <para>
        /// Per AUDIT_COMBAT/10 and the pup x32dbg capture, the GlobalKnobs
        /// <c>MonsterCriticalChance</c> scalar (=6 in retail) shrinks small authored
        /// CritChance values (~1.25) to 0 after the &gt;&gt;16 shift. Yields a positive
        /// number only when authored ≳ 43 (where 43 × 256 × 6 ≈ 65536).
        /// </para>
        /// </summary>
        private static int ComputeBaseCriticalChance(float authoredCritChance)
        {
            if (authoredCritChance <= 0f) return 0;
            long authoredFixed = (long)Mathf.RoundToInt(authoredCritChance * 256f);
            long globalScalar = (long)MonsterAttackData.Instance.MonsterCriticalChance;
            return (int)((authoredFixed * globalScalar) >> 16);
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
            // Stat-parity fix 2026-05-30: use the player's REAL defense. The server already aggregates
            // equipped-armor DR into PlayerState.ArmorDefenseRating (UnityGameServer.CalculateEquipmentBonuses,
            // built with the reverse-engineered ItemDefenseRatingPerLevel=8.26 client knob, so it's already in
            // client DR units). The avatar class .gc carries no base DefenseRating, so equipment dominates the
            // client's hit-time DR. The old 10*level stub understated DR -> server over-hit -> Avatar HP desync.
            var ps = player?.PlayerState;
            // Avatar DR engine — x64dbg-pinned 2026-05-30 (Hero::endComputeAttributes @0x4F7950 + getDefenseRating @0x50FB30):
            //   DR = baseCurveDR + derivedDR + equipment(+allocated)
            //   baseCurveDR = ComputeBaseDR(authoredClassDR=1.0, disc=level)  [players REUSE the MonsterDR curve, 0x932DA8]
            //   derivedDR   = (Strength × RPGSettings[+0x154]=3584=14.0 × DefenseRatingPerStrengthMod) >> 16 ; Fighter mod=1.0 -> Strength×14
            // Live-confirmed: Strength 11 -> derived 154 (=EDX at 0x4F8248), + base ~31 = client DR 185.
            // TODO: per-class authoredClassDR + DefenseRatingPerStrengthMod (FighterBase.gc / live UnitDesc[+0xD8]);
            //       base term shows a ~4-unit gap vs live (server 35 vs client 31) — verify/refine in the move-test.
            int strength = ps != null ? ps.Strength : 10;
            int baseCurveDR = MonsterCurves.ComputeBaseDR(1.0f, (byte)Mathf.Clamp(level, 1, 110));
            int derivedDR = (int)(((long)strength * 3584) >> 8);   // = Strength × 14 (RPGSettings[+0x154] × DefRatPerStrMod 1.0)
            int armorDR = ps != null ? ps.ArmorDefenseRating : 0;
            int dr = baseCurveDR + derivedDR + armorDR;
            return new PlayerUnitStats
            {
                BaseDefenseRating = dr,
                BaseDefenseRatingMod = 0,          // TODO Phase 2: equipment DR% from EquipmentStats + allocated-Strength DR
                BlockChance = 0,                   // TODO Phase 2: shield block from gear (0 = correct for unshielded)
                Discriminator = (ushort)Mathf.Clamp(level, 0, 255),  // player disc = level (live x64dbg: [+0x314]=1 at L1)
                MeleeDefenseRating = 0,
                MeleeDefenseRatingMod = 0,
                RangedDefenseRating = 0,
                RangedDefenseRatingMod = 0,
            };
        }
    }
}
