using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace RestoryVersus
{
    // Redirects Application.persistentDataPath (saves, backups, settings, third-party data)
    // to a folder of our choosing. Unity 6 generates a managed body for the getter, so one
    // prefix covers every managed caller. If the redirect cannot be proven, the process is
    // killed before the game's save services start.
    internal static class SaveIsolation
    {
        private static string redirected;

        public static string RealPath { get; private set; }
        public static string RedirectedPath => redirected;

        public static void Apply(Harmony harmony, string targetDir, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                RealPath = Application.persistentDataPath;
                var full = Path.GetFullPath(targetDir).Replace('\\', '/').TrimEnd('/');
                if (string.Equals(full, RealPath.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("save redirect target is the real save folder");
                Directory.CreateDirectory(full);
                redirected = full;

                var getter = AccessTools.PropertyGetter(typeof(Application), nameof(Application.persistentDataPath));
                if (getter == null) throw new MissingMethodException("Application.get_persistentDataPath");
                harmony.Patch(getter, prefix: new HarmonyMethod(typeof(SaveIsolation), nameof(PersistentDataPathPrefix)));

                var now = Application.persistentDataPath;
                if (now != redirected) throw new InvalidOperationException("redirect did not take: " + now);
                log.LogInfo("SAVE ISOLATION OK: persistentDataPath -> " + redirected + " (real: " + RealPath + ")");
            }
            catch (Exception e)
            {
                log.LogFatal("SAVE ISOLATION FAILED, killing the game so the real saves stay untouched: " + e);
                BepInEx.Logging.Logger.Listeners.Clear();
                Process.GetCurrentProcess().Kill();
            }
        }

        private static bool PersistentDataPathPrefix(ref string __result)
        {
            __result = redirected;
            return false;
        }
    }
}
