#include <Arduino.h>
#include <WiFi.h>
#include <ESPmDNS.h>
#include <PicoMQTT.h>
#include <Preferences.h>
#include <ctype.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "config.h"
#include <Wire.h>
#include <DFRobot_GP8XXX.h>
#include <bootloader_random.h>
#include <esp32-hal-bt.h>
#include <esp_attr.h>
#include <esp_bt.h>
#include <esp_mac.h>
#include <esp_netif.h>
#include <esp_system.h>
#include <esp_wifi.h>
#include <wifi_provisioning/manager.h>
#include <wifi_provisioning/scheme_ble.h>
#include "metric_conversion.h"
#include "button_long_press.h"
#include "pop_code.h"
#include "provisioning_logic.h"
#include "serial_command.h"
#include "status_payload.h"

#if !CONFIG_BLUEDROID_ENABLED
#error "BLE provisioning needs the Bluedroid BLE stack (CONFIG_BLUEDROID_ENABLED)"
#endif

// Defined in the Arduino WiFi library (WiFiGeneric.cpp); WiFiProv uses the
// same declaration. Initialises the Wi-Fi driver and the Arduino network
// event task without starting the radio, so the stored STA configuration can
// be inspected before deciding what to do.
bool wifiLowLevelInit(bool persistent);

WiFiServer mqttTcpServer(MQTT_BROKER_PORT);
PicoMQTT::Server mqtt(mqttTcpServer);

constexpr size_t DEBUG_LOG_MAX_CHARS = 192;
int currentCpuDacValue = 0;
int currentMemDacValue = 0;
unsigned long lastDacChangeTime = 0;
unsigned long lastCpuMetricTime = 0;
unsigned long lastMemMetricTime = 0;
unsigned long lastStaleDecayTime = 0;
int lastLoggedCpuDacValue = -1;
int lastLoggedMemDacValue = -1;
uint32_t lastDacLogMs = 0;
DFRobot_GP8413 dac(DAC_I2C_ADDRESS);
bool dacPresent = false;

// ---- Provisioning / network state (owned by loop()) ----
NetworkState networkState = NetworkState::Starting;
CredentialSource credentialSource = CredentialSource::None;
BootReason bootReason = BootReason::NoCredentials;
char provServiceName[PROV_SERVICE_NAME_BUFFER_SIZE] = {0};
char provPop[POP_BUFFER_SIZE] = {0};
bool provPopAvailable = false;
bool provStartSeen = false;
bool provStartFailed = false;
ProvisioningStartRecovery provStartRecovery = ProvisioningStartRecovery::GiveUp;  // valid once provStartFailed
bool provSucceeded = false;
bool provStopRequested = false;
bool networkServicesStarted = false;
bool wasWifiConnected = false;
bool deferredConnectActive = false;   // IP while provisioning, no accepted credentials yet
bool deferredConnectWarned = false;
uint32_t deferredConnectSinceMs = 0;
bool bluetoothMemoryReleased = false;
bool connectAttemptInFlight = false;
bool restartPending = false;
uint32_t provisioningStartedAtMs = 0;
uint32_t provSucceededAtMs = 0;
uint32_t lastProvHintMs = 0;
uint32_t lastWifiStatusLogMs = 0;
uint32_t lastStatusDiagnosticMs = 0;
uint32_t disconnectedSinceMs = 0;
uint32_t connectAttemptStartedAtMs = 0;
uint32_t nextReconnectAtMs = 0;
uint32_t reconnectDelayMs = WIFI_RECONNECT_MIN_MS;
uint32_t seenDisconnectCount = 0;
uint32_t restartAtMs = 0;

// ---- resmon/status publishing (owned by loop()) ----
bool statusEverPublished = false;
bool statusFormatErrorLogged = false;
uint32_t lastStatusPublishMs = 0;

// ---- Set from the Arduino network event task, consumed in loop() ----
volatile bool evtProvStarted = false;
volatile bool evtProvCredReceived = false;
volatile bool evtProvCredFailed = false;
volatile bool evtProvCredSuccess = false;
volatile bool evtProvEnded = false;
volatile int evtProvFailReason = 0;
volatile bool staHasIp = false;
volatile uint32_t staIpAddr = 0;
volatile uint32_t staDisconnectCount = 0;
volatile uint8_t lastDisconnectReason = 0;  // last real reason (ASSOC_LEAVE ignored)

// ---- Consecutive provisioning start failures, kept across software resets ----
constexpr uint32_t RTC_PROV_FAIL_MAGIC = 0x52534D31u;  // "RSM1"
RTC_NOINIT_ATTR uint32_t rtcProvFailMagic;
RTC_NOINIT_ATTR uint32_t rtcProvFailCount;

HoldReleaseDetector bootButton(BOOT_BUTTON_DEBOUNCE_MS, BOOT_BUTTON_MIN_HOLD_MS, BOOT_BUTTON_MAX_HOLD_MS);
SerialLineBuffer serialLine;

void printDebugLine(const char* message) {
    char buffer[DEBUG_LOG_MAX_CHARS + 1];
    strncpy(buffer, message, DEBUG_LOG_MAX_CHARS);
    buffer[DEBUG_LOG_MAX_CHARS] = '\0';
    Serial.println(buffer);
}

void printDebugFormatted(const char* format, ...) {
    va_list args;
    va_start(args, format);
    char buffer[DEBUG_LOG_MAX_CHARS + 1];
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    Serial.println(buffer);
}

// ======================= DAC output =======================

void writeDacValue() {
    if (!dacPresent) {
        return;
    }

    uint16_t cpuMapped = (uint16_t)map(currentCpuDacValue, 0, 255, 0, 32767);
    uint16_t memMapped = (uint16_t)map(currentMemDacValue, 0, 255, 0, 32767);
    dac.setDACOutVoltage(cpuMapped, CPU_OUTPUT_CHANNEL);
    dac.setDACOutVoltage(memMapped, MEM_OUTPUT_CHANNEL);
}

