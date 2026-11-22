#ifndef POP_CODE_H
#define POP_CODE_H

// Pure helpers for the BLE provisioning Proof of Possession (pairing code)
// and the advertised provisioning service name. No Arduino dependencies so
// they can be unit-tested on the host. See PROVISIONING.md.

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

// 32 symbols, no 0/O/1/I. 32 is a power of two, so taking 5 random bits per
// character has no modulo bias.
constexpr char POP_ALPHABET[] = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
constexpr size_t POP_ALPHABET_SIZE = sizeof(POP_ALPHABET) - 1;
constexpr size_t POP_LENGTH = 8;
constexpr size_t POP_BUFFER_SIZE = POP_LENGTH + 1;

static_assert(POP_ALPHABET_SIZE == 32, "PoP alphabet must have exactly 32 symbols");

// Advertised BLE name: prefix + last 3 bytes of the Wi-Fi STA MAC as uppercase hex.
constexpr char PROV_SERVICE_NAME_PREFIX[] = "PROV_RESMON_";
constexpr size_t PROV_SERVICE_NAME_BUFFER_SIZE = sizeof(PROV_SERVICE_NAME_PREFIX) - 1 + 6 + 1;

inline bool isPopAlphabetChar(char c) {
    if (c == '\0') {
        return false;
    }
    return memchr(POP_ALPHABET, c, POP_ALPHABET_SIZE) != nullptr;
}

// True when `pop` is exactly POP_LENGTH characters, all from POP_ALPHABET.
inline bool isValidPop(const char* pop) {
    if (pop == nullptr) {
        return false;
    }
    for (size_t i = 0; i < POP_LENGTH; ++i) {
        if (!isPopAlphabetChar(pop[i])) {
            return false;
        }
    }
    return pop[POP_LENGTH] == '\0';
}

// Fills `out` with a new PoP. `randomWord` must return uniformly random
// 32-bit values (esp_random() on the device). One word is consumed per
// character; only its low 5 bits are used.
inline void generatePop(char (&out)[POP_BUFFER_SIZE], uint32_t (*randomWord)()) {
    for (size_t i = 0; i < POP_LENGTH; ++i) {
        out[i] = POP_ALPHABET[randomWord() & (POP_ALPHABET_SIZE - 1)];
    }
    out[POP_LENGTH] = '\0';
}

// Formats `PROV_RESMON_XXXXXX` from a 6-byte MAC. Returns false if `out` is
// too small or `mac` is null.
inline bool formatProvisioningServiceName(char* out, size_t outSize, const uint8_t* mac) {
    if (out == nullptr || mac == nullptr || outSize < PROV_SERVICE_NAME_BUFFER_SIZE) {
        if (out != nullptr && outSize > 0) {
            out[0] = '\0';
        }
        return false;
    }
    const int written = snprintf(out, outSize, "%s%02X%02X%02X", PROV_SERVICE_NAME_PREFIX, mac[3], mac[4], mac[5]);
    return written > 0 && (size_t)written < outSize;
}

#endif
