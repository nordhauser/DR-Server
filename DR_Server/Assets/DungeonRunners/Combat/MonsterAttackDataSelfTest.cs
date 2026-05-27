using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Self-test for <see cref="MonsterAttackData"/>. Confirms global knobs cached and
    /// per-mob profile loads with inherited weapon fields. Section 10 task S10.2 deliverable.
    /// </summary>
    public static class MonsterAttackDataSelfTest
    {
        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();

            Debug.LogError("[MONSTER-ATTACK-SELFTEST] ═══════════════════════════════════════════════════");

            TestGlobalKnobsLoaded();
            TestAbbaLabbaMeleeGruntProfile();
            TestProfileInheritanceFromMeleeUnitWeapon();

            Debug.LogError("[MONSTER-ATTACK-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[MONSTER-ATTACK-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[MONSTER-ATTACK-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[MONSTER-ATTACK-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[MONSTER-ATTACK-SELFTEST] ALL TESTS PASS — MonsterAttackData ready for S10.3");
            }
        }

        private static void TestGlobalKnobsLoaded()
        {
            var d = MonsterAttackData.Instance;
            if (!d.IsInitialized)
            {
                Debug.LogError("[MONSTER-ATTACK-SELFTEST] [SKIP] not initialized; call after GCDatabase.Load");
                return;
            }
            CheckEq("MonsterAttackSpeed = 100", d.MonsterAttackSpeed, 100);
            CheckEq("MonsterCriticalChance = 6", d.MonsterCriticalChance, 6);
            CheckEq("MonsterMissChance = 100", d.MonsterMissChance, 100);
            CheckEq("MonsterDamageMod = 0", d.MonsterDamageMod, 0);
            CheckEq("HeroCriticalChance = 3", d.HeroCriticalChance, 3);
            CheckEq("WeaponDamagePerLevel = 10", d.WeaponDamagePerLevel, 10);
            CheckEq("SkillDamagePerLevel = 15", d.SkillDamagePerLevel, 15);
        }

        private static void TestAbbaLabbaMeleeGruntProfile()
        {
            const string GcType = "Abba_Labba_Melee_Grunt_Base";
            var d = MonsterAttackData.Instance;

            if (!d.TryGetProfile(GcType, out var p))
            {
                Debug.LogError($"[MONSTER-ATTACK-SELFTEST] [FAIL] TryGetProfile returned false for {GcType}");
                _failures.Add($"profile lookup failed for {GcType}");
                _testsRun++;
                return;
            }

            Debug.LogError($"[MONSTER-ATTACK-SELFTEST] loaded: {p}");

            CheckEqF("AttackSpeed = 0.8", p.AttackSpeed, 0.8f);
            CheckEqF("AttackRating = 0.25", p.AttackRating, 0.25f);
            CheckEqF("DefenseRating = 0.25", p.DefenseRating, 0.25f);
            CheckEqF("DamageMod = 0.25", p.DamageMod, 0.25f);
            CheckEqF("MaxHealth = 0.2", p.MaxHealth, 0.2f);
            CheckEqF("Speed = 55", p.Speed, 55f);
            CheckEq("FactionID = 2", p.FactionID, 2);

            Check("HasPrimaryWeapon", p.HasPrimaryWeapon);
            CheckEq("WeaponID = 10", p.WeaponID, 10);
            CheckEqF("WeaponDamage = 1", p.WeaponDamage, 1f);
            CheckEq("WeaponDamageType = SLASHING", p.WeaponDamageType, "SLASHING");

            CheckEq("AttackStyle = DEFENSIVE", p.AttackStyle, "DEFENSIVE");
            CheckEq("IdleAction = FOLLOW", p.IdleAction, "FOLLOW");
            CheckEq("AgroRange = 150", p.AgroRange, 150);
            CheckEq("ShoutRange = 300", p.ShoutRange, 300);
            CheckEq("LeashRange = 200", p.LeashRange, 200);
        }

        private static void TestProfileInheritanceFromMeleeUnitWeapon()
        {
            // base.MeleeUnitWeapon (in MeleeUnitWeapon.gc) defines:
            //   Damage = 1.0, DamageVolatility = 0.5, Range = 5, CoolDown = 1.75
            // The mob's PrimaryWeapon extends it but only overrides Damage = 1 + DamageType.
            // So Range/CoolDown should come from the parent.
            const string GcType = "Abba_Labba_Melee_Grunt_Base";
            var d = MonsterAttackData.Instance;
            if (!d.TryGetProfile(GcType, out var p)) return;

            CheckEqF("WeaponDamageVolatility (inherited 0.5)", p.WeaponDamageVolatility, 0.5f);
            CheckEqF("WeaponRange (inherited 5)", p.WeaponRange, 5f);
            CheckEqF("WeaponCoolDown (inherited 1.75)", p.WeaponCoolDown, 1.75f);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[MONSTER-ATTACK-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[MONSTER-ATTACK-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, int actual, int expected)
        {
            Check(name, actual == expected, $"got {actual}, expected {expected}");
        }

        private static void CheckEq(string name, string actual, string expected)
        {
            Check(name, actual == expected, $"got '{actual}', expected '{expected}'");
        }

        private static void CheckEqF(string name, float actual, float expected)
        {
            const float Tol = 0.001f;
            Check(name, Mathf.Abs(actual - expected) < Tol, $"got {actual:F3}, expected {expected:F3}");
        }
    }
}
