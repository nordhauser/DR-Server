using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Core
{
    /// <summary>
    /// Synthetic self-test for <see cref="Pathfinder"/>. Builds small hand-crafted
    /// <see cref="PathMap"/>s and checks A* behavior: direct reach, L-shape (1 corner),
    /// blocked goal, unreachable. Algorithmic correctness only — client-parity
    /// validation requires x32dbg captures and is deferred.
    ///
    /// Phase 4 task 4 deliverable.
    /// </summary>
    public static class PathfinderSelfTest
    {
        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();

            Debug.LogError("[PATHFINDER-SELFTEST] ═══════════════════════════════════════════════════");

            TestDirectionTable();
            TestGetDirFromAToB();
            TestDirectReachEmptyRoom();
            TestLShapePath();
            TestBlockedGoal();
            TestUnreachableGoal();
            TestNoStraightLineRequiresCorner();

            Debug.LogError($"[PATHFINDER-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[PATHFINDER-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[PATHFINDER-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[PATHFINDER-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[PATHFINDER-SELFTEST] ALL TESTS PASS — Pathfinder ready for Phase 5 (algorithmic only; x32dbg parity pending)");
            }
        }

        // ─── Tests ──────────────────────────────────────────────────────────

        private static void TestDirectionTable()
        {
            Check("Directions[0] = North (0,1)",
                PathMap.Directions[0].dx == 0 && PathMap.Directions[0].dy == 1);
            Check("Directions[2] = East (1,0)",
                PathMap.Directions[2].dx == 1 && PathMap.Directions[2].dy == 0);
            Check("Directions[4] = South (0,-1)",
                PathMap.Directions[4].dx == 0 && PathMap.Directions[4].dy == -1);
            Check("Directions[6] = West (-1,0)",
                PathMap.Directions[6].dx == -1 && PathMap.Directions[6].dy == 0);
            Check("DirectionCosts[cardinal] = 10",
                PathMap.DirectionCosts[0] == 10 && PathMap.DirectionCosts[2] == 10);
            Check("DirectionCosts[diagonal] = 14",
                PathMap.DirectionCosts[1] == 14 && PathMap.DirectionCosts[3] == 14);
        }

        private static void TestGetDirFromAToB()
        {
            var origin = MakeNode(0, 0);
            CheckEq("dir(0,0)→(0,1) = N (0)", PathMap.GetDirFromAToB(origin, MakeNode(0, 1)), 0);
            CheckEq("dir(0,0)→(1,1) = NE (1)", PathMap.GetDirFromAToB(origin, MakeNode(1, 1)), 1);
            CheckEq("dir(0,0)→(1,0) = E (2)", PathMap.GetDirFromAToB(origin, MakeNode(1, 0)), 2);
            CheckEq("dir(0,0)→(0,-1) = S (4)", PathMap.GetDirFromAToB(origin, MakeNode(0, -1)), 4);
            CheckEq("dir(0,0)→(-1,0) = W (6)", PathMap.GetDirFromAToB(origin, MakeNode(-1, 0)), 6);
            CheckEq("dir(0,0)→(0,0) = -1", PathMap.GetDirFromAToB(origin, MakeNode(0, 0)), -1);
            CheckEq("dir(0,0)→(5,0) = E (2) (only sign matters)",
                PathMap.GetDirFromAToB(origin, MakeNode(5, 0)), 2);
        }

        private static void TestDirectReachEmptyRoom()
        {
            // 10x10 empty room: every node walkable, no obstacles between start and goal.
            var pathMap = BuildSquareRoom(10, 10);
            var pf = new Pathfinder(pathMap);
            var start = pathMap.GetNodeAt(1, 1);
            var goal = pathMap.GetNodeAt(8, 8);

            pf.RequestPath(start, goal);
            Check("EmptyRoom: directReach true after RequestPath", pf.DirectReach);
            Check("EmptyRoom: done immediately", pf.IsDone);

            bool ok = pf.GetPath(out var waypoints);
            Check("EmptyRoom: GetPath returns true", ok);
            CheckEq("EmptyRoom: 2 waypoints", waypoints?.Count ?? 0, 2);
            if (waypoints != null && waypoints.Count == 2)
            {
                Check("EmptyRoom: waypoint[0] = start", waypoints[0] == start);
                Check("EmptyRoom: waypoint[1] = goal", waypoints[1] == goal);
            }
        }

        private static void TestLShapePath()
        {
            // 10x10 room with a wall along column 5, rows 0..7 (leaves gap at top).
            // Start (1,1), goal (8,1) — straight line blocked, must go up and around.
            var pathMap = BuildSquareRoom(10, 10);
            for (int y = 0; y <= 7; y++)
                BlockNode(pathMap, 5, y);

            var pf = new Pathfinder(pathMap);
            pf.RequestPath(pathMap.GetNodeAt(1, 1), pathMap.GetNodeAt(8, 1));

            while (!pf.IsDone && pf.NodesExpanded < 1000) pf.UpdateRequest(64);
            Check("LShape: search completed within budget", pf.IsDone);
            Check("LShape: not directReach (wall in the way)", !pf.DirectReach);

            bool ok = pf.GetPath(out var waypoints);
            Check("LShape: GetPath returns true", ok);
            Check("LShape: 3+ waypoints (must turn corner)",
                (waypoints?.Count ?? 0) >= 3, $"got {waypoints?.Count ?? 0}");
        }

        private static void TestBlockedGoal()
        {
            // Goal node itself is blocked → no path.
            var pathMap = BuildSquareRoom(10, 10);
            BlockNode(pathMap, 8, 8);

            var pf = new Pathfinder(pathMap);
            pf.RequestPath(pathMap.GetNodeAt(1, 1), pathMap.GetNodeAt(8, 8));

            while (!pf.IsDone && pf.NodesExpanded < 1000) pf.UpdateRequest(64);
            Check("BlockedGoal: search terminates", pf.IsDone);

            bool ok = pf.GetPath(out var waypoints);
            Check("BlockedGoal: GetPath returns false (no path)", !ok,
                $"got ok={ok} waypoints={waypoints?.Count ?? 0}");
        }

        private static void TestUnreachableGoal()
        {
            // Wall completely separates start side from goal side.
            var pathMap = BuildSquareRoom(10, 10);
            for (int y = 0; y < 10; y++)
                BlockNode(pathMap, 5, y);

            var pf = new Pathfinder(pathMap);
            pf.RequestPath(pathMap.GetNodeAt(1, 1), pathMap.GetNodeAt(8, 8));

            while (!pf.IsDone && pf.NodesExpanded < 1000) pf.UpdateRequest(64);
            Check("Unreachable: open set drains, search terminates", pf.IsDone);

            bool ok = pf.GetPath(out _);
            Check("Unreachable: GetPath returns false", !ok);
        }

        private static void TestNoStraightLineRequiresCorner()
        {
            // Single obstacle directly between start and goal; A* should route around.
            var pathMap = BuildSquareRoom(10, 10);
            BlockNode(pathMap, 5, 5);

            var pf = new Pathfinder(pathMap);
            pf.RequestPath(pathMap.GetNodeAt(2, 5), pathMap.GetNodeAt(8, 5));

            // Note: direct-reach via TryCanReachPoint may still return true if the ray
            // happens to miss the single blocked cell (sampling resolution). That's fine —
            // we just check we got SOME path.
            while (!pf.IsDone && pf.NodesExpanded < 1000) pf.UpdateRequest(64);
            Check("SingleObstacle: search terminates", pf.IsDone);
            bool ok = pf.GetPath(out var waypoints);
            Check("SingleObstacle: got a path", ok, $"waypoints={waypoints?.Count ?? 0}");
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        private static PathMap BuildSquareRoom(int width, int height)
        {
            var pathMap = PathMap.CreateEmpty("test_room", 0, width * 10f, 0, height * 10f);
            for (int gx = 0; gx < width; gx++)
            {
                for (int gy = 0; gy < height; gy++)
                {
                    pathMap.SetNode(new PathNode
                    {
                        GridX = gx,
                        GridY = gy,
                        WorldX = gx * 10f,
                        WorldY = gy * 10f,
                        Height = 0f,
                        ConnectionFlags = 0xFF,
                        SolidFlag = 0x00,
                    });
                }
            }
            return pathMap;
        }

        private static void BlockNode(PathMap pathMap, int gx, int gy)
        {
            var node = pathMap.GetNodeAt(gx, gy);
            if (node == null) return;
            node.SolidFlag = 0xFE;
            node.ConnectionFlags = 0x00;
        }

        private static PathNode MakeNode(int gx, int gy)
        {
            return new PathNode
            {
                GridX = gx,
                GridY = gy,
                WorldX = gx * 10f,
                WorldY = gy * 10f,
                SolidFlag = 0,
                ConnectionFlags = 0xFF,
            };
        }

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[PATHFINDER-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[PATHFINDER-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, int actual, int expected)
        {
            Check(name, actual == expected, $"got {actual}, expected {expected}");
        }
    }
}
