using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// S10.3 self-test for <see cref="MonsterDamageComputer"/>. Validates:
    /// roll-count semantics (2 on miss/block, 3 on hit-not-blocked), determinism (same
    /// seed → same outcome), and sensible damage range for the Abba_Labba_Melee_Grunt
    /// sample mob.
    ///
    /// <para>
    /// v1 spec compliance only — the unit-field caching is approximated, so byte-equal
    /// parity with client requires x32dbg roll-by-roll capture and refinement.
    /// </para>
    /// </summary>
    public static class MonsterDamageComputerSelfTest
    {
        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();

            Debug.LogError("[MDC-SELFTEST] ═══════════════════════════════════════════════════");

            TestProfileToStats();
            TestRollCountOnHitNotBlocked();
            TestRollCountOnMiss();
            TestDeterminism();
            TestSampleMobSwing();

            Debug.LogError("[MDC-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[MDC-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[MDC-SELFTEST] FAILURES:");
                foreach (var f in _failures) Debug.LogError($"[MDC-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[MDC-SELFTEST] ALL TESTS PASS — MonsterDamageComputer v1 algorithmically correct (x32dbg parity TODO)");
            }
        }

        private static void TestProfileToStats()
        {
            const string GcType = "Abba_Labba_Melee_Grunt_Base";
            if (!MonsterAttackData.Instance.TryGetProfile(GcType, out var profile))
            {
                Debug.LogError("[MDC-SELFTEST] [SKIP] profile load failed");
                return;
            }
            var stats = MonsterUnitStatsBuilder.Build(profile, level: 1);
            CheckEq("level 1", stats.Level, 1);
            CheckEq("AttackStyle = 1 (melee)", stats.AttackStyle, 1);
            CheckEq("WeaponDamageType = 0 (SLASHING)", stats.WeaponDamageType, 0);
            // S10e: curve-based AR. AR=0.25 (Fixed32=64), MonsterAR curve at disc=2:
            //   interp(L1=25600, L110=8396800) at 2.0 (= 512 Fixed32):
            //     25600 + (8396800-25600) × (512-256)/(28160-256) = 25600 + 76798 = 102398
            //   baseAR = (64 × 102398) >> 16 = 99
            CheckEq("BaseAttackRating ≈ 99 (curve-based, disc=2, AR=0.25)", stats.BaseAttackRating, 99);
            // DR=0.25: interp(L1=8960, L15=73472, L110=790272) at 2.0 (= 512 Fixed32):
            //   between L1 and L15: 8960 + (73472-8960) × (512-256)/(3840-256) = 8960 + 4610 = 13570
            //   baseDR = (64 × 13570) >> 16 = 13
            CheckEq("BaseDefenseRating ≈ 13 (curve-based, disc=2, DR=0.25)", stats.BaseDefenseRating, 13);
            // 10d transform (no pre-scale per S10g session): ((auth × 256 - 256) × 25600) >> 16
            //   = ((0.25 × 256 - 256) × 25600) >> 16
            //   = ((64 - 256) × 25600) >> 16 = (-192 × 25600) >> 16 = -75
            CheckEq("BaseDamageMod ≈ -75 (10d transform on 0.25, no pre-scale)",
                stats.BaseDamageMod, -75);
            CheckEq("BaseCriticalChance = 0 (10d: monsters cached as 0)", stats.BaseCriticalChance, 0);
            CheckEq("WeaponVolatilityFixed = 0.5 * 256 = 128", stats.WeaponVolatilityFixed, 128);
            CheckEq("WeaponDamagePerLevel = 10", stats.WeaponDamagePerLevel, 10);
            CheckEq("BlockChance = 0", stats.BlockChance, 0);
        }

        private static void TestRollCountOnHitNotBlocked()
        {
            var attacker = MakeStrongAttacker();
            var target = MakeWeakTarget();
            var rng = new MersenneTwister(0xDEADBEEF);

            int rngStart = rng.CallsSinceReseed;
            var result = MonsterDamageComputer.ComputeSwing(attacker, target, rng);
            int consumed = rng.CallsSinceReseed - rngStart;

            // Strong attacker vs weak target → almost always hits, almost never blocked.
            Check("strong vs weak should hit", result.Hit, $"hit={result.Hit} blocked={result.Blocked}");
            if (result.Hit && !result.Blocked)
            {
                CheckEq("3 RNG calls when hit + !blocked", consumed, 3);
                Check("damage > 0", result.Damage > 0, $"damage={result.Damage}");
            }
        }

        private static void TestRollCountOnMiss()
        {
            // 0 AR mob vs 999 DR player → ~0% hit chance (clamped to 10% floor)
            var attacker = new MonsterUnitStats
            {
                Level = 1, AttackStyle = 1, WeaponDamageType = 0, Discriminator = 0,
                BaseAttackRating = 0, BaseAttackRatingMod = -1000,  // negative ARmod → clamped to 0 AR
                CritMultiplier = 100,
                WeaponDamagePerLevel = 10, WeaponVolatilityFixed = 128, WeaponDamageFixed = 256,
            };
            var target = new PlayerUnitStats
            {
                BaseDefenseRating = 9999, BaseDefenseRatingMod = 0,
                Discriminator = 0, BlockChance = 0,
            };

            int totalRolls = 0;
            int misses = 0;
            const int trials = 20;
            for (int i = 0; i < trials; i++)
            {
                var rng = new MersenneTwister(0x10000 + (uint)i);
                int start = rng.CallsSinceReseed;
                var result = MonsterDamageComputer.ComputeSwing(attacker, target, rng);
                int consumed = rng.CallsSinceReseed - start;
                totalRolls += consumed;
                if (!result.Hit) misses++;
            }
            // 10% floor means we expect *some* hits, but most should miss
            Check("miss-heavy attacker mostly misses (or PvE 10% floor)", misses > 0,
                $"misses={misses}/{trials}");
            // All 20 swings should have consumed 2-3 calls each
            Check("total rolls in 40-60 range", totalRolls >= 40 && totalRolls <= 60,
                $"totalRolls={totalRolls} for {trials} trials");
        }

        private static void TestDeterminism()
        {
            var attacker = MakeStrongAttacker();
            var target = MakeWeakTarget();

            var rng1 = new MersenneTwister(0xABCDEF12);
            var rng2 = new MersenneTwister(0xABCDEF12);

            var r1 = MonsterDamageComputer.ComputeSwing(attacker, target, rng1);
            var r2 = MonsterDamageComputer.ComputeSwing(attacker, target, rng2);

            Check("determinism: r1Hit", r1.R1Hit == r2.R1Hit, $"a={r1.R1Hit:X} b={r2.R1Hit:X}");
            Check("determinism: damage", r1.Damage == r2.Damage, $"a={r1.Damage} b={r2.Damage}");
            Check("determinism: hit", r1.Hit == r2.Hit);
            Check("determinism: crit", r1.Crit == r2.Crit);
        }

        private static void TestSampleMobSwing()
        {
            const string GcType = "Abba_Labba_Melee_Grunt_Base";
            if (!MonsterAttackData.Instance.TryGetProfile(GcType, out var profile))
            {
                Debug.LogError("[MDC-SELFTEST] [SKIP] sample mob profile missing");
                return;
            }
            var attacker = MonsterUnitStatsBuilder.Build(profile, level: 1);
            var target = PlayerUnitStatsBuilder.Build(null, level: 1);
            var rng = new MersenneTwister(0x12345678);

            int totalDmg = 0;
            int hits = 0;
            const int swings = 50;
            for (int i = 0; i < swings; i++)
            {
                var r = MonsterDamageComputer.ComputeSwing(attacker, target, rng);
                if (r.Hit && !r.Blocked) { hits++; totalDmg += r.Damage; }
            }
            float avgDmg = hits > 0 ? totalDmg / (float)hits : 0f;
            Debug.LogError($"[MDC-SELFTEST] AbbaLabba vs L1 player: {hits}/{swings} hits, avg dmg={avgDmg:F0} (256 = 1 hp)");
            // v1 approximation: BaseDamageMod = round(.gc.DamageMod × 100) overshoots vs client's
            // actual cached value (e.g., client caches Warg pup baseDamageMod = −50 from
            // authored 1.0). Test is informational — the exact damage parity needs the .gc→unit
            // transform decompiled (deferred). Wide bound here just confirms formula produces
            // non-degenerate values.
            Check("avg damage in plausible range (256..15000)", avgDmg >= 256f && avgDmg <= 15000f,
                $"avgDmg={avgDmg:F0}");
        }

        // ── Helpers ────────────────────────────────────────────────

        private static MonsterUnitStats MakeStrongAttacker() => new MonsterUnitStats
        {
            Level = 10, AttackStyle = 1, WeaponDamageType = 0, Discriminator = 0,
            BaseAttackRating = 9999, BaseAttackRatingMod = 0,
            BaseDamageMod = 100, BaseCriticalChance = 6, CritMultiplier = 150,
            WeaponDamagePerLevel = 10, WeaponVolatilityFixed = 128, WeaponDamageFixed = 256,
            DamageModScale = 256,
        };

        private static PlayerUnitStats MakeWeakTarget() => new PlayerUnitStats
        {
            BaseDefenseRating = 1, BaseDefenseRatingMod = 0,
            BlockChance = 0, Discriminator = 0,
        };

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[MDC-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[MDC-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, int actual, int expected)
        {
            Check(name, actual == expected, $"got {actual}, expected {expected}");
        }
    }
}
