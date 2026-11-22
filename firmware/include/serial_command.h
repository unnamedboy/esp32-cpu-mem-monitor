#ifndef SERIAL_COMMAND_H
#define SERIAL_COMMAND_H

// Pure helpers for the USB serial PoP query (`RESMON?POP`). No Arduino
// dependencies so they can be unit-tested on the host. See docs/provisioning.md.

#include <stddef.h>
#include <stdio.h>
#include <string.h>

constexpr size_t SERIAL_COMMAND_MAX_CHARS = 32;
constexpr char SERIAL_COMMAND_POP_QUERY[] = "RESMON?POP";

// Bounded, non-blocking line assembler. Feed one byte at a time.
// - `\n` terminates a line; a `\r` immediately before it (CRLF) is dropped.
// - A line longer than SERIAL_COMMAND_MAX_CHARS is discarded entirely,
//   including everything up to and including its terminating `\n`.
// - Empty lines are ignored.
class SerialLineBuffer {
public:
    // Returns true when a complete line is ready; read it with line().
    // The line stays valid until the next feed() call.
    bool feed(char c) {
        if (lineReady_) {
            lineReady_ = false;
            length_ = 0;
        }

        if (c == '\n') {
            const bool discard = overflowed_;
            overflowed_ = false;
            if (!discard && length_ > 0 && buffer_[length_ - 1] == '\r') {
                --length_;
            }
            if (discard || length_ == 0 || length_ > SERIAL_COMMAND_MAX_CHARS) {
                length_ = 0;
                buffer_[0] = '\0';
                return false;
            }
            buffer_[length_] = '\0';
            lineReady_ = true;
            return true;
        }

        if (overflowed_) {
            return false;
        }

        // One slot beyond the limit is kept for the '\r' of a CRLF line;
        // anything longer is an overflow and the whole line is discarded.
        if (length_ >= kStoredChars) {
            overflowed_ = true;
            length_ = 0;
            return false;
        }

        buffer_[length_++] = c;
        return false;
    }

    const char* line() const {
        return buffer_;
    }

    void reset() {
        length_ = 0;
        overflowed_ = false;
        lineReady_ = false;
        buffer_[0] = '\0';
    }

private:
    static constexpr size_t kStoredChars = SERIAL_COMMAND_MAX_CHARS + 1;
    char buffer_[kStoredChars + 1] = {0};
    size_t length_ = 0;
    bool overflowed_ = false;
    bool lineReady_ = false;
};

enum class SerialCommand {
    Unknown,
    PopQuery,
};

inline SerialCommand parseSerialCommand(const char* line) {
    if (line != nullptr && strcmp(line, SERIAL_COMMAND_POP_QUERY) == 0) {
        return SerialCommand::PopQuery;
    }
    return SerialCommand::Unknown;
}

// `RESMON-POP name=<name> pop=<pop>`; returns false on truncation/bad input.
inline bool formatPopResponse(char* out, size_t outSize, const char* name, const char* pop) {
    if (out == nullptr || outSize == 0 || name == nullptr || pop == nullptr) {
        return false;
    }
    const int written = snprintf(out, outSize, "RESMON-POP name=%s pop=%s", name, pop);
    return written > 0 && (size_t)written < outSize;
}

// `RESMON-POP unavailable state=<state>`; returns false on truncation/bad input.
inline bool formatPopUnavailable(char* out, size_t outSize, const char* state) {
    if (out == nullptr || outSize == 0 || state == nullptr) {
        return false;
    }
    const int written = snprintf(out, outSize, "RESMON-POP unavailable state=%s", state);
    return written > 0 && (size_t)written < outSize;
}

#endif
