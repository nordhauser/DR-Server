using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Zlib compression/decompression utilities for network protocol
    /// </summary>
    public static class ZlibUtil
    {
        /// <summary>
        /// Compress data using zlib (Deflate with zlib header)
        /// </summary>
        public static byte[] Deflate(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            try
            {
                using (var output = new MemoryStream())
                {
                    // Write zlib header (RFC 1950)
                    // CMF (Compression Method and Flags): 0x78 (deflate, 32K window)
                    // FLG (Flags): 0x9C (default compression, no preset dictionary)
                    output.WriteByte(0x78);
                    output.WriteByte(0x9C);

                    // Compress with DeflateStream
                    using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                    {
                        deflate.Write(data, 0, data.Length);
                    }

                    // Write Adler-32 checksum (4 bytes, big-endian)
                    uint adler = Adler32(data);
                    output.WriteByte((byte)((adler >> 24) & 0xFF));
                    output.WriteByte((byte)((adler >> 16) & 0xFF));
                    output.WriteByte((byte)((adler >> 8) & 0xFF));
                    output.WriteByte((byte)(adler & 0xFF));

                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ZlibUtil] Deflate failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Decompress zlib data (Deflate with zlib header)
        /// </summary>
        public static byte[] Inflate(byte[] data, uint expectedSize = 0)
        {
            if (data == null || data.Length < 6) // Minimum: 2 header + 0 data + 4 checksum
                return Array.Empty<byte>();

            try
            {
                using (var input = new MemoryStream(data))
                {
                    // Read and verify zlib header
                    int cmf = input.ReadByte();
                    int flg = input.ReadByte();

                    if (cmf == -1 || flg == -1)
                        throw new InvalidDataException("Invalid zlib header");

                    // Verify header checksum: (CMF * 256 + FLG) % 31 == 0
                    if (((cmf * 256 + flg) % 31) != 0)
                        throw new InvalidDataException("Invalid zlib header checksum");

                    // Decompress with DeflateStream
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        using (var output = new MemoryStream(expectedSize > 0 ? (int)expectedSize : 4096))
                        {
                            deflate.CopyTo(output);
                            return output.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ZlibUtil] Inflate failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Calculate Adler-32 checksum (RFC 1950)
        /// </summary>
        private static uint Adler32(byte[] data)
        {
            const uint MOD_ADLER = 65521;
            uint a = 1, b = 0;

            foreach (byte c in data)
            {
                a = (a + c) % MOD_ADLER;
                b = (b + a) % MOD_ADLER;
            }

            return (b << 16) | a;
        }

        /// <summary>
        /// Verify Adler-32 checksum
        /// </summary>
        public static bool VerifyAdler32(byte[] data, uint checksum)
        {
            return Adler32(data) == checksum;
        }
    }
}