"""Two-copy versus race with the in-game bots. Prints RESULT: PASS|FAIL, exit 0 on pass.

Usage: python -I local/scripts/run_race_test.py [--timeout 420] [--keep-open]

Copy A hosts, copy B joins over UDP loopback. Each bot loads the copied save from the main menu,
A picks the first licensed device, both get it on the bench, ready up, run the shared countdown,
assemble with one deliberate miss each, and finish. Afterwards the owner's real save folder and
registry key are compared with the backup taken before the first launch.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

LOCAL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL = os.path.join(os.environ["USERPROFILE"], "AppData", "LocalLow", "Mandragora", "Restory")
BACKUP = os.path.join(LOCAL, "backup", "2026-10-24")
PORT = 27515
COPY_A = os.environ.get("RESTORY_HOST_COPY", "restory-steam-a")   # host: the Steam build 1.0.033r by default
COPY_B = os.environ.get("RESTORY_GUEST_COPY", "restory-steam-b")   # client

checks = []


def check(ok, what):
    checks.append(bool(ok))
    print(("  ok   " if ok else "  FAIL ") + what, flush=True)


def launch(copy, name, versus_args):
    game = os.path.join(LOCAL, "test-installs", copy)
    exe = os.path.join(game, "Restory.exe")
    # Only test copies: never a real install (a Steam library or wherever the owner plays).
    assert os.path.abspath(game).lower().startswith(os.path.join(LOCAL, "test-installs").lower() + os.sep)
    saves = os.path.join(LOCAL, "test_saves", name)
    # Fresh copy of the owner's save for every run (read-only on the owner's side).
    shutil.rmtree(saves, ignore_errors=True)
    shutil.copytree(os.path.join(REAL, "SaveData"), os.path.join(saves, "SaveData"))
    logs = os.path.join(LOCAL, "logs")
    os.makedirs(logs, exist_ok=True)
    status = os.path.join(saves, "versus_status.json")
    if os.path.exists(status):
        os.remove(status)
    args = [exe, "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540",
            "-logFile", os.path.join(logs, name + "-player.log"),
            "-versus-name", name, "-versus-savedir", saves, "-versus-route", "ip", "-versus-bot", "-versus-shots"] + versus_args
    proc = subprocess.Popen(args, cwd=game)
    print(f"launched {name} pid={proc.pid}", flush=True)
    return proc, status


def read_status(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def stop(proc):
    if proc.poll() is None:
        proc.kill()  # this exact process only
        proc.wait(timeout=30)


sys.path.insert(0, os.path.join(LOCAL, "scripts"))
import real_state  # noqa: E402


def fingerprint_real():
    return real_state.fingerprint()


def career_files(name):
    """Profile-1 save files in a copy's isolated save folder: name -> sha256."""
    return career_files_in(os.path.join(LOCAL, "test_saves", name, "SaveData"))


def career_files_in(d):
    """Career saves and settings of profile/slot 1 (not achievements: game 1.0.015r itself rewrites that file on
    first load of an older save, revoking "How did I do it?" in AchievementsManagerSaveDataV02)."""
    import hashlib
    out = {}
    if os.path.isdir(d):
        for f in sorted(os.listdir(d)):
            if ".slot1." in f and not f.startswith("achievements."):
                with open(os.path.join(d, f), "rb") as fh:
                    out[f] = hashlib.sha256(fh.read()).hexdigest()
    return out


def registry_export():
    return real_state.registry()


