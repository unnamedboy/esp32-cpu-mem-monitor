#include <unity.h>
#include "metric_conversion.h"

void test_percent_to_dac_value_maps_bounds_and_midpoint() {
    TEST_ASSERT_EQUAL_INT(0, percentToDacValue(0.0f));
    TEST_ASSERT_EQUAL_INT(128, percentToDacValue(50.0f));
    TEST_ASSERT_EQUAL_INT(255, percentToDacValue(100.0f));
}

void test_percent_to_dac_value_clamps_out_of_range_values() {
    TEST_ASSERT_EQUAL_INT(0, percentToDacValue(-1.0f));
    TEST_ASSERT_EQUAL_INT(255, percentToDacValue(101.0f));
}

void test_parse_percent_payload_accepts_valid_percent_values() {
    float percent = -1.0f;

    TEST_ASSERT_TRUE(parsePercentPayload("0", percent));
    TEST_ASSERT_FLOAT_WITHIN(0.001f, 0.0f, percent);

    TEST_ASSERT_TRUE(parsePercentPayload(" 50.5 ", percent));
    TEST_ASSERT_FLOAT_WITHIN(0.001f, 50.5f, percent);

    TEST_ASSERT_TRUE(parsePercentPayload("100", percent));
    TEST_ASSERT_FLOAT_WITHIN(0.001f, 100.0f, percent);
}

void test_parse_percent_payload_rejects_invalid_values() {
    float percent = 42.0f;

    TEST_ASSERT_FALSE(parsePercentPayload(nullptr, percent));
    TEST_ASSERT_FALSE(parsePercentPayload("", percent));
    TEST_ASSERT_FALSE(parsePercentPayload("cpu=50", percent));
    TEST_ASSERT_FALSE(parsePercentPayload("50%", percent));
    TEST_ASSERT_FALSE(parsePercentPayload("-0.1", percent));
    TEST_ASSERT_FALSE(parsePercentPayload("100.1", percent));
    TEST_ASSERT_FALSE(parsePercentPayload("nan", percent));
}

void runMetricConversionTests() {
    UNITY_BEGIN();
    RUN_TEST(test_percent_to_dac_value_maps_bounds_and_midpoint);
    RUN_TEST(test_percent_to_dac_value_clamps_out_of_range_values);
    RUN_TEST(test_parse_percent_payload_accepts_valid_percent_values);
    RUN_TEST(test_parse_percent_payload_rejects_invalid_values);
    UNITY_END();
}

#ifdef ARDUINO
#include <Arduino.h>

void setup() {
    delay(2000);
    runMetricConversionTests();
}

void loop() {
}
#else
int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    runMetricConversionTests();
    return 0;
}
#endif
