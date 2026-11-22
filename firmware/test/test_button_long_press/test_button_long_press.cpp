#include <unity.h>
#include "button_long_press.h"

static const uint32_t DEBOUNCE_MS = 50;
static const uint32_t MIN_HOLD_MS = 5000;
static const uint32_t MAX_HOLD_MS = 15000;

static int ev(ButtonEvent e) {
    return (int)e;
}

// Samples `pressed` every 10 ms from startMs to endMs (inclusive) and returns
// the first non-None event, recording when it happened.
static ButtonEvent drive(HoldReleaseDetector& d, bool pressed, uint32_t startMs, uint32_t endMs,
                         uint32_t* eventAtMs = nullptr) {
    for (uint32_t t = startMs;; t += 10) {
        ButtonEvent e = d.update(pressed, t);
        if (e != ButtonEvent::None) {
            if (eventAtMs != nullptr) {
                *eventAtMs = t;
            }
            return e;
        }
        if (t >= endMs) {
            break;
        }
    }
    return ButtonEvent::None;
}

// Press at raw edge `pressEdge`, release at raw edge `releaseEdge`, sampling
// exactly at the edges and then once the debounce has elapsed. Returns the
// event reported for the release.
static ButtonEvent pressFor(HoldReleaseDetector& d, uint32_t pressEdge, uint32_t releaseEdge) {
    d.update(true, pressEdge);
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Pressed), ev(d.update(true, pressEdge + DEBOUNCE_MS)));
    d.update(false, releaseEdge);
    return d.update(false, releaseEdge + DEBOUNCE_MS);
}

static HoldReleaseDetector armedDetector(uint32_t at) {
    HoldReleaseDetector d(DEBOUNCE_MS, MIN_HOLD_MS, MAX_HOLD_MS);
    d.update(false, at);
    return d;
}

void test_short_press_reports_released_too_short() {
    HoldReleaseDetector d(DEBOUNCE_MS, MIN_HOLD_MS, MAX_HOLD_MS);
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(drive(d, false, 0, 100)));
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Pressed), ev(drive(d, true, 110, 300)));
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(drive(d, true, 310, 1000)));
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::ReleasedTooShort), ev(drive(d, false, 1010, 1200)));
}

void test_hold_in_window_triggers_only_on_release() {
    HoldReleaseDetector d(DEBOUNCE_MS, MIN_HOLD_MS, MAX_HOLD_MS);
    drive(d, false, 0, 100);
    uint32_t pressedAt = 0;
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Pressed), ev(drive(d, true, 110, 300, &pressedAt)));
    const uint32_t pressEdge = 110;

    uint32_t thresholdAt = 0;
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::HoldThresholdReached),
                          ev(drive(d, true, pressedAt + 10, pressEdge + 6000, &thresholdAt)));
    TEST_ASSERT_EQUAL_UINT32(pressEdge + MIN_HOLD_MS, thresholdAt);

    // Still held for a while: no further events, nothing fires while held.
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(drive(d, true, thresholdAt + 10, pressEdge + 9000)));
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Triggered), ev(drive(d, false, pressEdge + 9010, pressEdge + 9200)));
}

void test_window_edges_are_inclusive() {
    {
        HoldReleaseDetector d = armedDetector(0);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::ReleasedTooShort), ev(pressFor(d, 100, 100 + MIN_HOLD_MS - 1)));
    }
    {
        HoldReleaseDetector d = armedDetector(0);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Triggered), ev(pressFor(d, 100, 100 + MIN_HOLD_MS)));
    }
    {
        HoldReleaseDetector d = armedDetector(0);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Triggered), ev(pressFor(d, 100, 100 + MAX_HOLD_MS)));
    }
    {
        HoldReleaseDetector d = armedDetector(0);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::ReleasedTooLong), ev(pressFor(d, 100, 100 + MAX_HOLD_MS + 1)));
    }
}

