#!/usr/bin/env python3
"""Tests for scripts/init_env.py (standard library + pytest, no network, no docker).

Run from the repository root:

    python3 -m pytest scripts/test_init_env.py

Every test builds a throwaway repository skeleton in `tmp_path`: compose.yaml is only used as the
"is this the repo root" marker, and the ESP32 template is copied from the real one so the generated
config.h stays faithful.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import init_env  # noqa: E402
from init_env import (  # noqa: E402
    EnvFile,
    Options,
    Provisioner,
    format_value,
    is_placeholder_dsn,
    npgsql_connection_string,
    parse_value,
)

REPO_ROOT = Path(__file__).resolve().parent.parent

REAL_NEON = (
    "postgresql://neondb_owner:realpw@ep-cool-name-12345.aws.neon.tech/neondb?sslmode=require"
)


@pytest.fixture()
def repo(tmp_path: Path) -> Path:
    """Minimal repository skeleton accepted by main()."""
    (tmp_path / "compose.yaml").write_text("services: {}\n", encoding="utf-8")
    template = REPO_ROOT / "esp32-alert-terminal" / "include" / "config.example.h"
    include = tmp_path / "esp32-alert-terminal" / "include"
    include.mkdir(parents=True)
    (include / "config.example.h").write_text(template.read_text(encoding="utf-8"), encoding="utf-8")
    (tmp_path / ".env").write_text(f"NEON_DB_URL={REAL_NEON}\n", encoding="utf-8")
    (tmp_path / ".gitignore").write_text(".env\n.env.*\n!.env.example\n/dist/\n", encoding="utf-8")
    return tmp_path


def provision(root: Path, systems: list[str], **kwargs) -> Provisioner:
    options = Options(
        root=root,
        systems=systems,
        rotate=kwargs.pop("rotate", False),
        interactive=False,
        lan_host=kwargs.pop("lan_host", "10.0.0.5"),
        wifi_ssid=kwargs.pop("wifi_ssid", "HomeNet"),
        wifi_password=kwargs.pop("wifi_password", "wifi-secret"),
    )
    assert not kwargs
    provisioner = Provisioner(options)
    provisioner.run()
    provisioner.write_all()
    return provisioner


# ---------------------------------------------------------------------------------------------
# Value parsing / formatting
# ---------------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("plain", "plain"),
        ('"double quoted"', "double quoted"),
        ("'single quoted'", "single quoted"),
        ("value # inline comment", "value"),
        ("value\t# inline comment", "value"),
        ("", ""),
        ("# comment only", ""),
        ('"with \\"escape\\""', 'with "escape"'),
    ],
)
def test_parse_value(raw: str, expected: str) -> None:
    assert parse_value(raw) == expected


def test_format_value_quotes_only_when_needed() -> None:
    assert format_value("sk_pi_abc-123") == "sk_pi_abc-123"
    assert format_value("") == ""
    # `$` must stay single-quoted so Compose does not interpolate it.
    assert format_value("pa$$word") == "'pa$$word'"
    assert format_value('has"quote') == "'has\"quote'"
    # Only when a single quote is already present does it fall back to double quotes.
    assert format_value("both ' and \"") == '"both \' and \\""'


def test_npgsql_connection_string_maps_neon_ssl() -> None:
    dsn = npgsql_connection_string(REAL_NEON)
    assert "Host=ep-cool-name-12345.aws.neon.tech" in dsn
    assert "Database=neondb" in dsn
    assert "Username=neondb_owner" in dsn
    assert "Password=realpw" in dsn
    # Neon's sslmode=require means "validate the certificate" to Npgsql's VerifyFull.
    assert "SSL Mode=VerifyFull" in dsn
    assert "Channel Binding=Require" in dsn


def test_npgsql_connection_string_rejects_non_postgres() -> None:
    with pytest.raises(ValueError):
        npgsql_connection_string("mysql://host/db")


def test_placeholder_dsn_detects_sample_values() -> None:
    assert is_placeholder_dsn("postgresql://user:password@ep-xxxx.aws.neon.tech/neondb")
    assert is_placeholder_dsn(
        "Host=ep-xxxx.aws.neon.tech;Database=neondb;Password=change-me;SSL Mode=VerifyFull;"
    )
    assert is_placeholder_dsn("postgresql://u:p@db.example.com/neondb")
    # Real values, including Neon hosts that merely start with `ep-`, are not placeholders.
    assert not is_placeholder_dsn(REAL_NEON)
    assert not is_placeholder_dsn("")


# ---------------------------------------------------------------------------------------------
# EnvFile
# ---------------------------------------------------------------------------------------------


def test_envfile_preserves_comments_and_unrelated_lines(tmp_path: Path) -> None:
    path = tmp_path / ".env"
    original = "# top comment\nA=1\n\n# about B\nB=2\n"
    path.write_text(original, encoding="utf-8")

    env = EnvFile(path)
    env.set("B", "2")  # unchanged value: byte-identical line, no cosmetic churn
    env.set("C", "3")
    assert env.to_text() == original + "C=3\n"


def test_envfile_set_collapses_duplicates(tmp_path: Path) -> None:
    path = tmp_path / ".env"
    path.write_text("A=1\nA=2\n", encoding="utf-8")

    env = EnvFile(path)
    env.set("A", "3")
    assert env.to_text() == "A=3\n"
    assert env.duplicate_keys() == []


def test_envfile_remove(tmp_path: Path) -> None:
    path = tmp_path / ".env"
    path.write_text("A=1\nB=2\nA=3\n", encoding="utf-8")

    env = EnvFile(path)
    assert env.remove("A") is True
    assert env.remove("missing") is False
    assert env.to_text() == "B=2\n"


# ---------------------------------------------------------------------------------------------
# Provisioning
# ---------------------------------------------------------------------------------------------


def test_generates_every_file_and_no_placeholders(repo: Path) -> None:
    provision(repo, ["sys-001"])

    for relative in (
        ".env",
        "external-auth-service/.env",
        "frontend/.env.local",
        "dist/devices/sys-001/pi.env",
        "dist/devices/sys-001/config.h",
    ):
        path = repo / relative
        assert path.exists(), relative
        assert path.stat().st_mode & 0o777 == 0o600, relative

    root = EnvFile(repo / ".env")
    assert (root.get("DB_URL") or "").startswith("Host=ep-cool-name-12345.aws.neon.tech")
    assert root.get("ExternalAuth__UseStub") == "false"
    assert root.get("ExternalAuth__InternalSecret") == root.get("INTERNAL_API_SECRET")
    assert root.get("MQTT_PUBLIC_PASS") == root.get("MQTT_CLIENT_PASS")

    auth = EnvFile(repo / "external-auth-service/.env")
    assert auth.get("DATABASE_URL") == REAL_NEON
    assert auth.get("INTERNAL_API_SECRET") == root.get("INTERNAL_API_SECRET")
    assert auth.get("SEED_PI_API_KEY_SYS_001") == root.get("PiAuth__ApiKey")
    assert auth.get("SEED_PI_API_KEY_SYS_002") == root.get("PiAuth__Systems__sys-002")

    frontend = EnvFile(repo / "frontend/.env.local")
    assert frontend.get("BFF_SESSION_SECRET") == root.get("BFF_SESSION_SECRET")
    assert frontend.get("INTERNAL_API_SECRET") == root.get("INTERNAL_API_SECRET")

    pi_env = EnvFile(repo / "dist/devices/sys-001/pi.env")
    assert pi_env.get("SYSTEM_ID") == "sys-001"
    assert pi_env.get("API_KEY") == root.get("PiAuth__ApiKey")
    assert pi_env.get("RTSP_HOST") == "10.0.0.5"

    header = (repo / "dist/devices/sys-001/config.h").read_text(encoding="utf-8")
    assert init_env.GENERATED_MARKER in header.split("\n", 1)[0]
    assert init_env.find_define(header, "SYSTEM_ID") == "sys-001"
    assert init_env.find_define(header, "MQTT_HOST") == "10.0.0.5"
    assert init_env.find_define(header, "WIFI_SSID") == "HomeNet"
    assert init_env.find_define(header, "MQTT_PASSWORD") == root.get("MQTT_CLIENT_PASS")
    assert (
        init_env.find_define(header, "FALLBACK_API_KEY")
        == root.get("PiAuth__AlertTerminalSystems__sys-001")
    )


def test_second_run_is_byte_identical(repo: Path) -> None:
    provision(repo, ["sys-001", "sys-002"])
    tracked = [
        ".env",
        "external-auth-service/.env",
        "frontend/.env.local",
        "dist/devices/sys-001/pi.env",
        "dist/devices/sys-001/config.h",
        "dist/devices/sys-002/pi.env",
        "dist/devices/sys-002/config.h",
    ]
    before = {name: (repo / name).read_bytes() for name in tracked}

    provision(repo, ["sys-001", "sys-002"])

    for name in tracked:
        assert (repo / name).read_bytes() == before[name], name


def test_explicit_empty_optional_values_are_kept(repo: Path) -> None:
    """Empty MEDIAMTX/WS/TS_AUTHKEY is a deliberate "feature off", not "fill in the default"."""
    env = repo / ".env"
    env.write_text(
        env.read_text(encoding="utf-8")
        + "MEDIAMTX_PUBLIC_URL=\nMQTT_PUBLIC_WS_URL=\nTS_AUTHKEY=\n",
        encoding="utf-8",
    )

    provision(repo, ["sys-001"])

    root = EnvFile(repo / ".env")
    assert root.get("MEDIAMTX_PUBLIC_URL") == ""
    assert root.get("MQTT_PUBLIC_WS_URL") == ""
    assert root.get("TS_AUTHKEY") == ""
    frontend = EnvFile(repo / "frontend/.env.local")
    assert frontend.get("MEDIAMTX_PUBLIC_URL") == ""
    assert frontend.get("MQTT_PUBLIC_WS_URL") == ""


def test_sample_db_url_is_replaced_but_real_one_is_preserved(repo: Path) -> None:
    env = repo / ".env"
    env.write_text(
        env.read_text(encoding="utf-8")
        + 'DB_URL="Host=ep-xxxx.aws.neon.tech;Database=neondb;Password=change-me;"\n',
        encoding="utf-8",
    )
    provision(repo, ["sys-001"])
    derived = EnvFile(repo / ".env").get("DB_URL")
    assert derived is not None
    assert "ep-cool-name-12345.aws.neon.tech" in derived

    # An operator-written connection string survives a re-run.
    custom = "Host=db.internal;Database=cams;Username=cams;Password=keepme;SSL Mode=VerifyFull;"
    text = env.read_text(encoding="utf-8").replace(derived, custom)
    env.write_text(text, encoding="utf-8")
    provision(repo, ["sys-001"])
    assert EnvFile(repo / ".env").get("DB_URL") == custom


def test_legacy_pi_key_belongs_to_default_system(repo: Path) -> None:
    """`--systems sys-002` must not hand sys-001's legacy key to the sys-002 Pi."""
    provision(repo, ["sys-002"])

    root = EnvFile(repo / ".env")
    assert root.get("PiAuth__DefaultSystemId") == "sys-001"
    sys002_key = root.get("PiAuth__Systems__sys-002")
    assert sys002_key
    assert sys002_key != root.get("PiAuth__ApiKey")

    pi_env = EnvFile(repo / "dist/devices/sys-002/pi.env")
    assert pi_env.get("API_KEY") == sys002_key
    assert pi_env.get("SYSTEM_ID") == "sys-002"

    auth = EnvFile(repo / "external-auth-service/.env")
    assert auth.get("SEED_PI_API_KEY_SYS_001") == root.get("PiAuth__ApiKey")
    assert auth.get("SEED_PI_API_KEY_SYS_002") == sys002_key


