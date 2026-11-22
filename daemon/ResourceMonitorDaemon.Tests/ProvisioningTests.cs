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
        };

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
