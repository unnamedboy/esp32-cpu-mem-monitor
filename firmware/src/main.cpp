#include <Arduino.h>
#include <WiFi.h>
#include <WiFiProv.h>
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
#include <esp_bt.h>
#include <esp_mac.h>
#include <esp_wifi.h>
#include "metric_conversion.h"
#include "button_long_press.h"
#include "pop_code.h"
#include "provisioning_logic.h"
#include "serial_command.h"

// Defined in the Arduino WiFi library (WiFiGeneric.cpp); WiFiProv uses the
// same declaration. Initialises the Wi-Fi driver without starting the radio,
// so the stored STA configuration can be inspected before deciding what to do.
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
bool networkServicesStarted = false;
bool wasWifiConnected = false;
bool bluetoothMemoryReleased = false;
uint32_t provisioningStartedAtMs = 0;
uint32_t lastProvHintMs = 0;
uint32_t lastWifiStatusLogMs = 0;
uint32_t lastStatusDiagnosticMs = 0;
uint32_t disconnectedSinceMs = 0;
uint32_t nextReconnectAtMs = 0;
uint32_t reconnectDelayMs = 0;

// ---- Set from the Arduino network event task, consumed in loop() ----
volatile bool evtProvStarted = false;
volatile bool evtProvCredReceived = false;
volatile bool evtProvCredFailed = false;
volatile bool evtProvCredSuccess = false;
volatile bool evtProvEnded = false;
volatile int evtProvFailReason = 0;
volatile uint8_t lastDisconnectReason = 0;

LongPressDetector bootButton(BOOT_BUTTON_DEBOUNCE_MS, BOOT_BUTTON_LONG_PRESS_MS);
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

