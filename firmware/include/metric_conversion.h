#ifndef METRIC_CONVERSION_H
#define METRIC_CONVERSION_H

#include <ctype.h>
#include <math.h>
#include <stdlib.h>

inline int percentToDacValue(float percent) {
    if (percent < 0.0f) {
        percent = 0.0f;
    } else if (percent > 100.0f) {
        percent = 100.0f;
    }

    return (int)round((percent / 100.0f) * 255.0f);
}

inline bool parsePercentPayload(const char* payload, float& percent) {
    if (payload == nullptr) {
        return false;
    }

    char* end = nullptr;
    const float parsed = strtof(payload, &end);
    if (end == payload || !isfinite(parsed)) {
        return false;
    }

    while (isspace((unsigned char)*end)) {
        ++end;
    }

    if (*end != '\0' || parsed < 0.0f || parsed > 100.0f) {
        return false;
    }

    percent = parsed;
    return true;
}

#endif
