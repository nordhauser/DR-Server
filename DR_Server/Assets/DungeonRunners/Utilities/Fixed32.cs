using System;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// 32-bit fixed-point value matching the DR client's Fixed32 representation.
    /// For position fields (UnitMover+0x50/+0x54/+0x58): 8 bits of fractional precision
    /// (1 world unit = 256 raw units). For heading fields (UnitMover+0x5C/+0x64): scale is
    /// 0x16800 raw units per full circle (2π), see Fixed32Math.cs.
    ///
    /// Multiply convention is 64-bit signed intermediate, right-shifted by 8:
    ///   result = (int)(((long)a * b) >> 8)
    /// Confirmed via Ghidra decompile of ComputeFacingVectors @ 0x00536BE0 (D1 derisk
    /// 2026-05-27): the client uses `(longlong)A * (longlong)B; result = (uint)lVar1 >> 8 |
    /// (int)((ulonglong)lVar1 >> 0x20) << 0x18`, equivalent to a 64-bit signed multiply
    /// followed by arithmetic-right-shift 8.
    ///
    /// Divide convention is 64-bit signed dividend shifted left by 8 to preserve fractional
    /// precision through the divide:
    ///   result = (int)(((long)a << 8) / b)
    /// This is the standard inverse of Multiply; client uses it for vector normalization.
    /// </summary>
    public readonly struct Fixed32 : IEquatable<Fixed32>, IComparable<Fixed32>
    {
        public readonly int RawValue;

        public const int FractionalBits = 8;
        public const int OneRaw = 1 << FractionalBits; // 256

        public static readonly Fixed32 Zero = new Fixed32(0);
        public static readonly Fixed32 One = new Fixed32(OneRaw);
        public static readonly Fixed32 MinusOne = new Fixed32(-OneRaw);
        public static readonly Fixed32 MaxValue = new Fixed32(int.MaxValue);
        public static readonly Fixed32 MinValue = new Fixed32(int.MinValue);

        public Fixed32(int rawValue)
        {
            RawValue = rawValue;
        }

        /// <summary>Construct from a float by scaling to raw (truncating toward zero).</summary>
        public static Fixed32 FromFloat(float value)
        {
            return new Fixed32((int)(value * OneRaw));
        }

        /// <summary>Construct from an integer world-unit value (1 unit = OneRaw raw).</summary>
        public static Fixed32 FromInt(int worldUnits)
        {
            return new Fixed32(worldUnits * OneRaw);
        }

        /// <summary>Construct directly from a raw int value (no scaling).</summary>
        public static Fixed32 FromRaw(int raw)
        {
            return new Fixed32(raw);
        }

        /// <summary>Convert to float for interop. Lossy at large values.</summary>
        public float ToFloat()
        {
            return RawValue / (float)OneRaw;
        }

        /// <summary>Integer part (truncated toward zero).</summary>
        public int ToInt()
        {
            return RawValue >> FractionalBits;
        }

        // ─── Arithmetic ────────────────────────────────────────────────────

        public static Fixed32 operator +(Fixed32 a, Fixed32 b)
        {
            return new Fixed32(a.RawValue + b.RawValue);
        }

        public static Fixed32 operator -(Fixed32 a, Fixed32 b)
        {
            return new Fixed32(a.RawValue - b.RawValue);
        }

        public static Fixed32 operator -(Fixed32 a)
        {
            return new Fixed32(-a.RawValue);
        }

        /// <summary>
        /// Multiply with 64-bit signed intermediate + arithmetic-right-shift 8.
        /// Matches client's `ComputeFacingVectors` math at byte-precision (D1 verified).
        /// </summary>
        public static Fixed32 operator *(Fixed32 a, Fixed32 b)
        {
            long product = (long)a.RawValue * b.RawValue;
            return new Fixed32((int)(product >> FractionalBits));
        }

        /// <summary>
        /// Multiply by an integer scalar (no shift — scalar isn't fixed-point).
        /// </summary>
        public static Fixed32 operator *(Fixed32 a, int scalar)
        {
            return new Fixed32(a.RawValue * scalar);
        }

        public static Fixed32 operator *(int scalar, Fixed32 a)
        {
            return new Fixed32(a.RawValue * scalar);
        }

        /// <summary>
        /// Divide with 64-bit dividend left-shifted to preserve fractional precision.
        /// Throws DivideByZeroException if divisor.RawValue == 0.
        /// </summary>
        public static Fixed32 operator /(Fixed32 a, Fixed32 b)
        {
            if (b.RawValue == 0)
                throw new DivideByZeroException("Fixed32 division by zero");
            long dividend = (long)a.RawValue << FractionalBits;
            return new Fixed32((int)(dividend / b.RawValue));
        }

        /// <summary>Integer scalar divide (no shift).</summary>
        public static Fixed32 operator /(Fixed32 a, int scalar)
        {
            if (scalar == 0)
                throw new DivideByZeroException("Fixed32 integer division by zero");
            return new Fixed32(a.RawValue / scalar);
        }

        // ─── Comparison ────────────────────────────────────────────────────

        public static bool operator ==(Fixed32 a, Fixed32 b) => a.RawValue == b.RawValue;
        public static bool operator !=(Fixed32 a, Fixed32 b) => a.RawValue != b.RawValue;
        public static bool operator <(Fixed32 a, Fixed32 b) => a.RawValue < b.RawValue;
        public static bool operator >(Fixed32 a, Fixed32 b) => a.RawValue > b.RawValue;
        public static bool operator <=(Fixed32 a, Fixed32 b) => a.RawValue <= b.RawValue;
        public static bool operator >=(Fixed32 a, Fixed32 b) => a.RawValue >= b.RawValue;

        public bool Equals(Fixed32 other) => RawValue == other.RawValue;
        public override bool Equals(object obj) => obj is Fixed32 f && Equals(f);
        public override int GetHashCode() => RawValue.GetHashCode();
        public int CompareTo(Fixed32 other) => RawValue.CompareTo(other.RawValue);

        // ─── Utility ───────────────────────────────────────────────────────

        public static Fixed32 Abs(Fixed32 a)
        {
            return a.RawValue >= 0 ? a : new Fixed32(-a.RawValue);
        }

        public static int Sign(Fixed32 a)
        {
            if (a.RawValue == 0) return 0;
            return a.RawValue > 0 ? 1 : -1;
        }

        public static Fixed32 Min(Fixed32 a, Fixed32 b)
        {
            return a.RawValue <= b.RawValue ? a : b;
        }

        public static Fixed32 Max(Fixed32 a, Fixed32 b)
        {
            return a.RawValue >= b.RawValue ? a : b;
        }

        public static Fixed32 Clamp(Fixed32 value, Fixed32 min, Fixed32 max)
        {
            if (value.RawValue < min.RawValue) return min;
            if (value.RawValue > max.RawValue) return max;
            return value;
        }

        public override string ToString()
        {
            return $"{ToFloat():F4} (raw 0x{RawValue:X8})";
        }
    }
}
