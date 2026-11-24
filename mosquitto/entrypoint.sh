#!/bin/sh
# Generates the Mosquitto password file on first start, then execs the broker.
#
# The `eclipse-mosquitto:2` image ships without password tooling in the runtime image, so we use
# `mosquitto_passwd` from the same image (it is installed at /usr/bin/mosquitto_passwd) and keep the
# generated file on a named volume so restarts do not churn credentials.
set -eu

CONFIG_DIR=/mosquitto/config
PASSWD_FILE="$CONFIG_DIR/passwd"

BACKEND_USER="${MQTT_BACKEND_USER:-backend}"
BACKEND_PASS="${MQTT_BACKEND_PASS:-change-me-backend}"
CLIENT_USER="${MQTT_CLIENT_USER:-dashboard}"
CLIENT_PASS="${MQTT_CLIENT_PASS:-change-me-dashboard}"

if [ -z "$BACKEND_PASS" ] || [ -z "$CLIENT_PASS" ]; then
  echo "mosquitto entrypoint: MQTT_BACKEND_PASS and MQTT_CLIENT_PASS must not be empty" >&2
  exit 1
fi

if [ ! -s "$PASSWD_FILE" ]; then
  echo "mosquitto entrypoint: generating password file at $PASSWD_FILE"

  # `-b` takes user/password on the command line; `-c` creates the file. The second call must not
  # pass `-c` or it would truncate the first entry.
  mosquitto_passwd -b -c "$PASSWD_FILE" "$BACKEND_USER" "$BACKEND_PASS"
  mosquitto_passwd -b "$PASSWD_FILE" "$CLIENT_USER" "$CLIENT_PASS"

  # The broker runs as the `mosquitto` user; the file must be readable by it and not by others.
  chown mosquitto:mosquitto "$PASSWD_FILE"
  chmod 0600 "$PASSWD_FILE"
else
  echo "mosquitto entrypoint: reusing existing password file"
fi

# The ACL is mounted read-only and already names the `backend` and `dashboard` users. If the
# operator renamed the users via env vars, warn instead of silently locking themselves out.
if [ "$BACKEND_USER" != "backend" ] || [ "$CLIENT_USER" != "dashboard" ]; then
  echo "mosquitto entrypoint: WARNING users were renamed; update mosquitto/acl to match" >&2
fi

exec /usr/sbin/mosquitto -c "$CONFIG_DIR/mosquitto.conf"
