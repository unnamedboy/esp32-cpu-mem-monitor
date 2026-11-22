#include <unity.h>
#include <string.h>
#include "status_payload.h"

static StatusSnapshot sampleSnapshot() {
    StatusSnapshot s;
    s.build = "phase4-ble-prov-status";
    s.uptimeS = 1234;
    s.heapFree = 182344;
    s.heapMin = 150000;
    s.hasRssi = true;
    s.rssi = -61;
    s.wifi = StatusWifi::Connected;
    s.prov = StatusProv::Provisioned;
    s.dacPresent = true;
    return s;
}

static const char* const SAMPLE_PAYLOAD =
    "{\"v\":1,\"build\":\"phase4-ble-prov-status\",\"uptime_s\":1234,\"heap_free\":182344,\"heap_min\":150000,"
    "\"rssi\":-61,\"wifi\":\"connected\",\"prov\":\"provisioned\",\"dac\":true}";

void test_sample_payload_exact_output() {
    char out[257];
    const size_t n = formatStatusPayload(out, sizeof(out), sampleSnapshot());
    TEST_ASSERT_EQUAL_STRING(SAMPLE_PAYLOAD, out);
    TEST_ASSERT_EQUAL_UINT32(strlen(SAMPLE_PAYLOAD), n);
    TEST_ASSERT_TRUE(n <= 256);
}

void test_rssi_omitted_when_not_available() {
    StatusSnapshot s = sampleSnapshot();
    s.hasRssi = false;
    s.rssi = -61;
    s.wifi = StatusWifi::Connecting;
    s.prov = StatusProv::DevFallback;
    s.dacPresent = false;
    char out[257];
    const size_t n = formatStatusPayload(out, sizeof(out), s);
    TEST_ASSERT_EQUAL_STRING(
        "{\"v\":1,\"build\":\"phase4-ble-prov-status\",\"uptime_s\":1234,\"heap_free\":182344,\"heap_min\":150000,"
        "\"wifi\":\"connecting\",\"prov\":\"dev-fallback\",\"dac\":false}",
        out);
    TEST_ASSERT_EQUAL_UINT32(strlen(out), n);
    TEST_ASSERT_NULL(strstr(out, "rssi"));
}

void test_extreme_numbers_fit_in_256_bytes() {
    StatusSnapshot s = sampleSnapshot();
    s.uptimeS = 0xFFFFFFFFu;
    s.heapFree = 0xFFFFFFFFu;
    s.heapMin = 0xFFFFFFFFu;
    s.rssi = -128;
    s.wifi = StatusWifi::Disconnected;
    s.prov = StatusProv::Unconfigured;
    char out[257];
    const size_t n = formatStatusPayload(out, sizeof(out), s);
    TEST_ASSERT_TRUE(n > 0);
    TEST_ASSERT_TRUE(n <= 256);
    TEST_ASSERT_NOT_NULL(strstr(out, "\"uptime_s\":4294967295,"));
    TEST_ASSERT_NOT_NULL(strstr(out, "\"rssi\":-128,"));
    TEST_ASSERT_NOT_NULL(strstr(out, "\"wifi\":\"disconnected\""));
    TEST_ASSERT_NOT_NULL(strstr(out, "\"prov\":\"unconfigured\""));

    // Worst case: longest accepted build plus the extreme numbers.
    char build[STATUS_BUILD_ESCAPED_MAX + 1];
    memset(build, 'b', sizeof(build) - 1);
    build[sizeof(build) - 1] = '\0';
    s.build = build;
    s.dacPresent = false;
    const size_t worst = formatStatusPayload(out, sizeof(out), s);
    TEST_ASSERT_TRUE(worst > 0);
    TEST_ASSERT_TRUE(worst <= 256);
}

