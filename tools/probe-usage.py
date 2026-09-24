#!/usr/bin/env python3
"""
probe-usage.py - one-shot read of your Claude org usage / spend / credit.

Run it yourself:
    python tools\\probe-usage.py
    python tools\\probe-usage.py --raw     # also dump full JSON (still no cookie)

What it does, all locally and read-only:
  1. Reads the DPAPI-wrapped AES key from Claude Desktop's "Local State".
  2. Decrypts the claude.ai session cookies from the local cookie store
     (Chromium v10 AES-GCM). The cookie VALUE is never printed.
  3. Calls a few org endpoints and prints the money / limit / reset fields.

No third-party packages required: SQLite is via the stdlib, and all Windows
crypto is done through ctypes (DPAPI + BCrypt), so nothing to pip install.
The only network call is the same HTTPS request to claude.ai that Claude
Desktop itself already makes.
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import json
import os
import shutil
import sqlite3
import ssl
import sys
import tempfile
import urllib.request
from ctypes import wintypes

BASE = "https://claude.ai"
FALLBACK_ORG = "7c1cc965-1604-4980-8572-eb6072624100"  # id seen in the local install


def find_claude_dir() -> str | None:
    """
    Locate the directory holding the LIVE cookie store. The Store/MSIX install
    keeps everything under %LOCALAPPDATA%\\Packages\\Claude_*\\LocalCache\\Roaming\\
    Claude, NOT under %APPDATA%\\Claude, so we check both and take whichever has
    a Local State + Network\\Cookies pair, most-recently-written first.
    """
    candidates: list[str] = []

    local = os.environ.get("LOCALAPPDATA", "")
    packages = os.path.join(local, "Packages")
    if os.path.isdir(packages):
        for name in os.listdir(packages):
            if name.startswith("Claude_"):
                candidates.append(
                    os.path.join(packages, name, "LocalCache", "Roaming", "Claude"))

    roaming = os.environ.get("APPDATA", "")
    if roaming:
        candidates.append(os.path.join(roaming, "Claude"))

    valid: list[tuple[float, str]] = []
    for d in candidates:
        local_state = os.path.join(d, "Local State")
        cookies = os.path.join(d, "Network", "Cookies")
        if os.path.exists(local_state) and os.path.exists(cookies):
            valid.append((os.path.getmtime(local_state), d))

    valid.sort(reverse=True)
    return valid[0][1] if valid else None


# --------------------------------------------------------------------------
# Windows crypto via ctypes (no external deps)
# --------------------------------------------------------------------------
class _DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def dpapi_unprotect(data: bytes) -> bytes:
    """CryptUnprotectData under the current user."""
    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    buf = ctypes.create_string_buffer(data, len(data))
    blob_in = _DATA_BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))
    blob_out = _DATA_BLOB()
    if not crypt32.CryptUnprotectData(
        ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)
    ):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        kernel32.LocalFree(blob_out.pbData)


class _BCRYPT_AUTH_INFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.ULONG),
        ("dwInfoVersion", wintypes.ULONG),
        ("pbNonce", ctypes.POINTER(ctypes.c_ubyte)),
        ("cbNonce", wintypes.ULONG),
        ("pbAuthData", ctypes.POINTER(ctypes.c_ubyte)),
        ("cbAuthData", wintypes.ULONG),
        ("pbTag", ctypes.POINTER(ctypes.c_ubyte)),
        ("cbTag", wintypes.ULONG),
        ("pbMacContext", ctypes.POINTER(ctypes.c_ubyte)),
        ("cbMacContext", wintypes.ULONG),
        ("cbAAD", wintypes.ULONG),
        ("cbData", ctypes.c_ulonglong),
        ("dwFlags", wintypes.ULONG),
    ]


def aes_gcm_decrypt(key: bytes, nonce: bytes, ciphertext: bytes, tag: bytes) -> bytes:
    """AES-256-GCM via CNG/BCrypt."""
    bcrypt = ctypes.windll.bcrypt

    h_alg = ctypes.c_void_p()
    status = bcrypt.BCryptOpenAlgorithmProvider(ctypes.byref(h_alg), "AES", None, 0)
    if status != 0:
        raise OSError(f"BCryptOpenAlgorithmProvider failed: 0x{status & 0xFFFFFFFF:08X}")
    try:
        mode = ctypes.create_unicode_buffer("ChainingModeGCM")
        status = bcrypt.BCryptSetProperty(
            h_alg, "ChainingMode",
            ctypes.cast(mode, ctypes.POINTER(ctypes.c_ubyte)),
            len(mode) * ctypes.sizeof(ctypes.c_wchar), 0)
        if status != 0:
            raise OSError(f"BCryptSetProperty failed: 0x{status & 0xFFFFFFFF:08X}")

        h_key = ctypes.c_void_p()
        key_buf = (ctypes.c_ubyte * len(key)).from_buffer_copy(key)
        status = bcrypt.BCryptGenerateSymmetricKey(
            h_alg, ctypes.byref(h_key), None, 0, key_buf, len(key), 0)
        if status != 0:
            raise OSError(f"BCryptGenerateSymmetricKey failed: 0x{status & 0xFFFFFFFF:08X}")
        try:
            nonce_buf = (ctypes.c_ubyte * len(nonce)).from_buffer_copy(nonce)
            tag_buf = (ctypes.c_ubyte * len(tag)).from_buffer_copy(tag)
            info = _BCRYPT_AUTH_INFO()
            info.cbSize = ctypes.sizeof(_BCRYPT_AUTH_INFO)
            info.dwInfoVersion = 1
            info.pbNonce = ctypes.cast(nonce_buf, ctypes.POINTER(ctypes.c_ubyte))
            info.cbNonce = len(nonce)
            info.pbTag = ctypes.cast(tag_buf, ctypes.POINTER(ctypes.c_ubyte))
            info.cbTag = len(tag)

            cipher_buf = (ctypes.c_ubyte * len(ciphertext)).from_buffer_copy(ciphertext)
            out = (ctypes.c_ubyte * len(ciphertext))()
            result = wintypes.ULONG(0)
            status = bcrypt.BCryptDecrypt(
                h_key, cipher_buf, len(ciphertext), ctypes.byref(info),
                None, 0, out, len(out), ctypes.byref(result), 0)
            if status != 0:
                raise OSError(f"BCryptDecrypt failed: 0x{status & 0xFFFFFFFF:08X}")
            return bytes(out[: result.value])
        finally:
            bcrypt.BCryptDestroyKey(h_key)
    finally:
        bcrypt.BCryptCloseAlgorithmProvider(h_alg, 0)


# --------------------------------------------------------------------------
# Cookie extraction
# --------------------------------------------------------------------------
def load_aes_key(local_state_path: str) -> bytes:
    with open(local_state_path, "r", encoding="utf-8") as fh:
        state = json.load(fh)
    b64 = state.get("os_crypt", {}).get("encrypted_key")
    if not b64:
        raise RuntimeError("no os_crypt.encrypted_key in Local State")
    blob = base64.b64decode(b64)
    if blob[:5] != b"DPAPI":
        raise RuntimeError("unexpected key prefix (app-bound v20 key not supported here)")
    return dpapi_unprotect(blob[5:])


def decrypt_cookie(enc: bytes, aes_key: bytes) -> str:
    prefix = enc[:3]
    if prefix in (b"v10", b"v20"):
        nonce = enc[3:15]
        ciphertext = enc[15:-16]
        tag = enc[-16:]
        plain = aes_gcm_decrypt(aes_key, nonce, ciphertext, tag)
        if prefix == b"v20" and len(plain) > 32:
            plain = plain[32:]  # app-bound: strip 32-byte header
        return plain.decode("utf-8", "replace")
    # Legacy DPAPI-only value.
    return dpapi_unprotect(enc).decode("utf-8", "replace")


def read_shared(path: str) -> bytes:
    """
    Read a file even while another process holds it open, by requesting
    FILE_SHARE_READ|WRITE|DELETE via CreateFileW. Python's plain open() asks for
    a share mode Chromium's lock on the live Cookies DB rejects.
    """
    GENERIC_READ = 0x80000000
    FILE_SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004
    OPEN_EXISTING = 3
    FILE_ATTRIBUTE_NORMAL = 0x80
    INVALID_HANDLE = ctypes.c_void_p(-1).value

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateFileW.restype = ctypes.c_void_p
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
        wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]

    handle = kernel32.CreateFileW(
        path, GENERIC_READ, FILE_SHARE_ALL, None,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None)
    if not handle or handle == INVALID_HANDLE:
        raise ctypes.WinError(ctypes.get_last_error())

    try:
        chunks: list[bytes] = []
        buf = ctypes.create_string_buffer(1024 * 1024)
        read = wintypes.DWORD(0)
        while True:
            ok = kernel32.ReadFile(
                ctypes.c_void_p(handle), buf, len(buf), ctypes.byref(read), None)
            if not ok:
                raise ctypes.WinError(ctypes.get_last_error())
            if read.value == 0:
                break
            chunks.append(buf.raw[: read.value])
        return b"".join(chunks)
    finally:
        kernel32.CloseHandle(ctypes.c_void_p(handle))


def read_cookies(cookies_db: str, aes_key: bytes) -> list[tuple[str, str]]:
    # Snapshot the DB and its WAL/journal sidecars (so the live session cookie,
    # which may still be in the WAL, is included) into a private temp dir.
    tmp_dir = tempfile.mkdtemp(prefix="usagebars-cookies-")
    tmp_db = os.path.join(tmp_dir, "Cookies")
    try:
        for suffix in ("", "-wal", "-journal"):
            src = cookies_db + suffix
            if os.path.exists(src):
                try:
                    with open(tmp_db + suffix, "wb") as out:
                        out.write(read_shared(src))
                except OSError as exc:
                    if suffix == "":
                        raise
                    # A missing/locked sidecar is non-fatal.
                    print(f"  (skipping {os.path.basename(src)}: {exc})")

        pairs: list[tuple[str, str]] = []
        con = sqlite3.connect(tmp_db)
        try:
            rows = con.execute(
                "select name, encrypted_value from cookies "
                "where host_key like '%claude.ai%'"
            ).fetchall()
        finally:
            con.close()

        for name, enc in rows:
            if not enc:
                continue
            try:
                pairs.append((name, decrypt_cookie(bytes(enc), aes_key)))
            except Exception as exc:  # noqa: BLE001
                print(f"  (could not decrypt cookie '{name}': {exc})")
        return pairs
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


# --------------------------------------------------------------------------
# API
# --------------------------------------------------------------------------
def make_getter(cookie_header: str):
    ctx = ssl.create_default_context()

    def get(path: str):
        url = f"{BASE}{path}"
        req = urllib.request.Request(
            url,
            headers={
                "Cookie": cookie_header,
                "User-Agent": "UsageBars-Probe/1.0",
                "Accept": "application/json",
                "anthropic-client-platform": "web_claude_ai",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=20, context=ctx) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            print(f"  GET {path} -> HTTP {exc.code}")
        except Exception as exc:  # noqa: BLE001
            print(f"  GET {path} -> {exc}")
        return None

    return get


def show(title: str, obj) -> None:
    print(f"\n{title}:")
    if obj is None:
        print("  (no data)")
        return
    print(json.dumps(obj, indent=2))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--claude-dir", default=None,
                    help="override the Claude data dir (auto-detected by default)")
    ap.add_argument("--raw", action="store_true", help="dump full JSON payloads")
    args = ap.parse_args()

    claude_dir = args.claude_dir or find_claude_dir()
    if not claude_dir:
        print("Could not find a Claude data dir with Local State + Network\\Cookies.")
        print("Checked the MSIX package path and %APPDATA%\\Claude.")
        print("Pass one explicitly with --claude-dir \"<path>\" if you know it.")
        return 1
    print(f"Claude data dir: {claude_dir}")

    local_state = os.path.join(claude_dir, "Local State")
    cookies_db = os.path.join(claude_dir, "Network", "Cookies")
    for path in (local_state, cookies_db):
        if not os.path.exists(path):
            print(f"Not found: {path}")
            return 1

    aes_key = load_aes_key(local_state)
    pairs = read_cookies(cookies_db, aes_key)
    have_session = any("session" in n.lower() for n, _ in pairs)
    print(f"Decrypted {len(pairs)} claude.ai cookie(s); session cookie present: {have_session}")
    if not have_session:
        print("No session cookie found - are you signed in to Claude Desktop?")
        return 1

    cookie_header = "; ".join(f"{n}={v}" for n, v in pairs)
    get = make_getter(cookie_header)

    org = FALLBACK_ORG
    orgs = get("/api/organizations")
    if isinstance(orgs, list):
        for o in orgs:
            if isinstance(o, dict) and o.get("uuid"):
                org = o["uuid"]
                break
    print(f"Org: {org}")

    usage = get(f"/api/organizations/{org}/usage")
    overage = get(f"/api/organizations/{org}/overage_spend_limit")
    prepaid = get(f"/api/organizations/{org}/prepaid_credit_balance")
    sub = get(f"/api/organizations/{org}/subscription_status")

    print("\n==================== SUMMARY ====================")
    if usage:
        eu = usage.get("extra_usage")
        if eu:
            print("extra_usage:")
            for k in ("is_enabled", "used_credits", "monthly_limit", "utilization"):
                print(f"  {k:<14}: {eu.get(k)}")
        else:
            print("extra_usage: (absent)")
        for w in ("five_hour", "seven_day", "seven_day_opus",
                  "seven_day_sonnet", "seven_day_cowork"):
            win = usage.get(w)
            if win:
                print(f"{w:<18}: util={win.get('utilization')}  resets_at={win.get('resets_at')}")
        limits = usage.get("limits")
        if limits:
            print("limits[]:")
            for lim in limits:
                print(f"  kind={lim.get('kind')} group={lim.get('group')} "
                      f"percent={lim.get('percent')} resets_at={lim.get('resets_at')}")

    show("overage_spend_limit", overage)
    show("prepaid_credit_balance", prepaid)
    show("subscription_status", sub)

    if args.raw:
        print("\n==================== RAW /usage ====================")
        print(json.dumps(usage, indent=2))

    print("\nDone. (Your cookie value was never printed.)")
    return 0


if __name__ == "__main__":
    if sys.platform != "win32":
        print("This probe is Windows-only (uses DPAPI/BCrypt).")
        sys.exit(2)
    sys.exit(main())