// Refreshes the DAC every UPDATE_INTERVAL. The values are logged only when
// they changed, and at most every DAC_LOG_MIN_INTERVAL_MS, so the serial port
// stays usable for the RESMON?POP pairing query.
void updateDacOutput() {
    if (millis() - lastDacChangeTime >= UPDATE_INTERVAL) {
        lastDacChangeTime = millis();
        writeDacValue();

        const uint32_t now = millis();
        const bool changed = currentCpuDacValue != lastLoggedCpuDacValue || currentMemDacValue != lastLoggedMemDacValue;
        if (changed && (lastLoggedCpuDacValue < 0 || (uint32_t)(now - lastDacLogMs) >= DAC_LOG_MIN_INTERVAL_MS)) {
            lastDacLogMs = now;
            lastLoggedCpuDacValue = currentCpuDacValue;
            lastLoggedMemDacValue = currentMemDacValue;
            printDebugFormatted("DAC values: cpu=%d mem=%d", currentCpuDacValue, currentMemDacValue);
        }
    }
}

void setCpuPercent(float percent) {
    currentCpuDacValue = percentToDacValue(percent);
    lastCpuMetricTime = millis();
    writeDacValue();
}

void setMemPercent(float percent) {
    currentMemDacValue = percentToDacValue(percent);
    lastMemMetricTime = millis();
    writeDacValue();
}

void decayStaleMetrics() {
    unsigned long now = millis();
    if (now - lastStaleDecayTime < STALE_DECAY_INTERVAL_MS) {
        return;
    }

    lastStaleDecayTime = now;
    bool changed = false;

    if (lastCpuMetricTime != 0 && now - lastCpuMetricTime > METRIC_TIMEOUT_MS && currentCpuDacValue != 0) {
        currentCpuDacValue = max(0, currentCpuDacValue - STALE_DECAY_STEP);
        changed = true;
    }

    if (lastMemMetricTime != 0 && now - lastMemMetricTime > METRIC_TIMEOUT_MS && currentMemDacValue != 0) {
        currentMemDacValue = max(0, currentMemDacValue - STALE_DECAY_STEP);
        changed = true;
    }

    if (changed) {
        writeDacValue();
    }
}

void handleMqttMetric(const char* topic, const char* payload) {
    float percent = 0.0f;
    if (!parsePercentPayload(payload, percent)) {
        return;
    }

    if (strcmp(topic, CPU_TOPIC) == 0) {
        setCpuPercent(percent);
    } else if (strcmp(topic, MEM_TOPIC) == 0) {
        setMemPercent(percent);
    }
}

void initDac() {
    Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
    if (dac.begin() == 0) {
        dacPresent = true;
        dac.setDACOutRange(DFRobot_GP8XXX::eOutputRange5V);
        dac.setDACOutVoltage(0, CPU_OUTPUT_CHANNEL);
        dac.setDACOutVoltage(0, MEM_OUTPUT_CHANNEL);
        printDebugFormatted("DAC initialized at 0x%02X (0-5V)", DAC_I2C_ADDRESS);
    } else {
        dacPresent = false;
        printDebugFormatted("DAC not found at 0x%02X; running without external DAC", DAC_I2C_ADDRESS);
    }
}

// ======================= Provisioning identity (NVS) =======================

uint32_t hardwareRandomWord() {
    return esp_random();
}

void initProvisioningFailureCounter() {
    // RTC_NOINIT memory survives ESP.restart() but is garbage after power-on.
    if (esp_reset_reason() != ESP_RST_SW || rtcProvFailMagic != RTC_PROV_FAIL_MAGIC) {
        rtcProvFailMagic = RTC_PROV_FAIL_MAGIC;
        rtcProvFailCount = 0;
    }
}

void setProvisioningRequest(bool requested) {
    Preferences prefs;
    if (!prefs.begin(PROV_NVS_NAMESPACE, false)) {
        printDebugLine("Provisioning: NVS unavailable; could not update request flag");
        return;
    }
    if (requested) {
        prefs.putBool(PROV_NVS_KEY_REQUEST, true);
    } else if (prefs.isKey(PROV_NVS_KEY_REQUEST)) {
        // Only remove an existing key: Preferences::remove() logs an error
        // for a missing key, which would be a misleading line on UART0.
        prefs.remove(PROV_NVS_KEY_REQUEST);
    }
    prefs.end();
}

// Loads or creates the PoP and builds the advertised service name. Returns
// the persisted provisioning-request flag. Must run before Wi-Fi/BT start:
// it temporarily enables the RNG entropy source used by esp_random().
bool loadProvisioningIdentity() {
    uint8_t mac[6] = {0};
    if (esp_read_mac(mac, ESP_MAC_WIFI_STA) != ESP_OK ||
        !formatProvisioningServiceName(provServiceName, sizeof(provServiceName), mac)) {
        snprintf(provServiceName, sizeof(provServiceName), "%s000000", PROV_SERVICE_NAME_PREFIX);
        printDebugLine("Provisioning: could not read STA MAC; using placeholder service name");
    }

    Preferences prefs;
    if (!prefs.begin(PROV_NVS_NAMESPACE, false)) {
        printDebugLine("Provisioning: NVS namespace unavailable; pairing code not available");
        provPopAvailable = false;
        return false;
    }

    provPop[0] = '\0';
    if (prefs.isKey(PROV_NVS_KEY_POP)) {
        prefs.getString(PROV_NVS_KEY_POP, provPop, sizeof(provPop));
    }

    if (!isValidPop(provPop)) {
        bootloader_random_enable();
        generatePop(provPop, hardwareRandomWord);
        bootloader_random_disable();
        if (prefs.putString(PROV_NVS_KEY_POP, provPop) == POP_LENGTH) {
            printDebugLine("Provisioning: generated new pairing code (stored in NVS)");
        } else {
            printDebugLine("Provisioning: failed to store new pairing code in NVS");
        }
    }
    provPopAvailable = isValidPop(provPop);

    const bool requested = prefs.getBool(PROV_NVS_KEY_REQUEST, false);
    prefs.end();
    return requested;
}

