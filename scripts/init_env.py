#!/usr/bin/env python3
"""Provision and synchronize every environment/config file in this repository.

Generates and keeps in sync:

    .env                              compose.yaml: backend, mosquitto, mediamtx,
                                      external-auth-service, tailscale sidecar
    external-auth-service/.env        shares INTERNAL_API_SECRET and NEON_DB_URL
    frontend/.env.local               local `next dev`; shares BFF_SESSION_SECRET
    dist/devices/<systemId>/pi.env    Security-Camera-Pi settings
    dist/devices/<systemId>/config.h  esp32-alert-terminal config (X-Api-Key, MQTT, Wi-Fi)

Design rules (see Security-Camera-Backend/Services/Security/ProductionSecretValidator.cs):

* Idempotent. Existing, non-placeholder values are preserved verbatim; only missing
  values are generated, so re-running with everything configured changes nothing.
* Every generated secret is `secrets.token_urlsafe(32)`: 32 bytes of CSPRNG entropy,
  base64url-encoded, so it can never collide with the repository's `dev-*`,
  `factory-pass-*` or `change-me*` placeholders that make the backend refuse to boot
  outside Development.
* `--rotate` regenerates every secret (JWT, BFF session key, internal API secret,
  MQTT passwords, per-system Pi / alert-terminal keys, factory-admin seed passwords)
  and prints the follow-up actions, notably recreating the mosquitto volume.
* Only values that cannot be generated are prompted for: NEON_DB_URL, TS_AUTHKEY,
  the public MediaMTX / MQTT-WebSocket URLs, the LAN address used by the Pi and the
  ESP32, plus Wi-Fi credentials. A prompt is skipped when a usable value already
  exists, and `--non-interactive` never prompts.
* Remote/device actions stay opt-in: `--push-pi user@host --system <id>` and
  `--flash-esp32 --system <id>`.
* Every generated file is written atomically with mode 0600, and `.gitignore`
  coverage is verified (a missing rule is appended, or `--check` reports it).

Standard library only; Python 3.9+.

Examples:
    scripts/init_env.py
    scripts/init_env.py --systems sys-001,sys-002
    scripts/init_env.py --non-interactive
    scripts/init_env.py --rotate
    scripts/init_env.py --check
    scripts/init_env.py --push-pi pi@cam-001 --system sys-001
    scripts/init_env.py --flash-esp32 --system sys-001
"""

from __future__ import annotations

import argparse
import contextlib
import getpass
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlsplit

# ---------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------

DEFAULT_SYSTEMS = "sys-001"
# The backend maps the legacy `PiAuth__ApiKey` to `PiAuth__DefaultSystemId`, whose own default is
# sys-001. Keep the legacy key on that tenant unless the operator pinned a different one.
DEFAULT_LEGACY_SYSTEM = "sys-001"

# Mirrors frontend/lib/edge-router.ts:SYSTEM_ID_PATTERN, so a generated id is always usable as a
# MediaMTX path and as an EDGE_BACKENDS_JSON key.
SYSTEM_ID_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
# .NET configuration keys carry `__` and often a hyphenated system id (`PiAuth__Systems__sys-001`),
# so the key pattern has to accept `-` and `.` beyond the POSIX shell set.
ENV_LINE_RE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.-]*)\s*=\s*(.*?)\s*$")
# Values matching this can be written without quotes.
SAFE_VALUE_RE = re.compile(r"^[A-Za-z0-9_@%+=:,./-]+$")

# Exactly the prefixes ProductionSecretValidator rejects.
FORBIDDEN_PREFIXES = (
    "dev-only-",
    "dev-pi-key-",
    "dev-alert-terminal-key-",
    "factory-pass-",
    "dev-only-jwt-secret-",
    "change-me",
    "replace-me",
)
FORBIDDEN_EXACT = (
    "changeme",
    "cam-0000-xxxx",
    "cam-0000-dev1",
    "your-ssid",
    "your-wifi-password",
)

# Any child of ExternalAuth:StubAccounts makes the backend refuse to start outside Development.
FORBIDDEN_KEY_PREFIXES = ("ExternalAuth__StubAccounts__", "ExternalAuth__StubAccounts:")

DEFAULT_TS_HOSTNAME = "security-camera-backend"
DEFAULT_LAN_HOST = "192.168.1.10"
DEFAULT_BACKEND_PORT = 8080
DEFAULT_RTSP_PORT = 8554
DEFAULT_LOCAL_BACKEND = "http://localhost:8080"
DEFAULT_LOCAL_EXTERNAL_AUTH = "http://localhost:3001"
DEFAULT_MEDIAMTX_PUBLIC = "http://localhost:8889"
DEFAULT_MQTT_WS_PUBLIC = "ws://localhost:9001"
# mosquitto/acl hard-codes these two usernames, so they are not configurable.
DEFAULT_MQTT_BACKEND_USER = "backend"
DEFAULT_MQTT_CLIENT_USER = "dashboard"

MOSQUITTO_VOLUME = "security-camera-mosquitto-data"
DEFAULT_PI_SERVICE = "security-camera-pi"
# external-auth-service/scripts/seed.ts always seeds these two factory kits, so their backend
# keys and seed passwords are managed even when the operator only asked for one system.
SEEDED_SYSTEMS = ("sys-001", "sys-002")

# String-valued defines this script owns in esp32-alert-terminal/include/config.h.
CONFIG_H_STRING_DEFINES = (
    "WIFI_SSID",
    "WIFI_PASSWORD",
    "SYSTEM_ID",
    "MQTT_HOST",
    "MQTT_USERNAME",
    "MQTT_PASSWORD",
    "FALLBACK_STATUS_URL",
    "FALLBACK_API_KEY",
)

GENERATED_MARKER = "GENERATED by scripts/init_env.py"


# ---------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------


