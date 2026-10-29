using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Restory.Data.Localization;
using Restory.Gameplay.GameSettings;
using RestoryVersus.Core;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RestoryVersus
{
    // The versus window (uGUI + the game's own TextMeshPro font), one centered window with three views:
    //   Connect (offline): name, address, Host / Join
    //   Lobby (connected): both players, the device list (host picks), Ready (client) / START (host)
    //   Result (after a race): win/lose, both times, Back to lobby
    // plus a small HUD in the top-right corner during the race. Right Shift shows/hides the window.
    // The window sits on a full-screen dimmer that takes the clicks, so they do not reach the workshop
    // (the game's UICursorDetector checks EventSystem.IsPointerOverGameObject).
    internal sealed partial class VersusMenu : MonoBehaviour
    {
        public static VersusMenu Instance { get; private set; }

        private static readonly HarmonyLib.AccessTools.FieldRef<Restory.UI.Views.MainMenu.GUI_MainMenuView, Button> NewGameButtonField =
            HarmonyLib.AccessTools.FieldRefAccess<Restory.UI.Views.MainMenu.GUI_MainMenuView, Button>("newGameButton");
        private static readonly HarmonyLib.AccessTools.FieldRef<Restory.UI.Views.MainMenu.GUI_MainMenuView, Button> LoadGameButtonField =
            HarmonyLib.AccessTools.FieldRefAccess<Restory.UI.Views.MainMenu.GUI_MainMenuView, Button>("loadGameButton");
        private Button gameMenuButton;
        private TextMeshProUGUI titleLabel;
        private float nextGameMenuCheck;
        private enum View { None, Connect, Lobby, Race, Result, CoopLobby, CoopPlaying }
        // Offline the page is a small stack: Choose (versus or co-op) -> Connect (host / join) for either mode.
        private enum OfflinePage { Choose, Connect, CoopConnect }
        private OfflinePage offlinePage = OfflinePage.Choose;
        // Set when the player leaves a lobby: the page stays up on Connect once the main menu is back.
        private bool reopenConnect;

        // Palette: dark panel, warm accent (the game's cozy shop light), green for ready/win.
        // The game's paper menus (Settings, Load game): dimmer #28172796, dark grey ink #565656, blue controls
        // #738FC7 with cream text #E6DFCC, cards on the SettingsPanel sprite.
        private static readonly Color Dim = new Color32(40, 23, 39, 150);
        private static readonly Color PanelBg = Color.white;
        private static readonly Color CardBg = new Color32(236, 231, 220, 255);
        private static readonly Color CardHi = new Color32(222, 214, 198, 255);
        private static readonly Color Accent = new Color32(115, 143, 199, 255);
        private static readonly Color AccentText = new Color32(230, 223, 204, 255);
        private static readonly Color Green = new Color32(79, 138, 85, 255);
        private static readonly Color Red = new Color32(181, 82, 74, 255);
        private static readonly Color TextMain = new Color32(86, 86, 86, 255);
        private static readonly Color TextMuted = new Color32(140, 133, 122, 255);
        private static readonly Color Disabled = new Color32(189, 182, 168, 255);
        private static readonly Color OpponentBar = new Color32(217, 145, 91, 255);

        // Parts of the game's own menus, found by name among the loaded sprites (the Settings and Load game
        // panels exist in the main menu and the workshop). Missing parts fall back to the mod's drawn shapes.
        private static class Kit
        {
            public static Sprite Page, Tab, BackIcon, Card, Button, ButtonFace, BarBg, BarFill, Star, Lines, Cat;
            public static TMP_FontAsset Font;

            public static void Load()
            {
                var map = new Dictionary<string, Sprite>();
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sp != null && !map.ContainsKey(sp.name)) map[sp.name] = sp;
                Sprite Get(string n) => map.TryGetValue(n, out var found) ? found : null;
                Page = Get("Notebook-Right");
                Tab = Get("Tab-blue");
                BackIcon = Get("BackIcon");
                Card = Get("SettingsPanel");
                Button = Get("dropdown-shadow");
                ButtonFace = Get("dropdown-main");
                BarBg = Get("ProgressBarBgl-SettingMenu");
                BarFill = Get("ProgressBarFill-SettingMenu");
                Star = Get("star");
                Lines = Get("lines");
                Cat = Get("ScaredCat");
                foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (f != null && f.name == "ShantellSans-Light SDF") Font = f;
                Plugin.Log.LogInfo("menu kit: page=" + (Page != null) + " tab=" + (Tab != null) + " card=" + (Card != null)
                    + " button=" + (Button != null) + "/" + (ButtonFace != null) + " bar=" + (BarBg != null) + "/" + (BarFill != null)
                    + " font=" + (Font != null));
            }
        }

        private Plugin plugin;
        private Sprite round;
        private TMP_FontAsset font;
        // The game's language as a short code: en, ru, es, fr, de, zh, ja, ko, pt (others fall back to en).
        private string lang = "en";
        private float nextLangCheck;
        private bool built;

        private GameObject root, window, viewChoose, viewConnect, viewCoopLobby, viewLobby, viewResult, hud;
        private Button routeSteamButton, routeIpButton, joinButton, inviteButton, coopInviteButton;
        private GameObject ipSection;
        private TextMeshProUGUI steamHint, portHintLabel;
        private TextMeshProUGUI connectIntro, coopHint, coopYouName, coopYouBadge, coopOtherName, coopOtherBadge;
        private Image coopYouBadgeBg, coopOtherBadgeBg, coopYouAvatar, coopOtherAvatar;
        private Button coopStartButton;
        // The co-op page while playing (the pause menu's "Online play" sign): settings, avatar, leave, close.
        private bool sessionPageOpen;
        private bool? leaveButtonPlaying;
        private GameObject coopRulesBox, coopSessionBox;
        private Button coopOrdersToggle, coopCloseButton, coopLeaveButton;
        private RectTransform coopAvatarGrid;
        private readonly Dictionary<string, Image> coopAvatarBg = new Dictionary<string, Image>();
        private GameObject coopHud;
        private TextMeshProUGUI coopHudLine, coopHudNotice;
        private Image coopHudAvatar;
        private TextMeshProUGUI subtitle, connectError, lobbyHint, lobbyDeviceTitle, lobbyStatus, resultTitle, resultLine1, resultLine2, resultReason;
        private TextMeshProUGUI cardYouName, cardYouBadge, cardOppName, cardOppBadge;
        private Image cardYouBadgeBg, cardOppBadgeBg;
        private TMP_InputField nameInput, addressInput;
        private Button startButton, readyButton;
        private TextMeshProUGUI readyLabel;
        private RectTransform deviceGrid;
        private readonly List<string> shownDevices = new List<string>();
        private readonly Dictionary<string, Button> buttons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Image> deviceBg = new Dictionary<string, Image>();
        private HudRow hudYou, hudOpp;
        private TextMeshProUGUI hudStatus;
        private TextMeshProUGUI banner;
        private Image modeNormalBg, modeHardBg;
        private GameObject profileSection, workshopSection;
        private Button goWorkshopButton;
        private Image cardYouAvatar, cardOppAvatar;
        private RectTransform avatarGrid;
        private readonly Dictionary<string, Image> avatarBg = new Dictionary<string, Image>();
        private TMP_InputField lobbyNameInput;
        private TextMeshProUGUI modeNormalLabel, modeHardLabel, modeHint, penaltyHintLabel;
        private float nextCursorCheck;

        private View view = View.None;
        private bool windowShown = true;
        private string shotDir;
        private int shotCount;
        private float shotAt = -1;
        private string shotName;

        public bool WindowVisible => window != null && window.activeSelf;
        private int exitHandledFrame = -1;

        // The back tab: one page back (connect or co-op -> choice -> closed), out of the lobby to the connect page,
        // or back to the lobby after a race.
        private void OnBack()
        {
            if (view == View.Connect) BackOffline();
            else if (view == View.Lobby) LeaveToConnect();
            else if (view == View.Result) { plugin.Session.VoteRematch(false); plugin.BackToLobby(); }
            else if (view == View.CoopLobby) LeaveCoop();
            else if (view == View.CoopPlaying) sessionPageOpen = false;
        }

        // An accepted Steam invite: show the network page (the lobby replaces it once connected); coop null = the
        // mode is not known yet.
        public void OpenNetworkPage(bool? coop)
        {
            windowShown = true;
            reopenConnect = true;
            if (coop.HasValue) offlinePage = coop.Value ? OfflinePage.CoopConnect : OfflinePage.Connect;
            else if (offlinePage == OfflinePage.Choose) offlinePage = OfflinePage.Connect;
        }

        private void LeaveCoop()
        {
            plugin.Coop.Stop();
            offlinePage = OfflinePage.CoopConnect;
            reopenConnect = true;
        }

        private void BackOffline()
        {
            if (offlinePage == OfflinePage.Choose) windowShown = false;
            else offlinePage = OfflinePage.Choose;
        }

        // Leaving the lobby ends the session (from the versus workshop that is a trip through the loading screen
        // to the main menu); the page then comes back on Connect rather than closing.
        private void LeaveToConnect()
        {
            plugin.StartOffline();
            offlinePage = OfflinePage.Connect;
            reopenConnect = true;
        }

        // Esc while the page is up: one page back offline; in the lobby and result Esc is swallowed so the game's
        // pause menu does not open under the page (leaving takes the back tab or Back).
        private bool ConsumeExit()
        {
            if (!WindowVisible) return false;
            exitHandledFrame = Time.frameCount;
            if (view == View.Connect) BackOffline();
            else if (view == View.CoopPlaying) sessionPageOpen = false;
            return true;
        }

        [HarmonyLib.HarmonyPatch(typeof(Restory.EventSystems.ExitEvents.ExitEventDispatcher), "ResolveExitButtonJustPressed")]
        private static class ExitPatch
        {
            private static bool Prefix() => !(Instance != null && Instance.ConsumeExit());
        }

        private bool ignorePointer;

        // ignoreRealPointer: test bots press buttons directly; with it the window does not take real mouse
        // clicks (a background copy received the owner's clicks from another game in one run).
        public void Init(Plugin owner, string screenshotDir, bool ignoreRealPointer = false)
        {
            plugin = owner;
            shotDir = screenshotDir;
            Instance = this;
            ignorePointer = ignoreRealPointer;
        }

        // ----- per frame -----

        private void Update()
        {
            Perf.Begin();
            try { UpdateMenu(); }
            finally { Perf.End(); }
        }

        private void UpdateMenu()
        {
            if (plugin == null || plugin.Session == null) return;
            if (!built) { if (!TryBuild()) return; }
            if (Time.unscaledTime >= nextLangCheck)
            {
                nextLangCheck = Time.unscaledTime + 2f;
                if (GameLanguage() != lang) { Rebuild(); return; }
            }

            // Esc normally reaches us through the game's own back dispatcher (ExitPatch); this is the fallback for
            // scenes without one.
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && exitHandledFrame != Time.frameCount
                && SceneCache.Find<Restory.EventSystems.ExitEvents.ExitEventDispatcher>(false) == null)
                ConsumeExit();
            if (Time.unscaledTime >= nextGameMenuCheck) { nextGameMenuCheck = Time.unscaledTime + 1f; EnsureGameMenuButton(); EnsurePauseMenuButton(); }

            var next = CurrentView();
            if (next != view)
            {
                // Open the window on every lobby/result/connect change, hide it when the race starts.
                // The connect window waits for the "Network game" sign in the main menu (or Right Shift);
                // lobby and result open by themselves, the race hides the window.
                // A guest sent back by the host leaving: open the co-op page, which says what happened.
                // A session that ended with an error (the host left, no answer, ...): stay on the page that says why.
                if (next == View.Connect && view != View.None)
                {
                    if (!string.IsNullOrEmpty(plugin.Coop.Error)) { reopenConnect = true; offlinePage = OfflinePage.CoopConnect; }
                    else if (!string.IsNullOrEmpty(plugin.NetError)) { reopenConnect = true; offlinePage = OfflinePage.Connect; }
                }
                windowShown = next == View.Connect ? reopenConnect : next != View.Race && next != View.CoopPlaying;
                reopenConnect = false;
                view = next;
                if (shotDir != null) { shotAt = Time.unscaledTime + 0.8f; shotName = next.ToString().ToLowerInvariant(); }
            }

            // Connect: opened from the "Network game" sign, closed with the back tab. Lobby and result are the
            // screens of a versus session: always up, except during the race.
            // The offline pages wait until the player is out of the versus workshop (after Back in its lobby).
            bool inWorkshop = Lobby.Global != null && Lobby.Global.IsInGameLoop;
            if (view != View.CoopPlaying) sessionPageOpen = false;
            bool visible = view == View.Connect ? windowShown && !inWorkshop : view == View.CoopPlaying ? sessionPageOpen : view != View.Race;
            // The race result has its own screen (VersusResultView), in the style of the end-of-day one.
            window.SetActive(visible && view != View.Result);
            if (view == View.Result) { EnsureResultWindow(); resultWin.SetActive(true); RefreshResultWindow(); }
            else if (resultWin != null) resultWin.SetActive(false);
            viewChoose.SetActive(view == View.Connect && offlinePage == OfflinePage.Choose);
            viewConnect.SetActive(view == View.Connect && offlinePage != OfflinePage.Choose);
            viewCoopLobby.SetActive(view == View.CoopLobby || (view == View.CoopPlaying && sessionPageOpen));
            bool coopPage = view == View.CoopLobby || view == View.CoopPlaying || (view == View.Connect && offlinePage == OfflinePage.CoopConnect);
            titleLabel.text = view == View.Connect && offlinePage == OfflinePage.Choose ? T("gameMenuButton")
                : coopPage ? T("coopTitle") : T("versusTitle");
            viewLobby.SetActive(view == View.Lobby);
            viewResult.SetActive(view == View.Result);
            hud.SetActive(view == View.Race);
            // Also over the pause menu, where it says who paused.
            coopHud.SetActive(view == View.CoopPlaying && Lobby.Global != null && (Lobby.InGame || CoopDayReady.ShowStatus) && !sessionPageOpen);
            if (coopHud.activeSelf) RefreshCoopHud();

            if (view == View.Connect) RefreshConnect();
            else if (view == View.Lobby) RefreshLobby();
            else if (view == View.Result) RefreshResult();
            else if (view == View.CoopLobby || (view == View.CoopPlaying && sessionPageOpen)) RefreshCoopLobby();
            if (hud.activeSelf) RefreshHud();
            // The mod version is always visible: two players on different versions cannot play together.
            subtitle.text = SubtitleText() + "  ·  v" + Plugin.Version;
            banner.text = BannerText();
            banner.gameObject.SetActive(banner.text != "");
            if (Time.unscaledTime >= nextCursorCheck) { nextCursorCheck = Time.unscaledTime + 3f; RaiseCursor(); }

            if (shotAt > 0 && Time.unscaledTime >= shotAt)
            {
                shotAt = -1;
                StartCoroutine(Shot(Path.Combine(shotDir, $"shot_{++shotCount:00}_{shotName}.png")));
            }
        }

        // ScreenCapture.CaptureScreenshot keeps the back buffer's alpha, which translucent UI lowers, so the
        // first screenshots looked see-through. Capture as a texture and force it opaque: what the monitor shows.
        private System.Collections.IEnumerator Shot(string path)
        {
            yield return new WaitForEndOfFrame();
            Save(path);
            // The same frame without the window, to compare the dimmer around it.
            if (window.activeSelf)
            {
                root.SetActive(false);
                yield return new WaitForEndOfFrame();
                Save(path.Replace(".png", "_bare.png"));
                root.SetActive(true);
            }
        }

        private static void Save(string path)
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Plugin.Log.LogInfo("screenshot " + path);
        }

        private View CurrentView()
        {
            var s = plugin.Session;
            var coop = plugin.Coop.Session;
            // Once started it is the game, also while a save is resent (each new day, a rejoin): no lobby over it.
            if (coop != null) return coop.Launches > 0 ? View.CoopPlaying : View.CoopLobby;
            if (plugin.Transport == null) return View.Connect;
            if (plugin.InLobby) return View.Lobby;
            bool over = s.Local.Phase == RacePhase.Finished || s.Local.Phase == RacePhase.Abandoned
                // The opponent dropped mid-race: show the result window, it has "Back to lobby".
                || (s.Connection == ConnectionState.Lost && s.Local.Phase == RacePhase.Racing);
            return over ? View.Result : View.Race;
        }

        // The game's mouse cursor is a RawImage (VirtualCursorView) on an ordinary game canvas, so the window
        // (top sorting layer) covered it. Give the cursor image its own canvas on the top layer, one order above
        // the window. Cursor views come and go with scenes, so this runs every second.
        private void RaiseCursor()
        {
            var layers = SortingLayer.layers;
            int top = layers.Length > 0 ? layers[layers.Length - 1].id : 0;
            foreach (var view in FindObjectsByType<Restory.UserInterface.VirtualCursorView>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var c = view.GetComponent<Canvas>();
                if (c != null && c.overrideSorting && c.sortingOrder == 32767) continue;
                if (c == null) c = view.gameObject.AddComponent<Canvas>();
                c.overrideSorting = true;
                c.sortingLayerID = top;
                c.sortingOrder = 32767;
                Plugin.Log.LogInfo("mouse cursor raised above the versus window (" + view.name + ")");
            }
        }

        // Bot hook: presses a button the way a player would. False if it is hidden or disabled.
        public bool Click(string id)
        {
            if (!buttons.TryGetValue(id, out var b) || b == null) return false;
            if (!b.gameObject.activeInHierarchy || !b.interactable) return false;
            b.onClick.Invoke();
            return true;
        }

        // Bots press buttons on the page: open it, on the connect page when offline.
        public void Show()
        {
            windowShown = true;
            if (view == View.Connect && offlinePage == OfflinePage.Choose) offlinePage = OfflinePage.Connect;
            if (view == View.CoopPlaying) windowShown = false;
        }

        public void HideForDiagnostics() => windowShown = false;
        public void DiagHost() => plugin.StartHost(plugin.Port);
        public void DiagLeave() => plugin.StartOffline();

        // A sign in the game's own main menu, made from the "New game" sign so it has the game's art, font,
        // hover presets and click sound. It sits in the same stack (a VerticalLayoutGroup under the pole), right
        // after "Load game". The sign's own localisation is switched off so it keeps our label.
        private void EnsureGameMenuButton()
        {
            if (gameMenuButton != null) return; // Unity's null: the main menu scene was unloaded
            var view = SceneCache.Find<Restory.UI.Views.MainMenu.GUI_MainMenuView>(false);
            if (view == null) return;
            var newGame = NewGameButtonField(view);
            if (newGame == null) return;
            var load = LoadGameButtonField(view);
            var go = Instantiate(newGame.gameObject, newGame.transform.parent);
            go.name = "GUI_VersusNetworkGameButton";
            go.SetActive(true);
            int after = Mathf.Max(newGame.transform.GetSiblingIndex(), load != null ? load.transform.GetSiblingIndex() : 0);
            go.transform.SetSiblingIndex(after + 1);
            foreach (var lt in go.GetComponentsInChildren<Restory.UserInterface.GUI_LocalisedText>(true)) lt.IsEnabled = false;
            var label = go.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = T("gameMenuButton");
                // The window title in the menu signs' own font (ShantellSans), so the tab and its window match.
                // (The page title keeps the menus' page font, ShantellSans-Light, like the game's own pages.)
            }
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => { windowShown = true; offlinePage = OfflinePage.Choose; });
            b.interactable = true;
            gameMenuButton = b;
            Plugin.Log.LogInfo("\"" + T("gameMenuButton") + "\" added to the game's main menu");
        }

        // The same sign in the pause menu (Esc in a save), made from its "Load" button, right after it: the
        // network game can start from inside a save.
        private static readonly HarmonyLib.AccessTools.FieldRef<Restory.UI.Views.PauseMenu.GUI_PauseMenuView, Button> PauseLoadField =
            HarmonyLib.AccessTools.FieldRefAccess<Restory.UI.Views.PauseMenu.GUI_PauseMenuView, Button>("loadButton");
        private Button pauseMenuButton;

        private void EnsurePauseMenuButton()
        {
            if (pauseMenuButton != null)
            {
                // While a versus race runs there is nothing to change here; in co-op it opens the co-op page.
                pauseMenuButton.gameObject.SetActive(plugin.Transport == null);
                return;
            }
            var view = SceneCache.Find<Restory.UI.Views.PauseMenu.GUI_PauseMenuView>(true);
            if (view == null) return;
            var load = PauseLoadField(view);
            if (load == null) return;
            var go = Instantiate(load.gameObject, load.transform.parent);
            go.name = "GUI_VersusNetworkGameButton";
            go.SetActive(true);
            go.transform.SetSiblingIndex(load.transform.GetSiblingIndex() + 1);
            foreach (var lt in go.GetComponentsInChildren<Restory.UserInterface.GUI_LocalisedText>(true)) lt.IsEnabled = false;
            var label = go.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = T("gameMenuButton");
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() =>
            {
                if (plugin.Coop.Session != null && plugin.Coop.Session.Playing) { sessionPageOpen = true; return; }
                windowShown = true;
                offlinePage = OfflinePage.Choose;
            });
            b.interactable = true;
            pauseMenuButton = b;
            Plugin.Log.LogInfo("\"" + T("gameMenuButton") + "\" added to the pause menu");
        }

        public bool PauseMenuButtonPresent => pauseMenuButton != null;

        public bool ClickPauseMenuButton()
        {
            if (pauseMenuButton == null) return false;
            pauseMenuButton.onClick.Invoke();
            return true;
        }

        // For the diagnostics run: press the sign like a player.
        public bool ClickGameMenuButton()
        {
            if (gameMenuButton == null || !gameMenuButton.gameObject.activeInHierarchy) return false;
            gameMenuButton.onClick.Invoke();
            return true;
        }

        public bool GameMenuButtonPresent => gameMenuButton != null;

        // ----- refresh -----

        private void RefreshConnect()
        {
            if (buttons.TryGetValue("host", out var hb)) hb.interactable = !Plugin.Broken;
            if (buttons.TryGetValue("join", out var jb)) jb.interactable = !Plugin.Broken;
            bool coopMode = offlinePage == OfflinePage.CoopConnect;
            connectIntro.text = coopMode ? T("coopConnectIntro") : T("connectIntro");
            string error = coopMode ? plugin.Coop.Error : plugin.NetError;
            connectError.text = string.IsNullOrEmpty(error) ? "" : ErrorText(error);
            bool steamUp = SteamNet.Available, steam = SteamRoute;
            SetToggle(routeSteamButton, steam);
            SetToggle(routeIpButton, !steam);
            routeSteamButton.interactable = steamUp && !Plugin.Broken;
            steamHint.gameObject.SetActive(steam || !steamUp);
            steamHint.text = !steamUp ? T("steamUnavailable") : T("steamHint");
            ipSection.SetActive(!steam);
            joinButton.gameObject.SetActive(!steam);
            portHintLabel.gameObject.SetActive(!steam);
        }

        // Steam picked and running; without Steam the page falls back to the address.
        private bool SteamRoute => plugin.UseSteam && SteamNet.Available;

        private void SetToggle(Button b, bool on)
        {
            b.GetComponent<Image>().color = on ? Accent : CardHi;
            var label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.color = on ? AccentText : TextMain;
        }

        private void RefreshLobby()
        {
            var s = plugin.Session;
            bool host = s.IsHost;
            bool connected = s.Connection == ConnectionState.Connected;

            bool menuStage = s.Stage == LobbyStage.Menu;
            profileSection.SetActive(menuStage);
            workshopSection.SetActive(!menuStage);
            SetAvatar(cardYouAvatar, plugin.Avatar);
            SetAvatar(cardOppAvatar, connected ? s.RemoteAvatar : "");
            if (menuStage) RefreshProfile();

            cardYouName.text = plugin.RacerName;
            SetBadge(cardYouBadge, cardYouBadgeBg, host ? T("host") : (s.LocalLobbyReady ? T("ready") : T("notReady")),
                host ? Accent : (s.LocalLobbyReady ? Green : Disabled), host || s.LocalLobbyReady ? AccentText : TextMain);
            cardOppName.text = connected ? (string.IsNullOrEmpty(s.RemoteName) ? T("opponent") : s.RemoteName) : (host ? T("waitingJoin") : T("connecting"));
            if (!connected) SetBadge(cardOppBadge, cardOppBadgeBg, "...", Disabled, TextMain);
            else if (host) SetBadge(cardOppBadge, cardOppBadgeBg, s.RemoteLobbyReady ? T("ready") : T("notReady"), s.RemoteLobbyReady ? Green : Disabled, s.RemoteLobbyReady ? AccentText : TextMain);
            else SetBadge(cardOppBadge, cardOppBadgeBg, T("host"), Accent, AccentText);

            // Device list: built from the workshop's device database; the same on both copies.
            if (Time.unscaledTime >= nextDeviceRefresh)
            {
                nextDeviceRefresh = Time.unscaledTime + 2f;
                var ids = RaceDevices.InGameplay ? RaceDevices.Available().ConvertAll(d => d.ID) : new List<string>();
                if (!SameList(ids, shownDevices)) BuildDeviceGrid(ids);
            }
            foreach (var kv in deviceBg)
            {
                bool chosen = kv.Key == s.LobbyDevice;
                kv.Value.color = chosen ? Accent : CardBg;
                var label = kv.Value.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = chosen ? AccentText : TextMain;
                if (buttons.TryGetValue("device:" + kv.Key, out var b)) b.interactable = host;
            }
            bool hardcore = s.LobbyMode == RaceMode.Hardcore;
            modeNormalBg.color = hardcore ? CardHi : Accent;
            modeNormalLabel.color = hardcore ? TextMain : AccentText;
            modeHardBg.color = hardcore ? Red : CardHi;
            modeHardLabel.color = hardcore ? AccentText : TextMain;
            if (buttons.TryGetValue("mode:normal", out var mn)) mn.interactable = host;
            if (buttons.TryGetValue("mode:hardcore", out var mh)) mh.interactable = host;
            modeHint.text = hardcore
                ? T("modeHardcoreHint").Replace("{s}", plugin.ResetPenaltySeconds.ToString("0.#", CultureInfo.InvariantCulture))
                : T("modeNormalHint");

            lobbyDeviceTitle.text = host ? T("chooseDevice") : (s.LobbyDevice == "" ? T("hostChoosing") : T("hostChose") + DeviceName(s.LobbyDevice));
            lobbyStatus.text = shownDevices.Count == 0 ? T("loadingWorkshop") : "";

            inviteButton.gameObject.SetActive(host && !connected && plugin.Transport != null && plugin.Transport.Route == "Steam");
            inviteButton.interactable = SteamNet.CanInvite;
            goWorkshopButton.gameObject.SetActive(host && menuStage);
            startButton.gameObject.SetActive(host && !menuStage);
            readyButton.gameObject.SetActive(!host);
            if (menuStage)
            {
                if (host)
                {
                    goWorkshopButton.interactable = s.CanGoWorkshop;
                    lobbyHint.text = !connected ? T("waitingJoin") : !s.RemoteLobbyReady ? T("waitReadyMenu") : T("pressChooseDevice");
                }
                else
                {
                    readyButton.interactable = connected;
                    readyLabel.text = s.LocalLobbyReady ? T("readyCancel") : T("readyButton");
                    lobbyHint.text = !connected ? T("connecting") : s.LocalLobbyReady ? T("waitHostChoose") : T("setProfileReady");
                }
                return;
            }
            if (host)
            {
                startButton.interactable = s.CanLaunch && Lobby.CanRace;
                lobbyHint.text = !connected ? T("waitingJoin")
                    : s.LobbyDevice == "" ? T("pickDevice")
                    : !s.RemoteLobbyReady ? T("waitReady")
                    : !Lobby.CanRace ? Why()
                    : T("pressStart");
            }
            else
            {
                readyButton.interactable = connected && s.LobbyDevice != "" && Lobby.CanRace;
                readyLabel.text = s.LocalLobbyReady ? T("readyCancel") : T("readyButton");
                lobbyHint.text = !connected ? T("connecting")
                    : s.LobbyDevice == "" ? "" // the list title already says the host is choosing
                    : !Lobby.CanRace ? Why()
                    : s.LocalLobbyReady ? T("waitStart") : T("pressReady");
            }
            if (Lobby.StatusKey != "") lobbyHint.text = LaunchStatus();
        }

        private void RefreshProfile()
        {
            // Two new avatars per frame: drawing all 16 in one frame was a ~100 ms hitch when the lobby opened.
            if (avatarBg.Count < AvatarArt.Count)
            {
                int added = 0;
                foreach (var id0 in AvatarArt.Ids())
                {
                    if (avatarBg.ContainsKey(id0)) continue;
                    if (added++ >= 2) break;
                    var id = id0;
                    var kv = new KeyValuePair<string, Sprite>(id, AvatarArt.Get(id));
                    var cell = Rect(avatarGrid, "Avatar", CardHi, true);
                    var b = cell.AddComponent<Button>();
                    b.onClick.AddListener(() => plugin.SetAvatar(id));
                    var img = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                    img.transform.SetParent(cell.transform, false);
                    var irt = img.GetComponent<RectTransform>();
                    irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
                    irt.offsetMin = new Vector2(6, 6); irt.offsetMax = new Vector2(-6, -6);
                    var ii = img.GetComponent<Image>();
                    ii.sprite = kv.Value;
                    ii.preserveAspect = true;
                    ii.raycastTarget = false;
                    buttons["avatar:" + id] = b;
                    avatarBg[id] = cell.GetComponent<Image>();
                }
            }
            var mine = plugin.Avatar;
            foreach (var kv in avatarBg) kv.Value.color = kv.Key == mine ? Accent : CardHi;
            if (!lobbyNameInput.isFocused && lobbyNameInput.text != plugin.RacerName) lobbyNameInput.text = plugin.RacerName;
        }

        private Image AvatarImage(Transform parent, float size)
        {
            var frame = Rect(parent, "AvatarFrame", CardHi, true);
            var le = frame.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = size;
            le.preferredHeight = le.minHeight = size;
            var go = new GameObject("Avatar", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(frame.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(4, 4); rt.offsetMax = new Vector2(-4, -4);
            var img = go.GetComponent<Image>();
            img.preserveAspect = true;
            img.raycastTarget = false;
            return img;
        }

        private static void SetAvatar(Image img, string id)
        {
            var sprite = VersusProfile.IconSprite(id);
            img.sprite = sprite;
            img.enabled = sprite != null;
        }

        private void RefreshResult()
        {
            var s = plugin.Session;
            var r = s.Result();
            if (r.Decided)
            {
                resultTitle.text = r.Draw ? T("draw") : r.LocalWins ? T("win") : T("lose");
                resultTitle.color = r.Draw ? TextMain : r.LocalWins ? Green : Red;
            }
            else
            {
                resultTitle.text = s.Connection == ConnectionState.Lost ? T("opponentLeft")
                    : s.Local.Phase == RacePhase.Abandoned ? T("abandoned") : T("waitingOpp");
                resultTitle.color = TextMuted;
            }
            penaltyHintLabel.gameObject.SetActive(s.RaceMode == RaceMode.Hardcore);
            resultLine1.text = ResultLine(plugin.RacerName, s.Local, s.ScoreMs(s.Local));
            resultLine2.text = s.Remote == null ? "" : ResultLine(string.IsNullOrEmpty(s.RemoteName) ? T("opponent") : s.RemoteName, s.Remote, s.ScoreMs(s.Remote));
            resultReason.text = Reason(r);
        }

        private string ResultLine(string who, RacerSnapshot snap, long score)
        {
            string time = snap.Phase == RacePhase.Finished ? Fmt(snap.ElapsedMs) : (snap.Phase == RacePhase.Abandoned ? T("gaveUp") : T("stillGoing"));
            if (plugin.Session.RaceMode != RaceMode.Hardcore) return $"<b>{who}</b>   {time}";
            return $"<b>{who}</b>   {time}   {T("misses")} {snap.Resets}   {T("total")} <b>{(snap.Phase == RacePhase.Finished ? Fmt(score) : "-")}</b>";
        }

        private void RefreshHud()
        {
            var s = plugin.Session;
            bool showResets = s.RaceMode == RaceMode.Hardcore;
            hudYou.Set(plugin.RacerName, s.Local, showResets, plugin.Avatar);
            if (s.Remote != null) hudOpp.Set(string.IsNullOrEmpty(s.RemoteName) ? T("opponent") : s.RemoteName, s.Remote, showResets, s.RemoteAvatar);
            hudOpp.Root.SetActive(s.Remote != null);
            hudStatus.text = Lobby.StatusKey != "" ? LaunchStatus()
                : s.CountdownScheduled ? T("getReady")
                : StartGate.IsHolding ? (s.RemoteReady ? T("bothAtBench") : T("waitBench"))
                : s.Connection == ConnectionState.Lost ? T("lost")
                : "";
            hudStatus.gameObject.SetActive(hudStatus.text != "");
        }

        // Problems the player must know about: an incompatible game build (patches failed), an untested build,
        // an opponent on another game build or another version of the mod.
        private string BannerText()
        {
            if (Plugin.Broken) return T("bannerBroken").Replace("{v}", Application.version);
            var s = plugin.Session;
            if (s != null && s.ForeignModVersion >= 0) return T("bannerModVersion");
            if (s != null && s.BuildMismatch) return T("bannerBuild");
            var c = plugin.Coop.Session;
            if (c != null && c.ForeignModVersion >= 0) return T("bannerModVersion");
            if (c != null && c.BuildMismatch) return T("bannerBuild");
            if (s != null && s.PeerInCoop) return T("peerInCoop");
            if (Application.version != Plugin.TestedGameVersion)
                return "<color=#FFCD40>" + T("bannerUntested").Replace("{v}", Application.version).Replace("{t}", Plugin.TestedGameVersion) + "</color>";
            return "";
        }

        private string SubtitleText()
        {
            var t = plugin.Transport;
            var coop = plugin.Coop.Session;
            if (coop != null)
            {
                var ct = plugin.Coop.Transport;
                string cconn = coop.Connection == ConnectionState.Connected ? T("connected") : coop.Connection == ConnectionState.Lost ? T("lost") : T("waitingShort");
                return (coop.IsHost ? T("host") + " · " + ct.Route : T("guest")) + "  ·  " + cconn;
            }
            if (t == null) return offlinePage == OfflinePage.Choose ? T("subtitleChoose") : offlinePage == OfflinePage.CoopConnect ? T("subtitleCoop") : T("subtitleOffline");
            var s = plugin.Session;
            string conn = s.Connection == ConnectionState.Connected ? T("connected") : s.Connection == ConnectionState.Lost ? T("lost") : T("waitingShort");
            string role = t.IsHost ? T("host") + " · " + t.Route : T("guest");
            string build = s.BuildMismatch ? "   <color=#EC5C5C>" + T("buildDiffers") + "</color>" : "";
            return role + "  ·  " + conn + build;
        }

        private string LaunchStatus() => T(Lobby.StatusKey) + (Lobby.StatusDetail == "" ? "" : ": " + ErrorText(Lobby.StatusDetail));

        // An error code as a sentence the player can act on, in the game's language. Anything unexpected gets a
        // general sentence; the details are in the log.
        private string ErrorText(string code)
        {
            switch (code)
            {
                case "noHost": return T("noHost");
                case "hostLeft": return T("coopHostLeftAfter");
                case "noSave": return T("coopNoSave");
                case "benchBusy": return T("benchBusy");
            }
            return Strings.ContainsKey("err_" + code) ? T("err_" + code) : T("err_generic");
        }

        private string Reason(RaceResult r)
        {
            switch (r.Reason)
            {
                case "both finished": return T("reasonBoth");
                case "opponent abandoned": return T("reasonOppGaveUp");
                case "you abandoned": return T("reasonYouGaveUp");
                default:
                    return r.Reason.StartsWith("different devices") ? T("reasonDevices") : "";
            }
        }

        private string Why()
        {
            if (!RaceDevices.InGameplay) return T("loadingWorkshop");
            if (Lobby.Global != null && !Lobby.Global.IsInGameLoop) return T("closeMenus");
            if (!Lobby.InWorkshop) return T("loadingWorkshop");
            return T("benchBusy");
        }

        private static void SetBadge(TextMeshProUGUI text, Image bg, string label, Color color, Color textColor)
        {
            text.text = label;
            text.color = textColor;
            bg.color = color;
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ----- build -----

        private bool TryBuild()
        {
            // Wait for the main menu or the workshop: before that (logos, loading) the game's own fonts are not
            // loaded yet and the first build picked TMP's default LiberationSans, without Cyrillic.
            var g = Lobby.Global;
            bool gameUiUp = g != null && (g.ActiveState is Restory.Infrastructure.StateMachine.MainMenuState || g.IsInGameLoop);
            if (!gameUiUp && Time.realtimeSinceStartup < 60f) return false;
            lang = GameLanguage();
            font = PickFont(GlyphSample());
            if (font == null) return false;
            Kit.Load();
            // The menus' own font, if it has the glyphs this language needs.
            if (Kit.Font != null && Kit.Font.HasCharacters(GlyphSample(), out uint[] _, true, true)) font = Kit.Font;
            round = MakeRoundedSprite(64, 18);

            root = new GameObject("RestoryVersusUI");
            root.transform.SetParent(transform, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // The main menu's art is on a canvas in a higher sorting layer (the first build showed it through
            // the window there), and the layer beats the order: take the top layer and the top order.
            var layers = SortingLayer.layers;
            if (layers.Length > 0) canvas.sortingLayerID = layers[layers.Length - 1].id;
            canvas.sortingOrder = 32766; // the game's mouse cursor goes to 32767 (RaiseCursor)
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>().enabled = !ignorePointer;

            BuildWindow();
            BuildHud();
            BuildCoopHud();
            built = true;
            Plugin.Log.LogInfo("versus menu built with font '" + font.name + "' (" + lang + ")");
            return true;
        }

        private void BuildWindow()
        {
            // Full-screen dimmer: blocks clicks to the workshop while the window is open.
            window = Rect(root.transform, "Window", Dim, false);
            Stretch(window);

            // The page: the game's notebook (Notebook-Right, 1542x1794 at the game's 4K reference), on the left
            // like Settings and Load game.
            var pageGo = new GameObject("Page", typeof(RectTransform), typeof(Image));
            pageGo.transform.SetParent(window.transform, false);
            var pageImg = pageGo.GetComponent<Image>();
            if (Kit.Page != null) { pageImg.sprite = Kit.Page; pageImg.preserveAspect = true; }
            else { pageImg.sprite = round; pageImg.type = Image.Type.Sliced; pageImg.color = new Color32(242, 237, 225, 255); }
            var prt = pageGo.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0f, 0.5f);
            prt.sizeDelta = new Vector2(900f * 1542f / 1794f, 900f);
            prt.anchoredPosition = new Vector2(-12f, -6f);

            // Doodles on the margins, as on the game's pages.
            Decor(pageGo.transform, Kit.Lines, new Vector2(0.86f, 0.955f), new Vector2(60, 29));
            Decor(pageGo.transform, Kit.Cat, new Vector2(0.04f, 0.555f), new Vector2(91, 43));
            Decor(pageGo.transform, Kit.Star, new Vector2(0.905f, 0.045f), new Vector2(27, 27));

            // The blue back tab at the top-left corner.
            var tab = new GameObject("BackTab", typeof(RectTransform), typeof(Image), typeof(Button));
            tab.transform.SetParent(pageGo.transform, false);
            var trt = tab.GetComponent<RectTransform>();
            trt.anchorMin = trt.anchorMax = new Vector2(0f, 1f);
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(64, 57);
            trt.anchoredPosition = new Vector2(120f, -18f);
            var tabImg = tab.GetComponent<Image>();
            if (Kit.Tab != null) tabImg.sprite = Kit.Tab; else { tabImg.sprite = round; tabImg.type = Image.Type.Sliced; tabImg.color = Accent; }
            var tabButton = tab.GetComponent<Button>();
            tabButton.onClick.AddListener(OnBack);
            buttons["tab"] = tabButton;
            var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            icon.transform.SetParent(tab.transform, false);
            var irt = icon.GetComponent<RectTransform>();
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.sizeDelta = new Vector2(44, 44);
            var iconImg = icon.GetComponent<Image>();
            iconImg.raycastTarget = false;
            if (Kit.BackIcon != null) iconImg.sprite = Kit.BackIcon; else icon.SetActive(false);

            // The writable part of the page, scrolling like the game's own pages.
            var area = new GameObject("Area", typeof(RectTransform), typeof(RectMask2D), typeof(ScrollRect));
            area.transform.SetParent(pageGo.transform, false);
            var art = area.GetComponent<RectTransform>();
            // Right of the notebook's spine (about 22% in) and inside the paper's right edge.
            art.anchorMin = new Vector2(0.245f, 0.07f);
            art.anchorMax = new Vector2(0.905f, 0.91f);
            art.offsetMin = art.offsetMax = Vector2.zero;
            var areaScroll = area.GetComponent<ScrollRect>();
            areaScroll.horizontal = false;
            areaScroll.scrollSensitivity = 30;
            areaScroll.movementType = ScrollRect.MovementType.Clamped;
            var panel = new GameObject("Content", typeof(RectTransform));
            panel.transform.SetParent(area.transform, false);
            var crt = panel.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1);
            crt.anchorMax = new Vector2(1, 1);
            crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = crt.offsetMax = Vector2.zero;
            panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            areaScroll.viewport = art;
            areaScroll.content = crt;
            var v = panel.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(4, 4, 6, 10);
            v.spacing = 12;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            var title = Label(panel.transform, T("gameMenuButton"), 38, TextMain, TextAlignmentOptions.Center, false);
            titleLabel = title;
            subtitle = Label(panel.transform, "", 19, TextMuted, TextAlignmentOptions.Center, false);
            banner = Label(panel.transform, "", 19, Red, TextAlignmentOptions.Left, true);

            viewChoose = Column(panel.transform, "Choose", 16);
            BuildChoose(viewChoose.transform);
            viewConnect = Column(panel.transform, "Connect", 16);
            BuildConnect(viewConnect.transform);
            viewCoopLobby = Column(panel.transform, "CoopLobby", 14);
            BuildCoopLobby(viewCoopLobby.transform);
            viewLobby = Column(panel.transform, "Lobby", 14);
            BuildLobby(viewLobby.transform);
            viewResult = Column(panel.transform, "Result", 18);
            BuildResult(viewResult.transform);
        }

        private static void Decor(Transform page, Sprite sprite, Vector2 anchor, Vector2 size)
        {
            if (sprite == null) return;
            var go = new GameObject("Doodle", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(page, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.raycastTarget = false;
        }

        private void BuildChoose(Transform parent)
        {
            ChoiceCard(parent, "pick:versus", T("versusTitle"), T("versusAbout"), null, () => offlinePage = OfflinePage.Connect);
            ChoiceCard(parent, "pick:coop", T("coopTitle"), T("coopAbout"), T("early"), () => offlinePage = OfflinePage.CoopConnect);
        }

        // A whole card as a button: title, a line about it, and an optional badge on the right.
        private void ChoiceCard(Transform parent, string id, string title, string about, string badge, Action onClick)
        {
            var card = Rect(parent, "Choice", CardBg, true);
            card.AddComponent<LayoutElement>().minHeight = 110;
            var h = card.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(30, 26, 18, 18);
            h.spacing = 12;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            var text = Column(card.transform, "Text", 4);
            Flex(text, 1);
            Label(text.transform, title, 30, TextMain, TextAlignmentOptions.Left, false);
            Label(text.transform, about, 18, TextMuted, TextAlignmentOptions.Left, false);
            if (badge != null)
            {
                var b = Rect(card.transform, "Badge", Disabled, true);
                var ble = b.AddComponent<LayoutElement>();
                ble.preferredWidth = 112; ble.preferredHeight = 32;
                Stretch(Label(b.transform, badge, 16, TextMain, TextAlignmentOptions.Center, false).gameObject);
            }
            var button = card.AddComponent<Button>();
            button.targetGraphic = card.GetComponent<Image>();
            var cb = button.colors;
            cb.highlightedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            cb.selectedColor = Color.white;
            cb.fadeDuration = 0.08f;
            button.colors = cb;
            button.onClick.AddListener(() => onClick());
            buttons[id] = button;
        }

        // Co-op lobby: who is here, what is happening (waiting, sending the save), the host's Start.
        private void BuildCoopLobby(Transform parent)
        {
            var cards = Column(parent, "Players", 8);
            cards.GetComponent<LayoutElement>().flexibleHeight = 0;
            BuildCard(cards.transform, out coopYouName, out coopYouBadge, out coopYouBadgeBg, out coopYouAvatar, T("you"));
            BuildCard(cards.transform, out coopOtherName, out coopOtherBadge, out coopOtherBadgeBg, out coopOtherAvatar, T("friend"));
            var box = Rect(parent, "About", CardBg, true);
            coopRulesBox = box;
            var v = box.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(26, 26, 16, 16);
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandHeight = false;
            Label(box.transform, T("coopRules"), 18, TextMuted, TextAlignmentOptions.Left, false);

            // While playing: the host's rules and everyone's avatar.
            coopSessionBox = Rect(parent, "Session", CardBg, true);
            var sv = coopSessionBox.AddComponent<VerticalLayoutGroup>();
            sv.padding = new RectOffset(22, 22, 14, 14);
            sv.spacing = 10;
            sv.childControlWidth = sv.childControlHeight = true;
            sv.childForceExpandHeight = false;
            var ordersRow = Row(coopSessionBox.transform, 12);
            var ordersLabel = Label(ordersRow.transform, T("coopGuestTakesOrders"), 20, TextMain, TextAlignmentOptions.Left, false);
            Flex(ordersLabel.gameObject, 1);
            coopOrdersToggle = Btn(ordersRow.transform, "coop:orders", T("no"), false, () => CoopSettings.SetGuestTakesOrders(!CoopSettings.GuestTakesOrders));
            coopOrdersToggle.GetComponent<LayoutElement>().preferredWidth = 120;
            coopOrdersToggle.GetComponent<LayoutElement>().preferredHeight = 44;
            Label(coopSessionBox.transform, T("chooseAvatar"), 20, TextMuted, TextAlignmentOptions.Left, false);
            var gridGo = new GameObject("Grid", typeof(RectTransform));
            gridGo.transform.SetParent(coopSessionBox.transform, false);
            coopAvatarGrid = gridGo.GetComponent<RectTransform>();
            var ag = gridGo.AddComponent<GridLayoutGroup>();
            ag.cellSize = new Vector2(48, 48);
            ag.spacing = new Vector2(6, 6);
            ag.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            ag.constraintCount = 8;
            coopSessionBox.SetActive(false);

            coopHint = Label(parent, "", 20, TextMuted, TextAlignmentOptions.Center, false);
            var bottom = Row(parent, 12);
            var leave = Btn(bottom.transform, "coop:leave", T("back"), false, LeaveCoop);
            coopLeaveButton = leave;
            leave.GetComponent<LayoutElement>().preferredWidth = 150;
            coopInviteButton = Btn(bottom.transform, "coop:invite", T("inviteFriend"), false, SteamNet.Invite);
            Flex(coopInviteButton.gameObject, 1);
            coopStartButton = Btn(bottom.transform, "coop:start", T("start"), true, () => plugin.Coop.Start());
            Flex(coopStartButton.gameObject, 1);
            coopCloseButton = Btn(bottom.transform, "coop:close", T("close"), true, () => sessionPageOpen = false);
            Flex(coopCloseButton.gameObject, 1);
            coopCloseButton.gameObject.SetActive(false);
        }

        // Test bot: the co-op page while playing, as the pause menu's sign opens it.
        public void BotSessionPage(bool open) => sessionPageOpen = open;

        // Leaving from inside the game: the way the pause menu's "Main menu" does it.
        private void LeaveCoopFromGame()
        {
            sessionPageOpen = false;
            plugin.StartCoroutine(VersusProfile.ExitWorkshopToMainMenu());
        }

        private void RefreshCoopSession(CoopSession c)
        {
            bool host = c.IsHost;
            bool on = CoopSettings.GuestTakesOrders;
            coopOrdersToggle.interactable = host;
            coopOrdersToggle.GetComponent<Image>().color = on ? Accent : CardHi;
            var label = coopOrdersToggle.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) { label.text = on ? T("yes") : T("no"); label.color = on ? AccentText : TextMain; }
            // Two new avatars per frame, as in the versus lobby.
            if (coopAvatarBg.Count < AvatarArt.Count)
            {
                int added = 0;
                foreach (var id0 in AvatarArt.Ids())
                {
                    if (coopAvatarBg.ContainsKey(id0)) continue;
                    if (added++ >= 2) break;
                    var id = id0;
                    var cell = Rect(coopAvatarGrid, "Avatar", CardHi, true);
                    var b = cell.AddComponent<Button>();
                    b.onClick.AddListener(() => plugin.SetAvatar(id));
                    var img = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                    img.transform.SetParent(cell.transform, false);
                    var irt = img.GetComponent<RectTransform>();
                    irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
                    irt.offsetMin = new Vector2(5, 5); irt.offsetMax = new Vector2(-5, -5);
                    var ii = img.GetComponent<Image>();
                    ii.sprite = AvatarArt.Get(id);
                    ii.preserveAspect = true;
                    ii.raycastTarget = false;
                    buttons["coopavatar:" + id] = b;
                    coopAvatarBg[id] = cell.GetComponent<Image>();
                }
            }
            var mine = plugin.Avatar;
            foreach (var kv in coopAvatarBg) kv.Value.color = kv.Key == mine ? Accent : CardHi;
        }

        private void RefreshCoopLobby()
        {
            var c = plugin.Coop.Session;
            if (c == null) return;
            bool connected = c.Connection == ConnectionState.Connected;
            coopYouName.text = plugin.RacerName;
            SetAvatar(coopYouAvatar, plugin.Avatar);
            SetBadge(coopYouBadge, coopYouBadgeBg, c.IsHost ? T("host") : T("guest"), c.IsHost ? Accent : Disabled, c.IsHost ? AccentText : TextMain);
            coopOtherName.text = connected ? (c.RemoteName == "" ? T("friend") : c.RemoteName) : (c.IsHost ? T("waitingFriend") : T("connecting"));
            SetAvatar(coopOtherAvatar, connected ? c.RemoteAvatar : "");
            if (!connected) SetBadge(coopOtherBadge, coopOtherBadgeBg, "...", Disabled, TextMain);
            else SetBadge(coopOtherBadge, coopOtherBadgeBg, c.IsHost ? T("guest") : T("host"), c.IsHost ? Disabled : Accent, c.IsHost ? TextMain : AccentText);

            // Once started it stays the in-game page, also while a save is being resent (a new day, a rejoin).
            bool playing = c.Launches > 0;
            coopRulesBox.SetActive(!playing);
            coopSessionBox.SetActive(playing);
            coopCloseButton.gameObject.SetActive(playing);
            if (playing != leaveButtonPlaying)
            {
                leaveButtonPlaying = playing;
                coopLeaveButton.GetComponentInChildren<TextMeshProUGUI>().text = playing ? T("coopLeave") : T("back");
                coopLeaveButton.onClick.RemoveAllListeners();
                coopLeaveButton.onClick.AddListener(playing ? (UnityEngine.Events.UnityAction)LeaveCoopFromGame : LeaveCoop);
            }
            if (playing) RefreshCoopSession(c);
            coopStartButton.gameObject.SetActive(c.IsHost && !playing);
            coopInviteButton.gameObject.SetActive(c.IsHost && !connected && !playing && plugin.Coop.Transport != null && plugin.Coop.Transport.Route == "Steam");
            coopInviteButton.interactable = SteamNet.CanInvite;
            coopStartButton.interactable = plugin.Coop.CanStart;
            string kb(int bytes) => (bytes / 1024).ToString(CultureInfo.InvariantCulture);
            string error = c.Error != "" ? c.Error : plugin.Coop.Error;
            coopHint.color = TextMuted;
            if (c.PeerInVersus) { coopHint.text = T("peerInVersus"); coopHint.color = Red; }
            else if (c.Stage == CoopStage.Transfer)
                coopHint.text = (c.IsHost ? T("sendingSave") : T("receivingSave")) + " " + kb(c.TransferDone) + " / " + kb(c.TransferTotal) + " KB";
            else if (error != "") { coopHint.text = ErrorText(error); coopHint.color = Red; }
            else if (playing) coopHint.text = !connected ? T("lost") : c.IsHost ? T("coopSessionHostHint") : T("coopSessionGuestHint");
            else if (c.IsHost) coopHint.text = !connected ? T("waitingFriend") : T("coopPressStart");
            else coopHint.text = !connected ? T("connecting") : T("coopWaitHost");
        }

        private void BuildConnect(Transform parent)
        {
            connectIntro = Label(parent, T("connectIntro"), 24, TextMain, TextAlignmentOptions.Left, false);
            Label(parent, T("yourName"), 21, TextMuted, TextAlignmentOptions.Left, false);
            nameInput = Input(parent, plugin.RacerName, T("yourName"));
            nameInput.onEndEdit.AddListener(v => plugin.SetRacerName(v));
            // Through Steam (invite a friend) or by address: picked here, remembered in the config.
            var routeRow = Row(parent, 10);
            routeSteamButton = Btn(routeRow.transform, "route:steam", T("routeSteam"), false, () => plugin.UseSteam = true);
            Flex(routeSteamButton.gameObject, 1);
            routeIpButton = Btn(routeRow.transform, "route:ip", T("routeIp"), false, () => plugin.UseSteam = false);
            Flex(routeIpButton.gameObject, 1);
            // Said plainly under the choice: the Steam route has not been tried with real Steam accounts.
            Label(parent, T("steamNotTested"), 18, Red, TextAlignmentOptions.Left, false);
            steamHint = Label(parent, "", 20, TextMuted, TextAlignmentOptions.Left, false);
            ipSection = Column(parent, "Address", 8);
            ipSection.GetComponent<LayoutElement>().flexibleHeight = 0;
            Label(ipSection.transform, T("hostAddress"), 21, TextMuted, TextAlignmentOptions.Left, false);
            addressInput = Input(ipSection.transform, plugin.JoinAddress, "192.168.0.10:" + plugin.Port);
            addressInput.onEndEdit.AddListener(v => plugin.JoinAddress = v);

            var row = Row(parent, 14);
            // The same page hosts or joins a race or a co-op game, depending on the card picked before.
            var host = Btn(row.transform, "host", T("hostButton"), true, () =>
            {
                bool coopMode = offlinePage == OfflinePage.CoopConnect;
                if (SteamRoute) { if (coopMode) plugin.StartCoopHostSteam(); else plugin.StartHostSteam(); }
                else if (coopMode) plugin.StartCoopHost(plugin.Port);
                else plugin.StartHost(plugin.Port);
            });
            Flex(host.gameObject, 1);
            joinButton = Btn(row.transform, "join", T("joinButton"), true, () =>
            {
                if (offlinePage == OfflinePage.CoopConnect) plugin.StartCoopJoin(addressInput.text);
                else plugin.StartJoin(addressInput.text);
            });
            Flex(joinButton.gameObject, 1);

            portHintLabel = Label(parent, T("portHint").Replace("{port}", plugin.Port.ToString(CultureInfo.InvariantCulture)), 18, TextMuted, TextAlignmentOptions.Left, false);
            connectError = Label(parent, "", 22, Red, TextAlignmentOptions.Left, false);
        }

        private void BuildLobby(Transform parent)
        {
            var cards = Column(parent, "Players", 8);
            cards.GetComponent<LayoutElement>().flexibleHeight = 0;
            BuildCard(cards.transform, out cardYouName, out cardYouBadge, out cardYouBadgeBg, out cardYouAvatar, T("you"));
            BuildCard(cards.transform, out cardOppName, out cardOppBadge, out cardOppBadgeBg, out cardOppAvatar, T("opponent"));

            // Stage 1 (main menu): name and avatar.
            profileSection = Column(parent, "Profile", 10);
            Label(profileSection.transform, T("yourProfile"), 26, TextMain, TextAlignmentOptions.Left, true);
            var nameRow = Row(profileSection.transform, 12);
            Label(nameRow.transform, T("yourName"), 21, TextMuted, TextAlignmentOptions.Left, false).gameObject.AddComponent<LayoutElement>().preferredWidth = 120;
            lobbyNameInput = Input(nameRow.transform, plugin.RacerName, T("yourName"));
            Flex(lobbyNameInput.gameObject, 1);
            lobbyNameInput.onEndEdit.AddListener(v => plugin.SetRacerName(v));
            Label(profileSection.transform, T("chooseAvatar"), 22, TextMuted, TextAlignmentOptions.Left, false);
            var avatarBox = Rect(profileSection.transform, "Avatars", CardBg, true);
            var abv = avatarBox.AddComponent<VerticalLayoutGroup>();
            abv.padding = new RectOffset(14, 14, 14, 14);
            abv.childControlWidth = abv.childControlHeight = true;
            var gridGo = new GameObject("Grid", typeof(RectTransform));
            gridGo.transform.SetParent(avatarBox.transform, false);
            avatarGrid = gridGo.GetComponent<RectTransform>();
            var ag = gridGo.AddComponent<GridLayoutGroup>();
            ag.cellSize = new Vector2(52, 52);
            ag.spacing = new Vector2(6, 6);
            ag.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            ag.constraintCount = 8;

            // Stage 2 (versus workshop): mode and device.
            workshopSection = Column(parent, "Workshop", 8);
            parent = workshopSection.transform;
            var modeRow = Row(parent, 12);
            Label(modeRow.transform, T("modeTitle"), 21, TextMuted, TextAlignmentOptions.Left, false).gameObject.AddComponent<LayoutElement>().preferredWidth = 90;
            var normal = Btn(modeRow.transform, "mode:normal", T("modeNormal"), false, () => plugin.Session.SelectMode(RaceMode.Normal));
            Flex(normal.gameObject, 1);
            normal.GetComponent<LayoutElement>().preferredHeight = 48;
            var hard = Btn(modeRow.transform, "mode:hardcore", T("modeHardcore"), false, () => plugin.Session.SelectMode(RaceMode.Hardcore));
            Flex(hard.gameObject, 1);
            hard.GetComponent<LayoutElement>().preferredHeight = 48;
            foreach (var b in new[] { normal, hard })
            {
                var cb = b.colors;
                cb.disabledColor = Color.white; // the client sees the host's choice in full color
                b.colors = cb;
                b.GetComponent<ButtonFade>().enabled = false;
            }
            modeNormalBg = normal.GetComponent<Image>();
            modeHardBg = hard.GetComponent<Image>();
            modeNormalLabel = normal.GetComponentInChildren<TextMeshProUGUI>();
            modeHardLabel = hard.GetComponentInChildren<TextMeshProUGUI>();
            modeHint = Label(parent, "", 18, TextMuted, TextAlignmentOptions.Left, false);

            lobbyDeviceTitle = Label(parent, "", 20, TextMain, TextAlignmentOptions.Left, false);
            // Scrollable device grid.
            var scrollGo = Rect(parent, "Devices", CardBg, true);
            var le = scrollGo.AddComponent<LayoutElement>();
            le.preferredHeight = 184; // exactly three rows (3 x 48 + 2 x 8 + padding); the scrollbar shows the rest
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 30;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            // Room on the right for the scrollbar.
            vrt.offsetMin = new Vector2(12, 12); vrt.offsetMax = new Vector2(-30, -12);
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            deviceGrid = content.GetComponent<RectTransform>();
            deviceGrid.anchorMin = new Vector2(0, 1); deviceGrid.anchorMax = new Vector2(1, 1);
            deviceGrid.pivot = new Vector2(0, 1);
            deviceGrid.offsetMin = deviceGrid.offsetMax = Vector2.zero;
            var grid = content.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(200, 48);
            grid.spacing = new Vector2(8, 8);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;
            // Two whole columns whatever the page's width (fixed 224 px cells were wider than the list).
            content.AddComponent<FitGridColumns>();
            var fit = content.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = vrt;
            scroll.content = deviceGrid;

            // A slim scrollbar in the menu's colours, so it is clear the list goes on.
            var bar = Rect(scrollGo.transform, "Scrollbar", new Color32(205, 197, 182, 255), Kit.BarBg == null);
            if (Kit.BarBg != null) { var bi = bar.GetComponent<Image>(); bi.sprite = Kit.BarBg; bi.type = Image.Type.Sliced; }
            var brt = bar.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(1, 0); brt.anchorMax = new Vector2(1, 1);
            brt.pivot = new Vector2(1, 0.5f);
            brt.sizeDelta = new Vector2(10, -24);
            brt.anchoredPosition = new Vector2(-12, 0);
            var area = new GameObject("Sliding Area", typeof(RectTransform));
            area.transform.SetParent(bar.transform, false);
            Stretch(area);
            var handle = Rect(area.transform, "Handle", Accent, Kit.BarFill == null);
            if (Kit.BarFill != null) { var hi = handle.GetComponent<Image>(); hi.sprite = Kit.BarFill; hi.type = Image.Type.Sliced; }
            var hrt = handle.GetComponent<RectTransform>();
            hrt.offsetMin = hrt.offsetMax = Vector2.zero;
            var sb = bar.AddComponent<Scrollbar>();
            sb.handleRect = hrt;
            sb.targetGraphic = handle.GetComponent<Image>();
            sb.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = sb;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            lobbyStatus = Label(scrollGo.transform, "", 26, TextMuted, TextAlignmentOptions.Center, false);
            Stretch(lobbyStatus.gameObject);

            lobbyHint = Label(workshopSection.transform.parent, "", 19, TextMuted, TextAlignmentOptions.Center, false);
            var bottom = Row(workshopSection.transform.parent, 12);
            var leave = Btn(bottom.transform, "leave", T("back"), false, LeaveToConnect);
            leave.GetComponent<LayoutElement>().preferredWidth = 150;
            inviteButton = Btn(bottom.transform, "invite", T("inviteFriend"), false, SteamNet.Invite);
            Flex(inviteButton.gameObject, 1);
            goWorkshopButton = Btn(bottom.transform, "goworkshop", T("chooseDeviceButton"), true, () => plugin.Session.GoWorkshop());
            Flex(goWorkshopButton.gameObject, 1);
            startButton = Btn(bottom.transform, "start", T("start"), true, () => plugin.Session.Launch());
            Flex(startButton.gameObject, 1);
            readyButton = Btn(bottom.transform, "ready", T("readyButton"), true, () => plugin.Session.SetLobbyReady(!plugin.Session.LocalLobbyReady));
            Flex(readyButton.gameObject, 1);
            readyLabel = readyButton.GetComponentInChildren<TextMeshProUGUI>();
        }

        private void BuildCard(Transform parent, out TextMeshProUGUI name, out TextMeshProUGUI badge, out Image badgeBg, out Image avatar, string caption)
        {
            var card = Rect(parent, "Card", CardBg, true);
            Flex(card, 1);
            var le = card.GetComponent<LayoutElement>();
            le.preferredHeight = 76;
            var v = card.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(26, 22, 6, 6);
            v.spacing = 0;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandHeight = false;
            var row = Row(card.transform, 12);
            avatar = AvatarImage(row.transform, 52);
            var who = Column(row.transform, "Who", 0);
            Flex(who, 1);
            Label(who.transform, caption, 15, TextMuted, TextAlignmentOptions.Left, false);
            name = Label(who.transform, "", 22, TextMain, TextAlignmentOptions.Left, false);
            name.textWrappingMode = TextWrappingModes.NoWrap;
            name.overflowMode = TextOverflowModes.Ellipsis;
            var b = Rect(row.transform, "Badge", Disabled, true);
            var ble = b.AddComponent<LayoutElement>();
            ble.preferredWidth = 112; ble.preferredHeight = 32;
            badgeBg = b.GetComponent<Image>();
            badge = Label(b.transform, "", 16, TextMain, TextAlignmentOptions.Center, false);
            Stretch(badge.gameObject);
        }

        private void BuildResult(Transform parent)
        {
            resultTitle = Label(parent, "", 56, Green, TextAlignmentOptions.Center, false);
            resultTitle.enableAutoSizing = true;
            resultTitle.fontSizeMin = 26;
            resultTitle.fontSizeMax = 56;
            resultTitle.textWrappingMode = TextWrappingModes.NoWrap;
            var box = Rect(parent, "Times", CardBg, true);
            var v = box.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(26, 26, 20, 20);
            v.spacing = 12;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandHeight = false;
            resultLine1 = Label(box.transform, "", 23, TextMain, TextAlignmentOptions.Left, false);
            resultLine2 = Label(box.transform, "", 23, TextMain, TextAlignmentOptions.Left, false);
            resultReason = Label(parent, "", 22, TextMuted, TextAlignmentOptions.Center, false);
            penaltyHintLabel = Label(parent, T("penaltyHint").Replace("{s}", plugin.ResetPenaltySeconds.ToString("0.#", CultureInfo.InvariantCulture)), 20, TextMuted, TextAlignmentOptions.Center, false);
            var row = Row(parent, 0);
            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(row.transform, false);
            Flex(spacer, 1);
            var back = Btn(row.transform, "back", T("backToLobby"), true, () => plugin.BackToLobby());
            back.GetComponent<LayoutElement>().preferredWidth = 280;
            var spacer2 = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer2.transform.SetParent(row.transform, false);
            Flex(spacer2, 1);
        }

        private void BuildDeviceGrid(List<string> ids)
        {
            foreach (Transform child in deviceGrid) Destroy(child.gameObject);
            foreach (var id in shownDevices) buttons.Remove("device:" + id);
            deviceBg.Clear();
            shownDevices.Clear();
            shownDevices.AddRange(ids);
            foreach (var id in ids)
            {
                var deviceId = id;
                var b = Btn(deviceGrid, "device:" + id, DeviceName(id), false, () => plugin.Session.SelectDevice(deviceId));
                var label = b.GetComponentInChildren<TextMeshProUGUI>();
                label.fontSize = 22;
                label.enableAutoSizing = true;
                label.fontSizeMin = 14;
                label.fontSizeMax = 22;
                deviceBg[id] = b.GetComponent<Image>();
                // Selection colors are set in RefreshLobby; a non-interactable device button must not grey out.
                var cb = b.colors;
                cb.disabledColor = Color.white;
                b.colors = cb;
                b.GetComponent<ButtonFade>().enabled = false;
            }
        }

        private void BuildHud()
        {
            hud = Rect(root.transform, "Hud", PanelBg, true);
            var rt = hud.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-24, -24);
            rt.sizeDelta = new Vector2(500, 0);
            hud.GetComponent<Image>().raycastTarget = false;
            var v = hud.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(34, 30, 14, 16);
            v.spacing = 10;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandHeight = false;
            var fit = hud.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            hudStatus = Label(hud.transform, "", 20, Accent, TextAlignmentOptions.Left, false);
            hudYou = new HudRow(this, hud.transform, Accent);
            hudOpp = new HudRow(this, hud.transform, OpponentBar);
            foreach (var g in hud.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
        }

        // In the shop during co-op: who you play with, whether the link holds, and short notices.
        private void BuildCoopHud()
        {
            // A small paper note on the shop's top beam: out of the way of the shelves and the bench.
            coopHud = Rect(root.transform, "CoopHud", new Color32(242, 237, 225, 230), true);
            var rt = coopHud.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, -14);
            rt.sizeDelta = new Vector2(340, 0);
            var v = coopHud.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(22, 20, 8, 10);
            v.spacing = 4;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandHeight = false;
            coopHud.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var row = Row(coopHud.transform, 10);
            coopHudAvatar = AvatarImage(row.transform, 34);
            coopHudLine = Label(row.transform, "", 18, TextMain, TextAlignmentOptions.Left, false);
            Flex(coopHudLine.gameObject, 1);
            coopHudNotice = Label(coopHud.transform, "", 17, Accent, TextAlignmentOptions.Left, false);
            foreach (var g in coopHud.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            coopHud.SetActive(false);
        }

        private void RefreshCoopHud()
        {
            var c = plugin.Coop.Session;
            if (c == null) return;
            bool connected = c.Connection == ConnectionState.Connected;
            string who = c.RemoteName == "" ? T("friend") : c.RemoteName;
            coopHudLine.text = T("coopTitle") + " · " + who + (connected ? "" : "  <color=#B5524A>" + T("lost") + "</color>");
            SetAvatar(coopHudAvatar, c.RemoteAvatar);
            string left = plugin.Coop.LeftNotice;
            string notice = left == "hostLeft" ? T("coopHostLeft").Replace("{n}", plugin.Coop.LeaveCountdown.ToString(CultureInfo.InvariantCulture))
                : CoopDayReady.Status(T, who) is var ready && ready != "" ? ready
                : CoopDayReady.BikeStatus(T, who) is var bike && bike != "" ? bike
                : CoopPause.RemotePaused ? T("coopRemotePaused").Replace("{n}", who)
                : left == "guestLeft" ? T("coopGuestLeft")
                : Time.unscaledTime < CoopShop.NoticeUntil ? T(CoopShop.Notice)
                : !c.IsHost && c.Stage == CoopStage.Transfer ? T("receivingSave") + " " + (c.TransferDone / 1024) + " / " + (c.TransferTotal / 1024) + " KB"
                : !c.IsHost && c.HostEndingDay ? T("coopHostDayEnd")
                : !c.IsHost && c.HostStatesReceived == 0 ? T("coopWaitingHostState")
                : "";
            coopHudNotice.text = notice;
            coopHudNotice.gameObject.SetActive(notice != "");
        }

        // Grid cells as wide as the columns allow, recomputed when the list's width changes.
        private sealed class FitGridColumns : MonoBehaviour
        {
            private GridLayoutGroup grid;
            private RectTransform rt;
            private float lastWidth = -1;

            private void Awake()
            {
                grid = GetComponent<GridLayoutGroup>();
                rt = (RectTransform)transform;
            }

            private void LateUpdate()
            {
                float w = rt.rect.width;
                if (Mathf.Approximately(w, lastWidth) || w <= 0) return;
                lastWidth = w;
                int cols = Mathf.Max(1, grid.constraintCount);
                float cell = Mathf.Floor((w - grid.padding.horizontal - grid.spacing.x * (cols - 1)) / cols);
                grid.cellSize = new Vector2(Mathf.Max(60, cell), grid.cellSize.y);
            }
        }

        // An inactive button fades as a whole (plate, face and text), like the game's greyed-out controls.
        private sealed class ButtonFade : MonoBehaviour
        {
            private Button button;
            private CanvasGroup group;

            private void Awake()
            {
                button = GetComponent<Button>();
                group = gameObject.AddComponent<CanvasGroup>();
            }

            private void LateUpdate()
            {
                float a = button != null && !button.interactable ? 0.45f : 1f;
                if (group.alpha != a) group.alpha = a;
            }
        }

        private sealed class HudRow
        {
            public readonly GameObject Root;
            private readonly TextMeshProUGUI label, numbers;
            private readonly RectTransform fill;
            private readonly Image fillImage;
            private readonly Color color;
            private readonly Image avatar;

            // [avatar] [name ......... parts time]
            //          [progress bar..............]
            public HudRow(VersusMenu m, Transform parent, Color color)
            {
                this.color = color;
                Root = m.Row(parent, 12);
                avatar = m.AvatarImage(Root.transform, 56);
                var col = m.Column(Root.transform, "HudCol", 4);
                Flex(col, 1);
                var top = m.Row(col.transform, 8);
                label = m.Label(top.transform, "", 22, TextMain, TextAlignmentOptions.Left, false);
                Flex(label.gameObject, 1);
                numbers = m.Label(top.transform, "", 20, TextMuted, TextAlignmentOptions.Right, false);
                Flex(numbers.gameObject, 1);
                // The settings menu's progress bar (ProgressBarBgl-SettingMenu + a filled ProgressBarFill-SettingMenu).
                var bar = m.Rect(col.transform, "Bar", new Color32(164, 156, 143, 255), Kit.BarBg == null);
                if (Kit.BarBg != null) bar.GetComponent<Image>().sprite = Kit.BarBg;
                bar.AddComponent<LayoutElement>().preferredHeight = 16;
                var f = m.Rect(bar.transform, "Fill", color, Kit.BarFill == null);
                fill = f.GetComponent<RectTransform>();
                fill.anchorMin = Vector2.zero;
                fill.anchorMax = Vector2.one;
                fill.offsetMin = fill.offsetMax = Vector2.zero;
                fillImage = f.GetComponent<Image>();
                fillImage.sprite = Kit.BarFill != null ? Kit.BarFill : m.round;
                fillImage.type = Image.Type.Filled;
                fillImage.fillMethod = Image.FillMethod.Horizontal;
                fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
                fillImage.fillAmount = 0f;
            }

            public void Set(string who, RacerSnapshot s, bool showResets, string avatarId)
            {
                SetAvatar(avatar, avatarId);
                label.text = who;
                numbers.text = showResets
                    ? $"{s.PartsPlaced}/{s.PartsTotal}   x{s.Resets}   {Fmt(s.ElapsedMs)}"
                    : $"{s.PartsPlaced}/{s.PartsTotal}   {Fmt(s.ElapsedMs)}";
                float frac = s.PartsTotal > 0 ? Mathf.Clamp01((float)s.PartsPlaced / s.PartsTotal) : 0f;
                fillImage.fillAmount = frac;
                fillImage.color = s.Phase == RacePhase.Finished ? Green : color;
            }
        }

        // ----- small uGUI helpers -----

        private GameObject Rect(Transform parent, string name, Color color, bool rounded)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            if (rounded)
            {
                // The game's card (SettingsPanel, the save-slot rows), or the mod's own rounded shape.
                img.sprite = Kit.Card != null ? Kit.Card : round;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = Kit.Card != null ? 2f : 1f;
            }
            return go;
        }

        private GameObject Row(Transform parent, float spacing)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            return go;
        }

        private GameObject Column(Transform parent, string name, float spacing)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            Flex(go, 1, vertical: true);
            return go;
        }

        private TextMeshProUGUI Label(Transform parent, string text, float size, Color color, TextAlignmentOptions align, bool bold)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.richText = true;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            return t;
        }

        private Button Btn(Transform parent, string id, string text, bool primary, Action onClick)
        {
            var go = Rect(parent, "Button", primary ? Accent : CardHi, true);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 56;
            le.minWidth = 100;
            Graphic target = go.GetComponent<Image>();
            if (primary && Kit.Button != null)
            {
                // The game's blue control: the dropdown's shadow plate with its blue face on top.
                var plate = go.GetComponent<Image>();
                plate.sprite = Kit.Button;
                plate.type = Image.Type.Sliced;
                plate.pixelsPerUnitMultiplier = 1f;
                plate.color = Color.white;
                if (Kit.ButtonFace != null)
                {
                    var face = new GameObject("Face", typeof(RectTransform), typeof(Image));
                    face.transform.SetParent(go.transform, false);
                    var frt = face.GetComponent<RectTransform>();
                    frt.anchorMin = Vector2.zero;
                    frt.anchorMax = Vector2.one;
                    frt.offsetMin = new Vector2(3, 6);
                    frt.offsetMax = new Vector2(-3, -2);
                    var fimg = face.GetComponent<Image>();
                    fimg.sprite = Kit.ButtonFace;
                    fimg.raycastTarget = false;
                    target = fimg;
                }
            }
            var b = go.AddComponent<Button>();
            b.targetGraphic = target;
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            cb.selectedColor = Color.white;
            cb.disabledColor = Color.white; // inactive buttons fade as a whole (ButtonFade), not tinted darker
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.onClick.AddListener(() => onClick());
            go.AddComponent<ButtonFade>();
            var label = Label(go.transform, text, 22, primary ? AccentText : TextMain, TextAlignmentOptions.Center, false);
            label.enableAutoSizing = true;
            label.fontSizeMin = 14;
            label.fontSizeMax = 22;
            Stretch(label.gameObject, 12);
            buttons[id] = b;
            return b;
        }

        private TMP_InputField Input(Transform parent, string value, string placeholder)
        {
            var go = Rect(parent, "Input", CardBg, true);
            go.AddComponent<LayoutElement>().preferredHeight = 52;
            var area = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            Stretch(area, 26);
            var ph = Label(area.transform, placeholder, 22, TextMuted, TextAlignmentOptions.Left, false);
            ph.fontStyle = FontStyles.Italic;
            ph.textWrappingMode = TextWrappingModes.NoWrap;
            Stretch(ph.gameObject);
            var txt = Label(area.transform, "", 22, TextMain, TextAlignmentOptions.Left, false);
            txt.textWrappingMode = TextWrappingModes.NoWrap;
            Stretch(txt.gameObject);
            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = area.GetComponent<RectTransform>();
            input.textComponent = txt;
            input.placeholder = ph;
            input.fontAsset = font;
            input.pointSize = 22;
            input.caretColor = Accent;
            input.selectionColor = new Color(1f, 0.55f, 0.24f, 0.35f);
            input.characterLimit = 64;
            input.text = value ?? "";
            return input;
        }

        private static void Stretch(GameObject go, float inset = 0)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, 0);
            rt.offsetMax = new Vector2(-inset, 0);
        }

        private static void Flex(GameObject go, float weight, bool vertical = false)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            if (vertical) le.flexibleHeight = weight;
            else le.flexibleWidth = weight;
        }

        // 9-sliced rounded rectangle, anti-aliased, generated once.
        private static Sprite MakeRoundedSprite(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                byte a = (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new UnityEngine.Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        // ----- font and language -----

        // The game's language: its settings (SystemLanguage), else its PlayerPrefs code, else the system's.
        // Test runs: -versus-lang <code> shows the mod in that language without touching the game's settings.
        internal static string ForcedLanguage;

        private static string GameLanguage()
        {
            if (!string.IsNullOrEmpty(ForcedLanguage)) return Known(ForcedLanguage);
            // Cached lookup: a full scene search every 2 s made frame hitches (a versus countdown 211 ms apart).
            var settings = SceneCache.Find<GameSettingsManager>(true);
            if (settings != null) return Code(settings.Localization);
            var pref = PlayerPrefs.GetString("Language", "");
            if (pref.Length >= 2) return Known(pref.Substring(0, 2).ToLowerInvariant());
            return Code(Application.systemLanguage);
        }

        private static string Code(SystemLanguage l)
        {
            switch (l)
            {
                case SystemLanguage.Russian: case SystemLanguage.Ukrainian: case SystemLanguage.Belarusian: return "ru";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Chinese: case SystemLanguage.ChineseSimplified: case SystemLanguage.ChineseTraditional: return "zh";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Portuguese: return "pt";
                default: return "en";
            }
        }

        private static string Known(string code) => code == "en" || code == "ru" || Lang.Table.ContainsKey(code) ? code : "en";

        // Characters the menu needs in this language: the texts themselves.
        private string GlyphSample() => T("gameMenuButton") + T("coopRules") + T("waitingJoin") + T("win") + T("readyButton");

        // A TextMeshPro font the game itself uses that has every character of the sample, so the window matches
        // the game and Cyrillic, kana, hangul and hanzi render. Fonts on texts the game localizes come first.
        private TMP_FontAsset PickFont(string sample)
        {
            var counts = new Dictionary<TMP_FontAsset, int>();
            foreach (var lf in FindObjectsByType<Restory.UserInterface.GUI_LocalisedFont>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var t in lf.GetComponentsInChildren<TMP_Text>(true))
                    if (t.font != null) counts[t.font] = (counts.TryGetValue(t.font, out var c) ? c : 0) + 100;
            foreach (var t in FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.font != null) counts[t.font] = (counts.TryGetValue(t.font, out var c) ? c : 0) + 1;
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && !counts.ContainsKey(f)) counts[f] = 0;
            var ordered = new List<KeyValuePair<TMP_FontAsset, int>>(counts);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in ordered)
                if (kv.Key.HasCharacters(sample, out uint[] _, true, true)) return kv.Key;
            Plugin.Log.LogWarning("no game font has every character for '" + lang + "': falling back to English texts");
            lang = "en";
            if (ordered.Count > 0) return ordered[0].Key;
            return TMP_Settings.defaultFontAsset;
        }

        // The language changed in the game's settings: build the window again in the new one.
        private void Rebuild()
        {
            Plugin.Log.LogInfo("game language changed: rebuilding the menu");
            if (root != null) Destroy(root);
            root = null;
            resultWin = null;
            built = false;
            buttons.Clear();
            deviceBg.Clear();
            avatarBg.Clear();
            shownDevices.Clear();
            view = View.None;
            if (gameMenuButton != null)
            {
                var label = gameMenuButton.GetComponentInChildren<TMP_Text>(true);
                if (label != null) { lang = GameLanguage(); label.text = T("gameMenuButton"); }
            }
        }

        // The game's localized device name ("Atari 2600"), cached; falls back to the database id.
        private string DeviceName(string id)
        {
            if (deviceNames.TryGetValue(id, out var cached)) return cached;
            var info = RaceDevices.Find(id);
            var loc = FindAnyObjectByType<LocalizationSystem>(FindObjectsInactive.Include);
            if (info == null || loc == null) return RaceDevices.DisplayName(id);
            string name = loc.TryGetTranslation(info.NameLocalizationKey, out var t) && !string.IsNullOrEmpty(t) ? t : RaceDevices.DisplayName(id);
            deviceNames[id] = name;
            return name;
        }

        private readonly Dictionary<string, string> deviceNames = new Dictionary<string, string>();
        private float nextDeviceRefresh;

        private static string Fmt(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

        private string T(string key)
        {
            if (!Strings.TryGetValue(key, out var pair)) return key;
            if (lang == "ru") return pair[1];
            if (lang != "en" && Lang.Table.TryGetValue(lang, out var table) && table.TryGetValue(key, out var text)) return text;
            return pair[0];
        }

        private static readonly Dictionary<string, string[]> Strings = new Dictionary<string, string[]>
        {

            { "subtitleOffline", new[] { "Race your friend to assemble the same device", "Кто быстрее соберёт одинаковое устройство" } },
            { "connectIntro", new[] { "Host a game or join a friend's.", "Создай игру или подключись к другу." } },
            { "yourName", new[] { "Your name", "Твоё имя" } },
            { "hostAddress", new[] { "Host address (IP:port)", "Адрес хоста (IP:порт)" } },
            { "hostButton", new[] { "Host a game", "Создать игру" } },
            { "joinButton", new[] { "Join", "Подключиться" } },
            { "portHint", new[] { "The host shares UDP port {port} (LAN, port forwarding or a virtual LAN like ZeroTier).", "Хост открывает UDP-порт {port} (локальная сеть, проброс порта или ZeroTier / Radmin VPN)." } },
            { "netError", new[] { "Network error: ", "Ошибка сети: " } },
            { "you", new[] { "YOU", "ВЫ" } },
            { "opponent", new[] { "Opponent", "Соперник" } },
            { "host", new[] { "Host", "Хост" } },
            { "guest", new[] { "Guest", "Гость" } },
            { "ready", new[] { "Ready", "Готов" } },
            { "notReady", new[] { "Not ready", "Не готов" } },
            { "waitingJoin", new[] { "Waiting for a player to join...", "Ждём, пока подключится игрок..." } },
            { "connecting", new[] { "Connecting to the host...", "Подключаемся к хосту..." } },
            { "connected", new[] { "connected", "подключено" } },
            { "lost", new[] { "connection lost", "связь потеряна" } },
            { "waitingShort", new[] { "waiting", "ожидание" } },
            { "buildDiffers", new[] { "different game version!", "разные версии игры!" } },
            { "chooseDevice", new[] { "Device for the race", "Устройство для гонки" } },
            { "hostChoosing", new[] { "The host is choosing a device...", "Хост выбирает устройство..." } },
            { "hostChose", new[] { "Device: ", "Устройство: " } },
            { "loadingWorkshop", new[] { "Loading the workshop...", "Загружаем мастерскую..." } },
            { "closeMenus", new[] { "Close game menus (pause, PC, dialogue) to race", "Закрой меню игры (пауза, ПК, диалог), чтобы начать" } },
            { "benchBusy", new[] { "Your bench is busy: take your device off the bench", "Верстак занят: убери с него своё устройство" } },
            { "pickDevice", new[] { "Pick a device", "Выбери устройство" } },
            { "waitReady", new[] { "Waiting for the opponent to press Ready", "Ждём, пока соперник нажмёт «Готов»" } },
            { "pressStart", new[] { "Everyone is ready!", "Все готовы!" } },
            { "pressReady", new[] { "Press Ready when you are", "Нажми «Готов», когда будешь готов" } },
            { "waitStart", new[] { "Waiting for the host to start", "Ждём, пока хост начнёт" } },
            { "start", new[] { "START", "НАЧАТЬ" } },
            { "readyButton", new[] { "READY", "ГОТОВ" } },
            { "readyCancel", new[] { "CANCEL", "ОТМЕНИТЬ" } },
            { "back", new[] { "Back", "Назад" } },
            { "subtitleChoose", new[] { "Choose how to play together", "Выбери, как играть вместе" } },
            { "versusTitle", new[] { "Versus", "Версус" } },
            { "versusAbout", new[] { "Race a friend: who assembles the same device faster", "Гонка с другом: кто быстрее соберёт одинаковое устройство" } },
            { "coopTitle", new[] { "Co-op", "Кооператив" } },
            { "coopAbout", new[] { "Run the workshop together", "Вести мастерскую вместе" } },
            { "early", new[] { "Beta", "Бета" } },
            { "coopDayEndHost", new[] { "Only the host ends the day.", "День заканчивает хост." } },
            { "coopOrdersHost", new[] { "Only the host takes orders.", "Заказы берёт хост." } },
            { "coopHostLeft", new[] { "The host left. Back to the main menu in {n} s...", "Хост вышел. Возврат в главное меню через {n} с..." } },
            { "err_portBusy", new[] { "This port is already taken: another copy of the game may have created a game already. Close it or change the port in the mod settings (Network.Port).", "Этот порт уже занят: возможно, игру уже создала другая копия игры. Закрой её или смени порт в настройках мода (Network.Port)." } },
            { "err_badAddress", new[] { "Could not read the address. Type it like this: 192.168.0.10:27515", "Не получилось разобрать адрес. Введи его так: 192.168.0.10:27515" } },
            { "err_hostFailed", new[] { "Could not create the game. Restart the game and try again.", "Не получилось создать игру. Перезапусти игру и попробуй снова." } },
            { "err_joinFailed", new[] { "Could not connect. Check the address and try again.", "Не получилось подключиться. Проверь адрес и попробуй снова." } },
            { "err_modDisabled", new[] { "Online play is off: the mod does not support this version of the game yet. Update the mod.", "Сетевая игра выключена: мод пока не поддерживает эту версию игры. Обнови мод." } },
            { "err_saveFailed", new[] { "Could not save the game for your friend. Try again.", "Не получилось сохранить игру для друга. Попробуй ещё раз." } },
            { "err_menuStuck", new[] { "The game did not go to the main menu. Go there yourself and connect again.", "Игра не перешла в главное меню. Выйди в него сам и подключись снова." } },
            { "err_transferLost", new[] { "The connection dropped while the save was being sent. Press Start again.", "Связь пропала, пока передавался сейв. Нажми «Начать» ещё раз." } },
            { "err_guestRejoined", new[] { "Your friend reconnected while the save was being sent. Press Start again.", "Друг переподключился, пока передавался сейв. Нажми «Начать» ещё раз." } },
            { "err_saveDamaged", new[] { "The save arrived damaged. Press Start again.", "Сейв пришёл повреждённым. Нажми «Начать» ещё раз." } },
            { "err_spawnFailed", new[] { "Could not put the device on the bench. Try again.", "Не получилось выложить устройство на верстак. Попробуй ещё раз." } },
            { "err_raceNotReady", new[] { "The game did not get the race ready in time. Try again.", "Игра не успела подготовить гонку. Попробуй ещё раз." } },
            { "err_generic", new[] { "Something went wrong. The details are in BepInEx\\LogOutput.log in the game folder.", "Что-то пошло не так. Подробности — в файле BepInEx\\LogOutput.log в папке игры." } },
            { "noHost", new[] { "The host does not answer. Check the address and that the host created the game.", "Хост не отвечает. Проверь адрес и что хост создал игру." } },
            { "routeSteam", new[] { "Through Steam", "Через Steam" } },
            { "coopRemotePaused", new[] { "Paused: {n} is in the menu", "Пауза: {n} в меню" } },
            { "coopGuestTakesOrders", new[] { "The guest may take orders", "Гость может брать заказы" } },
            { "yes", new[] { "Yes", "Да" } },
            { "no", new[] { "No", "Нет" } },
            { "close", new[] { "Close", "Закрыть" } },
            { "coopLeave", new[] { "Leave co-op", "Выйти из кооператива" } },
            { "coopSessionHostHint", new[] { "Only you, the host, change the rules. The avatar is everyone's own.", "Правила меняет только хост (ты). Аватарку каждый выбирает себе сам." } },
            { "coopSessionGuestHint", new[] { "The host sets the rules. Your avatar is yours to change.", "Правила задаёт хост. Аватарку можно поменять себе." } },
            { "coopDayReady", new[] { "Ready for the next day: {n}/2", "Готовы к новому дню: {n}/2" } },
            { "coopDayReadyWait", new[] { "waiting for {name} to stamp", "ждём печать от {name}" } },
            { "coopOrderTaken", new[] { "Order taken: the courier brings the device to the shop", "Заказ взят: устройство привезёт курьер" } },
            { "coopOrderAlreadyTaken", new[] { "This order is taken already", "Этот заказ уже взят" } },
            { "coopBikeReady", new[] { "End the day: {n}/2", "Закончить день: {n}/2" } },
            { "coopBikeWait", new[] { "waiting for {name} to click the bicycle", "ждём, когда {name} нажмёт на велосипед" } },
            { "coopBikeYourTurn", new[] { "click the bicycle to end the day", "нажми на велосипед, чтобы закончить день" } },
            { "coopCompetitionNote", new[] { "You cannot race each other in a competition in co-op. For that, play Versus.", "Соревноваться друг с другом в кооперативе нельзя. Для этого есть режим «Версус»." } },
            { "routeIp", new[] { "By IP address", "По IP-адресу" } },
            { "steamHint", new[] { "Create a game and the Steam window opens: pick a friend. Your friend accepts the invite in Steam (or Join Game in the friends list) and lands right in your game.", "Создай игру, откроется окно Steam: выбери друга. Друг принимает приглашение в Steam (или «Присоединиться к игре» в списке друзей) и сразу попадает к тебе." } },
            { "steamNotTested", new[] { "Steam connection: not tested yet with real Steam. If it does not work, use By IP address.", "Подключение через Steam: not tested — ещё не проверено на настоящем Steam. Если не сработает, выбери «По IP-адресу»." } },
            { "steamUnavailable", new[] { "Steam is not running, so only the IP address works right now.", "Steam не запущен, поэтому сейчас работает только подключение по IP." } },
            { "inviteFriend", new[] { "Invite a friend", "Пригласить друга" } },
            { "err_noSteam", new[] { "Steam is not running. Start Steam, or play by IP address.", "Steam не запущен. Запусти Steam или играй по IP-адресу." } },
            { "err_steamLobby", new[] { "Steam could not create the game. Check that you are online in Steam and try again.", "Steam не смог создать игру. Проверь, что ты в сети в Steam, и попробуй ещё раз." } },
            { "err_steamJoin", new[] { "Could not join your friend's game: it may have closed already. Ask for a new invite.", "Не получилось войти в игру друга: возможно, он её уже закрыл. Попроси новое приглашение." } },
            { "err_steamVersion", new[] { "Your friend has a different version of the mod. Install the same version on both PCs.", "У друга другая версия мода. Поставьте одинаковую версию на оба компьютера." } },
            { "err_noHostSteam", new[] { "Your friend's game does not answer through Steam. Ask your friend to invite you again.", "Игра друга не отвечает через Steam. Попроси друга пригласить тебя ещё раз." } },
            { "coopHostLeftAfter", new[] { "The host left the game. Your own save is back.", "Хост вышел из игры. Твой собственный сейв на месте." } },
            { "coopGuestLeft", new[] { "Your friend left. They can join again any time.", "Друг вышел. Он может зайти снова в любой момент." } },
            { "coopHostDayEnd", new[] { "The host ended the day. Waiting for the morning...", "Хост закончил день. Ждём утро..." } },
            { "coopWaitingHostState", new[] { "Waiting for the host's shop...", "Ждём мастерскую хоста..." } },
            { "friend", new[] { "Friend", "Друг" } },
            { "subtitleCoop", new[] { "Your friend comes into your save", "Друг заходит в твой сейв" } },
            { "coopConnectIntro", new[] { "Host with your save, or join a friend's.", "Создай игру со своим сейвом или подключись к другу." } },
            { "coopRules", new[] { "You play in the host's save. Each has their own bench; the room, the parts stock and the money are shared. The host takes orders (and can let the guest take them too) and ends the day.", "Игра идёт в сейве хоста. У каждого свой верстак; комната, склад деталей и деньги общие. Заказы берёт хост (может разрешить и гостю), день заканчивает хост." } },
            { "waitingFriend", new[] { "Waiting for your friend to join...", "Ждём, пока подключится друг..." } },
            { "coopPressStart", new[] { "Your friend is here. Start: they get your save and you both go to the shop.", "Друг подключился. «Начать»: он получит твой сейв, и вы оба окажетесь в мастерской." } },
            { "coopWaitHost", new[] { "Waiting for the host to start", "Ждём, пока хост начнёт" } },
            { "sendingSave", new[] { "Sending your save:", "Передаём твой сейв:" } },
            { "receivingSave", new[] { "Receiving the host's save:", "Получаем сейв хоста:" } },
            { "coopNoSave", new[] { "No save to share: start a game and save first.", "Нечем делиться: сначала начни игру и сохранись." } },
            { "peerInVersus", new[] { "Your friend picked Versus: both need Co-op.", "Друг выбрал «Версус»: нужно, чтобы оба выбрали «Кооператив»." } },
            { "peerInCoop", new[] { "Your friend picked Co-op: both need Versus.", "Друг выбрал «Кооператив»: нужно, чтобы оба выбрали «Версус»." } },
            { "getReady", new[] { "Get ready...", "Приготовься..." } },
            { "bothAtBench", new[] { "Both at the bench", "Оба у верстака" } },
            { "waitBench", new[] { "Waiting for the opponent at the bench...", "Ждём соперника у верстака..." } },
            { "win", new[] { "VICTORY", "ПОБЕДА" } },
            { "lose", new[] { "DEFEAT", "ПОРАЖЕНИЕ" } },
            { "draw", new[] { "DRAW", "НИЧЬЯ" } },
            { "abandoned", new[] { "You left the race", "Ты покинул гонку" } },
            { "waitingOpp", new[] { "Waiting for the opponent to finish...", "Ждём, пока соперник закончит..." } },
            { "gaveUp", new[] { "gave up", "сдался" } },
            { "stillGoing", new[] { "assembling...", "собирает..." } },
            { "misses", new[] { "misses", "промахов" } },
            { "total", new[] { "score", "итог" } },
            { "penaltyHint", new[] { "Score = time + {s} s for each miss (a part dropped outside its socket).", "Итог = время + {s} с за каждый промах (деталь мимо гнезда)." } },
            { "rematch", new[] { "REMATCH", "РЕВАНШ" } },
            { "rematchCancel", new[] { "CANCEL REMATCH", "ОТМЕНИТЬ РЕВАНШ" } },
            { "changeDevice", new[] { "Change device", "Сменить устройство" } },
            { "raceHeading", new[] { "Race {n}", "Гонка {n}" } },
            { "seriesScore", new[] { "Series", "Счёт серии" } },
            { "winnerBadge", new[] { "Winner", "Победитель" } },
            { "timeLabel", new[] { "Time", "Время" } },
            { "missesLabel", new[] { "Misses", "Промахи" } },
            { "totalLabel", new[] { "Score", "Итог" } },
            { "partsLabel", new[] { "Parts", "Детали" } },
            { "penaltyLabel", new[] { "Penalty", "Штраф" } },
            { "secondsShort", new[] { "s", "с" } },
            { "rematchWaiting", new[] { "Waiting for the opponent...", "Ждём соперника..." } },
            { "rematchWanted", new[] { "The opponent wants a rematch!", "Соперник хочет реванш!" } },
            { "rematchGo", new[] { "Rematch! Starting...", "Реванш! Запускаем..." } },
            { "backToLobby", new[] { "Back to lobby", "В лобби" } },
            { "modeTitle", new[] { "Mode", "Режим" } },
            { "gameMenuButton", new[] { "Online play", "Сетевая игра" } },
            { "opponentLeft", new[] { "The opponent left", "Соперник отключился" } },
            { "bannerBroken", new[] { "Versus is off: this game version ({v}) changed what the mod hooks into. Update the mod.", "Версус выключен: версия игры {v} изменила то, к чему цепляется мод. Обнови мод." } },
            { "bannerModVersion", new[] { "The other player runs a different version of the mod: both need the same one.", "У соперника другая версия мода: нужна одинаковая у обоих." } },
            { "bannerBuild", new[] { "Different game versions: update the game on both PCs to race.", "Разные версии игры: обновите игру у обоих, чтобы играть." } },
            { "bannerUntested", new[] { "Game version {v}: the mod was tested on {t}, something may not work.", "Версия игры {v}: мод проверен на {t}, что-то может не работать." } },
            { "yourProfile", new[] { "Your profile", "Твой профиль" } },
            { "chooseAvatar", new[] { "Avatar", "Аватарка" } },
            { "chooseDeviceButton", new[] { "CHOOSE DEVICE", "ВЫБРАТЬ УСТРОЙСТВО" } },
            { "waitReadyMenu", new[] { "Waiting for the opponent to pick a name and press Ready", "Ждём, пока соперник выберет имя и нажмёт «Готов»" } },
            { "pressChooseDevice", new[] { "Everyone is ready: go and choose the device", "Все готовы: переходим к выбору устройства" } },
            { "setProfileReady", new[] { "Pick your name and avatar, then press Ready", "Выбери имя и аватарку и нажми «Готов»" } },
            { "waitHostChoose", new[] { "Waiting for the host to go to the device choice", "Ждём, пока хост перейдёт к выбору устройства" } },
            { "modeNormal", new[] { "Normal", "Обычный" } },
            { "modeHardcore", new[] { "Hardcore", "Хардкор" } },
            { "modeNormalHint", new[] { "A part dropped outside its socket just stays on the table. Fastest wins.", "Деталь мимо гнезда просто остаётся на столе. Побеждает самый быстрый." } },
            { "modeHardcoreHint", new[] { "A part dropped outside its socket takes the whole device apart, and +{s} s.", "Деталь мимо гнезда — устройство разбирается заново, и +{s} с к итогу." } },
            { "startingWorkshop", new[] { "Starting: waiting for the workshop...", "Старт: ждём мастерскую..." } },
            { "startingBench", new[] { "Starting: going to the bench...", "Старт: идём к верстаку..." } },
            { "startFailed", new[] { "Could not start", "Не получилось начать" } },
            { "reasonBoth", new[] { "Both finished", "Оба собрали" } },
            { "reasonOppGaveUp", new[] { "The opponent gave up", "Соперник сдался" } },
            { "reasonYouGaveUp", new[] { "You gave up", "Ты сдался" } },
            { "reasonDevices", new[] { "Different devices: not scored", "Разные устройства: не засчитано" } },
        };
    }
}
