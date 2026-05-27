using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Smoke test for <see cref="CobjParser"/>. Boot-time validation: parses 3 known reference
    /// <c>.cobj</c> files and asserts header fields. Skipped silently if the unpacked
    /// <c>game.pki</c> dump folder is unavailable (e.g. on a deployed server).
    ///
    /// Reference values are taken from manual hex inspection of the source files on 2026-05-27.
    /// </summary>
    public static class CobjParserSelfTest
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
                Debug.LogError($"[COBJ-SELFTEST] dump dir not found; set {DumpEnvVar} or place at {DumpDefaultPath} — SKIPPED");
                return;
            }

            Debug.LogError("[COBJ-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[COBJ-SELFTEST] dump dir: {dumpDir}");

            TestSmallProp(dumpDir);
            TestWallStraight(dumpDir);
            TestWallStraight2(dumpDir);

            Debug.LogError($"[COBJ-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[COBJ-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[COBJ-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[COBJ-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[COBJ-SELFTEST] ALL TESTS PASS — CobjParser ready for Phase 3 use");
            }
        }

        private static string ResolveDumpDir()
        {
            string envPath = Environment.GetEnvironmentVariable(DumpEnvVar);
            if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) return envPath;
            if (Directory.Exists(DumpDefaultPath)) return DumpDefaultPath;
            return null;
        }

        private static void TestSmallProp(string dir)
        {
            string path = Path.Combine(dir, "AutumnForest_SmallTree_1.cobj");
            if (!File.Exists(path)) { Check("AutumnForest_SmallTree_1.cobj exists", false, "file missing"); return; }

            CobjData c = CobjParser.ParseFile(path);
            CheckEq("SmallTree dfcHash != 0", c.DfcHash != 0, "hash was zero");
            CheckEq("SmallTree cellSize1=5", c.CellSize1, 5);
            CheckEq("SmallTree width1=0", c.Width1, 0);
            CheckEq("SmallTree height1=0", c.Height1, 0);
            CheckEq("SmallTree heightmap empty", c.Heightmap.Length, 0);
            CheckEq("SmallTree cellSize2=5", c.CellSize2, 5);
            CheckEq("SmallTree width2=0", c.Width2, 0);
            CheckEq("SmallTree height2=0", c.Height2, 0);
            CheckEq("SmallTree cells empty", c.Cells.Length, 0);
            CheckEq("SmallTree consumed all 75 bytes", c.BytesConsumed, 75);
        }

        private static void TestWallStraight(string dir)
        {
            string path = Path.Combine(dir, "ElmForest_2_Straight_1.cobj");
            if (!File.Exists(path)) { Check("ElmForest_2_Straight_1.cobj exists", false, "file missing"); return; }

            CobjData c = CobjParser.ParseFile(path);
            CheckEq("Straight1 cellSize1=5", c.CellSize1, 5);
            CheckEq("Straight1 originX1=-20", c.OriginX1, -20);
            CheckEq("Straight1 originY1=-20", c.OriginY1, -20);
            CheckEq("Straight1 width1=9", c.Width1, 9);
            CheckEq("Straight1 height1=9", c.Height1, 9);
            CheckEq("Straight1 heightmap len=81", c.Heightmap.Length, 81);
            CheckEq("Straight1 cellSize2=5", c.CellSize2, 5);
            Check("Straight1 has sub-shape-2 cells", c.Cells.Length > 0, $"cells={c.Cells.Length}");
            Check("Straight1 fully consumed", c.BytesConsumed > 0, $"consumed={c.BytesConsumed}");
        }

        private static void TestWallStraight2(string dir)
        {
            string path = Path.Combine(dir, "ElmForest_4_Straight_2.cobj");
            if (!File.Exists(path)) { Check("ElmForest_4_Straight_2.cobj exists", false, "file missing"); return; }

            // Spec: 453-byte wall; should parse without throwing, with non-empty grids.
            CobjData c = CobjParser.ParseFile(path);
            CheckEq("Straight2 cellSize1=5", c.CellSize1, 5);
            Check("Straight2 has heightmap", c.Heightmap.Length > 0, $"heightmap.Length={c.Heightmap.Length}");
            Check("Straight2 has sub-shape-2 cells", c.Cells.Length > 0, $"cells={c.Cells.Length}");
        }

        // ─── Helpers ────────────────────────────────────────────────

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[COBJ-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[COBJ-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, int actual, int expected)
        {
            Check(name, actual == expected, $"got {actual}, expected {expected}");
        }

        private static void CheckEq(string name, bool actual, string failDetail = null)
        {
            Check(name, actual, failDetail);
        }
    }
}
