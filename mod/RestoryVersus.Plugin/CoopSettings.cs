using System;
using BepInEx.Configuration;
using HarmonyLib;
using Restory.Gameplay.Competitions;
using RestoryVersus.Core;

namespace RestoryVersus
{
    // The host's rules for the co-op game, changed on the co-op page (the "Online play" sign in the pause menu). The
    // host keeps them in its config and sends them when they change and whenever a guest starts playing; the guest
    // only reads them.
    //   GuestTakesOrders: the guest may accept e-mail orders (CoopEmail asks the host, who accepts it once).
    internal static class CoopSettings
    {
        private static ConfigEntry<bool> guestTakesOrders;
        private static CoopSession session;
        private static int sentForLaunch = -1;
        private static bool remoteGuestTakesOrders;

        public static void Init(ConfigFile config)
        {
            guestTakesOrders = config.Bind("Coop", "GuestTakesOrders", false, "Host: the guest may accept e-mail orders too (set on the co-op page in the game).");
        }

        public static bool GuestTakesOrders => session != null && !session.IsHost ? remoteGuestTakesOrders : guestTakesOrders != null && guestTakesOrders.Value;

        public static void Attach(CoopSession s)
        {
            session = s;
            sentForLaunch = -1;
            remoteGuestTakesOrders = false;
            if (s != null) s.OnAction += (name, data) =>
            {
                if (name == "settings" && !s.IsHost && data.Length > 0) remoteGuestTakesOrders = (data[0] & 1) != 0;
                else if (name == "compnote") CoopShop.Say("coopCompetitionNote");
            };
        }

        // Host only.
        public static void SetGuestTakesOrders(bool value)
        {
            if (session != null && !session.IsHost) return;
            guestTakesOrders.Value = value;
            sentForLaunch = -1;
            Plugin.Log.LogInfo("co-op settings: the guest " + (value ? "may" : "may not") + " take orders");
        }

        // A competition device ordered in co-op: the game's rule stays (one of each device on its way at a time, one
        // competition run); both players are told that racing each other is what Versus is for.
        [HarmonyPatch(typeof(CompetitionsApp), nameof(CompetitionsApp.TryCompleteRequestAndSubmitDevice))]
        private static class CompetitionNote
        {
            private static void Postfix(bool __result)
            {
                if (!__result || session == null || !session.Playing) return;
                CoopShop.Say("coopCompetitionNote");
                session.SendAction("compnote", Array.Empty<byte>());
            }
        }

        public static void Tick()
        {
            if (session == null || !session.IsHost || !session.Playing || session.Connection != ConnectionState.Connected) return;
            if (sentForLaunch == session.Launches) return;
            sentForLaunch = session.Launches;
            session.SendAction("settings", new[] { (byte)(guestTakesOrders.Value ? 1 : 0) });
        }
    }
}
