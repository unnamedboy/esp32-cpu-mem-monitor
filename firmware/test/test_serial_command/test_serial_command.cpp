#include <unity.h>
#include "serial_command.h"

// Feeds a whole string; returns the number of completed lines and copies the
// last completed line into `lastLine`.
static int feedString(SerialLineBuffer& buffer, const char* text, char* lastLine, size_t lastLineSize) {
    int lines = 0;
    for (const char* p = text; *p != '\0'; ++p) {
        if (buffer.feed(*p)) {
            ++lines;
            strncpy(lastLine, buffer.line(), lastLineSize - 1);
            lastLine[lastLineSize - 1] = '\0';
        }
    }
    return lines;
}

void test_line_buffer_accepts_lf_and_crlf() {
    SerialLineBuffer buffer;
    char line[64] = {0};

    TEST_ASSERT_EQUAL_INT(1, feedString(buffer, "RESMON?POP\n", line, sizeof(line)));
    TEST_ASSERT_EQUAL_STRING("RESMON?POP", line);

    TEST_ASSERT_EQUAL_INT(1, feedString(buffer, "RESMON?POP\r\n", line, sizeof(line)));
    TEST_ASSERT_EQUAL_STRING("RESMON?POP", line);
}

void test_line_buffer_ignores_empty_lines_and_waits_for_terminator() {
    SerialLineBuffer buffer;
    char line[64] = {0};

    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, "\n\r\n", line, sizeof(line)));
    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, "RESMON?P", line, sizeof(line)));
    TEST_ASSERT_EQUAL_INT(1, feedString(buffer, "OP\n", line, sizeof(line)));
    TEST_ASSERT_EQUAL_STRING("RESMON?POP", line);
}

void test_line_buffer_accepts_exactly_max_chars() {
    SerialLineBuffer buffer;
    char line[64] = {0};
    char input[SERIAL_COMMAND_MAX_CHARS + 3];

    memset(input, 'A', SERIAL_COMMAND_MAX_CHARS);
    input[SERIAL_COMMAND_MAX_CHARS] = '\r';
    input[SERIAL_COMMAND_MAX_CHARS + 1] = '\n';
    input[SERIAL_COMMAND_MAX_CHARS + 2] = '\0';

    TEST_ASSERT_EQUAL_INT(1, feedString(buffer, input, line, sizeof(line)));
    TEST_ASSERT_EQUAL_UINT32(SERIAL_COMMAND_MAX_CHARS, strlen(line));
}

void test_line_buffer_discards_overlong_line_then_recovers() {
    SerialLineBuffer buffer;
    char line[64] = {0};
    char input[SERIAL_COMMAND_MAX_CHARS + 2];

    memset(input, 'B', SERIAL_COMMAND_MAX_CHARS + 1);
    input[SERIAL_COMMAND_MAX_CHARS + 1] = '\0';

    // 33 characters then LF: discarded.
    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, input, line, sizeof(line)));
    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, "\n", line, sizeof(line)));

    // Much longer garbage ending in the command text: still discarded as one line.
    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, input, line, sizeof(line)));
    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, input, line, sizeof(line)));
    TEST_ASSERT_EQUAL_INT(0, feedString(buffer, "RESMON?POP\n", line, sizeof(line)));

    // The next line is processed normally.
    TEST_ASSERT_EQUAL_INT(1, feedString(buffer, "RESMON?POP\n", line, sizeof(line)));
    TEST_ASSERT_EQUAL_STRING("RESMON?POP", line);
}

void test_parse_serial_command_requires_exact_match() {
    TEST_ASSERT_EQUAL_INT((int)SerialCommand::PopQuery, (int)parseSerialCommand("RESMON?POP"));
    TEST_ASSERT_EQUAL_INT((int)SerialCommand::Unknown, (int)parseSerialCommand("resmon?pop"));
    TEST_ASSERT_EQUAL_INT((int)SerialCommand::Unknown, (int)parseSerialCommand("RESMON?POP "));
    TEST_ASSERT_EQUAL_INT((int)SerialCommand::Unknown, (int)parseSerialCommand("RESMON?"));
    TEST_ASSERT_EQUAL_INT((int)SerialCommand::Unknown, (int)parseSerialCommand(""));
    TEST_ASSERT_EQUAL_INT((int)SerialCommand::Unknown, (int)parseSerialCommand(nullptr));
}

void test_format_pop_response_matches_contract() {
    char out[96];
    TEST_ASSERT_TRUE(formatPopResponse(out, sizeof(out), "PROV_RESMON_A10BFE", "ABCDEFGH"));
    TEST_ASSERT_EQUAL_STRING("RESMON-POP name=PROV_RESMON_A10BFE pop=ABCDEFGH", out);

    TEST_ASSERT_TRUE(formatPopUnavailable(out, sizeof(out), "provisioned"));
    TEST_ASSERT_EQUAL_STRING("RESMON-POP unavailable state=provisioned", out);
}

void test_format_pop_response_reports_truncation() {
    char tiny[16];
    TEST_ASSERT_FALSE(formatPopResponse(tiny, sizeof(tiny), "PROV_RESMON_A10BFE", "ABCDEFGH"));
    TEST_ASSERT_FALSE(formatPopUnavailable(tiny, sizeof(tiny), "connecting"));
    TEST_ASSERT_FALSE(formatPopResponse(tiny, sizeof(tiny), nullptr, "ABCDEFGH"));
}

void runSerialCommandTests() {
    UNITY_BEGIN();
    RUN_TEST(test_line_buffer_accepts_lf_and_crlf);
    RUN_TEST(test_line_buffer_ignores_empty_lines_and_waits_for_terminator);
    RUN_TEST(test_line_buffer_accepts_exactly_max_chars);
    RUN_TEST(test_line_buffer_discards_overlong_line_then_recovers);
    RUN_TEST(test_parse_serial_command_requires_exact_match);
    RUN_TEST(test_format_pop_response_matches_contract);
    RUN_TEST(test_format_pop_response_reports_truncation);
    UNITY_END();
}

#ifdef ARDUINO
#include <Arduino.h>

void setup() {
    delay(2000);
    runSerialCommandTests();
}

void loop() {
}
#else
int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    runSerialCommandTests();
    return 0;
}
#endif
