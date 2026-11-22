#ifndef PROVISIONING_LOGIC_H
#define PROVISIONING_LOGIC_H

// Pure boot/connection decision logic for Wi-Fi provisioning. No Arduino
// dependencies so it can be unit-tested on the host. See PROVISIONING.md.

#include <stdint.h>

enum class BootAction {
    // Start BLE provisioning (ESP-IDF provisioning manager, Security1 + PoP).
    EnterProvisioning,
    // Connect as STA with credentials from the Wi-Fi NVS store (provisioned).
    ConnectStoredCredentials,
    // Connect as STA with the compiled secrets.h development fallback.
    ConnectDevFallback,
};

enum class BootReason {
    ProvisioningRequested,   // prov_req flag set by BOOT long-press
    StoredCredentials,       // Wi-Fi NVS store has an SSID
    DevFallback,             // compiled WIFI_SSID is non-empty
    NoCredentials,           // nothing configured at all
};

struct BootDecision {
    BootAction action;
    BootReason reason;
    // True when the persisted prov_req flag must be cleared before acting.
    bool clearProvisioningRequest;
    // True when stored Wi-Fi credentials must be erased before provisioning.
    bool resetStoredCredentials;
};

// Device lifecycle (contract order):
//   prov_req set            -> clear it, provisioning (credentials reset)
//   stored credentials      -> connect with them
//   compiled dev fallback   -> connect with it (no BLE)
//   otherwise               -> provisioning
inline BootDecision decideBootAction(bool provisioningRequested,
                                     bool hasStoredCredentials,
                                     bool hasDevFallbackCredentials) {
    if (provisioningRequested) {
        return {BootAction::EnterProvisioning, BootReason::ProvisioningRequested, true, true};
    }
    if (hasStoredCredentials) {
        return {BootAction::ConnectStoredCredentials, BootReason::StoredCredentials, false, false};
    }
    if (hasDevFallbackCredentials) {
        return {BootAction::ConnectDevFallback, BootReason::DevFallback, false, false};
    }
    return {BootAction::EnterProvisioning, BootReason::NoCredentials, false, false};
}

inline const char* bootReasonName(BootReason reason) {
    switch (reason) {
        case BootReason::ProvisioningRequested: return "provisioning requested (BOOT long-press)";
        case BootReason::StoredCredentials: return "stored Wi-Fi credentials";
        case BootReason::DevFallback: return "compiled secrets.h dev fallback";
        case BootReason::NoCredentials: return "no Wi-Fi credentials";
    }
    return "unknown";
}

// Runtime network state, also reported by the serial PoP query when the
// device is not in provisioning mode.
enum class NetworkState {
    Starting,       // setup() has not decided yet
    Provisioning,   // BLE provisioning active; PoP may be served
    Connecting,     // STA credentials present, not (yet/any more) connected
    Connected,      // STA has an IP address
    Error,          // provisioning could not start and nothing else is configured
};

enum class CredentialSource {
    None,
    Stored,         // Wi-Fi NVS store (BLE-provisioned)
    DevFallback,    // compiled secrets.h
};

// Value of `state=` in `RESMON-POP unavailable state=<state>`.
inline const char* popUnavailableStateName(NetworkState state, CredentialSource source) {
    switch (state) {
        case NetworkState::Starting: return "starting";
        case NetworkState::Provisioning: return "provisioning";
        case NetworkState::Connecting: return "connecting";
        case NetworkState::Connected:
            return source == CredentialSource::DevFallback ? "dev-fallback" : "provisioned";
        case NetworkState::Error: return "error";
    }
    return "unknown";
}

inline const char* networkStateName(NetworkState state) {
    switch (state) {
        case NetworkState::Starting: return "starting";
        case NetworkState::Provisioning: return "provisioning";
        case NetworkState::Connecting: return "connecting";
        case NetworkState::Connected: return "connected";
        case NetworkState::Error: return "error";
    }
    return "unknown";
}