bool hasStoredWifiCredentials() {
    // Same check as wifi_prov_mgr_is_provisioned(): a non-empty STA SSID in
    // the Wi-Fi driver's NVS-backed configuration.
    wifi_config_t conf;
    memset(&conf, 0, sizeof(conf));
    if (esp_wifi_get_config(WIFI_IF_STA, &conf) != ESP_OK) {
        return false;
    }
    return conf.sta.ssid[0] != '\0';
}

bool hasDevFallbackCredentials() {
    return WIFI_SSID != nullptr && WIFI_SSID[0] != '\0';
}

// Frees the BT controller/host memory on boots that do not provision. Only
// called from the STA paths; BLE cannot be started again until a restart.
void releaseBluetoothMemory() {
    if (bluetoothMemoryReleased) {
        return;
    }
    if (esp_bt_controller_get_status() != ESP_BT_CONTROLLER_STATUS_IDLE) {
        return;
    }
    const esp_err_t err = esp_bt_mem_release(ESP_BT_MODE_BTDM);
    bluetoothMemoryReleased = true;
    printDebugFormatted("BLE not needed this boot: BT memory release %s (free heap %u bytes)",
                        err == ESP_OK ? "ok" : esp_err_to_name(err), ESP.getFreeHeap());
}

void printProvisioningHint() {
    if (!provPopAvailable) {
        printDebugFormatted("Provisioning: name=%s pop=unavailable (NVS error)", provServiceName);
        return;
    }
    // Physical USB access is trusted (see docs/provisioning.md).
    printDebugFormatted("Provisioning: name=%s pop=%s", provServiceName, provPop);
}

// Persist the request, wipe stored Wi-Fi credentials, and restart into
// provisioning mode. Triggered only by the BOOT hold-and-release gesture.
void requestProvisioningAndRestart() {
    printDebugFormatted("BOOT released after %u-%u s: clearing Wi-Fi credentials and restarting into BLE provisioning",
                        (unsigned)(BOOT_BUTTON_MIN_HOLD_MS / 1000UL), (unsigned)(BOOT_BUTTON_MAX_HOLD_MS / 1000UL));

    setProvisioningRequest(true);
    rtcProvFailCount = 0;  // explicit user request: start with a fresh retry budget

    if (wifi_prov_mgr_reset_provisioning() != ESP_OK) {
        printDebugLine("Provisioning: failed to clear stored Wi-Fi credentials");
    }

    currentCpuDacValue = 0;
    currentMemDacValue = 0;
    writeDacValue();

    Serial.flush();
    ESP.restart();
}

// ======================= Network events =======================

// Runs in the Arduino network event task: only record facts here.
// Never log credentials received during provisioning.
void onNetworkEvent(arduino_event_t* event) {
    switch (event->event_id) {
        case ARDUINO_EVENT_PROV_START:
            evtProvStarted = true;
            break;
        case ARDUINO_EVENT_PROV_CRED_RECV:
            evtProvCredReceived = true;
            break;
        case ARDUINO_EVENT_PROV_CRED_FAIL:
            evtProvFailReason = (int)event->event_info.prov_fail_reason;
            evtProvCredFailed = true;
            break;
        case ARDUINO_EVENT_PROV_CRED_SUCCESS:
            evtProvCredSuccess = true;
            break;
        case ARDUINO_EVENT_PROV_END:
            evtProvEnded = true;
            break;
        case ARDUINO_EVENT_WIFI_STA_GOT_IP:
            staIpAddr = event->event_info.got_ip.ip_info.ip.addr;
            staHasIp = true;
            break;
        case ARDUINO_EVENT_WIFI_STA_LOST_IP:
            staHasIp = false;
            staIpAddr = 0;
            break;
        case ARDUINO_EVENT_WIFI_STA_DISCONNECTED: {
            const uint8_t reason = event->event_info.wifi_sta_disconnected.reason;
            // ASSOC_LEAVE is our own/the core's deliberate disconnect; keep
            // the last real failure reason for diagnostics.
            if (reason != WIFI_REASON_ASSOC_LEAVE) {
                lastDisconnectReason = reason;
            }
            staHasIp = false;
            staDisconnectCount = staDisconnectCount + 1;
            break;
        }
        default:
            break;
    }
}

// ======================= Wi-Fi start paths =======================

void markConnectAttempt(uint32_t now) {
    connectAttemptInFlight = true;
    connectAttemptStartedAtMs = now;
}

void startDevFallback(uint32_t now) {
    credentialSource = CredentialSource::DevFallback;
    networkState = NetworkState::Connecting;
    releaseBluetoothMemory();
    // Keep secrets.h credentials out of the Wi-Fi NVS store so they are
    // never mistaken for BLE-provisioned credentials.
    esp_wifi_set_storage(WIFI_STORAGE_RAM);
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    markConnectAttempt(now);
}

void handleProvisioningStartFailure(const char* step, esp_err_t err, uint32_t now) {
    if (provStartFailed) {
        return;
    }
    provStartFailed = true;
    networkState = NetworkState::Error;
    lastWifiStatusLogMs = now;

    rtcProvFailCount = rtcProvFailCount + 1;
    const uint32_t attempts = rtcProvFailCount;
    printDebugFormatted("Provisioning: ERROR BLE provisioning failed to start (%s: %s), attempt %u of %u", step,
                        esp_err_to_name(err), (unsigned)attempts, (unsigned)PROV_START_MAX_ATTEMPTS);

    provStartRecovery = decideProvisioningStartRecovery(attempts, PROV_START_MAX_ATTEMPTS, hasDevFallbackCredentials());
    switch (provStartRecovery) {
        case ProvisioningStartRecovery::RestartAndRetry:
            setProvisioningRequest(true);
            restartPending = true;
            restartAtMs = now + PROV_RETRY_RESTART_DELAY_MS;
            printDebugFormatted("Provisioning: restarting in %u ms to retry", (unsigned)PROV_RETRY_RESTART_DELAY_MS);
            break;
        case ProvisioningStartRecovery::UseDevFallback:
            rtcProvFailCount = 0;
            printDebugLine("Provisioning: giving up on BLE; connecting with the secrets.h dev fallback");
            // A manager may still be initialised (e.g. after the PROV_START
            // timeout). Deinit only logs one error line when the manager was
            // never initialised. In the rare case that it is still running,
            // deinit stops it inline and blocks loop() once for the BLE cleanup
            // delay (PROV_BLE_CLEANUP_DELAY_MS); acceptable on this give-up path.
            wifi_prov_mgr_deinit();
            startDevFallback(now);
            break;
        case ProvisioningStartRecovery::GiveUp:
            rtcProvFailCount = 0;
            printDebugLine("Provisioning: giving up; no Wi-Fi credentials. Power-cycle to retry.");
            break;
    }
}

