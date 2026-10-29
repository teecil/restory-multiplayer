using System;
using HarmonyLib;
using Restory.TimeSystems;
using Restory.UI.Presenters.DayEndWindow;
using RestoryVersus.Core;

namespace RestoryVersus
{
    // The stamp on the end-of-day screen means "ready for the next day". The game goes on to the next day only when
    // both players stamped; until then the co-op bar shows "Ready for the next day: 1/2". (The owner's game: the
    // guest stamped first, its shop opened while the host was still on the screen, and its time stood still.)
    // Each end of day has a number the host gives (sent before its end-of-day save), so a stamp is never counted for
    // another day.
    // The same for ending the day: the bicycle means "ready to end the day", clicking it again takes that back, and
    // the host's game ends the day once both clicked (owner: the host must not end the day in the middle of the
    // guest's repair). A day that ends by the clock ends as before.
    internal static class CoopDayReady
    {
        private static readonly System.Reflection.MethodInfo NextDay = AccessTools.Method(typeof(GUI_DayEndWindow), "ResolveStartNextDayActionPerformed");

        private static CoopSession session;
        private static int hostDayNo;       // host: its own count; guest: the host's, from "dayno"
        private static int windowNo = -1;   // the end of day the open screen belongs to
        private static int localReady = -1, remoteReady = -1;
        private static GUI_DayEndWindow waitingWindow;
        private static bool passing;
        private static bool windowOpen;
        private static readonly System.Reflection.MethodInfo Bicycle = AccessTools.Method(typeof(DayEnderFromPlayerInteraction), "ResolveBicycleClicked");
        private static bool bikeLocal, bikeRemote, passingBike;
        private static DayEnderFromPlayerInteraction bikeEnder;

        public static int Waits { get; private set; }
        public static int BothReady { get; private set; }

        public static void Attach(CoopSession s)
        {
            session = s;
            waitingWindow = null;
            windowOpen = false;
            if (s == null) return;
            bikeLocal = bikeRemote = false;
            bikeEnder = null;
            s.OnAction += (name, data) =>
            {
                if (name == "bike" && data.Length > 0) { bikeRemote = data[0] == 1; TryEndDay(); return; }
                if (data.Length < 4) return;
                int n = BitConverter.ToInt32(data, 0);
                if (name == "dayno" && !s.IsHost) hostDayNo = Math.Max(hostDayNo, n);
                else if (name == "dayready") { remoteReady = Math.Max(remoteReady, n); TryGo(); }
            };
        }

        private static bool Linked => session != null && session.Launches > 0 && session.Connection == ConnectionState.Connected;

        // Host, when its day ends (before its end-of-day save goes out).
        public static void HostDayEnds()
        {
            if (session == null || !session.IsHost) return;
            hostDayNo++;
            session.SendAction("dayno", BitConverter.GetBytes(hostDayNo));
        }

        // For the co-op bar: "" when there is nothing to say.
        public static string Status(Func<string, string> T, string otherName)
        {
            if (!windowOpen || session == null || windowNo < 0) return "";
            int n = (localReady >= windowNo ? 1 : 0) + (remoteReady >= windowNo ? 1 : 0);
            if (n == 0) return "";
            string text = T("coopDayReady").Replace("{n}", n.ToString());
            if (localReady >= windowNo && remoteReady < windowNo) text += " · " + T("coopDayReadyWait").Replace("{name}", otherName);
            return text;
        }

        public static bool ShowStatus => windowOpen && session != null && session.Launches > 0;

        // For the co-op bar in the shop: "End the day: 1/2 · waiting for <name>", or "".
        public static string BikeStatus(Func<string, string> T, string otherName)
        {
            if (session == null || !Linked || (!bikeLocal && !bikeRemote)) return "";
            int n = (bikeLocal ? 1 : 0) + (bikeRemote ? 1 : 0);
            string text = T("coopBikeReady").Replace("{n}", n.ToString());
            if (bikeLocal && !bikeRemote) text += " · " + T("coopBikeWait").Replace("{name}", otherName);
            else if (!bikeLocal && bikeRemote) text += " · " + T("coopBikeYourTurn");
            return text;
        }

