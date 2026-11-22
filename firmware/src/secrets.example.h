#ifndef SECRETS_EXAMPLE_H
#define SECRETS_EXAMPLE_H

// Optional development fallback. Copy to secrets.h (ignored by git).
// Used only when no BLE-provisioned Wi-Fi credentials are stored and no
// provisioning was requested; see docs/provisioning.md. Leave WIFI_SSID empty
// (or delete secrets.h) to always use BLE provisioning.
const char* WIFI_SSID = "your-wifi-ssid";
const char* WIFI_PASSWORD = "your-wifi-password";

#endif
