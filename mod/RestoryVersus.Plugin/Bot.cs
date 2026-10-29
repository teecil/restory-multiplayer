using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Restory.Gameplay.Competitions;
using Restory.Gameplay.Disassemble.StateMachine;
using Restory.Gameplay.Elements;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Test racer, enabled with -versus-bot. It uses the same lobby and launch path as a player: lobby
    // actions are presses on the versus menu's own buttons (VersusMenu.Click: only works when the button
    // is visible and enabled). In the race it drives real game objects, but not the mouse:
    //   connected -> the plugin loads the workshop from the main menu
    //   host: chooses a device and presses Start once the client is ready; client: presses Ready
    //   Start -> Lobby.Launch: race device on the bench, DeviceContainer.Activate (bench camera,
    //   disassembly mode), StartGate holds the intro, shared countdown, the game's 3-2-1-GO
    //   assembly: ElementSocket.AttachElement part by part, one deliberate miss through the game's
    //   own ResetDevice(), then the disassembly state machine enters EndCompetitionState (what the
    //   game does after the last part), which calls SuccessfullyCompleteCompetition
    //   after the result: back to the lobby (leave the bench, race device removed)
    // Not covered: the drag UI itself.
    internal sealed class Bot
    {
        private static readonly System.Reflection.MethodInfo ResetDevice = AccessTools.Method(typeof(CompetitionGameMode), "ResetDevice");
        private static readonly AccessTools.FieldRef<CompetitionGameMode, PlacedElementsHandler> Placed =
            AccessTools.FieldRefAccess<CompetitionGameMode, PlacedElementsHandler>("placedElementsHandler");
        private static readonly AccessTools.FieldRef<CompetitionGameMode, DisassembleStateMachine> StateMachine =
            AccessTools.FieldRefAccess<CompetitionGameMode, DisassembleStateMachine>("disassembleStateMachine");

        private readonly MonoBehaviour host;
        private readonly Func<RaceSession> session;
        private readonly Func<VersusMenu> menu;
        private readonly string wantedDevice;
        private readonly float secondsPerPart;
        private readonly int quitAfterSeconds;
        private readonly RaceMode[] modes;
        private readonly bool[] rematches;
        private readonly Plugin owner;

        public string Stage { get; private set; } = "starting";
        public bool BackInLobby { get; private set; }
        public bool Left { get; private set; }
        public int ProfileDuringRace { get; private set; }
        public string PickedAvatar { get; private set; } = "";

        public Bot(Plugin host, Func<RaceSession> session, Func<VersusMenu> menu, string wantedDevice, float secondsPerPart, int quitAfterSeconds, string modes)
        {
            this.host = host;
            owner = host;
            var steps = modes.Split(',');
            this.modes = new RaceMode[steps.Length];
            rematches = new bool[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                var st = steps[i].Trim().ToLowerInvariant();
                rematches[i] = st == "rematch" && i > 0;
                this.modes[i] = rematches[i] ? this.modes[i - 1] : st == "hardcore" ? RaceMode.Hardcore : RaceMode.Normal;
            }
            this.session = session;
            this.menu = menu;
            this.wantedDevice = wantedDevice;
            this.secondsPerPart = secondsPerPart;
            this.quitAfterSeconds = quitAfterSeconds;
        }

        public void Start()
        {
            KeepRunningInBackground = true;
            Application.runInBackground = true;
            host.StartCoroutine(Run());
        }

        // The game turns runInBackground on for fades and saves and back off afterwards
        // (RunInBackgroundSolver), which froze the unfocused copy on its intro in the first two-copy run.
        public static bool KeepRunningInBackground;

        [HarmonyPatch(typeof(Restory.Data.SaveLoad.RunInBackgroundSolver), nameof(Restory.Data.SaveLoad.RunInBackgroundSolver.OnConcurrentTasksFinished))]
        private static class KeepBackgroundPatch
        {
            private static bool Prefix() => !KeepRunningInBackground;
        }

        private void Say(string stage)
        {
            Stage = stage;
            Plugin.Log.LogInfo("[bot] " + stage);
        }

        private IEnumerator Run()
        {
            var s = session();
            Say("waiting for the opponent");
            while (s.Connection != ConnectionState.Connected) yield return new WaitForSecondsRealtime(0.5f);

            // Stage 1, in the main menu: the client picks an avatar and gets ready; the host's "Choose device"
            // is locked until then and takes both to the versus workshop.
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!s.CanGoWorkshop)
                {
                    if (menu().Click("goworkshop")) { Say("FAIL Choose device was clickable before the opponent was ready"); yield break; }
                    Say("lobby: Choose device is disabled until the opponent is ready (ok)");
                }
                float until = Time.realtimeSinceStartup + 240f;
                while (!s.CanGoWorkshop)
                {
                    if (Time.realtimeSinceStartup > until) { Say("FAIL opponent not ready in the menu after 240 s"); yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                yield return Press("goworkshop", "lobby: clicked Choose device");
                if (Stage.StartsWith("FAIL")) yield break;
            }
            else
            {
                var icons = VersusProfile.Icons();
                if (icons.Count > 1)
                {
                    PickedAvatar = icons[1].Key;
                    yield return Press("avatar:" + PickedAvatar, "lobby: clicked avatar " + PickedAvatar);
                    if (Stage.StartsWith("FAIL")) yield break;
                }
                yield return Press("ready", "lobby: clicked READY in the menu", 60f);
                if (Stage.StartsWith("FAIL")) yield break;
            }

            for (int i = 0; i < modes.Length; i++)
            {
                yield return OneRace(s, modes[i], i == 0, rematches[i], i + 1 < modes.Length && rematches[i + 1]);
                if (Stage.StartsWith("FAIL")) yield break;
            }

            // Leave versus: out to the main menu, and the player's own save profile comes back.
            yield return new WaitForSecondsRealtime(2f);
            yield return Press("leave", "clicked Leave");
            if (Stage.StartsWith("FAIL")) yield break;
            float deadline = Time.realtimeSinceStartup + 120f;
            while (!(Lobby.InMainMenu(out _) && VersusProfile.CurrentProfile != VersusProfile.Slot) && Time.realtimeSinceStartup < deadline)
                yield return new WaitForSecondsRealtime(0.5f);
            Left = Lobby.InMainMenu(out _) && VersusProfile.CurrentProfile != VersusProfile.Slot;
            Say("DONE races=" + races.Count + " backInLobby=" + BackInLobby + " left=" + Left + " profile=" + VersusProfile.CurrentProfile);

            if (quitAfterSeconds > 0)
            {
                yield return new WaitForSecondsRealtime(quitAfterSeconds);
                Application.Quit();
            }
        }

        public static bool Bounce = System.Linq.Enumerable.Contains(Environment.GetCommandLineArgs(), "-versus-bot-bounce");
        private bool bounced;

        private IEnumerator OneRace(RaceSession s, RaceMode raceMode, bool first, bool isRematch, bool nextIsRematch)
        {
            Say("race " + (races.Count + 1) + " (" + raceMode + (isRematch ? ", rematch" : "") + "): waiting for the workshop");
            float deadline = Time.realtimeSinceStartup + 240f;
            while (!isRematch && (!Lobby.CanRace || !owner.InLobby))
            {
                if (Time.realtimeSinceStartup > deadline) { Say("FAIL not in the lobby/workshop after 240 s: " + Lobby.WhyNotReady); yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            string modeButton = raceMode == RaceMode.Hardcore ? "mode:hardcore" : "mode:normal";

            if (isRematch) { /* the rematch starts by itself once both voted */ }
            else if (s.IsHost)
            {
                var available = RaceDevices.Available();
                if (available.Count == 0) { Say("FAIL no contest devices"); yield break; }
                string id = string.IsNullOrEmpty(wantedDevice) ? available[0].ID : wantedDevice;
                // Before any device is chosen START must be locked (checked here, before the client can react).
                if (first && !s.CanLaunch)
                {
                    if (menu().Click("start")) { Say("FAIL START was clickable before a device and the opponent were ready"); yield break; }
                    Say("lobby: START is disabled until the opponent is ready (ok)");
                }
                yield return Press("device:" + id, "lobby: clicked device " + id);
                if (Stage.StartsWith("FAIL")) yield break;
                yield return Press(modeButton, "lobby: clicked mode " + raceMode);
                if (Stage.StartsWith("FAIL")) yield break;
                yield return new WaitForSecondsRealtime(0.5f);
                deadline = Time.realtimeSinceStartup + 240f;
                while (!s.CanLaunch)
                {
                    if (Time.realtimeSinceStartup > deadline) { Say("FAIL opponent not ready after 240 s"); yield break; }
                    // Someone else changed the choice (a stray real click in the first profile-9 run): take it back.
                    if (s.LobbyDevice != id && menu().Click("device:" + id)) Say("lobby: device was changed, clicked " + id + " again");
                    if (s.LobbyMode != raceMode && menu().Click(modeButton)) Say("lobby: mode was changed, clicked " + raceMode + " again");
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                yield return new WaitForSecondsRealtime(1f);
                yield return Press("start", "lobby: clicked START");
                if (Stage.StartsWith("FAIL")) yield break;
            }
            else
            {
                yield return Press("ready", "lobby: clicked READY for " + s.LobbyDevice + " (" + s.LobbyMode + ")", 120f);
                if (Stage.StartsWith("FAIL")) yield break;
            }

            Say("waiting for GO");
            deadline = Time.realtimeSinceStartup + 240f;
            while (!ContestHooks.Racing)
            {
                // Test: drop out of the bench once right after sitting down (what a failed start looked like); the
                // plugin must sit back down and the race must still start.
                if (Bounce && !bounced && StartGate.IsHolding)
                {
                    bounced = true;
                    var disassemble = UnityEngine.Object.FindAnyObjectByType<Restory.Gameplay.Disassemble.DisassembleGameMode>(FindObjectsInactive.Include);
                    Say("test: leaving the bench while waiting for GO");
                    disassemble?.ExecuteExit();
                }
                if (Time.realtimeSinceStartup > deadline) { Say("FAIL contest did not start after 240 s (" + Lobby.Status + ")"); yield break; }
                // The host changed device or mode after our Ready, which withdraws it: confirm again like a player.
                if (!s.IsHost && !s.LocalLobbyReady && s.LobbyDevice != "" && menu().Click("ready"))
                    Say("lobby: choice changed, clicked READY again for " + s.LobbyDevice + " (" + s.LobbyMode + ")");
                yield return new WaitForSecondsRealtime(0.25f);
            }

            ProfileDuringRace = VersusProfile.CurrentProfile;
            Say("racing " + s.RaceMode + " (save profile " + ProfileDuringRace + ", tools owned " + VersusProfile.ToolsGranted + ")");
            var mode = ContestHooks.Mode;
            bool missed = false;
            int beforeMiss = -1, afterMiss = -1;
            int guard = 0;
            while (guard++ < 500)
            {
                ContestHooks.TryCountParts(out var placed, out var total);
                if (placed >= total) break;
                if (!missed && placed >= Math.Max(1, total * 2 / 5))
                {
                    missed = true;
                    beforeMiss = placed;
                    // What follows a part dropped outside its socket (normal mode skips it).
                    ResetDevice.Invoke(mode, null);
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    ContestHooks.TryCountParts(out afterMiss, out _);
                    Say("deliberate miss at " + beforeMiss + "/" + total + " -> " + afterMiss + "/" + total + " after ResetDevice");
                    yield return new WaitForSecondsRealtime(secondsPerPart);
                    continue;
                }
                if (!AttachOne(mode)) { Say("FAIL no part fits any empty socket at " + placed + "/" + total); yield break; }
                yield return new WaitForSecondsRealtime(secondsPerPart);
            }

            Say("assembled: EndCompetitionState");
            StateMachine(mode).Enter<EndCompetitionState>();

            deadline = Time.realtimeSinceStartup + 60f;
            while (!s.Result().Decided && Time.realtimeSinceStartup < deadline)
                yield return new WaitForSecondsRealtime(0.5f);
            var r = s.Result();
            races.Add(new RaceRecord
            {
                Mode = s.RaceMode.ToString(), Device = s.Local.DeviceId, RaceNo = s.Local.RaceNo,
                LocalMs = s.Local.ElapsedMs, LocalResets = s.Local.Resets, LocalScore = s.ScoreMs(s.Local),
                RemoteMs = s.Remote != null ? s.Remote.ElapsedMs : -1, RemoteResets = s.Remote != null ? s.Remote.Resets : -1,
                RemoteScore = s.Remote != null ? s.ScoreMs(s.Remote) : -1,
                Decided = r.Decided, LocalWins = r.LocalWins, PartsBeforeMiss = beforeMiss, PartsAfterMiss = afterMiss,
            });
            Say("result " + s.RaceMode + " decided=" + r.Decided + " localWins=" + r.LocalWins + " reason=" + r.Reason);

            // Let EndCompetitionState play its animation, then go on like a player pressing a button.
            yield return new WaitForSecondsRealtime(4f);
            if (nextIsRematch)
            {
                yield return Press("rematch", "clicked REMATCH");
                yield break;
            }
            yield return Press("back", "clicked Back to lobby");
            if (Stage.StartsWith("FAIL")) yield break;
            deadline = Time.realtimeSinceStartup + 20f;
            while (!(RaceDevices.BenchEmpty && s.Local.Phase == RacePhase.Idle) && Time.realtimeSinceStartup < deadline)
                yield return new WaitForSecondsRealtime(0.5f);
            BackInLobby = RaceDevices.BenchEmpty && s.Local.Phase == RacePhase.Idle;
            Say("back in the lobby=" + BackInLobby);
        }

        private sealed class RaceRecord
        {
            public string Mode, Device;
            public int RaceNo, LocalResets, RemoteResets, PartsBeforeMiss, PartsAfterMiss;
            public long LocalMs, LocalScore, RemoteMs, RemoteScore;
            public bool Decided, LocalWins;
        }

        private readonly List<RaceRecord> races = new List<RaceRecord>();

        public string RacesJson()
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < races.Count; i++)
            {
                var r = races[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"mode\":\"").Append(r.Mode).Append("\",\"device\":\"").Append(r.Device).Append("\",\"race\":").Append(r.RaceNo)
                  .Append(",\"localMs\":").Append(r.LocalMs).Append(",\"localResets\":").Append(r.LocalResets).Append(",\"localScore\":").Append(r.LocalScore)
                  .Append(",\"remoteMs\":").Append(r.RemoteMs).Append(",\"remoteResets\":").Append(r.RemoteResets).Append(",\"remoteScore\":").Append(r.RemoteScore)
                  .Append(",\"decided\":").Append(r.Decided ? "true" : "false").Append(",\"localWins\":").Append(r.LocalWins ? "true" : "false")
                  .Append(",\"partsBeforeMiss\":").Append(r.PartsBeforeMiss).Append(",\"partsAfterMiss\":").Append(r.PartsAfterMiss).Append('}');
            }
            return sb.Append(']').ToString();
        }

        // Presses a menu button, retrying for up to 30 s while it is hidden or disabled.
        private IEnumerator Press(string id, string done, float timeout = 30f)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (true)
            {
                var m = menu();
                if (m != null)
                {
                    m.Show();
                    if (m.Click(id)) { Say(done); yield break; }
                }
                if (Time.realtimeSinceStartup > deadline) { Say("FAIL menu button '" + id + "' not clickable after " + timeout + " s"); yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        // Puts one loose part into an empty socket made for it, in the device's own element order.
        private static bool AttachOne(CompetitionGameMode mode)
        {
            var device = mode.CurrentDeviceInCompetition.Device;
            var loose = new List<ElementBase>();
            var placed = Placed(mode).GetPlacedElements();
            foreach (var rec in placed.ElementsOnSurface) if (rec.Element != null && !rec.Element.InSocket) loose.Add(rec.Element);
            foreach (var rec in placed.ElementsInBin) if (rec.Element != null && !rec.Element.InSocket) loose.Add(rec.Element);

            var sockets = new List<ElementSocket>(device.ElementSockets);
            var order = device.ElementsOrderMap;
            sockets.Sort((a, b) =>
            {
                int oa = order != null && a.CompatibleElementInfo != null && order.TryGetValue(a.CompatibleElementInfo, out var x) ? x : int.MaxValue;
                int ob = order != null && b.CompatibleElementInfo != null && order.TryGetValue(b.CompatibleElementInfo, out var y) ? y : int.MaxValue;
                return oa.CompareTo(ob);
            });
            foreach (var socket in sockets)
            {
                if (socket == null || socket.NestedElement != null) continue;
                foreach (var el in loose)
                {
                    if (el.Info != socket.CompatibleElementInfo) continue;
                    socket.AttachElement(el);
                    return true;
                }
            }
            return false;
        }
    }
}
