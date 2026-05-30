using System;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Mersenne Twister RNG (MT19937) - Exact match to Dungeon Runners client implementation.
    /// 
    /// From Ghidra analysis:
    /// - State size: 0x270 (624) uint32 values
    /// - Magic constant: 0x6c078965
    /// - Native Dungeon Runners tempering masks at Random::generate(char const*) 0x0044B1F0
    /// 
    /// Server MUST send seed via opcode 0x0C and use this class for damage calculations
    /// to match client's Random::generate() output exactly.
    /// </summary>
    public class MersenneTwister
    {
        // MT19937 constants
        private const int N = 624;           // State size (0x270)
        private const int M = 397;           // Shift size (0x18D)
        private const uint MATRIX_A = 0x9908B0DF;   // Constant vector
        private const uint UPPER_MASK = 0x80000000; // Most significant bit
        private const uint LOWER_MASK = 0x7FFFFFFF; // Least significant 31 bits

        // Tempering constants (from Ghidra: generate function)
        private const uint TEMPERING_MASK_B = 0xFF3A58AD;  // From: uVar1 ^ (uVar1 & 0xff3a58ad) << 7
        private const uint TEMPERING_MASK_C = 0xFFFFDF8C;  // From: uVar1 ^ (uVar1 & 0xffffdf8c) << 0xf

        // State array and index
        private uint[] _mt = new uint[N];
        private int _mti = N + 1;  // mti == N+1 means uninitialized

        // Session 24: RNG diagnostics for sync tuning
        public uint LastSeed { get; private set; }
        public int CallsSinceReseed { get; private set; }
        public uint LastGeneratedValue { get; private set; }

        // 2026-05-23 RNG-divergence diagnostic. When true, every Generate() call logs
        // [RNG-TRACE] with pos, caller (first non-MersenneTwister stack frame), and value.
        // Compare against client x32dbg log breakpoint on Random::generate @ 0x0044B1F0 to
        // find the first call where server and client diverge.
        // Stack-trace lookups are slow — keep off in production. Flip to true for a single
        // test session, capture server.log, then flip back.
        // DISABLED 2026-05-29: per-Generate() logging + stack-trace lookup floods Unity's
        // console during fast-forward (consumed 100s-1000s of generates per swing) and freezes
        // the editor. The RNG-sync diagnostic that needed this is complete. Re-enable ONLY for a
        // single short capture with NO fast-forward active.
        public static bool VerboseRngTrace = false;

        /// <summary>
        /// Create uninitialized MT - must call Seed() before Generate()
        /// </summary>
        public MersenneTwister()
        {
        }

        /// <summary>
        /// Create and seed MT
        /// </summary>
        public MersenneTwister(uint seed)
        {
            Seed(seed);
        }

        /// <summary>
        /// Seed the RNG - matches client's Random::seed() exactly
        /// 
        /// From Ghidra:
        /// *(Random **)(in_EAX + 0x14) = this;  // mt[0] = seed
        /// *(undefined4 *)(in_EAX + 0x10) = 1;  // start at index 1
        /// do {
        ///     iVar1 = *(int *)(in_EAX + 0x10);
        ///     uVar2 = *(uint *)(in_EAX + 0x10 + iVar1 * 4);
        ///     *(uint *)(in_EAX + 0x14 + iVar1 * 4) = (uVar2 >> 0x1e ^ uVar2) * 0x6c078965 + iVar1;
        ///     *(int *)(in_EAX + 0x10) = *(int *)(in_EAX + 0x10) + 1;
        /// } while (*(int *)(in_EAX + 0x10) < 0x270);
        /// </summary>
        public void Seed(uint seed)
        {
            LastSeed = seed;
            CallsSinceReseed = 0;
            _mt[0] = seed;
            for (int i = 1; i < N; i++)
            {
                // mt[i] = (mt[i-1] ^ (mt[i-1] >> 30)) * 0x6c078965 + i
                _mt[i] = (uint)(0x6c078965 * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + i);
            }
            _mti = N; // Force regeneration on first generate call

            if (VerboseRngTrace)
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                string callerName = "?";
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    var dt = m.DeclaringType;
                    if (dt == typeof(MersenneTwister)) continue;
                    callerName = (dt != null ? dt.Name : "?") + "." + m.Name;
                    break;
                }
                UnityEngine.Debug.LogError($"[RNG-TRACE] SEED 0x{seed:X8} caller={callerName}");
            }
        }

        /// <summary>
        /// Generate random uint32 - matches client's Random::generate() exactly
        /// </summary>
        public uint Generate()
        {
            uint y;
            uint[] mag01 = { 0, MATRIX_A };

            // Generate N words at a time
            if (_mti >= N)
            {
                int kk;

                // If seed() hasn't been called, use default seed
                if (_mti == N + 1)
                {
                    Seed(0x1105); // Default seed from Ghidra: seed((Random *)0x1105,0)
                }

                // First loop: 0 to N-M-1 (0 to 226)
                // From Ghidra: iVar3 = 0xe3 (227 iterations)
                for (kk = 0; kk < N - M; kk++)
                {
                    y = (_mt[kk] & UPPER_MASK) | (_mt[kk + 1] & LOWER_MASK);
                    _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ mag01[y & 1];
                }

                // Second loop: N-M to N-2 (227 to 622)  
                // From Ghidra: iVar3 = 0x18c (396 iterations)
                for (; kk < N - 1; kk++)
                {
                    y = (_mt[kk] & UPPER_MASK) | (_mt[kk + 1] & LOWER_MASK);
                    _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ mag01[y & 1];
                }

                // Last element
                y = (_mt[N - 1] & UPPER_MASK) | (_mt[0] & LOWER_MASK);
                _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ mag01[y & 1];

                _mti = 0;
            }

            // Get next value from state
            y = _mt[_mti++];

            // Tempering - matches Random::generate(char const*) at 0x0044B1F0:
            // uVar1 = uVar1 ^ uVar1 >> 0xb;
            // uVar1 = uVar1 ^ (uVar1 & 0xff3a58ad) << 7;
            // uVar1 = uVar1 ^ (uVar1 & 0xffffdf8c) << 0xf;
            // return uVar1 >> 0x12 ^ uVar1;
            y ^= (y >> 11);
            y ^= (y & TEMPERING_MASK_B) << 7;
            y ^= (y & TEMPERING_MASK_C) << 15;
            y ^= (y >> 18);

            CallsSinceReseed++;
            LastGeneratedValue = y;

            if (VerboseRngTrace)
            {
                // First non-MersenneTwister stack frame = the caller we care about.
                // (frame 0 is Generate() itself, then potentially Generate(uint,uint) etc.)
                var st = new System.Diagnostics.StackTrace(1, false);
                string callerName = "?";
                string callerFrame2 = "";
                int outFrames = 0;
                for (int i = 0; i < st.FrameCount && outFrames < 2; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    var dt = m.DeclaringType;
                    if (dt == typeof(MersenneTwister)) continue;
                    string name = (dt != null ? dt.Name : "?") + "." + m.Name;
                    if (outFrames == 0) callerName = name;
                    else callerFrame2 = " <- " + name;
                    outFrames++;
                }
                UnityEngine.Debug.LogError($"[RNG-TRACE] pos={CallsSinceReseed} val=0x{y:X8} caller={callerName}{callerFrame2}");
            }

            return y;
        }

        /// <summary>
        /// Generate random number in range [min, max] inclusive
        /// Matches client's Random::generate(ulong, ulong)
        /// 
        /// From Ghidra:
        /// uVar1 = generate(this,unaff_EDI);
        /// uVar2 = (in_EAX - unaff_EBX) + 1;  // range = max - min + 1
        /// if (uVar2 != 0) {
        ///     return uVar1 % uVar2 + unaff_EBX;  // result % range + min
        /// }
        /// return unaff_EBX;
        /// </summary>
        public uint Generate(uint min, uint max)
        {
            uint range = max - min + 1;
            if (range == 0)
                return min;
            return (Generate() % range) + min;
        }

        /// <summary>
        /// Generate random int in range [min, max] inclusive
        /// Convenience method for damage calculations
        /// </summary>
        public int GenerateInt(int min, int max)
        {
            if (max < min)
                return min;
            uint range = (uint)(max - min + 1);
            return (int)((Generate() % range) + (uint)min);
        }
    }

    public static class NativeRandomStreams
    {
        private static readonly MersenneTwister _globalSoundRng = new MersenneTwister();

        public static uint GenerateGlobalSound()
        {
            return _globalSoundRng.Generate();
        }

        public static int GlobalSoundCalls => _globalSoundRng.CallsSinceReseed;
        public static uint GlobalSoundSeed => _globalSoundRng.LastSeed;
    }
}
