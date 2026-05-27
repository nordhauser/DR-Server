using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Smoke test for <see cref="TileLayoutLoader"/> + <see cref="TileCobjResolver"/>. Loads one
    /// real tile template, asserts non-trivial placement count, and reports the cobj-resolution
    /// hit rate. Skipped silently if <c>666 game.pki dump/</c> is unavailable.
    ///
    /// Phase 3 task 3b/3c deliverable.
    /// </summary>
    public static class TileLayoutSelfTest
    {
        private const string DumpEnvVar = "DR_GAME_PKI_DUMP";
        private const string DumpDefaultPath = @"C:\Users\tippi\Documents\Dungeon Runners\666 game.pki dump";

        private const string SampleTile = "elmforest_tileset_1n_A.tile";

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
                Debug.LogError($"[TILE-SELFTEST] dump dir not found; set {DumpEnvVar} or place at {DumpDefaultPath} — SKIPPED");
                return;
            }

            Debug.LogError("[TILE-SELFTEST] ═══════════════════════════════════════════════════");

            string tilePath = Path.Combine(dumpDir, SampleTile);
            if (!File.Exists(tilePath))
            {
                Debug.LogError($"[TILE-SELFTEST] sample tile not found: {tilePath} — SKIPPED");
                return;
            }

            TileLayout layout;
            try
            {
                layout = TileLayoutLoader.Load(tilePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TILE-SELFTEST] [FAIL] TileLayoutLoader.Load threw: {e.GetType().Name}: {e.Message}");
                _failures.Add("Load threw");
                return;
            }

            Debug.LogError($"[TILE-SELFTEST] loaded {SampleTile}: rootExtends={layout.RootExtends} placements={layout.Placements.Count}");

            CheckEq("RootExtends=base.world", layout.RootExtends, "base.world");
            Check("placements >= 50", layout.Placements.Count >= 50, $"got {layout.Placements.Count}");

            int withCobj = 0;
            int withoutCobj = 0;
            var unresolvedSamples = new List<string>();
            foreach (var p in layout.Placements)
            {
                string path = TileCobjResolver.ResolveCobjPath(p.ExtendsPath);
                if (path != null) withCobj++;
                else
                {
                    withoutCobj++;
                    if (unresolvedSamples.Count < 3) unresolvedSamples.Add(p.ExtendsPath);
                }
            }

            int hitPct = layout.Placements.Count > 0
                ? (int)(100L * withCobj / layout.Placements.Count)
                : 0;
            Debug.LogError($"[TILE-SELFTEST] cobj resolution: {withCobj}/{layout.Placements.Count} hit ({hitPct}%); {withoutCobj} unresolved");
            if (unresolvedSamples.Count > 0)
                Debug.LogError($"[TILE-SELFTEST] unresolved examples: {string.Join(", ", unresolvedSamples)}");

            Check("≥60% cobj hit rate", hitPct >= 60, $"got {hitPct}%");

            int parsed = 0;
            int parseErrors = 0;
            string lastError = null;
            int maxParse = Math.Min(layout.Placements.Count, 20);
            for (int i = 0; i < layout.Placements.Count && parsed < maxParse; i++)
            {
                var p = layout.Placements[i];
                string path = TileCobjResolver.ResolveCobjPath(p.ExtendsPath);
                if (path == null) continue;
                try
                {
                    CobjParser.ParseFile(path);
                    parsed++;
                }
                catch (Exception e)
                {
                    parseErrors++;
                    lastError = $"{Path.GetFileName(path)}: {e.Message}";
                }
            }
            Debug.LogError($"[TILE-SELFTEST] parsed {parsed} cobj files from this tile ({parseErrors} errors)");
            if (parseErrors > 0)
                Debug.LogError($"[TILE-SELFTEST] last parse error: {lastError}");

            Check("no parse errors on sample placements", parseErrors == 0, $"got {parseErrors} errors");

            Debug.LogError($"[TILE-SELFTEST] cobj index size: {TileCobjResolver.CobjFileCount} entries");
            Check("cobj index >= 700 files", TileCobjResolver.CobjFileCount >= 700, $"got {TileCobjResolver.CobjFileCount}");

            Debug.LogError($"[TILE-SELFTEST] ═══════════════════════════════════════════════════");
            Debug.LogError($"[TILE-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                Debug.LogError("[TILE-SELFTEST] FAILURES:");
                foreach (var f in _failures)
                    Debug.LogError($"[TILE-SELFTEST]   - {f}");
            }
            else
            {
                Debug.LogError("[TILE-SELFTEST] ALL TESTS PASS — TileLayoutLoader + Resolver ready for Phase 3 use");
            }
        }

        private static string ResolveDumpDir()
        {
            string envPath = Environment.GetEnvironmentVariable(DumpEnvVar);
            if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) return envPath;
            if (Directory.Exists(DumpDefaultPath)) return DumpDefaultPath;
            return null;
        }

        // ─── Helpers ────────────────────────────────────────────────

        private static void Check(string name, bool condition, string failDetail = null)
        {
            _testsRun++;
            if (condition)
            {
                _testsPassed++;
                Debug.LogError($"[TILE-SELFTEST] [PASS] {name}");
            }
            else
            {
                string msg = $"{name}" + (failDetail != null ? $" — {failDetail}" : "");
                _failures.Add(msg);
                Debug.LogError($"[TILE-SELFTEST] [FAIL] {msg}");
            }
        }

        private static void CheckEq(string name, string actual, string expected)
        {
            Check(name, actual == expected, $"got '{actual}', expected '{expected}'");
        }
    }
}