// Provisioning manager calls are made directly (instead of
// WiFiProv.beginProvision) so that auto-stop can be disabled between init and
// start; see docs/provisioning.md.
void startProvisioning(bool resetStoredCredentials, uint32_t now) {
    credentialSource = CredentialSource::None;
    networkState = NetworkState::Provisioning;
    provisioningStartedAtMs = now;
    lastProvHintMs = now;
    printProvisioningHint();

    if (!provPopAvailable) {
        handleProvisioningStartFailure("pairing code", ESP_ERR_INVALID_STATE, now);
        return;
    }

    wifi_prov_mgr_config_t config;
    memset(&config, 0, sizeof(config));
    config.scheme = wifi_prov_scheme_ble;
    const wifi_prov_event_handler_t freeBtdm = WIFI_PROV_SCHEME_BLE_EVENT_HANDLER_FREE_BTDM;
    config.scheme_event_handler = freeBtdm;
    config.app_event_handler.event_cb = NULL;
    config.app_event_handler.user_data = NULL;

    esp_err_t err = wifi_prov_mgr_init(config);
    if (err != ESP_OK) {
        handleProvisioningStartFailure("wifi_prov_mgr_init", err, now);
        return;
    }

    if (resetStoredCredentials && wifi_prov_mgr_reset_provisioning() != ESP_OK) {
        printDebugLine("Provisioning: failed to clear stored Wi-Fi credentials");
    }

    // Must be called while the manager is initialised but not started.
    err = wifi_prov_mgr_disable_auto_stop(PROV_BLE_CLEANUP_DELAY_MS);
    if (err != ESP_OK) {
        printDebugFormatted("Provisioning: could not disable auto-stop (%s); BLE may stop early",
                            esp_err_to_name(err));
    }

    err = wifi_prov_scheme_ble_set_service_uuid(PROV_BLE_SERVICE_UUID);
    if (err != ESP_OK) {
        printDebugFormatted("Provisioning: could not set service UUID (%s)", esp_err_to_name(err));
    }

    err = wifi_prov_mgr_start_provisioning(WIFI_PROV_SECURITY_1, provPop, provServiceName, NULL);
    if (err != ESP_OK) {
        wifi_prov_mgr_deinit();
        handleProvisioningStartFailure("wifi_prov_mgr_start_provisioning", err, now);
    }
}

void consumeProvisioningEvents(uint32_t now) {
    if (evtProvStarted) {
        evtProvStarted = false;
        switch (decideProvisioningStarted(provStartFailed, provStartRecovery)) {
            case ProvisioningStartedAction::Continue:
                provStartSeen = true;
                rtcProvFailCount = 0;
                printDebugFormatted("Provisioning: BLE advertising as %s (Security1 + PoP)", provServiceName);
                break;
            case ProvisioningStartedAction::ResumeProvisioning:
                // Started late, after the start timeout was handled (retry
                // restart pending, or given up). BLE is up: cancel the restart
                // and serve the pairing code again.
                provStartSeen = true;
                rtcProvFailCount = 0;
                restartPending = false;
                provStartFailed = false;
                setProvisioningRequest(false);
                networkState = NetworkState::Provisioning;
                lastProvHintMs = now;
                printDebugFormatted("Provisioning: BLE started late (after %u s); resuming provisioning mode",
                                    (unsigned)(PROV_START_TIMEOUT_MS / 1000UL));
                printDebugFormatted("Provisioning: BLE advertising as %s (Security1 + PoP)", provServiceName);
                printProvisioningHint();
                break;
            case ProvisioningStartedAction::Ignore:
                // The manager was deinitialised for the dev fallback; this
                // event was queued before that.
                printDebugLine("Provisioning: ignoring late BLE start (dev fallback active)");
                break;
        }
    }

    if (evtProvCredReceived) {
        evtProvCredReceived = false;
        printDebugLine("Provisioning: Wi-Fi credentials received; connecting");
    }

    // Success is handled before failure: if both are pending in one pass,
    // a drop after success must not be treated as rejected credentials.
    if (evtProvCredSuccess) {
        evtProvCredSuccess = false;
        credentialSource = CredentialSource::Stored;
        provSucceeded = true;
        provSucceededAtMs = now;
        printDebugFormatted("Provisioning: credentials accepted and stored; BLE stays up for %u s",
                            (unsigned)((PROV_STOP_AFTER_SUCCESS_MS + PROV_BLE_CLEANUP_DELAY_MS) / 1000UL));
    }

    if (evtProvCredFailed) {
        evtProvCredFailed = false;
        const bool authError = evtProvFailReason == (int)WIFI_PROV_STA_AUTH_ERROR;
        const char* reasonText = authError ? "authentication error" : "network not found";
        switch (decideCredentialFailureAction(provSucceeded)) {
            case CredentialFailureAction::ResetForRetry:
                printDebugFormatted("Provisioning: Wi-Fi connection failed (%s)", reasonText);
                // Clear the rejected credentials and let the client send new
                // ones in the same or a new session. Never reboot or leave
                // provisioning here.
                if (wifi_prov_mgr_reset_sm_state_on_failure() == ESP_OK) {
                    printDebugLine("Provisioning: ready for new credentials");
                    // Cancel the Arduino core's one automatic retry after the
                    // first disconnect of the boot, which would otherwise still
                    // use the rejected credentials. The resulting ASSOC_LEAVE
                    // disconnect is ignored by the core, the manager (state
                    // reset) and our policy. Not done when the reset failed:
                    // the manager may already be in SUCCESS (the retry worked),
                    // and disconnecting would drop a correctly provisioned STA.
                    esp_wifi_disconnect();
                } else {
                    printDebugFormatted("Provisioning: could not reset state; hold BOOT %u-%u s, then release, "
                                        "to restart provisioning",
                                        (unsigned)(BOOT_BUTTON_MIN_HOLD_MS / 1000UL),
                                        (unsigned)(BOOT_BUTTON_MAX_HOLD_MS / 1000UL));
                }
                break;
            case CredentialFailureAction::KeepCredentialsAndStopProvisioning:
                // The manager's Wi-Fi handler stays active until provisioning
                // stops and reports a post-success drop as CRED_FAIL.
                // Resetting would persistently wipe the provisioned credentials.
                printDebugFormatted("Provisioning: Wi-Fi lost during BLE grace period (%s); credentials kept",
                                    reasonText);
                if (!provStopRequested) {
                    provStopRequested = true;
                    printDebugFormatted("Provisioning: stopping BLE now (cleanup %u ms); firmware reconnect policy takes over",
                                        (unsigned)PROV_BLE_CLEANUP_DELAY_MS);
                    wifi_prov_mgr_stop_provisioning();
                }
                break;
        }
    }

    if (evtProvEnded) {
        evtProvEnded = false;
        // The core calls wifi_prov_mgr_deinit() on PROV_END; the FREE_BTDM
        // scheme handler then releases the BT memory.
        bluetoothMemoryReleased = true;
        printDebugFormatted("Provisioning: ended; BLE stopped (free heap %u bytes)", ESP.getFreeHeap());
    }

    if (provSucceeded && !provStopRequested && (uint32_t)(now - provSucceededAtMs) >= PROV_STOP_AFTER_SUCCESS_MS) {
        provStopRequested = true;
        printDebugFormatted("Provisioning: stopping BLE (cleanup %u ms)", (unsigned)PROV_BLE_CLEANUP_DELAY_MS);
        wifi_prov_mgr_stop_provisioning();
    }
}

