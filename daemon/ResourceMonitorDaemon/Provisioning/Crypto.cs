using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// AES-CTR as one continuous keystream: successive <see cref="Transform"/> calls continue where the
    /// previous call stopped, mid-block included. This mirrors the single cryptography.io CTR context in
    /// esp_prov security1.py and mbedtls_aes_crypt_ctr(nc_off, stream_block) on the device, which share
    /// one stream between every encrypt and decrypt of a session. The counter is the full 16-byte block
    /// incremented as a big-endian integer (NIST SP 800-38A, as both reference implementations do).
    /// Built on .NET AES-ECB so no hand-rolled block cipher is involved.
    /// </summary>
    internal sealed class AesCtrStream : IDisposable
    {
        private const int BlockSize = 16;

        private readonly Aes _aes;
        private readonly byte[] _counter = new byte[BlockSize];
        private readonly byte[] _keystream = new byte[BlockSize];
        private int _keystreamOffset = BlockSize;
        private bool _disposed;

        public AesCtrStream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> initialCounter)
        {
            if (key.Length != 16 && key.Length != 24 && key.Length != 32)
            {
                throw new ArgumentException("AES key must be 16, 24 or 32 bytes.", nameof(key));
            }

            if (initialCounter.Length != BlockSize)
            {
                throw new ArgumentException("AES-CTR initial counter must be 16 bytes.", nameof(initialCounter));
            }

            _aes = Aes.Create();
            // The Key setter clones its argument, so zero our temporary copy straight away. The clone Aes
            // keeps internally is released by _aes.Dispose(). (Aes.SetKey(span) is not available on net9.0.)
            var keyCopy = key.ToArray();
            try
            {
                _aes.Key = keyCopy;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyCopy);
            }
            initialCounter.CopyTo(_counter);
        }

        public byte[] Transform(ReadOnlySpan<byte> input)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var output = new byte[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                if (_keystreamOffset == BlockSize)
                {
                    _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                    IncrementCounter(_counter);
                    _keystreamOffset = 0;
                }

                output[i] = (byte)(input[i] ^ _keystream[_keystreamOffset++]);
            }

            return output;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CryptographicOperations.ZeroMemory(_counter);
            CryptographicOperations.ZeroMemory(_keystream);
            _aes.Dispose();
        }

        internal static void IncrementCounter(byte[] counter)
        {
            for (var i = counter.Length - 1; i >= 0; i--)
            {
                if (++counter[i] != 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Curve25519 key pair (RFC 7748 X25519) backed by BouncyCastle. Keys use the standard
    /// little-endian 32-byte encoding, which is what both esp_prov and the device (after its
    /// flip_endian calls in security1.c) put on the wire.
    /// </summary>
    internal sealed class X25519KeyPair : IDisposable
    {
        private readonly byte[] _privateKey;

        private X25519KeyPair(byte[] privateKey)
        {
            _privateKey = privateKey;
            PublicKey = new byte[X25519.PointSize];
            X25519.GeneratePublicKey(_privateKey, 0, PublicKey, 0);
        }

        public byte[] PublicKey { get; }

        public static X25519KeyPair Generate()
        {
            var privateKey = new byte[X25519.ScalarSize];
            RandomNumberGenerator.Fill(privateKey);
            return new X25519KeyPair(privateKey);
        }

        /// <summary>For test vectors only.</summary>
        internal static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
        {
            if (privateKey.Length != X25519.ScalarSize)
            {
                throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
            }

            return new X25519KeyPair(privateKey.ToArray());
        }

        public byte[] DeriveSharedSecret(ReadOnlySpan<byte> peerPublicKey)
        {
            if (peerPublicKey.Length != X25519.PointSize)
            {
                throw new ProvisioningProtocolException("Device public key has an invalid length.");
            }

            var shared = new byte[X25519.PointSize];
            if (!X25519.CalculateAgreement(_privateKey, 0, peerPublicKey.ToArray(), 0, shared, 0))
            {
                // All-zero result: the peer sent a small-order point.
                throw new ProvisioningProtocolException("Device public key is invalid (small-order point).");
            }

            return shared;
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_privateKey);
    }
}
