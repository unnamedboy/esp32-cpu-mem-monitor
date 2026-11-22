using System.Text;

// Wire formats transcribed from ESP-IDF v4.4.7 (the IDF bundled with Arduino-ESP32 2.0.17):
//   https://github.com/espressif/esp-idf/blob/v4.4.7/components/protocomm/proto/constants.proto
//   https://github.com/espressif/esp-idf/blob/v4.4.7/components/protocomm/proto/session.proto
//   https://github.com/espressif/esp-idf/blob/v4.4.7/components/protocomm/proto/sec1.proto
//   https://github.com/espressif/esp-idf/blob/v4.4.7/components/wifi_provisioning/proto/wifi_constants.proto
//   https://github.com/espressif/esp-idf/blob/v4.4.7/components/wifi_provisioning/proto/wifi_config.proto
// Which fields are populated follows the reference client:
//   https://github.com/espressif/esp-idf/blob/v4.4.7/tools/esp_prov/security/security1.py
//   https://github.com/espressif/esp-idf/blob/v4.4.7/tools/esp_prov/prov/wifi_prov.py
// All protos are proto3, so fields holding their default value (0, empty) are omitted.

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>constants.proto: Status.</summary>
    public enum ProtocommStatus
    {
        Success = 0,
        InvalidSecScheme = 1,
        InvalidProto = 2,
        TooManySessions = 3,
        InvalidArgument = 4,
        InternalError = 5,
        CryptoError = 6,
        InvalidSession = 7,
    }

    /// <summary>wifi_constants.proto: WifiStationState.</summary>
    public enum WifiStationState
    {
        Connected = 0,
        Connecting = 1,
        Disconnected = 2,
        ConnectionFailed = 3,
    }

    /// <summary>wifi_constants.proto: WifiConnectFailedReason.</summary>
    public enum WifiConnectFailedReason
    {
        AuthError = 0,
        NetworkNotFound = 1,
    }

    /// <summary>session.proto: SecSchemeVersion.</summary>
    internal enum SecSchemeVersion
    {
        SecScheme0 = 0,
        SecScheme1 = 1,
    }

    /// <summary>sec1.proto: Sec1MsgType.</summary>
    internal enum Sec1MsgType
    {
        SessionCommand0 = 0,
        SessionResponse0 = 1,
        SessionCommand1 = 2,
        SessionResponse1 = 3,
    }

    /// <summary>wifi_config.proto: WiFiConfigMsgType.</summary>
    internal enum WifiConfigMsgType
    {
        CmdGetStatus = 0,
        RespGetStatus = 1,
        CmdSetConfig = 2,
        RespSetConfig = 3,
        CmdApplyConfig = 4,
        RespApplyConfig = 5,
    }

    /// <summary>Field numbers from the .proto files above.</summary>
    internal static class ProtoFields
    {
        // SessionData
        public const int SessionDataSecVer = 2;
        public const int SessionDataSec0 = 10;
        public const int SessionDataSec1 = 11;

        // Sec1Payload
        public const int Sec1Msg = 1;
        public const int Sec1Sc0 = 20;
        public const int Sec1Sr0 = 21;
        public const int Sec1Sc1 = 22;
        public const int Sec1Sr1 = 23;

        // SessionCmd0 / SessionResp0
        public const int Cmd0ClientPubkey = 1;
        public const int Resp0Status = 1;
        public const int Resp0DevicePubkey = 2;
        public const int Resp0DeviceRandom = 3;

        // SessionCmd1 / SessionResp1
        public const int Cmd1ClientVerifyData = 2;
        public const int Resp1Status = 1;
        public const int Resp1DeviceVerifyData = 3;

        // WiFiConfigPayload
        public const int WifiMsg = 1;
        public const int WifiCmdGetStatus = 10;
        public const int WifiRespGetStatus = 11;
        public const int WifiCmdSetConfig = 12;
        public const int WifiRespSetConfig = 13;
        public const int WifiCmdApplyConfig = 14;
        public const int WifiRespApplyConfig = 15;

        // CmdSetConfig
        public const int SetConfigSsid = 1;
        public const int SetConfigPassphrase = 2;
        public const int SetConfigBssid = 3;
        public const int SetConfigChannel = 4;

        // RespGetStatus / RespSetConfig / RespApplyConfig
        public const int RespStatus = 1;
        public const int GetStatusStaState = 2;
        public const int GetStatusFailReason = 10;
        public const int GetStatusConnected = 11;

        // WifiConnectedState
        public const int ConnectedIp4Addr = 1;
        public const int ConnectedAuthMode = 2;
        public const int ConnectedSsid = 3;
        public const int ConnectedBssid = 4;
        public const int ConnectedChannel = 5;
    }

    /// <summary>Decoded SessionData carrying a Sec1 response (Response0 or Response1).</summary>
    internal sealed record SessionResponse(
        SecSchemeVersion SecVer,
        Sec1MsgType MsgType,
        ProtocommStatus Status,
        byte[]? DevicePubkey,
        byte[]? DeviceRandom,
        byte[]? DeviceVerifyData);

    /// <summary>Decoded WiFiConfigPayload response.</summary>
    internal sealed record WifiConfigResponse(
        WifiConfigMsgType MsgType,
        ProtocommStatus Status,
        WifiStationState StaState,
        WifiConnectFailedReason FailReason,
        string? Ip4Address,
        string? ConnectedSsid);

    /// <summary>Encoders/decoders for the Security1 session handshake (endpoint prov-session).</summary>
    internal static class Sec1Messages
    {
        /// <summary>
        /// SessionData{sec_ver=SecScheme1, sec1{sc0{client_pubkey}}}. msg=Session_Command0 is the proto3
        /// default and is therefore not emitted, exactly like security1.py setup0_request().
        /// </summary>
        public static byte[] EncodeCommand0(ReadOnlySpan<byte> clientPubkey)
        {
            var key = clientPubkey.ToArray();
            using var writer = new ProtoWriter();
            writer.WriteVarintField(ProtoFields.SessionDataSecVer, (ulong)SecSchemeVersion.SecScheme1);
            writer.WriteMessageField(ProtoFields.SessionDataSec1, sec1 =>
                sec1.WriteMessageField(ProtoFields.Sec1Sc0, sc0 =>
                    sc0.WriteBytesField(ProtoFields.Cmd0ClientPubkey, key)));
            return writer.ToArray();
        }

        /// <summary>SessionData{sec_ver=SecScheme1, sec1{msg=Session_Command1, sc1{client_verify_data}}}.</summary>
        public static byte[] EncodeCommand1(ReadOnlySpan<byte> clientVerifyData)
        {
            var verify = clientVerifyData.ToArray();
            using var writer = new ProtoWriter();
            writer.WriteVarintField(ProtoFields.SessionDataSecVer, (ulong)SecSchemeVersion.SecScheme1);
            writer.WriteMessageField(ProtoFields.SessionDataSec1, sec1 =>
            {
                sec1.WriteVarintField(ProtoFields.Sec1Msg, (ulong)Sec1MsgType.SessionCommand1);
                sec1.WriteMessageField(ProtoFields.Sec1Sc1, sc1 =>
                    sc1.WriteBytesField(ProtoFields.Cmd1ClientVerifyData, verify));
            });
            return writer.ToArray();
        }

        public static SessionResponse DecodeResponse(ReadOnlySpan<byte> data)
        {
            var secVer = SecSchemeVersion.SecScheme0;
            var msgType = Sec1MsgType.SessionCommand0;
            var status = ProtocommStatus.Success;
            byte[]? devicePubkey = null;
            byte[]? deviceRandom = null;
            byte[]? deviceVerify = null;

            var reader = new ProtoReader(data);
            while (reader.TryReadTag(out var field, out var wireType))
            {
                if (field == ProtoFields.SessionDataSecVer && wireType == ProtoWireType.Varint)
                {
                    secVer = (SecSchemeVersion)reader.ReadInt32();
                }
                else if (field == ProtoFields.SessionDataSec1 && wireType == ProtoWireType.LengthDelimited)
                {
                    var sec1 = new ProtoReader(reader.ReadLengthDelimited());
                    while (sec1.TryReadTag(out var f1, out var w1))
                    {
                        if (f1 == ProtoFields.Sec1Msg && w1 == ProtoWireType.Varint)
                        {
                            msgType = (Sec1MsgType)sec1.ReadInt32();
                        }
                        else if (f1 == ProtoFields.Sec1Sr0 && w1 == ProtoWireType.LengthDelimited)
                        {
                            var sr0 = new ProtoReader(sec1.ReadLengthDelimited());
                            while (sr0.TryReadTag(out var f2, out var w2))
                            {
                                if (f2 == ProtoFields.Resp0Status && w2 == ProtoWireType.Varint)
                                {
                                    status = (ProtocommStatus)sr0.ReadInt32();
                                }
                                else if (f2 == ProtoFields.Resp0DevicePubkey && w2 == ProtoWireType.LengthDelimited)
                                {
                                    devicePubkey = sr0.ReadLengthDelimited().ToArray();
                                }
                                else if (f2 == ProtoFields.Resp0DeviceRandom && w2 == ProtoWireType.LengthDelimited)
                                {
                                    deviceRandom = sr0.ReadLengthDelimited().ToArray();
                                }
                                else
                                {
                                    sr0.SkipField(w2);
                                }
                            }
                        }
                        else if (f1 == ProtoFields.Sec1Sr1 && w1 == ProtoWireType.LengthDelimited)
                        {
                            var sr1 = new ProtoReader(sec1.ReadLengthDelimited());
                            while (sr1.TryReadTag(out var f2, out var w2))
                            {
                                if (f2 == ProtoFields.Resp1Status && w2 == ProtoWireType.Varint)
                                {
                                    status = (ProtocommStatus)sr1.ReadInt32();
                                }
                                else if (f2 == ProtoFields.Resp1DeviceVerifyData && w2 == ProtoWireType.LengthDelimited)
                                {
                                    deviceVerify = sr1.ReadLengthDelimited().ToArray();
                                }
                                else
                                {
                                    sr1.SkipField(w2);
                                }
                            }
                        }
                        else
                        {
                            sec1.SkipField(w1);
                        }
                    }
                }
                else
                {
                    reader.SkipField(wireType);
                }
            }

            return new SessionResponse(secVer, msgType, status, devicePubkey, deviceRandom, deviceVerify);
        }
    }

    /// <summary>Encoders/decoders for the Wi-Fi configuration endpoint (prov-config), before encryption.</summary>
    internal static class WifiConfigMessages
    {
        /// <summary>
        /// WiFiConfigPayload{msg=TypeCmdSetConfig, cmd_set_config{ssid, passphrase}}. The returned
        /// array contains the passphrase in clear text; the caller must zero it after encryption.
        /// </summary>
        public static byte[] EncodeSetConfig(byte[] ssid, byte[] passphrase)
        {
            using var writer = new ProtoWriter(256);
            writer.WriteVarintField(ProtoFields.WifiMsg, (ulong)WifiConfigMsgType.CmdSetConfig);
            writer.WriteMessageField(ProtoFields.WifiCmdSetConfig, cmd =>
            {
                if (ssid.Length > 0)
                {
                    cmd.WriteBytesField(ProtoFields.SetConfigSsid, ssid);
                }

                if (passphrase.Length > 0)
                {
                    cmd.WriteBytesField(ProtoFields.SetConfigPassphrase, passphrase);
                }
            });
            return writer.ToArray();
        }

        /// <summary>
        /// WiFiConfigPayload{msg=TypeCmdApplyConfig}. wifi_prov.py does not set the (empty)
        /// cmd_apply_config payload either; the device dispatches on msg alone.
        /// </summary>
        public static byte[] EncodeApplyConfig()
        {
            using var writer = new ProtoWriter();
            writer.WriteVarintField(ProtoFields.WifiMsg, (ulong)WifiConfigMsgType.CmdApplyConfig);
            return writer.ToArray();
        }

        /// <summary>
        /// WiFiConfigPayload{msg=TypeCmdGetStatus, cmd_get_status{}}. msg=0 is the proto3 default and is
        /// omitted; the empty cmd_get_status payload is emitted to select the oneof member, matching
        /// wifi_prov.py, which explicitly sets it via MergeFrom.
        /// </summary>
        public static byte[] EncodeGetStatus()
        {
            using var writer = new ProtoWriter();
            writer.WriteBytesField(ProtoFields.WifiCmdGetStatus, ReadOnlySpan<byte>.Empty);
            return writer.ToArray();
        }

        public static WifiConfigResponse DecodeResponse(ReadOnlySpan<byte> data)
        {
            var msgType = WifiConfigMsgType.CmdGetStatus;
            var status = ProtocommStatus.Success;
            var staState = WifiStationState.Connected;
            var failReason = WifiConnectFailedReason.AuthError;
            string? ip4 = null;
            string? connectedSsid = null;
            var connectedPresent = false;

            var reader = new ProtoReader(data);
            while (reader.TryReadTag(out var field, out var wireType))
            {
                if (field == ProtoFields.WifiMsg && wireType == ProtoWireType.Varint)
                {
                    msgType = (WifiConfigMsgType)reader.ReadInt32();
                }
                else if ((field == ProtoFields.WifiRespGetStatus
                          || field == ProtoFields.WifiRespSetConfig
                          || field == ProtoFields.WifiRespApplyConfig)
                         && wireType == ProtoWireType.LengthDelimited)
                {
                    var resp = new ProtoReader(reader.ReadLengthDelimited());
                    while (resp.TryReadTag(out var f1, out var w1))
                    {
                        if (f1 == ProtoFields.RespStatus && w1 == ProtoWireType.Varint)
                        {
                            status = (ProtocommStatus)resp.ReadInt32();
                        }
                        else if (field == ProtoFields.WifiRespGetStatus && f1 == ProtoFields.GetStatusStaState && w1 == ProtoWireType.Varint)
                        {
                            staState = (WifiStationState)resp.ReadInt32();
                        }
                        else if (field == ProtoFields.WifiRespGetStatus && f1 == ProtoFields.GetStatusFailReason && w1 == ProtoWireType.Varint)
                        {
                            failReason = (WifiConnectFailedReason)resp.ReadInt32();
                        }
                        else if (field == ProtoFields.WifiRespGetStatus && f1 == ProtoFields.GetStatusConnected && w1 == ProtoWireType.LengthDelimited)
                        {
                            connectedPresent = true;
                            var connected = new ProtoReader(resp.ReadLengthDelimited());
                            while (connected.TryReadTag(out var f2, out var w2))
                            {
                                if (f2 == ProtoFields.ConnectedIp4Addr && w2 == ProtoWireType.LengthDelimited)
                                {
                                    ip4 = Encoding.UTF8.GetString(connected.ReadLengthDelimited());
                                }
                                else if (f2 == ProtoFields.ConnectedSsid && w2 == ProtoWireType.LengthDelimited)
                                {
                                    connectedSsid = Encoding.UTF8.GetString(connected.ReadLengthDelimited()).TrimEnd('\0');
                                }
                                else
                                {
                                    connected.SkipField(w2);
                                }
                            }
                        }
                        else
                        {
                            resp.SkipField(w1);
                        }
                    }
                }
                else
                {
                    reader.SkipField(wireType);
                }
            }

            // sta_state Connected is the proto3 zero value, so an empty RespGetStatus would otherwise read as
            // Connected. The device always attaches the `connected` sub-message when it really is connected
            // (wifi_config.c), so without it treat the state as still Connecting.
            if (msgType == WifiConfigMsgType.RespGetStatus && staState == WifiStationState.Connected && !connectedPresent)
            {
                staState = WifiStationState.Connecting;
            }

            return new WifiConfigResponse(msgType, status, staState, failReason, ip4, connectedSsid);
        }
    }
}
