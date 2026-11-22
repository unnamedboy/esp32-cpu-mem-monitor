#include <unity.h>
#include "button_long_press.h"

static const uint32_t DEBOUNCE_MS = 50;
static const uint32_t HOLD_MS = 5000;

// Holds `pressed` from `startMs` to `endMs` (inclusive) in 10 ms steps and
// returns the first non-None event seen, recording when it happened.
static ButtonEvent drive(LongPressDetector& d, bool pressed, uint32_t startMs, uint32_t endMs, uint32_t* eventAtMs = nullptr) {
    for (uint32_t t = startMs; ; t += 10) {
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

void test_short_press_reports_pressed_then_released_only() {
    LongPressDetector d(DEBOUNCE_MS, HOLD_MS);
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, false, 0, 100));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Pressed, (int)drive(d, true, 110, 300));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, true, 310, 1000));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Released, (int)drive(d, false, 1010, 1200));
}

void test_long_press_fires_once_after_hold_threshold() {
    LongPressDetector d(DEBOUNCE_MS, HOLD_MS);
    drive(d, false, 0, 100);
    uint32_t pressedAt = 0;
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Pressed, (int)drive(d, true, 110, 300, &pressedAt));

    uint32_t longAt = 0;
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::LongPress, (int)drive(d, true, pressedAt + 10, pressedAt + 6000, &longAt));
    TEST_ASSERT_EQUAL_UINT32(pressedAt + HOLD_MS, longAt);

    // Still held: no repeat. Release after long press: no Released event.
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, true, longAt + 10, longAt + 10000));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, false, longAt + 10010, longAt + 10500));
}

void test_bounce_shorter_than_debounce_is_ignored() {
    LongPressDetector d(DEBOUNCE_MS, HOLD_MS);
    drive(d, false, 0, 100);
    for (uint32_t t = 110; t < 1000; t += 20) {
        // Toggle every 20 ms: never stable for 50 ms.
        TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)d.update(((t / 20) % 2) == 0, t));
    }
    TEST_ASSERT_FALSE(d.isHeld());
}

void test_release_glitch_restarts_hold_timer() {
    LongPressDetector d(DEBOUNCE_MS, HOLD_MS);
    drive(d, false, 0, 100);
    uint32_t pressedAt = 0;
    drive(d, true, 110, 300, &pressedAt);
    // Held for 4 s, then a debounced release, then pressed again.
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, true, pressedAt + 10, pressedAt + 4000));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Released, (int)drive(d, false, pressedAt + 4010, pressedAt + 4200));
    uint32_t secondPressAt = 0;
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Pressed, (int)drive(d, true, pressedAt + 4210, pressedAt + 4400, &secondPressAt));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, true, secondPressAt + 10, secondPressAt + HOLD_MS - 10));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::LongPress, (int)drive(d, true, secondPressAt + HOLD_MS, secondPressAt + HOLD_MS + 100));
}

void test_button_held_at_start_never_triggers_until_released() {
    LongPressDetector d(DEBOUNCE_MS, HOLD_MS);
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, true, 0, 20000));
    TEST_ASSERT_FALSE(d.isHeld());
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)drive(d, false, 20010, 20200));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Pressed, (int)drive(d, true, 20210, 20400));
}

void test_hold_timer_is_wrap_safe() {
    LongPressDetector d(DEBOUNCE_MS, HOLD_MS);
    const uint32_t start = 0xFFFFFFFFu - 2000u;
    d.update(false, start);
    d.update(true, start + 10);
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::Pressed, (int)d.update(true, start + 10 + DEBOUNCE_MS));
    const uint32_t pressedAt = start + 10 + DEBOUNCE_MS;
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::None, (int)d.update(true, pressedAt + HOLD_MS - 1));
    TEST_ASSERT_EQUAL_INT((int)ButtonEvent::LongPress, (int)d.update(true, pressedAt + HOLD_MS));
}

void runButtonLongPressTests() {
    UNITY_BEGIN();
    RUN_TEST(test_short_press_reports_pressed_then_released_only);
    RUN_TEST(test_long_press_fires_once_after_hold_threshold);
    RUN_TEST(test_bounce_shorter_than_debounce_is_ignored);
    RUN_TEST(test_release_glitch_restarts_hold_timer);
    RUN_TEST(test_button_held_at_start_never_triggers_until_released);
    RUN_TEST(test_hold_timer_is_wrap_safe);
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
