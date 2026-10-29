using System;
using HarmonyLib;
using Restory.Gameplay.Competitions;
using Restory.Gameplay.Elements;

namespace RestoryVersus
{
    // Bridges the game's contest (CompetitionGameMode) to the race session.
    //  - instance: captured when Zenject injects it (Construct) or it is re-enabled
    //  - start / stop / device reset: the mode's own C# events
    //  - finish: prefix on SuccessfullyCompleteCompetition, because that method calls Stop()
    //    (raising OnCompetitionStopped) before it raises OnCompetitionSuccessfullyCompleted
    //  - an "error" is a dropped part that misses a socket; the game answers with ResetDevice()
    //  - progress: sockets with a nested element / all sockets, polled while a device is up
    internal static class ContestHooks
    {
        private static readonly AccessTools.FieldRef<CompetitionGameMode, float> GameSeconds =
            AccessTools.FieldRefAccess<CompetitionGameMode, float>("gameSecondsTimer");

        private static CompetitionGameMode mode;

        public static Action<string, int, int> Prepared;   // deviceId, placed, total
        public static Action Started;
        public static Action Stopped;
        public static Action DeviceReset;
        public static Action<float> Finished;              // in-game seconds
        // False in a normal-mode race: a missed drop must not take the device apart.
        public static Func<bool> AllowReset = () => true;
        // False (the default) in a versus race: the normal device shadow instead of the contest's neon one.
        // The contest rug stays.
        public static Func<bool> KeepContestShadows = () => true;

        // Found by name: game 1.0.033r dropped the contest's neon shadow (and this field), nothing to undo there.
        private static readonly System.Reflection.FieldInfo ShadowField = AccessTools.Field(typeof(CompetitionGameMode), "deviceShadow");
        private static bool saidNoShadow;

        // ResolvePlacedDeviceChanged sets the contest rug and the neon shadow (DeviceShadow, the only shadow decal
        // in the game) when a contest device lands on the bench; put the normal shadow colour back right after.
        [HarmonyPatch(typeof(CompetitionGameMode), "ResolvePlacedDeviceChanged")]
        private static class PlainShadowPatch
        {
            private static void Postfix(CompetitionGameMode __instance)
            {
                if (!__instance.HasDeviceInCompetition || KeepContestShadows()) return;
                if (ShadowField == null) { if (!saidNoShadow) { saidNoShadow = true; Plugin.Log.LogInfo("versus: this game version has no contest shadow, nothing to replace"); } return; }
                var shadow = ShadowField?.GetValue(__instance);
                if (shadow == null) return;
                AccessTools.Method(shadow.GetType(), "SetDefaultDeviceShadow")?.Invoke(shadow, null);
                Plugin.Log.LogInfo("versus: normal device shadow instead of the contest's neon one");
            }
        }

        public static CompetitionGameMode Mode => mode;

        private static readonly AccessTools.FieldRef<CompetitionGameMode, Restory.UI.Presenters.CompetitionStates.GUI_CompetitionTimer> Timer =
            AccessTools.FieldRefAccess<CompetitionGameMode, Restory.UI.Presenters.CompetitionStates.GUI_CompetitionTimer>("guiCompetitionTimer");
        private static readonly AccessTools.FieldRef<Restory.UI.Presenters.CompetitionStates.GUI_CompetitionTimer, TMPro.TMP_Text> TimerText =
            AccessTools.FieldRefAccess<Restory.UI.Presenters.CompetitionStates.GUI_CompetitionTimer, TMPro.TMP_Text>("timerText");
        private static string lastMatText;
        public static string MatText => lastMatText ?? "";

        // The contest timer on the mat counts game time, which is stopped in versus, so it sat at 00:00. In versus
        // it shows the race time instead (the game's own UpdateTimer is skipped there, see VersusProfile).
        public static void ShowRaceTimeOnMat(RestoryVersus.Core.RaceSession s)
        {
            if (mode == null || !VersusProfile.Active || s == null) return;
            var timer = Timer(mode);
            if (timer == null) return;
            var text = TimerText(timer);
            if (text == null) return;
            long ms = s.Local.Phase == RestoryVersus.Core.RacePhase.Racing || s.Local.Phase == RestoryVersus.Core.RacePhase.Finished ? s.Local.ElapsedMs : 0;
            var value = TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss");
            if (value == lastMatText && text.text == value) return;
            lastMatText = value;
            text.text = value;
        }
        public static bool Attached => mode != null;
        // Between the game's contest start and its stop/completion.
        public static bool Racing { get; private set; }