def main():
    global COPY_A, COPY_B
    ap = argparse.ArgumentParser()
    ap.add_argument("--timeout", type=int, default=420)
    ap.add_argument("--keep-open", action="store_true")
    ap.add_argument("--copy-a", default=COPY_A, help="host test copy (default %(default)s)")
    ap.add_argument("--copy-b", default=COPY_B, help="client test copy (default %(default)s)")
    args = ap.parse_args()
    COPY_A, COPY_B = args.copy_a, args.copy_b
    print(f"copies: host {COPY_A}, client {COPY_B}", flush=True)

    # Snapshot the owner's real data right before this run, so the owner's own game sessions in between
    # (a 2026-10-24 run "failed" because the owner had started the real install from F:\ meanwhile) do not count.
    real_before = fingerprint_real()
    reg_before = real_state.save_registry()

    # Each copy starts from a fresh copy of the owner's SaveData, so its profile-1 files must still equal
    # the source's at the end.
    source = career_files_in(os.path.join(REAL, "SaveData"))
    career_before = {"a": source, "b": source}
    a, a_status = launch(COPY_A, "a", ["-versus-host", str(PORT), "-versus-bot-speed", "0.35", "-versus-bot-quit", "0", "-versus-bot-modes", "normal,hardcore,rematch"])
    time.sleep(3)
    b, b_status = launch(COPY_B, "b", ["-versus-join", f"127.0.0.1:{PORT}", "-versus-bot-speed", "0.5", "-versus-bot-quit", "0", "-versus-bot-modes", "normal,hardcore,rematch", "-versus-bot-bounce"])

    sa = sb = None          # snapshot when both results are decided (before the bots go back to the lobby)
    fa = fb = None          # last status, after the bots are back in the lobby
    deadline = time.time() + args.timeout
    last = ""
    try:
        while time.time() < deadline:
            if a.poll() is not None or b.poll() is not None:
                print("a game copy exited early", flush=True)
                break
            fa, fb = read_status(a_status), read_status(b_status)
            line = " | ".join(
                f"{n}: {s['bot']} {s['local']['phase']} {s['local']['placed']}/{s['local']['total']}" if s else f"{n}: -"
                for n, s in (("a", fa), ("b", fb)))
            if line != last:
                print("  " + line, flush=True)
                last = line
            if sa is None and fa and fb and fa["result"]["decided"] and fb["result"]["decided"]:
                time.sleep(1.0)
                sa, sb = read_status(a_status), read_status(b_status)
            if fa and fb and fa["botLeft"] and fb["botLeft"]:
                break
            if fa and fb and ("FAIL" in (fa["bot"] or "") or "FAIL" in (fb["bot"] or "")):
                print("a bot reported FAIL", flush=True)
                break
            time.sleep(1)
    finally:
        if not args.keep_open:
            stop(a)
            stop(b)

    def log_has(copy, text):
        with open(os.path.join(LOCAL, "test-installs", copy, "BepInEx", "LogOutput.log"), encoding="utf-8", errors="replace") as fh:
            return text in fh.read()

    def player_log_has(name, text):
        with open(os.path.join(LOCAL, "logs", name + "-player.log"), encoding="utf-8", errors="replace") as fh:
            return text in fh.read()

    def log_index(copy, text):
        with open(os.path.join(LOCAL, "test-installs", copy, "BepInEx", "LogOutput.log"), encoding="utf-8", errors="replace") as fh:
            return fh.read().find(text)

    def player_log_count(name, text):
        with open(os.path.join(LOCAL, "logs", name + "-player.log"), encoding="utf-8", errors="replace") as fh:
            return fh.read().count(text)

    print("checks", flush=True)
    check(log_has(COPY_A, "global state: GameLoopState"), "copy a finished loading into the game loop")
    check(log_has(COPY_B, "global state: GameLoopState"), "copy b finished loading into the game loop")
    ea, eb = player_log_count("a", "Exception"), player_log_count("b", "Exception")
    check(ea == 0 and eb == 0, f"no exceptions in either game log (a={ea}, b={eb})")
    check(sa is not None and sb is not None and sa["lobbyDevice"] != "" and sa["lobbyDevice"] == sb["lobbyDevice"],
          "lobby: the client saw the host's device choice")
    check(log_has(COPY_B, "lobby: ready for") and log_has(COPY_A, "opponent is ready") and log_has(COPY_A, "launching "),
          "lobby: client pressed Ready before the host's Start")
    check(log_has(COPY_A, "versus menu built") and log_has(COPY_B, "versus menu built"), "the versus menu was built on both")
    check(log_has(COPY_A, "Choose device is disabled until the opponent is ready (ok)"), "menu stage: Choose device locked until the client was ready")
    check(log_has(COPY_B, "clicked avatar") and log_has(COPY_B, "clicked READY in the menu"), "menu stage: client picked an avatar and pressed READY")
    ia_go, ia_tpl = log_index(COPY_A, "clicked Choose device"), log_index(COPY_A, "versus template installed")
    ib_go, ib_tpl = log_index(COPY_B, "host is taking us to the versus workshop"), log_index(COPY_B, "versus template installed")
    check(0 <= ia_go < ia_tpl and 0 <= ib_go < ib_tpl, "the versus workshop loaded only after the host pressed Choose device (on both)")
    # From the snapshot taken during the race: after Leave the session is offline and these are empty.
    if sa and sb:
        check(sa["remoteName"] == "b" and sb["remoteName"] == "a", f"each side shows the other's name ({sa['remoteName']} / {sb['remoteName']})")
        check(sb["localAvatar"] != "" and sa["remoteAvatar"] == sb["localAvatar"] and sb["remoteAvatar"] == sa["localAvatar"] != "",
              f"each side shows the other's avatar (a sees {sa['remoteAvatar']}, b chose {sb['localAvatar']}; b sees {sb['remoteAvatar']}, a has {sa['localAvatar']})")
        check(sa["localAvatar"] != sb["localAvatar"], "the client's avatar choice took effect (differs from the host's default)")
    check(log_has(COPY_A, "START is disabled until the opponent is ready (ok)"), "menu: START could not be pressed before the opponent was ready")
    check(log_has(COPY_A, "clicked device") and log_has(COPY_B, "clicked READY") and log_has(COPY_A, "clicked START"),
          "menu: host picked the device and pressed START, client pressed READY (menu buttons)")
    check(log_has(COPY_A, "clicked Back to lobby") and log_has(COPY_B, "clicked Back to lobby"), "menu: both pressed Back to lobby")
    check(log_has(COPY_A, "start gate holding") and log_has(COPY_B, "start gate holding"),
          "both went to the bench by themselves and the intro was held")
    check(log_has(COPY_A, "start gate released") and log_has(COPY_B, "start gate released"),
          "both played the game's 3-2-1-GO from the shared countdown")
    check(sa is not None and sb is not None, "both copies wrote a status file")
    if sa and sb:
        check(sa["saveDir"].endswith("test_saves/a") and sb["saveDir"].endswith("test_saves/b"), "each copy uses its own save folder")
        check(sa["attached"] and sb["attached"], "both attached to CompetitionGameMode")
        check(sa["connection"] == "Connected" and sb["connection"] == "Connected", "both connected")
        la, lb = sa["local"], sb["local"]
        check(la["device"] != "" and la["device"] == lb["device"], f"same race device ({la['device']} / {lb['device']})")
        check(la["race"] >= 1 and la["race"] == lb["race"], f"same race number ({la['race']} / {lb['race']})")
        gap = abs(sa["countdownWallMs"] - sb["countdownWallMs"]) if sa["countdownWallMs"] > 0 and sb["countdownWallMs"] > 0 else None
        check(gap is not None and gap <= 100, f"countdowns fired within 100 ms by wall clock (gap {gap} ms, rtt a={sa['rttMs']} b={sb['rttMs']} ms)")
        check(la["phase"] == "Finished" and lb["phase"] == "Finished", "race 1: both finished")
        check(la["total"] > 0 and la["total"] == lb["total"] and la["placed"] == la["total"], f"race 1: part counts agree ({la['placed']}/{la['total']} vs {lb['placed']}/{lb['total']})")
        check(sa["botRaceProfile"] == 9 and sb["botRaceProfile"] == 9, f"both raced in the versus save profile 9 ({sa['botRaceProfile']} / {sb['botRaceProfile']})")
        check(sa["toolsOwned"] > 0 and sa["toolsOwned"] == sb["toolsOwned"], f"same full tool set on both ({sa['toolsOwned']} / {sb['toolsOwned']})")
    # Per race, from the bots' records (race 1 normal, race 2 hardcore; penalty 5 s).
    races_a = fa["botRaces"] if fa else []
    races_b = fb["botRaces"] if fb else []
    # Race 3 is a rematch: both pressed Rematch on the result screen, same device and mode started by itself.
    check(len(races_a) == 3 and len(races_b) == 3, f"three races recorded on both, the last a rematch ({len(races_a)} / {len(races_b)})")
    if len(races_a) == 3 and len(races_b) == 3:
        check(races_a[2]["device"] == races_a[1]["device"] == races_b[2]["device"] and races_a[2]["mode"] == "Hardcore",
              f"rematch: same device and mode as race 2 ({races_a[2]['device']}, {races_a[2]['mode']})")
        check(log_has(COPY_A, "rematch agreed") and log_has(COPY_B, "rematch agreed") and log_has(COPY_A, "rematch: starting"),
              "rematch: both agreed and the host started it again by itself")
    def last_series(copy):
        with open(os.path.join(LOCAL, "test-installs", copy, "BepInEx", "LogOutput.log"), encoding="utf-8", errors="replace") as fh:
            found = [l.split("versus series: ")[1].strip() for l in fh if "versus series: " in l]
        return found[-1] if found else ""
    wins_a = sum(1 for r in races_a if r["decided"] and r["localWins"])
    want_a, want_b = f"{wins_a}:{len(races_a) - wins_a}:0", f"{len(races_a) - wins_a}:{wins_a}:0"
    check(last_series(COPY_A) == want_a and last_series(COPY_B) == want_b,
          f"the series score matches the races (a {last_series(COPY_A)} want {want_a}, b {last_series(COPY_B)} want {want_b})")
    for i, (ra, rb) in enumerate(zip(races_a, races_b)):
        n = i + 1
        want = "Normal" if i == 0 else "Hardcore"
        check(ra["mode"] == want and rb["mode"] == want, f"race {n}: both played {want} ({ra['mode']} / {rb['mode']})")
        check(ra["race"] == n and rb["race"] == n and ra["device"] == rb["device"] != "", f"race {n}: same race number {n} and device ({ra['device']})")
        check(ra["decided"] and rb["decided"] and ra["localWins"] != rb["localWins"], f"race {n}: decided with exactly one winner (a wins: {ra['localWins']})")
        check(ra["remoteMs"] == rb["localMs"] and rb["remoteMs"] == ra["localMs"] and ra["remoteScore"] == rb["localScore"] and rb["remoteScore"] == ra["localScore"],
              f"race {n}: both sides agree on times and scores (a {ra['localMs']} ms / {ra['localScore']}, b {rb['localMs']} ms / {rb['localScore']})")
        if want == "Normal":
            check(ra["localResets"] == 0 and rb["localResets"] == 0, f"race 1 normal: the missed drop did not reset the device ({ra['localResets']} / {rb['localResets']} resets)")
            check(ra["partsAfterMiss"] == ra["partsBeforeMiss"] and rb["partsAfterMiss"] == rb["partsBeforeMiss"],
                  f"race 1 normal: parts stayed in place after the miss (a {ra['partsBeforeMiss']}->{ra['partsAfterMiss']}, b {rb['partsBeforeMiss']}->{rb['partsAfterMiss']})")
            check(ra["localScore"] == ra["localMs"] and rb["localScore"] == rb["localMs"], "race 1 normal: score is the time, no penalty")
        else:
            check(ra["localResets"] == 1 and rb["localResets"] == 1, f"race {n} hardcore: the missed drop reset the device once ({ra['localResets']} / {rb['localResets']})")
            check(ra["partsAfterMiss"] < ra["partsBeforeMiss"] and rb["partsAfterMiss"] < rb["partsBeforeMiss"],
                  f"race {n} hardcore: the device came apart (a {ra['partsBeforeMiss']}->{ra['partsAfterMiss']}, b {rb['partsBeforeMiss']}->{rb['partsAfterMiss']})")
            check(ra["localScore"] == ra["localMs"] + 5000 and rb["localScore"] == rb["localMs"] + 5000, f"race {n} hardcore: score is time + 5 s")
    check(log_has(COPY_B, "test: leaving the bench") and log_has(COPY_B, "back at the bench after an unexpected exit"),
          "a racer that dropped out of the bench before GO was sat back down, and the race still ran")
    check(log_has(COPY_A, "normal mode: missed drop, device not reset") and log_has(COPY_B, "normal mode: missed drop, device not reset"),
          "normal mode skipped the game's ResetDevice on both")
    # Game 1.0.033r has no contest neon shadow any more: nothing to replace there.
    shadow_ok = lambda c: log_has(c, "normal device shadow instead of the contest") or log_has(c, "this game version has no contest shadow")
    check(shadow_ok(COPY_A) and shadow_ok(COPY_B), "the normal device shadow in versus on both (the contest's neon one replaced, or none in this game version)")
    if sa and sb:
        def mmss(ms):
            return "%02d:%02d" % (ms // 60000, (ms // 1000) % 60)
        check(sa["matTimer"] == mmss(sa["local"]["elapsedMs"]) and sb["matTimer"] == mmss(sb["local"]["elapsedMs"]) and sa["matTimer"] != "00:00",
              f"the mat timer shows the race time (a {sa['matTimer']} for {sa['local']['elapsedMs']} ms, b {sb['matTimer']} for {sb['local']['elapsedMs']} ms)")
        check(sa["patchFailures"] == 0 and sb["patchFailures"] == 0, "every patch applied on both")
        check(sa["malformed"] == 0 and sb["malformed"] == 0, "no malformed messages between the real copies")
    check(log_has(COPY_A, "mouse cursor raised") and log_has(COPY_B, "mouse cursor raised"), "the game's mouse cursor was raised above the window on both")
    check(player_log_has("a", "COMPETITION COMPLETED!") and player_log_has("b", "COMPETITION COMPLETED!"),
          "both finished through the game's EndCompetitionState")
    check(log_has(COPY_A, "race time not recorded") and log_has(COPY_B, "race time not recorded"),
          "race times were kept out of the career best times")
    check(log_has(COPY_A, "versus template installed") and log_has(COPY_B, "versus template installed"), "both loaded the versus workshop from the template")
    check(log_has(COPY_A, "game time stopped") and log_has(COPY_B, "game time stopped"), "game time stopped in the versus workshop (no customers, no end of day)")
    check(log_has(COPY_A, "tools added") and log_has(COPY_B, "tools added"), "every tool granted in the versus workshop")
    check(log_has(COPY_A, "race device removed") and log_has(COPY_B, "race device removed"), "Back to lobby removed the race device on both")
    check(fa is not None and fb is not None and fa["botLeft"] and fb["botLeft"] and fa["profile"] == 1 and fb["profile"] == 1,
          f"Leave: both back in the main menu with their own profile 1 ({fa and fa['profile']} / {fb and fb['profile']})")
    career_after = {n: career_files(n) for n in ("a", "b")}
    check(career_before == career_after, "career saves (profile 1) not changed by versus")

    real_after = fingerprint_real()
    diff = [k for k in set(real_before) | set(real_after) if real_before.get(k) != real_after.get(k)]
    check(not diff, f"owner's real save folder unchanged ({len(diff)} differences)")
    check(registry_export() == reg_before, "owner's registry key unchanged")
    real_state.restore_registry()

    passed = sum(checks)
    print(f"{passed}/{len(checks)} checks passed")
    print("RESULT: PASS" if all(checks) else "RESULT: FAIL")
    return 0 if all(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