def die(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(1)


def warn(message: str) -> None:
    print(f"warning: {message}", file=sys.stderr)


def new_secret() -> str:
    """32 bytes from the CSPRNG, base64url-encoded (43 chars, no quoting needed)."""
    return secrets.token_urlsafe(32)


def new_pi_key() -> str:
    return "sk_pi_" + secrets.token_urlsafe(32)


def new_terminal_key() -> str:
    return "sk_term_" + secrets.token_urlsafe(32)


def new_device_code() -> str:
    """`CAM-XXXX-XXXX`, printed on the hardware. Crockford-ish: no 0/O/1/I."""
    alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"

    def block() -> str:
        return "".join(secrets.choice(alphabet) for _ in range(4))

    return f"CAM-{block()}-{block()}"


def parse_value(raw: str) -> str:
    """Turn the right-hand side of a KEY=VALUE line into its literal value."""
    raw = raw.strip()
    if not raw:
        return ""
    if raw.startswith("#"):
        return ""
    if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in ("'", '"'):
        inner = raw[1:-1]
        if raw[0] == '"':
            inner = inner.replace('\\"', '"').replace("\\\\", "\\")
        return inner
    # Unquoted values may carry an inline comment (`KEY=value # note`).
    for marker in (" #", "\t#"):
        if marker in raw:
            raw = raw.split(marker, 1)[0]
    return raw.strip()


def format_value(value: str) -> str:
    """Render a value for a .env file, quoting only when necessary.

    Single quotes are preferred for values containing `$` because Docker Compose performs
    interpolation inside double-quoted (and unquoted) .env values but not inside single ones.
    """
    if value == "":
        return ""
    if SAFE_VALUE_RE.match(value):
        return value
    if "'" not in value and "\n" not in value:
        return f"'{value}'"
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def is_placeholder(value: str | None) -> bool:
    """True when a value is missing or one of the repository's shipped placeholders."""
    if value is None:
        return True
    trimmed = value.strip()
    if not trimmed:
        return True
    lowered = trimmed.lower()
    if lowered in FORBIDDEN_EXACT:
        return True
    return any(lowered.startswith(prefix) for prefix in FORBIDDEN_PREFIXES)


def is_forbidden_value(value: str | None) -> bool:
    """Like is_placeholder, but an empty value is acceptable (it just disables a feature)."""
    if not value or not value.strip():
        return False
    lowered = value.strip().lower()
    if lowered in FORBIDDEN_EXACT:
        return True
    return any(lowered.startswith(prefix) for prefix in FORBIDDEN_PREFIXES)


# Substrings that only ever appear in the sample connection strings shipped by .env.example:
# `postgresql://user:password@ep-xxxx...` and `Host=ep-xxxx;...;Password=change-me;`.
# `ep-xxxx` alone is not enough (real Neon hosts look like `ep-cool-name-12345.aws.neon.tech`),
# so the markers stay specific to the placeholders.
_DSN_PLACEHOLDER_MARKERS = (
    "ep-xxxx",
    "user:password@",
    "password=change-me",
    "password='change-me'",
    "example.com",
    "example.org",
    "example.net",
)


def is_placeholder_dsn(value: str | None) -> bool:
    """True when a URL/connection string is one of the repository's sample DSNs.

    A whole-value prefix check cannot see a `change-me` password embedded in
    `Host=...;Password=change-me;`, which is exactly how `.env.example` ships `DB_URL`.
    """
    if not value or not value.strip():
        return False
    lowered = value.strip().lower()
    if is_placeholder(lowered):
        return True
    return any(marker in lowered for marker in _DSN_PLACEHOLDER_MARKERS)


# ---------------------------------------------------------------------------------------------
# .env editing that preserves comments, ordering and unrelated lines
# ---------------------------------------------------------------------------------------------


class EnvFile:
    """Line-oriented .env editor: never reorders or rewrites lines it does not own."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self.existed = path.exists()
        self.lines: list[str] = (
            path.read_text(encoding="utf-8").splitlines() if self.existed else []
        )
        self._last_section: str | None = None

    # -- reads -------------------------------------------------------------------------------
    def get(self, key: str) -> str | None:
        # Last occurrence wins, which is how Docker Compose and dotenv read a duplicated key.
        found: str | None = None
        for line in self.lines:
            match = ENV_LINE_RE.match(line)
            if match and match.group(1) == key:
                found = parse_value(match.group(2))
        return found

    def has(self, key: str) -> bool:
        return any(
            (match := ENV_LINE_RE.match(line)) is not None and match.group(1) == key
            for line in self.lines
        )

    def items(self) -> list[tuple[str, str]]:
        """Effective KEY/VALUE pairs, one per key (see `get`)."""
        pairs: dict[str, str] = {}
        for line in self.lines:
            match = ENV_LINE_RE.match(line)
            if match:
                pairs[match.group(1)] = parse_value(match.group(2))
        return list(pairs.items())

    def duplicate_keys(self) -> list[str]:
        seen: dict[str, int] = {}
        for line in self.lines:
            match = ENV_LINE_RE.match(line)
            if match:
                seen[match.group(1)] = seen.get(match.group(1), 0) + 1
        return sorted(key for key, count in seen.items() if count > 1)

    # -- writes ------------------------------------------------------------------------------
    def set(self, key: str, value: str, *, section: str | None = None) -> None:
        rendered = f"{key}={format_value(value)}"
        first: int | None = None
        kept: list[str] = []
        for line in self.lines:
            match = ENV_LINE_RE.match(line)
            if match and match.group(1) == key:
                if first is not None:
                    continue  # a later duplicate would win for Compose; drop it
                first = len(kept)
                # Leave the line byte-identical when the value already matches, so re-running
                # against a hand-written .env produces no cosmetic quoting churn.
                if parse_value(match.group(2)) != value:
                    line = rendered
            kept.append(line)
        if first is not None:
            self.lines = kept
            if section:
                self._last_section = section
            return

        if section and section != self._last_section:
            if self.lines and self.lines[-1].strip():
                self.lines.append("")
            self.lines.extend(section.splitlines())
        self._last_section = section
        self.lines.append(rendered)

    def remove(self, key: str) -> bool:
        """Drop every definition of `key`; True when at least one line was removed."""
        kept: list[str] = []
        removed = False
        for line in self.lines:
            match = ENV_LINE_RE.match(line)
            if match and match.group(1) == key:
                removed = True
                continue
            kept.append(line)
        self.lines = kept
        return removed

    def remove_key_prefixes(self, prefixes: tuple[str, ...]) -> list[str]:
        removed: list[str] = []
        kept: list[str] = []
        for line in self.lines:
            match = ENV_LINE_RE.match(line)
            if match and any(match.group(1).startswith(p) for p in prefixes):
                removed.append(match.group(1))
                continue
            kept.append(line)
        self.lines = kept
        return removed

    def to_text(self) -> str:
        return "\n".join(self.lines).rstrip("\n") + "\n"


def write_secret_file(path: Path, text: str, mode: int = 0o600) -> None:
    """Atomically write a secret-bearing file with restrictive permissions.

    Only directories this script creates are tightened: changing the mode of an existing
    directory (the repository root, `frontend/`, `external-auth-service/`) would be an
    unrelated, surprising modification of the operator's working tree.
    """
    parent_existed = path.parent.is_dir()
    path.parent.mkdir(parents=True, exist_ok=True)
    if not parent_existed:
        with contextlib.suppress(OSError):
            path.parent.chmod(0o700)
    if not text.endswith("\n"):
        text += "\n"

    fd, tmp_name = tempfile.mkstemp(dir=str(path.parent), prefix=f".{path.name}.", suffix=".tmp")
    tmp = Path(tmp_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(tmp, mode)
        os.replace(tmp, path)
    except BaseException:
        with contextlib.suppress(OSError):
            tmp.unlink()
        raise


# ---------------------------------------------------------------------------------------------
# Derivations
# ---------------------------------------------------------------------------------------------

_SSL_MODES = {
    "disable": "Disable",
    "allow": "Prefer",
    "prefer": "Prefer",
    "require": "VerifyFull",
    "verify-ca": "VerifyCA",
    "verify-full": "VerifyFull",
}
_CHANNEL_BINDING = {"disable": "Disable", "prefer": "Prefer", "require": "Require"}


def _npgsql_quote(value: str) -> str:
    if value and not re.search(r"[;'\"=\s]", value):
        return value
    return "'" + value.replace("'", "''") + "'"


def npgsql_connection_string(postgres_url: str) -> str:
    """Translate a `postgresql://` URL into the Npgsql connection string DB_URL expects.

    Neon's URL parameters map onto Npgsql's enums: `sslmode=require` becomes `SSL Mode=VerifyFull`
    (Npgsql's "require" means "encrypt but do not validate"), and `channel_binding=require` is
    carried over so the server-side SCRAM binding keeps working.
    """
    parts = urlsplit(postgres_url.strip())
    if parts.scheme not in ("postgres", "postgresql"):
        raise ValueError(
            f"NEON_DB_URL must start with postgresql:// (got scheme {parts.scheme!r})"
        )
    if not parts.hostname:
        raise ValueError("NEON_DB_URL has no host")

    query = parse_qs(parts.query)
    ssl_mode = _SSL_MODES.get((query.get("sslmode") or ["require"])[0].lower(), "VerifyFull")
    channel_binding = _CHANNEL_BINDING.get(
        (query.get("channel_binding") or ["require"])[0].lower(), "Require"
    )

    fields: list[tuple[str, str]] = [("Host", parts.hostname)]
    if parts.port:
        fields.append(("Port", str(parts.port)))
    fields.append(("Database", unquote(parts.path.lstrip("/")) or "neondb"))
    if parts.username:
        fields.append(("Username", unquote(parts.username)))
    if parts.password:
        fields.append(("Password", unquote(parts.password)))
    fields.append(("SSL Mode", ssl_mode))
    fields.append(("Channel Binding", channel_binding))

    return "; ".join(f"{name}={_npgsql_quote(value)}" for name, value in fields) + ";"


def url_host(url: str | None) -> str | None:
    """Hostname of a URL, or None when empty/invalid/loopback."""
    if not url or not url.strip():
        return None
    try:
        host = urlsplit(url.strip()).hostname
    except ValueError:
        return None
    if not host or host in ("localhost", "127.0.0.1", "::1", "0.0.0.0"):
        return None
    return host


def strip_template_preamble(text: str) -> str:
    """Drop the example file's "copy me into include/" instructions from a generated config."""
    lines = text.splitlines(keepends=True)
    if lines and "Copy to include/config.h" in lines[0]:
        while lines and lines[0].lstrip().startswith("//"):
            lines.pop(0)
        while lines and not lines[0].strip():
            lines.pop(0)
    return "".join(lines)


def c_string(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def find_define(text: str, name: str) -> str | None:
    match = re.search(rf'(?m)^[ \t]*#define[ \t]+{re.escape(name)}[ \t]+"([^"\n]*)"', text)
    return match.group(1) if match else None


def replace_define(text: str, name: str, rendered: str) -> str:
    pattern = re.compile(rf"(?m)^([ \t]*#define[ \t]+{re.escape(name)}[ \t]+)(.*)$")
    new_text, count = pattern.subn(lambda m: m.group(1) + rendered, text, count=1)
    if count:
        return new_text
    return text.rstrip("\n") + f"\n#define {name} {rendered}\n"


# ---------------------------------------------------------------------------------------------
# Provisioner
# ---------------------------------------------------------------------------------------------


@dataclass
class Options:
    root: Path
    systems: list[str]
    rotate: bool
    interactive: bool
    lan_host: str | None
    wifi_ssid: str | None
    wifi_password: str | None


@dataclass
class Report:
    files: list[Path] = field(default_factory=list)
    created: list[tuple[str, str]] = field(default_factory=list)
    rotated: list[tuple[str, str]] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)


class Provisioner:
    def __init__(self, options: Options) -> None:
        self.o = options
        self.root = options.root
        self.report = Report()

        self.root_env = EnvFile(self.root / ".env")
        self.auth_env = EnvFile(self.root / "external-auth-service" / ".env")
        self.frontend_env = EnvFile(self.root / "frontend" / ".env.local")
        self.dist_dir = self.root / "dist" / "devices"
        self.pi_envs = {
            system: EnvFile(self.dist_dir / system / "pi.env") for system in options.systems
        }
        # Systems whose config.h is still the untouched example (its values are placeholders).
        self.from_template: set[str] = set()
        self.config_headers: dict[str, str] = {}
        for system in options.systems:
            if not (self.dist_dir / system / "config.h").exists():
                self.from_template.add(system)
            self.config_headers[system] = self._read_header(system)

        # `frontend/.env` is the older single-dotenv file. Next.js gives `.env.local`
        # precedence, so its session secret and external-auth URL are adopted rather than
        # silently replaced by fresh values.
        legacy_frontend = self.root / "frontend" / ".env"
        self.legacy_frontend_env = EnvFile(legacy_frontend) if legacy_frontend.exists() else None

        self._lan_cache: str | None = None
        self._wifi_cache: tuple[str, str] | None = None

    # -- io ----------------------------------------------------------------------------------
    def _read_header(self, system: str) -> str:
        path = self.dist_dir / system / "config.h"
        if path.exists():
            return path.read_text(encoding="utf-8")
        template = self.root / "esp32-alert-terminal" / "include" / "config.example.h"
        if not template.exists():
            die(f"missing ESP32 config template: {self.rel(template)}")
        return strip_template_preamble(template.read_text(encoding="utf-8"))

    def rel(self, path: Path) -> str:
        try:
            return str(path.relative_to(self.root))
        except ValueError:
            return str(path)

    def remember(self, path: Path) -> None:
        if path not in self.report.files:
            self.report.files.append(path)

    # -- prompting ---------------------------------------------------------------------------
    def ask(self, label: str, default: str = "", *, secret: bool = False) -> str:
        """Ask for an external value; an empty answer keeps `default`."""
        if not self.o.interactive:
            return default

        if default:
            shown = "***" if secret else default
        else:
            shown = "empty = disabled"
        prompt = f"  {label} [{shown}]: "
        try:
            answer = getpass.getpass(prompt) if secret else input(prompt)
        except EOFError:
            print()
            die(
                f"no answer for {label!r} (stdin closed); set it beforehand or re-run "
                "with --non-interactive"
            )
        except KeyboardInterrupt:
            print()
            die("interrupted")
        answer = (answer or "").strip()
        return answer or default

    def external(
        self,
        env: EnvFile,
        key: str,
        prompt: str,
        default: str = "",
        *,
        secret: bool = False,
        placeholder=is_placeholder,
    ) -> str:
        """Resolve a value that cannot be generated.

        Order: the value already in the file, then the process environment (so CI can supply
        `NEON_DB_URL=... scripts/init_env.py --non-interactive`), then a prompt whose default is
        `default`.

        An explicitly empty value in the file is preserved: for `MEDIAMTX_PUBLIC_URL`,
        `MQTT_PUBLIC_WS_URL` and `TS_AUTHKEY` empty is a deliberate "disabled" (the dashboard
        keeps its fallbacks), so silently substituting the localhost default would enable a
        feature the operator turned off.
        """
        existing = env.get(key)
        if existing is not None:
            if not placeholder(existing):
                return existing
            if not existing.strip():
                return ""
        from_process = os.environ.get(key, "").strip()
        if from_process and not placeholder(from_process):
            return from_process
        answer = self.ask(prompt, default, secret=secret)
        return answer if not placeholder(answer) else ""

    # -- orchestration -----------------------------------------------------------------------
    def run(self) -> None:
        self.provision_root_env()
        self.provision_external_auth_env()
        self.provision_frontend_env()
        for system in self.o.systems:
            self.provision_pi_env(system)
            self.provision_config_header(system)

    def write_all(self) -> None:
        for env in (
            self.root_env,
            self.auth_env,
            self.frontend_env,
            *self.pi_envs.values(),
        ):
            write_secret_file(env.path, env.to_text())
            self.remember(env.path)
        for system, text in self.config_headers.items():
            path = self.dist_dir / system / "config.h"
            write_secret_file(path, text)
            self.remember(path)

    # -- generated-vs-preserved bookkeeping --------------------------------------------------
    def secret(self, env: EnvFile, key: str, generator) -> str:
        existing = env.get(key)
        if not self.o.rotate and existing and not is_placeholder(existing):
            return existing
        value = generator()
        rotated = self.o.rotate and bool(existing) and not is_placeholder(existing)
        (self.report.rotated if rotated else self.report.created).append(
            (self.rel(env.path), key)
        )
        return value

    @staticmethod
    def plain(env: EnvFile, key: str, default: str) -> str:
        existing = env.get(key)
        if existing is not None and not is_placeholder(existing):
            return existing
        if existing is not None and not existing.strip():
            return ""  # an explicit empty value is a deliberate "disabled"
        return default

    # ---- root .env -------------------------------------------------------------------------
    def provision_root_env(self) -> None:
        env = self.root_env

        S_DB = (
            "# --- Database -------------------------------------------------------------------\n"
            "# NEON_DB_URL feeds external-auth-service (serverless driver, postgresql:// URL).\n"
            "# DB_URL is the Npgsql form the ASP.NET Core backend reads, derived from it.\n"
            "# Both point at the same Neon database."
        )
        S_AUTH = (
            "# --- Backend authentication ------------------------------------------------------\n"
            "# Validated at startup by ProductionSecretValidator: dev-*, factory-pass-* and\n"
            "# change-me* values make the backend refuse to boot outside Development."
        )
        S_EXTERNAL = (
            "# --- External factory-admin service ----------------------------------------------\n"
            "# INTERNAL_API_SECRET must match external-auth-service/.env exactly."
        )
        S_BFF = (
            "# --- Dashboard BFF ---------------------------------------------------------------\n"
            "# Seals the bff_session cookie; must match frontend/.env.local."
        )
        S_MQTT = (
            "# --- MQTT broker (mosquitto) -----------------------------------------------------\n"
            "# mosquitto/entrypoint.sh seeds its passwd file from these on FIRST start. After a\n"
            "# password change the volume must be recreated or the broker keeps the old hash:\n"
            f"#   docker compose rm -sf mosquitto && docker volume rm {MOSQUITTO_VOLUME}"
        )
        S_REALTIME = (
            "# --- Public browser endpoints (MediaMTX / MQTT over WebSockets) ------------------\n"
            "# Empty = the dashboard keeps its MJPEG + polling fallbacks."
        )
        S_TAILSCALE = (
            "# --- Tailscale sidecar -----------------------------------------------------------\n"
            "# Opt in with: docker compose --profile tailscale up -d"
        )
        S_NET = "# --- Dashboard / network policy -------------------------------------------------"

        # 1. External values: kept when set, prompted only when missing.
        neon = self.external(
            env,
            "NEON_DB_URL",
            "NEON_DB_URL (postgresql:// URL used by external-auth-service)",
            placeholder=is_placeholder_dsn,
        )
        if not neon:
            die(
                "NEON_DB_URL is required: set it in .env, export it, or run interactively "
                "(it cannot be generated)"
            )
        env.set("NEON_DB_URL", neon, section=S_DB)

        db_url = env.get("DB_URL")
        # `is_placeholder_dsn` treats empty as "not a sample" (an empty value disables things),
        # but DB_URL is required, so a missing or empty line must be derived as well.
        if db_url is None or not db_url.strip() or is_placeholder_dsn(db_url):
            try:
                db_url = npgsql_connection_string(neon)
            except ValueError as exc:
                die(f"cannot derive DB_URL from NEON_DB_URL: {exc}")
            self.report.created.append((".env", "DB_URL"))
            note(
                "DB_URL was derived from NEON_DB_URL; edit .env if the backend should use a "
                "different Npgsql connection string."
            )
        env.set("DB_URL", db_url, section=S_DB)

        # 2. Backend secrets. The device code is not rotated: it is the built-in admin's stored
        #    password, so rotating it would lock that account out.
        env.set("Jwt__Secret", self.secret(env, "Jwt__Secret", new_secret), section=S_AUTH)
        env.set("Jwt__ExpirationMinutes", self.plain(env, "Jwt__ExpirationMinutes", "30"))
        if env.has("Admin__DeviceCode") and not is_placeholder(env.get("Admin__DeviceCode")):
            env.set("Admin__DeviceCode", env.get("Admin__DeviceCode") or "")
        else:
            env.set("Admin__DeviceCode", self.secret(env, "Admin__DeviceCode", new_device_code))

        # PiAuth:ApiKey is the legacy single-key setting and the backend always maps it to
        # PiAuth:DefaultSystemId (sys-001 unless overridden). It therefore belongs to the
        # *default* system, not to whichever system happens to be first on the command line:
        # `--systems sys-002` must not hand sys-001's key to the sys-002 Pi, or that Pi would
        # ingest into sys-001.
        default_system = env.get("PiAuth__DefaultSystemId")
        if not default_system or is_placeholder(default_system):
            default_system = DEFAULT_LEGACY_SYSTEM
        env.set("PiAuth__ApiKey", self.secret(env, "PiAuth__ApiKey", new_pi_key))
        env.set("PiAuth__DefaultSystemId", default_system)
        env.set("PiAuth__NodeSystemId", self.plain(env, "PiAuth__NodeSystemId", ""))
        # A Systems entry for the default system would only add a second, unused key for the same
        # tenant (an earlier buggy run may have created one), so drop it.
        if env.remove(f"PiAuth__Systems__{default_system}"):
            self.report.notes.append(
                f"removed redundant PiAuth__Systems__{default_system}: PiAuth__ApiKey already "
                "owns that system via PiAuth__DefaultSystemId"
            )
        for system in self.managed_systems():
            if system == default_system:
                continue
            env.set(
                f"PiAuth__Systems__{system}",
                self.secret(env, f"PiAuth__Systems__{system}", new_pi_key),
            )
        for system in self.managed_systems():
            env.set(
                f"PiAuth__AlertTerminalSystems__{system}",
                self.secret(env, f"PiAuth__AlertTerminalSystems__{system}", new_terminal_key),
            )

        # 3. Secret shared with external-auth-service and the dashboard.
        internal = self.secret(env, "INTERNAL_API_SECRET", new_secret)
        env.set("INTERNAL_API_SECRET", internal, section=S_EXTERNAL)
        # A stale ExternalAuth__InternalSecret would silently fail admin validation.
        env.set("ExternalAuth__InternalSecret", internal)
        env.set("ExternalAuth__UseStub", "false")
        env.set(
            "ExternalAuth__BaseUrl",
            self.plain(env, "ExternalAuth__BaseUrl", DEFAULT_LOCAL_EXTERNAL_AUTH),
        )

        # 4. BFF session key, shared with frontend/.env.local. A key already present in the legacy
        #    frontend/.env is adopted so existing dev cookies keep verifying.
        env.set(
            "BFF_SESSION_SECRET",
            self.secret(env, "BFF_SESSION_SECRET", self.bff_secret_fallback),
            section=S_BFF,
        )

        # 5. MQTT broker credentials. mosquitto/acl names exactly `backend` and `dashboard`.
        backend_user = self.plain(env, "MQTT_BACKEND_USER", DEFAULT_MQTT_BACKEND_USER)
        client_user = self.plain(env, "MQTT_CLIENT_USER", DEFAULT_MQTT_CLIENT_USER)
        if backend_user != DEFAULT_MQTT_BACKEND_USER or client_user != DEFAULT_MQTT_CLIENT_USER:
            warn(
                "MQTT users were renamed, but mosquitto/acl still grants only `backend` and "
                "`dashboard`. Update mosquitto/acl or MQTT clients cannot authenticate."
            )
        env.set("MQTT_BACKEND_USER", backend_user, section=S_MQTT)
        backend_pass = self.secret(env, "MQTT_BACKEND_PASS", new_secret)
        env.set("MQTT_BACKEND_PASS", backend_pass)
        env.set("MQTT_CLIENT_USER", client_user)
        client_pass = self.secret(env, "MQTT_CLIENT_PASS", new_secret)
        env.set("MQTT_CLIENT_PASS", client_pass)
        env.set("Mqtt__Enabled", self.plain(env, "Mqtt__Enabled", "true"))
        env.set("Mqtt__Host", self.plain(env, "Mqtt__Host", "mosquitto"))
        env.set("Mqtt__Port", self.plain(env, "Mqtt__Port", "1883"))
        env.set("Mqtt__TopicPrefix", self.plain(env, "Mqtt__TopicPrefix", "systems"))

        # 6. Browser-facing realtime endpoints.
        mediamtx = self.external(
            env,
            "MEDIAMTX_PUBLIC_URL",
            "MEDIAMTX_PUBLIC_URL (browser-reachable MediaMTX origin, empty = MJPEG fallback)",
            DEFAULT_MEDIAMTX_PUBLIC,
        )
        env.set("MEDIAMTX_PUBLIC_URL", mediamtx, section=S_REALTIME)

        mqtt_ws = self.external(
            env,
            "MQTT_PUBLIC_WS_URL",
            "MQTT_PUBLIC_WS_URL (browser-reachable ws://, empty = polling fallback)",
            DEFAULT_MQTT_WS_PUBLIC,
        )
        env.set("MQTT_PUBLIC_WS_URL", mqtt_ws)
        env.set("MQTT_PUBLIC_USER", client_user)
        env.set("MQTT_PUBLIC_PASS", client_pass)

        # 7. Tailscale sidecar.
        ts_authkey = self.external(
            env,
            "TS_AUTHKEY",
            "TS_AUTHKEY (tagged Tailscale auth key, empty = sidecar disabled)",
        )
        env.set("TS_AUTHKEY", ts_authkey, section=S_TAILSCALE)
        env.set("TS_HOSTNAME", self.plain(env, "TS_HOSTNAME", DEFAULT_TS_HOSTNAME))
        env.set("TS_EXTRA_ARGS", self.plain(env, "TS_EXTRA_ARGS", ""))

        # 8. Network / dashboard policy.
        env.set("ALLOWED_ORIGINS", self.plain(env, "ALLOWED_ORIGINS", ""), section=S_NET)
        env.set("COOKIE_SECURE", self.plain(env, "COOKIE_SECURE", "false"))
        env.set("TRUST_PROXY_HEADERS", self.plain(env, "TRUST_PROXY_HEADERS", "false"))
        env.set("EDGE_BACKENDS_JSON", self.plain(env, "EDGE_BACKENDS_JSON", ""))
        env.set("Cors__AllowedOrigins", self.plain(env, "Cors__AllowedOrigins", ""))

        # 9. Scrub anything that trips ProductionSecretValidator.
        removed = env.remove_key_prefixes(FORBIDDEN_KEY_PREFIXES)
        if removed:
            self.report.notes.append(
                "removed stub-account keys from .env (they make the backend refuse to start): "
                + ", ".join(removed)
            )

        self.warn_about_dev_looking_secrets()

        # compose.yaml lists its PiAuth pass-throughs by name, so a new system needs its own
        # entries or the backend never sees that key (and the Pi gets 401s).
        missing_forwarding = [
            f"PiAuth__Systems__{s}"
            for s in self.managed_systems()
            if s != default_system and s not in {"sys-002"}
        ] + [
            f"PiAuth__AlertTerminalSystems__{s}"
            for s in self.managed_systems()
            if s not in {"sys-001", "sys-002"}
        ]
        if missing_forwarding:
            self.report.notes.append(
                "compose.yaml forwards PiAuth__ApiKey (DefaultSystemId), "
                "PiAuth__Systems__sys-002 and PiAuth__AlertTerminalSystems__sys-001/2 only; add "
                "pass-through entries for "
                + ", ".join(missing_forwarding)
                + " or the backend never sees those keys (the devices get 401s)."
            )

    def warn_about_dev_looking_secrets(self) -> None:
        """Flag preserved values that are not repo placeholders but still look like dev keys.

        `ProductionSecretValidator` accepts these, so they are kept (they may be the keys the
        running deployment already uses), but an operator probably wants them rotated before
        trusting the stack with a real camera.
        """
        suspects: list[str] = []
        for env in (self.root_env, self.auth_env):
            for key, value in env.items():
                if not value or is_placeholder(value):
                    continue
                if value.startswith("sk_dev_") or value.lower().startswith("dev-"):
                    suspects.append(f"{self.rel(env.path)}: {key}")
        if suspects:
            self.report.notes.append(
                "preserved values that look like development keys (pass --rotate to replace "
                "them): " + ", ".join(suspects)
            )

    # ---- external-auth-service/.env ---------------------------------------------------------
    def provision_external_auth_env(self) -> None:
        env = self.auth_env
        neon = self.root_env.get("NEON_DB_URL") or ""
        internal = self.root_env.get("INTERNAL_API_SECRET") or ""

        env.set(
            "DATABASE_URL",
            neon,
            section=(
                "# --- Neon PostgreSQL (same value as the repository root .env) -----------------"
            ),
        )
        env.set(
            "INTERNAL_API_SECRET",
            internal,
            section=(
                "# --- Shared with the backend as ExternalAuth__InternalSecret --------------------"
            ),
        )
        env.set("PORT", self.plain(env, "PORT", "3001"))

        # scripts/seed.ts seeds exactly sys-001 and sys-002, numbered 1-based. A Pi key here that
        # differs from the backend's PiAuth key would desynchronize that Pi on the next seed, so
        # both slots are always written from the root .env keys (see managed_systems).
        seed_section = (
            "# --- Factory-kit seed values (`npm run seed`) -------------------------------------\n"
            "# SEED_PI_API_KEY_SYS_00N must equal the backend's PiAuth key for the same system,\n"
            "# otherwise a re-seed desynchronizes that Pi from the backend."
        )
        for index, system in enumerate(SEEDED_SYSTEMS, start=1):
            env.set(
                f"SEED_CAM_ADMIN_{index}_PASSWORD",
                self.seed_password(env, index),
                section=seed_section if index == 1 else None,
            )
            env.set(f"SEED_PI_API_KEY_SYS_{index:03d}", self.pi_key(system))

        unsupported = [s for s in self.o.systems if s not in SEEDED_SYSTEMS]
        if unsupported:
            self.report.notes.append(
                "external-auth-service/scripts/seed.ts only seeds sys-001 and sys-002; extend its "
                "`kits` array for " + ", ".join(unsupported) + "."
            )

    def managed_systems(self) -> list[str]:
        """Requested systems plus the factory kits seed.ts always creates."""
        systems = list(self.o.systems)
        for seeded in SEEDED_SYSTEMS:
            if seeded not in systems:
                systems.append(seeded)
        return systems

    def seed_password(self, env: EnvFile, index: int) -> str:
        return self.secret(env, f"SEED_CAM_ADMIN_{index}_PASSWORD", new_secret)

    def default_system(self) -> str:
        """System that owns the legacy PiAuth__ApiKey (read after the root .env is provisioned)."""
        value = self.root_env.get("PiAuth__DefaultSystemId")
        if value and not is_placeholder(value):
            return value
        return DEFAULT_LEGACY_SYSTEM

    def pi_key(self, system: str) -> str:
        if system == self.default_system():
            return self.root_env.get("PiAuth__ApiKey") or ""
        return self.root_env.get(f"PiAuth__Systems__{system}") or ""

    # ---- frontend/.env.local ----------------------------------------------------------------
    def provision_frontend_env(self) -> None:
        env = self.frontend_env
        root = self.root_env
        mediamtx = root.get("MEDIAMTX_PUBLIC_URL") or ""
        mqtt_ws = root.get("MQTT_PUBLIC_WS_URL") or ""

        env.set(
            "BACKEND_URL",
            self.plain(env, "BACKEND_URL", DEFAULT_LOCAL_BACKEND),
            section=(
                "# --- Local development (`next dev` on the host) -------------------------------\n"
                f"# {GENERATED_MARKER}. Gitignored. Docker Compose supplies its own environment\n"
                "# block and ignores this file."
            ),
        )
        env.set(
            "BACKEND_DEFAULT_SYSTEM_ID",
            self.plain(env, "BACKEND_DEFAULT_SYSTEM_ID", self.o.systems[0]),
        )
        # These three must match the root .env exactly.
        env.set("BFF_SESSION_SECRET", root.get("BFF_SESSION_SECRET") or "")
        env.set("INTERNAL_API_SECRET", root.get("INTERNAL_API_SECRET") or "")
        env.set(
            "EXTERNAL_AUTH_URL",
            self.plain(env, "EXTERNAL_AUTH_URL", self.external_auth_url()),
        )
        # The legacy frontend/.env is only noted, never deleted.
        if self.legacy_frontend_env is not None:
            self.report.notes.append(
                f"{self.rel(self.legacy_frontend_env.path)} is still present and Next.js now "
                "prefers .env.local; delete it once the generated values look right."
            )
        env.set(
            "MEDIAMTX_PUBLIC_URL",
            self.plain(env, "MEDIAMTX_PUBLIC_URL", mediamtx),
            section=(
                "# --- Browser-facing realtime endpoints (mirrors the root .env) ------------------"
            ),
        )
        env.set("NEXT_PUBLIC_MEDIAMTX_URL", self.plain(env, "NEXT_PUBLIC_MEDIAMTX_URL", mediamtx))
        env.set("MQTT_PUBLIC_WS_URL", self.plain(env, "MQTT_PUBLIC_WS_URL", mqtt_ws))
        env.set(
            "MQTT_PUBLIC_USER",
            self.plain(
                env, "MQTT_PUBLIC_USER", root.get("MQTT_CLIENT_USER") or DEFAULT_MQTT_CLIENT_USER
            ),
        )
        env.set("MQTT_PUBLIC_PASS", root.get("MQTT_CLIENT_PASS") or "")
        env.set("COOKIE_SECURE", self.plain(env, "COOKIE_SECURE", "false"))

    def bff_secret_fallback(self) -> str:
        """Reuse the legacy frontend/.env session secret when it is usable."""
        if self.legacy_frontend_env is not None:
            legacy = self.legacy_frontend_env.get("BFF_SESSION_SECRET")
            if legacy and not is_placeholder(legacy) and len(legacy.encode("utf-8")) >= 32:
                return legacy
        return new_secret()

    def external_auth_url(self) -> str:
        """Server-side external-auth target for `next dev` on the host.

        A deployed Cloudflare Worker URL from the legacy frontend/.env wins, then the backend's
        own ExternalAuth__BaseUrl; otherwise the locally published compose service.
        """
        if self.legacy_frontend_env is not None:
            legacy = self.legacy_frontend_env.get("EXTERNAL_AUTH_URL")
            if legacy and not is_placeholder(legacy):
                return legacy
        value = self.root_env.get("ExternalAuth__BaseUrl")
        if value and not is_placeholder(value):
            return value
        return DEFAULT_LOCAL_EXTERNAL_AUTH

    # ---- dist/devices/<id>/pi.env -----------------------------------------------------------
    def lan_host(self) -> str:
        if self._lan_cache:
            return self._lan_cache
        if self.o.lan_host:
            self._lan_cache = self.o.lan_host
            return self._lan_cache

        # Reuse whatever the operator already set for RTSP ingest or the ESP32 broker. Values are
        # only trusted from files that were already generated: the example config.h ships
        # `192.168.1.10`, which would otherwise be adopted as a real address.
        for system in self.o.systems:
            existing = self.pi_envs[system].get("RTSP_HOST")
            if existing and not is_placeholder(existing):
                self._lan_cache = existing
                return existing
            if system in self.from_template:
                continue
            defined = find_define(self.config_headers.get(system, ""), "MQTT_HOST")
            if defined and not is_placeholder(defined):
                self._lan_cache = defined
                return defined

        for candidate in (
            url_host(self.root_env.get("MEDIAMTX_PUBLIC_URL")),
            url_host(self.root_env.get("MQTT_PUBLIC_WS_URL")),
        ):
            if candidate:
                self._lan_cache = candidate
                return candidate

        self._lan_cache = self.ask(
            "LAN address of this machine (Pi RTSP ingest + ESP32 MQTT broker)",
            DEFAULT_LAN_HOST,
        ) or DEFAULT_LAN_HOST
        return self._lan_cache

    def wifi(self) -> tuple[str, str]:
        if self._wifi_cache:
            return self._wifi_cache
        if self.o.wifi_ssid is not None and self.o.wifi_password is not None:
            self._wifi_cache = (self.o.wifi_ssid, self.o.wifi_password)
            return self._wifi_cache

        ssid = password = ""
        for system in self.o.systems:
            if system in self.from_template:
                continue
            text = self.config_headers.get(system, "")
            found_ssid = find_define(text, "WIFI_SSID")
            found_password = find_define(text, "WIFI_PASSWORD")
            if not ssid and found_ssid and not is_placeholder(found_ssid):
                ssid = found_ssid
            if not password and found_password and not is_placeholder(found_password):
                password = found_password
            if ssid and password:
                break

        if not ssid:
            ssid = self.ask("Wi-Fi SSID for the ESP32 terminal(s)", "")
        if not password:
            password = self.ask("Wi-Fi password for the ESP32 terminal(s)", "", secret=True)
        if not ssid or not password:
            warn(
                "Wi-Fi SSID/password are empty; the generated config.h will not connect until "
                "WIFI_SSID and WIFI_PASSWORD are filled in."
            )
        self._wifi_cache = (ssid, password)
        return self._wifi_cache

    def backend_host(self) -> str:
        return self.root_env.get("TS_HOSTNAME") or DEFAULT_TS_HOSTNAME

    def provision_pi_env(self, system: str) -> None:
        env = self.pi_envs[system]
        backend_host = self.backend_host()
        lan_host = self.lan_host()
        key = self.pi_key(system)

        env.set(
            "BACKEND_HOST",
            self.plain(env, "BACKEND_HOST", backend_host),
            section=(
                f"# {GENERATED_MARKER} for {system}. Gitignored, mode 0600.\n"
                "# Install as /etc/security-camera-pi.env (systemd EnvironmentFile)."
            ),
        )
        env.set(
            "BACKEND_URL",
            self.plain(env, "BACKEND_URL", f"http://{backend_host}:{DEFAULT_BACKEND_PORT}"),
        )
        env.set("SYSTEM_ID", system)
        env.set("PI_API_KEY", key)
        env.set("API_KEY", key)
        env.set("STREAM_MODE", self.plain(env, "STREAM_MODE", "webrtc"))
        env.set(
            "RTSP_HOST",
            self.plain(env, "RTSP_HOST", lan_host),
            section=(
                "# --- MediaMTX RTSP ingest (host port 8554 in compose.yaml) --------------------\n"
                "# The streamer builds\n"
                "# rtsp://<RTSP_USER>:<API_KEY>@<RTSP_HOST>:<RTSP_PORT>/<SYSTEM_ID>."
            ),
        )
        env.set("RTSP_PORT", self.plain(env, "RTSP_PORT", str(DEFAULT_RTSP_PORT)))
        env.set("RTSP_USER", self.plain(env, "RTSP_USER", "pi"))
        env.set("STREAM_WIDTH", self.plain(env, "STREAM_WIDTH", "1280"))
        env.set("STREAM_HEIGHT", self.plain(env, "STREAM_HEIGHT", "720"))
        env.set("STREAM_FPS", self.plain(env, "STREAM_FPS", "15"))
        env.set("STREAM_BITRATE_KBPS", self.plain(env, "STREAM_BITRATE_KBPS", "2500"))
        env.set("STREAM_FFMPEG_ENCODER", self.plain(env, "STREAM_FFMPEG_ENCODER", "h264_v4l2m2m"))
        env.set("FFMPEG_PATH", self.plain(env, "FFMPEG_PATH", "ffmpeg"))
        env.set(
            "PREVIEW_ENABLED",
            self.plain(env, "PREVIEW_ENABLED", "true"),
            section=(
                "# --- Motion detection / preview / offline queue ---------------------------------"
            ),
        )
        env.set("PREVIEW_INTERVAL", self.plain(env, "PREVIEW_INTERVAL", "1"))
        env.set("MOTION_MIN_AREA", self.plain(env, "MOTION_MIN_AREA", "1500"))
        env.set("COOLDOWN_SECONDS", self.plain(env, "COOLDOWN_SECONDS", "8"))
        env.set("QUEUE_MAX_MB", self.plain(env, "QUEUE_MAX_MB", "500"))

    # ---- dist/devices/<id>/config.h ---------------------------------------------------------
    def provision_config_header(self, system: str) -> None:
        text = self.config_headers[system]
        backend_host = self.backend_host()
        lan_host = self.lan_host()
        ssid, wifi_password = self.wifi()
        mqtt_ws = self.root_env.get("MQTT_PUBLIC_WS_URL") or ""
        use_tls = mqtt_ws.startswith(("wss://", "mqtts://"))
        # mosquitto/acl only grants the `dashboard` client read access to systems/#.
        mqtt_user = self.root_env.get("MQTT_CLIENT_USER") or DEFAULT_MQTT_CLIENT_USER

        replacements = [
            ("WIFI_SSID", f'"{c_string(ssid)}"'),
            ("WIFI_PASSWORD", f'"{c_string(wifi_password)}"'),
            ("SYSTEM_ID", f'"{c_string(system)}"'),
            ("MQTT_HOST", f'"{c_string(lan_host)}"'),
            ("MQTT_PORT", "8883" if use_tls else "1883"),
            ("MQTT_USE_TLS", "true" if use_tls else "false"),
            ("MQTT_USERNAME", f'"{c_string(mqtt_user)}"'),
            ("MQTT_PASSWORD", f'"{c_string(self.root_env.get("MQTT_CLIENT_PASS") or "")}"'),
            (
                "FALLBACK_STATUS_URL",
                f'"http://{c_string(backend_host)}:{DEFAULT_BACKEND_PORT}/api/device/status"',
            ),
            (
                "FALLBACK_API_KEY",
                f'"{c_string(self.root_env.get(f"PiAuth__AlertTerminalSystems__{system}") or "")}"',
            ),
        ]
        for name, rendered in replacements:
            text = replace_define(text, name, rendered)

        if GENERATED_MARKER not in text.split("\n", 1)[0]:
            text = (
                f"// {GENERATED_MARKER} for system {system} — DO NOT COMMIT.\n"
                "// Source: esp32-alert-terminal/include/config.example.h. Wi-Fi, SYSTEM_ID, MQTT\n"
                "// credentials and the HTTP fallback are managed; pins and tuning stay editable.\n"
                "// Flash with: scripts/init_env.py --flash-esp32 --system "
                f"{system}\n\n" + text
            )
        self.config_headers[system] = text


def note(message: str) -> None:
    print(f"note: {message}")


# ---------------------------------------------------------------------------------------------
# Verification
# ---------------------------------------------------------------------------------------------


def verify_forbidden_values(
    root: Path, env_files: list[EnvFile], headers: dict[str, str]
) -> list[str]:
    """Report every value `ProductionSecretValidator` would refuse to boot with."""
    problems: list[str] = []
    for env in env_files:
        for key, value in env.items():
            if is_forbidden_value(value):
                problems.append(
                    f"{os.path.relpath(env.path, root)}: {key} still holds a development "
                    f"placeholder ('{value}')"
                )
    for system, text in headers.items():
        for name in CONFIG_H_STRING_DEFINES:
            value = find_define(text, name)
            if is_forbidden_value(value):
                problems.append(
                    f"dist/devices/{system}/config.h: {name} still holds a placeholder "
                    f"('{value}')"
                )
    return problems


def _git(root: Path, *args: str) -> subprocess.CompletedProcess[str] | None:
    try:
        return subprocess.run(
            ["git", "-C", str(root), *args], capture_output=True, text=True, check=False
        )
    except (FileNotFoundError, OSError):
        return None


def is_git_repo(root: Path) -> bool:
    result = _git(root, "rev-parse", "--is-inside-work-tree")
    return bool(result and result.returncode == 0 and result.stdout.strip() == "true")


def git_ignores(root: Path, path: Path) -> bool | None:
    """True/False when git can answer, None when git or the repository is unavailable."""
    if not is_git_repo(root):
        return None
    result = _git(root, "check-ignore", "-q", "--no-index", "--", os.path.relpath(path, root))
    return None if result is None else result.returncode == 0


GITIGNORE_BLOCK = (
    "# --- generated by scripts/init_env.py -------------------------------------------------\n"
    "# Per-device provisioning artifacts contain Pi API keys and Wi-Fi passwords.\n"
    "/dist/\n"
)


def ensure_gitignore(root: Path, paths: list[Path], *, fix: bool) -> list[str]:
    """Verify (and with `fix`, repair) that every generated secret file is gitignored."""
    if not is_git_repo(root):
        print("note: not a git repository; .gitignore coverage was not verified")
        return []

    exposed = [p for p in paths if git_ignores(root, p) is False]
    if not exposed:
        return []

    if not fix:
        return [
            f"{os.path.relpath(p, root)} is NOT excluded by .gitignore "
            "(re-run without --check to append /dist/)"
            for p in exposed
        ]

    gitignore = root / ".gitignore"
    text = gitignore.read_text(encoding="utf-8") if gitignore.exists() else ""
    if "/dist/" not in text.splitlines():
        if text and not text.endswith("\n"):
            text += "\n"
        gitignore.write_text(
            text + ("\n" if text.strip() else "") + GITIGNORE_BLOCK, encoding="utf-8"
        )
        print(f"updated {os.path.relpath(gitignore, root)}: added /dist/")

    problems: list[str] = []
    for path in exposed:
        if git_ignores(root, path) is not True:
            problems.append(
                f"{os.path.relpath(path, root)} is still not gitignored; refusing to leave a "
                "secret file exposed"
            )
    return problems


# ---------------------------------------------------------------------------------------------
# Optional device actions
# ---------------------------------------------------------------------------------------------


def push_pi(root: Path, target: str, system: str, service: str) -> None:
    source = root / "dist" / "devices" / system / "pi.env"
    if not source.exists():
        die(f"missing {source}; run this script without --push-pi first")
    if shutil.which("scp") is None or shutil.which("ssh") is None:
        die("scp and ssh must be in PATH")

    remote_tmp = f"/tmp/security-camera-pi-{system}.env"
    print(f"scp {os.path.relpath(source, root)} -> {target}:{remote_tmp}")
    subprocess.run(["scp", "-q", str(source), f"{target}:{remote_tmp}"], check=True)

    remote = (
        f"sudo install -m 600 -o root -g pi {remote_tmp} /etc/security-camera-pi.env"
        f" && rm -f {remote_tmp}"
        f" && sudo systemctl restart {service}.service"
    )
    print(f"ssh {target} '{remote}'")
    subprocess.run(["ssh", target, remote], check=True)
    print(f"restarted {service}.service on {target}")


def flash_esp32(root: Path, system: str) -> None:
    esp32_dir = root / "esp32-alert-terminal"
    source = root / "dist" / "devices" / system / "config.h"
    if not source.exists():
        die(f"missing {source}; run this script without --flash-esp32 first")
    target = esp32_dir / "include" / "config.h"
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    os.chmod(target, 0o600)
    print(f"copied {os.path.relpath(source, root)} -> {os.path.relpath(target, root)}")

    if shutil.which("pio") is None:
        die("`pio` (PlatformIO) not found in PATH; the config was copied but not flashed")
    print(f"pio run -t upload (cwd={os.path.relpath(esp32_dir, root)})")
    subprocess.run(["pio", "run", "-t", "upload"], cwd=str(esp32_dir), check=True)


# ---------------------------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------------------------


def parse_systems(raw: str) -> list[str]:
    systems: list[str] = []
    for chunk in raw.split(","):
        system = chunk.strip()
        if not system:
            continue
        if not SYSTEM_ID_RE.match(system):
            die(
                f"invalid system id {system!r}: must match {SYSTEM_ID_RE.pattern} "
                "(the same rule frontend/lib/edge-router.ts enforces)"
            )
        if system not in systems:
            systems.append(system)
    if not systems:
        die("--systems must name at least one system id")
    return systems


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="scripts/init_env.py",
        description="Generate and synchronize every environment/config file in this repository.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Examples:\n"
            "  scripts/init_env.py\n"
            "  scripts/init_env.py --non-interactive --systems sys-001,sys-002\n"
            "  scripts/init_env.py --rotate\n"
            "  scripts/init_env.py --check\n"
            "  scripts/init_env.py --push-pi pi@cam-001 --system sys-001\n"
            "  scripts/init_env.py --flash-esp32 --system sys-001\n"
        ),
    )
    parser.add_argument(
        "--systems",
        default=DEFAULT_SYSTEMS,
        metavar="IDS",
        help=f"comma-separated camera system ids (default: {DEFAULT_SYSTEMS})",
    )
    parser.add_argument(
        "--rotate",
        action="store_true",
        help="regenerate every secret: JWT, BFF session key, internal API secret, MQTT "
        "passwords, Pi/terminal keys and factory-admin seed passwords",
    )
    parser.add_argument(
        "--non-interactive",
        "-y",
        action="store_true",
        help="never prompt; keep existing values and use built-in defaults",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify existing files (permissions, gitignore, placeholders, cross-file secrets) "
        "without writing anything",
    )
    parser.add_argument(
        "--repo-root",
        default=str(Path(__file__).resolve().parent.parent),
        metavar="DIR",
        help="repository root (default: the parent of this script)",
    )
    parser.add_argument(
        "--push-pi",
        metavar="USER@HOST",
        help="after generating, scp the system's pi.env to the Pi and restart its service",
    )
    parser.add_argument(
        "--service",
        default=DEFAULT_PI_SERVICE,
        metavar="NAME",
        help=f"systemd unit restarted by --push-pi (default: {DEFAULT_PI_SERVICE})",
    )
    parser.add_argument(
        "--flash-esp32",
        action="store_true",
        help="copy the system's config.h into esp32-alert-terminal/include/ and run "
        "`pio run -t upload`",
    )
    parser.add_argument(
        "--system",
        metavar="ID",
        help="target system for --push-pi / --flash-esp32 (default: first --systems entry)",
    )
    parser.add_argument("--lan-host", metavar="HOST", help="LAN address of this machine (Pi/ESP32)")
    parser.add_argument("--wifi-ssid", metavar="SSID", help="Wi-Fi SSID for the ESP32 terminals")
    parser.add_argument(
        "--wifi-password", metavar="PASS", help="Wi-Fi password for the ESP32 terminals"
    )
    return parser


