using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Managers;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Smoke test for <see cref="PathMapBuilder"/>. Generates a small 5×5 elmforest maze
    /// using <see cref="MazeGenerator"/>, builds a <see cref="PathMap"/> from the resulting
    /// cells, and asserts plausible structure (non-empty grid, mostly walkable, footprint
    /// matches the maze bounding box).
    ///
    /// Skipped if game.pki dump is unavailable. Phase 3 task 4 deliverable.
    /// </summary>
    public static class PathMapBuilderSelfTest
    {
        private const string DumpEnvVar = "DR_GAME_PKI_DUMP";
        private const string DumpDefaultPath = @"C:\Users\tippi\Documents\Dungeon Runners\666 game.pki dump";

        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();

            string dumpDir = ResolveDumpDir();
            if (dumpDir == null)
            {
                Debug.LogError($"[PATHMAPBUILD-SELFTEST] dump dir not found — SKIPPED");
                return;
            }

            Debug.LogError("[PATHMAPBUILD-SELFTEST] ═══════════════════════════════════════════════════");

            const int W = 5, H = 5;
            const uint Seed = 0x160FC90C;

            var rng = new MersenneTwister(Seed);
            var gen = new MazeGenerator(W, H, Seed, rng: rng);
            List<MazeGenerator.MazeCell> cells;
            try
            {
                cells = gen.Generate();
            }
            catch (Exception e)
            {
                Debug.LogError($"[PATHMAPBUILD-SELFTEST] [FAIL] MazeGenerator.Generate threw: {e}");
                _failures.Add("Generate threw");
                return;
            }

            Check("MazeGenerator produced cells", cells != null && cells.Count == W * H, $"got {cells?.Count}");

            PathMap pathMap;
            DateTime t0 = DateTime.UtcNow;
            try
            {
                pathMap = PathMapBuilder.Build("dungeon00_level01_test", cells);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PATHMAPBUILD-SELFTEST] [FAIL] PathMapBuilder.Build threw: {e}");
                _failures.Add("Build threw");
                return;
            }
            double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            Debug.LogError($"[PATHMAPBUILD-SELFTEST] build time: {ms:F0}ms for {cells.Count} cells");

            Check("PathMap not null", pathMap != null);
            if (pathMap == null) { Finish(); return; }

            int nodeCount = pathMap.NodeCount;
            Debug.LogError($"[PATHMAPBUILD-SELFTEST] PathMap: nodes={nodeCount}");

            // Each maze cell is 400 world units. 5×5 maze = 2000×2000. Node spacing = 10.
            // So 200×200 = 40000 nodes — but maze footprint is inclusive so could be off by a few rows.
            Check("node count > 30000", nodeCount > 30000, $"got {nodeCount}");
            Check("node count < 50000", nodeCount < 50000, $"got {nodeCount}");

            int walkable = 0;
            int blocked = 0;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            for (int gx = -1000; gx < 1000; gx++)
            {
                for (int gy = -1000; gy < 1000; gy++)
                {
                    var n = pathMap.GetNodeAt(gx, gy);
                    if (n == null) continue;
                    if (n.IsWalkable) walkable++; else blocked++;
                    if (n.WorldX < minX) minX = n.WorldX;
                    if (n.WorldX > maxX) maxX = n.WorldX;
                    if (n.WorldY < minY) minY = n.WorldY;
                    if (n.WorldY > maxY) maxY = n.WorldY;
                }
            }
            Debug.LogError($"[PATHMAPBUILD-SELFTEST] walkable={walkable} blocked={blocked} bounds=({minX:F0},{minY:F0})→({maxX:F0},{maxY:F0})");

            Check("majority walkable", walkable > blocked, $"walkable={walkable} blocked={blocked}");
            Check("at least some blocked (walls present)", blocked > 100, $"blocked={blocked}");

            // Center cell should be walkable (maze cells always have walkable interior even with walls).
            var centerCell = cells[cells.Count / 2];
            var centerNode = pathMap.GetNodeAtWorld(centerCell.WorldCenterX, centerCell.WorldCenterY);
            Check("center of middle cell has a node", centerNode != null);
            if (centerNode != null)
                Check("center of middle cell is walkable", centerNode.IsWalkable, $"SolidFlag=0x{centerNode.SolidFlag:X2}");

            // Task 6: reachability check between two cell centers — analogue of PF3 sample queries.
            // Pick the corner cell most-distant from the center; if maze has any walkable path
            // between them, TryCanReachPoint should report true. (TILE_SIZE = 10 means a 40-step ray.)
            var cornerCell = cells[0];
            bool canReach = pathMap.TryCanReachPoint(
                centerCell.WorldCenterX, centerCell.WorldCenterY,
                cornerCell.WorldCenterX, cornerCell.WorldCenterY,
                out bool reachResult);
            Debug.LogError($"[PATHMAPBUILD-SELFTEST] reach query center→corner: tryOK={canReach} reach={reachResult}");
            Check("reach query between cell centers does not error", canReach);

            // Sanity: both endpoints should have nodes (PF3 sample 1 was "NO NODE on both endpoints")
            var startNode = pathMap.GetNodeAtWorld(centerCell.WorldCenterX, centerCell.WorldCenterY);
            var endNode = pathMap.GetNodeAtWorld(cornerCell.WorldCenterX, cornerCell.WorldCenterY);
            Check("PF3 sample 1 analogue: start endpoint has node", startNode != null);
            Check("PF3 sample 1 analogue: end endpoint has node", endNode != null);

            Finish();
        }

        private static void Finish()
        {
            Debug.LogError($"[PATHMAPBUILD-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[PATHMAPBUILD-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[PATHMAPBUILD-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[PATHMAPBUILD-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[PATHMAPBUILD-SELFTEST] ALL TESTS PASS — PathMapBuilder ready");
            }
        }

        private static string ResolveDumpDir()
        {
            string envPath = Environment.GetEnvironmentVariable(DumpEnvVar);
            if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) return envPath;
            if (Directory.Exists(DumpDefaultPath)) return DumpDefaultPath;
            return null;
        }

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[PATHMAPBUILD-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[PATHMAPBUILD-SELFTEST] [FAIL] {msg}");
            }
        }
    }
}
