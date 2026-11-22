#ifndef BUTTON_LONG_PRESS_H
#define BUTTON_LONG_PRESS_H

// Pure, non-blocking debounce + "hold for a window, then release" detector.
// No Arduino dependencies so it can be unit-tested on the host. Time values
// are millis()-style uint32_t and wrap-safe.
//
// The action fires on RELEASE, and only when the button was held for
// minHoldMs..maxHoldMs (inclusive). Longer holds are ignored, so a pin that
// stays low (stuck button, or a serial tool holding DTR asserted, which pulls
// GPIO0 low through the dev board's auto-program circuit) can never trigger
// the action.

#include <stdint.h>

enum class ButtonEvent {
    None,
    Pressed,              // debounced press started
    HoldThresholdReached, // held for minHoldMs: releasing now triggers
    HeldTooLong,          // held beyond maxHoldMs: this press will be ignored
    ReleasedTooShort,     // released before minHoldMs: nothing happens
    ReleasedTooLong,      // released after maxHoldMs: nothing happens
    Triggered,            // released within the window: perform the action
};

class HoldReleaseDetector {
public:
    HoldReleaseDetector(uint32_t debounceMs, uint32_t minHoldMs, uint32_t maxHoldMs)
        : debounceMs_(debounceMs), minHoldMs_(minHoldMs), maxHoldMs_(maxHoldMs) {}

    // `pressed` is the raw (undebounced) logical button state. The detector
    // only arms after it has seen a stable release, so a button already held
    // when sampling starts is ignored until it is released.
    //
    // Hold duration is measured between the raw press and release edges, so
    // the debounce delay does not shift the window.
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
                pressedAtMs_ = rawChangedAtMs_;
                thresholdReported_ = false;
                tooLongReported_ = false;
                return ButtonEvent::Pressed;
            }

            // Debounced release.
            const bool wasArmed = armed_;
            armed_ = true;
            if (!wasArmed) {
                return ButtonEvent::None;
            }
            const uint32_t heldMs = (uint32_t)(rawChangedAtMs_ - pressedAtMs_);
            if (heldMs < minHoldMs_) {
                return ButtonEvent::ReleasedTooShort;
            }
            if (heldMs > maxHoldMs_) {
                return ButtonEvent::ReleasedTooLong;
            }
            return ButtonEvent::Triggered;
        }

        if (stableState_ && rawState_ && armed_) {
            const uint32_t heldMs = (uint32_t)(nowMs - pressedAtMs_);
            if (!tooLongReported_ && heldMs > maxHoldMs_) {
                tooLongReported_ = true;
                thresholdReported_ = true;
                return ButtonEvent::HeldTooLong;
            }
            if (!thresholdReported_ && heldMs >= minHoldMs_) {
                thresholdReported_ = true;
                return ButtonEvent::HoldThresholdReached;
            }
        }

        return ButtonEvent::None;
    }

    bool isHeld() const {
        return initialised_ && stableState_ && armed_;
    }

private:
    uint32_t debounceMs_;
    uint32_t minHoldMs_;
    uint32_t maxHoldMs_;
    bool initialised_ = false;
    bool rawState_ = false;
    bool stableState_ = false;
    bool armed_ = false;
    bool thresholdReported_ = false;
    bool tooLongReported_ = false;
    uint32_t rawChangedAtMs_ = 0;
    uint32_t pressedAtMs_ = 0;
};

#endif
