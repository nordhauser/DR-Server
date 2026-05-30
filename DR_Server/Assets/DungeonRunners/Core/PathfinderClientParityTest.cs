using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Utilities;
using UnityEngine;

namespace DungeonRunners.Core
{
    /// <summary>
    /// Phase 4b client-parity validation for <see cref="Pathfinder"/>. Loads captured
    /// <c>(start, goal) → waypoints[]</c> triples from a running client (via x32dbg
    /// breakpoint at <c>Pathfinder::GetPath @ 0x005D2F20</c>) and runs each through
    /// our server Pathfinder, comparing outputs.
    ///
    /// Capture file: <c>WORK/PHASE4B_PATHFINDER_CAPTURES.txt</c>.
    /// Procedure: <c>WORK/PHASE4B_CAPTURE_PROCEDURE.md</c>.
    ///
    /// Acceptance bar (per Phase 4 plan): ≥90% of cases match the client byte-for-byte
    /// or within ≤1 tile drift. Fewer → investigate divergence.
    /// </summary>
    public static class PathfinderClientParityTest
    {
        private const string DefaultCapturePath = @"C:\Users\tippi\Documents\Dungeon Runners\WORK\PHASE4B_PATHFINDER_CAPTURES.txt";
        private const int OneTileTolerance = Fixed32.OneRaw * 10; // ≤1 tile = 10 world units in Fixed32 raw

        private static int _testsRun;
        private static int _testsPassed;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0;
            _testsPassed = 0;
            _failures.Clear();

            string capturePath = ResolveCapturePath();
            if (capturePath == null || !File.Exists(capturePath))
            {
                Debug.LogError($"[PATHFINDER-PARITY] capture file not found ({DefaultCapturePath}) — SKIPPED");
                return;
            }

            var cases = LoadCaptures(capturePath);
            if (cases.Count == 0)
            {
                Debug.LogError($"[PATHFINDER-PARITY] no cases in {capturePath} — SKIPPED (waiting for captures)");
                return;
            }

            Debug.LogError("[PATHFINDER-PARITY] ═══════════════════════════════════════════════════");
            Debug.LogError($"[PATHFINDER-PARITY] loaded {cases.Count} cases from {Path.GetFileName(capturePath)}");

            int strongPass = 0;
            int softPass = 0;
            int fail = 0;
            int skipped = 0;

            foreach (var c in cases)
            {
                var verdict = RunCase(c);
                switch (verdict.Status)
                {
                    case CaseStatus.StrongPass: strongPass++; break;
                    case CaseStatus.SoftPass: softPass++; break;
                    case CaseStatus.Fail: fail++; break;
                    case CaseStatus.Skipped: skipped++; break;
                }
                Debug.LogError($"[PATHFINDER-PARITY] [{verdict.Status}] {c.Name}: {verdict.Detail}");
            }

            int total = strongPass + softPass + fail;
            int passPct = total > 0 ? (100 * (strongPass + softPass)) / total : 0;

            Debug.LogError($"[PATHFINDER-PARITY] ═══════════════════════════════════════════════════");
            Debug.LogError($"[PATHFINDER-PARITY] {strongPass} strong / {softPass} soft / {fail} fail / {skipped} skipped — {passPct}% pass");
            if (passPct >= 90 && total > 0)
                Debug.LogError("[PATHFINDER-PARITY] ACCEPTANCE MET (≥90% pass)");
            else if (total > 0)
                Debug.LogError($"[PATHFINDER-PARITY] BELOW ACCEPTANCE (got {passPct}%, need ≥90%)");
        }

        // ─── Case execution ─────────────────────────────────────────────────

        internal enum CaseStatus { StrongPass, SoftPass, Fail, Skipped }

        internal struct Verdict
        {
            public CaseStatus Status;
            public string Detail;
        }

        private static Verdict RunCase(Capture c)
        {
            // Try exact zone match first; fall back to BaseZone prefix for procedural
            // instances (instance IDs vary per session; the PathMap is registered under
            // the current session's instance key).
            var pm = PathMapManager.Instance.GetPathMap(c.Zone);
            if (pm == null && !string.IsNullOrEmpty(c.BaseZone))
                pm = PathMapManager.Instance.FindByPrefix(c.BaseZone);
            return RunCaseAgainst(c, pm);
        }