void updateDacOutput() {
    if (millis() - lastDacChangeTime >= UPDATE_INTERVAL) {
        lastDacChangeTime = millis();
        writeDacValue();
        printDebugFormatted("DAC values: cpu=%d mem=%d", currentCpuDacValue, currentMemDacValue);
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

void clearProvisioningRequest() {
    Preferences prefs;
    if (prefs.begin(PROV_NVS_NAMESPACE, false)) {
        prefs.remove(PROV_NVS_KEY_REQUEST);
        prefs.end();
    }
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

// Frees the BT controller/host memory when BLE is not going to be used in
// this boot. BLE cannot be started again until the next restart.
void releaseBluetoothMemory() {
    if (bluetoothMemoryReleased) {
        return;
    }
    if (esp_bt_controller_get_status() != ESP_BT_CONTROLLER_STATUS_IDLE) {
        return;
    }
    // Returns an error if the core already released it at startup; the
    // outcome is the same.
    esp_bt_mem_release(ESP_BT_MODE_BTDM);
    bluetoothMemoryReleased = true;
}

void printProvisioningHint() {
    if (!provPopAvailable) {
        printDebugFormatted("Provisioning: name=%s pop=unavailable (NVS error)", provServiceName);
        return;
    }
    // Physical USB access is trusted (see PROVISIONING.md).
    printDebugFormatted("Provisioning: name=%s pop=%s", provServiceName, provPop);
}

// Persist the request, wipe stored Wi-Fi credentials, and restart into
// provisioning mode. Triggered only by a BOOT long-press.
void requestProvisioningAndRestart() {
    printDebugFormatted("BOOT held %lu ms: clearing Wi-Fi credentials and restarting into BLE provisioning",
                        (unsigned long)BOOT_BUTTON_LONG_PRESS_MS);

    Preferences prefs;
    if (prefs.begin(PROV_NVS_NAMESPACE, false)) {
        prefs.putBool(PROV_NVS_KEY_REQUEST, true);
        prefs.end();
    } else {
        printDebugLine("Provisioning: could not persist request flag; credentials will still be cleared");
    }

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
        case ARDUINO_EVENT_WIFI_STA_DISCONNECTED:
            lastDisconnectReason = event->event_info.wifi_sta_disconnected.reason;
            break;
        default:
            break;
    }
}

void consumeProvisioningEvents() {
    if (evtProvStarted) {
        evtProvStarted = false;
        provStartSeen = true;
        if (networkState == NetworkState::Error) {
            networkState = NetworkState::Provisioning;  // started late, after the timeout
        }
        printDebugFormatted("Provisioning: BLE advertising as %s (Security1 + PoP)", provServiceName);
    }

    if (evtProvCredReceived) {
        evtProvCredReceived = false;
        printDebugLine("Provisioning: Wi-Fi credentials received; connecting");
    }

    if (evtProvCredFailed) {
        evtProvCredFailed = false;
        const bool authError = evtProvFailReason == (int)WIFI_PROV_STA_AUTH_ERROR;
        printDebugFormatted("Provisioning: Wi-Fi connection failed (%s)",
                            authError ? "authentication error" : "network not found");
        // Clear the rejected credentials and let the client send new ones
        // in the same provisioning session.
        if (wifi_prov_mgr_reset_sm_state_on_failure() == ESP_OK) {
            printDebugLine("Provisioning: ready for new credentials");
        } else {
            printDebugLine("Provisioning: could not reset state; hold BOOT 5 s to restart provisioning");
        }
    }

    if (evtProvCredSuccess) {
        evtProvCredSuccess = false;
        credentialSource = CredentialSource::Stored;
        printDebugLine("Provisioning: credentials accepted and stored");
    }

    if (evtProvEnded) {
        evtProvEnded = false;
        bluetoothMemoryReleased = true;  // FREE_BTDM scheme handler released it
        printDebugFormatted("Provisioning: ended; BLE stopped and BT memory released (free heap %u bytes)",
                            ESP.getFreeHeap());
    }
}

// ======================= Network services =======================

// mDNS and the MQTT broker are started once, on the first connection. Both
// keep working across Wi-Fi reconnects (the broker listens on INADDR_ANY and
// the mDNS responder follows the STA interface state).
void startNetworkServicesOnce() {
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

    printDebugLine("========== ESP32 CPU Monitor network ready ==========");
    printDebugLine("MQTT broker:");
    printDebugFormatted("   - Host: %s.local", MDNS_HOSTNAME);
    printDebugFormatted("   - Port: %d", MQTT_BROKER_PORT);
    printDebugFormatted("   - CPU topic: %s", CPU_TOPIC);
    printDebugFormatted("   - MEM topic: %s", MEM_TOPIC);
    printDebugLine("=====================================================");
}

void onWifiConnected() {
    if (networkState == NetworkState::Provisioning) {
        credentialSource = CredentialSource::Stored;
    }
    networkState = NetworkState::Connected;
    reconnectDelayMs = WIFI_RECONNECT_MIN_MS;

    // SSID is printed once per connection for diagnosis; never the password.
    printDebugFormatted("Wi-Fi connected: ssid=%s ip=%s source=%s", WiFi.SSID().c_str(),
                        WiFi.localIP().toString().c_str(), credentialSourceName(credentialSource));
    startNetworkServicesOnce();
}

void onWifiDisconnected(uint32_t now) {
    if (networkState != NetworkState::Connected) {
        return;
    }
    networkState = NetworkState::Connecting;
    disconnectedSinceMs = now;
    lastWifiStatusLogMs = now;
    reconnectDelayMs = WIFI_RECONNECT_MIN_MS;
    nextReconnectAtMs = now + reconnectDelayMs;
    printDebugFormatted("Wi-Fi disconnected (reason=%u); reconnecting with backoff", (unsigned)lastDisconnectReason);
}

void serviceNetwork(uint32_t now) {
    consumeProvisioningEvents();

    const bool connected = WiFi.status() == WL_CONNECTED;
    if (connected && !wasWifiConnected) {
        onWifiConnected();
    } else if (!connected && wasWifiConnected) {
        onWifiDisconnected(now);
    }
    wasWifiConnected = connected;

    switch (networkState) {
        case NetworkState::Provisioning:
            if (!provStartSeen && (uint32_t)(now - provisioningStartedAtMs) >= PROV_START_TIMEOUT_MS) {
                networkState = NetworkState::Error;
                lastWifiStatusLogMs = now;
                printDebugLine("Provisioning: BLE provisioning did not start");
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
                printDebugFormatted("Wi-Fi: connecting (source=%s, %lu s, status=%d, last reason=%u)",
                                    credentialSourceName(credentialSource),
                                    (unsigned long)((now - disconnectedSinceMs) / 1000UL), (int)WiFi.status(),
                                    (unsigned)lastDisconnectReason);
            }
            if ((int32_t)(now - nextReconnectAtMs) >= 0) {
                WiFi.reconnect();
                reconnectDelayMs = nextReconnectDelayMs(reconnectDelayMs, WIFI_RECONNECT_MIN_MS, WIFI_RECONNECT_MAX_MS);
                nextReconnectAtMs = now + reconnectDelayMs;
            }
            break;

        case NetworkState::Error:
            if ((uint32_t)(now - lastWifiStatusLogMs) >= PROV_HINT_INTERVAL_MS) {
                lastWifiStatusLogMs = now;
                printDebugLine("Wi-Fi: no usable credentials and BLE provisioning is not running. "
                               "Hold BOOT 5 s to retry, or add firmware/src/secrets.h for development.");
            }
            break;

        case NetworkState::Starting:
        case NetworkState::Connected:
            break;
    }
}

// ======================= BOOT button / serial commands =======================

void serviceBootButton(uint32_t now) {
    const int level = digitalRead(BOOT_BUTTON_PIN);
    const bool pressed = BOOT_BUTTON_ACTIVE_LOW ? (level == LOW) : (level == HIGH);

    switch (bootButton.update(pressed, now)) {
        case ButtonEvent::Pressed:
            printDebugFormatted("BOOT button pressed: hold %lu s to reset Wi-Fi and reopen BLE provisioning",
                                (unsigned long)(BOOT_BUTTON_LONG_PRESS_MS / 1000UL));
            break;
        case ButtonEvent::Released:
            printDebugLine("BOOT button released: provisioning reset cancelled");
            break;
        case ButtonEvent::LongPress:
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

void printStartupDiagnostics() {
    printDebugFormatted("Build: %s", FIRMWARE_BUILD_ID);
    printDebugFormatted("Features: MQTT broker=enabled DAC=%s BLE provisioning=enabled (Security1+PoP) dev fallback=%s",
                        dacPresent ? "present" : "absent",
                        hasDevFallbackCredentials() ? "configured" : (RESMON_HAS_SECRETS_H ? "empty" : "absent"));
    printDebugLine("Serial port: UART0 at 115200 bps; command: RESMON?POP");
    printDebugFormatted("Pins: I2C SDA=GPIO%d SCL=GPIO%d; BOOT button=GPIO%d (input, long-press %lu ms)", I2C_SDA_PIN,
                        I2C_SCL_PIN, BOOT_BUTTON_PIN, (unsigned long)BOOT_BUTTON_LONG_PRESS_MS);
    printDebugFormatted("I2C devices: DAC 0x%02X %s; DAC channels: CPU=%d MEM=%d", DAC_I2C_ADDRESS,
                        dacPresent ? "detected" : "not detected", CPU_OUTPUT_CHANNEL, MEM_OUTPUT_CHANNEL);
    printDebugFormatted("Wi-Fi: boot=%s state=%s source=%s", bootReasonName(bootReason),
                        networkStateName(networkState), credentialSourceName(credentialSource));
    printDebugFormatted("MQTT: %s", networkServicesStarted ? "listening" : "starts after Wi-Fi connects");
    printDebugFormatted("Free heap: %u bytes", ESP.getFreeHeap());
}

void printPeriodicDiagnostics(uint32_t now) {
    if ((uint32_t)(now - lastStatusDiagnosticMs) < STATUS_DIAGNOSTIC_INTERVAL_MS) {
        return;
    }
    lastStatusDiagnosticMs = now;

    if (networkState == NetworkState::Connected) {
        printDebugFormatted("Status: wifi=connected ip=%s rssi=%d source=%s mqtt=%s dac=%s heap=%u min_heap=%u",
                            WiFi.localIP().toString().c_str(), (int)WiFi.RSSI(),
                            credentialSourceName(credentialSource), networkServicesStarted ? "listening" : "stopped",
                            dacPresent ? "present" : "absent", ESP.getFreeHeap(), ESP.getMinFreeHeap());
    } else {
        printDebugFormatted("Status: wifi=%s source=%s mqtt=%s dac=%s heap=%u min_heap=%u",
                            networkStateName(networkState), credentialSourceName(credentialSource),
                            networkServicesStarted ? "listening" : "not started", dacPresent ? "present" : "absent",
                            ESP.getFreeHeap(), ESP.getMinFreeHeap());
    }
}

// ======================= Setup / loop =======================

void beginNetwork(bool provisioningRequested) {
    WiFi.onEvent(onNetworkEvent);

    if (!wifiLowLevelInit(true)) {
        networkState = NetworkState::Error;
        printDebugLine("Wi-Fi: driver initialisation failed");
        return;
    }

    const BootDecision decision =
        decideBootAction(provisioningRequested, hasStoredWifiCredentials(), hasDevFallbackCredentials());
    bootReason = decision.reason;
    if (decision.clearProvisioningRequest) {
        clearProvisioningRequest();
    }

    const uint32_t now = millis();
    disconnectedSinceMs = now;
    lastWifiStatusLogMs = now;
    reconnectDelayMs = WIFI_RECONNECT_MIN_MS;
    // Leave the first connection attempt (and the core's own single retry)
    // time to finish before the loop starts forcing reconnects.
    nextReconnectAtMs = now + 2 * WIFI_RECONNECT_MIN_MS;

    switch (decision.action) {
        case BootAction::ConnectStoredCredentials:
            credentialSource = CredentialSource::Stored;
            networkState = NetworkState::Connecting;
            releaseBluetoothMemory();
            WiFi.mode(WIFI_STA);
            WiFi.begin();
            break;

        case BootAction::ConnectDevFallback:
            credentialSource = CredentialSource::DevFallback;
            networkState = NetworkState::Connecting;
            releaseBluetoothMemory();
            // Keep secrets.h credentials out of the Wi-Fi NVS store so they
            // are never mistaken for BLE-provisioned credentials.
            esp_wifi_set_storage(WIFI_STORAGE_RAM);
            WiFi.persistent(false);
            WiFi.mode(WIFI_STA);
            WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
            break;

        case BootAction::EnterProvisioning:
            credentialSource = CredentialSource::None;
            networkState = NetworkState::Provisioning;
            if (decision.reason == BootReason::NoCredentials) {
                printDebugLine("Wi-Fi: no stored credentials and no secrets.h fallback; entering BLE provisioning");
            }
            provisioningStartedAtMs = now;
            lastProvHintMs = now;
            printProvisioningHint();
            WiFiProv.beginProvision(WIFI_PROV_SCHEME_BLE, WIFI_PROV_SCHEME_HANDLER_FREE_BTDM, WIFI_PROV_SECURITY_1,
                                    provPop, provServiceName, NULL, PROV_BLE_SERVICE_UUID,
                                    decision.resetStoredCredentials);
            break;
    }
}

void setup() {
    Serial.begin(115200);
    printDebugLine("");
    printDebugFormatted("Build: %s", FIRMWARE_BUILD_ID);

    // GPIO0 is only read after boot; the board pull-up keeps it high.
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
    decayStaleMetrics();
    updateDacOutput();
    printPeriodicDiagnostics(now);
    yield();
}
