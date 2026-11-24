// ESP32 Alert Terminal — MQTT alarm display for the Security Camera system.
//
// The backend publishes a retained JSON document to systems/<SYSTEM_ID>/alarm every time arming
// state changes or a suspect event opens an alarm window:
//
//   { "armed": true, "alarm": true, "alarmUntil": "2026-11-24T11:30:00.000Z",
//     "line1": "SUSPECT DETECTED", "line2": "                ",
//     "serverTime": "2026-11-24T11:29:45.000Z" }
//
// Because the message is retained, a terminal that boots (or reconnects) mid-alarm gets the
// current state immediately — no polling, no missed alarms.
//
// When the broker is unreachable the firmware falls back to GET /api/device/status on a timer so
// the LCD still shows something useful. MQTT is always preferred; the fallback only runs while
// the MQTT connection is down.

#include <Arduino.h>
#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <LiquidCrystal_I2C.h>
#include <PubSubClient.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <time.h>

#include "config.h"

namespace {

// ---------------------------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------------------------

constexpr uint32_t SERIAL_BAUD = 115200;
constexpr uint32_t WIFI_RETRY_MS = 5000;
constexpr uint32_t MQTT_MIN_BACKOFF_MS = 2000;
constexpr uint32_t MQTT_MAX_BACKOFF_MS = 60000;
constexpr uint32_t HTTP_TIMEOUT_MS = 4000;
constexpr uint32_t LCD_REFRESH_MS = 250;
constexpr size_t MQTT_BUFFER_SIZE = 1024;

// The alarm document is tiny; a 1 KiB JSON document is plenty and keeps the heap stable.
constexpr size_t JSON_CAPACITY = 1024;

// ---------------------------------------------------------------------------------------------
// Hardware
// ---------------------------------------------------------------------------------------------

LiquidCrystal_I2C lcd(LCD_I2C_ADDRESS, LCD_COLUMNS, LCD_ROWS);

#if MQTT_USE_TLS
WiFiClientSecure transport;
#else
WiFiClient transport;
#endif

PubSubClient mqtt(transport);

char alarmTopic[96];
char eventsTopic[96];

// ---------------------------------------------------------------------------------------------
// Terminal state
// ---------------------------------------------------------------------------------------------

struct AlarmState {
  bool armed = false;
  bool alarm = false;
  bool valid = false;             // false until the first payload arrives
  time_t alarmUntil = 0;          // epoch seconds; 0 = no expiry advertised
  char line1[LCD_COLUMNS + 1] = "                ";
  char line2[LCD_COLUMNS + 1] = "                ";
};

AlarmState state;

// Render bookkeeping so the LCD is only rewritten when something actually changed.
char renderedLine1[LCD_COLUMNS + 1] = "";
char renderedLine2[LCD_COLUMNS + 1] = "";
uint32_t lastRenderMs = 0;

// Connection bookkeeping.
uint32_t mqttBackoffMs = MQTT_MIN_BACKOFF_MS;
uint32_t nextMqttAttemptMs = 0;
uint32_t nextWifiAttemptMs = 0;
uint32_t nextFallbackPollMs = 0;
bool mqttWasConnected = false;

// Buzzer pattern bookkeeping.
uint32_t buzzerPhaseMs = 0;
bool buzzerOn = false;

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------

void copyPadded(char *destination, size_t size, const char *source) {
  // The backend already pads lines to 16 characters, but a hand-published payload may not. Always
  // normalise to exactly LCD_COLUMNS characters so stale characters never linger on the display.
  const size_t width = size - 1;
  size_t written = 0;
  if (source != nullptr) {
    while (written < width && source[written] != '\0') {
      destination[written] = source[written];
      ++written;
    }
  }
  while (written < width) {
    destination[written++] = ' ';
  }
  destination[width] = '\0';
}

bool equalsIgnoreCase(const char *a, const char *b) {
  if (a == nullptr || b == nullptr) {
    return false;
  }
  return strcasecmp(a, b) == 0;
}

// Parses "YYYY-MM-DDTHH:MM:SS(.sss)?Z" (or an offset suffix) into epoch seconds. Returns 0 when
// the string cannot be parsed. The backend always emits UTC with a trailing Z.
time_t parseIso8601Utc(const char *value) {
  if (value == nullptr) {
    return 0;
  }

  int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
  if (sscanf(value, "%4d-%2d-%2dT%2d:%2d:%2d", &year, &month, &day, &hour, &minute, &second) != 6) {
    return 0;
  }
  if (month < 1 || month > 12 || day < 1 || day > 31) {
    return 0;
  }

  // Days from civil (Howard Hinnant's algorithm) — avoids depending on the ESP32 RTC being set.
  const int adjustedYear = year - (month <= 2 ? 1 : 0);
  const int era = (adjustedYear >= 0 ? adjustedYear : adjustedYear - 399) / 400;
  const unsigned yearOfEra = static_cast<unsigned>(adjustedYear - era * 400);
  const unsigned dayOfYear = (153u * static_cast<unsigned>(month + (month > 2 ? -3 : 9)) + 2u) / 5u +
                             static_cast<unsigned>(day) - 1u;
  const unsigned dayOfEra = yearOfEra * 365u + yearOfEra / 4u - yearOfEra / 100u + dayOfYear;
  const long daysSinceEpoch = static_cast<long>(era) * 146097L + static_cast<long>(dayOfEra) - 719468L;

  return static_cast<time_t>(daysSinceEpoch) * 86400L + hour * 3600L + minute * 60L + second;
}

// ---------------------------------------------------------------------------------------------
// Display / actuators
// ---------------------------------------------------------------------------------------------

void renderDisplay(bool force) {
  if (!force && strcmp(state.line1, renderedLine1) == 0 && strcmp(state.line2, renderedLine2) == 0) {
    return;
  }

  lcd.setCursor(0, 0);
  lcd.print(state.line1);
  lcd.setCursor(0, 1);
  lcd.print(state.line2);

  strncpy(renderedLine1, state.line1, LCD_COLUMNS);
  renderedLine1[LCD_COLUMNS] = '\0';
  strncpy(renderedLine2, state.line2, LCD_COLUMNS);
  renderedLine2[LCD_COLUMNS] = '\0';
}

void driveIndicators() {
  digitalWrite(PIN_STATUS_LED, state.armed ? HIGH : LOW);
  digitalWrite(PIN_ALARM_LED, state.alarm ? HIGH : LOW);
}

void silenceBuzzer() {
  buzzerOn = false;
  digitalWrite(PIN_BUZZER, LOW);
}

void serviceBuzzer(uint32_t now) {
  if (!state.alarm) {
    if (buzzerOn) {
      silenceBuzzer();
    }
    return;
  }

  const uint32_t phase = buzzerOn ? BUZZER_ON_MS : BUZZER_OFF_MS;
  if (now - buzzerPhaseMs < phase) {
    return;
  }

  buzzerPhaseMs = now;
  buzzerOn = !buzzerOn;
  digitalWrite(PIN_BUZZER, buzzerOn ? HIGH : LOW);
}

// Clears a stale alarm when its window has passed and no "alarm": false arrived (for example
// because the terminal was offline for the whole window).
void expireAlarmIfDue() {
  if (!state.alarm || state.alarmUntil == 0) {
    return;
  }

  const time_t now = time(nullptr);
  if (now < 1000000000L) {
    // Clock is not set yet; the retained clear message will handle it.
    return;
  }

  if (now >= state.alarmUntil) {
    Serial.println("[alarm] window expired locally; clearing");
    state.alarm = false;
    copyPadded(state.line1, sizeof(state.line1), state.armed ? "ARMED" : "DISARMED");
    copyPadded(state.line2, sizeof(state.line2), "");
    driveIndicators();
    silenceBuzzer();
    renderDisplay(true);
  }
}

// ---------------------------------------------------------------------------------------------
// Alarm payload handling
// ---------------------------------------------------------------------------------------------

bool applyAlarmDocument(JsonDocument &document) {
  if (document["armed"].is<bool>()) {
    state.armed = document["armed"].as<bool>();
  }
  if (document["alarm"].is<bool>()) {
    state.alarm = document["alarm"].as<bool>();
  }

  const char *alarmUntil = document["alarmUntil"].is<const char *>()
                               ? document["alarmUntil"].as<const char *>()
                               : nullptr;
  state.alarmUntil = state.alarm ? parseIso8601Utc(alarmUntil) : 0;

  const char *line1 = document["line1"].is<const char *>() ? document["line1"].as<const char *>() : nullptr;
  const char *line2 = document["line2"].is<const char *>() ? document["line2"].as<const char *>() : nullptr;

  if (line1 != nullptr) {
    copyPadded(state.line1, sizeof(state.line1), line1);
  } else {
    copyPadded(state.line1, sizeof(state.line1), state.alarm ? "ALARM" : (state.armed ? "ARMED" : "DISARMED"));
  }

  if (line2 != nullptr) {
    copyPadded(state.line2, sizeof(state.line2), line2);
  } else {
    copyPadded(state.line2, sizeof(state.line2), "");
  }

  state.valid = true;
  driveIndicators();
  if (!state.alarm) {
    silenceBuzzer();
  }
  renderDisplay(true);
  return true;
}

void handleAlarmMessage(const uint8_t *payload, size_t length) {
  JsonDocument document;
  const DeserializationError error = deserializeJson(document, payload, length);
  if (error) {
    Serial.printf("[mqtt] ignored malformed alarm payload: %s\n", error.c_str());
    return;
  }

  if (applyAlarmDocument(document)) {
    Serial.printf("[mqtt] armed=%d alarm=%d line1=%s\n", state.armed, state.alarm, state.line1);
  }
}

void handleEventsMessage(const uint8_t *payload, size_t length) {
  // Events drive the dashboard's live table; the terminal only needs to know one happened so the
  // LCD does not keep showing a stale "no motion" line. A short summary is enough.
  JsonDocument document;
  if (deserializeJson(document, payload, length)) {
    return;
  }

  const bool isSuspect = document["isSuspect"].is<bool>() && document["isSuspect"].as<bool>();
  Serial.printf("[mqtt] event received (suspect=%d)\n", isSuspect);
}

void onMqttMessage(char *topic, uint8_t *payload, unsigned int length) {
  if (strcmp(topic, alarmTopic) == 0) {
    handleAlarmMessage(payload, length);
  } else if (strcmp(topic, eventsTopic) == 0) {
    handleEventsMessage(payload, length);
  }
}

// ---------------------------------------------------------------------------------------------
// HTTP fallback
// ---------------------------------------------------------------------------------------------

void pollStatusFallback() {
  if (!FALLBACK_ENABLED || WiFi.status() != WL_CONNECTED) {
    return;
  }

  HTTPClient http;
  http.setTimeout(HTTP_TIMEOUT_MS);
  http.setConnectTimeout(HTTP_TIMEOUT_MS);

  // The backend resolves the tenant from the API key, so no query string is needed.
  if (!http.begin(FALLBACK_STATUS_URL)) {
    return;
  }

  http.addHeader("X-Api-Key", FALLBACK_API_KEY);
  const int status = http.GET();
  if (status != 200) {
    Serial.printf("[http] fallback status=%d\n", status);
    http.end();
    return;
  }

  JsonDocument document;
  const DeserializationError error = deserializeJson(document, http.getString());
  http.end();
  if (error) {
    Serial.printf("[http] fallback payload malformed: %s\n", error.c_str());
    return;
  }

  if (applyAlarmDocument(document)) {
    Serial.printf("[http] fallback armed=%d alarm=%d\n", state.armed, state.alarm);
  }
}

// ---------------------------------------------------------------------------------------------
// Connectivity
// ---------------------------------------------------------------------------------------------

void ensureWifi(uint32_t now) {
  if (WiFi.status() == WL_CONNECTED) {
    return;
  }

  if (now < nextWifiAttemptMs) {
    return;
  }
  nextWifiAttemptMs = now + WIFI_RETRY_MS;

  Serial.printf("[wifi] connecting to %s\n", WIFI_SSID);
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
}

void ensureMqtt(uint32_t now) {
  if (WiFi.status() != WL_CONNECTED) {
    return;
  }

  if (mqtt.connected()) {
    mqttBackoffMs = MQTT_MIN_BACKOFF_MS;
    return;
  }

  if (now < nextMqttAttemptMs) {
    return;
  }

  Serial.printf("[mqtt] connecting to %s:%d (backoff %ums)\n", MQTT_HOST, MQTT_PORT, mqttBackoffMs);

  const bool connected = mqtt.connect(
      MQTT_CLIENT_ID, MQTT_USERNAME, MQTT_PASSWORD, alarmTopic, 1, true, "{\"alarm\":false}");

  if (connected) {
    Serial.println("[mqtt] connected");
    mqttBackoffMs = MQTT_MIN_BACKOFF_MS;
    mqtt.subscribe(alarmTopic, 1);
    mqtt.subscribe(eventsTopic, 0);
    nextFallbackPollMs = now + FALLBACK_POLL_INTERVAL_MS;
    return;
  }

  Serial.printf("[mqtt] connect failed (state=%d)\n", mqtt.state());
  nextMqttAttemptMs = now + mqttBackoffMs;
  mqttBackoffMs = min(mqttBackoffMs * 2, MQTT_MAX_BACKOFF_MS);
}

// ---------------------------------------------------------------------------------------------
// Setup / loop
// ---------------------------------------------------------------------------------------------

void setupTopics() {
  snprintf(alarmTopic, sizeof(alarmTopic), "systems/%s/alarm", SYSTEM_ID);
  snprintf(eventsTopic, sizeof(eventsTopic), "systems/%s/events", SYSTEM_ID);
}

void setupHardware() {
  pinMode(PIN_STATUS_LED, OUTPUT);
  pinMode(PIN_ALARM_LED, OUTPUT);
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_STATUS_LED, LOW);
  digitalWrite(PIN_ALARM_LED, LOW);
  digitalWrite(PIN_BUZZER, LOW);

