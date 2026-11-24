# ESP32 Alert Terminal

A 16x2 I2C LCD terminal that mirrors the Security Camera system's alarm state. It subscribes over
MQTT to `systems/<SYSTEM_ID>/alarm` and renders the backend's retained alarm document, driving a
status LED, a red alarm LED and a piezo buzzer. If the broker is unreachable it falls back to
polling `GET /api/device/status`.

## Wiring

| Signal | Default pin | Notes |
| --- | --- | --- |
| LCD SDA | GPIO 21 | ESP32 default I2C SDA |
| LCD SCL | GPIO 22 | ESP32 default I2C SCL |
| Status LED | GPIO 25 | Solid while armed |
| Alarm LED | GPIO 26 | Lit while an alarm window is open |
| Piezo buzzer | GPIO 27 | Pulsed 400 ms on / 600 ms off while alarming |

The LCD backpack is assumed to be at I2C address `0x27`; change `LCD_I2C_ADDRESS` in
`include/config.h` if yours differs. LEDs need a series resistor (220–330 Ω); a passive piezo
should be driven through a transistor if it draws more than a few mA.

## Configure

```bash
cd esp32-alert-terminal
cp include/config.example.h include/config.h
# edit include/config.h: Wi-Fi, SYSTEM_ID, MQTT host/credentials, fallback API key
```

`include/config.h` is gitignored — never commit Wi-Fi or broker credentials.

`SYSTEM_ID` must equal the camera's `SystemId`; it is the MQTT topic segment and the MediaMTX path.

## Build and flash

```bash
pip install platformio          # or use the VS Code PlatformIO extension
pio run                         # compile
pio run -t upload               # flash over USB
pio device monitor -b 115200    # serial log
```

## Behaviour

- **Retained state on boot.** The backend publishes the alarm document with the retain flag, so a
  terminal that powers on mid-alarm shows the alarm immediately instead of waiting for the next
  event.
- **Local expiry.** If `alarmUntil` passes while the terminal was offline, the firmware clears the
  alarm locally rather than buzzing forever.
- **Exponential backoff.** Failed MQTT connects retry at 2 s, 4 s, 8 s … capped at 60 s. A
  successful connect resets the backoff.
- **HTTP fallback.** While MQTT is down, `GET /api/device/status` is polled every
  `FALLBACK_POLL_INTERVAL_MS` (15 s) with the alert-terminal API key in `X-Api-Key`. The backend
  rejects camera keys on this endpoint, so use a dedicated alert-terminal key.

## TLS in production

Set `MQTT_USE_TLS true` and `MQTT_PORT 8883`, then terminate TLS in front of Mosquitto (a reverse
proxy or tunnel). The firmware calls `setInsecure()` so a self-signed certificate works; replace it
with a pinned CA (`transport.setCACert(...)`) for a production deployment.
