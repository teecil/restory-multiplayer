# ReStory Multiplayer

Two-player online play for **ReStory: Chill Electronics Repairs** (Mandragora / tinyBuild, Steam app 3812600), as a
[BepInEx 5](https://github.com/BepInEx/BepInEx) plugin:

- **Co-op**: a friend joins the host's save. One shared shop, one wallet, one parts stock; each player has their own
  bench.
- **Versus**: both players get the same broken device and race to assemble it.

Tested with game **1.0.033r** (Steam, the Fuzzby update of 2026-10-27). Player instructions in Russian:
[mod/dist/README_RU.txt](mod/dist/README_RU.txt). The mod follows the game's language: all 9 of the game's languages
are covered.

This repository holds the mod's own code only: no game files, no saves, no decompiled game code.

## Status

| | |
|---|---|
| Co-op | works; automated two-copy test 75/75 checks |
| Co-op started from inside a save | works; automated test 11/11 |
| Versus | works; automated test 70/70 (three races, rematch) |
| Core (network, sync rules) | headless tests 235/235 |
| Played by people | the author and a friend on two PCs over the internet, connected through Porthole (IP route): a versus match, then several co-op days with the friend as host; their logs found most of the hard bugs (devices missing on the guest, the host's upload saturated, the guest's bench device taken by the host, customer cash lost), all fixed and added to the bot tests. These sessions were on game build 1.0.015r; 1.0.033r is covered by the automated tests |
| Connection by IP | tested (two copies on one PC, and two PCs over the internet) |
| Connection through Steam (lobby + invite) | **not tested** with two real Steam accounts; marked as such in the game's menu |

## What is shared in co-op

The host's game is the authority; the guest's actions are sent to the host, which applies them and sends the
outcome back.

- The room: objects and devices moved by either player, objects in a player's hand, furniture and wall colour.
- What the game makes on its own on the host (a customer's device, bills, cash, licences): a room list every 2 s
  catches anything no event reported.
- The courier's box, the customers' box and the personal box, with their contents; taking something out is struck
  from the other copy.
- The parts stock (only changes travel after the first full stock) and one wallet; the guest can buy with it.
- Cash from customers (with its sum) and putting it into the register.
- Mail and e-mail orders: the host may let the guest take orders; an order can be taken only once.
- Quests (the game's to-do list): completed by either player, completed in the host's save.
- Time follows the host. The day ends when **both** clicked the bicycle; the next day starts when **both** stamped
  the end-of-day screen. One pause for both.
- Each player's bench is their own: what lies there is hidden from the other player.

## Install (players)

1. Download the release zip (BepInEx 5.4.23.5 + the plugin, no game files).
2. Unzip everything into the game folder, next to `Restory.exe`, with the game closed.
3. Start the game: the main menu has a new "Online play" sign (also in the pause menu).

Both players need the same mod version (shown on the "Online play" page). The host needs an open UDP port 27515
(LAN, port forwarding, or a tunnel / virtual LAN: Porthole is the one tested), or both use the Steam route (not tested yet).

## Build

Requirements: .NET SDK 8, the game installed, BepInEx 5.4.23.5 extracted to
`downloads/bepinex-5.4.23.5-extracted/` (from the [official release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)).

```
dotnet build mod/RestoryVersus.Plugin -c Release -p:GameDir="<game dir>"
```

The plugin compiles against the game's own `Restory_Data/Managed` assemblies. `python -I scripts/make_dist.py`
packs BepInEx, the plugin and the Russian README into `mod/dist/RestoryVersus-<version>.zip` and refuses if a game
file or a save slips in.

## Tests

- Headless (network, sync rules, fake Steam link, 200 ms round trip traffic): `dotnet run --project mod/RestoryMultiplayer.Tests -c Release`.
- Two real game copies with bots: put two copies of the game (with BepInEx and the plugin) in
  `test-installs/restory-steam-a` and `-b` (or set `RESTORY_HOST_COPY` / `RESTORY_GUEST_COPY`), then
  `python -I scripts/run_coop_test.py`, `run_coop_ingame_test.py`, `run_race_test.py`. Each copy plays from a copy
  of the player's saves in `test_saves/`; the tests check that the real saves are not touched and that the
  game's analytics are off in test runs.
- `scripts/check_game_members.py <decompiled game dir>` lists every game member the mod finds by name that is
  missing in a game version (run it after a game update).
- `scripts/play_two_copies.ps1`: two copies side by side for manual testing.

## Known issues

- Work the guest leaves on its own bench at the end of the day is not kept: the next day starts from the host's
  save, where that device is still in the room.
- Moving the customers' box or the personal box away from its spot is not shared (rare).
- When the host's mail changes while the guest has the mail app open, the guest's list is rebuilt.
- The new devices of 1.0.033r (Fuzzby, PlayMachine controller) were not part of the automated tests.
- Two players only.

## Credits

Built on [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX). Made
with the [coop-anything](https://github.com/cranebeaker/coop-anything) playbook. ReStory is © Mandragora / tinyBuild;
this is a fan mod, not affiliated with them.