  lcd.init();
  lcd.backlight();
  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("SECURITY CAMERA");
  lcd.setCursor(0, 1);
  lcd.print("CONNECTING...");
}

void setupMqtt() {
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setBufferSize(MQTT_BUFFER_SIZE);
  mqtt.setKeepAlive(30);
  mqtt.setCallback(onMqttMessage);
#if MQTT_USE_TLS
  transport.setInsecure();
#endif
}

}  // namespace

void setup() {
  Serial.begin(SERIAL_BAUD);
  delay(200);
  Serial.println();
  Serial.println("=== ESP32 Alert Terminal " ALARM_TERMINAL_FIRMWARE_VERSION " ===");
  Serial.printf("system=%s broker=%s:%d tls=%d\n", SYSTEM_ID, MQTT_HOST, MQTT_PORT, MQTT_USE_TLS);

  setupTopics();
  setupHardware();
  setupMqtt();

  // Render the boot placeholder immediately; retained alarm state replaces it on connect.
  copyPadded(state.line1, sizeof(state.line1), "SECURITY CAMERA");
  copyPadded(state.line2, sizeof(state.line2), "CONNECTING...");
  renderDisplay(true);
}

void loop() {
  const uint32_t now = millis();

  ensureWifi(now);

  if (WiFi.status() == WL_CONNECTED) {
    ensureMqtt(now);

    if (mqtt.connected()) {
      mqtt.loop();
      mqttWasConnected = true;
    } else if (mqttWasConnected) {
      // The broker just went away; start the fallback clock so the LCD stays fresh.
      mqttWasConnected = false;
      nextFallbackPollMs = now;
    }

    if (!mqtt.connected() && FALLBACK_ENABLED && now >= nextFallbackPollMs) {
      nextFallbackPollMs = now + FALLBACK_POLL_INTERVAL_MS;
      pollStatusFallback();
    }
  }

  expireAlarmIfDue();
  serviceBuzzer(now);

  if (now - lastRenderMs >= LCD_REFRESH_MS) {
    lastRenderMs = now;
    renderDisplay(false);
  }

  delay(10);
}
