using System.Security.Cryptography;
using System.Text;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// Client side of ESP-IDF protocomm Security1 (X25519 + AES-256-CTR) with proof of possession,
    /// reimplemented from esp_prov security1.py (ESP-IDF v4.4.7):
    /// https://github.com/espressif/esp-idf/blob/v4.4.7/tools/esp_prov/security/security1.py
    ///
    /// Handshake on endpoint prov-session:
    ///   Command0(client_pubkey) -> Response0(device_pubkey, device_random)
    ///   key = X25519(client_priv, device_pubkey) XOR SHA256(pop); AES-CTR IV = device_random
    ///   Command1(AES-CTR(device_pubkey)) -> Response1(device_verify_data)
    ///   AES-CTR(device_verify_data) must equal client_pubkey.
    /// After the handshake, one keystream is shared by every encrypt and decrypt, in call order.
    /// </summary>
    public sealed class Security1Session : IDisposable
    {
        private readonly IProvisioningTransport _transport;
        private readonly AesCtrStream _cipher;
        // Never disposed: it has no wait handle to release, and disposing it would make an in-flight
        // SendEncryptedAsync throw ObjectDisposedException from Release().
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _stateLock = new();
        private bool _broken;
        private bool _busy;
        private bool _disposed;

        private Security1Session(IProvisioningTransport transport, AesCtrStream cipher)
        {
            _transport = transport;
            _cipher = cipher;
        }

        /// <summary>
        /// Runs the Security1 handshake. Throws <see cref="PairingCodeRejectedException"/> when the
        /// device does not accept <paramref name="pop"/>.
        /// </summary>
        public static async Task<Security1Session> EstablishAsync(
            IProvisioningTransport transport, string pop, CancellationToken cancellationToken)
        {
            using var clientKey = X25519KeyPair.Generate();
            return await EstablishAsync(transport, pop, clientKey, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<Security1Session> EstablishAsync(
            IProvisioningTransport transport, string pop, X25519KeyPair clientKey, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentException.ThrowIfNullOrEmpty(pop);

            var clientPubkey = clientKey.PublicKey;

            // Command0 / Response0
            var response0Bytes = await transport.SendAsync(
                ProvisioningConstants.SessionEndpoint, Sec1Messages.EncodeCommand0(clientPubkey), cancellationToken).ConfigureAwait(false);
            var response0 = Sec1Messages.DecodeResponse(response0Bytes);
            EnsureSecurity1(response0, Sec1MsgType.SessionResponse0);
            if (response0.Status != ProtocommStatus.Success)
            {
                throw new ProvisioningProtocolException($"Device refused to start a secure session (status {response0.Status}).");
            }

            var devicePubkey = response0.DevicePubkey;
            var deviceRandom = response0.DeviceRandom;
            if (devicePubkey is null || devicePubkey.Length != ProvisioningConstants.PublicKeyLength)
            {
                throw new ProvisioningProtocolException("Device sent a missing or malformed public key.");
            }

            if (deviceRandom is null || deviceRandom.Length != ProvisioningConstants.DeviceRandomLength)
            {
                throw new ProvisioningProtocolException("Device sent a missing or malformed session random.");
            }

            // Key derivation
            var sessionKey = clientKey.DeriveSharedSecret(devicePubkey);
            var popBytes = Encoding.UTF8.GetBytes(pop);
            var popHash = SHA256.HashData(popBytes);
            for (var i = 0; i < sessionKey.Length; i++)
            {
                sessionKey[i] ^= popHash[i];
            }

            CryptographicOperations.ZeroMemory(popBytes);
            CryptographicOperations.ZeroMemory(popHash);

            var cipher = new AesCtrStream(sessionKey, deviceRandom);
            CryptographicOperations.ZeroMemory(sessionKey);
            try
            {
                // Command1 / Response1
                var clientVerify = cipher.Transform(devicePubkey);
                byte[] response1Bytes;
                try
                {
                    response1Bytes = await transport.SendAsync(
                        ProvisioningConstants.SessionEndpoint, Sec1Messages.EncodeCommand1(clientVerify), cancellationToken).ConfigureAwait(false);
                }
                catch (ProvisioningTransportException ex) when (ex.Error is not ProvisioningTransportError.BluetoothOff
                                                                    and not ProvisioningTransportError.BluetoothUnavailable)
                {
                    // A real device answers a wrong PoP by dropping the connection without a response.
                    throw new PairingCodeRejectedException(PairingCodeRejectionReason.DeviceClosedConnection, ex);
                }

                if (response1Bytes.Length == 0)
                {
                    throw new PairingCodeRejectedException(PairingCodeRejectionReason.DeviceClosedConnection);
                }

                var response1 = Sec1Messages.DecodeResponse(response1Bytes);
                if (response1.MsgType == Sec1MsgType.SessionResponse0)
                {
                    // The device failed Command1 and closed the link, but the write was acknowledged (the
                    // prepared-write path in protocomm_ble.c responds OK even on failure), so the read
                    // returned the stale Response0 still stored in the attribute.
                    throw new PairingCodeRejectedException(PairingCodeRejectionReason.DeviceClosedConnection);
                }

                EnsureSecurity1(response1, Sec1MsgType.SessionResponse1);
                if (response1.Status == ProtocommStatus.CryptoError)
                {
                    throw new PairingCodeRejectedException(PairingCodeRejectionReason.VerificationMismatch);
                }

                if (response1.Status != ProtocommStatus.Success)
                {
                    throw new ProvisioningProtocolException($"Device failed to complete the secure session (status {response1.Status}).");
                }

                if (response1.DeviceVerifyData is null || response1.DeviceVerifyData.Length != ProvisioningConstants.PublicKeyLength)
                {
                    throw new PairingCodeRejectedException(PairingCodeRejectionReason.VerificationMismatch);
                }

                var decryptedVerify = cipher.Transform(response1.DeviceVerifyData);
                if (!CryptographicOperations.FixedTimeEquals(decryptedVerify, clientPubkey))
                {
                    throw new PairingCodeRejectedException(PairingCodeRejectionReason.VerificationMismatch);
                }

                return new Security1Session(transport, cipher);
            }
            catch
            {
                cipher.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Encrypts <paramref name="plaintext"/>, sends it to <paramref name="endpoint"/> and returns the
        /// decrypted response. Calls are serialized. If an exchange fails half-way, the client and device
        /// keystreams can no longer be aligned, so the session is marked unusable.
        /// The caller owns <paramref name="plaintext"/> and should zero it if it holds secrets.
        /// </summary>
        public async Task<byte[]> SendEncryptedAsync(string endpoint, byte[] plaintext, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_stateLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _busy = true;
                }

                if (_broken)
                {
                    throw new ProvisioningException("The secure session with the device was interrupted. Reconnect and try again.");
                }

                var completed = false;
                try
                {
                    var ciphertext = _cipher.Transform(plaintext);
                    var response = await _transport.SendAsync(endpoint, ciphertext, cancellationToken).ConfigureAwait(false);
                    var decrypted = _cipher.Transform(response);
                    completed = true;
                    return decrypted;
                }
                finally
                {
                    if (!completed)
                    {
                        _broken = true;
                    }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    _busy = false;
                    if (_disposed)
                    {
                        // Dispose() ran while this exchange was in flight and left the key to us.
                        _cipher.Dispose();
                    }
                }

                _gate.Release();
            }
        }

        /// <summary>
        /// Releases the session key. Safe to call while an exchange is in flight: the key is then wiped
        /// when that exchange finishes, and later calls throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_busy)
                {
                    _cipher.Dispose();
                }
            }
        }

        private static void EnsureSecurity1(SessionResponse response, Sec1MsgType expected)
        {
            if (response.SecVer != SecSchemeVersion.SecScheme1)
            {
                throw new ProvisioningProtocolException(
                    $"Device does not use protocomm Security1 (reported scheme {response.SecVer}).");
            }

            if (response.MsgType != expected)
            {
                throw new ProvisioningProtocolException(
                    $"Unexpected session message from device: {response.MsgType}, expected {expected}.");
            }
        }
    }
}