// ======================= Network services =======================

// mDNS and the MQTT broker are started once, on the first connection. Both
// keep working across Wi-Fi reconnects (the broker listens on INADDR_ANY and
// the mDNS responder follows the STA interface state).
void startNetworkServicesOnce(uint32_t now) {
    if (networkServicesStarted) {
        return;
    }
    networkServicesStarted = true;

    if (MDNS.begin(MDNS_HOSTNAME)) {
        MDNS.addService("mqtt", "tcp", MQTT_BROKER_PORT);
        printDebugFormatted("mDNS hostname: %s.local", MDNS_HOSTNAME);
    } else {
        printDebugLine("Error setting up MDNS responder!");
    }

    mqtt.begin();
    // First status STATUS_INTERVAL_MS after the broker starts: a publish in
    // this loop pass could not reach any subscriber yet. Uses the loop()
    // timestamp, not millis(), so serviceStatusTopic(now) sees no "future" time.
    lastStatusPublishMs = now;

    printDebugLine("========== ESP32 CPU Monitor network ready ==========");
    printDebugLine("MQTT broker:");
    printDebugFormatted("   - Host: %s.local", MDNS_HOSTNAME);
    printDebugFormatted("   - Port: %d", MQTT_BROKER_PORT);
    printDebugFormatted("   - CPU topic: %s", CPU_TOPIC);
    printDebugFormatted("   - MEM topic: %s", MEM_TOPIC);
#if ENABLE_STATUS_TOPIC
    printDebugFormatted("   - Status topic: %s (published every %u s)", STATUS_TOPIC,
                        (unsigned)(STATUS_INTERVAL_MS / 1000UL));
#endif
    printDebugLine("=====================================================");
}

// Reads the IP captured from the GOT_IP event and the AP info straight from
// the driver. The Arduino WiFi accessors are not used for this because they
// report nothing after the provisioning manager started Wi-Fi itself.
void formatStaIp(char* out, size_t outSize) {
    const uint32_t addr = staIpAddr;
    snprintf(out, outSize, "%u.%u.%u.%u", (unsigned)(addr & 0xFF), (unsigned)((addr >> 8) & 0xFF),
             (unsigned)((addr >> 16) & 0xFF), (unsigned)((addr >> 24) & 0xFF));
}

bool readApInfo(char* ssid, size_t ssidSize, int* rssi) {
    wifi_ap_record_t ap;
    memset(&ap, 0, sizeof(ap));
    if (esp_wifi_sta_get_ap_info(&ap) != ESP_OK) {
        snprintf(ssid, ssidSize, "?");
        *rssi = 0;
        return false;
    }
    snprintf(ssid, ssidSize, "%.*s", (int)sizeof(ap.ssid), (const char*)ap.ssid);
    *rssi = ap.rssi;
    return true;
}

