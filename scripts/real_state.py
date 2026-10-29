"""The owner's real game data as the tests check it: the save folder and the registry key, before and after a run.

Saves are checked strictly. Left out of the comparison: what the engine itself keeps there for every run of any
copy (Unity's analytics and session files under Unity\\, Player.log), and the registry values Unity and the game use
for bookkeeping (session counters and ids, the last window size, the promo check time). The registry key is put back
as it was after a run, so a test window's size never reaches the owner's game."""
import json
import os
import re
import subprocess
import sys
import tempfile

LOCAL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL = os.path.join(os.environ["USERPROFILE"], "AppData", "LocalLow", "Mandragora", "Restory")
KEY = r"HKCU\Software\Mandragora\Restory"
BACKUP = os.path.join(tempfile.gettempdir(), "restory_reg_before_test.reg")
_IGNORED_FILES = re.compile(r"^(Unity[\\/]|Player(-prev)?\.log$)", re.IGNORECASE)
_IGNORED_VALUES = re.compile(r'^"(unity\.|unity_connect\.|Screenmanager |PromoLastRequestTicks)', re.IGNORECASE)


def fingerprint():
    out = os.path.join(tempfile.gettempdir(), "restory_real_fp.json")
    subprocess.run([sys.executable, "-I", os.path.join(LOCAL, "scripts", "fingerprint.py"), REAL, out], check=True, capture_output=True)
    with open(out) as fh:
        data = json.load(fh)
    return {k: v for k, v in data.items() if not _IGNORED_FILES.match(k)}


def _export(path):
    subprocess.run(["reg", "export", KEY, path, "/y"], check=True, capture_output=True)
    raw = open(path, "rb").read()
    text = raw.decode("utf-16") if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else raw.decode("utf-8", "replace")
    values, current = [], ""
    for line in text.splitlines():
        current += line.strip()
        if current.endswith("\\"):
            current = current[:-1]
            continue
        if current.startswith('"') and not _IGNORED_VALUES.match(current):
            values.append(current)
        current = ""
    return sorted(values)


def registry():
    return _export(os.path.join(tempfile.gettempdir(), "restory_reg_now.reg"))


def save_registry():
    """Before a run: keep the key to put it back afterwards; returns its values for the check."""
    return _export(BACKUP)


def restore_registry():
    if os.path.exists(BACKUP):
        subprocess.run(["reg", "import", BACKUP], capture_output=True)
