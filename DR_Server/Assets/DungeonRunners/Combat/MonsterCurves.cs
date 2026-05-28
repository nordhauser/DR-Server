using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Section 10e — retail's `CurveTable` lookups for mob stat caching.
    ///
    /// <para>Mob `Unit::computeAttributes` calls the UnitDesc's getAttackRating /
    /// getDefenseRating / getMaxHealth virtuals, which do:
    /// <code>baseStat = (UnitDesc[+0xD0_or_similar] × CurveTable.GetValue(disc &lt;&lt; 8)) &gt;&gt; 16</code>
    /// where the curve is selected by flag bit 0x10 on `UnitDesc[+0x13C]`:
    ///  - bit clear: PvE units (mobs + player avatars) — use the curves below
    ///  - bit set:   PvP units (arena/posse) — different curves at <c>0x932dc0/0x932dc8</c>
    ///
    /// Note 2026-05-28: bit 0x10 = "PvP context", NOT "player-summoned henchman" as
    /// the prior comment said. Verified live with HW execute BP on getAttackRating —
    /// all PvE units (players AND mobs in dungeon00) take the bit-clear branch, the
    /// Henchman/PvP cache never populates during normal play.
    /// </para>
    ///
    /// <para>Curve "level" input = the unit's discriminator byte shifted left 8 (so disc=2
    /// queries at level 2.0 in Fixed32). Discriminator is the mob's "tier" marker, not
    /// its actual displayed level.</para>
    ///
    /// <para>Verified empirically: dungeon00_level01 pup with rank1 override AR=0.15
    /// gives cached baseAR = (38 × curve(disc=2)) &gt;&gt; 16 = (38 × 102362) &gt;&gt; 16 = 59
    /// ≈ 60 (matches x32dbg capture).</para>
    ///
    /// <para>
    /// ★ B1 RESOLVED (2026-05-28, late session — full struct walk):
    /// Walked the live CurveTable tree from <c>0x932da0</c> → parent <c>0x158E0CB8</c>:
    ///  - Parent vtable <c>0x008A2D98</c>, count=4 children, sorted-entry array at
    ///    <c>+0x6C/+0x70</c>, min/max level clamps at <c>+0x7C/+0x80</c> (=1.0/110.0 Fixed32).
    ///  - The 2 sorted entries ARE the leaf sub-CurveTables at parent <c>+0x18/+0x1C</c>
    ///    (vtable <c>0x008A2E48</c>), each carrying level at <c>+0x68</c> and value at <c>+0x6C</c>.
    ///  - Entry A: <c>+0x68=0x00000100</c> (L=1.0), <c>+0x6C=0x00006400</c> (V=25,600 Fixed32)
    ///  - Entry B: <c>+0x68=0x00006E00</c> (L=110.0), <c>+0x6C=0x00802000</c> (V=8,396,800 Fixed32)
    ///
    /// THESE EXACTLY MATCH THE ENCODED CURVE BELOW. Prior session's memory misread
    /// the parent's <c>+0x6C/+0x70</c> as raw values; they're actually pointers to the
    /// 2 sorted entries. The "60.5 / 5452" numbers in prior memory came from the
    /// shared-pool struct at parent <c>+0x14</c> (vtable <c>0x008A8A48</c>) which is a
    /// different table entirely, not part of MonsterAR.
    ///
    /// Hand-verification with client's exact <c>CurveTableEntry::getValue @ 0x005d4050</c>:
    ///   curve(disc=2) = 25,600 + (8,371,200 × 601 / 65,536) = 102,369
    ///   baseAR(pup, auth=0.15) = (38 × 102,369) >> 16 = 59 ✓ matches Section 10c capture
    ///
    /// MonsterDefenseRating walked the same way (parent at <c>0x932da8</c> → leaves):
    ///   - L1.0, V=8,960  (35 × 256 ✓)
    ///   - L15.0, V=73,472 (287 × 256 ✓)
    ///   - L110.0, V=790,272 (3087 × 256 ✓)
    /// All 3 encoded DR values match the live struct byte-for-byte.
    ///
    /// The Interp helper below now uses the client's exact double-truncation formula
    /// (frac computed and truncated first, then applied to value delta and truncated
    /// again) instead of single-truncation, for byte-for-byte parity at the edges.
    /// </para>
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

        // Linear-interpolate a sorted (key, value) table at the given key, using the
        // client's exact double-truncation formula from CurveTableEntry::getValue
        // (Ghidra @ 0x005d4050):
        //   frac65536 = ((key - lo.level) * 65536) / (hi.level - lo.level)   // truncate #1
        //   delta     = (hi.value - lo.value) * frac65536
        //   result    = lo.value + delta / 65536                              // truncate #2
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
                    long frac65536 = ((long)(keyFixed - k0) * 65536L) / (k1 - k0);
                    long delta = (long)(v1 - v0) * frac65536;
                    return v0 + (int)(delta / 65536L);
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