def print_report(provisioner: Provisioner) -> None:
    report = provisioner.report
    print()
    print("Files written (mode 600)")
    for path in report.files:
        print(f"  {provisioner.rel(path)}")
    if report.created:
        print()
        print("New values")
        for path, key in report.created:
            print(f"  {path}: {key}")
    if report.rotated:
        print()
        print("Rotated secrets")
        for path, key in report.rotated:
            print(f"  {path}: {key}")
    if report.notes:
        print()
        print("Notes")
        for text in report.notes:
            print(f"  - {text}")


def print_rotation_warnings(root: Path, provisioner: Provisioner) -> None:
    rotated_keys = {key for _, key in provisioner.report.rotated}
    created_keys = {key for _, key in provisioner.report.created}
    mqtt_keys = {"MQTT_BACKEND_PASS", "MQTT_CLIENT_PASS"}
    mqtt_changed = bool(rotated_keys & mqtt_keys)
    mqtt_created = bool((created_keys & mqtt_keys) - rotated_keys)
    stale_volume = mqtt_created and mosquitto_volume_exists()

    if not provisioner.report.rotated and not stale_volume:
        return

    print()
    if provisioner.report.rotated:
        print("!! Secrets were rotated. Follow-up required:")
    else:
        print("!! Follow-up required:")
    if mqtt_changed:
        print("   - MQTT passwords changed. The broker hashes them only on FIRST start, so the")
        print("     volume must be recreated or every client fails to authenticate:")
        print("       docker compose rm -sf mosquitto")
        print(f"       docker volume rm {MOSQUITTO_VOLUME}")
        print("       docker compose up -d mosquitto")
    elif stale_volume:
        print(f"   - MQTT passwords were just generated, but the {MOSQUITTO_VOLUME} volume already")
        print("     exists with older credentials. Recreate it before starting the broker:")
        print("       docker compose rm -sf mosquitto")
        print(f"       docker volume rm {MOSQUITTO_VOLUME}")
    if not provisioner.report.rotated:
        return
    print("   - INTERNAL_API_SECRET changed: restart external-auth-service and update the")
    print("     Cloudflare Worker secret (`cd external-auth-service && npx wrangler secret put")
    print("     INTERNAL_API_SECRET`).")
    print("   - BFF_SESSION_SECRET changed: every bff_session cookie is invalid; users must")
    print("     sign in again.")
    print("   - Pi / terminal keys changed: re-push each pi.env (--push-pi) and re-flash each")
    print("     ESP32 (--flash-esp32).")
    print("   - Factory-admin seed passwords changed: re-run")
    print("     `npm --prefix external-auth-service run seed`.")


