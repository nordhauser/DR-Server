using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Self-test for Fixed32 + Fixed32Math. Run via RunAll() at boot or from a menu item.
    /// Pass criterion: every case prints [PASS]. A single [FAIL] aborts Phase 1.
    ///
    /// Phase 1 (Option 1-full) deliverable. Reference values are either mathematical
    /// (known correct from the algorithm) or eventually captured from x32dbg single-stepping
    /// of ComputeFacingVectors / InterpolateHeading in the client.
    /// </summary>
    public static class Fixed32SelfTest
    {
        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();
            Debug.LogError("[FIXED32-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError("[FIXED32-SELFTEST] Starting Fixed32 + Fixed32Math validation");

            TestBasicArithmetic();
            TestMultiplyReferenceVectors();
            TestDivide();
            TestSignAndAbs();
            TestInterpolateHeadingBasic();
            TestInterpolateHeadingWrapAround();
            TestInterpolateHeadingTargetReached();
            TestNormalizeHeading();
            TestUnitVectorFromHeading();

            Debug.LogError($"[FIXED32-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[FIXED32-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError($"[FIXED32-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[FIXED32-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[FIXED32-SELFTEST] ALL TESTS PASS — Fixed32 ready for Phase 2 use");
            }
        }

        // ─── Test helpers ──────────────────────────────────────────────────

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[FIXED32-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[FIXED32-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, Fixed32 actual, Fixed32 expected)
        {
            Check(name, actual == expected, $"got raw=0x{actual.RawValue:X8} ({actual}), expected raw=0x{expected.RawValue:X8} ({expected})");
        }

        private static void CheckEq(string name, int actualRaw, int expectedRaw)
        {
            Check(name, actualRaw == expectedRaw, $"got 0x{actualRaw:X8} ({actualRaw}), expected 0x{expectedRaw:X8} ({expectedRaw})");
        }

        // ─── Basic arithmetic ──────────────────────────────────────────────

        private static void TestBasicArithmetic()
        {
            // 1.0 + 1.0 = 2.0
            CheckEq("Add: 1+1=2", Fixed32.One + Fixed32.One, new Fixed32(512));
            // 5.0 - 3.0 = 2.0
            CheckEq("Sub: 5-3=2", Fixed32.FromInt(5) - Fixed32.FromInt(3), Fixed32.FromInt(2));
            // -2.5 (raw -640)
            CheckEq("Negate: -(2.5)=-2.5", -Fixed32.FromFloat(2.5f), new Fixed32(-640));
            // FromFloat round-trip
            CheckEq("FromFloat(1.5)=raw 384", Fixed32.FromFloat(1.5f).RawValue, 384);
            CheckEq("FromFloat(-1.5)=raw -384", Fixed32.FromFloat(-1.5f).RawValue, -384);
            // FromInt
            CheckEq("FromInt(10) raw=2560", Fixed32.FromInt(10).RawValue, 2560);
            // ToInt truncation
            CheckEq("ToInt(1.9)=1", Fixed32.FromFloat(1.9f).ToInt(), 1);
            CheckEq("ToInt(-1.5)=-2 (arith shift)", Fixed32.FromFloat(-1.5f).ToInt(), -2);
        }

        // ─── Multiply ──────────────────────────────────────────────────────

        private static void TestMultiplyReferenceVectors()
        {
            // 2.0 * 3.0 = 6.0
            CheckEq("Mul: 2*3=6", Fixed32.FromInt(2) * Fixed32.FromInt(3), Fixed32.FromInt(6));

            // 0.5 * 0.5 = 0.25 (raw 128 * 128 = 16384, >> 8 = 64)
            CheckEq("Mul: 0.5*0.5=0.25", Fixed32.FromFloat(0.5f) * Fixed32.FromFloat(0.5f), new Fixed32(64));

            // 1.5 * 2.5 = 3.75 (raw 384 * 640 = 245760, >> 8 = 960)
            CheckEq("Mul: 1.5*2.5=3.75", Fixed32.FromFloat(1.5f) * Fixed32.FromFloat(2.5f), new Fixed32(960));

            // Negative: -1 * 1 = -1
            CheckEq("Mul: -1*1=-1", Fixed32.MinusOne * Fixed32.One, Fixed32.MinusOne);

            // -2 * -3 = 6
            CheckEq("Mul: -2*-3=6", -Fixed32.FromInt(2) * -Fixed32.FromInt(3), Fixed32.FromInt(6));

            // Large value: 1000 * 1.5 = 1500 (raw 256000 * 384 = 98304000, >> 8 = 384000)
            CheckEq("Mul: 1000*1.5=1500", Fixed32.FromInt(1000) * Fixed32.FromFloat(1.5f), Fixed32.FromInt(1500));

            // Scalar multiply (no shift)
            CheckEq("Scalar Mul: 1.5 * 4 = 6.0", Fixed32.FromFloat(1.5f) * 4, Fixed32.FromInt(6));

            // Critical reference: ComputeFacingVectors-style mul.
            // facingX = 0x80 (0.5 raw), speed = 0x500 (5.0 raw).
            // (long)0x80 * 0x500 = 0x40000. >> 8 = 0x400 = 1024 raw = 4.0
            CheckEq("Mul ref ComputeFacingVectors: 0.5 * 5.0 = 2.5",
                new Fixed32(0x80) * new Fixed32(0x500), new Fixed32(0x280));
        }

        // ─── Divide ────────────────────────────────────────────────────────

        private static void TestDivide()
        {
            // 6 / 2 = 3
            CheckEq("Div: 6/2=3", Fixed32.FromInt(6) / Fixed32.FromInt(2), Fixed32.FromInt(3));

            // 1 / 2 = 0.5
            CheckEq("Div: 1/2=0.5", Fixed32.One / Fixed32.FromInt(2), Fixed32.FromFloat(0.5f));

            // 7 / 4 = 1.75
            CheckEq("Div: 7/4=1.75", Fixed32.FromInt(7) / Fixed32.FromInt(4), Fixed32.FromFloat(1.75f));

            // Negative: -10 / 2 = -5
            CheckEq("Div: -10/2=-5", -Fixed32.FromInt(10) / Fixed32.FromInt(2), -Fixed32.FromInt(5));

            // Multiply/divide round trip
            Fixed32 a = Fixed32.FromInt(7);
            Fixed32 b = Fixed32.FromInt(3);
            Fixed32 product = a * b;
            CheckEq("MulDiv round-trip: (7*3)/3 = 7", product / b, a);
        }

        // ─── Sign, abs, min, max, clamp ────────────────────────────────────

        private static void TestSignAndAbs()
        {
            CheckEq("Abs(-5)=5", Fixed32.Abs(-Fixed32.FromInt(5)), Fixed32.FromInt(5));
            CheckEq("Abs(5)=5", Fixed32.Abs(Fixed32.FromInt(5)), Fixed32.FromInt(5));
            Check("Sign(-5)=-1", Fixed32.Sign(-Fixed32.FromInt(5)) == -1);
            Check("Sign(0)=0", Fixed32.Sign(Fixed32.Zero) == 0);
            Check("Sign(5)=1", Fixed32.Sign(Fixed32.FromInt(5)) == 1);
            CheckEq("Min(3,5)=3", Fixed32.Min(Fixed32.FromInt(3), Fixed32.FromInt(5)), Fixed32.FromInt(3));
            CheckEq("Max(3,5)=5", Fixed32.Max(Fixed32.FromInt(3), Fixed32.FromInt(5)), Fixed32.FromInt(5));
            CheckEq("Clamp(7,0,5)=5", Fixed32.Clamp(Fixed32.FromInt(7), Fixed32.Zero, Fixed32.FromInt(5)), Fixed32.FromInt(5));
            CheckEq("Clamp(-2,0,5)=0", Fixed32.Clamp(-Fixed32.FromInt(2), Fixed32.Zero, Fixed32.FromInt(5)), Fixed32.Zero);
            CheckEq("Clamp(3,0,5)=3", Fixed32.Clamp(Fixed32.FromInt(3), Fixed32.Zero, Fixed32.FromInt(5)), Fixed32.FromInt(3));
        }

        // ─── InterpolateHeading ────────────────────────────────────────────

        private static void TestInterpolateHeadingBasic()
        {
            // Heading at 0, target at π/2 (0x5A00), max step π/4 (0x2D00).
            // Expected: step by +0x2D00 → result 0x2D00.
            Fixed32 result = Fixed32Math.InterpolateHeading(
                new Fixed32(0),
                new Fixed32(Fixed32Math.HalfPiRaw),
                new Fixed32(Fixed32Math.HalfPiRaw / 2));
            CheckEq("InterpHead: 0→π/2 step π/4 = π/4",
                result.RawValue, Fixed32Math.HalfPiRaw / 2);

            // Heading at π/2, target at 0, max step π/4.
            // diff = 0 - π/2 = -π/2. step by -π/4 → result π/4.
            result = Fixed32Math.InterpolateHeading(
                new Fixed32(Fixed32Math.HalfPiRaw),
                new Fixed32(0),
                new Fixed32(Fixed32Math.HalfPiRaw / 2));
            CheckEq("InterpHead: π/2→0 step π/4 = π/4",
                result.RawValue, Fixed32Math.HalfPiRaw / 2);
        }

        private static void TestInterpolateHeadingWrapAround()
        {
            // Heading at 0xB400 (π), target at 0x100 (just past 0).
            // diff = 0x100 - 0xB400 = -0xB300, which is > -0xB400 (so no wrap adjustment).
            // Then -0xB300 < 0 (turning clockwise) by |0xB300| which is huge.
            // With small max step (say 0x100), we step by -0x100 → 0xB400 - 0x100 = 0xB300.
            Fixed32 result = Fixed32Math.InterpolateHeading(
                new Fixed32(Fixed32Math.PiRaw),     // π
                new Fixed32(0x100),                  // just past 0 = the "long way around"
                new Fixed32(0x100));                 // small step
            CheckEq("InterpHead: π→0x100 step 0x100 wraps clockwise (decreasing)",
                result.RawValue, Fixed32Math.PiRaw - 0x100);

            // Heading at 0x100 (just past 0), target at 0xB400 (π).
            // diff = 0xB400 - 0x100 = 0xB300, < 0xB401 so no wrap.
            // diff > 0, step by +0x100 → 0x200.
            result = Fixed32Math.InterpolateHeading(
                new Fixed32(0x100),
                new Fixed32(Fixed32Math.PiRaw),
                new Fixed32(0x100));
            CheckEq("InterpHead: 0x100→π step 0x100 increases",
                result.RawValue, 0x200);

            // Wrap-around test: heading at 0x100, target at 0x16700 (just before 2π = 0x16800).
            // Going the "short way" (counter-clockwise/negative) is faster.
            // diff = 0x16700 - 0x100 = 0x16600, which is > 0xB400 (π), so > 0xB401 path:
            // diff = 0x16600 - 0x16800 = -0x200.
            // Step by -0x200 → 0x100 - 0x200 = -0x100, then normalize: -0x100 + 0x16800 = 0x16700.
            // So result = 0x16700 (snapped to target since |diff|=0x200 ≤ step=0x200).
            result = Fixed32Math.InterpolateHeading(
                new Fixed32(0x100),
                new Fixed32(0x16700),
                new Fixed32(0x200));
            CheckEq("InterpHead: 0x100→0x16700 takes short path (wraps neg), snaps to target",
                result.RawValue, 0x16700);
        }

        private static void TestInterpolateHeadingTargetReached()
        {
            // Heading == target, no step. diff=0 → result=current.
            Fixed32 result = Fixed32Math.InterpolateHeading(
                new Fixed32(0x5A00),
                new Fixed32(0x5A00),
                new Fixed32(0x100));
            CheckEq("InterpHead: at target, no change", result.RawValue, 0x5A00);

            // Within step: heading at 0, target at 0x50, step 0x100.
            // diff=0x50, 0 < diff < step → snap to target = 0x50.
            result = Fixed32Math.InterpolateHeading(
                new Fixed32(0),
                new Fixed32(0x50),
                new Fixed32(0x100));
            CheckEq("InterpHead: within-step snap to target", result.RawValue, 0x50);
        }

        // ─── NormalizeHeading ──────────────────────────────────────────────

        private static void TestNormalizeHeading()
        {
            CheckEq("Norm: 0=0", Fixed32Math.NormalizeHeading(new Fixed32(0)).RawValue, 0);
            CheckEq("Norm: π=π", Fixed32Math.NormalizeHeading(new Fixed32(Fixed32Math.PiRaw)).RawValue, Fixed32Math.PiRaw);
            CheckEq("Norm: 2π=0",
                Fixed32Math.NormalizeHeading(new Fixed32(Fixed32Math.TwoPiRaw)).RawValue, 0);
            CheckEq("Norm: 2π+1=1",
                Fixed32Math.NormalizeHeading(new Fixed32(Fixed32Math.TwoPiRaw + 1)).RawValue, 1);
            CheckEq("Norm: -1=2π-1",
                Fixed32Math.NormalizeHeading(new Fixed32(-1)).RawValue, Fixed32Math.TwoPiRaw - 1);
            CheckEq("Norm: -2π=0",
                Fixed32Math.NormalizeHeading(new Fixed32(-Fixed32Math.TwoPiRaw)).RawValue, 0);
            CheckEq("Norm: 4π=0",
                Fixed32Math.NormalizeHeading(new Fixed32(Fixed32Math.TwoPiRaw * 2)).RawValue, 0);
        }

        // ─── UnitVectorFromHeading (sin/cos LUT) ───────────────────────────

        private static void TestUnitVectorFromHeading()
        {
            // Heading 0° → idx = (360 - 0) % 360 = 0. SIN[0]=0, COS[0]=256.
            // So result = (X=0, Y=256) — facing positive-Y direction.
            var v = Fixed32Math.UnitVectorFromHeading(new Fixed32(0));
            CheckEq("UnitVec(0°) X=0", v.x.RawValue, 0);
            CheckEq("UnitVec(0°) Y=256 (=1.0)", v.y.RawValue, 256);

            // Heading 90° (= 0x5A00 raw) → idx = (360 - 90) % 360 = 270.
            // SIN[270]=-256, COS[270]=0. Result = (X=-256, Y=0).
            v = Fixed32Math.UnitVectorFromHeading(new Fixed32(Fixed32Math.HalfPiRaw));
            CheckEq("UnitVec(90°) X=-256", v.x.RawValue, -256);
            CheckEq("UnitVec(90°) Y=0", v.y.RawValue, 0);

            // Heading 180° (= 0xB400 raw) → idx = (360 - 180) % 360 = 180.
            // SIN[180]=0, COS[180]=-256. Result = (X=0, Y=-256).
            v = Fixed32Math.UnitVectorFromHeading(new Fixed32(Fixed32Math.PiRaw));
            CheckEq("UnitVec(180°) X=0", v.x.RawValue, 0);
            CheckEq("UnitVec(180°) Y=-256", v.y.RawValue, -256);

            // Heading 270° (= 0x10E00 raw) → idx = (360 - 270) % 360 = 90.
            // SIN[90]=256, COS[90]=0. Result = (X=256, Y=0).
            v = Fixed32Math.UnitVectorFromHeading(new Fixed32(0x10E00));
            CheckEq("UnitVec(270°) X=256", v.x.RawValue, 256);
            CheckEq("UnitVec(270°) Y=0", v.y.RawValue, 0);

            // Heading 45° (= 0x2D00 raw) → idx = (360 - 45) % 360 = 315.
            // SIN[315]=-181, COS[315]=181 (from table data at index 315).
            v = Fixed32Math.UnitVectorFromHeading(new Fixed32(0x2D00));
            CheckEq("UnitVec(45°) X=-181", v.x.RawValue, -181);
            CheckEq("UnitVec(45°) Y=181", v.y.RawValue, 181);

            // Negative heading (e.g., -1° = -0x100 raw) → idx = (360 - (-1)) % 360 = 361 % 360 = 1.
            // SIN[1]=4, COS[1]=255.
            v = Fixed32Math.UnitVectorFromHeading(new Fixed32(-0x100));
            CheckEq("UnitVec(-1°) X=4", v.x.RawValue, 4);
            CheckEq("UnitVec(-1°) Y=255", v.y.RawValue, 255);

            // Magnitude check at cardinal: |UnitVec(0°)|² ≈ 256² (= 65536)
            // SIN² + COS² should equal 256² (within rounding) for any heading.
            // At 0°: 0² + 256² = 65536. ✓
            // At 45°: (-181)² + 181² = 65522 (close to 65536; ~0.02% error from quantization).
            Check("UnitVec(0°) magnitude² ≈ 65536",
                  Math.Abs(0 * 0 + 256 * 256 - 65536) < 16,
                  $"got {0 * 0 + 256 * 256}");
            v = Fixed32Math.UnitVectorFromHeading(new Fixed32(0x2D00));
            int mag2 = v.x.RawValue * v.x.RawValue + v.y.RawValue * v.y.RawValue;
            Check("UnitVec(45°) magnitude² within 1% of 65536",
                  Math.Abs(mag2 - 65536) <= 656,
                  $"got {mag2}, expected ~65536");
        }
    }
}
