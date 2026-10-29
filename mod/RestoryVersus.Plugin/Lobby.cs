using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Restory.Gameplay.Competitions;
using Restory.Gameplay.Disassemble;
using Restory.Infrastructure.StateMachine;
using Restory.UI.Presenters.MainMenu;
using UnityEngine;

namespace RestoryVersus
{
    // Game-side steps of the lobby flow:
    //   connected in the main menu -> Continue (the menu's own handler) so both copies sit in the workshop
    //   Start -> race device on the bench -> DeviceContainer.Activate() (what a click on the bench device
    //            does: camera to the bench, disassembly mode, StartCompetitionState) -> StartGate holds the
    //            intro until both are there -> shared countdown -> the game's own 3-2-1-GO
    //   Back to lobby -> leave the bench (DisassembleGameMode.ExecuteExit) -> destroy the race device
    internal static class Lobby
    {
        private static readonly MethodInfo ContinueMenu = AccessTools.Method(typeof(GUI_MainMenu), "ResolveContinueMenu");

        private static GUI_MainMenu continuedMenu;
        private static float workshopSince = -1;
        private static float menuSince = -1;

        // The game's global state machine (main menu, loading, game loop, pause...), captured when built.
        internal static GlobalStateMachine Global;

        [HarmonyPatch]
        private static class GlobalStateCapture
        {
            private static MethodBase TargetMethod() => AccessTools.FirstConstructor(typeof(GlobalStateMachine), c => c.GetParameters().Length > 0);
            private static void Postfix(GlobalStateMachine __instance)
            {
                Global = __instance;
                __instance.OnActiveStateChanged += () =>
                    Plugin.Log.LogInfo("global state: " + (__instance.ActiveState == null ? "none" : __instance.ActiveState.GetType().Name));
            }
        }

        // Launch progress for the menu: a key it translates, plus a technical detail for failures.
        public static string StatusKey { get; private set; } = "";
        public static string StatusDetail { get; private set; } = "";
        public static string Status => StatusKey == "" ? "" : StatusKey + (StatusDetail == "" ? "" : ": " + StatusDetail);

        private static void SetStatus(string key, string detail = "")
        {
            StatusKey = key;
            StatusDetail = detail;
        }
        public static bool Launching { get; private set; }

        // In the game loop of the workshop (not loading, not paused) for a moment, so the save has finished
        // loading. The first lobby build used a 5 s timer instead; pressing Continue too early then broke the
        // loading screen and the game never reached GameLoopState, so the bench could not be entered.
        public static bool InWorkshop
        {
            get
            {
                bool now = Global != null && Global.IsInGameLoop && RaceDevices.InGameplay && ContestHooks.Attached;
                if (!now) { workshopSince = -1; return false; }
                if (workshopSince < 0) workshopSince = Time.realtimeSinceStartup;
                return Time.realtimeSinceStartup - workshopSince > 2f;
            }
        }

        // In a save (the shop's game loop, or its pause menu): where the pause menu's "Online play" sign is.
        public static bool InGame => Global != null && (Global.IsInGameLoop || Global.ActiveState is Restory.Infrastructure.StateMachine.States.GamePauseState);

        public static bool CanRace => InWorkshop && (RaceDevices.BenchEmpty || RaceDevices.BenchHasRaceDevice);

        public static string WhyNotReady
        {
            get
            {
                if (!RaceDevices.InGameplay) return "loading the workshop...";
                if (Global != null && !Global.IsInGameLoop) return "close menus and windows (pause, PC, dialogue) to race";
                if (!InWorkshop) return "workshop is loading...";
                if (!CanRace) return "your bench is busy: take your device off the bench first";
                return "";
            }
        }

        // The main menu is up, idle (MainMenuState, not still closing the previous loading screen) and has
        // been for a second. Continue does not have to be visible: the versus profile brings its own save,
        // so a player without any save of their own can race too.
        public static bool InMainMenu(out GUI_MainMenu menu)
        {
            menu = null;
            bool idle = Global != null && Global.ActiveState is MainMenuState;
            if (idle) menu = SceneCache.Find<GUI_MainMenu>(false);
            bool now = idle && menu != null && menu.IsInitialized && (menu.ContinueButtonVisibility || VersusProfile.HasTemplate);
            if (!now) { menuSince = -1; return false; }
            if (menuSince < 0) menuSince = Time.realtimeSinceStartup;
            return Time.realtimeSinceStartup - menuSince > 1f;
        }

        // Loads the versus workshop once per main menu instance.
        public static bool TryContinueFromMainMenu()
        {
            if (!InMainMenu(out var menu) || ReferenceEquals(menu, continuedMenu)) return false;
            if (!VersusProfile.EnterFromMainMenu()) return false;
            continuedMenu = menu;
            Plugin.Log.LogInfo("main menu: Continue (versus)");
            ContinueMenu.Invoke(menu, null);
            return true;
        }

        // Co-op: Continue from the main menu with whatever slot is selected (the host's own, or the guest's copy
        // of it). False while the menu is not ready yet.
        public static bool ContinueNow()
        {
            if (!InMainMenu(out var menu)) return false;
            continuedMenu = menu;
            Plugin.Log.LogInfo("main menu: Continue (co-op, " + SaveScheme.Name + " " + VersusProfile.CurrentProfile + ")");
            ContinueMenu.Invoke(menu, null);
            return true;
        }

        public static IEnumerator Launch(string deviceId)
        {
            if (Launching) yield break;
            Launching = true;
            try
            {
                SetStatus("startingWorkshop");
                float deadline = Time.realtimeSinceStartup + 120f;
                while (!CanRace)
                {
                    if (Time.realtimeSinceStartup > deadline) { Fail("benchBusy", "not in the workshop or bench busy after 120 s"); yield break; }
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                if (RaceDevices.BenchHasRaceDevice) RaceDevices.DestroyRaceDevice();

                if (!RaceDevices.TrySpawn(deviceId, out var error)) { Fail("spawnFailed", error); yield break; }

                deadline = Time.realtimeSinceStartup + 10f;
                while (!(ContestHooks.Mode != null && ContestHooks.Mode.HasDeviceInCompetition))
                {
                    if (Time.realtimeSinceStartup > deadline) { Fail("raceNotReady", "the game did not prepare the contest"); yield break; }
                    yield return null;
                }

                SetStatus("startingBench");
                var container = ContestHooks.Mode.CurrentDeviceInCompetition;
                container.Activate();

                deadline = Time.realtimeSinceStartup + 15f;
                while (!StartGate.IsHolding && !ContestHooks.Racing)
                {
                    if (Time.realtimeSinceStartup > deadline) { Fail("raceNotReady", "did not reach the bench"); yield break; }
                    yield return null;
                }
                SetStatus("");
            }
            finally
            {
                Launching = false;
            }
        }

        public static IEnumerator ReturnToLobby(Action done)
        {
            var disassemble = UnityEngine.Object.FindAnyObjectByType<DisassembleGameMode>(FindObjectsInactive.Include);
            if (disassemble != null && ContestHooks.Mode != null && ContestHooks.Mode.HasDeviceInCompetition)
            {
                disassemble.ExecuteExit();
                yield return new WaitForSecondsRealtime(1.5f); // camera back to the workshop
            }
            if (RaceDevices.BenchHasRaceDevice) RaceDevices.DestroyRaceDevice();
            SetStatus("");
            done?.Invoke();
        }

        // code: what the menu shows (VersusMenu.ErrorText); why: what the log says.
        private static void Fail(string code, string why)
        {
            SetStatus("startFailed", code);
            Plugin.Log.LogError("launch failed: " + why);
        }
    }
}
