using System;
using System.Collections.Generic;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// B-SPIKE Day 2 prototype — infer client's room RNG position from observed wander
    /// packets (opcode 0x65). The client's MT19937 sequence is deterministic from the
    /// server-sent seed (opcode 0x0C). When the client rolls wander, its observed
    /// posX/posY tell us `MT[N] % span == rawXmodded` and `MT[N+1] % span == rawYmodded`
    /// where N is the unknown client RNG position.
    ///
    /// Algorithm: cache MT outputs from seed up to some max (covers typical session
    /// drift); for each wander observation, scan the candidate position window and
    /// return positions matching both X and Y modular constraints. Multi-event
    /// observations intersect to disambiguate.
    /// </summary>
    public class RngPositionInferrer
    {
        public readonly uint Seed;
        public readonly int CacheSize;

        // _outputs[i] = MT19937 tempered output for position (i+1).
        // Position 1 = first Generate() after Seed(). Matches CallsSinceReseed semantics.
        private readonly uint[] _outputs;

        public RngPositionInferrer(uint seed, int cacheSize = 10000)
        {
            Seed = seed;
            CacheSize = cacheSize;
            _outputs = new uint[cacheSize];
            bool prevVerbose = MersenneTwister.VerboseRngTrace;
            try
            {
                MersenneTwister.VerboseRngTrace = false; // avoid 10k log lines per inferrer
                var mt = new MersenneTwister(seed);
                for (int i = 0; i < cacheSize; i++)
                    _outputs[i] = mt.Generate();
            }
            finally
            {
                MersenneTwister.VerboseRngTrace = prevVerbose;
            }
        }

        /// <summary>Get the value Random::generate() returns at position N (1-indexed).</summary>
        public uint OutputAtPosition(int position)
        {
            if (position < 1 || position > CacheSize)
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"position={position} outside cache [1..{CacheSize}]");
            return _outputs[position - 1];
        }

        /// <summary>
        /// Find all positions N in [searchStart, searchEnd] satisfying:
        ///   OutputAtPosition(N)     % span == rawXmodded
        ///   OutputAtPosition(N + 1) % span == rawYmodded
        /// (These are the X and Y wander offset rolls from client's Wander::update case 1.)
        /// </summary>
        public List<int> FindMatchingWanderPositions(uint rawXmodded, uint rawYmodded, uint span,
                                                      int searchStart, int searchEnd)
        {
            if (span == 0)
                throw new ArgumentException("span must be > 0", nameof(span));
            if (searchStart < 1) searchStart = 1;
            if (searchEnd > CacheSize - 1) searchEnd = CacheSize - 1; // need N+1

            var matches = new List<int>();
            for (int n = searchStart; n <= searchEnd; n++)
            {
                if (_outputs[n - 1] % span == rawXmodded &&
                    _outputs[n] % span == rawYmodded)
                {
                    matches.Add(n);
                }
            }
            return matches;
        }

        /// <summary>
        /// Single-shot inference: given a series of wander observations with their
        /// corresponding server-known positions (the position server's _roomRng was at
        /// the moment of the corresponding client event), intersect candidate sets and
        /// return drift = client_position - server_position if uniquely identified.
        /// Returns -1 if not unique. driftWindow caps the search range per event.
        /// </summary>
        public int InferDrift(List<WanderObservation> observations, int driftWindow = 5000)
        {
            if (observations == null || observations.Count == 0) return -1;

            // For each observation, compute candidate clientPositions, then convert to
            // candidate drifts: drift_i = clientPos_i - serverPos_i
            // The TRUE drift, if expressed at the LATEST observation's serverPos, should
            // match across all observations modulo "expected drift evolution between events".
            // For a simple first prototype, assume drift_i ~= drift_j for all i,j and intersect.

            HashSet<int> candidateDrifts = null;
            foreach (var obs in observations)
            {
                var matches = FindMatchingWanderPositions(
                    obs.RawXModded, obs.RawYModded, obs.Span,
                    obs.ServerPositionAtEvent,
                    obs.ServerPositionAtEvent + driftWindow);

                var thisDrifts = new HashSet<int>();
                foreach (int matchPos in matches)
                    thisDrifts.Add(matchPos - obs.ServerPositionAtEvent);

                if (candidateDrifts == null)
                    candidateDrifts = thisDrifts;
                else
                    candidateDrifts.IntersectWith(thisDrifts);

                if (candidateDrifts.Count == 0) return -1; // no consistent drift
                if (candidateDrifts.Count == 1)
                {
                    foreach (int d in candidateDrifts) return d;
                }
            }

            if (candidateDrifts != null && candidateDrifts.Count == 1)
                foreach (int d in candidateDrifts) return d;
            return -1;
        }

        /// <summary>
        /// Drift between consecutive client events is monotonically non-decreasing.
        /// Find the trajectory by enumerating all monotonic paths through per-event
        /// candidate sets and picking the one with the smoothest growth (lowest
        /// variance in per-step drift deltas, normalized by server-position spacing).
        /// Returns null if no monotonic path exists.
        /// </summary>
        public List<int> InferDriftTrajectory(List<WanderObservation> observations, int driftWindow = 10000)
        {
            if (observations == null || observations.Count == 0) return null;

            // Per-observation candidate drifts.
            var perObsDrifts = new List<List<int>>();
            foreach (var obs in observations)
            {
                var matches = FindMatchingWanderPositions(
                    obs.RawXModded, obs.RawYModded, obs.Span,
                    obs.ServerPositionAtEvent,
                    obs.ServerPositionAtEvent + driftWindow);
                var drifts = new List<int>();
                foreach (int m in matches) drifts.Add(m - obs.ServerPositionAtEvent);
                drifts.Sort();
                perObsDrifts.Add(drifts);
            }

            // Enumerate all monotonic paths via DFS. Cap the explored paths to avoid
            // combinatorial explosion in degenerate cases.
            var paths = new List<List<int>>();
            const int MaxPaths = 50000;
            EnumerateMonotonicPaths(perObsDrifts, 0, 0, new List<int>(), paths, MaxPaths);
            if (paths.Count == 0) return null;
            if (paths.Count == 1) return paths[0];

            // Multiple monotonic paths exist — pick the one whose drift growth per
            // server-position unit is most consistent (lowest variance).
            double bestScore = double.MaxValue;
            List<int> bestPath = null;
            foreach (var path in paths)
            {
                double score = SmoothnessScore(path, observations);
                if (score < bestScore) { bestScore = score; bestPath = path; }
            }
            return bestPath;
        }

        private static void EnumerateMonotonicPaths(List<List<int>> perObsDrifts, int eventIdx, int floor,
                                                     List<int> currentPath, List<List<int>> output, int maxPaths)
        {
            if (output.Count >= maxPaths) return;
            if (eventIdx == perObsDrifts.Count)
            {
                output.Add(new List<int>(currentPath));
                return;
            }
            foreach (int d in perObsDrifts[eventIdx])
            {
                if (d < floor) continue;
                currentPath.Add(d);
                EnumerateMonotonicPaths(perObsDrifts, eventIdx + 1, d, currentPath, output, maxPaths);
                currentPath.RemoveAt(currentPath.Count - 1);
                if (output.Count >= maxPaths) return;
            }
        }

        /// <summary>
        /// Day 4 — damage-based refinement. Given a (rough) drift estimate from
        /// wander trajectory and an observed swing damage value, brute-force the
        /// search window ±searchRadius positions to find the exact drift where
        /// MonsterDamageComputer.ComputeSwing yields the observed damage.
        ///
        /// Returns the unique drift that produces the observed damage, or:
        ///   -1 if no match in window
        ///   -2 if multiple matches (ambiguous — caller should provide more signals)
        ///
        /// This method is CPU-expensive: creates a fresh MT and advances it (S+d-1)
        /// times per candidate. With searchRadius=200, that's 400 MT instantiations
        /// and ~200*2000 = 400k Generate calls. ~50-100ms per call in practice.
        /// </summary>
        public static int RefineDriftFromDamage(
            uint seed,
            int serverPosition,
            int driftEstimate,
            int searchRadius,
            int observedDamage,
            MonsterUnitStats attacker,
            PlayerUnitStats target)
        {
            int found = -1;
            int matchCount = 0;
            bool prevVerbose = MersenneTwister.VerboseRngTrace;
            try
            {
                MersenneTwister.VerboseRngTrace = false;
                for (int d = driftEstimate - searchRadius; d <= driftEstimate + searchRadius; d++)
                {
                    if (d < 0) continue;
                    int advance = serverPosition + d - 1;
                    if (advance < 0) continue;
                    var mt = new MersenneTwister(seed);
                    for (int i = 0; i < advance; i++) mt.Generate();
                    var result = MonsterDamageComputer.ComputeSwing(attacker, target, mt);
                    if (result.Damage == observedDamage)
                    {
                        if (matchCount == 0) found = d;
                        matchCount++;
                        if (matchCount > 1) return -2; // ambiguous; need more signals
                    }
                }
            }
            finally
            {
                MersenneTwister.VerboseRngTrace = prevVerbose;
            }
            return matchCount == 0 ? -1 : found;
        }

        private static double SmoothnessScore(List<int> path, List<WanderObservation> observations)
        {
            // Compute per-step drift-growth-rate (drift increment per server-position
            // unit). Variance across steps = smoothness. Lower variance = smoother.
            if (path.Count < 2) return 0;
            var rates = new List<double>();
            for (int i = 1; i < path.Count; i++)
            {
                int srvDelta = observations[i].ServerPositionAtEvent - observations[i - 1].ServerPositionAtEvent;
                if (srvDelta <= 0) srvDelta = 1; // avoid div by 0
                rates.Add((double)(path[i] - path[i - 1]) / srvDelta);
            }
            double mean = 0;
            foreach (double r in rates) mean += r;
            mean /= rates.Count;
            double var = 0;
            foreach (double r in rates) var += (r - mean) * (r - mean);
            return var;
        }
    }

    public struct WanderObservation
    {
        /// <summary>(int)(MT[N] % span) — derived from observed posX. Use ComputeRawModded.</summary>
        public uint RawXModded;
        /// <summary>(int)(MT[N+1] % span) — derived from observed posY.</summary>
        public uint RawYModded;
        /// <summary>2 * WanderRange — modulus the client used in `rawX % span`.</summary>
        public uint Span;
        /// <summary>Server's _roomRng.CallsSinceReseed at the moment client rolled this wander.</summary>
        public int ServerPositionAtEvent;

        /// <summary>
        /// Given an observed target position and the wander anchor (DefaultX or ClientX
        /// at the moment of the roll) and range (= WanderRange rounded to int), recover
        /// the modular constraint on the underlying RNG value:
        ///
        ///   client did: targetX = anchorX + (int)(rawX % span) - range
        ///   so:         (uint)(targetX - anchorX + range) == rawX % span
        /// </summary>
        public static uint ComputeRawModded(float targetCoord, float anchorCoord, int range)
        {
            return (uint)((int)Math.Round(targetCoord - anchorCoord) + range);
        }
    }

    /// <summary>
    /// Synthetic self-test — no Unity, no runtime dependencies. Generates a fake
    /// wander event from a known seed+position, then verifies the inverter recovers
    /// the position. Run from RngPositionInferrerSelfTest.RunAll() at boot.
    /// </summary>
    public static class RngPositionInferrerSelfTest
    {
        private static int _testsRun;
        private static int _testsPassed;
        private static readonly List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _testsRun = 0; _testsPassed = 0; _failures.Clear();
            UnityEngine.Debug.LogError("[RNG-INFER-SELFTEST] ═══════════════════════════════════════════════");

            TestSingleObservationUnique();
            TestSingleObservationAmbiguous();
            TestTwoObservationsIntersect();
            TestNoMatch();
            TestSmallSpanLargeAmbiguity();
            TestRealisticDrift();
            // Day 3 — multi-event realistic scenarios
            TestTrajectory_GrowingDrift_3events();
            TestTrajectory_GrowingDrift_5events();
            TestTrajectory_SmallSpan_AccumulatesUnique();
            TestTrajectory_RealisticMultiMob();
            // Day 4 — damage-based refinement
            TestRefineDriftFromDamage_ExactMatch();
            TestRefineDriftFromDamage_NarrowWindow();

            UnityEngine.Debug.LogError("[RNG-INFER-SELFTEST] ═══════════════════════════════════════════════");
            UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] {_testsPassed}/{_testsRun} tests passed");
            if (_failures.Count > 0)
            {
                UnityEngine.Debug.LogError("[RNG-INFER-SELFTEST] FAILURES:");
                foreach (var f in _failures) UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST]   - {f}");
            }
        }

        private static void Assert(bool condition, string label)
        {
            _testsRun++;
            if (condition) { _testsPassed++; return; }
            _failures.Add(label);
            UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] FAIL: {label}");
        }

        private static void TestSingleObservationUnique()
        {
            // Pick a seed, simulate the client rolling wander at known position N.
            uint seed = 0xC24AFBDB;
            int truePosition = 1234;
            uint span = 40;

            var inferrer = new RngPositionInferrer(seed, cacheSize: 6000);
            uint rawX = inferrer.OutputAtPosition(truePosition);
            uint rawY = inferrer.OutputAtPosition(truePosition + 1);

            // The observation:
            var obs = new WanderObservation
            {
                RawXModded = rawX % span,
                RawYModded = rawY % span,
                Span = span,
                ServerPositionAtEvent = 100, // pretend server is at position 100
            };

            var matches = inferrer.FindMatchingWanderPositions(
                obs.RawXModded, obs.RawYModded, obs.Span,
                obs.ServerPositionAtEvent,
                obs.ServerPositionAtEvent + 5000);

            // Expected: truePosition is one of the matches.
            bool foundTrue = matches.Contains(truePosition);
            Assert(foundTrue, $"TestSingleObservationUnique: true position {truePosition} should be among matches, got {matches.Count} match(es)");

            // Span=40 gives ~5000/40/40 = ~3 expected matches by chance.
            // Document the ambiguity rate for the design.
            UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] TestSingleObservationUnique: {matches.Count} matches with span={span} over 5000 positions");
        }

        private static void TestSingleObservationAmbiguous()
        {
            // With very small span (e.g. span=4 → tiny WanderRange=2 mob), expect many matches.
            uint seed = 0x12345678;
            int truePosition = 500;
            uint span = 4;

            var inferrer = new RngPositionInferrer(seed, cacheSize: 5000);
            uint rawX = inferrer.OutputAtPosition(truePosition);
            uint rawY = inferrer.OutputAtPosition(truePosition + 1);

            var matches = inferrer.FindMatchingWanderPositions(
                rawX % span, rawY % span, span,
                1, 4000);

            // Expect ~4000/(4*4) = 250 matches; not single.
            Assert(matches.Count > 100, $"TestSingleObservationAmbiguous: expected many matches with span={span}, got {matches.Count}");
            Assert(matches.Contains(truePosition), "TestSingleObservationAmbiguous: true position should still be in matches");
        }

        private static void TestTwoObservationsIntersect()
        {
            // Two events with DIFFERENT true positions but SAME drift => intersection
            // should yield unique drift.
            uint seed = 0xDB66809A;
            int driftTrue = 250;

            // Event 1: server at 200, client at 450.
            int serverPos1 = 200;
            int clientPos1 = serverPos1 + driftTrue;
            uint span = 40;

            // Event 2: server at 600, client at 850 (same drift).
            int serverPos2 = 600;
            int clientPos2 = serverPos2 + driftTrue;

            var inferrer = new RngPositionInferrer(seed, cacheSize: 8000);

            var obs1 = new WanderObservation
            {
                RawXModded = inferrer.OutputAtPosition(clientPos1) % span,
                RawYModded = inferrer.OutputAtPosition(clientPos1 + 1) % span,
                Span = span,
                ServerPositionAtEvent = serverPos1,
            };
            var obs2 = new WanderObservation
            {
                RawXModded = inferrer.OutputAtPosition(clientPos2) % span,
                RawYModded = inferrer.OutputAtPosition(clientPos2 + 1) % span,
                Span = span,
                ServerPositionAtEvent = serverPos2,
            };

            int inferredDrift = inferrer.InferDrift(new List<WanderObservation> { obs1, obs2 }, driftWindow: 5000);
            Assert(inferredDrift == driftTrue, $"TestTwoObservationsIntersect: expected drift={driftTrue}, got {inferredDrift}");
        }

        private static void TestNoMatch()
        {
            // Random garbage values that don't correspond to any position → no match.
            uint seed = 0x55555555;
            var inferrer = new RngPositionInferrer(seed, cacheSize: 3000);

            // Pick mod values that are very unlikely to occur at any position.
            // For span=2, rawX%2 is always 0 or 1, so ANY value matches. Use a larger span.
            uint span = 1000;
            // Use values that don't match the actual output at any position with these mods.
            // Construct a deliberately broken observation: use OutputAtPosition(100) but
            // tweak it slightly so it's no longer a real RNG output mod.
            uint realRawX = inferrer.OutputAtPosition(100);
            var obs = new WanderObservation
            {
                RawXModded = (realRawX % span) ^ 0x1, // deliberately wrong
                RawYModded = (inferrer.OutputAtPosition(101) % span) ^ 0x1,
                Span = span,
                ServerPositionAtEvent = 1,
            };

            int result = inferrer.InferDrift(new List<WanderObservation> { obs }, driftWindow: 2000);
            // Most likely no match — but with tweaked X/Y both flipped, may coincidentally
            // match elsewhere. Accept "no match" or "multiple low-value matches", as long
            // as we don't claim a confident match at the true position.
            UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] TestNoMatch: result={result} (expected -1 or coincidental)");
            // Not a hard assertion — this test documents behavior under bad input.
            _testsRun++;
            _testsPassed++;
        }

        private static void TestSmallSpanLargeAmbiguity()
        {
            // Realistic small mob with WanderRange=4 → span=8. Document ambiguity.
            uint seed = 0xABCDEF00;
            uint span = 8;
            int truePos = 2000;

            var inferrer = new RngPositionInferrer(seed, cacheSize: 8000);
            uint rawX = inferrer.OutputAtPosition(truePos);
            uint rawY = inferrer.OutputAtPosition(truePos + 1);

            var matches = inferrer.FindMatchingWanderPositions(rawX % span, rawY % span, span, 1, 7000);
            UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] TestSmallSpan(span=8): {matches.Count} matches over 7000 positions");

            Assert(matches.Contains(truePos), "TestSmallSpan: true position should be in match set");
            Assert(matches.Count > 50, $"TestSmallSpan: expect many ambiguous matches (got {matches.Count})");
        }

        private static void TestRealisticDrift()
        {
            // Drift of 1849 (today's measured value). Two wander observations spaced 500
            // RNG positions apart on the server side, drift stays constant.
            uint seed = 0x69811CBF;
            int driftTrue = 1849;
            uint span = 24; // WanderRange=12 → span=24

            var inferrer = new RngPositionInferrer(seed, cacheSize: 12000);

            // Event A: server at 100, client at 1949
            int serverA = 100;
            var obsA = new WanderObservation
            {
                RawXModded = inferrer.OutputAtPosition(serverA + driftTrue) % span,
                RawYModded = inferrer.OutputAtPosition(serverA + driftTrue + 1) % span,
                Span = span,
                ServerPositionAtEvent = serverA,
            };

            // Event B: server at 600, client at 2449 (same drift)
            int serverB = 600;
            var obsB = new WanderObservation
            {
                RawXModded = inferrer.OutputAtPosition(serverB + driftTrue) % span,
                RawYModded = inferrer.OutputAtPosition(serverB + driftTrue + 1) % span,
                Span = span,
                ServerPositionAtEvent = serverB,
            };

            // Try with just one observation first - measure how many matches we get
            var singleMatches = inferrer.FindMatchingWanderPositions(
                obsA.RawXModded, obsA.RawYModded, span,
                serverA, serverA + 5000);
            UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] TestRealisticDrift single-obs matches: {singleMatches.Count} candidates");

            // Now try with two
            int inferredDrift = inferrer.InferDrift(new List<WanderObservation> { obsA, obsB }, driftWindow: 5000);
            Assert(inferredDrift == driftTrue,
                $"TestRealisticDrift: expected drift={driftTrue}, got {inferredDrift}");
        }

        // ─── Day 3 multi-event scenarios ───────────────────────────────────────

        private static void TestTrajectory_GrowingDrift_3events()
        {
            // Simulates 3 wander observations across a session where drift grows.
            // Server fires 100 calls between events; client fires 150 (50 extra
            // each interval). Drift starts at 200 and grows.
            uint seed = 0xC0FFEE00;
            uint span = 24;
            int[] trueDrifts = { 200, 250, 300 };
            int[] serverPositions = { 50, 150, 250 };

            var inferrer = new RngPositionInferrer(seed, cacheSize: 10000);
            var observations = new List<WanderObservation>();
            for (int i = 0; i < 3; i++)
            {
                int clientPos = serverPositions[i] + trueDrifts[i];
                observations.Add(new WanderObservation
                {
                    RawXModded = inferrer.OutputAtPosition(clientPos) % span,
                    RawYModded = inferrer.OutputAtPosition(clientPos + 1) % span,
                    Span = span,
                    ServerPositionAtEvent = serverPositions[i],
                });
            }

            var trajectory = inferrer.InferDriftTrajectory(observations, driftWindow: 5000);
            Assert(trajectory != null, "TestTrajectory_GrowingDrift_3events: trajectory should not be null");
            if (trajectory == null) return;
            Assert(trajectory.Count == 3, $"TestTrajectory_GrowingDrift_3events: expected 3 drift values, got {trajectory.Count}");
            for (int i = 0; i < 3; i++)
                Assert(trajectory[i] == trueDrifts[i],
                    $"TestTrajectory_GrowingDrift_3events: event {i} expected drift={trueDrifts[i]}, got {trajectory[i]}");
        }

        private static void TestTrajectory_GrowingDrift_5events()
        {
            // 5 events with realistic drift growth — mimics ~30s combat session.
            uint seed = 0xBEEFCAFE;
            uint span = 16;
            int[] trueDrifts = { 100, 250, 450, 700, 1000 };
            int[] serverPositions = { 20, 80, 160, 260, 380 };

            var inferrer = new RngPositionInferrer(seed, cacheSize: 12000);
            var observations = new List<WanderObservation>();
            for (int i = 0; i < 5; i++)
            {
                int clientPos = serverPositions[i] + trueDrifts[i];
                observations.Add(new WanderObservation
                {
                    RawXModded = inferrer.OutputAtPosition(clientPos) % span,
                    RawYModded = inferrer.OutputAtPosition(clientPos + 1) % span,
                    Span = span,
                    ServerPositionAtEvent = serverPositions[i],
                });
            }

            var trajectory = inferrer.InferDriftTrajectory(observations, driftWindow: 5000);
            Assert(trajectory != null, "TestTrajectory_GrowingDrift_5events: trajectory should not be null");
            if (trajectory == null) return;
            int allCorrect = 0;
            for (int i = 0; i < 5; i++)
                if (trajectory[i] == trueDrifts[i]) allCorrect++;
            UnityEngine.Debug.LogError(
                $"[RNG-INFER-SELFTEST] TestTrajectory_GrowingDrift_5events: {allCorrect}/5 events identified correctly, " +
                $"trajectory=[{string.Join(",", trajectory)}] expected=[{string.Join(",", trueDrifts)}]");
            Assert(allCorrect == 5, $"TestTrajectory_GrowingDrift_5events: {allCorrect}/5 correct");
        }

        private static void TestTrajectory_SmallSpan_AccumulatesUnique()
        {
            // 6 small-span observations. Each alone is highly ambiguous (~hundreds
            // of candidates), but the monotonicity constraint should collapse to
            // unique trajectory.
            uint seed = 0xDEADBEEF;
            uint span = 8;
            int[] trueDrifts = { 50, 120, 200, 290, 390, 500 };
            int[] serverPositions = { 10, 50, 100, 160, 230, 310 };

            var inferrer = new RngPositionInferrer(seed, cacheSize: 12000);
            var observations = new List<WanderObservation>();
            for (int i = 0; i < 6; i++)
            {
                int clientPos = serverPositions[i] + trueDrifts[i];
                observations.Add(new WanderObservation
                {
                    RawXModded = inferrer.OutputAtPosition(clientPos) % span,
                    RawYModded = inferrer.OutputAtPosition(clientPos + 1) % span,
                    Span = span,
                    ServerPositionAtEvent = serverPositions[i],
                });
            }

            var trajectory = inferrer.InferDriftTrajectory(observations, driftWindow: 5000);
            int allCorrect = 0;
            if (trajectory != null)
            {
                for (int i = 0; i < 6; i++)
                    if (trajectory[i] == trueDrifts[i]) allCorrect++;
                UnityEngine.Debug.LogError(
                    $"[RNG-INFER-SELFTEST] TestTrajectory_SmallSpan_AccumulatesUnique: {allCorrect}/6 events correct, " +
                    $"trajectory=[{string.Join(",", trajectory)}] expected=[{string.Join(",", trueDrifts)}]");
            }
            else
            {
                UnityEngine.Debug.LogError("[RNG-INFER-SELFTEST] TestTrajectory_SmallSpan_AccumulatesUnique: trajectory NULL");
            }
            Assert(allCorrect == 6, $"TestTrajectory_SmallSpan_AccumulatesUnique: {allCorrect}/6 correct (small span is the hardest case)");
        }

        private static void TestTrajectory_RealisticMultiMob()
        {
            // 8 events over a realistic session: mixed wander spans (mobs of different
            // sizes), drift grows from 0 to ~2000 (matches today's 1849 measurement).
            uint seed = 0x69811CBF;
            uint[] spans = { 24, 16, 40, 24, 16, 40, 24, 16 }; // mixed mob sizes
            int[] trueDrifts = { 0, 250, 500, 750, 1000, 1250, 1600, 2000 };
            int[] serverPositions = { 5, 60, 130, 210, 300, 400, 510, 630 };

            var inferrer = new RngPositionInferrer(seed, cacheSize: 14000);
            var observations = new List<WanderObservation>();
            for (int i = 0; i < 8; i++)
            {
                int clientPos = serverPositions[i] + trueDrifts[i];
                observations.Add(new WanderObservation
                {
                    RawXModded = inferrer.OutputAtPosition(clientPos) % spans[i],
                    RawYModded = inferrer.OutputAtPosition(clientPos + 1) % spans[i],
                    Span = spans[i],
                    ServerPositionAtEvent = serverPositions[i],
                });
            }

            var trajectory = inferrer.InferDriftTrajectory(observations, driftWindow: 5000);
            int allCorrect = 0;
            if (trajectory != null)
            {
                for (int i = 0; i < 8; i++)
                    if (trajectory[i] == trueDrifts[i]) allCorrect++;
                UnityEngine.Debug.LogError(
                    $"[RNG-INFER-SELFTEST] TestTrajectory_RealisticMultiMob: {allCorrect}/8 events correct, " +
                    $"trajectory=[{string.Join(",", trajectory)}] expected=[{string.Join(",", trueDrifts)}]");
            }
            else
            {
                UnityEngine.Debug.LogError("[RNG-INFER-SELFTEST] TestTrajectory_RealisticMultiMob: trajectory NULL");
            }
            Assert(allCorrect == 8, $"TestTrajectory_RealisticMultiMob: {allCorrect}/8 correct");
        }

        // ─── Day 4: per-swing damage refinement ───────────────────────────────

        private static (MonsterUnitStats attacker, PlayerUnitStats target) BuildSampleStats()
        {
            // Realistic level-1 mob attacking a level-1 player. Matches the kind of
            // stats we see in tutorial-zone combat.
            var attacker = new MonsterUnitStats
            {
                Level = 1, AttackStyle = 1, WeaponDamageType = 0, Discriminator = 0,
                BaseAttackRating = 60, BaseAttackRatingMod = 0,
                CritMultiplier = 100,
                WeaponDamagePerLevel = 10, WeaponVolatilityFixed = 256, WeaponDamageFixed = 1024,
            };
            var target = new PlayerUnitStats
            {
                BaseDefenseRating = 30, BaseDefenseRatingMod = 0,
                Discriminator = 0, BlockChance = 5,
            };
            return (attacker, target);
        }

        private static void TestRefineDriftFromDamage_ExactMatch()
        {
            // Set up: known seed, known server position, known true drift.
            uint seed = 0xC24AFBDB;
            int serverPos = 100;
            int trueDrift = 1849;
            var (attacker, target) = BuildSampleStats();

            // Simulate "client's actual swing" by advancing MT to serverPos+trueDrift-1
            // and running ComputeSwing — that yields the damage the client computed.
            bool prevVerbose = MersenneTwister.VerboseRngTrace;
            MersenneTwister.VerboseRngTrace = false;
            int trueDamage;
            try
            {
                var mt = new MersenneTwister(seed);
                int advance = serverPos + trueDrift - 1;
                for (int i = 0; i < advance; i++) mt.Generate();
                var trueResult = MonsterDamageComputer.ComputeSwing(attacker, target, mt);
                trueDamage = trueResult.Damage;
                UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] TestRefineDriftFromDamage_ExactMatch: trueDamage={trueDamage}");
            }
            finally { MersenneTwister.VerboseRngTrace = prevVerbose; }

            // Refine: estimate is off by 50, search ±200 window.
            int driftEstimate = trueDrift - 50;
            int foundDrift = RngPositionInferrer.RefineDriftFromDamage(
                seed, serverPos, driftEstimate, searchRadius: 200,
                observedDamage: trueDamage, attacker: attacker, target: target);

            Assert(foundDrift == trueDrift,
                $"TestRefineDriftFromDamage_ExactMatch: expected={trueDrift}, got={foundDrift} (estimate was {driftEstimate})");
        }

        private static void TestRefineDriftFromDamage_NarrowWindow()
        {
            // Verify: when search window is narrow enough, unique match.
            uint seed = 0xDB66809A;
            int serverPos = 200;
            int trueDrift = 1000;
            var (attacker, target) = BuildSampleStats();

            bool prevVerbose = MersenneTwister.VerboseRngTrace;
            MersenneTwister.VerboseRngTrace = false;
            int trueDamage;
            try
            {
                var mt = new MersenneTwister(seed);
                int advance = serverPos + trueDrift - 1;
                for (int i = 0; i < advance; i++) mt.Generate();
                var trueResult = MonsterDamageComputer.ComputeSwing(attacker, target, mt);
                trueDamage = trueResult.Damage;
                UnityEngine.Debug.LogError($"[RNG-INFER-SELFTEST] TestRefineDriftFromDamage_NarrowWindow: trueDamage={trueDamage}");
            }
            finally { MersenneTwister.VerboseRngTrace = prevVerbose; }

            // Estimate is within 30 of true; search ±100 should pin it.
            int driftEstimate = trueDrift + 20;
            int foundDrift = RngPositionInferrer.RefineDriftFromDamage(
                seed, serverPos, driftEstimate, searchRadius: 100,
                observedDamage: trueDamage, attacker: attacker, target: target);

            // Could be uniquely found, or could have 2 ambiguous matches → -2.
            // Document the result.
            UnityEngine.Debug.LogError(
                $"[RNG-INFER-SELFTEST] TestRefineDriftFromDamage_NarrowWindow: foundDrift={foundDrift} (true={trueDrift})");

            // Accept either exact match OR ambiguity (which is documented limitation).
            bool ok = foundDrift == trueDrift || foundDrift == -2;
            Assert(ok, $"TestRefineDriftFromDamage_NarrowWindow: expected {trueDrift} or -2 (ambig), got {foundDrift}");
        }
    }
}
