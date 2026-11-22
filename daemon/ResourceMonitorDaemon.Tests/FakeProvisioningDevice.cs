using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Tests
{
    /// <summary>
    /// In-memory model of the device side of ESP-IDF v4.4.7 protocomm Security1 + wifi_config, modelled
    /// on components/protocomm/src/security/security1.c and wifi_provisioning/src/wifi_config.c.
    /// Its crypto deliberately uses different code paths from the client: BouncyCastle's
    /// X25519PrivateKeyParameters/X25519Agreement for the key exchange and BouncyCastle's
    /// SicBlockCipher (CTR) for its own keystream. Message layouts are written out here with literal
    /// field numbers rather than the client's Sec1Messages/WifiConfigMessages, but the low-level
    /// protobuf framing reuses the client's ProtoReader/ProtoWriter, so it does not independently
    /// verify varint/tag encoding (the byte-level ProtoWire tests cover that).
    /// </summary>
    internal sealed class FakeProvisioningDevice : IProvisioningTransport
    {
        private readonly string _pop;
        private DeviceCtrStream? _stream;
        private byte[]? _devicePubkey;
        private byte[]? _clientPubkey;
        private byte[]? _lastResponse0;
        private bool _failedAttempt;
        private WifiStationState? _settledState; // Connected/ConnectionFailed persist until the next ApplyConfig
        private bool _sessionEstablished;
        private bool _connectionClosed;

        public FakeProvisioningDevice(string pop)
        {
            _pop = pop;
        }

        // Behaviour knobs
        public bool SupportsVersionEndpoint { get; set; } = true;
        public bool ForgeDeviceVerifyData { get; set; }
        public int ConnectingPolls { get; set; } = 2;
        public WifiStationState FinalState { get; set; } = WifiStationState.Connected;
        public WifiConnectFailedReason FinalFailReason { get; set; } = WifiConnectFailedReason.AuthError;
        public string Ip4Address { get; set; } = "192.168.1.50";
        public ProtocommStatus SetConfigStatus { get; set; } = ProtocommStatus.Success;
        public bool FailNextConfigExchange { get; set; }
        public bool StaleResponse0OnWrongPop { get; set; }
        public bool ResetStateOnFailure { get; set; }
        public bool DisconnectWhileConnecting { get; set; }
        public bool CloseLinkAfterConnected { get; set; }
        public int EmptyStatusPolls { get; set; }
        public ProtocommStatus? ApplyStatusOverride { get; set; }
        /// <summary>
        /// SSID reported when connected; defaults to the SSID received in SetConfig. "" sends an empty
        /// ssid field.
        /// </summary>
        public string? JoinedSsid { get; set; }

        // Observations
        public string? ReceivedSsid { get; private set; }
        public string? ReceivedPassphrase { get; private set; }
        public bool Applied { get; private set; }
        public int StatusPolls { get; private set; }
        public int SetConfigCount { get; private set; }
        public List<string> EndpointLog { get; } = new();
        public List<byte[]> ConfigCiphertexts { get; } = new();
        public bool Disposed { get; private set; }

        public Task<byte[]> SendAsync(string endpoint, byte[] data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EndpointLog.Add(endpoint);
            if (_connectionClosed)
            {
                throw new ProvisioningTransportException(ProvisioningTransportError.DeviceDisconnected, "Device disconnected.");
            }

            return Task.FromResult(endpoint switch
            {
                "proto-ver" when SupportsVersionEndpoint =>
                    Encoding.UTF8.GetBytes("{\"prov\":{\"ver\":\"v1.1\",\"cap\":[\"wifi_scan\"]}}"),
                "prov-session" => HandleSession(data),
                "prov-config" when _sessionEstablished => HandleConfig(data),
                _ => throw new ProvisioningTransportException(ProvisioningTransportError.EndpointNotFound, $"No endpoint {endpoint}."),
            });
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        // ---- prov-session -----------------------------------------------------------------

        private byte[] HandleSession(byte[] request)
        {
            // SessionData{sec_ver=2, sec1=11{msg=1, sc0=20{client_pubkey=1}, sc1=22{client_verify_data=2}}}
            var secVer = 0;
            var msg = 0;
            byte[]? clientPubkey = null;
            byte[]? clientVerify = null;
            var reader = new ProtoReader(request);
            while (reader.TryReadTag(out var f, out var w))
            {
                if (f == 2) { secVer = reader.ReadInt32(); }
                else if (f == 11)
                {
                    var sec1 = new ProtoReader(reader.ReadLengthDelimited());
                    while (sec1.TryReadTag(out var f1, out var w1))
                    {
                        if (f1 == 1) { msg = sec1.ReadInt32(); }
                        else if (f1 == 20) { clientPubkey = ReadSingleBytesField(sec1.ReadLengthDelimited(), 1); }
                        else if (f1 == 22) { clientVerify = ReadSingleBytesField(sec1.ReadLengthDelimited(), 2); }
                        else { sec1.SkipField(w1); }
                    }
                }
                else { reader.SkipField(w); }
            }

            if (secVer != 1)
            {
                return CloseConnection();
            }

            return msg switch
            {
                0 => HandleCommand0(clientPubkey),
                2 => HandleCommand1(clientVerify),
                _ => CloseConnection(),
            };
        }

        private byte[] HandleCommand0(byte[]? clientPubkey)
        {
            if (clientPubkey is null || clientPubkey.Length != 32)
            {
                return CloseConnection();
            }

            var random = new SecureRandom();
            var devicePrivate = new X25519PrivateKeyParameters(random);
            _devicePubkey = devicePrivate.GeneratePublicKey().GetEncoded();
            _clientPubkey = clientPubkey;
            _sessionEstablished = false; // new session (new connection); Wi-Fi/provisioning state persists

            var agreement = new X25519Agreement();
            agreement.Init(devicePrivate);
            var key = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPubkey, 0), key, 0);
            var popHash = SHA256.HashData(Encoding.ASCII.GetBytes(_pop));
            for (var i = 0; i < key.Length; i++)
            {
                key[i] ^= popHash[i];
            }

            var deviceRandom = new byte[16];
            random.NextBytes(deviceRandom);
            _stream = new DeviceCtrStream(key, deviceRandom);

            // SessionData{sec_ver=1, sec1{msg=Session_Response0(1), sr0(21){status omitted, device_pubkey(2), device_random(3)}}}
            using var writer = new ProtoWriter();
            writer.WriteVarintField(2, 1);
            writer.WriteMessageField(11, sec1 =>
            {
                sec1.WriteVarintField(1, 1);
                sec1.WriteMessageField(21, sr0 =>
                {
                    sr0.WriteBytesField(2, _devicePubkey);
                    sr0.WriteBytesField(3, deviceRandom);
                });
            });
            _lastResponse0 = writer.ToArray();
            return _lastResponse0;
        }

        private byte[] HandleCommand1(byte[]? clientVerify)
        {
            if (_stream is null || _devicePubkey is null || _clientPubkey is null || clientVerify is null)
            {
                return CloseConnection();
            }

            var check = _stream.Transform(clientVerify);
            byte[] deviceVerify;
            if (!check.AsSpan().SequenceEqual(_devicePubkey))
            {
                if (StaleResponse0OnWrongPop)
                {
                    // Prepared-write path: exec_write acknowledges the write even though the handler
                    // failed, closes the link, and the read returns the attribute's previous value.
                    _connectionClosed = true;
                    return _lastResponse0!;
                }

                if (!ForgeDeviceVerifyData)
                {
                    // security1.c returns ESP_FAIL; protocomm_ble.c then calls esp_ble_gatts_close().
                    return CloseConnection();
                }

                deviceVerify = RandomNumberGenerator.GetBytes(32);
            }
            else
            {
                deviceVerify = _stream.Transform(_clientPubkey);
                _sessionEstablished = true;
            }

            // SessionData{sec_ver=1, sec1{msg=Session_Response1(3), sr1(23){status omitted, device_verify_data(3)}}}
            using var writer = new ProtoWriter();
            writer.WriteVarintField(2, 1);
            writer.WriteMessageField(11, sec1 =>
            {
                sec1.WriteVarintField(1, 3);
                sec1.WriteMessageField(23, sr1 => sr1.WriteBytesField(3, deviceVerify));
            });
            return writer.ToArray();
        }

        private byte[] CloseConnection()
        {
            _connectionClosed = true;
            throw new ProvisioningTransportException(ProvisioningTransportError.DeviceDisconnected, "Device closed the connection.");
        }

        // ---- prov-config ------------------------------------------------------------------

        private byte[] HandleConfig(byte[] ciphertext)
        {
            ConfigCiphertexts.Add(ciphertext);
            if (FailNextConfigExchange)
            {
                // The device consumed the request (advancing its keystream) but the reply was lost.
                FailNextConfigExchange = false;
                _stream!.Transform(ciphertext);
                throw new ProvisioningTransportException(ProvisioningTransportError.Timeout, "Response lost.");
            }

            var request = _stream!.Transform(ciphertext);
            var msg = 0;
            var reader = new ProtoReader(request);
            while (reader.TryReadTag(out var f, out var w))
            {
                if (f == 1) { msg = reader.ReadInt32(); }
                else if (f == 12)
                {
                    var cmd = new ProtoReader(reader.ReadLengthDelimited());
                    while (cmd.TryReadTag(out var f1, out var w1))
                    {
                        if (f1 == 1) { ReceivedSsid = Encoding.UTF8.GetString(cmd.ReadLengthDelimited()); }
                        else if (f1 == 2) { ReceivedPassphrase = Encoding.UTF8.GetString(cmd.ReadLengthDelimited()); }
                        else { cmd.SkipField(w1); }
                    }
                }
                else { reader.SkipField(w); }
            }

            using var writer = new ProtoWriter();
            switch (msg)
            {
                case 2: // TypeCmdSetConfig -> TypeRespSetConfig(3), resp_set_config(13)
                    SetConfigCount++;
                    writer.WriteVarintField(1, 3);
                    writer.WriteMessageField(13, r => WriteStatus(r, SetConfigStatus));
                    break;
                case 4: // TypeCmdApplyConfig -> TypeRespApplyConfig(5), resp_apply_config(15)
                    // wifi_prov_mgr_configure_sta fails (-> InternalError) after a failed attempt unless the
                    // firmware called wifi_prov_mgr_reset_sm_state_on_failure().
                    var applyStatus = ApplyStatusOverride
                        ?? (_failedAttempt && !ResetStateOnFailure ? ProtocommStatus.InternalError : ProtocommStatus.Success);
                    if (applyStatus == ProtocommStatus.Success)
                    {
                        Applied = true;
                        _failedAttempt = false;
                        _settledState = null;
                        StatusPolls = 0;
                    }

                    writer.WriteVarintField(1, 5);
                    writer.WriteMessageField(15, r => WriteStatus(r, applyStatus));
                    break;
                case 0: // TypeCmdGetStatus -> TypeRespGetStatus(1), resp_get_status(11)
                    if (Applied && DisconnectWhileConnecting)
                    {
                        return CloseConnection();
                    }

                    StatusPolls++;
                    // Before ApplyConfig a real v4.4.7 device reports Connecting: prov_ctx is calloc'd, so
                    // wifi_state is 0 (WIFI_PROV_STA_CONNECTING). It never sends Disconnected (wifi_config.c
                    // maps WIFI_PROV_STA_DISCONNECTED to ConnectionFailed).
                    var state = !Applied ? WifiStationState.Connecting
                        : _settledState ?? (StatusPolls <= ConnectingPolls ? WifiStationState.Connecting : FinalState);
                    if (Applied && state is WifiStationState.Connected or WifiStationState.ConnectionFailed)
                    {
                        _settledState = state;
                    }

                    if (state == WifiStationState.ConnectionFailed)
                    {
                        _failedAttempt = true;
                    }

                    writer.WriteVarintField(1, 1);
                    if (StatusPolls <= EmptyStatusPolls)
                    {
                        writer.WriteBytesField(11, ReadOnlySpan<byte>.Empty); // RespGetStatus with every field defaulted
                        break;
                    }

                    if (state == WifiStationState.Connected && CloseLinkAfterConnected)
                    {
                        // handlers.c calls wifi_prov_mgr_done() right after reporting Connected.
                        _connectionClosed = true;
                    }

                    writer.WriteMessageField(11, r =>
                    {
                        if (state != WifiStationState.Connected)
                        {
                            r.WriteVarintField(2, (ulong)state);
                        }

                        if (state == WifiStationState.Connected)
                        {
                            // connected(11){ip4_addr(1), auth_mode(2)=WPA2_PSK, channel(5)}
                            r.WriteMessageField(11, c =>
                            {
                                c.WriteBytesField(1, Encoding.ASCII.GetBytes(Ip4Address));
                                // JoinedSsid = "" sends a present-but-empty ssid field.
                                if ((JoinedSsid ?? ReceivedSsid) is { } joined)
                                {
                                    c.WriteBytesField(3, Encoding.UTF8.GetBytes(joined));
                                }

                                c.WriteVarintField(2, 3);
                                c.WriteVarintField(5, 6);
                            });
                        }
                        else if (state == WifiStationState.ConnectionFailed)
                        {
                            // protobuf-c emits the selected oneof member even when it is 0 (AuthError).
                            r.WriteVarintField(10, (ulong)FinalFailReason);
                        }
                    });
                    break;
                default:
                    return CloseConnection();
            }

            return _stream.Transform(writer.ToArray());
        }

        private static void WriteStatus(ProtoWriter writer, ProtocommStatus status)
        {
            if (status != ProtocommStatus.Success)
            {
                writer.WriteVarintField(1, (ulong)status);
            }
        }

        private static byte[]? ReadSingleBytesField(ReadOnlySpan<byte> message, int fieldNumber)
        {
            byte[]? value = null;
            var reader = new ProtoReader(message);
            while (reader.TryReadTag(out var f, out var w))
            {
                if (f == fieldNumber) { value = reader.ReadLengthDelimited().ToArray(); }
                else { reader.SkipField(w); }
            }

            return value;
        }

        /// <summary>Device-side running CTR keystream built on BouncyCastle's SicBlockCipher.</summary>
        private sealed class DeviceCtrStream
        {
            private readonly SicBlockCipher _ctr = new(new AesEngine());
            private readonly byte[] _block = new byte[16];
            private int _offset = 16;

            public DeviceCtrStream(byte[] key, byte[] iv)
            {
                _ctr.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
            }

            public byte[] Transform(byte[] input)
            {
                var output = new byte[input.Length];
                for (var i = 0; i < input.Length; i++)
                {
                    if (_offset == 16)
                    {
                        // Encrypting a zero block yields E(counter) and advances SicBlockCipher's counter.
                        _ctr.ProcessBlock(new byte[16], 0, _block, 0);
                        _offset = 0;
                    }

                    output[i] = (byte)(input[i] ^ _block[_offset++]);
                }

                return output;
            }
        }
    }

    /// <summary>Wraps a transport and lets a test hold or stall individual prov-config exchanges.</summary>
    internal sealed class HookedTransport : IProvisioningTransport
    {
        private readonly IProvisioningTransport _inner;
        private int _configCalls;

        public HookedTransport(IProvisioningTransport inner)
        {
            _inner = inner;
        }

        /// <summary>Called before forwarding the Nth (1-based) prov-config exchange.</summary>
        public Func<int, CancellationToken, Task>? BeforeConfig { get; set; }

        public async Task<byte[]> SendAsync(string endpoint, byte[] data, CancellationToken cancellationToken)
        {
            if (endpoint == "prov-config" && BeforeConfig is not null)
            {
                await BeforeConfig(++_configCalls, cancellationToken);
            }

            return await _inner.SendAsync(endpoint, data, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