// Returns false when the transition is deferred (still provisioning without
// accepted credentials); the caller then re-evaluates on the next loop().
bool onWifiConnected(uint32_t now) {
    switch (decideConnectedTransition(networkState, provSucceeded)) {
        case ConnectedTransition::StayProvisioning:
            // Expected for a few ms on every successful pairing (GOT_IP
            // arrives before CRED_SUCCESS): stay silent unless it persists.
            if (!deferredConnectActive) {
                deferredConnectActive = true;
                deferredConnectWarned = false;
                deferredConnectSinceMs = now;
            }
            if (shouldWarnDeferredConnect(deferredConnectSinceMs, now, PROV_DEFERRED_CONNECT_WARN_MS,
                                          deferredConnectWarned)) {
                deferredConnectWarned = true;
                printDebugFormatted("Wi-Fi: got an IP while provisioning, but no credentials were accepted after %u s; "
                                    "staying in provisioning mode",
                                    (unsigned)(PROV_DEFERRED_CONNECT_WARN_MS / 1000UL));
            }
            return false;
        case ConnectedTransition::BecomeConnectedStored:
            credentialSource = CredentialSource::Stored;
            break;
        case ConnectedTransition::BecomeConnected:
            break;
    }
    deferredConnectActive = false;
    networkState = NetworkState::Connected;
    connectAttemptInFlight = false;
    reconnectDelayMs = WIFI_RECONNECT_MIN_MS;

    char ip[16];
    char ssid[33];
    int rssi = 0;
    formatStaIp(ip, sizeof(ip));
    readApInfo(ssid, sizeof(ssid), &rssi);
    // SSID is printed once per connection for diagnosis; never the password.
    printDebugFormatted("Wi-Fi connected: ssid=%s ip=%s rssi=%d source=%s", ssid, ip, rssi,
                        credentialSourceName(credentialSource));
    startNetworkServicesOnce(now);
    return true;
}

void onWifiDisconnected(uint32_t now) {
    if (networkState != NetworkState::Connected) {
        return;
    }
    networkState = NetworkState::Connecting;
    disconnectedSinceMs = now;
    lastWifiStatusLogMs = now;
    printDebugFormatted("Wi-Fi disconnected (reason=%u); reconnecting with backoff", (unsigned)lastDisconnectReason);
}

void serviceNetwork(uint32_t now) {
    consumeProvisioningEvents(now);

    // A disconnect event ends the attempt in flight; the next attempt waits
    // for the current backoff delay.
    const uint32_t disconnects = staDisconnectCount;
    if (disconnects != seenDisconnectCount) {
        seenDisconnectCount = disconnects;
        connectAttemptInFlight = false;
        nextReconnectAtMs = now + reconnectDelayMs;
    }

    const bool connected = staHasIp;
    if (connected && !wasWifiConnected) {
        // A deferred transition leaves wasWifiConnected false so it is
        // re-evaluated, e.g. once CRED_SUCCESS has been consumed.
        wasWifiConnected = onWifiConnected(now);
    } else if (!connected && wasWifiConnected) {
        onWifiDisconnected(now);
        wasWifiConnected = false;
    } else if (!connected) {
        deferredConnectActive = false;
    }

    if (restartPending && (int32_t)(now - restartAtMs) >= 0) {
        Serial.flush();
        ESP.restart();
    }

    switch (networkState) {
        case NetworkState::Provisioning:
            if (!provStartSeen && (uint32_t)(now - provisioningStartedAtMs) >= PROV_START_TIMEOUT_MS) {
                handleProvisioningStartFailure("no PROV_START event", ESP_ERR_TIMEOUT, now);
                break;
            }
            if ((uint32_t)(now - lastProvHintMs) >= PROV_HINT_INTERVAL_MS) {
                lastProvHintMs = now;
                printProvisioningHint();
            }
            break;

        case NetworkState::Connecting:
            if ((uint32_t)(now - lastWifiStatusLogMs) >= WIFI_STATUS_LOG_INTERVAL_MS) {
                lastWifiStatusLogMs = now;
                printDebugFormatted("Wi-Fi: connecting (source=%s, %lu s, last reason=%u, next retry in %lu s)",
                                    credentialSourceName(credentialSource),
                                    (unsigned long)((now - disconnectedSinceMs) / 1000UL),
                                    (unsigned)lastDisconnectReason,
                                    connectAttemptInFlight
                                        ? 0UL
                                        : (unsigned long)((int32_t)(nextReconnectAtMs - now) > 0
                                                              ? (nextReconnectAtMs - now) / 1000UL
                                                              : 0UL));
            }
            if (connectAttemptInFlight &&
                (uint32_t)(now - connectAttemptStartedAtMs) >= WIFI_CONNECT_ATTEMPT_TIMEOUT_MS) {
                connectAttemptInFlight = false;  // no result: treat as failed
                nextReconnectAtMs = now;
            }
            // The firmware owns the reconnect policy (core auto-reconnect is
            // off): connect only when no attempt is in flight.
            if (!connectAttemptInFlight && (int32_t)(now - nextReconnectAtMs) >= 0) {
                esp_wifi_connect();
                markConnectAttempt(now);
                reconnectDelayMs = nextReconnectDelayMs(reconnectDelayMs, WIFI_RECONNECT_MIN_MS, WIFI_RECONNECT_MAX_MS);
            }
            break;

        case NetworkState::Error:
            if (!restartPending && (uint32_t)(now - lastWifiStatusLogMs) >= PROV_HINT_INTERVAL_MS) {
                lastWifiStatusLogMs = now;
                printDebugLine("Wi-Fi: ERROR no usable credentials and BLE provisioning is not running. "
                               "Power-cycle to retry, or add firmware/src/secrets.h for development.");
            }
            break;

        case NetworkState::Starting:
        case NetworkState::Connected:
            break;
    }
}

// ======================= Device status topic =======================

// RSSI of the current AP, read from the driver (see readApInfo()).
bool readStaRssi(int32_t* rssi) {
    wifi_ap_record_t ap;
    memset(&ap, 0, sizeof(ap));
    if (esp_wifi_sta_get_ap_info(&ap) != ESP_OK) {
        return false;
    }
    *rssi = ap.rssi;
    return true;
}