void test_buffer_too_small_fails_without_partial_json() {
    const size_t full = strlen(SAMPLE_PAYLOAD);
    char out[257];

    // Exactly fits (length + NUL).
    TEST_ASSERT_EQUAL_UINT32(full, formatStatusPayload(out, full + 1, sampleSnapshot()));

    // One byte short, and much too short: failure, empty output.
    memset(out, 'x', sizeof(out));
    TEST_ASSERT_EQUAL_UINT32(0, formatStatusPayload(out, full, sampleSnapshot()));
    TEST_ASSERT_EQUAL_STRING("", out);

    memset(out, 'x', sizeof(out));
    TEST_ASSERT_EQUAL_UINT32(0, formatStatusPayload(out, 10, sampleSnapshot()));
    TEST_ASSERT_EQUAL_STRING("", out);

    TEST_ASSERT_EQUAL_UINT32(0, formatStatusPayload(nullptr, sizeof(out), sampleSnapshot()));
    out[0] = 'x';
    TEST_ASSERT_EQUAL_UINT32(0, formatStatusPayload(out, 0, sampleSnapshot()));
    TEST_ASSERT_EQUAL_CHAR('x', out[0]);
}

void test_overlong_build_fails() {
    // Matches the daemon, which keeps at most 64 characters of `build`.
    TEST_ASSERT_EQUAL_UINT32(64, STATUS_BUILD_ESCAPED_MAX);

    char build[STATUS_BUILD_ESCAPED_MAX + 2];
    memset(build, 'a', sizeof(build) - 1);
    build[sizeof(build) - 1] = '\0';  // one byte more than accepted
    StatusSnapshot s = sampleSnapshot();
    s.build = build;
    char out[512];
    memset(out, 'x', sizeof(out));
    TEST_ASSERT_EQUAL_UINT32(0, formatStatusPayload(out, sizeof(out), s));
    TEST_ASSERT_EQUAL_STRING("", out);

    build[sizeof(build) - 2] = '\0';  // exactly the limit
    TEST_ASSERT_TRUE(formatStatusPayload(out, sizeof(out), s) > 0);

    // The limit applies to the escaped form: 63 chars + `"` -> 65 bytes.
    build[sizeof(build) - 3] = '"';
    memset(out, 'x', sizeof(out));
    TEST_ASSERT_EQUAL_UINT32(0, formatStatusPayload(out, sizeof(out), s));
    TEST_ASSERT_EQUAL_STRING("", out);
}

void test_build_string_is_escaped() {
    StatusSnapshot s = sampleSnapshot();
    s.build = "a\"b\\c\nd\x01";
    char out[257];
    TEST_ASSERT_TRUE(formatStatusPayload(out, sizeof(out), s) > 0);
    TEST_ASSERT_NOT_NULL(strstr(out, "\"build\":\"a\\\"b\\\\c\\u000ad\\u0001\","));

    s.build = nullptr;
    TEST_ASSERT_TRUE(formatStatusPayload(out, sizeof(out), s) > 0);
    TEST_ASSERT_NOT_NULL(strstr(out, "\"build\":\"\","));
}

void test_escape_rejects_small_buffer() {
    char out[4];
    TEST_ASSERT_TRUE(escapeStatusJsonString(out, sizeof(out), "abc"));
    TEST_ASSERT_EQUAL_STRING("abc", out);
    TEST_ASSERT_FALSE(escapeStatusJsonString(out, sizeof(out), "abcd"));
    TEST_ASSERT_EQUAL_STRING("", out);
    // An escape sequence is never split.
    TEST_ASSERT_FALSE(escapeStatusJsonString(out, sizeof(out), "ab\""));
    TEST_ASSERT_EQUAL_STRING("", out);
}

void test_wifi_mapping_from_network_state() {
    TEST_ASSERT_EQUAL_STRING("connected", statusWifiName(statusWifiFromNetwork(NetworkState::Connected)));
    TEST_ASSERT_EQUAL_STRING("connecting", statusWifiName(statusWifiFromNetwork(NetworkState::Connecting)));
    TEST_ASSERT_EQUAL_STRING("disconnected", statusWifiName(statusWifiFromNetwork(NetworkState::Starting)));
    TEST_ASSERT_EQUAL_STRING("disconnected", statusWifiName(statusWifiFromNetwork(NetworkState::Provisioning)));
    TEST_ASSERT_EQUAL_STRING("disconnected", statusWifiName(statusWifiFromNetwork(NetworkState::Error)));
}