        // The bicycle: true lets the game end the day now.
        public static bool BicycleClicked(DayEnderFromPlayerInteraction ender)
        {
            if (passingBike || !Linked) return true;
            bikeEnder = ender;
            bikeLocal = !bikeLocal;
            session.SendAction("bike", new[] { (byte)(bikeLocal ? 1 : 0) });
            Plugin.Log.LogInfo("co-op day: bicycle " + (bikeLocal ? "clicked: ready to end the day" : "clicked again: not ready any more"));
            if (bikeLocal && bikeRemote && session.IsHost) { Ended(); return true; }
            return false;
        }

        // The shop's bicycle handler (a plain class the scene's container makes), for the test bot.
        private static DayEnderFromPlayerInteraction sceneEnder;

        [HarmonyPatch(typeof(DayEnderFromPlayerInteraction), nameof(DayEnderFromPlayerInteraction.Initialize))]
        private static class EnderCapture
        {
            private static void Postfix(DayEnderFromPlayerInteraction __instance) => sceneEnder = __instance;
        }

        public static string BotClickBicycle()
        {
            if (sceneEnder == null) return "no bicycle in this shop";
            Bicycle?.Invoke(sceneEnder, null);
            return "clicked (" + (bikeLocal ? "ready" : "not ready") + ", the other " + (bikeRemote ? "ready" : "not ready") + ")";
        }

        // Host: both clicked the bicycle.
        private static void TryEndDay()
        {
            if (session == null || !session.IsHost || !bikeLocal || !bikeRemote || bikeEnder == null) return;
            Ended();
            passingBike = true;
            try { Bicycle?.Invoke(bikeEnder, null); }
            finally { passingBike = false; }
        }

        private static void Ended()
        {
            bikeLocal = bikeRemote = false;
            Plugin.Log.LogInfo("co-op day: both clicked the bicycle, the day ends");
        }

        private static void TryGo()
        {
            if (waitingWindow == null || localReady < windowNo || remoteReady < windowNo) return;
            var w = waitingWindow;
            waitingWindow = null;
            BothReady++;
            Plugin.Log.LogInfo("co-op day: both stamped, on to the next day");
            passing = true;
            try { NextDay?.Invoke(w, null); }
            finally { passing = false; }
        }

        // The other player left while this one waited: no one to wait for.
        public static void Tick()
        {
            // The end-of-day screen goes with its scene (no Hide): the shop of the next day means it is closed.
            if (windowOpen && Lobby.Global != null && Lobby.Global.IsInGameLoop) windowOpen = false;
            // Out of the shop (the day is ending, or a new one loading): nobody's bicycle counts any more.
            if (!CoopGame.InShopFor(0.5f)) { bikeLocal = bikeRemote = false; bikeEnder = null; }
            if (waitingWindow != null && !Linked)
            {
                var w = waitingWindow;
                waitingWindow = null;
                passing = true;
                try { NextDay?.Invoke(w, null); }
                finally { passing = false; }
            }
        }

        [HarmonyPatch(typeof(GUI_DayEndWindow), nameof(GUI_DayEndWindow.Show))]
        private static class ShowPatch
        {
            private static void Postfix()
            {
                windowOpen = true;
                windowNo = hostDayNo;
            }
        }

        [HarmonyPatch(typeof(GUI_DayEndWindow), nameof(GUI_DayEndWindow.Hide))]
        private static class HidePatch
        {
            private static void Postfix() => windowOpen = false;
        }

        [HarmonyPatch(typeof(GUI_DayEndWindow), "ResolveStartNextDayActionPerformed")]
        private static class StampPatch
        {
            private static bool Prefix(GUI_DayEndWindow __instance)
            {
                if (passing || !Linked) return true;
                if (localReady < windowNo)
                {
                    localReady = windowNo;
                    session.SendAction("dayready", BitConverter.GetBytes(windowNo));
                    Plugin.Log.LogInfo("co-op day: stamped here (" + windowNo + ")");
                }
                if (remoteReady >= windowNo) { BothReady++; return true; }
                waitingWindow = __instance;
                Waits++;
                Plugin.Log.LogInfo("co-op day: waiting for the other player's stamp");
                return false;
            }
        }
    }
}