        public static float InGameSeconds => mode != null ? GameSeconds(mode) : 0f;

        public static string CurrentDeviceId
        {
            get
            {
                var c = mode != null ? mode.CurrentDeviceInCompetition : null;
                return c != null && c.Device != null && c.Device.Info != null ? c.Device.Info.ID : "";
            }
        }

        public static bool TryCountParts(out int placed, out int total)
        {
            placed = 0;
            total = 0;
            var c = mode != null ? mode.CurrentDeviceInCompetition : null;
            if (c == null || c.Device == null) return false;
            var sockets = c.Device.ElementSockets;
            if (sockets == null) return false;
            for (int i = 0; i < sockets.Count; i++)
            {
                ElementSocket s = sockets[i];
                if (s == null) continue;
                total++;
                if (s.NestedElement != null) placed++;
            }
            return true;
        }

        private static void Attach(CompetitionGameMode instance)
        {
            if (instance == null || ReferenceEquals(instance, mode)) return;
            Detach();
            mode = instance;
            mode.OnCompetitionPrepared += HandlePrepared;
            mode.OnCompetitionStarted += HandleStarted;
            mode.OnCompetitionStopped += HandleStopped;
            mode.OnDeviceReset += HandleReset;
            Plugin.Log.LogInfo("attached to CompetitionGameMode " + instance.GetInstanceID());
        }

        private static void Detach()
        {
            if (mode == null) return;
            mode.OnCompetitionPrepared -= HandlePrepared;
            mode.OnCompetitionStarted -= HandleStarted;
            mode.OnCompetitionStopped -= HandleStopped;
            mode.OnDeviceReset -= HandleReset;
            mode = null;
        }

        private static void HandlePrepared()
        {
            TryCountParts(out var placed, out var total);
            Plugin.Log.LogInfo($"contest prepared: device={CurrentDeviceId} parts={placed}/{total} t={InGameSeconds:0.0}");
            Prepared?.Invoke(CurrentDeviceId, placed, total);
        }

        private static void HandleStarted()
        {
            Plugin.Log.LogInfo("contest started: device=" + CurrentDeviceId);
            Racing = true;
            Started?.Invoke();
        }

        private static void HandleStopped()
        {
            Plugin.Log.LogInfo("contest stopped");
            Racing = false;
            Stopped?.Invoke();
        }

        private static void HandleReset()
        {
            Plugin.Log.LogInfo("contest device reset (missed drop)");
            DeviceReset?.Invoke();
        }

        // The game resets the whole contest device when a dragged part is dropped outside its socket
        // (ResolveElementStoppedDragging -> ResetDeviceAfterEndOfFrameCoroutine -> ResetDevice). In a normal-mode
        // race that drop just leaves the part on the table.
        [HarmonyPatch(typeof(CompetitionGameMode), "ResetDevice")]
        private static class NoResetPatch
        {
            private static bool Prefix(CompetitionGameMode __instance)
            {
                if (AllowReset()) return true;
                Plugin.Log.LogInfo("normal mode: missed drop, device not reset");
                return false;
            }
        }

        [HarmonyPatch(typeof(CompetitionGameMode), "Construct")]
        private static class ConstructPatch
        {
            private static void Postfix(CompetitionGameMode __instance) => Attach(__instance);
        }

        [HarmonyPatch(typeof(CompetitionGameMode), "OnEnable")]
        private static class OnEnablePatch
        {
            private static void Postfix(CompetitionGameMode __instance) => Attach(__instance);
        }

        [HarmonyPatch(typeof(CompetitionGameMode), "OnDisable")]
        private static class OnDisablePatch
        {
            private static void Postfix(CompetitionGameMode __instance)
            {
                if (ReferenceEquals(__instance, mode)) Detach();
            }
        }

        [HarmonyPatch(typeof(CompetitionGameMode), nameof(CompetitionGameMode.SuccessfullyCompleteCompetition))]
        private static class CompletePatch
        {
            private static void Prefix(CompetitionGameMode __instance)
            {
                Attach(__instance);
                var secs = GameSeconds(__instance);
                Plugin.Log.LogInfo($"contest completed: device={CurrentDeviceId} in-game {secs:0.0}s");
                Finished?.Invoke(secs);
            }
        }
    }
}
