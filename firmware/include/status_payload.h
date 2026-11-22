#ifndef STATUS_PAYLOAD_H
#define STATUS_PAYLOAD_H

// Pure formatter for the `resmon/status` device status payload. No Arduino
// dependencies so it can be unit-tested on the host. Contract:
// COMMUNICATION.md ("Device status").
//
// Never put SSID, IP/MAC addresses, the PoP or passwords into this struct or
// payload.

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include "provisioning_logic.h"

constexpr int STATUS_SCHEMA_VERSION = 1;
// Largest escaped `build` value accepted (bytes, without quotes/NUL). The
// daemon keeps at most 64 characters of `build` (COMMUNICATION.md).
constexpr size_t STATUS_BUILD_ESCAPED_MAX = 64;

enum class StatusWifi {
    Connected,
    Connecting,
    Disconnected,
};

enum class StatusProv {
    Provisioning,
    Provisioned,
    DevFallback,
    Unconfigured,
};

struct StatusSnapshot {
    const char* build;     // FIRMWARE_BUILD_ID; nullptr is emitted as ""
    uint32_t uptimeS;      // millis() / 1000
    uint32_t heapFree;     // ESP.getFreeHeap()
    uint32_t heapMin;      // ESP.getMinFreeHeap()
    bool hasRssi;          // false -> `rssi` omitted
    int32_t rssi;          // dBm, only used when hasRssi
    StatusWifi wifi;
    StatusProv prov;
    bool dacPresent;
};

inline const char* statusWifiName(StatusWifi wifi) {
    switch (wifi) {
        case StatusWifi::Connected: return "connected";
        case StatusWifi::Connecting: return "connecting";
        case StatusWifi::Disconnected: return "disconnected";
    }
    return "disconnected";
}

inline const char* statusProvName(StatusProv prov) {
    switch (prov) {
        case StatusProv::Provisioning: return "provisioning";
        case StatusProv::Provisioned: return "provisioned";
        case StatusProv::DevFallback: return "dev-fallback";
        case StatusProv::Unconfigured: return "unconfigured";
    }
    return "unconfigured";
}

// `wifi` field from the firmware network state machine.
inline StatusWifi statusWifiFromNetwork(NetworkState state) {
    switch (state) {
        case NetworkState::Connected: return StatusWifi::Connected;
        case NetworkState::Connecting: return StatusWifi::Connecting;
        case NetworkState::Starting:
        case NetworkState::Provisioning:
        case NetworkState::Error:
            return StatusWifi::Disconnected;
    }
    return StatusWifi::Disconnected;
}

// `prov` field from the firmware network state machine:
//   Provisioning state (BLE provisioning active, not connected) -> provisioning
//   credentials from the Wi-Fi NVS store (BLE-provisioned)      -> provisioned
//   credentials from the compiled secrets.h fallback             -> dev-fallback
//   no credential source (Starting, Error)                      -> unconfigured
// After a successful provisioning the state is Connected with source Stored,
// so the BLE grace period reports `provisioned`.
inline StatusProv statusProvFromNetwork(NetworkState state, CredentialSource source) {
    if (state == NetworkState::Provisioning) {
        return StatusProv::Provisioning;
    }
    switch (source) {
        case CredentialSource::Stored: return StatusProv::Provisioned;
        case CredentialSource::DevFallback: return StatusProv::DevFallback;
        case CredentialSource::None: return StatusProv::Unconfigured;
    }
    return StatusProv::Unconfigured;
}

// JSON string-escapes `in` into `out` (no surrounding quotes): `"` and `\`
// are backslash-escaped, control characters become \u00XX, other bytes are
// copied. Returns false (out = "") if it does not fit in outSize.
inline bool escapeStatusJsonString(char* out, size_t outSize, const char* in) {
    if (out == nullptr || outSize == 0) {
        return false;
    }
    size_t pos = 0;
    for (const char* p = (in != nullptr ? in : ""); *p != '\0'; ++p) {
        const unsigned char c = (unsigned char)*p;
        char escaped[7];
        size_t n;
        if (c == '"' || c == '\\') {
            escaped[0] = '\\';
            escaped[1] = (char)c;
            n = 2;
        } else if (c < 0x20) {
            snprintf(escaped, sizeof(escaped), "\\u%04x", (unsigned)c);
            n = 6;
        } else {
            escaped[0] = (char)c;
            n = 1;
        }
        if (pos + n >= outSize) {
            out[0] = '\0';
            return false;
        }
        for (size_t i = 0; i < n; ++i) {
            out[pos++] = escaped[i];
        }
    }
    out[pos] = '\0';
    return true;
}

// Writes the compact JSON payload into `out`. Returns its length (> 0), or 0
// with out = "" (when outSize > 0) if it does not fit or the build string is
// too long; partial JSON is never left in `out`.
inline size_t formatStatusPayload(char* out, size_t outSize, const StatusSnapshot& s) {
    if (out == nullptr || outSize == 0) {
        return 0;
    }
    out[0] = '\0';

    char build[STATUS_BUILD_ESCAPED_MAX + 1];
    if (!escapeStatusJsonString(build, sizeof(build), s.build)) {
        return 0;
    }

    char rssiField[32] = "";
    if (s.hasRssi) {
        snprintf(rssiField, sizeof(rssiField), ",\"rssi\":%ld", (long)s.rssi);
    }

    const int n = snprintf(out, outSize,
                           "{\"v\":%d,\"build\":\"%s\",\"uptime_s\":%lu,\"heap_free\":%lu,\"heap_min\":%lu%s,"
                           "\"wifi\":\"%s\",\"prov\":\"%s\",\"dac\":%s}",
                           STATUS_SCHEMA_VERSION, build, (unsigned long)s.uptimeS, (unsigned long)s.heapFree,
                           (unsigned long)s.heapMin, rssiField, statusWifiName(s.wifi), statusProvName(s.prov),
                           s.dacPresent ? "true" : "false");
    if (n <= 0 || (size_t)n >= outSize) {
        out[0] = '\0';
        return 0;
    }
    return (size_t)n;
}

#endif
