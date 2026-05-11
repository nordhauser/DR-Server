using System;
using System.IO;
using System.IO.Compression;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Handles zlib compression and decompression for game packets
    /// </summary>
    public static class PacketCompression
    {
        public static byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
                return data;

            using (var output = new MemoryStream())
            {
                // Write zlib header (0x78, 0x9C for default compression)
                output.WriteByte(0x78);
                output.WriteByte(0x9C);

                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                // Calculate and write Adler-32 checksum
                uint adler = CalculateAdler32(data);
                output.WriteByte((byte)((adler >> 24) & 0xFF));
                output.WriteByte((byte)((adler >> 16) & 0xFF));
                output.WriteByte((byte)((adler >> 8) & 0xFF));
                output.WriteByte((byte)(adler & 0xFF));

                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length < 6)
                return data;

            using (var input = new MemoryStream(data))
            {
                // Skip zlib header (2 bytes)
                input.ReadByte();
                input.ReadByte();

                using (var output = new MemoryStream())
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        private static uint CalculateAdler32(byte[] data)
        {
            const uint MOD_ADLER = 65521;
            uint a = 1, b = 0;

            foreach (byte byteVal in data)
            {
                a = (a + byteVal) % MOD_ADLER;
                b = (b + a) % MOD_ADLER;
            }

            return (b << 16) | a;
        }
    }
}