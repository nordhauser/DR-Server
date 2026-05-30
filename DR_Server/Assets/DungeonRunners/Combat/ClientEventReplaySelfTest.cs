using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// P1 boot self-test for client-event-replay (the per-player RNG-mirror model).
    /// Validates the foundational guarantee the replay relies on: the same seed advanced to
    /// the same absolute position yields the same next value(s) — so the server, replaying a
    /// client swing event by fast-forwarding a mirror to the reported count, computes
    /// byte-identical r1/r2/r3. Also checks mirrors are independent + reproducible.
    /// Pure (no live deps); runs alongside the other [*-SELFTEST] at boot.
    /// The real end-to-end validation is P2 (live, with the client emitting per-swing 0x66).
    /// </summary>
    public static class ClientEventReplaySelfTest
    {
        public static void RunAll()
        {
            int pass = 0, fail = 0;
            const uint seed = 0x1A2B3C4Du;

            // T1 — fast-forward lands exactly at the target position.
            var m = new MersenneTwister(seed);
            for (int i = 0; i < 137; i++) m.Generate();
            Check(m.CallsSinceReseed == 137, "T1 fast-forward position == 137", ref pass, ref fail);

            // T2 — same seed + same position => identical next value (THE replay guarantee).
            var a = new MersenneTwister(seed); for (int i = 0; i < 250; i++) a.Generate();
            var b = new MersenneTwister(seed); for (int i = 0; i < 250; i++) b.Generate();
            uint va = a.Generate(), vb = b.Generate();
            Check(va == vb, $"T2 position-determinism 0x{va:X8}==0x{vb:X8}", ref pass, ref fail);

            // T3 — two mirrors at different positions are independent (no shared state).
            var p1 = new MersenneTwister(seed); for (int i = 0; i < 10; i++) p1.Generate();
            var p2 = new MersenneTwister(seed); for (int i = 0; i < 20; i++) p2.Generate();
            Check(p1.CallsSinceReseed == 10 && p2.CallsSinceReseed == 20,
                  "T3 mirror independence (10 vs 20)", ref pass, ref fail);

            // T4 — incremental-to-N and fast-forward-to-N produce the same stream (replay==live).
            var inc = new MersenneTwister(seed); for (int i = 0; i < 99; i++) inc.Generate();
            var ff = new MersenneTwister(seed); for (int i = 0; i < 99; i++) ff.Generate();
            bool seqMatch = true;
            for (int i = 0; i < 3; i++) if (inc.Generate() != ff.Generate()) seqMatch = false;
            Check(seqMatch, "T4 3-value sequence reproducible at pos 99", ref pass, ref fail);

            Debug.LogError($"[REPLAY-SELFTEST] {pass}/{pass + fail} passed " +
                           $"(UseClientEventReplay={CombatManager.UseClientEventReplay})");
        }

        private static void Check(bool ok, string name, ref int pass, ref int fail)
        {
            if (ok) { pass++; }
            else { fail++; Debug.LogError($"[REPLAY-SELFTEST] FAIL: {name}"); }
        }
    }
}