def test_redundant_systems_entry_for_default_system_is_removed(repo: Path) -> None:
    provision(repo, ["sys-001"])
    env = repo / ".env"
    env.write_text(
        env.read_text(encoding="utf-8") + "PiAuth__Systems__sys-001=sk_pi_stale\n",
        encoding="utf-8",
    )

    provisioner = provision(repo, ["sys-001"])

    assert not EnvFile(repo / ".env").has("PiAuth__Systems__sys-001")
    assert any("removed redundant PiAuth__Systems__sys-001" in n for n in provisioner.report.notes)


def test_rotate_regenerates_secrets_but_not_the_device_code(repo: Path) -> None:
    provision(repo, ["sys-001"])
    first = EnvFile(repo / ".env")
    before = {
        key: first.get(key)
        for key in (
            "Jwt__Secret",
            "INTERNAL_API_SECRET",
            "BFF_SESSION_SECRET",
            "MQTT_BACKEND_PASS",
            "MQTT_CLIENT_PASS",
            "PiAuth__ApiKey",
            "Admin__DeviceCode",
        )
    }

    provisioner = provision(repo, ["sys-001"], rotate=True)

    second = EnvFile(repo / ".env")
    for key, old in before.items():
        if key == "Admin__DeviceCode":
            assert second.get(key) == old  # the built-in admin's stored password
        else:
            assert second.get(key) != old, key
    assert {key for _, key in provisioner.report.rotated} >= {
        "Jwt__Secret",
        "INTERNAL_API_SECRET",
        "BFF_SESSION_SECRET",
        "MQTT_BACKEND_PASS",
        "MQTT_CLIENT_PASS",
        "PiAuth__ApiKey",
    }