def mosquitto_volume_exists() -> bool:
    """True when the broker's data volume is already present (best effort, needs docker)."""
    if shutil.which("docker") is None:
        return False
    try:
        result = subprocess.run(
            ["docker", "volume", "inspect", MOSQUITTO_VOLUME],
            capture_output=True,
            text=True,
            check=False,
        )
    except OSError:
        return False
    return result.returncode == 0


def print_credentials(provisioner: Provisioner) -> None:
    env = provisioner.root_env
    print()
    print(f"Credentials stored in {provisioner.rel(env.path)}")
    device_code = env.get("Admin__DeviceCode")
    if device_code:
        print(f"  built-in admin initial password (Admin__DeviceCode): {device_code}")
    for system in provisioner.o.systems:
        key = provisioner.pi_key(system)
        if system in SEEDED_SYSTEMS:
            username = f"cam_admin_{SEEDED_SYSTEMS.index(system) + 1}"
            print(f"  {system}: factory admin {username!r}, Pi API key {key}")
        else:
            print(f"  {system}: Pi API key {key}")


def print_next_steps(provisioner: Provisioner, args: argparse.Namespace) -> None:
    first = provisioner.o.systems[0]
    print()
    print("Next steps")
    print("  docker compose up -d --build")
    print("  npm --prefix external-auth-service run seed")
    if not args.push_pi:
        print(f"  scripts/init_env.py --push-pi pi@<host> --system {first}")
    if not args.flash_esp32:
        print(f"  scripts/init_env.py --flash-esp32 --system {first}")


