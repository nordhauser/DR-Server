using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Section 10e — retail's `CurveTable` lookups for mob stat caching, extracted
    /// from `Database/gc/Tables.gc` and verified via x32dbg runtime memory dump.
    ///
    /// <para>Mob `Unit::computeAttributes` calls the UnitDesc's getAttackRating /
    /// getDefenseRating / getMaxHealth virtuals, which do:
    /// <code>baseStat = (UnitDesc[+0xD0_or_similar] × CurveTable.GetValue(disc &lt;&lt; 8)) &gt;&gt; 16</code>
    /// where the curve is selected by flag bit 0x10 on `UnitDesc[+0x13C]`:
    ///  - bit clear: <c>Tables.Monster*</c> (regular mob curves, what we use here)
    ///  - bit set:   <c>Tables.Henchman*</c> (player-summoned units)
    /// </para>
    ///
    /// <para>Curve "level" input = the unit's discriminator byte shifted left 8 (so disc=2
    /// queries at level 2.0 in Fixed32). Discriminator is the mob's "tier" marker, not
    /// its actual displayed level.</para>
    ///
    /// <para>Verified empirically: dungeon00_level01 pup with rank1 override AR=0.15
    /// gives cached baseAR = (38 × curve(disc=2)) &gt;&gt; 16 = (38 × 102362) &gt;&gt; 16 = 59
    /// ≈ 60 (matches x32dbg capture).</para>
    /// </summary>
    public static class MonsterCurves
    {
        // Each curve is a sorted array of (level, value) pairs in Fixed32 (×256).
        // Linear interpolation between adjacent entries, clamped at min/max bounds.

        // Tables.gc MonsterAttackRating: L1→100, L110→32800
        private static readonly (int LevelFixed, int ValueFixed)[] MonsterAttackRatingCurve =
        {
            (1   * 256,   100 * 256),  // L1 → 25600 Fixed32
            (110 * 256, 32800 * 256),  // L110 → 8396800 Fixed32
        };

        // Tables.gc MonsterDefenseRating: L1→35, L15→287, L110→3087
        private static readonly (int LevelFixed, int ValueFixed)[] MonsterDefenseRatingCurve =
        {
            (1   * 256,   35 * 256),
            (15  * 256,  287 * 256),
            (110 * 256, 3087 * 256),
        };

        // Tables.gc MonsterDamage: L1→12.7, L25→86.625, L110→372.12
        // Encoded as Fixed32 (×256), so 12.7 → 3251, 86.625 → 22176, 372.12 → 95263
        private static readonly (int LevelFixed, int ValueFixed)[] MonsterDamageCurve =
        {
            (1   * 256,   3251),
            (25  * 256,  22176),
            (110 * 256,  95263),
        };

        // Tables.gc MonsterHealth: L1→60.5, L100→5452
        private static readonly (int LevelFixed, int ValueFixed)[] MonsterHealthCurve =
        {
            (1   * 256,    60 * 256 + 128),    // 60.5 Fixed32
            (100 * 256,  5452 * 256),
        };

        // Linear-interpolate a sorted (key, value) table at the given key.
        // Both key and value are Fixed32 (× 256). Returns Fixed32.
        private static int Interp(int keyFixed, (int LevelFixed, int ValueFixed)[] curve)
        {
            if (curve.Length == 0) return 0;
            if (keyFixed <= curve[0].LevelFixed) return curve[0].ValueFixed;
            if (keyFixed >= curve[curve.Length - 1].LevelFixed) return curve[curve.Length - 1].ValueFixed;
            for (int i = 1; i < curve.Length; i++)
            {
                if (keyFixed <= curve[i].LevelFixed)
                {
                    int k0 = curve[i - 1].LevelFixed;
                    int v0 = curve[i - 1].ValueFixed;
                    int k1 = curve[i].LevelFixed;
                    int v1 = curve[i].ValueFixed;
                    long frac = (long)(keyFixed - k0) * (long)(v1 - v0) / (k1 - k0);
                    return v0 + (int)frac;
                }
            }
            return curve[curve.Length - 1].ValueFixed;
        }

        /// <summary>Cached baseAR for a mob: (auth × MonsterAttackRating(disc<<8)) >> 16.</summary>
        public static int ComputeBaseAR(float authoredAttackRating, byte discriminator)
        {
            int authFixed = Mathf.RoundToInt(authoredAttackRating * 256f);
            int curveVal = Interp(discriminator << 8, MonsterAttackRatingCurve);
            return (int)(((long)authFixed * curveVal) >> 16);
        }

        public static int ComputeBaseDR(float authoredDefenseRating, byte discriminator)
        {
            int authFixed = Mathf.RoundToInt(authoredDefenseRating * 256f);
            int curveVal = Interp(discriminator << 8, MonsterDefenseRatingCurve);
            return (int)(((long)authFixed * curveVal) >> 16);
        }

        // MaxHealth uses level (not disc) in the formula, plus additional multipliers.
        // For now we don't drive MaxHP from the server-side sim; this is here for future use.
        public static int ComputeBaseMaxHP(float authoredMaxHealth, int level)
        {
            int authFixed = Mathf.RoundToInt(authoredMaxHealth * 256f);
            int curveVal = Interp(level << 8, MonsterHealthCurve);
            return (int)(((long)authFixed * curveVal) >> 16);
        }
    }
}
