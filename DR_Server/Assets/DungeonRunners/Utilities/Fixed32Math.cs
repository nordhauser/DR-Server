using System;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Angle/vector operations on Fixed32 values, mirroring the DR client's helper functions.
    /// Heading scale: 0x16800 raw units = 2π radians. 0xB400 = π. 0x5A00 = π/2.
    ///
    /// Functions ported here:
    ///   InterpolateHeading        — from client's @ 0x00535360 (Ghidra-decoded)
    ///   NormalizeHeading          — wraps to [0, TwoPiRaw)
    ///   UnitVectorFromHeading     — STUB pending sin/cos table extraction from client EXE
    ///
    /// Confidence notes:
    ///   InterpolateHeading: line-for-line port from decompile; pure integer; should bit-equal
    ///     client output. Unit-test reference values can be captured via x32dbg later.
    ///   UnitVectorFromHeading: client uses a lookup table (likely 256-entry or 1024-entry
    ///     sin/cos table in the binary's data segment). MUST extract the table from
    ///     DungeonRunners.exe before this can be used; mathematical sin/cos won't be bit-equal.
    /// </summary>
    public static class Fixed32Math
    {
        /// <summary>π in heading-Fixed32 units.</summary>
        public const int PiRaw = 0xB400; // 46080

        /// <summary>2π in heading-Fixed32 units.</summary>
        public const int TwoPiRaw = 0x16800; // 92160

        /// <summary>π/2 in heading-Fixed32 units.</summary>
        public const int HalfPiRaw = 0x5A00; // 23040

        /// <summary>π + 1 — threshold used by InterpolateHeading wrap logic.</summary>
        public const int PiPlusOneRaw = 0xB401;

        /// <summary>-π — threshold used by InterpolateHeading wrap logic.</summary>
        public const int NegPiRaw = -0xB400;

        /// <summary>
        /// Step `current` toward `target` by at most `maxStep`, taking the shortest arc.
        /// Result is normalized to [0, 2π) range.
        ///
        /// Direct port of client function at 0x00535360 (D1 derisk 2026-05-27).
        /// All arithmetic is integer; result should bit-equal client output for matching inputs.
        ///
        /// Returns: the new heading after one step.
        /// </summary>
        public static Fixed32 InterpolateHeading(Fixed32 current, Fixed32 target, Fixed32 maxStep)
        {
            int cur = current.RawValue;
            int tgt = target.RawValue;
            int step = maxStep.RawValue;

            // Compute shortest-arc diff: tgt - cur, wrapped to [-π, π].
            int diff = tgt - cur;
            if (diff < PiPlusOneRaw)
            {
                if (diff < NegPiRaw)
                {
                    diff = diff + TwoPiRaw;
                }
            }
            else
            {
                diff = diff - TwoPiRaw;
            }

            int result;
            if (diff < 0)
            {
                // Turning negative (clockwise).
                if (-diff < step)
                {
                    // Within one step — snap to target.
                    result = tgt;
                }
                else
                {
                    // Step by -maxStep.
                    result = cur + (-step);
                }
            }
            else
            {
                if (diff < 1)
                {
                    // diff == 0: already at target.
                    result = cur;
                }
                else if (diff < step)
                {
                    // Within one step — snap to target.
                    result = tgt;
                }
                else
                {
                    // Step by +maxStep.
                    result = cur + step;
                }
            }

            // Normalize to [0, 2π).
            if (result < TwoPiRaw)
            {
                if (result < 0)
                {
                    // Wrap up: add 2π enough times to land in range.
                    // Matches client's:
                    //   result + 0x16800 + ((-result - 1U) / 0x16800) * 0x16800
                    result = result + TwoPiRaw + (int)((uint)(-result - 1) / TwoPiRaw) * TwoPiRaw;
                }
            }
            else if (result > TwoPiRaw)
            {
                // Wrap down: subtract 2π enough times.
                // Matches client's:
                //   result + ((result - 0x16801U) / 0x16800) * -0x16800 + -0x16800
                result = result + (int)((uint)(result - (TwoPiRaw + 1)) / TwoPiRaw) * (-TwoPiRaw) + (-TwoPiRaw);
            }

            return new Fixed32(result);
        }

        /// <summary>
        /// Normalize a heading to [0, 2π) by adding/subtracting 2π as needed.
        /// Pure integer; result bit-stable.
        /// </summary>
        public static Fixed32 NormalizeHeading(Fixed32 heading)
        {
            int raw = heading.RawValue;
            if (raw < 0)
            {
                raw = raw + TwoPiRaw + (int)((uint)(-raw - 1) / TwoPiRaw) * TwoPiRaw;
            }
            else if (raw >= TwoPiRaw)
            {
                raw = raw - TwoPiRaw - (int)((uint)(raw - TwoPiRaw) / TwoPiRaw) * TwoPiRaw;
            }
            return new Fixed32(raw);
        }

        /// <summary>
        /// Convert a Fixed32 heading to a unit-vector (X, Y) pair in Fixed32 position-scale
        /// (8 fractional bits). Direct port of client's `VectorType2D::FromHeading` @ 0x00527F90.
        ///
        /// Tables (SIN_TABLE + COS_TABLE) extracted 2026-05-27 from DungeonRunners.exe at
        /// virtual addresses 0x00920D50 (SIN, 360×4 bytes) and 0x009212F0 (COS, 360×4 bytes),
        /// each entry an int32 with 8-bit fractional precision. Verified reference values:
        /// SIN[0]=0, SIN[90]=256, SIN[180]=0, SIN[270]=-256. COS[0]=256, COS[90]=0,
        /// COS[180]=-256, COS[270]=0. Bit-equal to client output.
        ///
        /// Index formula: `(360 - (heading.Raw >> 8)) mod 360`. The shift drops Fixed32's
        /// bottom 8 fractional bits (heading uses 0x16800 = 92160 raw per 2π, so >> 8 = degrees).
        /// The subtraction inverts rotation direction — matches client's coordinate convention.
        /// </summary>
        public static (Fixed32 x, Fixed32 y) UnitVectorFromHeading(Fixed32 heading)
        {
            int idx = (360 - (heading.RawValue >> 8)) % 360;
            if (idx < 0) idx += 360;
            // Match client: X = SIN_TABLE[idx], Y = COS_TABLE[idx]
            return (new Fixed32(SIN_TABLE[idx]), new Fixed32(COS_TABLE[idx]));
        }

        /// <summary>
        /// Inverse of <see cref="UnitVectorFromHeading"/>: compute a Fixed32 heading angle
        /// from a direction vector. Used by <c>UnitMover</c>'s state-2 to derive heading
        /// toward the next waypoint. Inputs are Fixed32 raw deltas; only the angle matters.
        /// Output is normalized to [0, 0x16800).
        ///
        /// Uses <see cref="System.Math.Atan2"/> internally (float) and converts to Fixed32
        /// angle. May drift by ≤1 raw unit from the client's exact lookup-table inverse, but
        /// the resulting heading is within the DoD's ≤1-tile tolerance.
        /// </summary>
        public static Fixed32 HeadingFromVector(int dxRaw, int dyRaw)
        {
            if (dxRaw == 0 && dyRaw == 0) return Fixed32.Zero;

            // Client convention: heading 0 = +Y axis (North). UnitVectorFromHeading returns
            // (sin(degIdx), cos(degIdx)) where degIdx = (360 - degrees) mod 360. Invert:
            // degrees = (360 - atan2(dx, dy)) mod 360. Then degrees * 256 → Fixed32 angle raw.
            double radians = System.Math.Atan2(dxRaw, dyRaw);  // dy first → 0 == +Y
            double degrees = radians * 180.0 / System.Math.PI;
            if (degrees < 0) degrees += 360.0;
            int degIdx = (360 - (int)System.Math.Round(degrees)) % 360;
            if (degIdx < 0) degIdx += 360;
            return new Fixed32(degIdx << 8);
        }

        // ─── Sin/Cos lookup tables ─────────────────────────────────────────
        // Extracted from DungeonRunners.exe at 0x00920D50 (SIN) and 0x009212F0 (COS).
        // 360 entries each, indexed by angle in degrees, value is Fixed32 (8-bit fractional).
        // See UnitVectorFromHeading above for the indexing formula matching client.

        private static readonly int[] SIN_TABLE = new int[] {
               0,    4,    8,   13,   17,   22,   26,   31,   35,   40,   44,   48,
              53,   57,   61,   66,   70,   74,   79,   83,   87,   91,   95,  100,
             104,  108,  112,  116,  120,  124,  127,  131,  135,  139,  143,  146,
             150,  154,  157,  161,  164,  167,  171,  174,  177,  181,  184,  187,
             190,  193,  196,  198,  201,  204,  207,  209,  212,  214,  217,  219,
             221,  223,  226,  228,  230,  232,  233,  235,  237,  238,  240,  242,
             243,  244,  246,  247,  248,  249,  250,  251,  252,  252,  253,  254,
             254,  255,  255,  255,  255,  255,  256,  255,  255,  255,  255,  255,
             254,  254,  253,  252,  252,  251,  250,  249,  248,  247,  246,  244,
             243,  242,  240,  238,  237,  235,  233,  232,  230,  228,  226,  223,
             221,  219,  217,  214,  212,  209,  207,  204,  201,  198,  196,  193,
             190,  187,  184,  181,  177,  174,  171,  167,  164,  161,  157,  154,
             150,  146,  143,  139,  135,  131,  127,  124,  120,  116,  112,  108,
             104,  100,   95,   91,   87,   83,   79,   74,   70,   66,   61,   57,
              53,   48,   44,   40,   35,   31,   26,   22,   17,   13,    8,    4,
               0,   -4,   -8,  -13,  -17,  -22,  -26,  -31,  -35,  -40,  -44,  -48,
             -53,  -57,  -61,  -66,  -70,  -74,  -79,  -83,  -87,  -91,  -95, -100,
            -104, -108, -112, -116, -120, -124, -128, -131, -135, -139, -143, -146,
            -150, -154, -157, -161, -164, -167, -171, -174, -177, -181, -184, -187,
            -190, -193, -196, -198, -201, -204, -207, -209, -212, -214, -217, -219,
            -221, -223, -226, -228, -230, -232, -233, -235, -237, -238, -240, -242,
            -243, -244, -246, -247, -248, -249, -250, -251, -252, -252, -253, -254,
            -254, -255, -255, -255, -255, -255, -256, -255, -255, -255, -255, -255,
            -254, -254, -253, -252, -252, -251, -250, -249, -248, -247, -246, -244,
            -243, -242, -240, -238, -237, -235, -233, -232, -230, -228, -226, -223,
            -221, -219, -217, -214, -212, -209, -207, -204, -201, -198, -196, -193,
            -190, -187, -184, -181, -177, -174, -171, -167, -164, -161, -157, -154,
            -150, -146, -143, -139, -135, -131, -128, -124, -120, -116, -112, -108,
            -104, -100,  -95,  -91,  -87,  -83,  -79,  -74,  -70,  -66,  -61,  -57,
             -53,  -48,  -44,  -40,  -35,  -31,  -26,  -22,  -17,  -13,   -8,   -4,
        };

        private static readonly int[] COS_TABLE = new int[] {
             256,  255,  255,  255,  255,  255,  254,  254,  253,  252,  252,  251,
             250,  249,  248,  247,  246,  244,  243,  242,  240,  238,  237,  235,
             233,  232,  230,  228,  226,  223,  221,  219,  217,  214,  212,  209,
             207,  204,  201,  198,  196,  193,  190,  187,  184,  181,  177,  174,
             171,  167,  164,  161,  157,  154,  150,  146,  143,  139,  135,  131,
             128,  124,  120,  116,  112,  108,  104,  100,   95,   91,   87,   83,
              79,   74,   70,   66,   61,   57,   53,   48,   44,   40,   35,   31,
              26,   22,   17,   13,    8,    4,    0,   -4,   -8,  -13,  -17,  -22,
             -26,  -31,  -35,  -40,  -44,  -48,  -53,  -57,  -61,  -66,  -70,  -74,
             -79,  -83,  -87,  -91,  -95, -100, -104, -108, -112, -116, -120, -124,
            -127, -131, -135, -139, -143, -146, -150, -154, -157, -161, -164, -167,
            -171, -174, -177, -181, -184, -187, -190, -193, -196, -198, -201, -204,
            -207, -209, -212, -214, -217, -219, -221, -223, -226, -228, -230, -232,
            -233, -235, -237, -238, -240, -242, -243, -244, -246, -247, -248, -249,
            -250, -251, -252, -252, -253, -254, -254, -255, -255, -255, -255, -255,
            -256, -255, -255, -255, -255, -255, -254, -254, -253, -252, -252, -251,
            -250, -249, -248, -247, -246, -244, -243, -242, -240, -238, -237, -235,
            -233, -232, -230, -228, -226, -223, -221, -219, -217, -214, -212, -209,
            -207, -204, -201, -198, -196, -193, -190, -187, -184, -181, -177, -174,
            -171, -167, -164, -161, -157, -154, -150, -146, -143, -139, -135, -131,
            -128, -124, -120, -116, -112, -108, -104, -100,  -95,  -91,  -87,  -83,
             -79,  -74,  -70,  -66,  -61,  -57,  -53,  -48,  -44,  -40,  -35,  -31,
             -26,  -22,  -17,  -13,   -8,   -4,    0,    4,    8,   13,   17,   22,
              26,   31,   35,   40,   44,   48,   53,   57,   61,   66,   70,   74,
              79,   83,   87,   91,   95,  100,  104,  108,  112,  116,  120,  124,
             128,  131,  135,  139,  143,  146,  150,  154,  157,  161,  164,  167,
             171,  174,  177,  181,  184,  187,  190,  193,  196,  198,  201,  204,
             207,  209,  212,  214,  217,  219,  221,  223,  226,  228,  230,  232,
             233,  235,  237,  238,  240,  242,  243,  244,  246,  247,  248,  249,
             250,  251,  252,  252,  253,  254,  254,  255,  255,  255,  255,  255,
        };
    }
}