def run_check(root: Path, systems: list[str]) -> int:
    problems: list[str] = []

    root_env = EnvFile(root / ".env")
    auth_env = EnvFile(root / "external-auth-service" / ".env")
    frontend_env = EnvFile(root / "frontend" / ".env.local")
    pi_envs = {s: EnvFile(root / "dist" / "devices" / s / "pi.env") for s in systems}
    headers: dict[str, str] = {}
    for system in systems:
        path = root / "dist" / "devices" / system / "config.h"
        headers[system] = path.read_text(encoding="utf-8") if path.exists() else ""

    paths = [
        root_env.path,
        auth_env.path,
        frontend_env.path,
        *(env.path for env in pi_envs.values()),
        *(root / "dist" / "devices" / s / "config.h" for s in systems),
    ]

    existing: list[Path] = []
    for path in paths:
        if not path.exists():
            problems.append(f"missing: {os.path.relpath(path, root)}")
            continue
        existing.append(path)
        mode = path.stat().st_mode & 0o777
        if mode != 0o600:
            problems.append(f"{os.path.relpath(path, root)}: mode {mode:o} (expected 600)")

    env_files = [root_env, auth_env, frontend_env, *pi_envs.values()]
    problems += verify_forbidden_values(root, env_files, headers)
    for env in env_files:
        for key in env.duplicate_keys():
            # Compose/dotenv take the last value, so a leftover duplicate silently wins.
            problems.append(
                f"{os.path.relpath(env.path, root)}: {key} is defined more than once"
            )

    def mismatch(path: Path, key: str, shared: str, expected: str | None) -> None:
        if path.exists() and (shared or "") != (expected or ""):
            problems.append(
                f"{os.path.relpath(path, root)}: {key} does not match the root .env value"
            )

    internal = root_env.get("INTERNAL_API_SECRET")
    mismatch(
        auth_env.path, "INTERNAL_API_SECRET", auth_env.get("INTERNAL_API_SECRET"), internal
    )
    mismatch(
        frontend_env.path,
        "INTERNAL_API_SECRET",
        frontend_env.get("INTERNAL_API_SECRET"),
        internal,
    )
    mismatch(
        frontend_env.path,
        "BFF_SESSION_SECRET",
        frontend_env.get("BFF_SESSION_SECRET"),
        root_env.get("BFF_SESSION_SECRET"),
    )
    mismatch(
        auth_env.path, "DATABASE_URL", auth_env.get("DATABASE_URL"), root_env.get("NEON_DB_URL")
    )

    if (root_env.get("ExternalAuth__InternalSecret") or "") != (internal or ""):
        problems.append(".env: ExternalAuth__InternalSecret does not match INTERNAL_API_SECRET")
    if is_placeholder_dsn(root_env.get("NEON_DB_URL")):
        problems.append(".env: NEON_DB_URL still holds the sample connection string")
    if is_placeholder_dsn(root_env.get("DB_URL")):
        problems.append(".env: DB_URL still holds the sample connection string")
    if is_placeholder_dsn(auth_env.get("DATABASE_URL")):
        problems.append(
            "external-auth-service/.env: DATABASE_URL still holds the sample connection string"
        )
    # PiAuth__ApiKey always belongs to PiAuth__DefaultSystemId, which is not necessarily the
    # first --systems entry.
    default_system = root_env.get("PiAuth__DefaultSystemId") or DEFAULT_LEGACY_SYSTEM
    if root_env.has(f"PiAuth__Systems__{default_system}"):
        problems.append(
            f".env: PiAuth__Systems__{default_system} duplicates the legacy "
            "PiAuth__ApiKey/PiAuth__DefaultSystemId key"
        )
    if (root_env.get("ExternalAuth__UseStub") or "false").lower() == "true":
        problems.append(".env: ExternalAuth__UseStub must be false")

    mismatch(
        frontend_env.path,
        "MQTT_PUBLIC_PASS",
        frontend_env.get("MQTT_PUBLIC_PASS"),
        root_env.get("MQTT_CLIENT_PASS"),
    )

    for system in systems:
        env = pi_envs[system]
        if env.existed:
            expected = (
                root_env.get("PiAuth__ApiKey")
                if system == default_system
                else root_env.get(f"PiAuth__Systems__{system}")
            )
            mismatch(env.path, "API_KEY", env.get("API_KEY"), expected)
            mismatch(env.path, "PI_API_KEY", env.get("PI_API_KEY"), env.get("API_KEY"))
            if env.get("SYSTEM_ID") != system:
                problems.append(
                    f"{os.path.relpath(env.path, root)}: SYSTEM_ID is {env.get('SYSTEM_ID')!r}, "
                    f"expected {system!r}"
                )

        # The flashed header must carry the same tenant, credential and fallback key.
        text = headers.get(system, "")
        if not text:
            continue
        header_path = os.path.relpath(root / "dist" / "devices" / system / "config.h", root)
        if find_define(text, "SYSTEM_ID") != system:
            problems.append(
                f"{header_path}: SYSTEM_ID is {find_define(text, 'SYSTEM_ID')!r}, "
                f"expected {system!r}"
            )
        if find_define(text, "MQTT_PASSWORD") != (root_env.get("MQTT_CLIENT_PASS") or ""):
            problems.append(f"{header_path}: MQTT_PASSWORD does not match the root .env value")
        terminal_key = root_env.get(f"PiAuth__AlertTerminalSystems__{system}") or ""
        if find_define(text, "FALLBACK_API_KEY") != terminal_key:
            problems.append(
                f"{header_path}: FALLBACK_API_KEY does not match "
                f"PiAuth__AlertTerminalSystems__{system}"
            )

    problems += ensure_gitignore(root, existing, fix=False)

    if problems:
        print(f"check failed: {len(problems)} problem(s)", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    verified = "mode 600, no placeholders"
    if is_git_repo(root):
        verified += ", gitignored"
    print(f"check OK: {len(paths)} file(s) verified ({verified})")
    return 0


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    root = Path(args.repo_root).resolve()
    if not (root / "compose.yaml").exists():
        die(f"{root} does not look like the repository root (no compose.yaml)")

    systems = parse_systems(args.systems)

    if args.check:
        return run_check(root, systems)

    interactive = not args.non_interactive
    if interactive and not sys.stdin.isatty():
        print("note: stdin is not a TTY; running non-interactively")
        interactive = False

    target = args.system or systems[0]
    if target not in systems:
        die(f"--system {target!r} is not listed in --systems ({', '.join(systems)})")

    options = Options(
        root=root,
        systems=systems,
        rotate=args.rotate,
        interactive=interactive,
        lan_host=args.lan_host,
        wifi_ssid=args.wifi_ssid,
        wifi_password=args.wifi_password,
    )
    provisioner = Provisioner(options)
    provisioner.run()
    provisioner.write_all()

    env_files = [
        provisioner.root_env,
        provisioner.auth_env,
        provisioner.frontend_env,
        *provisioner.pi_envs.values(),
    ]
    problems = verify_forbidden_values(root, env_files, provisioner.config_headers)
    problems += ensure_gitignore(root, provisioner.report.files, fix=True)
    for path in provisioner.report.files:
        mode = path.stat().st_mode & 0o777
        if mode != 0o600:
            problems.append(f"{provisioner.rel(path)}: mode {mode:o} (expected 600)")
    if problems:
        for problem in problems:
            print(f"error: {problem}", file=sys.stderr)
        return 1

    print_report(provisioner)
    print_rotation_warnings(root, provisioner)
    print_credentials(provisioner)
    print_next_steps(provisioner, args)

    try:
        if args.push_pi:
            push_pi(root, args.push_pi, target, args.service)
        if args.flash_esp32:
            flash_esp32(root, target)
    except subprocess.CalledProcessError as exc:
        die(f"device action failed: {exc.cmd[0]} exited with {exc.returncode}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