def test_check_passes_on_generated_tree(repo: Path) -> None:
    provision(repo, ["sys-001"])
    assert init_env.run_check(repo, ["sys-001"]) == 0


def test_check_reports_duplicate_and_unsynced_secret(repo: Path) -> None:
    provision(repo, ["sys-001"])
    env = repo / ".env"
    env.write_text(env.read_text(encoding="utf-8") + "Jwt__Secret=duplicate\n", encoding="utf-8")
    assert init_env.run_check(repo, ["sys-001"]) == 1

    # Re-provisioning repairs the duplicate; a desynchronized frontend secret is still reported.
    provision(repo, ["sys-001"])
    assert init_env.run_check(repo, ["sys-001"]) == 0
    frontend = repo / "frontend/.env.local"
    frontend.write_text(
        frontend.read_text(encoding="utf-8").replace("BFF_SESSION_SECRET=", "BFF_SESSION_SECRET=x"),
        encoding="utf-8",
    )
    assert init_env.run_check(repo, ["sys-001"]) == 1


def test_check_flags_sample_db_url(repo: Path) -> None:
    provision(repo, ["sys-001"])
    env = repo / ".env"
    env.write_text(
        env.read_text(encoding="utf-8").replace(
            "NEON_DB_URL=" + REAL_NEON,
            "NEON_DB_URL=postgresql://user:password@ep-xxxx.aws.neon.tech/neondb",
        ),
        encoding="utf-8",
    )
    assert init_env.run_check(repo, ["sys-001"]) == 1


