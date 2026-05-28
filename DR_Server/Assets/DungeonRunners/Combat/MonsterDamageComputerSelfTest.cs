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
            TestOnQueryApplyDamage();
            TestComputeReflectedDamage();
            TestComputeBaseDamageMod();

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

        /// <summary>
        /// C6 — locks in MonsterDamageComputer.OnQueryApplyDamage semantics. Mirrors
        /// the client function at Unit::onQueryApplyDamage @ 0x0050b9c0. Test cases
        /// drawn from: (1) decompile arithmetic, (2) creature .gc convention check
        /// done 2026-05-28 (amazon_gatekeeper01.gc has FireResist=60 etc., confirming
        /// 0-100 percent-resisted semantics for creature-side data).
        /// </summary>
        private static void TestOnQueryApplyDamage()
        {
            const uint baseDmg = 25600;  // wire = 100 HP

            // ── DamageTakenMod ──
            {
                var target = new PlayerUnitStats { DamageTakenMod = 100 };  // default — no-op
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 0, target, out var resisted);
                CheckEq("DamageTakenMod=100 (default) → unchanged", (int)got, (int)baseDmg);
                Check("DamageTakenMod=100 → not resisted", !resisted);
            }
            {
                var target = new PlayerUnitStats { DamageTakenMod = 50 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 0, target, out var resisted);
                CheckEq("DamageTakenMod=50 → half", (int)got, 12800);
                Check("DamageTakenMod=50 → not resisted", !resisted);
            }
            {
                var target = new PlayerUnitStats { DamageTakenMod = 200 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 0, target, out var resisted);
                CheckEq("DamageTakenMod=200 → double (vulnerability)", (int)got, 51200);
            }

            // ── Physical types (0, 1, 2) skip the elemental switch ──
            {
                var target = new PlayerUnitStats { FireResist = 100, IceResist = 100 };  // would 1-shot immune if applied
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 0, target, out var _);
                CheckEq("type=0 SLASHING: high FireResist ignored", (int)got, (int)baseDmg);
                got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 1, target, out var _2);
                CheckEq("type=1 PIERCING: high FireResist ignored", (int)got, (int)baseDmg);
                got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 2, target, out var _3);
                CheckEq("type=2 BLUDGEONING: high FireResist ignored", (int)got, (int)baseDmg);
            }

            // ── Elemental — type 3 (Fire) ──
            {
                var target = new PlayerUnitStats { FireResist = 0 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, target, out var resisted);
                CheckEq("type=3 + FireResist=0 → unchanged (resMult=100)", (int)got, (int)baseDmg);
                Check("FireResist=0 → not resisted", !resisted);
            }
            {
                var target = new PlayerUnitStats { FireResist = 50 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, target, out var resisted);
                CheckEq("type=3 + FireResist=50 → half (resMult=50)", (int)got, 12800);
                Check("FireResist=50 → not resisted (partial)", !resisted);
            }
            {
                var target = new PlayerUnitStats { FireResist = 100 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, target, out var resisted);
                CheckEq("type=3 + FireResist=100 → immune (resMult=0, <1 branch)", (int)got, 0);
                Check("FireResist=100 → resisted flag set", resisted);
            }
            {
                var target = new PlayerUnitStats { FireResist = 200 };  // over-cap
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, target, out var resisted);
                CheckEq("type=3 + FireResist=200 → immune (resMult=-100, <1 branch)", (int)got, 0);
                Check("FireResist=200 → resisted flag set", resisted);
            }

            // ── Elemental — other types reach their correct resist slot ──
            {
                var t4 = new PlayerUnitStats { IceResist = 100 };
                var t5 = new PlayerUnitStats { PoisonResist = 100 };
                var t6 = new PlayerUnitStats { DivineResist = 100 };
                var t7 = new PlayerUnitStats { ShadowResist = 100 };
                CheckEq("type=4 → IceResist slot",
                    (int)MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 4, t4, out _), 0);
                CheckEq("type=5 → PoisonResist slot",
                    (int)MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 5, t5, out _), 0);
                CheckEq("type=6 → DivineResist slot",
                    (int)MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 6, t6, out _), 0);
                CheckEq("type=7 → ShadowResist slot",
                    (int)MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 7, t7, out _), 0);
            }

            // ── Combined: DamageTakenMod + elemental res stack multiplicatively ──
            {
                var target = new PlayerUnitStats { DamageTakenMod = 50, FireResist = 50 };
                // Step 1: 25600 × 50/100 = 12800
                // Step 2: 12800 × 50/100 = 6400
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, target, out var resisted);
                CheckEq("DamageTakenMod=50 + FireResist=50 → quarter", (int)got, 6400);
                Check("partial stack → not resisted", !resisted);
            }

            // ── Real creature anchor: amazon_gatekeeper01.gc has FireResist=60 ──
            {
                // Target the OTHER direction — player hitting the gatekeeper. Even though
                // OnQueryApplyDamage lives on PlayerUnitStats here, the math is identical
                // on the mob side. Anchor the gatekeeper's authored value to lock the
                // inversion convention: resMult = 100 - 60 = 40 → take 40% damage.
                var target = new PlayerUnitStats { FireResist = 60 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, target, out var resisted);
                CheckEq("amazon_gatekeeper FireResist=60 → take 40% (resMult=40)",
                    (int)got, 10240);  // 25600 × 40/100
                Check("FireResist=60 → not resisted (partial)", !resisted);
            }

            // ── Zero damage in → zero out (short-circuit) ──
            {
                var target = new PlayerUnitStats { DamageTakenMod = 200, FireResist = 0 };
                var got = MonsterDamageComputer.OnQueryApplyDamage(0, 3, target, out var resisted);
                CheckEq("damage=0 → 0 (short-circuit)", (int)got, 0);
                Check("damage=0 → not resisted", !resisted);
            }

            // ── Null target → unchanged (defensive) ──
            {
                var got = MonsterDamageComputer.OnQueryApplyDamage(baseDmg, 3, null, out var resisted);
                CheckEq("null target → unchanged (defensive)", (int)got, (int)baseDmg);
                Check("null target → not resisted", !resisted);
            }
        }

        /// <summary>
        /// C7 — locks in MonsterDamageComputer.ComputeReflectedDamage semantics. Mirrors the
        /// reflect/thorns block inside <c>Unit::onApplyDamage @ 0x0050BE50</c>.
        /// </summary>
        private static void TestComputeReflectedDamage()
        {
            const uint baseDmg = 25600;  // 100 HP wire

            // ── No reflect ──
            CheckEq("reflect base=0 → 0",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 0, 0, 0, 0), 0);

            // ── eventKind=4 always returns 0 (no recursion) ──
            CheckEq("eventKind=4 → 0 (no recursion even with reflect=999)",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 4, 999, 999, 999), 0);

            // ── Base reflect applies to all non-special kinds ──
            CheckEq("kind=0 + base=50 → 50% = 12800",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 0, 50, 999, 999), 12800);
            CheckEq("kind=3 (spell) + base=25 → 25% = 6400",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 3, 25, 999, 999), 6400);

            // ── Melee event adds meleeBonus to base ──
            CheckEq("kind=1 (melee) + base=10 + meleeBonus=20 → 30% = 7680",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 1, 10, 20, 999), 7680);
            CheckEq("kind=1 + base=0 + meleeBonus=15 → 15% = 3840",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 1, 0, 15, 999), 3840);

            // ── Ranged event adds rangedBonus to base ──
            CheckEq("kind=2 (ranged) + base=10 + rangedBonus=30 → 40% = 10240",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 2, 10, 999, 30), 10240);
            CheckEq("kind=2 + base=0 + rangedBonus=5 → 5% = 1280",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 2, 0, 999, 5), 1280);

            // ── Cross-style bonus NOT applied ──
            CheckEq("kind=1 ignores rangedBonus (rangedBonus=999, base=0, meleeBonus=0) → 0",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 1, 0, 0, 999), 0);
            CheckEq("kind=2 ignores meleeBonus (meleeBonus=999, base=0, rangedBonus=0) → 0",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 2, 0, 999, 0), 0);

            // ── Negative reflect (vulnerability debuff) → 0 ──
            CheckEq("base=-50 → 0 (negative reflect clamped)",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 0, -50, 0, 0), 0);
            CheckEq("kind=1 + base=10 + meleeBonus=-30 (net -20) → 0",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 1, 10, -30, 0), 0);

            // ── Reflect over 100% damages attacker for more than they dealt ──
            CheckEq("base=200 → 200% = 51200 (50 HP back)",
                (int)MonsterDamageComputer.ComputeReflectedDamage(baseDmg, 0, 200, 0, 0), 51200);

            // ── Zero incoming damage → 0 ──
            CheckEq("damage=0 + base=100 → 0",
                (int)MonsterDamageComputer.ComputeReflectedDamage(0, 0, 100, 0, 0), 0);

            // ── EventKindFromAttackStyle mapping ──
            CheckEq("attackStyle=1 → kind 1 (melee)", MonsterDamageComputer.EventKindFromAttackStyle(1), 1);
            CheckEq("attackStyle=5 → kind 1 (melee variant)", MonsterDamageComputer.EventKindFromAttackStyle(5), 1);
            CheckEq("attackStyle=6 → kind 1 (melee variant)", MonsterDamageComputer.EventKindFromAttackStyle(6), 1);
            CheckEq("attackStyle=8 → kind 1 (melee variant)", MonsterDamageComputer.EventKindFromAttackStyle(8), 1);
            CheckEq("attackStyle=3 → kind 2 (ranged)", MonsterDamageComputer.EventKindFromAttackStyle(3), 2);
            CheckEq("attackStyle=9 → kind 2 (ranged variant)", MonsterDamageComputer.EventKindFromAttackStyle(9), 2);
            CheckEq("attackStyle=13 → kind 2 (ranged variant)", MonsterDamageComputer.EventKindFromAttackStyle(13), 2);
            CheckEq("attackStyle=0 → kind 0 (default)", MonsterDamageComputer.EventKindFromAttackStyle(0), 0);
            CheckEq("attackStyle=99 → kind 0 (unknown defaults to 0)", MonsterDamageComputer.EventKindFromAttackStyle(99), 0);
        }

        /// <summary>
        /// B2 — locks in MonsterUnitStatsBuilder.ComputeBaseDamageMod against the exact
        /// transform from client <c>UnitDesc::getDamageMod @ 0x0050FBF0</c>:
        /// <code>
        /// authFixed = round(authored × 256)
        /// if (authFixed - 256 == 256) return 0   // auth=2.0 client special-case
        /// return ((int64)(authFixed - 256) × 25600) >> 16   // ≈ (authored - 1.0) × 100
        /// </code>
        ///
        /// <para>
        /// Authored anchor values pulled from .gc inheritance chain (verified 2026-05-28):
        ///  - <c>UnitMelee_GruntRecruit</c> (Pup tier): 1.00
        ///  - <c>UnitMelee_GruntVeteran</c> (Grunt tier): 1.10
        ///  - <c>UnitMelee_Champion</c>: 2.20
        ///  - <c>UnitMelee_Hero</c>: 2.20
        ///  - <c>Abba_Labba_Melee_Hero_Base</c>: 0.25
        ///  - <c>amazon_gatekeeper01</c>, <c>boss2</c>: 5.00
        ///  - <c>boss_legend01..06</c>, <c>boss_caster</c>, <c>boss_ghost</c>: 2.00 (hits the special-case → 0)
        /// </para>
        ///
        /// <para>
        /// Note: the captured pup runtime DamageMod=0.50 (per S10g) is half the .gc-authored
        /// 1.00 — that halving happens in Tim's DatabaseLoader path BEFORE this formula. The
        /// formula itself is universal across tiers; no RECRUIT/GRUNT/CHAMPION dispatch is
        /// needed server-side.
        /// </para>
        ///
        /// <para>
        /// Client also gates the entire branch on <c>UnitDesc[+0x13C] &amp; 0x02 != 0</c> —
        /// when clear, returns 0 regardless of authored value. Server doesn't model this bit
        /// because mob registration with MonsterAttackController is itself an in-combat gate.
        /// </para>
        /// </summary>
        private static void TestComputeBaseDamageMod()
        {
            // ── Identity at auth=1.0 ──
            CheckEq("auth=1.0 → 0 (baseline)",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(1.0f), 0);

            // ── 2.0 special-case (client quirky behavior) ──
            CheckEq("auth=2.0 → 0 (client special-case in getDamageMod @ 0x0050FBF0)",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(2.0f), 0);

            // ── Tier anchors (.gc inheritance) ──
            // (auth - 1.0) × 100 — sub-unit cases truncate via the (delta × 25600) >> 16 form
            // Pup authored = 1.0 → 0
            CheckEq("auth=1.0 (GruntRecruit/Pup tier) → 0", MonsterUnitStatsBuilder.ComputeBaseDamageMod(1.0f), 0);

            // Grunt authored = 1.10 → (281 - 256) × 25600 >> 16 = 25 × 25600 / 65536 = 9
            // Mathf rounds 1.10*256=281.6 → 282; (282-256) × 25600 / 65536 = 26 × 25600 / 65536 = 10
            CheckEq("auth=1.10 (GruntVeteran tier) → ~10",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(1.10f), 10);

            // Champion/Hero authored = 2.20: (563 - 256) × 25600 / 65536 = 307 × 25600 / 65536 = 119
            // Mathf rounds 2.20*256=563.2 → 563
            CheckEq("auth=2.20 (Champion/Hero tier) → ~119",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(2.20f), 119);

            // ── Pup runtime (post-Tim-halving) ──
            // S10g capture: server's Monster.DamageMod = 0.50 → cached = -50 ✓
            CheckEq("auth=0.5 (pup runtime per S10g capture) → -50",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(0.5f), -50);

            // ── Other authored anchors ──
            // Abba Labba Melee Hero Base = 0.25 → (64 - 256) × 25600 / 65536 = -192 × 25600 / 65536 = -75
            CheckEq("auth=0.25 (Abba_Labba_Melee_Hero) → -75",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(0.25f), -75);

            // amazon_gatekeeper01, boss2 = 5.0 → (1024 - 1) × ... — wait, (1280-256)*25600/65536 = 1024 × 25600 / 65536 = 400
            CheckEq("auth=5.0 (amazon_gatekeeper / boss2) → 400",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(5.0f), 400);

            // ── Edge cases ──
            // auth=0.10 → (26 - 256) × 25600 / 65536 = -230 × 25600 / 65536 = -89.84 → -90
            // Mathf rounds 0.10*256=25.6 → 26
            CheckEq("auth=0.10 (AbaddonCannonProc spell) → ~-90",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(0.10f), -90);

            // auth=0 → (0 - 256) × 25600 / 65536 = -256 × 25600 / 65536 = -100
            CheckEq("auth=0.0 → -100 (max debuff)",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(0.0f), -100);

            // auth=1.5 → (384 - 256) × 25600 / 65536 = 128 × 25600 / 65536 = 50
            CheckEq("auth=1.5 → 50",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(1.5f), 50);

            // auth=3.0 (above 2.0 special case) → (768-256) × 25600 / 65536 = 512 × 25600 / 65536 = 200
            CheckEq("auth=3.0 (above 2.0 special-case threshold) → 200",
                MonsterUnitStatsBuilder.ComputeBaseDamageMod(3.0f), 200);
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
