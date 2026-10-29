"""Two-copy co-op step 1: the guest joins the host's save. Prints RESULT: PASS|FAIL, exit 0 on pass.

Usage: python -I local/scripts/run_coop_test.py [--timeout 240] [--keep-open]

Copy D hosts (-coop-host, -coop-bot presses Start), copy E joins. Both start from a fresh copy of the owner's
SaveData (read-only on the owner's side). Checks: the guest got the host's save byte for byte as its slot 8,
both reached the shop, both loaded the same objects (ids), the guest's own slot 1 files are untouched, no
exceptions, and the owner's real save folder and registry key did not change.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

LOCAL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REAL = os.path.join(os.environ["USERPROFILE"], "AppData", "LocalLow", "Mandragora", "Restory")
PORT = 27600
# The Steam build (1.0.033r) by default; RESTORY_HOST_COPY / RESTORY_GUEST_COPY pick other test copies.
HOST_COPY = os.environ.get("RESTORY_HOST_COPY", "restory-steam-a")
GUEST_COPY = os.environ.get("RESTORY_GUEST_COPY", "restory-steam-b")
HOST, GUEST = "coophost", "coopguest"

checks = []


def check(ok, what):
    checks.append(bool(ok))
    print(("  ok   " if ok else "  FAIL ") + what, flush=True)


def launch(copy, name, extra, background=True):
    game = os.path.join(LOCAL, "test-installs", copy)
    # Only test copies: never a real install (a Steam library or wherever the owner plays).
    assert os.path.abspath(game).lower().startswith(os.path.join(LOCAL, "test-installs").lower() + os.sep)
    saves = os.path.join(LOCAL, "test_saves", name)
    shutil.rmtree(saves, ignore_errors=True)
    shutil.copytree(os.path.join(REAL, "SaveData"), os.path.join(saves, "SaveData"))
    logs = os.path.join(LOCAL, "logs")
    os.makedirs(logs, exist_ok=True)
    args = [os.path.join(game, "Restory.exe"), "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540",
            "-logFile", os.path.join(logs, name + "-player.log"),
            "-versus-name", name, "-versus-savedir", saves, "-versus-route", "ip"] + (["-versus-background"] if background else []) + extra
    proc = subprocess.Popen(args, cwd=game)
    print(f"launched {name} ({copy}) pid={proc.pid}", flush=True)
    return proc, os.path.join(saves, "versus_status.json")


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


def sha(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def slot_files(name, slot):
    d = os.path.join(LOCAL, "test_saves", name, "SaveData")
    out = {}
    if os.path.isdir(d):
        for f in sorted(os.listdir(d)):
            if f".slot{slot}." in f and not f.startswith("achievements."):
                out[f] = sha(os.path.join(d, f))
    return out


# The owner's real save folder and registry key (real_state.py: saves strictly, engine bookkeeping left out).
sys.path.insert(0, os.path.join(LOCAL, "scripts"))
import real_state  # noqa: E402


def fingerprint_real():
    return real_state.fingerprint()


def registry_export():
    return real_state.registry()


def bepinex_log(copy):
    with open(os.path.join(LOCAL, "test-installs", copy, "BepInEx", "LogOutput.log"), encoding="utf-8", errors="replace") as fh:
        return fh.read()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--timeout", type=int, default=240)
    ap.add_argument("--keep-open", action="store_true")
    ap.add_argument("--day-wait", type=int, default=180, help="seconds to wait for the next day on both")
    ap.add_argument("--visit-wait", type=int, default=78, help="seconds to wait in the shop (customer at the host, bot device steps)")
    args = ap.parse_args()

    real_before = fingerprint_real()
    reg_before = real_state.save_registry()

    host, host_status = launch(HOST_COPY, HOST, ["-coop-host", str(PORT), "-coop-bot", "-coop-bot-dayend"])
    time.sleep(3)
    guest, guest_status = launch(GUEST_COPY, GUEST, ["-coop-join", f"127.0.0.1:{PORT}", "-coop-bot", "-coop-bot-dayend"])
    guest_own_before = slot_files(GUEST, 1)

    fh = fg = None
    stash = {}
    visits_host, visits_guest = set(), set()
    clock_seen = []
    deadline = time.time() + args.timeout
    last = ""
    try:
        while time.time() < deadline:
            if host.poll() is not None or guest.poll() is not None:
                print("a game copy exited early", flush=True)
                break
            fh, fg = read_status(host_status), read_status(guest_status)
            if fh and fh.get("coop", {}).get("clockTicks", -1) > 0 and fh["coop"].get("localWorld", [-1])[0] > 0:
                clock_seen.append(fh["coop"]["clockTicks"])

            def desc(s):
                if not s or not s["coop"]["active"]:
                    return "-"
                c = s["coop"]
                return f"{c['connection']} {c['stage']} {c['transfer'][0]}/{c['transfer'][1]} world {c['localWorld'][0]}"
            line = f"host: {desc(fh)} | guest: {desc(fg)}"
            if line != last:
                print("  " + line, flush=True)
                last = line
            if fh and fg and fh["coop"].get("localWorld", [-1])[0] > 0 and fg["coop"].get("localWorld", [-1])[0] > 0 \
                    and fh["coop"]["remoteWorld"][0] > 0 and fg["coop"]["remoteWorld"][0] > 0:
                # The host's bot opens the shop; give the clock time to run, then compare the two shops.
                first = -1
                for _ in range(20):  # the status file is rewritten twice a second; retry a torn read
                    first = (read_status(host_status) or {}).get("coop", {}).get("clockTicks", -1)
                    if first > 0:
                        break
                    time.sleep(0.25)
                # Then wait for a customer at the host (up to --visit-wait s), watching that none comes to the guest.
                until = time.time() + max(25, args.visit_wait)
                while time.time() < until:
                    time.sleep(1)
                    h, g = read_status(host_status), read_status(guest_status)
                    if h and g:
                        fh, fg = h, g
                        visits_host.add(h["coop"].get("visit"))
                        visits_guest.add(g["coop"].get("visit"))
                        if time.time() > until - max(25, args.visit_wait) + 25 and h["coop"].get("visit") not in (None, "NoVisitInProgress"):
                            time.sleep(3)
                            break
                # Both bots run on their own clocks (each starts when it sees both shops): wait until both
                # are past their stock and purchase steps, so the first day's snapshot is a settled one.
                settle = time.time() + 60
                while time.time() < settle:
                    h, g = read_status(host_status), read_status(guest_status)
                    if h and g and h["coop"].get("room", {}).get("botStep", 0) >= 16 and g["coop"].get("room", {}).get("botStep", 0) >= 15:
                        break
                    time.sleep(1)
                time.sleep(5)
                fh, fg = read_status(host_status) or fh, read_status(guest_status) or fg
                if first <= 0 and clock_seen:
                    first = min(clock_seen)
                fh["coop"]["clockAtShopTicks"] = first
                shop_h, shop_g = fh, fg   # the first day, before the bots end it
                # The host's bot ends the day at ~70 s in the shop; both go through the end-of-day screen and load
                # the next morning. Wait for both to be in the next day's shop with their worlds reported.
                day0 = fh["coop"].get("day", -1)
                until = time.time() + args.day_wait
                while time.time() < until:
                    time.sleep(1)
                    h, g = read_status(host_status), read_status(guest_status)
                    if not (h and g):
                        continue
                    fh, fg = h, g
                    hc, gc = h["coop"], g["coop"]
                    if hc.get("day", -1) > day0 and gc.get("day", -1) > day0 and hc["localWorld"][0] > 0 and gc["localWorld"][0] > 0 \
                            and not gc.get("hostEndingDay") and hc["localWorld"] == gc["localWorld"]:
                        time.sleep(8)
                        fh, fg = read_status(host_status) or fh, read_status(guest_status) or fg
                        break
                fh["coop"]["day0"] = day0
                fh["coop"]["clockAtShopTicks"] = first  # the day loop above replaced fh
                break
            time.sleep(1)
        # The guest's game dies mid-day and comes back: the host sends it the current save.
        rejoin = {}
        stash["slot8"] = slot_files(GUEST, 8)
        for name in (HOST, GUEST):
            rp = os.path.join(LOCAL, "test_saves", name, "coop_room.txt")
            stash["room_" + name] = open(rp, encoding="utf-8").read() if os.path.exists(rp) else ""
        if fh and fg and fh["coop"].get("day", -1) > fh["coop"].get("day0", 99):
            print("  killing the guest's game and starting it again", flush=True)
            stop(guest)
            stash["log_" + GUEST_COPY] = bepinex_log(GUEST_COPY)  # the restart starts a new log
            guest, guest_status = launch(GUEST_COPY, GUEST, ["-coop-join", f"127.0.0.1:{PORT}"])
            until = time.time() + 200
            while time.time() < until:
                time.sleep(2)
                h, g = read_status(host_status), read_status(guest_status)
                if h and g and g["coop"].get("active") and g["coop"].get("localWorld", [-1])[0] > 0 \
                        and h["coop"].get("localWorld") == g["coop"].get("localWorld"):
                    time.sleep(3)
                    rejoin = {"host": read_status(host_status), "guest": read_status(guest_status)}
                    break
            # The host leaves for the main menu: the guest is told and goes back to its own save.
            if rejoin:
                open(os.path.join(LOCAL, "test_saves", HOST, "coop_leave.flag"), "w").close()
                until = time.time() + 90
                while time.time() < until:
                    time.sleep(2)
                    g = read_status(guest_status)
                    if g and not g["coop"].get("active") and not g["coop"].get("guestSlot"):
                        rejoin["after_leave"] = g
                        break
    finally:
        if not args.keep_open:
            stop(host)
            stop(guest)

    ch, cg = (fh or {}).get("coop", {}), (fg or {}).get("coop", {})
    check(ch.get("active") and cg.get("active") and ch.get("host") and not cg.get("host"), "host hosts, guest joins")
    check(ch.get("stage") == "Launch" and cg.get("stage") == "Launch", f"both launched (host {ch.get('stage')}, guest {cg.get('stage')})")

    # The file the host sent is the host's latest slot 1 save; the guest holds the same bytes as slot 8.
    sent = ch.get("saveFile", "")
    host_file = os.path.join(LOCAL, "test_saves", HOST, "SaveData", sent)
    guest_slot8 = stash.get("slot8") or slot_files(GUEST, 8)
    story8 = [f for f in guest_slot8 if f.startswith("Story.")]
    check(sent != "" and len(story8) == 1, f"guest has one Story save in slot 8 ({story8}) for the host's {sent}")
    if sent and story8 and os.path.exists(host_file):
        # The host may have autosaved since; compare with the copy the host started from instead.
        src = os.path.join(REAL, "SaveData", sent)
        ref = sha(src) if os.path.exists(src) else sha(host_file)
        check(guest_slot8[story8[0]] == ref, "the guest's slot 8 save is the host's save byte for byte")
    check(fg and fg["coop"].get("guestSlot"), "the guest plays in slot 8")

    hw, gw = ch.get("localWorld", [-1, ""]), cg.get("localWorld", [-1, ""])
    check(hw[0] > 0 and gw[0] > 0, f"both are in the shop (host {hw[0]} objects, guest {gw[0]})")
    check(hw == gw, f"both loaded the same objects (host {hw}, guest {gw})")
    if hw != gw:
        def ids(name):
            p = os.path.join(LOCAL, "test_saves", name, "coop_ids.txt")
            return set(open(p, encoding="utf-8").read().splitlines()) if os.path.exists(p) else set()
        a, b = ids(HOST), ids(GUEST)
        a_ids, b_ids = {l.split(" ")[0]: l for l in a}, {l.split(" ")[0]: l for l in b}
        only_h = [a_ids[k] for k in a_ids.keys() - b_ids.keys()]
        only_g = [b_ids[k] for k in b_ids.keys() - a_ids.keys()]
        print(f"  only on the host ({len(only_h)}): {only_h[:8]}")
        print(f"  only on the guest ({len(only_g)}): {only_g[:8]}")

    # Step 2: the guest's shop follows the host's day.
    tick_min = 600_000_000  # .NET ticks per game minute
    check(ch.get("shuttersOpen") and ch.get("clockRunning"), f"host: shop open, clock running ({ch.get('clock')})")
    t_first, t_host, t_guest = ch.get("clockAtShopTicks", -1), ch.get("clockTicks", -1), cg.get("clockTicks", -1)
    check(t_first > 0 and t_host - t_first >= tick_min, f"host's clock moved during the test ({(t_host - t_first) / tick_min:.1f} game min)")
    check(cg.get("guestPlaying") and not cg.get("clockRunning"), "guest: own clock stopped, playing the host's save")
    check(cg.get("hostStates", 0) > 0 and abs(t_host - t_guest) <= 5 * tick_min,
          f"guest's clock follows the host's (host {ch.get('clock')}, guest {cg.get('clock')}, {cg.get('hostStates')} states)")
    check(cg.get("shuttersOpen") == ch.get("shuttersOpen"), f"guest's shutters match the host's ({cg.get('shuttersOpen')})")
    host_had_visit = bool(visits_host - {None, "NoVisitInProgress"})
    check(visits_guest <= {"NoVisitInProgress"}, f"no customer visits on the guest (guest saw {sorted(v for v in visits_guest if v)})")
    print(f"  info a customer came to the host during the test: {host_had_visit} ({sorted(v for v in visits_host if v)})")
    check(fg and fg.get("patchFailures") == 0 and fh.get("patchFailures") == 0, "all patches applied on both")

    # Step 5: the end of the day in one flow. The host's bot ended the day; both went through the end-of-day
    # screen and loaded the next morning from the host's end-of-day save, the guest without the main menu.
    day0 = ch.get("day0", -1)
    check(ch.get("daySaves", 0) >= 1 and cg.get("guestDaySwitches", 0) >= 1,
          f"the host sent its end-of-day save, the guest ran the day switch ({ch.get('daySaves')} / {cg.get('guestDaySwitches')})")
    check(ch.get("day", -1) > day0 >= 0 and cg.get("day") == ch.get("day"), f"both are on the next day (day {day0} -> host {ch.get('day')}, guest {cg.get('day')})")
    check(ch.get("localWorld") == cg.get("localWorld") and ch["localWorld"][0] > 0, f"the next day's shops match ({ch.get('localWorld')} / {cg.get('localWorld')})")
    gl = bepinex_log(GUEST_COPY)
    check(gl.count("main menu: Continue (co-op") == 1, f"the guest loaded through the main menu only once, at the start ({gl.count('main menu: Continue (co-op')})")
    check(cg.get("guestSlot") and cg.get("guestPlaying"), "the guest still plays the host's save in slot 8")

    # Step 3: the shared room. Each bot moved its own object through the game's drop code, then both moved the
    # lamp at the same moment to different storages; both copies must show the same room.
    def room(name):
        text = stash.get("room_" + name)
        if text is None:
            p = os.path.join(LOCAL, "test_saves", name, "coop_room.txt")
            text = open(p, encoding="utf-8").read() if os.path.exists(p) else ""
        out = {}
        for line in text.splitlines():
            parts = line.split(" ")
            out[parts[0]] = parts[1:]
        return out
    rh, rg = room(HOST), room(GUEST)
    def moved(copy, what):
        log = stash.get("log_" + copy) or bepinex_log(copy)
        i = log.find("co-op bot: " + what)
        return log[i:log.find(chr(10), i)].strip() if i >= 0 else "(not in the log)"
    print("  info " + moved(HOST_COPY, "move ") + " | " + moved(GUEST_COPY, "move "))
    print("  info " + moved(HOST_COPY, "contested") + " | " + moved(GUEST_COPY, "contested"))
    hr, gr = ch.get("room", {}), cg.get("room", {})
    check(hr.get("reported", 0) >= 2 and gr.get("reported", 0) >= 2, f"both reported their drops (host {hr.get('reported')}, guest {gr.get('reported')})")
    check(hr.get("applied", 0) >= 1 and gr.get("applied", 0) >= 1, f"both applied the other's drops (host {hr.get('applied')}, guest {gr.get('applied')})")
    check(hr.get("missing", 1) == 0 and gr.get("missing", 1) == 0 and gr.get("pending", 1) == 0, f"nothing missing, nothing left pending ({hr}, {gr})")
    diff = [k for k in set(rh) | set(rg) if rh.get(k) != rg.get(k)]
    check(len(rh) > 0 and not diff, f"both copies show the same room ({len(rh)} objects, {len(diff)} differ)")
    for k in diff[:5]:
        print(f"    {k}: host {rh.get(k)} | guest {rg.get(k)}")
    # The object in the other player's hand: the host's bot carried the clock for 2 s before dropping it.
    print("  info " + moved(HOST_COPY, "carried"))
    hh, gh = shop_h["coop"].get("held", {}), shop_g["coop"].get("held", {})
    print(f"  info held host {hh} | guest {gh}")
    check(hh.get("posesSent", 0) >= 12, f"the host sent the clock's pose while carrying it ({hh.get('posesSent')})")
    # posesFollowed counts the guest's frames while following: two background copies on one PC run at 9-17 fps.
    check(gh.get("locksSeen", 0) >= 1 and gh.get("posesFollowed", 0) >= 10, f"the guest's clock followed the host's hand and could not be grabbed ({gh})")
    check(gh.get("releases", 0) >= 1 and gh.get("locked", 1) == 0, "after the drop the clock is free again on the guest")

    # Textures: the guest's bot gave a part of the box a fresh dirt texture before moving it; the host must hold
    # the same texture under the same number, and the number must be in the guest's range.
    gt = shop_g["coop"].get("tex", {})
    print(f"  info textures host {shop_h['coop'].get('tex')} | guest {gt}")
    bot_tex = gt.get("bot", "")
    tex_id = int(bot_tex.split(":")[0]) if ":" in bot_tex else -1
    check(tex_id >= 1000000, f"the guest's new texture number is in the guest's range ({bot_tex})")
    host_has = (shop_h["coop"].get("tex", {}).get("guestRange") or "").split(",")
    check(gt.get("sent", 0) >= 1 and shop_h["coop"].get("tex", {}).get("received", 0) >= 1, "the texture went to the host with the box")
    check(bot_tex in host_has, f"the host holds the same texture under the same number ({bot_tex} in {host_has[:4]})")

    # Step 4: devices. Host spawned a box, the guest moved it, the host took it to its bench and back to a shelf.
    for what in ("device spawned", "device move", "device to bench", "device back"):
        print("  info " + moved(HOST_COPY, what) + " | " + moved(GUEST_COPY, what))
    hd, gd = ch.get("devices", {}), cg.get("devices", {})
    print(f"  info devices host {hd} | guest {gd}")
    check(hd.get("failed", 1) == 0 and gd.get("failed", 1) == 0, "no device report or rebuild failed")
    check(hd.get("count", 0) > 0 and hd.get("count") == gd.get("count"), f"both have the same number of devices ({hd.get('count')} / {gd.get('count')})")
    check(gd.get("applied", 0) >= 3 and gd.get("hiddenApplied", 0) >= 1, "the guest got the new box, saw it leave for the host's bench and come back")
    check(hd.get("applied", 0) >= 1, "the host rebuilt the box after the guest moved it")
    print("  info " + moved(GUEST_COPY, "device shipped"))
    left = moved(HOST_COPY, "a customer left")
    check(" left at " in left or "customers' box" in left, f"a customer left a device at the host ({left})")
    # Before the day ended (after that both load the host's save anyway): the guest saw it appear live.
    sdh, sdg = shop_h["coop"].get("devices", {}), shop_g["coop"].get("devices", {})
    check(sdh.get("count", 0) >= 2 and sdh.get("count") == sdg.get("count"), f"the customer's device reached the guest during the day ({sdh.get('count')} / {sdg.get('count')} devices)")
    check(hd.get("counterAnnounced", 0) >= 1, f"the guest's device at the counter reached the host's order logic ({hd.get('counterAnnounced')})")
    check(ch.get("money", -1) >= 0 and cg.get("money") == ch.get("money"), f"one wallet: the guest shows the host's money ({ch.get('money')} / {cg.get('money')})")

    # Step 6: one stock of parts, buying with the shared money (statuses from before the day ended).
    for what in ("part added", "part taken", "bought"):
        print("  info " + moved(HOST_COPY, what) + " | " + moved(GUEST_COPY, what))
    sh, sg = shop_h["coop"].get("stock", {}), shop_g["coop"].get("stock", {})
    print(f"  info stock host {sh} | guest {sg}")
    check(sh.get("total", -1) > 0 and sh.get("hash") == sg.get("hash"), f"both show the same parts stock ({sh.get('total')} parts, {sh.get('hash')} / {sg.get('hash')})")
    check(sg.get("changesSent", 0) >= 1 and sh.get("changesApplied", 0) >= 1 and sh.get("removeMisses", 1) == 0,
          "the part the guest took is gone from the host's stock")
    # The bots' clocks start when each copy sees both shops, so the purchase can land after the snapshot above;
    # the counters only grow, so they are read at the end. The delivery stays queued over night (no courier yet).
    fsh, fsg = ch.get("stock", {}), cg.get("stock", {})
    check(fsg.get("buysForwarded", 0) >= 2 and fsh.get("buysApplied", 0) >= 2, f"the guest's purchase reached the host (forwarded {fsg.get('buysForwarded')}, applied {fsh.get('buysApplied')})")
    check(fsh.get("deliveryWaiting") or "queued for delivery" in bepinex_log(HOST_COPY), "the host queued the guest's purchase for delivery")
    check(fsh.get("hash") == fsg.get("hash"), f"the same parts stock on the next day ({fsh.get('hash')} / {fsg.get('hash')})")
    check(shop_h["coop"].get("money") == shop_g["coop"].get("money"), f"one wallet after the purchase ({shop_h['coop'].get('money')} / {shop_g['coop'].get('money')})")
    # Step 8: the courier. The host's courier left a box of parts at the delivery spot; the guest saw the same box,
    # took the parts out into the shared stock and carried the box to a shelf.
    for what in ("courier came", "courier box ", "courier box to a shelf"):
        print("  info " + moved(HOST_COPY, what) + " | " + moved(GUEST_COPY, what))
    dh, dg = shop_h["coop"].get("delivery", {}), shop_g["coop"].get("delivery", {})
    print(f"  info delivery host {dh} | guest {dg}")
    came = moved(HOST_COPY, "courier came")
    check(" at the delivery spot with " in came, f"the courier brought the host a box ({came})")
    check(dh.get("statesSent", 0) >= 1 and dg.get("statesApplied", 0) >= 1, f"the guest got the host's courier box ({dg.get('statesApplied')} states)")
    check("parts from box" in moved(GUEST_COPY, "courier box "), "the guest took the parts out of the courier box")
    check(dh.get("takesApplied", 0) >= 2 and dg.get("takesSent", 0) >= 2, f"what the guest took and the box it moved reached the host ({dh.get('takesApplied')})")
    check(dh.get("hash") and dh.get("hash") == dg.get("hash"), f"both see the same delivery state after ({dh.get('hash')} / {dg.get('hash')})")
    box_id = came.split("box ", 1)[1].split(" ")[0] if "box " in came else ""
    check(box_id and box_id in rh and rh.get(box_id) == rg.get(box_id) and rh[box_id][1] == "shown",
          f"the courier box stands on the same shelf on both ({rh.get(box_id)} / {rg.get(box_id)})")
    # Step 10: the room list. The host made a radio and removed it again with no drop to report: the guest got it
    # from the list and lost it again; the customers' box the host filled is the same box on both.
    made = moved(HOST_COPY, "silently made")
    made_id = made.split(" ")[5] if len(made.split(" ")) > 5 else "?"
    print("  info " + made + " | removed " + moved(HOST_COPY, "silently removed"))
    gl_all = stash.get("log_" + GUEST_COPY) or bepinex_log(GUEST_COPY)
    check(made_id != "?" and ("(" + made_id + ")") in gl_all and "the other player brought it into the room" in gl_all,
          f"a radio the host made without a drop reached the guest through the room list ({made_id})")
    check(("(" + made_id + ") is not in the host's shop, removed here") in gl_all, "removed on the host without a drop: removed on the guest too")
    eh, eg = shop_h["coop"].get("census", {}), shop_g["coop"].get("census", {})
    print(f"  info census host {eh} | guest {eg}")
    check(eh.get("sent", 0) >= 3 and eg.get("received", 0) >= 3 and eh.get("answered", 0) >= 1, "the host sends the room list, the guest answers what it lacks")
    print("  info " + moved(HOST_COPY, "customers' box"))
    bh, bg = shop_h["coop"].get("boxes", {}), shop_g["coop"].get("boxes", {})
    print(f"  info boxes host {bh} | guest {bg}")
    check("customers' box " in moved(HOST_COPY, "customers' box") and "with" in moved(HOST_COPY, "customers' box"), "customers filled the spots, the next device went into the customers' box")
    check(bh.get("hashes") and bh.get("hashes") == bg.get("hashes"), f"the customers' box and the personal box are the same on both ({bh.get('hashes')} / {bg.get('hashes')})")
    ph, pg = ch.get("pause", {}), cg.get("pause", {})
    check(pg.get("byOther", 0) >= 1 and pg.get("resumedByOther", 0) >= 1, f"the host's pause paused the guest, and resuming resumed it ({pg})")
    # "Top bar only with notices" (guest): shown while the host is paused, hidden once it is quiet, back when off;
    # the bar names the host with its role.
    bar = moved(GUEST_COPY, "bar notices-only")
    print("  info " + bar)
    m = re.search(r'paused shown=(\w+) notice=(".*?") quiet shown=(\w+) notice=(".*?") off shown=(\w+) line=(".*?") role=(".*?")$', bar)
    check(m is not None and m.group(1) == "True" and m.group(2) != '""', "notices only: the bar shows while the host is paused")
    check(m is not None and (m.group(3) == "True") == (m.group(4) != '""'), "notices only: with no notice the bar hides")
    check(m is not None and m.group(5) == "True" and json.loads(m.group(6)).endswith(json.loads(m.group(7))),
          "the bar is back when the option is off and names the host as host")
    nh = ch.get("net", {})
    print(f"  info net host {nh} | guest {cg.get('net')}")
    check(nh.get("packets", 0) > 0 and nh.get("resent", 0) <= nh.get("packets", 1) * 0.05, "the host resent at most 5% of its datagrams")

    # The owner's bug: a device the guest put on its own bench by a button (no drop) stayed on the host's desk, and
    # the host could carry it off mid-repair. Now the guest notices the move and the host hides it.
    benched = moved(GUEST_COPY, "bench without a drop")
    print("  info " + benched)
    gl_all2 = stash.get("log_" + GUEST_COPY) or bepinex_log(GUEST_COPY)
    check("Placed" in benched and "on a bench -> sent" in gl_all2, "the guest's device went onto its bench without a drop, and the guest reported it")
    sdh2 = shop_h["coop"].get("devices", {})
    check(sdh2.get("remoteHidden", 0) >= 1, f"the host hides the device on the guest's bench ({sdh2.get('remoteHidden')} hidden for the guest)")

    # Cash: the customer's 123 on the host's desk shows as 123 on the guest; the guest's deposit lands in the one wallet.
    print("  info " + moved(HOST_COPY, "a customer paid") + " | " + moved(GUEST_COPY, "cash "))
    hl_all = bepinex_log(HOST_COPY)
    pile = moved(GUEST_COPY, "cash ")
    pile_sum = pile.split("pile of ")[1].split(" ")[0] if "pile of " in pile else "?"
    check("into the register" in pile and ("the guest put " + pile_sum + " into the register") in hl_all,
          "the guest saw the customer's 123 and put it into the register; the host put it in the wallet")
    check(shop_h["coop"].get("money") == shop_g["coop"].get("money"), f"one wallet after the cash ({shop_h['coop'].get('money')} / {shop_g['coop'].get('money')})")

    # Quests (the to-do list): one the guest completed is completed in the host's game (and so in its save).
    todo = moved(GUEST_COPY, "to-do ")
    print("  info " + todo)
    if "completed " in todo:
        todo_id = todo.split("completed ", 1)[1].strip()
        check(("the other player completed " + todo_id) in bepinex_log(HOST_COPY), f"the quest the guest completed is completed on the host too ({todo_id})")
    else:
        print("  info no open to-do item in the test save: quest completion by the guest was not exercised")

    # The end of the day by the bicycle: the host's click alone does not end it, the guest's click too does.
    print("  info " + moved(HOST_COPY, "bicycle") + " | " + moved(GUEST_COPY, "bicycle"))
    check("ready, the other not ready" in moved(HOST_COPY, "bicycle") and "co-op bot: bicycle clicked" in (stash.get("log_" + GUEST_COPY) or bepinex_log(GUEST_COPY)),
          "the host clicked the bicycle first and the day waited; then the guest clicked")

    # The end of day: the stamp means ready; the next day starts only when both stamped.
    dh_, dg_ = ch.get("dayReady", {}), cg.get("dayReady", {})
    print(f"  info day ready host {dh_} | guest {dg_}")
    check(dh_.get("both", 0) >= 1 and dg_.get("both", 0) >= 1 and dh_.get("waits", 0) + dg_.get("waits", 0) >= 1,
          "the first to stamp waited for the other; both went on once both stamped")
    # E-mail: the host's mail reaches the guest; with the host's permission the guest takes an order through the host.
    eh_, eg_ = shop_h["coop"].get("email", {}), shop_g["coop"].get("email", {})
    took = moved(GUEST_COPY, "e-mail order")
    print(f"  info email host {eh_} | guest {eg_} | {took}")
    if "asked for" in took:
        check(eg_.get("asked", 0) >= 1 and eh_.get("takenForGuest", 0) >= 1, "the guest's order was taken by the host for it, once")
    else:
        print("  info no order in the test save's inbox: the guest's order taking was not exercised")

    # Step 9: furniture. The host put another piece in a slot, the guest painted the walls: both see both.
    print("  info " + moved(HOST_COPY, "furniture") + " | " + moved(GUEST_COPY, "furniture"))
    uh, ug = shop_h["coop"].get("furniture", {}), shop_g["coop"].get("furniture", {})
    print(f"  info furniture host {uh} | guest {ug}")
    check(" -> " in moved(HOST_COPY, "furniture") and " -> " in moved(GUEST_COPY, "furniture"), "the host changed a piece of furniture, the guest painted the walls")
    check(uh.get("sent", 0) >= 1 and ug.get("applied", 0) >= 1 and ug.get("sent", 0) >= 1 and uh.get("applied", 0) >= 1,
          f"each side's furniture change reached the other ({uh}, {ug})")
    check(uh.get("hash") and uh.get("hash") == ug.get("hash"), f"the same furniture and walls on both ({uh.get('hash')} / {ug.get('hash')})")
    check(ch.get("furniture", {}).get("hash") and ch.get("furniture", {}).get("hash") == cg.get("furniture", {}).get("hash"),
          "the same furniture on the next day")
    devs_h = sorted(v for v in rh.values() if v and v[0].startswith("device:"))
    devs_g = sorted(v for v in rg.values() if v and v[0].startswith("device:"))
    check(devs_h and devs_h == devs_g, f"the devices sit in the same place on both ({devs_h[:2]})")
    lamp = [v for v in rh.values() if v and v[0].startswith("Lamp")]
    print(f"  info the lamp ended on {lamp[0][3] if lamp else '?'}")

    # Step 7: drop-outs. The guest's game was killed and started again; then the host left for the main menu.
    rh2, rg2 = (rejoin.get("host") or {}).get("coop", {}), (rejoin.get("guest") or {}).get("coop", {})
    check(rh2.get("rejoins", 0) >= 1 and rg2.get("localWorld", [-1])[0] > 0 and rh2.get("localWorld") == rg2.get("localWorld"),
          f"a guest that came back mid-game got the current save and the same shop (rejoins {rh2.get('rejoins')}, {rh2.get('localWorld')} / {rg2.get('localWorld')})")
    al = (rejoin.get("after_leave") or {}).get("coop", {})
    check(bool(rejoin.get("after_leave")) and "the host is gone" in bepinex_log(GUEST_COPY),
          "when the host left, the guest went back to the main menu by itself, its own save slot back")

    check(slot_files(GUEST, 1) == guest_own_before, "the guest's own slot 1 files are untouched")
    for copy in (HOST_COPY, GUEST_COPY):
        log = bepinex_log(copy)
        n = log.count("Exception")
        check(n == 0, f"{copy}: no exceptions in the BepInEx log ({n})")
    check(fingerprint_real() == real_before, "owner's real save folder unchanged")
    check(registry_export() == reg_before, "owner's real registry key unchanged")
    real_state.restore_registry()

    passed = sum(checks)
    print(f"{passed}/{len(checks)} checks passed")
    print("RESULT: " + ("PASS" if passed == len(checks) else "FAIL"))
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
