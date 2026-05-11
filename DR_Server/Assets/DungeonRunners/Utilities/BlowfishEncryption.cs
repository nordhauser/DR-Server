using System;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Handles Blowfish encryption/decryption for auth packets
    /// </summary>
    public class BlowfishEncryption
    {
        private readonly BlowfishEngine _engine;
        private readonly KeyParameter _keyParam;

        public BlowfishEncryption(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            // Add null terminator if not present
            if (!key.EndsWith("\0"))
                key += "\0";

            byte[] keyBytes = System.Text.Encoding.ASCII.GetBytes(key);
            _keyParam = new KeyParameter(keyBytes);
            _engine = new BlowfishEngine();
        }

        public byte[] Encrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return data;

            // Pad to 8-byte blocks
            int paddedLength = ((data.Length + 7) / 8) * 8;
            byte[] padded = new byte[paddedLength];
            Array.Copy(data, padded, data.Length);

            byte[] output = new byte[paddedLength];
            _engine.Init(true, _keyParam);

            for (int i = 0; i < paddedLength; i += 8)
            {
                _engine.ProcessBlock(padded, i, output, i);
            }

            return output;
        }

        public byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length % 8 != 0)
                return data;

            byte[] output = new byte[data.Length];
            _engine.Init(false, _keyParam);

            for (int i = 0; i < data.Length; i += 8)
            {
                _engine.ProcessBlock(data, i, output, i);
            }

            return output;
        }
    }
}