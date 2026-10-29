using System;
using Restory.UI.Presenters.PauseMenu;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // One pause for both (owner's choice): whoever opens the game's pause menu pauses the other player too. The game
    // pauses by showing GUI_PauseMenu (GamePauseState: time scale 0, sounds paused, the world takes no input), so
    // the other side opens its own pause menu, and the co-op bar says who paused. If that player closes it while the
    // other is still paused, it opens again; when the other resumes, it closes by itself. A pause this player opened
    // on its own stays until this player closes it.
    internal static class CoopPause
    {
        private static CoopSession session;
        private static bool remotePaused;   // the other player's pause menu is open
        private static bool ourOwnSent;     // what we last told the other side about our own pause
        private static bool forced;         // the pause menu here is open because of the other player

        public static bool RemotePaused => session != null && remotePaused;
        public static int TimesPausedByOther { get; private set; }
        public static int TimesResumedByOther { get; private set; }
        // Test bot: open or close the pause menu as the player would.
        public static bool BotPaused { set { var m = Menu; if (m == null) return; if (value) m.Show(); else m.Hide(); } }

        public static void Attach(CoopSession s)
        {
            session = s;
            remotePaused = false;
            ourOwnSent = false;
            forced = false;
            if (s != null) s.OnAction += (name, data) => { if (name == "pause" && data.Length > 0) remotePaused = data[0] == 1; };
        }

        private static GUI_PauseMenu Menu => SceneCache.Find<GUI_PauseMenu>(true);

        public static void Tick()
        {
            if (session == null || !session.Playing) return;
            var menu = Menu;
            if (menu == null || !Lobby.InGame)
            {
                forced = false;
                if (ourOwnSent) { ourOwnSent = false; session.SendAction("pause", new byte[] { 0 }); }
                return;
            }
            bool shown = menu.IsShown;
            // Ours: open, and not opened by us for the other player.
            bool own = shown && !forced;
            if (own != ourOwnSent)
            {
                ourOwnSent = own;
                session.SendAction("pause", new[] { (byte)(own ? 1 : 0) });
                Plugin.Log.LogInfo("co-op pause: " + (own ? "paused here, the other player pauses too" : "resumed here"));
            }
            // Not while the shop is still loading; once forced, again at once if this player closed it.
            if (remotePaused && !shown && (forced || CoopGame.InShopFor(1f)))
            {
                menu.Show();
                if (!forced) { TimesPausedByOther++; Plugin.Log.LogInfo("co-op pause: the other player paused"); }
                forced = true;
            }
            else if (!remotePaused && forced)
            {
                forced = false;
                if (shown) menu.Hide();
                TimesResumedByOther++;
                Plugin.Log.LogInfo("co-op pause: the other player resumed");
            }
        }
    }
}