void test_hold_beyond_window_is_reported_once_and_ignored() {
    HoldReleaseDetector d(DEBOUNCE_MS, MIN_HOLD_MS, MAX_HOLD_MS);
    drive(d, false, 0, 100);
    drive(d, true, 110, 300);
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::HoldThresholdReached), ev(drive(d, true, 310, 6000)));
    uint32_t tooLongAt = 0;
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::HeldTooLong), ev(drive(d, true, 6010, 20000, &tooLongAt)));
    TEST_ASSERT_TRUE(tooLongAt > 110 + MAX_HOLD_MS);
    // Held "forever" (e.g. DTR asserted): no repeats.
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(drive(d, true, tooLongAt + 10, tooLongAt + 120000)));
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::ReleasedTooLong),
                          ev(drive(d, false, tooLongAt + 120010, tooLongAt + 120300)));
}

void test_bounce_shorter_than_debounce_is_ignored() {
    HoldReleaseDetector d(DEBOUNCE_MS, MIN_HOLD_MS, MAX_HOLD_MS);
    drive(d, false, 0, 100);
    for (uint32_t t = 110; t < 1000; t += 20) {
        // Toggle every 20 ms: never stable for 50 ms.
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(d.update(((t / 20) % 2) == 0, t)));
    }
    TEST_ASSERT_FALSE(d.isHeld());
}

void test_button_held_at_start_never_triggers_until_released() {
    HoldReleaseDetector d(DEBOUNCE_MS, MIN_HOLD_MS, MAX_HOLD_MS);
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(drive(d, true, 0, 10000)));
    TEST_ASSERT_FALSE(d.isHeld());
    // Releasing an unarmed hold must not trigger, even inside the window.
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(drive(d, false, 10010, 10200)));
    TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Pressed), ev(drive(d, true, 10210, 10400)));
}

void test_window_is_wrap_safe() {
    const uint32_t start = 0xFFFFFFFFu - 2000u;
    {
        HoldReleaseDetector d = armedDetector(start);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Triggered), ev(pressFor(d, start + 10, start + 10 + MIN_HOLD_MS)));
    }
    {
        HoldReleaseDetector d = armedDetector(start);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::ReleasedTooShort),
                              ev(pressFor(d, start + 10, start + 10 + MIN_HOLD_MS - 1)));
    }
    {
        HoldReleaseDetector d = armedDetector(start);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::ReleasedTooLong),
                              ev(pressFor(d, start + 10, start + 10 + MAX_HOLD_MS + 1)));
    }
    {
        HoldReleaseDetector d = armedDetector(start);
        d.update(true, start + 10);
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::Pressed), ev(d.update(true, start + 10 + DEBOUNCE_MS)));
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(d.update(true, start + 10 + MIN_HOLD_MS - 1)));
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::HoldThresholdReached), ev(d.update(true, start + 10 + MIN_HOLD_MS)));
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::None), ev(d.update(true, start + 10 + MAX_HOLD_MS)));
        TEST_ASSERT_EQUAL_INT(ev(ButtonEvent::HeldTooLong), ev(d.update(true, start + 10 + MAX_HOLD_MS + 1)));
    }
}

void runButtonLongPressTests() {
    UNITY_BEGIN();
    RUN_TEST(test_short_press_reports_released_too_short);
    RUN_TEST(test_hold_in_window_triggers_only_on_release);
    RUN_TEST(test_window_edges_are_inclusive);
    RUN_TEST(test_hold_beyond_window_is_reported_once_and_ignored);
    RUN_TEST(test_bounce_shorter_than_debounce_is_ignored);
    RUN_TEST(test_button_held_at_start_never_triggers_until_released);
    RUN_TEST(test_window_is_wrap_safe);
    UNITY_END();
}

#ifdef ARDUINO
#include <Arduino.h>

void setup() {
    delay(2000);
    runButtonLongPressTests();
}

void loop() {
}
#else
int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    runButtonLongPressTests();
    return 0;
}
#endif