// Publishes resmon/status into the hosted broker (delivered to subscribed
// clients only; QoS 0, not retained) every STATUS_INTERVAL_MS while Wi-Fi
// is connected; the first one STATUS_INTERVAL_MS after the broker starts. No
// serial output per publish; the periodic Status line reports the last
// publish.
void serviceStatusTopic(uint32_t now) {
#if ENABLE_STATUS_TOPIC
    if (!networkServicesStarted || networkState != NetworkState::Connected) {
        return;
    }
    if (!isStatusPublishDue(now, lastStatusPublishMs, STATUS_INTERVAL_MS)) {
        return;
    }
    lastStatusPublishMs = now;

    StatusSnapshot snapshot;
    snapshot.build = FIRMWARE_BUILD_ID;
    snapshot.uptimeS = now / 1000UL;
    snapshot.heapFree = ESP.getFreeHeap();
    snapshot.heapMin = ESP.getMinFreeHeap();
    snapshot.rssi = 0;
    snapshot.hasRssi = readStaRssi(&snapshot.rssi);
    snapshot.wifi = statusWifiFromNetwork(networkState);
    snapshot.prov = statusProvFromNetwork(networkState, credentialSource);
    snapshot.dacPresent = dacPresent;

    char payload[STATUS_PAYLOAD_MAX + 1];
    const size_t length = formatStatusPayload(payload, sizeof(payload), snapshot);
    if (length == 0) {
        if (!statusFormatErrorLogged) {
            statusFormatErrorLogged = true;
            printDebugFormatted("Status topic: ERROR payload does not fit %u bytes; %s not published",
                                (unsigned)STATUS_PAYLOAD_MAX, STATUS_TOPIC);
        }
        return;
    }
    mqtt.publish(STATUS_TOPIC, (const void*)payload, length, (uint8_t)0, false);
    statusEverPublished = true;
#else
    (void)now;
#endif
}

// "off", "never" or "<seconds>s" (since the last status publish).
void formatStatusPublishAge(char* out, size_t outSize, uint32_t now) {
    if (ENABLE_STATUS_TOPIC == 0) {
        snprintf(out, outSize, "off");
    } else if (!statusEverPublished) {
        snprintf(out, outSize, "never");
    } else {
        snprintf(out, outSize, "%lus", (unsigned long)((uint32_t)(now - lastStatusPublishMs) / 1000UL));
    }
}

// ======================= BOOT button / serial commands =======================

void serviceBootButton(uint32_t now) {
    const int level = digitalRead(BOOT_BUTTON_PIN);
    const bool pressed = BOOT_BUTTON_ACTIVE_LOW ? (level == LOW) : (level == HIGH);

    switch (bootButton.update(pressed, now)) {
        case ButtonEvent::Pressed:
            printDebugFormatted("BOOT pressed: hold %u-%u s, then release, to reset Wi-Fi and reopen BLE provisioning",
                                (unsigned)(BOOT_BUTTON_MIN_HOLD_MS / 1000UL),
                                (unsigned)(BOOT_BUTTON_MAX_HOLD_MS / 1000UL));
            break;
        case ButtonEvent::HoldThresholdReached:
            printDebugFormatted("BOOT held %u s: release now (before %u s) to reset Wi-Fi",
                                (unsigned)(BOOT_BUTTON_MIN_HOLD_MS / 1000UL),
                                (unsigned)(BOOT_BUTTON_MAX_HOLD_MS / 1000UL));
            break;
        case ButtonEvent::HeldTooLong:
            printDebugFormatted("BOOT held too long (>%u s), ignored (stuck button or serial DTR?)",
                                (unsigned)(BOOT_BUTTON_MAX_HOLD_MS / 1000UL));
            break;
        case ButtonEvent::ReleasedTooShort:
            printDebugLine("BOOT released early: nothing changed");
            break;
        case ButtonEvent::ReleasedTooLong:
            printDebugLine("BOOT released after holding too long: ignored");
            break;
        case ButtonEvent::Triggered:
            requestProvisioningAndRestart();
            break;
        case ButtonEvent::None:
            break;
    }
}

void handleSerialLine(const char* line) {
    if (parseSerialCommand(line) != SerialCommand::PopQuery) {
        return;
    }

    char response[96];
    bool ok;
    if (networkState == NetworkState::Provisioning && provPopAvailable) {
        ok = formatPopResponse(response, sizeof(response), provServiceName, provPop);
    } else {
        ok = formatPopUnavailable(response, sizeof(response),
                                  popUnavailableStateName(networkState, credentialSource));
    }
    if (ok) {
        Serial.println(response);
    }
}

void serviceSerialInput() {
    for (size_t budget = SERIAL_INPUT_BUDGET_PER_LOOP; budget > 0 && Serial.available() > 0; --budget) {
        const int c = Serial.read();
        if (c < 0) {
            break;
        }
        if (serialLine.feed((char)c)) {
            handleSerialLine(serialLine.line());
        }
    }
}

// ======================= Diagnostics =======================

const char* btControllerStatusName() {
    switch (esp_bt_controller_get_status()) {
        case ESP_BT_CONTROLLER_STATUS_IDLE: return "idle";
        case ESP_BT_CONTROLLER_STATUS_INITED: return "initialised";
        case ESP_BT_CONTROLLER_STATUS_ENABLED: return "enabled";
        default: return "unknown";
    }
}

void printStartupDiagnostics() {
    printDebugFormatted("Build: %s", FIRMWARE_BUILD_ID);
    printDebugFormatted("Features: MQTT broker=enabled DAC=%s BLE provisioning=enabled (Security1+PoP) dev fallback=%s",
                        dacPresent ? "present" : "absent",
                        hasDevFallbackCredentials() ? "configured" : (RESMON_HAS_SECRETS_H ? "empty" : "absent"));
    printDebugLine("Serial port: UART0 at 115200 bps; command: RESMON?POP");
    printDebugFormatted("Pins: I2C SDA=GPIO%d SCL=GPIO%d; BOOT button=GPIO%d (input, hold %u-%u s then release)",
                        I2C_SDA_PIN, I2C_SCL_PIN, BOOT_BUTTON_PIN, (unsigned)(BOOT_BUTTON_MIN_HOLD_MS / 1000UL),
                        (unsigned)(BOOT_BUTTON_MAX_HOLD_MS / 1000UL));
    printDebugFormatted("I2C devices: DAC 0x%02X %s; DAC channels: CPU=%d MEM=%d", DAC_I2C_ADDRESS,
                        dacPresent ? "detected" : "not detected", CPU_OUTPUT_CHANNEL, MEM_OUTPUT_CHANNEL);
    // btStarted() also forces esp32-hal-bt.c into the link, whose strong
    // btInUse() keeps initArduino() from releasing BT memory before setup().
    printDebugFormatted("BT controller: %s (started=%s, memory %s)", btControllerStatusName(),
                        btStarted() ? "yes" : "no", bluetoothMemoryReleased ? "released" : "reserved");
    printDebugFormatted("Wi-Fi: boot=%s state=%s source=%s", bootReasonName(bootReason),
                        networkStateName(networkState), credentialSourceName(credentialSource));
    printDebugFormatted("MQTT: %s", networkServicesStarted ? "listening" : "starts after Wi-Fi connects");
#if ENABLE_STATUS_TOPIC
    printDebugFormatted("Status topic: enabled (%s every %u s while Wi-Fi is connected)", STATUS_TOPIC,
                        (unsigned)(STATUS_INTERVAL_MS / 1000UL));
#else
    printDebugLine("Status topic: disabled (ENABLE_STATUS_TOPIC=0)");
#endif
    printDebugFormatted("Free heap: %u bytes", ESP.getFreeHeap());
}

