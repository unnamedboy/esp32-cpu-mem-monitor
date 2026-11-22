#ifndef CONFIG_H
#define CONFIG_H

#include <stddef.h>
#include <stdint.h>

// Log level guard. platformio.ini pins CORE_DEBUG_LEVEL=1 (errors only).
// - Level 5 (verbose) is a real leak: the Arduino core logs the SSID and
//   Wi-Fi password received during provisioning (WiFiGeneric.cpp,
//   PROV_CRED_RECV log_v).
// - Levels 3-4 leak no secrets (WiFiProv is not linked, and the precompiled
//   ESP-IDF libraries are built with CONFIG_LOG_MAXIMUM_LEVEL=1), but they
//   put Wi-Fi/event chatter on UART0, which also carries the RESMON?POP
//   pairing query. They are refused as a safety margin.
#if defined(CORE_DEBUG_LEVEL) && CORE_DEBUG_LEVEL >= 3
#error "CORE_DEBUG_LEVEL >= 3 is refused (5 logs the provisioned Wi-Fi password; 3-4 flood the pairing UART); keep it <= 2"
#elif !defined(CORE_DEBUG_LEVEL) && defined(CONFIG_ARDUHAL_LOG_DEFAULT_LEVEL) && CONFIG_ARDUHAL_LOG_DEFAULT_LEVEL >= 3
#error "Arduino default log level >= 3 is refused; set -DCORE_DEBUG_LEVEL=1"
#endif

// ================= 1. Build / Wi-Fi 配置 =================
const char* FIRMWARE_BUILD_ID = "phase4-ble-prov-status";

// Optional development fallback credentials (ignored firmware/src/secrets.h).
// Used only when no BLE-provisioned credentials are stored and no
// provisioning was requested. See PROVISIONING.md for the boot order.
#if __has_include("secrets.h")
#include "secrets.h"
#define RESMON_HAS_SECRETS_H 1
#else
const char* WIFI_SSID = "";
const char* WIFI_PASSWORD = "";
#define RESMON_HAS_SECRETS_H 0
#endif

// ================= 2. 硬件/引脚配置 =================
const int I2C_SDA_PIN = 21;
const int I2C_SCL_PIN = 22;
const uint8_t DAC_I2C_ADDRESS = 0x59;
const int CPU_OUTPUT_CHANNEL = 0;  // GP8413 channel 0
const int MEM_OUTPUT_CHANNEL = 1;  // GP8413 channel 1
const int UPDATE_INTERVAL = 1000;  // DAC refresh interval (ms)
const uint32_t DAC_LOG_MIN_INTERVAL_MS = 10000;  // "DAC values" printed only on change, at most this often
const int METRIC_TIMEOUT_MS = 5000; // No data timeout before output starts returning to 0
const int STALE_DECAY_INTERVAL_MS = 50;
const int STALE_DECAY_STEP = 1;

// On-board BOOT button (GPIO0, boot-strapping pin). Input only, read only
// after boot has finished; the dev board already has a pull-up and the
// button pulls the pin to GND. No external wiring. Holding it while the
// board resets enters the ROM serial download mode instead of the firmware.
// Reset Wi-Fi + reopen provisioning: hold BOOT for MIN..MAX, then RELEASE.
// Holds longer than MAX are ignored (stuck button, or a serial tool holding
// DTR asserted, which pulls GPIO0 low via the auto-program circuit).
// See HARDWARE.md.
const int BOOT_BUTTON_PIN = 0;
const bool BOOT_BUTTON_ACTIVE_LOW = true;
const uint32_t BOOT_BUTTON_DEBOUNCE_MS = 50;
const uint32_t BOOT_BUTTON_MIN_HOLD_MS = 5000;
const uint32_t BOOT_BUTTON_MAX_HOLD_MS = 15000;

// ================= 3. 网络接口配置 =================
const char* MDNS_HOSTNAME = "resmon";
const uint32_t WIFI_RECONNECT_MIN_MS = 5000;          // first STA reconnect attempt delay
const uint32_t WIFI_CONNECT_ATTEMPT_TIMEOUT_MS = 30000; // attempt with no result is treated as failed
const uint32_t WIFI_RECONNECT_MAX_MS = 60000;         // reconnect backoff ceiling
const uint32_t WIFI_STATUS_LOG_INTERVAL_MS = 10000;   // "still connecting" log rate
const uint32_t STATUS_DIAGNOSTIC_INTERVAL_MS = 60000; // periodic health line

// ================= 4. BLE provisioning 配置 =================
// Contract: PROVISIONING.md. Security1 + PoP, BLE scheme, BT memory freed
// after provisioning.
const char* PROV_NVS_NAMESPACE = "resmon";
const char* PROV_NVS_KEY_POP = "pop";
const char* PROV_NVS_KEY_REQUEST = "prov_req";
const uint32_t PROV_HINT_INTERVAL_MS = 30000;   // serial "Provisioning: name=.. pop=.." rate
const uint32_t PROV_START_TIMEOUT_MS = 5000;    // no PROV_START by then -> start failure
// An IP while provisioning normally precedes CRED_SUCCESS by milliseconds;
// warn only if no credentials were accepted after this long.
const uint32_t PROV_DEFERRED_CONNECT_WARN_MS = 5000;
const uint32_t PROV_START_MAX_ATTEMPTS = 3;     // consecutive failed starts before giving up
const uint32_t PROV_RETRY_RESTART_DELAY_MS = 3000; // log visible before the retry restart
// Auto-stop is disabled so BLE outlives the client's Connected status read.
// The firmware stops provisioning this long after credentials succeed; the
// manager then keeps BLE up for PROV_BLE_CLEANUP_DELAY_MS more before
// PROV_END and the BT memory release.
const uint32_t PROV_STOP_AFTER_SUCCESS_MS = 30000;
const uint32_t PROV_BLE_CLEANUP_DELAY_MS = 5000; // contract: >= 5000
const size_t SERIAL_INPUT_BUDGET_PER_LOOP = 64; // max serial bytes consumed per loop()
// GATT service UUID 021a9004-0382-4aea-bff4-6b3f1c5adfb4 (Arduino WiFiProv
// default), in the little-endian byte order wifi_prov_scheme_ble expects.
uint8_t PROV_BLE_SERVICE_UUID[16] = {
    0xb4, 0xdf, 0x5a, 0x1c, 0x3f, 0x6b, 0xf4, 0xbf,
    0xea, 0x4a, 0x82, 0x03, 0x04, 0x90, 0x1a, 0x02,
};

// ================= 5. MQTT Broker 配置 =================
const int MQTT_BROKER_PORT = 1883;
const char* CPU_TOPIC = "resmon/cpu";
const char* MEM_TOPIC = "resmon/mem";

// Device status topic (COMMUNICATION.md "Device status"). The firmware
// publishes a compact JSON health snapshot into its own broker: QoS 0, not
// retained, every STATUS_INTERVAL_MS while Wi-Fi is connected (the first one
// STATUS_INTERVAL_MS after the broker starts). Build with
// -DENABLE_STATUS_TOPIC=0 to disable.
#ifndef ENABLE_STATUS_TOPIC
#define ENABLE_STATUS_TOPIC 1
#endif
const char* STATUS_TOPIC = "resmon/status";
const uint32_t STATUS_INTERVAL_MS = 10000;
const size_t STATUS_PAYLOAD_MAX = 256;  // max payload bytes (contract limit)

#endif
