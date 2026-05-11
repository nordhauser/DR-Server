using System;
using System.Collections.Generic;
using System.Text;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Little-endian binary writer for network protocol
    /// </summary>
    public class LEWriter
    {
        private readonly List<byte> _buffer = new List<byte>();

        public void WriteByte(byte value)
        {
            _buffer.Add(value);
        }

        public void WriteBytes(byte[] bytes)
        {
            if (bytes != null && bytes.Length > 0)
            {
                _buffer.AddRange(bytes);
            }
        }

        public void WriteUInt16(ushort value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
        }

        // ✅ NEW METHOD - Write UInt16 at specific position (for backfilling size field)
        public void WriteUInt16At(int position, ushort value)
        {
            if (position < 0 || position + 1 >= _buffer.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(position),
                    $"Position {position} is out of range for buffer size {_buffer.Count}");
            }

            _buffer[position] = (byte)(value & 0xFF);
            _buffer[position + 1] = (byte)((value >> 8) & 0xFF);
        }

        public void WriteUInt24(int value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
            _buffer.Add((byte)((value >> 16) & 0xFF));
        }

        public void WriteUInt32(uint value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
            _buffer.Add((byte)((value >> 16) & 0xFF));
            _buffer.Add((byte)((value >> 24) & 0xFF));
        }

        public void WriteInt32(int value)
        {
            int before = _buffer.Count;
            WriteUInt32((uint)value);
            int after = _buffer.Count;
            if (after - before != 4)
            {
                throw new Exception("WriteInt32 WROTE " + (after - before) + " BYTES!");
            }
        }

        public void WriteUInt64(ulong value)
        {
            _buffer.Add((byte)(value & 0xFF));
            _buffer.Add((byte)((value >> 8) & 0xFF));
            _buffer.Add((byte)((value >> 16) & 0xFF));
            _buffer.Add((byte)((value >> 24) & 0xFF));
            _buffer.Add((byte)((value >> 32) & 0xFF));
            _buffer.Add((byte)((value >> 40) & 0xFF));
            _buffer.Add((byte)((value >> 48) & 0xFF));
            _buffer.Add((byte)((value >> 56) & 0xFF));
        }

        public void WriteFloat(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _buffer.AddRange(bytes);
        }

        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteUInt16(0);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt16((ushort)bytes.Length);
            _buffer.AddRange(bytes);
        }

        public void WriteCString(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                _buffer.AddRange(bytes);
            }
            _buffer.Add(0); // Null terminator
        }

        public byte[] ToArray()
        {
            return _buffer.ToArray();
        }

        // ✅ ADDED: Get raw buffer for advanced operations
        public byte[] GetBuffer()
        {
            return _buffer.ToArray();
        }

        public int Length => _buffer.Count;

        public int Position => _buffer.Count;

        public void Clear()
        {
            _buffer.Clear();
        }
    }
}