def test_generated_config_h_keeps_hand_edits(repo: Path) -> None:
    """Pins and tuning in config.h are the operator's; only managed defines are rewritten."""
    provision(repo, ["sys-001"])
    header_path = repo / "dist/devices/sys-001/config.h"
    text = header_path.read_text(encoding="utf-8")
    assert "#define PIN_STATUS_LED" in text
    edited = init_env.replace_define(text, "PIN_STATUS_LED", "99")
    header_path.write_text(edited, encoding="utf-8")

    provision(repo, ["sys-001"])

    again = header_path.read_text(encoding="utf-8")
    assert "#define PIN_STATUS_LED 99" in again
    assert again.count(init_env.GENERATED_MARKER) == 1


def test_lan_host_is_reused_from_existing_pi_env(repo: Path) -> None:
    provision(repo, ["sys-001"])
    pi_env = repo / "dist/devices/sys-001/pi.env"
    pi_env.write_text(
        pi_env.read_text(encoding="utf-8").replace("RTSP_HOST=10.0.0.5", "RTSP_HOST=192.168.5.9"),
        encoding="utf-8",
    )

    # No --lan-host: the value already in pi.env is the operator's address.
    provision(repo, ["sys-001"], lan_host=None)

    assert EnvFile(pi_env).get("RTSP_HOST") == "192.168.5.9"
    header = (repo / "dist/devices/sys-001/config.h").read_text(encoding="utf-8")
    assert init_env.find_define(header, "MQTT_HOST") == "192.168.5.9"


def test_parse_systems_trims_and_deduplicates() -> None:
    assert init_env.parse_systems(" sys-001 , sys-002 ,sys-001") == ["sys-001", "sys-002"]


def test_invalid_system_id_is_rejected() -> None:
    with pytest.raises(SystemExit):
        init_env.parse_systems("sys-001,../etc")
    with pytest.raises(SystemExit):
        init_env.parse_systems(" , ")


def test_non_interactive_reads_missing_value_from_environment(repo: Path, monkeypatch) -> None:
    env = repo / ".env"
    env.write_text("", encoding="utf-8")
    monkeypatch.setenv("NEON_DB_URL", REAL_NEON)

    provision(repo, ["sys-001"])

    assert EnvFile(repo / ".env").get("NEON_DB_URL") == REAL_NEON


def test_missing_neon_db_url_fails_clearly(repo: Path) -> None:
    (repo / ".env").write_text("", encoding="utf-8")
    os.environ.pop("NEON_DB_URL", None)
    with pytest.raises(SystemExit):
        provision(repo, ["sys-001"])
