using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Managers;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// A1.2 of plan vivid-marinating-pixel (2026-05-28). Tunes the
    /// <see cref="PathMapBuilder.WallHeightThreshold"/> against captured client
    /// pathfinder traces (PHASE4B_PATHFINDER_CAPTURES.txt). For each candidate
    /// threshold in <see cref="DefaultThresholds"/>, builds a candidate
    /// <see cref="PathMap"/> from the same maze cells and scores it against the
    /// loaded captures via
    /// <see cref="PathfinderClientParityTest.RunCaseAgainst"/>. Reports a single
    /// summary table so the production threshold can be picked by score, not
    /// guess.
    ///
    /// Off by default (<see cref="Enabled"/> = false) because each sweep rebuilds
    /// N PathMaps (one per threshold) and that's expensive — ~500ms/build × 9
    /// thresholds = ~5s per zone enter for the 5x5 test. Flip on, enter a
    /// dungeon once, read the table, flip off.
    /// </summary>
    public static class PathMapThresholdSweep
    {
        /// <summary>Master gate. Default off — expensive to run on every zone enter.
        /// Flip true when re-tuning is needed (e.g. after adding new captures), let it fire
        /// once, then flip back to false.</summary>
        public static bool Enabled = false;

        /// <summary>Default sweep set: 9 candidates centered on the production 30.</summary>
        public static readonly int[] DefaultThresholds =
        {
            10, 15, 20, 25, 30, 35, 40, 45, 50
        };

        /// <summary>
        /// Build candidate PathMaps with each threshold and score against the
        /// loaded captures. No mutation of <see cref="PathMapManager"/> — production
        /// PathMap is unaffected.
        /// </summary>
        public static void Run(
            string zoneName,
            IReadOnlyList<MazeGenerator.MazeCell> cells,
            IReadOnlyList<int> thresholds = null)
        {
            if (cells == null || cells.Count == 0)
            {
                Debug.LogError($"[PATHMAP-SWEEP] zone='{zoneName}' SKIP — empty cell list");
                return;
            }

            string capturePath = PathfinderClientParityTest.ResolveCapturePath();
            var captures = PathfinderClientParityTest.LoadCaptures(capturePath);
            if (captures == null || captures.Count == 0)
            {
                Debug.LogError($"[PATHMAP-SWEEP] no captures in {capturePath} — SKIP");
                return;
            }

            var set = thresholds ?? DefaultThresholds;
            Debug.LogError($"[PATHMAP-SWEEP] ═══════════════════════════════════════════════════");
            Debug.LogError($"[PATHMAP-SWEEP] zone='{zoneName}' candidates={set.Count} captures={captures.Count}");

            int bestThreshold = -1;
            int bestStrong = -1;
            int bestSoft = -1;

            foreach (int t in set)
            {
                var pm = PathMapBuilder.Build(zoneName, cells, t);
                if (pm == null)
                {
                    Debug.LogError($"[PATHMAP-SWEEP] threshold={t,3} — Build returned null, SKIP");
                    continue;
                }

                int strong = 0, soft = 0, fail = 0, skipped = 0;
                foreach (var c in captures)
                {
                    var v = PathfinderClientParityTest.RunCaseAgainst(c, pm);
                    switch (v.Status)
                    {
                        case PathfinderClientParityTest.CaseStatus.StrongPass: strong++; break;
                        case PathfinderClientParityTest.CaseStatus.SoftPass: soft++; break;
                        case PathfinderClientParityTest.CaseStatus.Fail: fail++; break;
                        case PathfinderClientParityTest.CaseStatus.Skipped: skipped++; break;
                    }
                }
                int scored = strong + soft + fail;
                int passPct = scored > 0 ? (100 * (strong + soft)) / scored : 0;

                Debug.LogError(
                    $"[PATHMAP-SWEEP] threshold={t,3} → strong={strong} soft={soft} fail={fail} skip={skipped} ({passPct}% pass)");

                // Best by strong-pass count, then by soft-pass count (lexicographic).
                if (strong > bestStrong || (strong == bestStrong && soft > bestSoft))
                {
                    bestStrong = strong;
                    bestSoft = soft;
                    bestThreshold = t;
                }
            }

            Debug.LogError($"[PATHMAP-SWEEP] ═══════════════════════════════════════════════════");
            if (bestThreshold >= 0)
            {
                Debug.LogError(
                    $"[PATHMAP-SWEEP] BEST threshold={bestThreshold} (strong={bestStrong} soft={bestSoft}). " +
                    $"Production currently uses {PathMapBuilder.WallHeightThreshold}.");
            }
            else
            {
                Debug.LogError("[PATHMAP-SWEEP] no winner — every candidate scored 0/0 (likely all captures skipped). Check zone/baseZone match.");
            }
        }
    }
}
