using System;
using System.Linq;
using HarmonyLib;
using Restory.Data.Analytics;

namespace RestoryVersus
{
    // Test runs (bots, diagnostics) are not players: nothing of them goes to the game's developers. Crash reports
    // were already off for bots (Plugin); the game's own analytics (RestoryAnalyticsService, Unity Services
    // Analytics) and the engine's session analytics are switched off too. The Steam build (1.0.033r) wrote analytics
    // and session files on every test copy's run. A player's own game is left as the game sets it.
    internal static class TestPrivacy
    {
        private static readonly string[] TestFlags =
            { "-versus-bot", "-coop-bot", "-coop-bot-ingame", "-coop-join-retry", "-versus-diag", "-versus-diag-dayend", "-versus-shots" };

        public static readonly bool IsTestRun = Environment.GetCommandLineArgs().Any(a => TestFlags.Contains(a));

        public static void Apply()
        {
            if (!IsTestRun) return;
            UnityEngine.CrashReportHandler.CrashReportHandler.enableCaptureExceptions = false;
            // The engine's analytics, by name: the module is not referenced at build time.
            var analytics = Type.GetType("UnityEngine.Analytics.Analytics, UnityEngine.UnityAnalyticsModule");
            foreach (var prop in new[] { "enabled", "deviceStatsEnabled", "initializeOnStartup" })
            {
                try { analytics?.GetProperty(prop)?.SetValue(null, false); }
                catch { }
            }
            var reporting = Type.GetType("UnityEngine.Analytics.PerformanceReporting, UnityEngine.UnityAnalyticsModule");
            try { reporting?.GetProperty("enabled")?.SetValue(null, false); }
            catch { }
            Plugin.Log.LogInfo("test run: analytics and crash reports off");
        }

        [HarmonyPatch(typeof(RestoryAnalyticsService), "StartDataCollection")]
        private static class NoCollection
        {
            private static bool Prefix() => !IsTestRun;
        }

        [HarmonyPatch(typeof(RestoryAnalyticsService), nameof(RestoryAnalyticsService.SendCustomEvent), new[] { typeof(string) })]
        private static class NoEvent
        {
            private static bool Prefix() => !IsTestRun;
        }

        [HarmonyPatch(typeof(RestoryAnalyticsService), nameof(RestoryAnalyticsService.SendCustomEvent), new[] { typeof(string), typeof(Restory.Data.Analytics.IAnalyticsParameter[]) })]
        private static class NoEventWithParameters
        {
            private static bool Prefix() => !IsTestRun;
        }
    }
}