inline const char* credentialSourceName(CredentialSource source) {
    switch (source) {
        case CredentialSource::None: return "none";
        case CredentialSource::Stored: return "stored";
        case CredentialSource::DevFallback: return "dev-fallback";
    }
    return "unknown";
}

// Bounded exponential reconnect backoff: doubles the delay, clamped to
// [minMs, maxMs].
inline uint32_t nextReconnectDelayMs(uint32_t currentMs, uint32_t minMs, uint32_t maxMs) {
    if (currentMs < minMs) {
        return minMs;
    }
    if (currentMs >= maxMs / 2) {
        return maxMs;
    }
    return currentMs * 2;
}

// What to do when the STA obtains an IP address.
enum class ConnectedTransition {
    // Normal STA operation: become Connected, keep the credential source.
    BecomeConnected,
    // Provisioning succeeded: become Connected with source = stored.
    BecomeConnectedStored,
    // Still provisioning and the credentials have NOT been accepted (e.g. the
    // core's one automatic retry connected after the firmware had already
    // reset the rejected credentials). Nothing is stored in NVS, so stay in
    // provisioning mode and re-evaluate once provisioning succeeds.
    StayProvisioning,
};

inline ConnectedTransition decideConnectedTransition(NetworkState state, bool provisioningSucceeded) {
    if (state != NetworkState::Provisioning) {
        return ConnectedTransition::BecomeConnected;
    }
    return provisioningSucceeded ? ConnectedTransition::BecomeConnectedStored : ConnectedTransition::StayProvisioning;
}

// On a normal successful pairing GOT_IP reaches the firmware a moment before
// CRED_SUCCESS (the manager posts CRED_SUCCESS from its own GOT_IP handler),
// so a short StayProvisioning deferral is expected. Warn only when the
// deferral has lasted at least `graceMs` (wrap-safe), and only once.
inline bool shouldWarnDeferredConnect(uint32_t deferredSinceMs, uint32_t nowMs, uint32_t graceMs,
                                      bool alreadyWarned) {
    return !alreadyWarned && (uint32_t)(nowMs - deferredSinceMs) >= graceMs;
}

// What to do on WIFI_PROV_CRED_FAIL.
enum class CredentialFailureAction {
    // Before success: the client's credentials were rejected. Clear them
    // (wifi_prov_mgr_reset_sm_state_on_failure) so the client can retry.
    ResetForRetry,
    // After success (BLE grace period): the network dropped, e.g. the AP
    // rebooted. The manager still reports CRED_FAIL, but resetting would
    // persistently wipe the just-provisioned credentials. Keep them and stop
    // provisioning so only the firmware's reconnect policy runs.
    KeepCredentialsAndStopProvisioning,
};

inline CredentialFailureAction decideCredentialFailureAction(bool provisioningSucceeded) {
    return provisioningSucceeded ? CredentialFailureAction::KeepCredentialsAndStopProvisioning
                                 : CredentialFailureAction::ResetForRetry;
}

// What to do when BLE provisioning fails to start in this boot.
enum class ProvisioningStartRecovery {
    RestartAndRetry,        // restart (prov_req set) and try provisioning again
    UseDevFallback,         // give up on BLE, connect with secrets.h credentials
    GiveUp,                 // stay up, log a clear error, wait for user action
};

// `failedAttempts` counts consecutive failed starts including this one
// (carried across software restarts). Never loops forever: after
// `maxAttempts` failures the device stops restarting.
inline ProvisioningStartRecovery decideProvisioningStartRecovery(uint32_t failedAttempts, uint32_t maxAttempts,
                                                                 bool hasDevFallbackCredentials) {
    if (failedAttempts < maxAttempts) {
        return ProvisioningStartRecovery::RestartAndRetry;
    }
    if (hasDevFallbackCredentials) {
        return ProvisioningStartRecovery::UseDevFallback;
    }
    return ProvisioningStartRecovery::GiveUp;
}

#endif
