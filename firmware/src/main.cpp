#include <Arduino.h>
#include <WiFi.h>
#include <ESPmDNS.h>
#include <PicoMQTT.h>
#include <ctype.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "config.h"
#include <Wire.h>
#include <DFRobot_GP8XXX.h>
#include "metric_conversion.h"

WiFiServer mqttTcpServer(MQTT_BROKER_PORT);
PicoMQTT::Server mqtt(mqttTcpServer);

constexpr size_t DEBUG_LOG_MAX_CHARS = 192;
int currentCpuDacValue = 100;
int currentMemDacValue = 0;
unsigned long lastDacChangeTime = 0;
unsigned long lastCpuMetricTime = 0;
unsigned long lastMemMetricTime = 0;
unsigned long lastStaleDecayTime = 0;
DFRobot_GP8413 dac(DAC_I2C_ADDRESS);
bool dacPresent = false;

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

void setup() {
    Serial.begin(115200);
    printDebugFormatted("Build: %s", FIRMWARE_BUILD_ID);
    printDebugLine("Features: MQTT broker=enabled DAC=enabled");
    printDebugLine("Serial port: UART0 at 115200 bps");
    printDebugFormatted("I2C pins: SDA=%d SCL=%d DAC=0x%02X", I2C_SDA_PIN, I2C_SCL_PIN, DAC_I2C_ADDRESS);
    printDebugFormatted("DAC channels: CPU=%d MEM=%d", CPU_OUTPUT_CHANNEL, MEM_OUTPUT_CHANNEL);
    printDebugFormatted("Free heap: %u bytes", ESP.getFreeHeap());

    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    while (WiFi.status() != WL_CONNECTED) {
        delay(500);
        Serial.print(".");
    }

    printDebugLine("\nWiFi connected");
    printDebugFormatted("IP address: %s", WiFi.localIP().toString().c_str());

    if (MDNS.begin(MDNS_HOSTNAME)) {
        printDebugFormatted("mDNS hostname: %s.local", MDNS_HOSTNAME);
    } else {
        printDebugLine("Error setting up MDNS responder!");
    }

    Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
    if (dac.begin() == 0) {
        dacPresent = true;
        dac.setDACOutRange(DFRobot_GP8XXX::eOutputRange5V);
        dac.setDACOutVoltage(0, CPU_OUTPUT_CHANNEL);
        dac.setDACOutVoltage(0, MEM_OUTPUT_CHANNEL);
        printDebugLine("DAC initialized at 0x59 (0-5V)");
    } else {
        dacPresent = false;
        printDebugFormatted("DAC not found at 0x%02X; running without external DAC", DAC_I2C_ADDRESS);
    }

    MDNS.addService("mqtt", "tcp", MQTT_BROKER_PORT);

    mqtt.subscribe(CPU_TOPIC, handleMqttMetric);
    mqtt.subscribe(MEM_TOPIC, handleMqttMetric);
    mqtt.begin();

    printDebugLine("\n========== ESP32 CPU Monitor started ==========");
    printDebugLine("MQTT broker:");
    printDebugFormatted("   - Host: %s.local", MDNS_HOSTNAME);
    printDebugFormatted("   - Port: %d", MQTT_BROKER_PORT);
    printDebugFormatted("   - CPU topic: %s", CPU_TOPIC);
    printDebugFormatted("   - MEM topic: %s", MEM_TOPIC);
    printDebugLine("==============================================");
}

void loop() {
    mqtt.loop();
    decayStaleMetrics();
    updateDacOutput();
    yield();
}
