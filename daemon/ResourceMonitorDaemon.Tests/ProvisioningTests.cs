using System.Text;
using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Tests
{
    /// <summary>
    /// Tests for the BLE provisioning client (ResourceMonitorDaemon.Provisioning). Expected byte
    /// sequences are derived by hand from the ESP-IDF v4.4.7 .proto field numbers:
    /// tag byte = (field_number &lt;&lt; 3) | wire_type.
    /// </summary>
    internal static class ProvisioningTests
    {
        private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(5);
        private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(5);
        private static readonly ProvisioningDeviceInfo TestDevice = new("PROV_RESMON_A1B2C3", 0x240AC4A1B2C3, -60);

        public static readonly (string Name, Action Run)[] All =
        {
            ("ProtoWire encodes multi-byte and negative varints", () =>
            {
                using var writer = new ProtoWriter();
                writer.WriteVarintField(1, 300);  // 08 AC 02
                writer.WriteInt32Field(4, -1);    // 20 FF FF FF FF FF FF FF FF FF 01
                AssertBytes("08AC02" + "20FFFFFFFFFFFFFFFFFF01", writer.ToArray());

                var reader = new ProtoReader(writer.ToArray());
                AssertTrue(reader.TryReadTag(out var f1, out var w1));
                AssertEqual(1, f1);
                AssertEqual(ProtoWireType.Varint, w1);
                AssertEqual(300UL, reader.ReadVarint());
                AssertTrue(reader.TryReadTag(out var f2, out _));
                AssertEqual(4, f2);
                AssertEqual(-1, reader.ReadInt32());
                AssertTrue(!reader.TryReadTag(out _, out _));
            }),
            ("ProtoWire encodes field numbers above 15 with two-byte tags", () =>
            {
                using var writer = new ProtoWriter();
                writer.WriteBytesField(20, new byte[] { 0xAA }); // (20<<3)|2 = 162 = A2 01
                AssertBytes("A20101AA", writer.ToArray());
            }),
            ("ProtoWire reader skips unknown fields of every wire type", () =>
            {
                // field 9 varint, field 8 fixed64, field 7 fixed32, field 6 bytes, then field 1 varint = 5
                var data = Hex("4896" + "01" + "41" + "0102030405060708" + "3D" + "01020304" + "3203616263" + "0805");
                var reader = new ProtoReader(data);
                ulong? value = null;
                while (reader.TryReadTag(out var field, out var wireType))
                {
                    if (field == 1) { value = reader.ReadVarint(); }
                    else { reader.SkipField(wireType); }
                }

                AssertEqual(5UL, value);
            }),
            ("ProtoWire reader rejects truncated input", () =>
            {
                AssertThrows<ProvisioningProtocolException>(() =>
                {
                    var reader = new ProtoReader(Hex("0A05AABB")); // length 5, only 2 bytes present
                    reader.TryReadTag(out _, out _);
                    reader.ReadLengthDelimited();
                });
                AssertThrows<ProvisioningProtocolException>(() =>
                {
                    var reader = new ProtoReader(Hex("08FF"));     // varint continuation never ends
                    reader.TryReadTag(out _, out _);
                    reader.ReadVarint();
                });
            }),
            ("Sec1 Command0 matches session.proto/sec1.proto layout", () =>
            {
                var pubkey = Sequence(0x01, 32);
                // SessionData: sec_ver(2)=1 -> 10 01; sec1(11) -> 5A len
                //   Sec1Payload: msg omitted (Session_Command0 = 0); sc0(20) -> A2 01 len
                //     SessionCmd0: client_pubkey(1) -> 0A 20 <32 bytes>
                var expected = "1001" + "5A25" + "A20122" + "0A20" + ToHex(pubkey);
                AssertBytes(expected, Sec1Messages.EncodeCommand0(pubkey));
            }),
            ("Sec1 Command1 matches session.proto/sec1.proto layout", () =>
            {
                var verify = Sequence(0x40, 32);
                // Sec1Payload: msg(1)=2 -> 08 02; sc1(22) -> B2 01 len; SessionCmd1: client_verify_data(2) -> 12 20 ...
                var expected = "1001" + "5A27" + "0802" + "B20122" + "1220" + ToHex(verify);
                AssertBytes(expected, Sec1Messages.EncodeCommand1(verify));
            }),
            ("Sec1 Response0 decodes status, device pubkey and random", () =>
            {
                var pubkey = Sequence(0x80, 32);
                var random = Sequence(0x10, 16);
                // Sec1Payload: msg=1 (08 01); sr0(21) -> AA 01 len; SessionResp0: status omitted, 12 20 <pk>, 1A 10 <rand>
                var sr0 = "1220" + ToHex(pubkey) + "1A10" + ToHex(random);   // 34 + 18 = 52 = 0x34
                var sec1 = "0801" + "AA0134" + sr0;                           // 2 + 3 + 52 = 57 = 0x39
                var decoded = Sec1Messages.DecodeResponse(Hex("1001" + "5A39" + sec1));
                AssertEqual(SecSchemeVersion.SecScheme1, decoded.SecVer);
                AssertEqual(Sec1MsgType.SessionResponse0, decoded.MsgType);
                AssertEqual(ProtocommStatus.Success, decoded.Status);
                AssertBytes(ToHex(pubkey), decoded.DevicePubkey!);
                AssertBytes(ToHex(random), decoded.DeviceRandom!);
            }),
            ("Sec1 Response1 decodes non-success status and verify data", () =>
            {
                // Sec1Payload: msg=3 (08 03); sr1(23) -> BA 01 len; SessionResp1: status(1)=6 -> 08 06; device_verify_data(3) -> 1A 02 BEEF
                var decoded = Sec1Messages.DecodeResponse(Hex("1001" + "5A0B" + "0803" + "BA0106" + "0806" + "1A02BEEF"));
                AssertEqual(Sec1MsgType.SessionResponse1, decoded.MsgType);
                AssertEqual(ProtocommStatus.CryptoError, decoded.Status);
                AssertBytes("BEEF", decoded.DeviceVerifyData!);
            }),
            ("WifiConfig SetConfig matches wifi_config.proto layout", () =>
            {
                // WiFiConfigPayload: msg(1)=2 -> 08 02; cmd_set_config(12) -> 62 len
                //   CmdSetConfig: ssid(1) -> 0A 04 "home"; passphrase(2) -> 12 08 "password"
                var encoded = WifiConfigMessages.EncodeSetConfig(Encoding.UTF8.GetBytes("home"), Encoding.UTF8.GetBytes("password"));
                AssertBytes("0802" + "6210" + "0A04" + ToHex(Encoding.UTF8.GetBytes("home")) + "1208" + ToHex(Encoding.UTF8.GetBytes("password")), encoded);
            }),
            ("WifiConfig ApplyConfig and GetStatus match wifi_config.proto layout", () =>
            {
                AssertBytes("0804", WifiConfigMessages.EncodeApplyConfig());  // msg=TypeCmdApplyConfig
                AssertBytes("5200", WifiConfigMessages.EncodeGetStatus());    // msg=0 omitted, empty cmd_get_status(10)
            }),
            ("WifiConfig RespGetStatus decodes Connected with ip4_addr", () =>
            {
                var ip = ToHex(Encoding.ASCII.GetBytes("192.168.1.50"));     // 12 bytes
                // WifiConnectedState: ip4_addr(1) -> 0A 0C ip; auth_mode(2)=3 -> 10 03; channel(5)=6 -> 28 06
                var connected = "0A0C" + ip + "1003" + "2806";               // 14 + 2 + 2 = 18 = 0x12
                // RespGetStatus: status omitted, sta_state omitted (Connected = 0), connected(11) -> 5A 12 ...
                var resp = "5A12" + connected;                               // 20 = 0x14
                var decoded = WifiConfigMessages.DecodeResponse(Hex("0801" + "5A14" + resp));
                AssertEqual(WifiConfigMsgType.RespGetStatus, decoded.MsgType);
                AssertEqual(ProtocommStatus.Success, decoded.Status);
                AssertEqual(WifiStationState.Connected, decoded.StaState);
                AssertEqual("192.168.1.50", decoded.Ip4Address);
            }),
            ("WifiConfig RespGetStatus decodes ConnectionFailed reasons", () =>
            {
                // sta_state(2)=3 -> 10 03; fail_reason(10)=1 -> 50 01
                var decoded = WifiConfigMessages.DecodeResponse(Hex("0801" + "5A04" + "1003" + "5001"));
                AssertEqual(WifiStationState.ConnectionFailed, decoded.StaState);
                AssertEqual(WifiConnectFailedReason.NetworkNotFound, decoded.FailReason);

                // AuthError = 0 may be emitted explicitly (protobuf-c oneof) or omitted; both mean AuthError.
                var explicitZero = WifiConfigMessages.DecodeResponse(Hex("0801" + "5A04" + "1003" + "5000"));
                AssertEqual(WifiConnectFailedReason.AuthError, explicitZero.FailReason);
                var omitted = WifiConfigMessages.DecodeResponse(Hex("0801" + "5A02" + "1003"));
                AssertEqual(WifiConnectFailedReason.AuthError, omitted.FailReason);
            }),
            ("WifiConfig RespSetConfig decodes status", () =>
            {
                // msg=3; resp_set_config(13) -> 6A 02; status(1)=4 -> 08 04
                var decoded = WifiConfigMessages.DecodeResponse(Hex("0803" + "6A02" + "0804"));
                AssertEqual(WifiConfigMsgType.RespSetConfig, decoded.MsgType);
                AssertEqual(ProtocommStatus.InvalidArgument, decoded.Status);
            }),

            // ---- crypto primitives -------------------------------------------------------------

            ("X25519 matches RFC 7748 section 6.1 test vectors", () =>
            {
                using var alice = X25519KeyPair.FromPrivateKey(Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));
                using var bob = X25519KeyPair.FromPrivateKey(Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"));
                AssertBytes("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", alice.PublicKey);
                AssertBytes("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", bob.PublicKey);
                const string shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
                AssertBytes(shared, alice.DeriveSharedSecret(bob.PublicKey));
                AssertBytes(shared, bob.DeriveSharedSecret(alice.PublicKey));
            }),
            ("X25519 rejects small-order device public keys", () =>
            {
                using var key = X25519KeyPair.Generate();
                AssertThrows<ProvisioningProtocolException>(() => key.DeriveSharedSecret(new byte[32]));
                AssertThrows<ProvisioningProtocolException>(() => key.DeriveSharedSecret(new byte[31]));
            }),
            ("AES-256-CTR matches NIST SP 800-38A F.5.5 and F.5.6", () =>
            {
                using var encrypt = new AesCtrStream(Hex(NistCtrKey), Hex(NistCtrCounter));
                AssertBytes(NistCtrCiphertext, encrypt.Transform(Hex(NistCtrPlaintext)));
                using var decrypt = new AesCtrStream(Hex(NistCtrKey), Hex(NistCtrCounter));
                AssertBytes(NistCtrPlaintext, decrypt.Transform(Hex(NistCtrCiphertext)));
            }),
            ("AES-CTR keystream continues across calls of arbitrary length", () =>
            {
                using var stream = new AesCtrStream(Hex(NistCtrKey), Hex(NistCtrCounter));
                var plaintext = Hex(NistCtrPlaintext);
                var output = new List<byte>();
                var offset = 0;
                foreach (var size in new[] { 1, 15, 17, 3, 28 }) // 64 bytes total, crossing block boundaries
                {
                    output.AddRange(stream.Transform(plaintext.AsSpan(offset, size)));
                    offset += size;
                }

                AssertBytes(NistCtrCiphertext, output.ToArray());
            }),
            ("AES-CTR counter increments as a 128-bit big-endian integer", () =>
            {
                var counter = Hex("000000000000000000000000000000FF");
                AesCtrStream.IncrementCounter(counter);
                AssertBytes("00000000000000000000000000000100", counter);
                var wrap = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
                AesCtrStream.IncrementCounter(wrap);
                AssertBytes("00000000000000000000000000000000", wrap);
            }),

            // ---- Security1 + wifi_config against the in-memory fake device ------------------

            ("Fake device round trip: handshake, set config, apply, status Connected", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { ConnectingPolls = 2 };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);

                var result = await client.ConfigureAsync("home-network", "correct horse battery", CancellationToken.None);

                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), result);
                AssertEqual("home-network", device.ReceivedSsid);
                AssertEqual("correct horse battery", device.ReceivedPassphrase);
                AssertTrue(device.Applied, "ApplyConfig was not sent");
                AssertEqual(3, device.StatusPolls); // 2 x Connecting, then Connected: keystream stays aligned
                foreach (var ciphertext in device.ConfigCiphertexts)
                {
                    AssertTrue(!Encoding.UTF8.GetString(ciphertext).Contains("correct horse"), "passphrase sent in clear");
                }
            })),
            ("Fake device: wrong pairing code -> PairingCodeRejectedException", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH");
                var ex = await AssertThrowsAsync<PairingCodeRejectedException>(
                    () => Security1Session.EstablishAsync(device, "ABCDEFGX", CancellationToken.None));
                AssertEqual(PairingCodeRejectionReason.DeviceClosedConnection, ex.Reason);
                AssertTrue(!ex.Message.Contains("ABCDEFG"), "pairing code leaked into message");
            })),
            ("Fake device: mismatched device verify data -> PairingCodeRejectedException", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { ForgeDeviceVerifyData = true };
                var ex = await AssertThrowsAsync<PairingCodeRejectedException>(
                    () => Security1Session.EstablishAsync(device, "ZZZZZZZZ", CancellationToken.None));
                AssertEqual(PairingCodeRejectionReason.VerificationMismatch, ex.Reason);
            })),
            ("Fake device: AuthError -> Failed(AuthError)", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH")
                {
                    FinalState = WifiStationState.ConnectionFailed,
                    FinalFailReason = WifiConnectFailedReason.AuthError,
                };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var result = await new WifiConfigClient(session, FastPoll, FastTimeout)
                    .ConfigureAsync("home-network", "wrong password", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Failed(WifiConnectFailedReason.AuthError), result);
            })),
            ("Fake device: NetworkNotFound -> Failed(NetworkNotFound)", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH")
                {
                    FinalState = WifiStationState.ConnectionFailed,
                    FinalFailReason = WifiConnectFailedReason.NetworkNotFound,
                };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var result = await new WifiConfigClient(session, FastPoll, FastTimeout)
                    .ConfigureAsync("missing", "password1", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Failed(WifiConnectFailedReason.NetworkNotFound), result);
            })),
            ("Fake device: never leaves Connecting -> Timeout", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { ConnectingPolls = int.MaxValue };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var result = await new WifiConfigClient(session, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(120))
                    .ConfigureAsync("home-network", "password1", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Timeout(WifiStationState.Connecting), result);
                AssertTrue(device.StatusPolls is >= 2 and <= 13, $"unexpected poll count {device.StatusPolls}");
            })),
            ("Fake device: rejected SetConfig error does not leak the passphrase", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { SetConfigStatus = ProtocommStatus.InvalidArgument };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var ex = await AssertThrowsAsync<ProvisioningProtocolException>(
                    () => new WifiConfigClient(session, FastPoll, FastTimeout).SetConfigAsync("home-network", "s3cret-pass", CancellationToken.None));
                AssertTrue(!ex.ToString().Contains("s3cret-pass"), "passphrase leaked into exception");
            })),
            ("WifiConfigClient validates SSID/passphrase length without echoing them", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH");
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);
                var longPass = new string('p', 64);
                var ex = await AssertThrowsAsync<ArgumentException>(() => client.SetConfigAsync("home", longPass, CancellationToken.None));
                AssertTrue(!ex.Message.Contains(longPass), "passphrase leaked into exception");
                await AssertThrowsAsync<ArgumentException>(() => client.SetConfigAsync(new string('s', 33), "password1", CancellationToken.None));
                await AssertThrowsAsync<ArgumentException>(() => client.SetConfigAsync("", "password1", CancellationToken.None));
                AssertEqual(0, device.ConfigCiphertexts.Count);
            })),
            ("Security1Session refuses further use after a half-completed exchange", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { FailNextConfigExchange = true };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);
                await AssertThrowsAsync<ProvisioningTransportException>(() => client.GetStatusAsync(CancellationToken.None));
                var ex = await AssertThrowsAsync<ProvisioningException>(() => client.GetStatusAsync(CancellationToken.None));
                AssertTrue(ex is not ProvisioningTransportException, "expected session-interrupted error");
            })),
            ("WifiConfigClient normalizes reported IPv4 addresses", () =>
            {
                AssertEqual("192.168.1.50", WifiConfigClient.NormalizeIp4("192.168.1.50"));
                AssertEqual("10.0.0.7", WifiConfigClient.NormalizeIp4(" 10.0.0.7\0"));
                AssertEqual<string?>(null, WifiConfigClient.NormalizeIp4(""));
                AssertEqual<string?>(null, WifiConfigClient.NormalizeIp4("fe80::1"));
                AssertEqual<string?>(null, WifiConfigClient.NormalizeIp4("12345"));
            }),

            // ---- BLE helpers (pure logic only; WinRT calls need real hardware) ----------------

            ("Scan aggregator merges scan responses, filters and de-duplicates", () =>
            {
                var aggregator = new ScanResultAggregator();
                const ulong esp = 0x24_0A_C4_12_34_56;
                const ulong other = 0x11_22_33_44_55_66;

                AssertEqual<ProvisioningDeviceInfo?>(null, aggregator.Observe(esp, null, -70));         // ADV_IND without name
                var first = aggregator.Observe(esp, "PROV_RESMON_123456", -68);                          // SCAN_RSP with name
                AssertEqual(new ProvisioningDeviceInfo("PROV_RESMON_123456", esp, -68), first);
                AssertEqual<ProvisioningDeviceInfo?>(null, aggregator.Observe(esp, "PROV_RESMON_123456", -60)); // repeat
                AssertEqual<ProvisioningDeviceInfo?>(null, aggregator.Observe(esp, "", -55));            // nameless repeat keeps name
                AssertEqual<ProvisioningDeviceInfo?>(null, aggregator.Observe(other, "PROV_OTHER_ABCDEF", -40));
                AssertEqual<ProvisioningDeviceInfo?>(null, aggregator.Observe(other, "prov_resmon_abcdef", -40)); // case-sensitive prefix
                AssertEqual<ProvisioningDeviceInfo?>(null, aggregator.Observe(0x1, "PROV_RESMON_", -40));         // prefix only

                var snapshot = aggregator.Snapshot();
                AssertEqual(1, snapshot.Count);
                AssertEqual(new ProvisioningDeviceInfo("PROV_RESMON_123456", esp, -55), snapshot[0]);
                AssertEqual("24:0A:C4:12:34:56", snapshot[0].AddressText);
            }),
            ("Scan aggregator orders devices by signal strength", () =>
            {
                var aggregator = new ScanResultAggregator();
                aggregator.Observe(1, "PROV_RESMON_AAAAAA", -80);
                aggregator.Observe(2, "PROV_RESMON_BBBBBB", -50);
                var snapshot = aggregator.Snapshot();
                AssertEqual("PROV_RESMON_BBBBBB", snapshot[0].Name);
                AssertEqual("PROV_RESMON_AAAAAA", snapshot[1].Name);
            }),
            ("0x2901 user description text maps to lower-case endpoint names", () =>
            {
                AssertEqual("prov-session", BleProvisioningTransport.ParseUserDescription(Encoding.UTF8.GetBytes("prov-session")));
                AssertEqual("prov-config", BleProvisioningTransport.ParseUserDescription(Encoding.UTF8.GetBytes("Prov-Config\0")));
                AssertEqual("proto-ver", BleProvisioningTransport.ParseUserDescription(Encoding.UTF8.GetBytes(" proto-ver \n")));
            }),

            // ---- USB serial pairing-code query ----------------------------------------------

            ("PopLineParser parses a valid reply", () =>
            {
                AssertEqual<PopReply?>(new PopReply.Available("PROV_RESMON_A1B2C3", "ABCDEFGH"),
                    PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=ABCDEFGH"));
            }),
            ("PopLineParser accepts CRLF and LF line endings and log noise before the reply", () =>
            {
                var expected = new PopReply.Available("PROV_RESMON_A1B2C3", "XYZ23456");
                AssertEqual<PopReply?>(expected, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=XYZ23456\r\n"));
                AssertEqual<PopReply?>(expected, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=XYZ23456\r"));
                AssertEqual<PopReply?>(expected, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=XYZ23456\n"));
                AssertEqual<PopReply?>(expected, PopLineParser.Parse("\u001b[0mRESMON-POP name=PROV_RESMON_A1B2C3 pop=XYZ23456"));
            }),
            ("PopLineParser parses the unavailable reply", () =>
            {
                AssertEqual<PopReply?>(new PopReply.Unavailable("provisioned"), PopLineParser.Parse("RESMON-POP unavailable state=provisioned\r\n"));
                AssertEqual<PopReply?>(new PopReply.Unavailable("connecting"), PopLineParser.Parse("RESMON-POP unavailable state=connecting"));
                AssertEqual<PopReply?>(new PopReply.Unavailable("unknown"), PopLineParser.Parse("RESMON-POP unavailable"));
            }),
            ("PopLineParser ignores garbage, boot logs, the hint line and the echoed query", () =>
            {
                foreach (var line in new[]
                {
                    "", "\r\n", "ets Jun  8 2016 00:22:57", "rst:0x1 (POWERON_RESET),boot:0x13 (SPI_FAST_FLASH_BOOT)",
                    "I (512) wifi_prov_mgr: Provisioning started with service name : PROV_RESMON_A1B2C3",
                    "Provisioning: name=PROV_RESMON_A1B2C3 pop=ABCDEFGH", "RESMON?POP", "RESMON-POP", "RESMON-POP ",
                    "RESMON-POP name=PROV_RESMON_A1B2C3", "RESMON-POP pop=ABCDEFGH", "ÿþ\u0000RESMON",
                })
                {
                    AssertEqual<PopReply?>(null, PopLineParser.Parse(line));
                }
            }),
            ("PopLineParser rejects wrong names and malformed codes", () =>
            {
                AssertEqual<PopReply?>(null, PopLineParser.Parse("RESMON-POP name=PROV_OTHER_A1B2C3 pop=ABCDEFGH"));
                AssertEqual<PopReply?>(null, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_a1b2c3 pop=ABCDEFGH")); // lowercase hex
                AssertEqual<PopReply?>(null, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3D pop=ABCDEFGH"));
                AssertEqual<PopReply?>(null, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=ABCDEFG0")); // 0 not in alphabet
                AssertEqual<PopReply?>(null, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=ABCDEFGHJ"));
                AssertEqual<PopReply?>(null, PopLineParser.Parse("RESMON-POP name=PROV_RESMON_A1B2C3 pop=abcdefgh"));
            }),
            ("SerialPopReader picks the code of the device being paired only", () =>
            {
                var matches = new[]
                {
                    new UsbPopMatch("COM3", "PROV_RESMON_111111", "AAAAAAAA"),
                    new UsbPopMatch("COM7", "PROV_RESMON_A1B2C3", "BBBBBBBB"),
                };
                AssertEqual("BBBBBBBB", SerialPopReader.FindPop(matches, "PROV_RESMON_A1B2C3"));
                AssertEqual<string?>(null, SerialPopReader.FindPop(matches, "PROV_RESMON_999999"));
            }),
            ("Pairing code values are redacted from ToString and normalized from user input", () =>
            {
                AssertTrue(!new UsbPopMatch("COM3", "PROV_RESMON_111111", "SECRETPP").ToString().Contains("SECRETPP"), "UsbPopMatch leaks pop");
                AssertTrue(!new PopReply.Available("PROV_RESMON_111111", "SECRETPP").ToString().Contains("SECRETPP"), "PopReply leaks pop");
                AssertTrue(PairingCode.TryNormalize(" abcd-efgh ", out var code));
                AssertEqual("ABCDEFGH", code);
                AssertTrue(!PairingCode.TryNormalize("ABCDEFG", out _), "7 chars accepted");
                AssertTrue(!PairingCode.TryNormalize("ABCDEFGI", out _), "I accepted");
                AssertTrue(!PairingCode.TryNormalize(null, out _), "null accepted");
            }),

            // ---- ProvisioningService facade --------------------------------------------------

            ("ProvisioningService provisions the fake device and reports steps in order", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("K7M3P9QX");
                var logger = new CapturingLogger();
                var service = new ProvisioningService(logger, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);
                var steps = new SyncProgress<ProvisioningStep>();

                var result = await service.ProvisionAsync(TestDevice, "k7m3-p9qx", "home-network", "hunter2-wifi-pass", steps, CancellationToken.None);

                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), result);
                AssertEqual(
                    "ConnectingToDevice,SecuringSession,SendingWifiConfig,WaitingForWifiConnection",
                    string.Join(",", steps.Items));
                AssertEqual("proto-ver", device.EndpointLog[0]);
                AssertTrue(device.Disposed, "transport was not disposed");
                AssertTrue(logger.Lines.Any(l => l.Contains("v1.1")), "protocol version was not logged");
                AssertTrue(logger.Lines.All(l => !l.Contains("hunter2-wifi-pass") && !l.Contains("K7M3P9QX") && !l.Contains("home-network")),
                    "secret or SSID leaked into the log");
            })),
            ("ProvisioningService continues when proto-ver is absent", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("K7M3P9QX") { SupportsVersionEndpoint = false };
                var service = new ProvisioningService(null, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);
                var result = await service.ProvisionAsync(TestDevice, "K7M3P9QX", "home-network", "password1", null, CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), result);
            })),
            ("ProvisioningService surfaces a wrong pairing code and disposes the transport", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("K7M3P9QX");
                var logger = new CapturingLogger();
                var service = new ProvisioningService(logger, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);
                await AssertThrowsAsync<PairingCodeRejectedException>(
                    () => service.ProvisionAsync(TestDevice, "K7M3P9QZ", "home-network", "password1", null, CancellationToken.None));
                AssertTrue(device.Disposed, "transport was not disposed");
                AssertTrue(device.ReceivedPassphrase is null, "credentials sent without a session");
            })),
            ("ProvisioningService validates input before connecting", () => RunAsync(async () =>
            {
                var connected = false;
                var service = new ProvisioningService(null, (_, _) =>
                {
                    connected = true;
                    return Task.FromResult<IProvisioningTransport>(new FakeProvisioningDevice("K7M3P9QX"));
                }, FastPoll, FastTimeout);
                await AssertThrowsAsync<ArgumentException>(
                    () => service.ProvisionAsync(TestDevice, "K7M3P9Q", "home-network", "password1", null, CancellationToken.None));
                await AssertThrowsAsync<ArgumentException>(
                    () => service.ProvisionAsync(TestDevice, "K7M3P9QX", "home-network", new string('x', 64), null, CancellationToken.None));
                AssertTrue(!connected, "connected despite invalid input");
            })),
            ("ProtocolVersionInfo parses JSON and plain version strings", () =>
            {
                var json = ProtocolVersionInfo.Parse(Encoding.UTF8.GetBytes("{\n\t\"prov\":\t{\n\t\t\"ver\":\t\"v1.1\",\n\t\t\"cap\":\t[\"wifi_scan\"]\n\t}\n}"));
                AssertEqual("v1.1", json.Version);
                AssertEqual("wifi_scan", string.Join(",", json.Capabilities));
                AssertEqual("v1.0", ProtocolVersionInfo.Parse(Encoding.UTF8.GetBytes("v1.0")).Version);
                AssertEqual<string?>(null, ProtocolVersionInfo.Parse(Array.Empty<byte>()).Version);
            }),

            // ---- review follow-ups -----------------------------------------------------------

            ("ProtoWire rejects out-of-range tags with ProvisioningProtocolException", () =>
            {
                foreach (var hex in new[]
                {
                    "F8FFFFFF7F",           // field number 0xFFFFFFFF (overflowed int before the fix)
                    "8080808010",           // field number 2^29, one above the maximum
                    "FFFFFFFFFFFFFFFFFF01", // 64-bit tag
                    "0E00", "0F00",         // wire types 6 and 7
                    "0700",                 // field 0
                })
                {
                    AssertThrows<ProvisioningProtocolException>(() =>
                    {
                        var reader = new ProtoReader(Hex(hex));
                        reader.TryReadTag(out _, out _);
                    });
                    AssertThrows<ProvisioningProtocolException>(() => Sec1Messages.DecodeResponse(Hex(hex)));
                    AssertThrows<ProvisioningProtocolException>(() => WifiConfigMessages.DecodeResponse(Hex(hex)));
                }

                // Largest legal field number still decodes: tag (2^29-1)<<3 | 0 = F8 FF FF FF 0F.
                var ok = new ProtoReader(Hex("F8FFFFFF0F00"));
                AssertTrue(ok.TryReadTag(out var field, out _));
                AssertEqual(0x1FFFFFFF, field);
            }),
            ("ProtoWire rejects huge or truncated lengths", () =>
            {
                foreach (var hex in new[] { "0AFFFFFFFF0F", "0AFFFFFFFFFFFFFFFF7F", "0A80", "0A02AA" })
                {
                    AssertThrows<ProvisioningProtocolException>(() => Sec1Messages.DecodeResponse(Hex(hex)));
                    AssertThrows<ProvisioningProtocolException>(() => WifiConfigMessages.DecodeResponse(Hex("5A" + hex[2..])));
                }
            }),
            ("Message decoders only ever throw ProvisioningProtocolException on random input", () =>
            {
                var random = new Random(7748);
                var seeds = new[]
                {
                    Sec1Messages.EncodeCommand0(Sequence(1, 32)),
                    Hex("1001" + "5A0B" + "0803" + "BA0106" + "0806" + "1A02BEEF"),
                    Hex("0801" + "5A04" + "1003" + "5001"),
                    WifiConfigMessages.EncodeSetConfig(Encoding.UTF8.GetBytes("home"), Encoding.UTF8.GetBytes("password")),
                };
                for (var i = 0; i < 20000; i++)
                {
                    byte[] input;
                    if (i % 2 == 0)
                    {
                        input = new byte[random.Next(0, 48)];
                        random.NextBytes(input);
                    }
                    else
                    {
                        input = (byte[])seeds[random.Next(seeds.Length)].Clone();
                        for (var m = random.Next(1, 4); m > 0; m--)
                        {
                            input[random.Next(input.Length)] = (byte)random.Next(256);
                        }
                    }

                    foreach (var decode in new Action<byte[]>[] { b => Sec1Messages.DecodeResponse(b), b => WifiConfigMessages.DecodeResponse(b) })
                    {
                        try
                        {
                            decode(input);
                        }
                        catch (ProvisioningProtocolException)
                        {
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"{ex.GetType().Name} for input {ToHex(input)}");
                        }
                    }
                }
            }),
            ("Empty RespGetStatus is not mistaken for Connected", () => RunAsync(async () =>
            {
                AssertEqual(WifiStationState.Connecting, WifiConfigMessages.DecodeResponse(Hex("0801" + "5A00")).StaState);
                AssertEqual(WifiStationState.Connecting, WifiConfigMessages.DecodeResponse(Hex("0801")).StaState);

                var device = new FakeProvisioningDevice("ABCDEFGH") { ConnectingPolls = 0, EmptyStatusPolls = 2 };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var result = await new WifiConfigClient(session, FastPoll, FastTimeout).ConfigureAsync("home", "password1", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), result);
                AssertEqual(3, device.StatusPolls);
            })),
            ("Stale Response0 returned for Command1 -> PairingCodeRejectedException", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { StaleResponse0OnWrongPop = true };
                var ex = await AssertThrowsAsync<PairingCodeRejectedException>(
                    () => Security1Session.EstablishAsync(device, "ZZZZZZZZ", CancellationToken.None));
                AssertEqual(PairingCodeRejectionReason.DeviceClosedConnection, ex.Reason);
            })),
            ("Link lost while waiting for Wi-Fi -> ConnectionLost result", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { DisconnectWhileConnecting = true };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var result = await new WifiConfigClient(session, FastPoll, FastTimeout).ConfigureAsync("home", "password1", CancellationToken.None);
                var lost = result as WifiProvisioningResult.ConnectionLost;
                AssertTrue(lost is not null, $"expected ConnectionLost, got {result}");
                AssertTrue(lost!.Reason.Contains("may have joined Wi-Fi"), "reason should tell the user the device may be connected");
            })),
            ("Link dropped right after the Connected read still yields Connected", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { CloseLinkAfterConnected = true };
                var service = new ProvisioningService(null, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);
                var result = await service.ProvisionAsync(TestDevice, "ABCDEFGH", "home", "password1", null, CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), result);
            })),
            ("Retry after Failed without device reset -> ProvisioningRetryRejectedException", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { FinalState = WifiStationState.ConnectionFailed };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);
                var first = await client.ConfigureAsync("home", "wrong-pass", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Failed(WifiConnectFailedReason.AuthError), first);

                var ex = await AssertThrowsAsync<ProvisioningRetryRejectedException>(
                    () => client.ConfigureAsync("home", "right-pass", CancellationToken.None));
                AssertTrue(ex.Message.Contains("BOOT button for 5–15 seconds, then release"), "message should explain how to restart provisioning");
                AssertTrue(ex.PreviousAttemptFailed, "previous failure not detected");
                AssertTrue(!ex.ToString().Contains("right-pass"), "passphrase leaked");
            })),
            ("Retry after Failed with device reset_sm_state_on_failure succeeds", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { FinalState = WifiStationState.ConnectionFailed, ResetStateOnFailure = true };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);
                await client.ConfigureAsync("home", "wrong-pass", CancellationToken.None);
                device.FinalState = WifiStationState.Connected;
                var second = await client.ConfigureAsync("home", "right-pass", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), second);
                AssertEqual("right-pass", device.ReceivedPassphrase);
            })),
            ("WaitForConnectionAsync stays within its budget when GetStatus hangs", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH");
                var hooked = new HookedTransport(device)
                {
                    // 1 = initial GetStatus, 2 = SetConfig, 3 = ApplyConfig, 4+ = polls: the first poll never gets an answer.
                    BeforeConfig = (n, ct) => n >= 4 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask,
                };
                using var session = await Security1Session.EstablishAsync(hooked, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(300));
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await client.ConfigureAsync("home", "password1", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Timeout(WifiStationState.Connecting), result);
                AssertTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"overran budget: {stopwatch.Elapsed}");
            })),
            ("ProvisionAsync twice: device without failure reset -> ProvisioningRetryRejectedException", () => RunAsync(async () =>
            {
                // One fake device across two connections: session state resets on Command0, Wi-Fi state persists.
                var device = new FakeProvisioningDevice("K7M3P9QX") { FinalState = WifiStationState.ConnectionFailed };
                var service = new ProvisioningService(null, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);

                var first = await service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "wrong-pass", null, CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Failed(WifiConnectFailedReason.AuthError), first);

                var ex = await AssertThrowsAsync<ProvisioningRetryRejectedException>(
                    () => service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "right-pass", null, CancellationToken.None));
                AssertTrue(ex.PreviousAttemptFailed, "earlier failure from the previous session was not detected");
                AssertTrue(ex.Message.Contains("5–15 seconds, then release"), "missing BOOT instructions");
                AssertTrue(!ex.ToString().Contains("right-pass"), "passphrase leaked");
            })),
            ("ProvisionAsync twice: device with failure reset -> second attempt Connected", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("K7M3P9QX")
                {
                    FinalState = WifiStationState.ConnectionFailed,
                    ResetStateOnFailure = true,
                };
                var service = new ProvisioningService(null, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);

                var first = await service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "wrong-pass", null, CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Failed(WifiConnectFailedReason.AuthError), first);

                device.FinalState = WifiStationState.Connected;
                var second = await service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "right-pass", null, CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50"), second);
                AssertEqual("right-pass", device.ReceivedPassphrase);
            })),
            ("ApplyConfig InternalError always gives restart guidance", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("K7M3P9QX") { ApplyStatusOverride = ProtocommStatus.InternalError };
                var service = new ProvisioningService(null, (_, _) => Task.FromResult<IProvisioningTransport>(device), FastPoll, FastTimeout);
                var ex = await AssertThrowsAsync<ProvisioningRetryRejectedException>(
                    () => service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "password1", null, CancellationToken.None));
                AssertTrue(!ex.PreviousAttemptFailed, "no earlier failure was reported by the device");
                AssertTrue(ex.Message.Contains("Power-cycle the device"), "missing restart guidance");

                device.ApplyStatusOverride = ProtocommStatus.InvalidArgument;
                var other = await AssertThrowsAsync<ProvisioningProtocolException>(
                    () => service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "password1", null, CancellationToken.None));
                AssertTrue(other is not ProvisioningRetryRejectedException, "only InternalError means a stuck state machine");
            })),
            ("ProvisionAsync on a device that already joined Wi-Fi returns Connected without resending credentials", () => RunAsync(async () =>
            {
                // First run: the device takes longer than the (shortened) wait, so the client reports Timeout.
                var device = new FakeProvisioningDevice("K7M3P9QX") { ConnectingPolls = int.MaxValue };
                var logger = new CapturingLogger();
                var service = new ProvisioningService(logger, (_, _) => Task.FromResult<IProvisioningTransport>(device),
                    TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100));
                var first = await service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "first-pass", null, CancellationToken.None);
                AssertTrue(first is WifiProvisioningResult.Timeout, $"expected Timeout, got {first}");
                AssertEqual(1, device.SetConfigCount);

                // The device joins afterwards; the user retries with the same device.
                device.ConnectingPolls = 0;
                var steps = new SyncProgress<ProvisioningStep>();
                var second = await service.ProvisionAsync(TestDevice, "K7M3P9QX", "home", "second-pass", steps, CancellationToken.None);

                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50") { AlreadyConnected = true }, second);
                AssertEqual(1, device.SetConfigCount);               // credentials were not sent again
                AssertEqual("first-pass", device.ReceivedPassphrase);
                AssertTrue(!steps.Items.Contains(ProvisioningStep.WaitingForWifiConnection), "should not wait again");
                AssertTrue(logger.Lines.Any(l => l.Contains("already connected")), "missing 'already connected' log");
            })),
            ("ConfigureAsync on an already-connected device returns Connected without SetConfig", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { ConnectingPolls = 0 };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);
                var first = await client.ConfigureAsync("home", "first-pass", CancellationToken.None);
                AssertTrue(first is WifiProvisioningResult.Connected { AlreadyConnected: false }, "first attempt sent the credentials");
                var again = await client.ConfigureAsync("home", "second-pass", CancellationToken.None);
                AssertEqual<WifiProvisioningResult>(new WifiProvisioningResult.Connected("192.168.1.50") { AlreadyConnected = true }, again);
                AssertEqual(1, device.SetConfigCount);
            })),
            ("ConnectionLost wording does not assume a disconnect", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH") { DisconnectWhileConnecting = true };
                using var session = await Security1Session.EstablishAsync(device, "ABCDEFGH", CancellationToken.None);
                var result = await new WifiConfigClient(session, FastPoll, FastTimeout).ConfigureAsync("home", "password1", CancellationToken.None);
                var lost = (WifiProvisioningResult.ConnectionLost)result;
                AssertTrue(lost.Reason.StartsWith("Lost contact with the device"), lost.Reason);
            })),
            ("Security1Session.Dispose during an in-flight exchange is safe", () => RunAsync(async () =>
            {
                var device = new FakeProvisioningDevice("ABCDEFGH");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var hooked = new HookedTransport(device)
                {
                    BeforeConfig = (_, _) => { entered.TrySetResult(); return release.Task; },
                };
                var session = await Security1Session.EstablishAsync(hooked, "ABCDEFGH", CancellationToken.None);
                var client = new WifiConfigClient(session, FastPoll, FastTimeout);

                var inFlight = client.GetStatusAsync(CancellationToken.None);
                await entered.Task;
                session.Dispose();
                release.SetResult();

                var status = await inFlight; // must not throw ObjectDisposedException from the semaphore or cipher
                AssertEqual(WifiStationState.Connecting, status.State); // unconfigured device, like real v4.4.7
                await AssertThrowsAsync<ObjectDisposedException>(() => client.GetStatusAsync(CancellationToken.None));
                session.Dispose(); // idempotent
            })),
        };

        /// <summary>IProgress that records synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            public List<T> Items { get; } = new();
            public void Report(T value) => Items.Add(value);
        }

        /// <summary>Records every formatted log line and exception text.</summary>
        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public List<string> Lines { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (Lines)
                {
                    Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
                }
            }
        }

        // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt / F.5.6 CTR-AES256.Decrypt
        private const string NistCtrKey = "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4";
        private const string NistCtrCounter = "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff";
        private const string NistCtrPlaintext =
            "6bc1bee22e409f96e93d7e117393172a" + "ae2d8a571e03ac9c9eb76fac45af8e51" +
            "30c81c46a35ce411e5fbc1191a0a52ef" + "f69f2445df4f9b17ad2b417be66c3710";
        private const string NistCtrCiphertext =
            "601ec313775789a5b7a7f504bbf3d228" + "f443e3ca4d62b59aca84e990cacaf5c5" +
            "2b0930daa23de94ce87017ba2d84988d" + "dfc9c58db67aada613c2dd08457941a6";

        private static void RunAsync(Func<Task> test) => test().GetAwaiter().GetResult();

        // ---- assertion helpers -------------------------------------------------------------

        internal static void AssertTrue(bool condition, string message = "condition was false")
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        internal static void AssertEqual<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
            }
        }

        internal static void AssertBytes(string expectedHex, byte[] actual)
        {
            var actualHex = ToHex(actual);
            if (!string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"expected {expectedHex.ToUpperInvariant()}, got {actualHex}");
            }
        }

        internal static TException AssertThrows<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
            }

            throw new InvalidOperationException($"expected {typeof(TException).Name}, nothing was thrown");
        }

        internal static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
            }

            throw new InvalidOperationException($"expected {typeof(TException).Name}, nothing was thrown");
        }

        internal static byte[] Hex(string hex) => Convert.FromHexString(hex);

        internal static string ToHex(byte[] data) => Convert.ToHexString(data);

        internal static byte[] Sequence(byte start, int count)
        {
            var result = new byte[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = unchecked((byte)(start + i));
            }

            return result;
        }
    }
}