void test_prov_mapping_from_network_state() {
    TEST_ASSERT_EQUAL_STRING("provisioning",
                             statusProvName(statusProvFromNetwork(NetworkState::Provisioning, CredentialSource::None)));
    TEST_ASSERT_EQUAL_STRING("provisioned",
                             statusProvName(statusProvFromNetwork(NetworkState::Connected, CredentialSource::Stored)));
    TEST_ASSERT_EQUAL_STRING("provisioned",
                             statusProvName(statusProvFromNetwork(NetworkState::Connecting, CredentialSource::Stored)));
    TEST_ASSERT_EQUAL_STRING("dev-fallback", statusProvName(statusProvFromNetwork(NetworkState::Connected,
                                                                                  CredentialSource::DevFallback)));
    TEST_ASSERT_EQUAL_STRING("unconfigured",
                             statusProvName(statusProvFromNetwork(NetworkState::Starting, CredentialSource::None)));
    TEST_ASSERT_EQUAL_STRING("unconfigured",
                             statusProvName(statusProvFromNetwork(NetworkState::Error, CredentialSource::None)));
}

void test_publish_due_after_interval() {
    TEST_ASSERT_FALSE(isStatusPublishDue(1000, 1000, 10000));
    TEST_ASSERT_FALSE(isStatusPublishDue(10999, 1000, 10000));
    TEST_ASSERT_TRUE(isStatusPublishDue(11000, 1000, 10000));
    TEST_ASSERT_TRUE(isStatusPublishDue(500000, 1000, 10000));  // long gap, e.g. Wi-Fi was down
}

void test_publish_not_due_when_last_is_later_than_now() {
    // `last` taken with millis() after the loop's `now` timestamp.
    TEST_ASSERT_FALSE(isStatusPublishDue(20000, 20001, 10000));
    TEST_ASSERT_FALSE(isStatusPublishDue(20000, 20250, 10000));
    TEST_ASSERT_FALSE(isStatusPublishDue(0, 5, 10000));
}

void test_publish_due_across_millis_wrap() {
    const uint32_t last = 0xFFFFF000u;  // 4096 ms before the wrap
    TEST_ASSERT_FALSE(isStatusPublishDue(last + 9999u, last, 10000));  // wraps past 0
    TEST_ASSERT_TRUE(isStatusPublishDue(last + 10000u, last, 10000));
    TEST_ASSERT_TRUE(isStatusPublishDue(last + 10001u, last, 10000));
    // `last` just after the wrap, `now` just before it: not due.
    TEST_ASSERT_FALSE(isStatusPublishDue(0xFFFFFFFFu, 2u, 10000));
}

void runStatusPayloadTests() {
    UNITY_BEGIN();
    RUN_TEST(test_sample_payload_exact_output);
    RUN_TEST(test_rssi_omitted_when_not_available);
    RUN_TEST(test_extreme_numbers_fit_in_256_bytes);
    RUN_TEST(test_buffer_too_small_fails_without_partial_json);
    RUN_TEST(test_overlong_build_fails);
    RUN_TEST(test_build_string_is_escaped);
    RUN_TEST(test_escape_rejects_small_buffer);
    RUN_TEST(test_wifi_mapping_from_network_state);
    RUN_TEST(test_prov_mapping_from_network_state);
    RUN_TEST(test_publish_due_after_interval);
    RUN_TEST(test_publish_not_due_when_last_is_later_than_now);
    RUN_TEST(test_publish_due_across_millis_wrap);
    UNITY_END();
}

#ifdef ARDUINO
#include <Arduino.h>

void setup() {
    delay(2000);
    runStatusPayloadTests();
}

void loop() {
}
#else
int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    runStatusPayloadTests();
    return 0;
}
#endif
