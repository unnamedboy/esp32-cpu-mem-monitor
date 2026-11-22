#include <unity.h>
#include "pop_code.h"

static uint32_t sequenceNext = 0;

static uint32_t sequentialWord() {
    return sequenceNext++;
}

static uint32_t highBitsOnlyWord() {
    // Low 5 bits are all ones; the high bits must be ignored.
    return 0xFFFFFFE0u | 0x1Fu;
}

void test_alphabet_excludes_ambiguous_characters() {
    TEST_ASSERT_EQUAL_UINT32(32, POP_ALPHABET_SIZE);
    TEST_ASSERT_NULL(strchr(POP_ALPHABET, '0'));
    TEST_ASSERT_NULL(strchr(POP_ALPHABET, 'O'));
    TEST_ASSERT_NULL(strchr(POP_ALPHABET, '1'));
    TEST_ASSERT_NULL(strchr(POP_ALPHABET, 'I'));
}

void test_generate_pop_uses_low_five_bits_and_terminates() {
    char pop[POP_BUFFER_SIZE];
    sequenceNext = 0;
    generatePop(pop, sequentialWord);
    TEST_ASSERT_EQUAL_STRING("23456789", pop);

    sequenceNext = 30;
    generatePop(pop, sequentialWord);
    // 30,31 then wraps to 0..5 via the 5-bit mask.
    TEST_ASSERT_EQUAL_STRING("YZ234567", pop);

    generatePop(pop, highBitsOnlyWord);
    TEST_ASSERT_EQUAL_STRING("ZZZZZZZZ", pop);
    TEST_ASSERT_TRUE(isValidPop(pop));
}

void test_is_valid_pop_accepts_only_eight_alphabet_characters() {
    TEST_ASSERT_TRUE(isValidPop("ABCDEFGH"));
    TEST_ASSERT_TRUE(isValidPop("23456789"));
    TEST_ASSERT_FALSE(isValidPop(nullptr));
    TEST_ASSERT_FALSE(isValidPop(""));
    TEST_ASSERT_FALSE(isValidPop("ABCDEFG"));
    TEST_ASSERT_FALSE(isValidPop("ABCDEFGHJ"));
    TEST_ASSERT_FALSE(isValidPop("abcdefgh"));
    TEST_ASSERT_FALSE(isValidPop("ABCDEFG0"));
    TEST_ASSERT_FALSE(isValidPop("ABCDEFGO"));
    TEST_ASSERT_FALSE(isValidPop("ABCDEFG1"));
    TEST_ASSERT_FALSE(isValidPop("ABCDEFGI"));
    TEST_ASSERT_FALSE(isValidPop("ABC EFGH"));
}

void test_service_name_uses_last_three_mac_bytes_uppercase() {
    const uint8_t mac[6] = {0x24, 0x6f, 0x28, 0xa1, 0x0b, 0xfe};
    char name[PROV_SERVICE_NAME_BUFFER_SIZE];
    TEST_ASSERT_TRUE(formatProvisioningServiceName(name, sizeof(name), mac));
    TEST_ASSERT_EQUAL_STRING("PROV_RESMON_A10BFE", name);
    TEST_ASSERT_EQUAL_UINT32(18, strlen(name));
}

void test_service_name_rejects_small_buffer_and_null_mac() {
    const uint8_t mac[6] = {0, 0, 0, 1, 2, 3};
    char small[PROV_SERVICE_NAME_BUFFER_SIZE - 1];
    TEST_ASSERT_FALSE(formatProvisioningServiceName(small, sizeof(small), mac));
    TEST_ASSERT_EQUAL_STRING("", small);

    char name[PROV_SERVICE_NAME_BUFFER_SIZE];
    TEST_ASSERT_FALSE(formatProvisioningServiceName(name, sizeof(name), nullptr));
}

void runPopCodeTests() {
    UNITY_BEGIN();
    RUN_TEST(test_alphabet_excludes_ambiguous_characters);
    RUN_TEST(test_generate_pop_uses_low_five_bits_and_terminates);
    RUN_TEST(test_is_valid_pop_accepts_only_eight_alphabet_characters);
    RUN_TEST(test_service_name_uses_last_three_mac_bytes_uppercase);
    RUN_TEST(test_service_name_rejects_small_buffer_and_null_mac);
    UNITY_END();
}

#ifdef ARDUINO
#include <Arduino.h>

void setup() {
    delay(2000);
    runPopCodeTests();
}

void loop() {
}
#else
int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    runPopCodeTests();
    return 0;
}
#endif
