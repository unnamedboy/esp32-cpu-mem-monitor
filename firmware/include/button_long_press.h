#ifndef BUTTON_LONG_PRESS_H
#define BUTTON_LONG_PRESS_H

// Pure, non-blocking debounce + long-press detector. No Arduino dependencies
// so it can be unit-tested on the host. Time values are millis()-style
// uint32_t and wrap-safe.

#include <stdint.h>

enum class ButtonEvent {
    None,
    Pressed,     // debounced press started (detector armed)
    Released,    // debounced release before the long-press threshold
    LongPress,   // held continuously for >= holdMs; reported once per press
};

class LongPressDetector {
public:
    LongPressDetector(uint32_t debounceMs, uint32_t holdMs)
        : debounceMs_(debounceMs), holdMs_(holdMs) {}

    // `pressed` is the raw (undebounced) logical button state.
    // The detector only arms after it has seen a stable release, so a button
    // already held when sampling starts never triggers a long press.
    ButtonEvent update(bool pressed, uint32_t nowMs) {
        if (!initialised_) {
            initialised_ = true;
            rawState_ = pressed;
            stableState_ = pressed;
            rawChangedAtMs_ = nowMs;
            armed_ = !pressed;
            return ButtonEvent::None;
        }

        if (pressed != rawState_) {
            rawState_ = pressed;
            rawChangedAtMs_ = nowMs;
        }

        if (rawState_ != stableState_ && (uint32_t)(nowMs - rawChangedAtMs_) >= debounceMs_) {
            stableState_ = rawState_;
            if (stableState_) {
                if (!armed_) {
                    return ButtonEvent::None;
                }
                pressedAtMs_ = nowMs;
                longPressReported_ = false;
                return ButtonEvent::Pressed;
            }

            const bool wasArmedPress = armed_ && !longPressReported_;
            armed_ = true;
            return wasArmedPress ? ButtonEvent::Released : ButtonEvent::None;
        }

        if (stableState_ && armed_ && !longPressReported_ && (uint32_t)(nowMs - pressedAtMs_) >= holdMs_) {
            longPressReported_ = true;
            return ButtonEvent::LongPress;
        }

        return ButtonEvent::None;
    }

    bool isHeld() const {
        return initialised_ && stableState_ && armed_;
    }

    uint32_t heldForMs(uint32_t nowMs) const {
        return isHeld() ? (uint32_t)(nowMs - pressedAtMs_) : 0;
    }

private:
    uint32_t debounceMs_;
    uint32_t holdMs_;
    bool initialised_ = false;
    bool rawState_ = false;
    bool stableState_ = false;
    bool armed_ = false;
    bool longPressReported_ = false;
    uint32_t rawChangedAtMs_ = 0;
    uint32_t pressedAtMs_ = 0;
};

#endif
