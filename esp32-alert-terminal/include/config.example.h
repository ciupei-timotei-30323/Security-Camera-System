// Copy to include/config.h and fill in. include/config.h is gitignored.
//
// The same file works for both the development broker (plain 1883) and the production broker
// (TLS 8883): set MQTT_USE_TLS accordingly and point MQTT_HOST at the tunnel/reverse-proxy name.

#pragma once

// --- Wi-Fi -------------------------------------------------------------------------------------
#define WIFI_SSID "your-ssid"
#define WIFI_PASSWORD "your-wifi-password"

// --- Tenant ------------------------------------------------------------------------------------
// Must match the camera's SystemId; the topic is systems/<SYSTEM_ID>/alarm.
#define SYSTEM_ID "sys-001"

// --- MQTT broker -------------------------------------------------------------------------------
// Development: the compose.yaml mosquitto service. It DOES publish 1883 to the host, so a LAN IP
// works here (or a hostname if you have one).
// Production:  expose mosquitto behind a TLS tunnel/proxy and use port 8883.
#define MQTT_HOST "192.168.1.10"
#define MQTT_PORT 1883
#define MQTT_USE_TLS false

// Read-only client credentials seeded by mosquitto/entrypoint.sh (MQTT_CLIENT_USER/PASS).
// The ACL allows read access to systems/+/alarm and systems/+/events only.
#define MQTT_USERNAME "alert-terminal"
#define MQTT_PASSWORD "change-me"

// Stable client id so a reconnecting terminal replaces its own session instead of colliding.
#define MQTT_CLIENT_ID "alert-terminal-" SYSTEM_ID

// --- HTTP fallback -----------------------------------------------------------------------------
// GET /api/device/status when the broker is unreachable. The endpoint needs the terminal's API key
// in the X-Api-Key header; the backend resolves the tenant from that key.
//
// Unlike MQTT, the backend publishes NO host port in the compose stack, so point this at the
// Tailscale sidecar name when the terminal is on the tailnet. A LAN address only works when you run
// the backend yourself with `dotnet run` (see the root README).
#define FALLBACK_ENABLED true
#define FALLBACK_STATUS_URL "http://security-camera-backend:8080/api/device/status"
#define FALLBACK_API_KEY "change-me-to-the-alert-terminal-api-key"
#define FALLBACK_POLL_INTERVAL_MS 15000

// --- Hardware ----------------------------------------------------------------------------------
// 16x2 I2C LCD at 0x27 (the common PCF8574 backpack address).
#define LCD_I2C_ADDRESS 0x27
#define LCD_COLUMNS 16
#define LCD_ROWS 2

// Status LED: solid while armed.
#define PIN_STATUS_LED 25
// Alarm LED + piezo buzzer: active while an alarm window is open.
#define PIN_ALARM_LED 26
#define PIN_BUZZER 27

// Buzzer pulse pattern while alarming.
#define BUZZER_ON_MS 400
#define BUZZER_OFF_MS 600

// Set false for an active-low buzzer module, true for a passive piezo driven by a square wave.
#define BUZZER_PASSIVE false