void printPeriodicDiagnostics(uint32_t now) {
    if ((uint32_t)(now - lastStatusDiagnosticMs) < STATUS_DIAGNOSTIC_INTERVAL_MS) {
        return;
    }
    lastStatusDiagnosticMs = now;

    char statusAge[16];
    formatStatusPublishAge(statusAge, sizeof(statusAge), now);

    if (networkState == NetworkState::Connected) {
        char ip[16];
        char ssid[33];
        int rssi = 0;
        formatStaIp(ip, sizeof(ip));
        readApInfo(ssid, sizeof(ssid), &rssi);
        printDebugFormatted("Status: wifi=connected ip=%s rssi=%d source=%s mqtt=%s dac=%s cpu=%d mem=%d heap=%u min_heap=%u status_last=%s",
                            ip, rssi, credentialSourceName(credentialSource),
                            networkServicesStarted ? "listening" : "stopped", dacPresent ? "present" : "absent",
                            currentCpuDacValue, currentMemDacValue, ESP.getFreeHeap(), ESP.getMinFreeHeap(),
                            statusAge);
    } else {
        printDebugFormatted("Status: wifi=%s source=%s last_reason=%u mqtt=%s dac=%s cpu=%d mem=%d heap=%u min_heap=%u status_last=%s",
                            networkStateName(networkState), credentialSourceName(credentialSource),
                            (unsigned)lastDisconnectReason, networkServicesStarted ? "listening" : "not started",
                            dacPresent ? "present" : "absent", currentCpuDacValue, currentMemDacValue,
                            ESP.getFreeHeap(), ESP.getMinFreeHeap(), statusAge);
    }
}

// ======================= Setup / loop =======================

void beginNetwork(bool provisioningRequested) {
    WiFi.onEvent(onNetworkEvent);
    // The firmware owns the reconnect policy (see serviceNetwork()).
    WiFi.setAutoReconnect(false);
    // Used by WiFi.mode() on the STA paths.
    WiFi.setHostname(MDNS_HOSTNAME);

    if (!wifiLowLevelInit(true)) {
        networkState = NetworkState::Error;
        printDebugLine("Wi-Fi: ERROR driver initialisation failed");
        return;
    }

    // The provisioning manager starts Wi-Fi itself, so set the DHCP hostname
    // on the STA interface directly as well.
    esp_netif_t* staNetif = esp_netif_get_handle_from_ifkey("WIFI_STA_DEF");
    if (staNetif != nullptr) {
        esp_netif_set_hostname(staNetif, MDNS_HOSTNAME);
    }

    const BootDecision decision =
        decideBootAction(provisioningRequested, hasStoredWifiCredentials(), hasDevFallbackCredentials());
    bootReason = decision.reason;
    if (decision.clearProvisioningRequest) {
        setProvisioningRequest(false);
    }

    const uint32_t now = millis();
    disconnectedSinceMs = now;
    lastWifiStatusLogMs = now;
    reconnectDelayMs = WIFI_RECONNECT_MIN_MS;
    nextReconnectAtMs = now + WIFI_RECONNECT_MIN_MS;

    switch (decision.action) {
        case BootAction::ConnectStoredCredentials:
            credentialSource = CredentialSource::Stored;
            networkState = NetworkState::Connecting;
            releaseBluetoothMemory();
            WiFi.mode(WIFI_STA);
            WiFi.begin();
            markConnectAttempt(now);
            break;

        case BootAction::ConnectDevFallback:
            startDevFallback(now);
            break;

        case BootAction::EnterProvisioning:
            if (decision.reason == BootReason::NoCredentials) {
                printDebugLine("Wi-Fi: no stored credentials and no secrets.h fallback; entering BLE provisioning");
            }
            startProvisioning(decision.resetStoredCredentials, now);
            break;
    }
}

void setup() {
    Serial.begin(115200);
    printDebugLine("");
    printDebugFormatted("Build: %s", FIRMWARE_BUILD_ID);

    initProvisioningFailureCounter();

    // GPIO0 is only read after boot. The internal pull-up keeps it high when
    // BOOT is not pressed (any on-board pull-up is unverified).
    pinMode(BOOT_BUTTON_PIN, INPUT_PULLUP);

    initDac();

    const bool provisioningRequested = loadProvisioningIdentity();

    mqtt.subscribe(CPU_TOPIC, handleMqttMetric);
    mqtt.subscribe(MEM_TOPIC, handleMqttMetric);

    beginNetwork(provisioningRequested);

    printStartupDiagnostics();
    lastStatusDiagnosticMs = millis();
}

void loop() {
    const uint32_t now = millis();

    serviceBootButton(now);
    serviceSerialInput();
    serviceNetwork(now);
    if (networkServicesStarted) {
        mqtt.loop();
    }
    serviceStatusTopic(now);
    decayStaleMetrics();
    updateDacOutput();
    printPeriodicDiagnostics(now);
    yield();
}
