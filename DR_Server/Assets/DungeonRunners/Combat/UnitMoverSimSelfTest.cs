using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Utilities;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Synthetic self-test for <see cref="UnitMoverSim"/> v0.5 scaffold. Validates the
    /// non-stub state transitions: heading normalization, ClearMoveState, state-1/state-3
    /// rotation, MoveToPoint→Pathfinder integration. State-2 path-follow is not tested
    /// (it's a v0.5 stub; full port lands in a follow-up).
    ///
    /// Phase 5 task 5 deliverable (v0.5).
    /// </summary>
    public static class UnitMoverSimSelfTest
    {
        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();

            Debug.LogError("[MOVERSIM-SELFTEST] ═══════════════════════════════════════════════════");

            TestNormalizeAngle();
            TestClearMoveState();
            TestStateOneRotation();
            TestStateThreeRotation();
            TestMoveToPointNoOp();
            TestMoveToPointIssuesPath();
            TestPathFollowReachesGoal();
            TestMoveUnitAdvancesPosition();
            TestResolveMovementBlocksWall();
            TestResolveMovementSlidesAlongWall();

            Debug.LogError("[MOVERSIM-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[MOVERSIM-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[MOVERSIM-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[MOVERSIM-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[MOVERSIM-SELFTEST] ALL TESTS PASS — UnitMoverSim v0.7 ready (state-2 + MoveUnit + ResolveMovement slide)");
            }
        }

        private static void TestNormalizeAngle()
        {
            CheckEq("normalize(0) = 0", UnitMoverSim.NormalizeAngle(0), 0);
            CheckEq("normalize(0x16800) = 0", UnitMoverSim.NormalizeAngle(0x16800), 0);
            CheckEq("normalize(0x16801) = 1", UnitMoverSim.NormalizeAngle(0x16801), 1);
            CheckEq("normalize(-1) = 0x16800-1", UnitMoverSim.NormalizeAngle(-1), 0x16800 - 1);
            CheckEq("normalize(-0x16800) = 0", UnitMoverSim.NormalizeAngle(-0x16800), 0);
            CheckEq("normalize(2 * 0x16800) = 0", UnitMoverSim.NormalizeAngle(2 * 0x16800), 0);
        }

        private static void TestClearMoveState()
        {
            var m = new UnitMoverSim
            {
                State = UnitMoverSim.MoveStateEnum.PathFollow,
                HeadingCurrent = 0x10000,
                HeadingTargetSecondary = 0x5000,
                HeadingDelta = 0x100,
                HeadingReset = 0x1234,
                Flags = 0xFF,
                Waypoints = new List<(int, int)> { (100, 200), (300, 400) },
                PathRequestId = 42,
            };
            m.ClearMoveState();
            CheckEq("State = Idle", (int)m.State, 0);
            CheckEq("HeadingCurrent = HeadingReset", m.HeadingCurrent, 0x1234);
            CheckEq("HeadingTargetSecondary = HeadingReset", m.HeadingTargetSecondary, 0x1234);
            CheckEq("HeadingDelta = 0", m.HeadingDelta, 0);
            CheckEq("Waypoints empty", m.Waypoints.Count, 0);
            CheckEq("PathRequestId = -1", m.PathRequestId, -1);
            CheckEq("Flags bit 2 cleared", m.Flags & 0x04, 0);
        }

        private static void TestStateOneRotation()
        {
            var m = new UnitMoverSim
            {
                State = UnitMoverSim.MoveStateEnum.RotatePrimary,
                HeadingCurrent = 0x16800 - 100,
                HeadingDelta = 200,
            };
            m.UpdateSteering();
            // Heading wraps: 0x16800 - 100 + 200 = 0x16800 + 100 → normalize → 100
            CheckEq("state-1 wraps positive", m.HeadingCurrent, 100);

            var m2 = new UnitMoverSim
            {
                State = UnitMoverSim.MoveStateEnum.RotatePrimary,
                HeadingCurrent = 100,
                HeadingDelta = -200,
            };
            m2.UpdateSteering();
            // 100 - 200 = -100 → normalize → 0x16800 - 100
            CheckEq("state-1 wraps negative", m2.HeadingCurrent, 0x16800 - 100);
        }

        private static void TestStateThreeRotation()
        {
            var m = new UnitMoverSim
            {
                State = UnitMoverSim.MoveStateEnum.RotateSecondary,
                HeadingCurrent = 0,
                HeadingTargetSecondary = 0x1000,
                HeadingDelta = 0x500,
            };
            m.UpdateSteering();
            CheckEq("state-3 increments secondary", m.HeadingTargetSecondary, 0x1500);
            CheckEq("state-3 mirrors to current", m.HeadingCurrent, 0x1500);
        }

        private static void TestMoveToPointNoOp()
        {
            var m = new UnitMoverSim
            {
                PosX = Fixed32.FromInt(100),
                PosY = Fixed32.FromInt(200),
            };
            int posBeforeRaw = m.PosX.RawValue;
            m.MoveToPoint(posBeforeRaw, m.PosY.RawValue);  // exact same position → no-op
            CheckEq("no-op when target == current PosX", m.PosX.RawValue, posBeforeRaw);
            CheckEq("no-op leaves State = Idle", (int)m.State, 0);
        }

        private static void TestMoveToPointIssuesPath()
        {
            // 10x10 empty room PathMap.
            var pathMap = PathMap.CreateEmpty("test_room", 0, 100f, 0, 100f);
            for (int gx = 0; gx < 10; gx++)
            for (int gy = 0; gy < 10; gy++)
                pathMap.SetNode(new PathNode
                {
                    GridX = gx, GridY = gy,
                    WorldX = gx * 10f, WorldY = gy * 10f,
                    ConnectionFlags = 0xFF, SolidFlag = 0,
                });

            var pf = new Pathfinder(pathMap);
            var m = new UnitMoverSim
            {
                PosX = Fixed32.FromInt(15),
                PosY = Fixed32.FromInt(15),
                PathMap = pathMap,
                Pathfinder = pf,
            };
            m.MoveToPoint(Fixed32.FromInt(85).RawValue, Fixed32.FromInt(85).RawValue);
            CheckEq("after MoveToPoint State = PathFollow", (int)m.State, 2);
            Check("MoveToPoint populates waypoints", m.Waypoints.Count > 0,
                $"waypoint count={m.Waypoints.Count}");
        }

        private static void TestPathFollowReachesGoal()
        {
            var pathMap = MakeEmptyRoom(10, 10);
            var pf = new Pathfinder(pathMap);
            var m = new UnitMoverSim
            {
                PosX = Fixed32.FromInt(15),
                PosY = Fixed32.FromInt(15),
                Speed = Fixed32.FromFloat(2.5f),       // 2.5 world-units per tick
                ArriveRadius = Fixed32.FromInt(3),
                PathMap = pathMap,
                Pathfinder = pf,
            };
            bool arrived = false;
            m.OnArrived = () => arrived = true;

            m.MoveToPoint(Fixed32.FromInt(85).RawValue, Fixed32.FromInt(85).RawValue);
            CheckEq("path-follow: State = PathFollow after MoveToPoint", (int)m.State, 2);
            Check("path-follow: waypoint vector non-empty", m.Waypoints.Count > 0);

            int initialDistSq = SqDistTo(m, 85, 85);

            // Tick until arrived or 200 ticks.
            int ticks = 0;
            while (!arrived && ticks < 200)
            {
                m.UpdateMovement();
                ticks++;
            }

            Check("path-follow: arrived within 200 ticks", arrived, $"ticks={ticks}");
            CheckEq("path-follow: State = Idle after arrival", (int)m.State, 0);
            // Sanity: we got closer than starting distance.
            int finalDistSq = SqDistTo(m, 85, 85);
            Check("path-follow: moved closer to goal", finalDistSq < initialDistSq);
        }

        private static void TestMoveUnitAdvancesPosition()
        {
            var m = new UnitMoverSim
            {
                PosX = Fixed32.FromInt(0),
                PosY = Fixed32.FromInt(0),
                Speed = Fixed32.FromFloat(2.0f),
                State = UnitMoverSim.MoveStateEnum.RotatePrimary,
                HeadingCurrent = 0,  // North = +Y per client convention
            };
            int startY = m.PosY.RawValue;
            m.MoveUnit();
            Check("MoveUnit: PosY advanced (heading=0=North)", m.PosY.RawValue > startY,
                $"start={startY} after={m.PosY.RawValue}");
        }

        private static void TestResolveMovementBlocksWall()
        {
            // 10x10 room with column 5 fully blocked → wall between mob and goal.
            var pathMap = MakeEmptyRoom(10, 10);
            for (int y = 0; y < 10; y++)
            {
                var n = pathMap.GetNodeAt(5, y);
                n.SolidFlag = 0xFE;
                n.ConnectionFlags = 0x00;
            }

            var m = new UnitMoverSim
            {
                PosX = Fixed32.FromInt(40),   // just east of wall at world x=50
                PosY = Fixed32.FromInt(50),
                PathMap = pathMap,
            };

            // Try to step +X by 20 (would land at x=60, past the wall at x=50).
            int dxRaw = Fixed32.FromInt(20).RawValue;
            int wallXRaw = Fixed32.FromInt(50).RawValue;
            int startPosX = m.PosX.RawValue;
            m.ResolveMovement(dxRaw, 0);
            Check("wall: mob did not cross wall at x=50",
                m.PosX.RawValue < wallXRaw,
                $"start=0x{startPosX:X} after=0x{m.PosX.RawValue:X} wall=0x{wallXRaw:X}");
            Check("wall: mob did not jump to far side",
                m.PosX.RawValue < startPosX + dxRaw,
                $"after=0x{m.PosX.RawValue:X} would-be=0x{startPosX + dxRaw:X}");
        }

        private static void TestResolveMovementSlidesAlongWall()
        {
            // 10x10 room with column 5 fully blocked. Mob moves diagonally NE — X
            // should be blocked, Y should still go through (slide along wall).
            var pathMap = MakeEmptyRoom(10, 10);
            for (int y = 0; y < 10; y++)
            {
                var n = pathMap.GetNodeAt(5, y);
                n.SolidFlag = 0xFE;
                n.ConnectionFlags = 0x00;
            }

            var m = new UnitMoverSim
            {
                PosX = Fixed32.FromInt(40),
                PosY = Fixed32.FromInt(40),
                PathMap = pathMap,
            };

            int dxRaw = Fixed32.FromInt(20).RawValue;  // would cross wall
            int dyRaw = Fixed32.FromInt(20).RawValue;  // wall-parallel
            int wallXRaw = Fixed32.FromInt(50).RawValue;
            int startY = m.PosY.RawValue;
            m.ResolveMovement(dxRaw, dyRaw);
            Check("slide: X did not cross wall", m.PosX.RawValue < wallXRaw,
                $"after=0x{m.PosX.RawValue:X} wall=0x{wallXRaw:X}");
            Check("slide: Y component moved (along wall)", m.PosY.RawValue > startY,
                $"start=0x{startY:X} after=0x{m.PosY.RawValue:X}");
        }

        private static int SqDistTo(UnitMoverSim m, int gx, int gy)
        {
            long dx = m.PosX.RawValue - Fixed32.FromInt(gx).RawValue;
            long dy = m.PosY.RawValue - Fixed32.FromInt(gy).RawValue;
            long sq = dx * dx + dy * dy;
            return sq > int.MaxValue ? int.MaxValue : (int)sq;
        }

        private static PathMap MakeEmptyRoom(int width, int height)
        {
            var pathMap = PathMap.CreateEmpty("test_room", 0, width * 10f, 0, height * 10f);
            for (int gx = 0; gx < width; gx++)
            for (int gy = 0; gy < height; gy++)
                pathMap.SetNode(new PathNode
                {
                    GridX = gx, GridY = gy,
                    WorldX = gx * 10f, WorldY = gy * 10f,
                    ConnectionFlags = 0xFF, SolidFlag = 0,
                });
            return pathMap;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[MOVERSIM-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[MOVERSIM-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, int actual, int expected)
        {
            Check(name, actual == expected, $"got {actual}, expected {expected}");
        }
    }
}
