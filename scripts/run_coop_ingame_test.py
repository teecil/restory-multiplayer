"""Co-op started from inside a save, plus the join timeout and running in the background.
Prints RESULT: PASS|FAIL, exit 0 on pass.

Usage: python -I local/scripts/run_coop_ingame_test.py [--timeout 400]

The guest (copy E) starts first and without -versus-background: once the host's window opens it is unfocused,
so it only keeps running because a network game now runs in the background. It first joins a port where nobody
listens (must report "the host does not answer"), then the host's port. The host (copy D) loads its own save,
opens the pause menu, presses "Online play", Co-op, Host a game, and Start when the guest is in; it must not
reload. Screenshots of the pause menu, the page and the lobby go to the host's save folder.
"""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from run_coop_test import LOCAL, HOST_COPY, GUEST_COPY, launch, read_status, stop, bepinex_log, fingerprint_real, registry_export  # noqa: E402

PORT = 27610
checks = []


def check(ok, what):
    checks.append(bool(ok))
    print(("  ok   " if ok else "  FAIL ") + what, flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--timeout", type=int, default=400)
    args = ap.parse_args()
    real_before = fingerprint_real()
    import real_state
    reg_before = real_state.save_registry()

    guest, gs = launch(GUEST_COPY, "ingameguest", ["-coop-join", f"127.0.0.1:{PORT + 1}", "-coop-join-retry", f"127.0.0.1:{PORT}"],
                       background=False)
    time.sleep(2)
    host, hs = launch(HOST_COPY, "ingamehost", ["-coop-bot-ingame", str(PORT)])
    fh = fg = None
    deadline = time.time() + args.timeout
    try:
        while time.time() < deadline:
            if host.poll() is not None or guest.poll() is not None:
                print("a game copy exited early", flush=True)
                break
            fh, fg = read_status(hs), read_status(gs)
            if fh and fg and fh["coop"].get("localWorld", [-1])[0] > 0 and fh["coop"].get("localWorld") == fg["coop"].get("localWorld"):
                time.sleep(15)  # both playing for a while, the guest unfocused
                fh, fg = read_status(hs) or fh, read_status(gs) or fg
                break
            time.sleep(2)
    finally:
        stop(host)
        stop(guest)

    hl, gl = bepinex_log(HOST_COPY), bepinex_log(GUEST_COPY)
    check("added to the pause menu" in hl and "clicked True" in hl, "the pause menu has the \"Online play\" sign, and it opens the page")
    check("hosting from inside the game" in hl, "the host started co-op from inside its save")
    check(hl.count("main menu: Continue") == 1, f"the host loaded its save once and did not reload for co-op ({hl.count('main menu: Continue')})")
    check("co-op: noHost" in gl and "no host at the first address" in gl, "joining an address with no host says the host does not answer")
    ch, cg = (fh or {}).get("coop", {}), (fg or {}).get("coop", {})
    check(ch.get("localWorld", [-1])[0] > 0 and ch.get("localWorld") == cg.get("localWorld"), f"both in the same shop ({ch.get('localWorld')} / {cg.get('localWorld')})")
    check("timed out" not in hl and "timed out" not in gl and ch.get("connection") == "Connected",
          "no connection loss while the guest's window was in the background")
    for name in ("ingame_pause.png", "ingame_page.png", "ingame_lobby.png"):
        check(os.path.exists(os.path.join(LOCAL, "test_saves", "ingamehost", name)), "screenshot " + name)
    check(hl.count("Exception") == 0 and gl.count("Exception") == 0, "no exceptions in either log")
    check(fingerprint_real() == real_before and registry_export() == reg_before, "owner's real saves and registry unchanged")
    real_state.restore_registry()
    print(f"{sum(checks)}/{len(checks)} checks passed")
    print("RESULT: " + ("PASS" if all(checks) else "FAIL"))
    return 0 if all(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