        /// <summary>
        /// Score a single capture against an arbitrary <see cref="PathMap"/>. Used by
        /// <see cref="PathMapThresholdSweep"/> to evaluate candidate builds without
        /// mutating <see cref="PathMapManager"/>.
        /// </summary>
        internal static Verdict RunCaseAgainst(Capture c, PathMap pm)
        {
            if (pm == null)
                return new Verdict { Status = CaseStatus.Skipped, Detail = $"no PathMap for zone='{c.Zone}' or baseZone='{c.BaseZone}'" };

            var startNode = pm.GetNodeAtWorld(c.StartX / (float)Fixed32.OneRaw, c.StartY / (float)Fixed32.OneRaw);
            var goalNode = pm.GetNodeAtWorld(c.GoalX / (float)Fixed32.OneRaw, c.GoalY / (float)Fixed32.OneRaw);
            if (startNode == null || goalNode == null)
                return new Verdict { Status = CaseStatus.Skipped, Detail = "start or goal endpoint has no PathMap node" };

            var pf = new Pathfinder(pm);
            pf.RequestPath(startNode, goalNode);
            while (!pf.IsDone && pf.NodesExpanded < 8192) pf.UpdateRequest(64);
            if (!pf.GetPath(out var ourWaypoints))
                return new Verdict { Status = CaseStatus.Fail, Detail = "our Pathfinder returned no path" };

            // Compare directReach outcome.
            if (pf.DirectReach != c.DirectReach)
                return new Verdict { Status = CaseStatus.Fail, Detail = $"directReach mismatch (ours={pf.DirectReach}, client={c.DirectReach})" };

            // Compare waypoint counts.
            if (ourWaypoints.Count != c.Waypoints.Count)
                return new Verdict { Status = CaseStatus.Fail, Detail = $"waypoint count mismatch (ours={ourWaypoints.Count}, client={c.Waypoints.Count})" };

            // Compare per-waypoint coords.
            int worstDriftSq = 0;
            for (int i = 0; i < ourWaypoints.Count; i++)
            {
                int ourX = Fixed32.FromFloat(ourWaypoints[i].WorldX).RawValue;
                int ourY = Fixed32.FromFloat(ourWaypoints[i].WorldY).RawValue;
                int clientX = c.Waypoints[i].x;
                int clientY = c.Waypoints[i].y;
                long dx = ourX - clientX;
                long dy = ourY - clientY;
                long driftSq = dx * dx + dy * dy;
                if (driftSq > worstDriftSq && driftSq < int.MaxValue) worstDriftSq = (int)driftSq;
            }

            if (worstDriftSq == 0)
                return new Verdict { Status = CaseStatus.StrongPass, Detail = "byte-equal waypoints" };

            long tolSq = (long)OneTileTolerance * OneTileTolerance;
            if (worstDriftSq <= tolSq)
                return new Verdict { Status = CaseStatus.SoftPass, Detail = $"worst drift √{worstDriftSq} ≤ 1 tile" };

            return new Verdict { Status = CaseStatus.Fail, Detail = $"drift √{worstDriftSq} > 1 tile tolerance" };
        }

        // ─── Capture loading ─────────────────────────────────────────────────

        internal sealed class Capture
        {
            public string Name = "";
            public string Zone = "";
            public string BaseZone = "";
            public uint Seed;
            public int StartX, StartY;
            public int GoalX, GoalY;
            public bool DirectReach;
            public List<(int x, int y)> Waypoints = new List<(int, int)>();
        }

        internal static List<Capture> LoadCaptures(string path)
        {
            var cases = new List<Capture>();
            Capture current = null;
            int lineNum = 0;
            foreach (var rawLine in File.ReadAllLines(path))
            {
                lineNum++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var parts = line.Split(' ');
                switch (parts[0].ToUpperInvariant())
                {
                    case "CASE":
                        current = new Capture();
                        foreach (var tok in parts)
                        {
                            if (tok.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                                current.Name = tok.Substring(5);
                        }
                        if (current.Name == "") current.Name = $"line{lineNum}";
                        break;
                    case "ZONE":
                        if (current != null && parts.Length >= 2) current.Zone = parts[1];
                        break;
                    case "BASEZONE":
                        if (current != null && parts.Length >= 2) current.BaseZone = parts[1];
                        break;
                    case "SEED":
                        if (current != null && parts.Length >= 2)
                        {
                            string s = parts[1];
                            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                            uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out current.Seed);
                        }
                        break;
                    case "START":
                        if (current != null && parts.Length >= 3)
                        {
                            int.TryParse(parts[1], out current.StartX);
                            int.TryParse(parts[2], out current.StartY);
                        }
                        break;
                    case "GOAL":
                        if (current != null && parts.Length >= 3)
                        {
                            int.TryParse(parts[1], out current.GoalX);
                            int.TryParse(parts[2], out current.GoalY);
                        }
                        break;
                    case "DIRECTREACH":
                        if (current != null && parts.Length >= 2) current.DirectReach = parts[1] != "0";
                        break;
                    case "WP":
                        if (current != null && parts.Length >= 3)
                        {
                            int.TryParse(parts[1], out int wx);
                            int.TryParse(parts[2], out int wy);
                            current.Waypoints.Add((wx, wy));
                        }
                        break;
                    case "END":
                        if (current != null) cases.Add(current);
                        current = null;
                        break;
                }
            }
            if (current != null) cases.Add(current);
            return cases;
        }

        internal static string ResolveCapturePath()
        {
            string envPath = Environment.GetEnvironmentVariable("DR_PATHFINDER_CAPTURES");
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;
            return DefaultCapturePath;
        }
    }
}
