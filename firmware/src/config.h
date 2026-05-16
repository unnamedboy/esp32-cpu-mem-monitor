#ifndef CONFIG_H
#define CONFIG_H

// ================= 1. Wi-Fi 配置 =================
const char* FIRMWARE_BUILD_ID = "phase1-mqtt";

#if __has_include("secrets.h")
#include "secrets.h"
#else
const char* WIFI_SSID = "";
const char* WIFI_PASSWORD = "";
#endif

// ================= 2. 硬件/引脚配置 =================
const int I2C_SDA_PIN = 21;
const int I2C_SCL_PIN = 22;
const uint8_t DAC_I2C_ADDRESS = 0x59;
const int CPU_OUTPUT_CHANNEL = 0;  // GP8413 channel 0
const int MEM_OUTPUT_CHANNEL = 1;  // GP8413 channel 1
const int UPDATE_INTERVAL = 1000;  // 串口打印频率 (ms)
const int METRIC_TIMEOUT_MS = 5000; // No data timeout before output starts returning to 0
const int STALE_DECAY_INTERVAL_MS = 50;
const int STALE_DECAY_STEP = 1;

// ================= 3. 网络接口配置 =================
const char* MDNS_HOSTNAME = "resmon"; 

// ================= 4. MQTT Broker 配置 =================
const int MQTT_BROKER_PORT = 1883;
const char* CPU_TOPIC = "resmon/cpu";
const char* MEM_TOPIC = "resmon/mem";

#endif
