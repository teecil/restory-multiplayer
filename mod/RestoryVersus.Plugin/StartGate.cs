using System;
using HarmonyLib;
using Restory.Gameplay.Disassemble.StateMachine;

namespace RestoryVersus
{
    // Holds the contest intro until both racers are at the bench.
    // Entering the bench with a contest device enters StartCompetitionState, whose Enter() plays the
    // game's own 3-2-1-GO and then starts the contest. While a race session is connected we skip that
    // Enter, report "ready", and replay it when the shared countdown fires, so both copies show the
    // same 3-2-1-GO at the same moment. Leaving the bench calls Exit(), which withdraws "ready".
    internal static class StartGate
    {
        public static Func<bool> ShouldHold = () => false;
        public static Action<string> Held;
        // How long the intro was held when the bench was left (seconds).
        public static Action<float> Cancelled;
        private static float heldAt;

        private static StartCompetitionState held;
        private static bool passthrough;

        public static bool IsHolding => held != null;

        // Plays the held intro. False when nothing is held.
        public static bool Release()
        {
            if (held == null) return false;
            var state = held;
            held = null;
            passthrough = true;
            try { state.Enter(); }
            finally { passthrough = false; }
            Plugin.Log.LogInfo("start gate released: playing 3-2-1-GO");
            return true;
        }

        [HarmonyPatch(typeof(StartCompetitionState), nameof(StartCompetitionState.Enter))]
        private static class EnterPatch
        {
            private static bool Prefix(StartCompetitionState __instance)
            {
                if (passthrough || !ShouldHold()) return true;
                held = __instance;
                heldAt = UnityEngine.Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("start gate holding the contest intro for " + ContestHooks.CurrentDeviceId);
                Held?.Invoke(ContestHooks.CurrentDeviceId);
                return false;
            }
        }

        [HarmonyPatch(typeof(StartCompetitionState), nameof(StartCompetitionState.Exit))]
        private static class ExitPatch
        {
            private static void Prefix(StartCompetitionState __instance)
            {
                if (!ReferenceEquals(__instance, held)) return;
                held = null;
                Plugin.Log.LogInfo("start gate: left the bench while waiting");
                Cancelled?.Invoke(UnityEngine.Time.realtimeSinceStartup - heldAt);
            }
        }
    }
}
