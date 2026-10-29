using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Command line (all optional):
    //   -versus-savedir <path>     test copies only: redirect persistentDataPath (default: the game's own saves;
    //                              versus itself always runs in save profile 9 from the template, see VersusProfile)
    //   -versus-name <name>        racer name, also the default save subfolder
    //   -versus-host [port]        listen for one opponent
    //   -versus-join <host:port>   connect to a host
    //   -versus-bot                test racer (see Bot.cs); -versus-bot-device <id>, -versus-bot-speed <s/part>,
    //                              -versus-bot-quit <s after back in the lobby>
    //   -versus-background         keep running when the window is not focused (two copies on one PC)
    //   -versus-shots              screenshot each menu view into the save folder (visual checks)
    //   -coop-host [port]          co-op: host from the main menu; -coop-join <host:port> joins one
    //   -coop-bot                  co-op test: the host presses Start as soon as it can
    // Right Shift shows/hides the window (VersusMenu). Flow: Host/Join (loads the workshop) -> lobby: host picks the device,
    // opponent presses Ready, host presses Start -> both go to the bench, shared 3-2-1-GO -> race ->
    // result -> Back to lobby (the race device is removed).
    [BepInPlugin(Guid, "ReStory Versus", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "coopanything.restory.versus";
        public const string Version = "0.13.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance { get; private set; }

        // The game build the mod was written and tested against.
        public const string TestedGameVersion = "1.0.033r";

        // Patch classes that did not apply (e.g. a game update renamed a method). Any failure disables versus.
        internal static readonly List<string> PatchFailures = new List<string>();
        internal static bool Broken => PatchFailures.Count > 0;

        private ConfigEntry<int> cfgPort;
        private ConfigEntry<string> cfgRoute;
        private ConfigEntry<string> cfgName;
        private ConfigEntry<float> cfgResetPenalty;
        private ConfigEntry<string> cfgAvatar;
        private ConfigEntry<bool> cfgContestShadows;
        private ConfigEntry<bool> cfgCoopBarNoticesOnly;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private NetTransport transport;
        private RaceSession session;
        private string racerName;
        private string gameBuild;
        private string statusPath;
        private string dataDir; // status file and screenshots
        private float nextStatusWrite;
        private float nextPartsPoll;
        private string netError = "";
        private Bot bot;
        private bool returning;
        private VersusMenu menu;
        private CoopGame coop;
        private bool coopBot;
        private float nextCoopBotStart;
        private bool coopBotOpened;
        private bool coopShot;
        private bool coopBotCarried;
        private float nextLeaveFlagCheck;

        private System.Collections.IEnumerator CoopShot(string path)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }
        private bool coopBotDayEnd = System.Linq.Enumerable.Contains(Environment.GetCommandLineArgs(), "-coop-bot-dayend");
        private float coopBotShopSince = -1;
        private int coopBotStep;
        private float nextRoomWrite;

        private void Awake()
        {
            Log = Logger;
            TestPrivacy.Apply();
            Instance = this;
            var harmony = new Harmony(Guid);

            cfgName = Config.Bind("Player", "Name", "Player", "Name shown to the opponent.");
            cfgPort = Config.Bind("Network", "Port", 27515, "UDP port the host listens on.");
            // The default is the tested route; Steam has not been tried with real Steam accounts yet.
            cfgRoute = Config.Bind("Network", "Route", "ip", "How the menu connects players: ip (the host's IP) or steam (invite a Steam friend; not tested with real Steam yet).");
            SteamNet.ReadLaunchArgs();
            // Tests pick the route: -versus-route ip|steam (test bots press the menu's own buttons).
            // Two copies side by side on one screen (the owner's Test.bat): -versus-window 640x360 keeps this copy in a
            // window of that size. The game applies its own display settings at start (GameSettingsManager), so the
            // launch options alone gave full screen; the game's settings themselves are left as they are.
            var windowArg = Arg("-versus-window");
            if (windowArg != null)
            {
                var wh = windowArg.ToLowerInvariant().Split('x');
                if (wh.Length == 2 && int.TryParse(wh[0], out var ww) && int.TryParse(wh[1], out var wht) && ww >= 320 && wht >= 180) { forcedWidth = ww; forcedHeight = wht; }
            }
            var routeArg = Arg("-versus-route");
            if (routeArg == "ip" || routeArg == "steam") cfgRoute.Value = routeArg;
            cfgAvatar = Config.Bind("Player", "Avatar", "", "Avatar: id of one of the game's profile icons.");
            cfgContestShadows = Config.Bind("Visual", "ContestShadows", false, "Keep the contest's neon shadows in versus races (the contest rug is kept either way).");
            cfgCoopBarNoticesOnly = Config.Bind("Visual", "CoopBarNoticesOnly", false, "Show the co-op bar at the top only when it has a notice (waiting, pause, a lost connection, ...).");
            cfgResetPenalty = Config.Bind("Race", "ResetPenaltySeconds", 5f, "Seconds added to the final time for each device reset (a dropped part that missed its socket).");

            racerName = Arg("-versus-name") ?? cfgName.Value;
            VersusMenu.ForcedLanguage = Arg("-versus-lang");
            VersusProfile.Init(Config);
            CoopGame.Init(Config);
            CoopSettings.Init(Config);
            coop = new CoopGame(this);

            // Players race on their own saves (the workshop needs a career that reached it). Test copies pass
            // -versus-savedir to keep the owner's saves untouched. Until 0.3.0 the redirect was always on, so a
            // friend's copy started from an empty folder: no Continue, no workshop, no lobby.
            var saveDir = Arg("-versus-savedir");
            if (saveDir != null)
            {
                // First, before any game service can touch the disk.
                SaveIsolation.Apply(harmony, saveDir, Log);
                dataDir = SaveIsolation.RedirectedPath;
            }
            else
            {
                Log.LogInfo("saves: the game's own save folder (versus uses profile " + VersusProfile.Slot + ")");
                dataDir = Paths.BepInExRootPath;
            }
            statusPath = Path.Combine(dataDir, "versus_status.json");

            // One patch class at a time: after a game update a renamed method fails its own patch only, and the
            // menu says so instead of the whole mod dying at load.
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e)
                {
                    PatchFailures.Add(type.Name);
                    Log.LogError("patch " + type.FullName + " failed: " + e.GetBaseException().Message);
                }
            }
            foreach (var m in harmony.GetPatchedMethods())
                Log.LogInfo("patched " + m.DeclaringType?.FullName + "." + m.Name);
            if (PatchFailures.Count > 0) Log.LogError("versus disabled: " + PatchFailures.Count + " patches failed (" + string.Join(", ", PatchFailures.ToArray()) + ")");
            if (Application.version != TestedGameVersion) Log.LogWarning("game version " + Application.version + " differs from the tested " + TestedGameVersion);

            gameBuild = Application.version + "/" + Application.buildGUID;
            Log.LogInfo($"ReStory Versus {Version} on Unity {Application.unityVersion}, game {gameBuild}, racer '{racerName}'");

            ContestHooks.Prepared = (id, placed, total) => session?.ContestPrepared(id, placed, total);
            ContestHooks.Started = () => session?.ContestStarted(Now);
            ContestHooks.Stopped = () => session?.ContestStopped();
            ContestHooks.DeviceReset = () => session?.DeviceReset();
            ContestHooks.Finished = secs => session?.ContestFinished(Now, secs);

            ContestHooks.KeepContestShadows = () => cfgContestShadows.Value || ContestHooks.Mode == null
                || !RaceDevices.IsRaceDevice(ContestHooks.Mode.CurrentDeviceInCompetition);
            ContestHooks.AllowReset = () => session == null || session.RaceMode == RaceMode.Hardcore
                || ContestHooks.Mode == null || !RaceDevices.IsRaceDevice(ContestHooks.Mode.CurrentDeviceInCompetition);
            StartGate.ShouldHold = () => session != null && transport != null && session.Connection == ConnectionState.Connected;
            StartGate.Held = id => session?.SetLocalReady(true, id);
            StartGate.Cancelled = held =>
            {
                session?.SetLocalReady(false, "");
                // Out of the bench within moments of sitting down, before the countdown: the game dropped us (seen
                // once in a test run, cause unknown), so sit back down. Twice at most per race, so a player who
                // really wants to leave can.
                if (held < 3f && transport != null && benchRetries < 2) StartCoroutine(SitBackAtBench());
            };

            if (Arg("-versus-background", allowFlagOnly: true) != null)
            {
                // Two copies on one PC by hand: keep the unfocused one running instead of freezing.
                Bot.KeepRunningInBackground = true;
                Application.runInBackground = true;
                Log.LogInfo("running in background (-versus-background)");
            }

            var hostArg = Arg("-versus-host", allowFlagOnly: true);
            var joinArg = Arg("-versus-join");
            if (hostArg != null) StartHost(int.TryParse(hostArg, out var p) ? p : cfgPort.Value);
            else if (joinArg != null) StartJoin(joinArg);
            else StartOffline();
            var coopHostArg = Arg("-coop-host", allowFlagOnly: true);
            var coopJoinArg = Arg("-coop-join");
            if (coopHostArg != null) StartCoopHost(int.TryParse(coopHostArg, out var cp) ? cp : cfgPort.Value);
            else if (coopJoinArg != null) StartCoopJoin(coopJoinArg);
            coopBot = Arg("-coop-bot", allowFlagOnly: true) != null;
            // Test: host from inside a save (-coop-bot-ingame <port>); a guest that retries a join (-coop-join-retry).
            var ingamePort = Arg("-coop-bot-ingame");
            if (ingamePort != null) StartCoroutine(CoopBotHostFromGame(int.TryParse(ingamePort, out var ip) ? ip : cfgPort.Value));
            joinRetry = Arg("-coop-join-retry");
            if (coopBot) Application.runInBackground = true;

            if (Arg("-versus-diag", allowFlagOnly: true) != null) StartCoroutine(Diagnostics.Run());
            if (Arg("-versus-diag-dayend", allowFlagOnly: true) != null) StartCoroutine(Diagnostics.DayEnd());

            var uiRoot = new GameObject("RestoryVersusMenu");
            DontDestroyOnLoad(uiRoot);
            menu = uiRoot.AddComponent<VersusMenu>();
            menu.Init(this, Arg("-versus-shots", allowFlagOnly: true) != null ? dataDir : null, Arg("-versus-bot", allowFlagOnly: true) != null);

            if (Arg("-versus-bot", allowFlagOnly: true) != null)
            {
                // Test runs must not send crash reports to the game's developers.
                UnityEngine.CrashReportHandler.CrashReportHandler.enableCaptureExceptions = false;
                float speed = float.TryParse(Arg("-versus-bot-speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) ? sp : 0.4f;
                int quit = int.TryParse(Arg("-versus-bot-quit"), out var q) ? q : 0;
                bot = new Bot(this, () => session, () => menu, Arg("-versus-bot-device"), speed, quit, Arg("-versus-bot-modes") ?? "normal,hardcore");
                bot.Start();
                Log.LogInfo($"bot enabled: {speed}s per part");
            }
        }

        private long Now => clock.ElapsedMilliseconds;

        internal void StartOffline()
        {
            StopNet();
            session = NewSession(null);
            // Leaving versus: out of the versus workshop; the main menu then restores the player's profile.
            if (VersusProfile.Active && Lobby.Global != null && Lobby.Global.IsInGameLoop)
                StartCoroutine(VersusProfile.ExitWorkshopToMainMenu());
        }

        internal void StartHost(int port)
        {
            if (Broken) { netError = "modDisabled"; return; }
            coop?.Stop();
            StopNet();
            try
            {
                transport = NetTransport.Host(port);
                session = NewSession(transport);
                Log.LogInfo("hosting on UDP " + transport.LocalPort);
                netError = "";
            }
            catch (Exception e) { Log.LogError("host failed: " + e); StartOffline(); netError = NetErrors.Classify(e, true); }
        }

        internal void StartJoin(string address)
        {
            if (Broken) { netError = "modDisabled"; return; }
            coop?.Stop();
            StopNet();
            try
            {
                var parts = address.Split(':');
                int port = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : cfgPort.Value;
                transport = NetTransport.Connect(parts[0], port);
                session = NewSession(transport);
                Log.LogInfo("joining " + parts[0] + ":" + port);
                netError = "";
            }
            catch (Exception e) { Log.LogError("join failed: " + e); StartOffline(); netError = NetErrors.Classify(e, false); }
        }

        // Through Steam: a friends-only lobby, the invite window opens once it exists.
        internal void StartHostSteam()
        {
            if (Broken) { netError = "modDisabled"; return; }
            if (!SteamNet.Available) { netError = "noSteam"; return; }
            coop?.Stop();
            StopNet();
            try
            {
                var link = new SteamLink(0);
                transport = NetTransport.Over(link, true, null);
                session = NewSession(transport);
                SteamNet.CreateLobby(link, "versus", code => { StartOffline(); netError = code; });
                Log.LogInfo("hosting through Steam");
                netError = "";
            }
            catch (Exception e) { Log.LogError("steam host failed: " + e); StartOffline(); netError = "steamLobby"; }
        }

        internal void StartCoopHostSteam()
        {
            if (Broken) { netError = "modDisabled"; return; }
            StopNet();
            session = NewSession(null);
            coop.HostSteam(racerName, gameBuild, Avatar);
        }

        // An invite was accepted (in the game, or Steam started the game with it): enter the lobby, then join its
        // owner in the lobby's mode.
        private void JoinSteamInvite(ulong lobby)
        {
            if (Broken) { netError = "modDisabled"; return; }
            Log.LogInfo("steam: joining lobby " + lobby);
            menu?.OpenNetworkPage(null);
            SteamNet.JoinLobby(lobby, (owner, mode) =>
            {
                bool coopMode = mode == "coop";
                menu?.OpenNetworkPage(coopMode);
                if (coopMode)
                {
                    StopNet();
                    session = NewSession(null);
                    coop.JoinSteam(owner, racerName, gameBuild, Avatar);
                    return;
                }
                coop?.Stop();
                StopNet();
                var link = new SteamLink(owner);
                transport = NetTransport.Over(link, false, link.HostPeer);
                session = NewSession(transport);
                netError = "";
            }, code => { netError = code; menu?.OpenNetworkPage(false); });
        }

        internal bool UseSteam
        {
            get => cfgRoute.Value != "ip";
            set => cfgRoute.Value = value ? "steam" : "ip";
        }

        // Co-op uses its own connection; versus goes offline meanwhile (one port, one game at a time).
        internal void StartCoopHost(int port)
        {
            if (Broken) { netError = "modDisabled"; return; }
            StopNet();
            session = NewSession(null);
            coop.Host(port, racerName, gameBuild, Avatar);
        }

        internal void StartCoopJoin(string address)
        {
            if (Broken) { netError = "modDisabled"; return; }
            StopNet();
            session = NewSession(null);
            coop.Join(address, cfgPort.Value, racerName, gameBuild, Avatar);
        }

        private RaceSession NewSession(ITransport t)
        {
            var s = new RaceSession(t, racerName, gameBuild, (long)(cfgResetPenalty.Value * 1000));
            s.OnLog += line => Log.LogInfo("[race] " + line);
            s.SetLocalProfile(racerName, Avatar);
            s.OnCountdown += () => StartGate.Release();
            s.OnLaunch += id => { benchRetries = 0; StartCoroutine(Lobby.Launch(id)); };
            return s;
        }

        private void StopNet()
        {
            if (session != null) { session.Leave(); session.Tick(Now); }
            transport?.Dispose();
            transport = null;
        }

        internal void BackToLobby()
        {
            if (returning) return;
            returning = true;
            StartCoroutine(Lobby.ReturnToLobby(() =>
            {
                session.ResetForLobby();
                returning = false;
            }));
        }

        private void Update()
        {
            Perf.Frame();
            Perf.Begin();
            try { UpdatePlugin(); }
            finally { Perf.End(); }
        }

        private string joinRetry;
        private bool retryKeepsAwake;
        private float nextJoinRetry;

        // The player's way into co-op from a save: load it, Esc, "Online play", Co-op, Host a game; Start when the
        // friend is in. Screenshots of each step for the visual check.
        private System.Collections.IEnumerator CoopBotHostFromGame(int port)
        {
            while (!Lobby.InMainMenu(out _)) yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(1f);
            Log.LogInfo("co-op bot (in game): loading the host's own save");
            Lobby.ContinueNow();
            while (!CoopGame.InShopFor(5f)) yield return new WaitForSecondsRealtime(0.5f);
            var pause = UnityEngine.Object.FindAnyObjectByType<Restory.UI.Presenters.PauseMenu.GUI_PauseMenu>(FindObjectsInactive.Include);
            if (pause == null) { Log.LogError("co-op bot (in game): no pause menu"); yield break; }
            pause.Show();
            yield return new WaitForSecondsRealtime(1.5f);
            yield return CoopShot(Path.Combine(dataDir, "ingame_pause.png"));
            Log.LogInfo("co-op bot (in game): pause menu sign present " + menu.PauseMenuButtonPresent + ", clicked " + menu.ClickPauseMenuButton());
            yield return new WaitForSecondsRealtime(1.2f);
            yield return CoopShot(Path.Combine(dataDir, "ingame_page.png"));
            menu.Click("pick:coop");
            yield return new WaitForSecondsRealtime(0.5f);
            StartCoopHost(port);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return CoopShot(Path.Combine(dataDir, "ingame_lobby.png"));
            float until = Time.realtimeSinceStartup + 240f;
            while (!coop.CanStart && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
            Log.LogInfo("co-op bot (in game): the friend is in, pressing Start");
            coop.Start();
            while (coop.Session != null && !coop.Session.Playing && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(1f);
            pause.Hide();
            Log.LogInfo("co-op bot (in game): started, back to the game");
        }

        private int benchRetries;

        private System.Collections.IEnumerator SitBackAtBench()
        {
            benchRetries++;
            yield return new WaitForSecondsRealtime(0.8f);
            var mode = ContestHooks.Mode;
            if (StartGate.IsHolding || ContestHooks.Racing || mode == null || !mode.HasDeviceInCompetition || !RaceDevices.BenchHasRaceDevice) yield break;
            Log.LogInfo("back at the bench after an unexpected exit (" + benchRetries + ")");
            mode.CurrentDeviceInCompetition.Activate();
        }

        private bool backgroundForNet;
        private string lastSeries = "";
        private float joinStartedAt = -1;

        private int forcedWidth, forcedHeight;
        private float nextWindowCheck;

        private void KeepForcedWindow()
        {
            if (forcedWidth <= 0 || Time.unscaledTime < nextWindowCheck) return;
            nextWindowCheck = Time.unscaledTime + 1f;
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != forcedWidth || Screen.height != forcedHeight)
                Screen.SetResolution(forcedWidth, forcedHeight, FullScreenMode.Windowed);
        }

        private void UpdatePlugin()
        {
            KeepForcedWindow();
            if (session == null) return;
            SteamNet.Tick();
            // An accepted invite waits until the game is somewhere it can leave from (main menu, or in a save).
            if (SteamNet.PendingInvite != 0 && SteamNet.Available && (Lobby.InMainMenu(out _) || CoopGame.InShopFor(2f)) && !VersusProfile.Busy)
            {
                var lobby = SteamNet.PendingInvite;
                SteamNet.PendingInvite = 0;
                JoinSteamInvite(lobby);
            }
            // Networked: keep running when the window is not focused (two copies on one PC, alt-tab), or the other
            // side sees the connection drop. The game would turn it off again after its fades and saves.
            bool net = transport != null || coop.Session != null;
            if (net && !Bot.KeepRunningInBackground) { Bot.KeepRunningInBackground = true; backgroundForNet = true; }
            if (net && !Application.runInBackground) Application.runInBackground = true;
            if (!net && backgroundForNet) { Bot.KeepRunningInBackground = false; backgroundForNet = false; }
            // Joining a host that does not answer: give up after a while and say so.
            if (transport != null && !transport.IsHost && session.Connection == ConnectionState.Waiting)
            {
                if (joinStartedAt < 0) joinStartedAt = Time.unscaledTime;
                else if (Time.unscaledTime - joinStartedAt > JoinTimeout(transport))
                {
                    Log.LogWarning("join: no answer from the host in " + JoinTimeout(transport) + " s");
                    string why = transport.Route == "Steam" ? "noHostSteam" : "noHost";
                    StartOffline();
                    netError = why;
                }
            }
            else joinStartedAt = -1;
            // Connected: both players go to the versus workshop (from the main menu, or via the main menu when
            // they are in their career workshop). Offline in the main menu: back to the player's own profile.
            // Only once the host pressed "Choose device" (workshop stage): until then the lobby is in the menu.
            if (transport != null && session.Stage == LobbyStage.Workshop)
            {
                Lobby.TryContinueFromMainMenu();
                if (Lobby.InGame && !VersusProfile.Active && VersusProfile.HasTemplate && !VersusProfile.Busy)
                    StartCoroutine(VersusProfile.ExitWorkshopToMainMenu());
                VersusProfile.PrepareWorkshop();
            }
            else if (Lobby.InMainMenu(out _))
            {
                VersusProfile.RestoreInMainMenu();
                // A guest keeps slot 8 while connected (it holds the host's save); otherwise its own slot is back.
                if (coop.Session == null) CoopGame.RestoreInMainMenu();
            }
            coop.Tick(Now);
            // Test guest: between join attempts there is no network session, so an unfocused window would sleep and
            // never retry; the bot keeps it awake only until it is connected, then the network rule alone holds.
            if (joinRetry != null)
            {
                if (coop.Session == null) { Bot.KeepRunningInBackground = true; Application.runInBackground = true; retryKeepsAwake = true; }
                else if (retryKeepsAwake && coop.Session.Connection == ConnectionState.Connected)
                {
                    retryKeepsAwake = false;
                    Bot.KeepRunningInBackground = false;
                    backgroundForNet = false;
                }
            }
            // Test guest: after "the host does not answer", try the address given for the retry.
            if (joinRetry != null && coop.Session == null && coop.Error == "noHost" && Time.unscaledTime >= nextJoinRetry)
            {
                nextJoinRetry = Time.unscaledTime + 10f;
                Log.LogInfo("co-op bot: no host at the first address, joining " + joinRetry);
                StartCoopJoin(joinRetry);
            }
            if (coopBot && coop.Session != null && coop.Session.IsHost && coop.CanStart && Time.unscaledTime >= nextCoopBotStart)
            {
                nextCoopBotStart = Time.unscaledTime + 5f;
                Log.LogInfo("co-op bot: pressing Start");
                coop.Start();
            }
            // The test host opens the shop a few seconds in, so the clock runs and the guest has something to follow.
            if (coopBot && coop.Session != null && coop.Session.IsHost && coop.LocalWorldCount > 0 && !coopBotOpened)
            {
                coopBotOpened = true;
                Log.LogInfo("co-op bot: opening the shutters: " + CoopShop.OpenShuttersForTest());
            }
            if (coopBot) CoopBotRoom();
            if (coopBot) CoopBotNewDay();
            if (Time.unscaledTime >= nextPartsPoll)
            {
                nextPartsPoll = Time.unscaledTime + 0.1f;
                if (ContestHooks.TryCountParts(out var placed, out var total)) session.PartsChanged(placed, total);
                session.InGameSecondsChanged(ContestHooks.InGameSeconds);
            }
            ContestHooks.ShowRaceTimeOnMat(session);
            // In the workshop stage the client's Ready only stands while it can actually race. (In the menu stage
            // there is no workshop yet: applying it there withdrew the menu Ready the frame it was given.)
            if (!session.IsHost && session.Stage == LobbyStage.Workshop && session.LocalLobbyReady && !Lobby.CanRace) session.SetLobbyReady(false);
            var series = session.SeriesWins + ":" + session.SeriesLosses + ":" + session.SeriesDraws;
            if (series != lastSeries) { if (series != "0:0:0") Log.LogInfo("versus series: " + series); lastSeries = series; }
            // Rematch: both voted on the result screen -> both back to the lobby, the client ready, the host starts
            // the same device and mode again (the votes clear with the launch).
            if (transport != null && session.RematchAgreed)
            {
                bool over = session.Local.Phase == RacePhase.Finished || session.Local.Phase == RacePhase.Abandoned;
                if (over && !returning) { Log.LogInfo("rematch agreed: back to the lobby"); BackToLobby(); }
                else if (InLobby && Lobby.CanRace && session.Stage == LobbyStage.Workshop)
                {
                    if (!session.IsHost && !session.LocalLobbyReady && session.LobbyDevice != "") session.SetLobbyReady(true);
                    if (session.IsHost && session.CanLaunch) { Log.LogInfo("rematch: starting " + session.LobbyDevice + " (" + session.LobbyMode + ") again"); session.Launch(); }
                }
            }
            session.Tick(Now);
            if (Time.unscaledTime >= nextStatusWrite)
            {
                nextStatusWrite = Time.unscaledTime + 0.5f;
                WriteStatus();
            }
        }

        // Test bots in the shop: each side moves its own object, then both move the same one at once; the room as
        // each copy sees it goes to coop_room.txt every 2 s for the test to compare.
        private void CoopBotRoom()
        {
            var s = coop.Session;
            if (s == null || coop.LocalWorldCount <= 0 || s.RemoteWorldCount <= 0) return;
            if (coopBotShopSince < 0) coopBotShopSince = Time.unscaledTime;
            float t = Time.unscaledTime - coopBotShopSince;
            if (coopBotStep == 0 && t > 8f)
            {
                coopBotStep = 1;
                Log.LogInfo("co-op bot: move " + CoopRoom.BotMove(s.IsHost ? "Clock" : "Radio", 0));
            }
            else if (coopBotStep == 1 && t > 14f)
            {
                coopBotStep = 2;
                Log.LogInfo("co-op bot: contested move " + CoopRoom.BotMove("Lamp", s.IsHost ? 1 : 2));
            }
            // The host carries the clock around for 2 s: the guest sees it in the host's hand.
            if (!coopBotCarried && s.IsHost && t > 16f)
            {
                coopBotCarried = true;
                StartCoroutine(CoopHeld.BotCarry("Clock", line => Log.LogInfo("co-op bot: " + line)));
            }
            // Devices: the host brings a box in, the guest moves it to another shelf, the host takes it to its bench
            // (gone from the guest's room) and back to a shelf (back on the guest's, as it is now).
            else if (coopBotStep == 2 && t > 20f)
            {
                coopBotStep = 3;
                if (s.IsHost) Log.LogInfo("co-op bot: device spawned " + CoopDevices.BotSpawn());
            }
            else if (coopBotStep == 3 && t > 30f)
            {
                coopBotStep = 4;
                var d = CoopDevices.BotDevice();
                if (!s.IsHost && d != null) Log.LogInfo("co-op bot: cleaned " + CoopTextures.BotDirty(d));
                if (!s.IsHost) Log.LogInfo("co-op bot: device move " + (d == null ? "no device" : CoopRoom.BotMove(CoopDevices.Top(d).GetComponent<Restory.Gameplay.InteractiveObjects.InteractiveObject>(), 3)));
            }
            else if (coopBotStep == 4 && t > 40f)
            {
                coopBotStep = 5;
                var d = CoopDevices.BotDevice();
                if (s.IsHost) Log.LogInfo("co-op bot: device to bench " + CoopDevices.BotToBench());
            }
            else if (coopBotStep == 5 && t > 50f)
            {
                coopBotStep = 6;
                if (s.IsHost)
                {
                    var devices = SceneCache.Find<Restory.Gameplay.Devices.DeviceService>(true);
                    var grabbed = devices != null && devices.PlacedDeviceContainer != null ? devices.GrabPlacedDeviceContainer() : null;
                    Log.LogInfo("co-op bot: device back to a shelf " + (grabbed == null ? "(nothing on the bench)" : CoopRoom.BotMove(grabbed, 4)));
                }
            }
            else if (coopBotStep == 6 && t > 60f)
            {
                coopBotStep = 7;
                if (!s.IsHost) Log.LogInfo("co-op bot: device shipped " + CoopDevices.BotShip());
            }
            // A new day without waiting for one: the host ends the day the way the bicycle does (the game's own
            // end-of-day path), comes back next morning and resends; the guest reloads.
            // The shared stock and buying: the host gets a part, the guest takes one, the guest buys.
            else if (coopBotStep == 7 && t > 63f)
            {
                coopBotStep = 8;
                if (s.IsHost) Log.LogInfo("co-op bot: part added " + CoopStock.BotAddPart());
                if (s.IsHost) { CoopSettings.SetGuestTakesOrders(true); Log.LogInfo("co-op bot: the guest may take orders"); }
            }
            else if (coopBotStep == 8 && t > 66f)
            {
                coopBotStep = 9;
                if (!s.IsHost) Log.LogInfo("co-op bot: part taken " + CoopStock.BotTakePart());
            }
            else if (coopBotStep == 9 && t > 68f)
            {
                coopBotStep = 10;
                if (!s.IsHost) Log.LogInfo("co-op bot: bought " + CoopStock.BotBuy());
                if (!s.IsHost) Log.LogInfo("co-op bot: e-mail order " + CoopEmail.BotTakeOrder());
            }
            // The courier: the host gets a box of parts at the delivery spot, the guest sees it, takes the parts out
            // into the shared stock and carries the box to a shelf (a room object for both from then on).
            else if (coopBotStep == 10 && t > 71f)
            {
                coopBotStep = 11;
                if (s.IsHost) Log.LogInfo("co-op bot: courier came " + CoopDelivery.BotDeliver());
                if (s.IsHost) Log.LogInfo("co-op bot: a customer left " + CoopDevices.BotCustomerDevice());
            }
            else if (coopBotStep == 11 && t > 76f)
            {
                coopBotStep = 12;
                if (!s.IsHost) Log.LogInfo("co-op bot: courier box " + CoopDelivery.BotTakeParts());
                if (!s.IsHost) Log.LogInfo("co-op bot: bench without a drop " + CoopDevices.BotBenchWithoutDrop());
                if (s.IsHost) Log.LogInfo("co-op bot: a customer paid " + CoopCash.BotCustomerPays());
                if (s.IsHost) Log.LogInfo("co-op bot: to-do " + CoopToDo.BotAddOne());
            }
            else if (coopBotStep == 12 && t > 80f)
            {
                coopBotStep = 13;
                var box = CoopDelivery.BotBox();
                if (!s.IsHost) Log.LogInfo("co-op bot: courier box to a shelf " + (box == null ? "(no box)" : CoopRoom.BotMove(box, 5)));
                // Things the game makes on the host with no drop to report: only the room list brings them over.
                if (s.IsHost) Log.LogInfo("co-op bot: silently made " + CoopCensus.BotSilentSpawn());
                if (s.IsHost) Log.LogInfo("co-op bot: customers' box " + CoopServiceBoxes.BotFillCustomersBox());
            }
            // Furniture: the host puts another piece in a slot, then the guest paints the walls.
            else if (coopBotStep == 13 && t > 83f)
            {
                coopBotStep = 14;
                if (s.IsHost) Log.LogInfo("co-op bot: furniture " + CoopFurniture.BotChange(true));
                if (s.IsHost) Log.LogInfo("co-op bot: silently removed " + CoopCensus.BotSilentRemove());
                if (!s.IsHost) Log.LogInfo("co-op bot: cash " + CoopCash.BotDeposit());
                if (!s.IsHost) Log.LogInfo("co-op bot: to-do " + CoopToDo.BotCompleteOne());
            }
            else if (coopBotStep == 14 && t > 87f)
            {
                coopBotStep = 15;
                if (!s.IsHost) Log.LogInfo("co-op bot: furniture " + CoopFurniture.BotChange(false));
            }
            // One pause for both: the host opens its pause menu, the guest must be paused too, then both resume.
            else if (coopBotStep == 15 && t > 90f)
            {
                coopBotStep = 16;
                if (s.IsHost) { CoopPause.BotPaused = true; Log.LogInfo("co-op bot: pause menu opened"); }
                if (s.IsHost) StartCoroutine(SessionPageShot());
                if (!s.IsHost) StartCoroutine(BotCoopBar());
            }
            else if (coopBotStep == 16 && t > 93f)
            {
                coopBotStep = 17;
                if (s.IsHost) { CoopPause.BotPaused = false; Log.LogInfo("co-op bot: pause menu closed"); }
            }
            // The day ends when both clicked the bicycle: the host first (nothing happens yet), then the guest.
            else if (coopBotStep == 17 && t > 96f && coopBotDayEnd)
            {
                coopBotStep = 18;
                if (s.IsHost) Log.LogInfo("co-op bot: bicycle " + CoopDayReady.BotClickBicycle());
            }
            else if (coopBotStep == 18 && t > 101f && coopBotDayEnd)
            {
                coopBotStep = 19;
                if (!s.IsHost) Log.LogInfo("co-op bot: bicycle " + CoopDayReady.BotClickBicycle());
            }
            // One look at the shop with the co-op panel, for the visual check.
            if (!coopShot && t > 6f)
            {
                coopShot = true;
                StartCoroutine(CoopShot(Path.Combine(dataDir, "coop_shop.png")));
            }
            // Test harness: a flag file asks this copy to leave for the main menu the way the pause menu does.
            if (Time.unscaledTime >= nextLeaveFlagCheck)
            {
                nextLeaveFlagCheck = Time.unscaledTime + 1f;
                var flag = Path.Combine(dataDir, "coop_leave.flag");
                if (File.Exists(flag))
                {
                    File.Delete(flag);
                    Log.LogInfo("co-op bot: leaving for the main menu");
                    StartCoroutine(VersusProfile.ExitWorkshopToMainMenu());
                }
            }
            if (Time.unscaledTime >= nextRoomWrite)
            {
                nextRoomWrite = Time.unscaledTime + 2f;
                try { CoopRoom.WriteRoom(Path.Combine(dataDir, "coop_room.txt")); }
                catch (Exception e) { Log.LogWarning("co-op bot: room file: " + e.Message); }
            }
        }

        // Test bots: on the end-of-day screen, press "New day" a few seconds after it shows (what the player's
        // click on the stamp ends in).
        private float dayWindowSince = -1;
        private UnityEngine.Object dayWindowPressed;

        private System.Collections.IEnumerator SessionPageShot()
        {
            yield return new WaitForSecondsRealtime(0.8f);
            menu?.BotSessionPage(true);
            yield return new WaitForSecondsRealtime(1.2f);
            yield return CoopShot(Path.Combine(dataDir, "coop_session.png"));
            menu?.BotSessionPage(false);
        }

        // Test bot (guest): "top bar only with notices" on; the bar must show while the host is paused (a notice)
        // and hide once the pause is over; the bar names the other player with its role.
        private System.Collections.IEnumerator BotCoopBar()
        {
            bool before = CoopBarNoticesOnly;
            CoopBarNoticesOnly = true;
            yield return new WaitForSecondsRealtime(1.5f);
            string paused = menu.CoopBarShown + " notice=" + Json(menu.CoopBarNotice);
            string line = menu.CoopBarLine;
            yield return new WaitForSecondsRealtime(4.5f);
            string quiet = menu.CoopBarShown + " notice=" + Json(menu.CoopBarNotice);
            CoopBarNoticesOnly = before;
            yield return new WaitForSecondsRealtime(0.5f);
            Log.LogInfo("co-op bot: bar notices-only paused shown=" + paused + " quiet shown=" + quiet
                + " off shown=" + menu.CoopBarShown + " line=" + Json(line) + " role=" + Json(menu.RoleOf(true)));
        }

        private void CoopBotNewDay()
        {
            var w = SceneCache.Find<Restory.UI.Presenters.DayEndWindow.GUI_DayEndWindow>(false);
            if (w == null || !w.isActiveAndEnabled) { dayWindowSince = -1; return; }
            if (ReferenceEquals(w, dayWindowPressed)) return;
            if (dayWindowSince < 0) dayWindowSince = Time.unscaledTime;
            if (Time.unscaledTime - dayWindowSince < 4f) return;
            dayWindowPressed = w;
            Log.LogInfo("co-op bot: pressing New day");
            AccessTools.Method(typeof(Restory.UI.Presenters.DayEndWindow.GUI_DayEndWindow), "ResolveStartNextDayActionPerformed")?.Invoke(w, null);
        }

        private void OnApplicationQuit()
        {
            coop?.Stop();
            StopNet();
            WriteStatus();
        }

        // Machine-readable state for test bots and the launcher.
        private void WriteStatus()
        {
            try
            {
                var r = session.Result();
                var sb = new StringBuilder();
                sb.Append("{\"plugin\":\"").Append(Version).Append('"');
                sb.Append(",\"name\":").Append(Json(racerName));
                sb.Append(",\"saveDir\":").Append(Json(dataDir));
                sb.Append(",\"attached\":").Append(ContestHooks.Attached ? "true" : "false");
                sb.Append(",\"connection\":\"").Append(session.Connection).Append('"');
                sb.Append(",\"inWorkshop\":").Append(Lobby.InWorkshop ? "true" : "false");
                sb.Append(",\"profile\":").Append(VersusProfile.CurrentProfile);
                sb.Append(",\"stage\":\"").Append(session.Stage).Append('"');
                sb.Append(",\"malformed\":").Append(session.MalformedMessages);
                sb.Append(",\"matTimer\":").Append(Json(ContestHooks.MatText));
                sb.Append(",\"patchFailures\":").Append(PatchFailures.Count);
                sb.Append(",\"perfModMs\":").Append(Perf.LastModMsAvg.ToString("0.000", CultureInfo.InvariantCulture));
                sb.Append(",\"perfFrameMs\":").Append(Perf.LastFrameMsAvg.ToString("0.0", CultureInfo.InvariantCulture));
                sb.Append(",\"localAvatar\":").Append(Json(session.LocalAvatar));
                sb.Append(",\"remoteAvatar\":").Append(Json(session.RemoteAvatar));
                sb.Append(",\"remoteName\":").Append(Json(session.RemoteName));
                sb.Append(",\"toolsOwned\":").Append(VersusProfile.ToolsGranted);
                sb.Append(",\"benchEmpty\":").Append(RaceDevices.BenchEmpty ? "true" : "false");
                sb.Append(",\"lobbyDevice\":").Append(Json(session.LobbyDevice));
                sb.Append(",\"canLaunch\":").Append(session.CanLaunch ? "true" : "false");
                sb.Append(",\"launch\":").Append(Json(Lobby.Status));
                sb.Append(",\"localReady\":").Append(session.LocalReady ? "true" : "false");
                sb.Append(",\"remoteReady\":").Append(session.RemoteReady ? "true" : "false");
                sb.Append(",\"holding\":").Append(StartGate.IsHolding ? "true" : "false");
                sb.Append(",\"countdownWallMs\":").Append(session.CountdownWallMs);
                sb.Append(",\"rttMs\":").Append(transport != null ? transport.RttMs : 0);
                sb.Append(",\"bot\":").Append(bot == null ? "null" : Json(bot.Stage));
                sb.Append(",\"botBackInLobby\":").Append(bot != null && bot.BackInLobby ? "true" : "false");
                sb.Append(",\"botLeft\":").Append(bot != null && bot.Left ? "true" : "false");
                sb.Append(",\"botRaceProfile\":").Append(bot != null ? bot.ProfileDuringRace : 0);
                sb.Append(",\"botRaces\":").Append(bot != null ? bot.RacesJson() : "[]");
                sb.Append(",\"raceMode\":\"").Append(session.RaceMode).Append('"');
                sb.Append(",\"series\":[").Append(session.SeriesWins).Append(',').Append(session.SeriesLosses).Append(',').Append(session.SeriesDraws).Append(']');
                sb.Append(",\"local\":").Append(SnapJson(session.Local));
                sb.Append(",\"remote\":").Append(session.Remote == null ? "null" : SnapJson(session.Remote));
                sb.Append(",\"result\":{\"decided\":").Append(r.Decided ? "true" : "false")
                  .Append(",\"localWins\":").Append(r.LocalWins ? "true" : "false")
                  .Append(",\"draw\":").Append(r.Draw ? "true" : "false")
                  .Append(",\"reason\":").Append(Json(r.Reason)).Append('}');
                var cs = coop.Session;
                sb.Append(",\"coop\":{\"active\":").Append(cs != null ? "true" : "false");
                if (cs != null)
                {
                    sb.Append(",\"host\":").Append(cs.IsHost ? "true" : "false")
                      .Append(",\"connection\":\"").Append(cs.Connection).Append('"')
                      .Append(",\"stage\":\"").Append(cs.Stage).Append('"')
                      .Append(",\"remoteName\":").Append(Json(cs.RemoteName))
                      .Append(",\"transfer\":[").Append(cs.TransferDone).Append(',').Append(cs.TransferTotal).Append(']')
                      .Append(",\"saveFile\":").Append(Json(cs.SaveFileName))
                      .Append(",\"localWorld\":[").Append(coop.LocalWorldCount).Append(',').Append(Json(coop.LocalWorldHash)).Append(']')
                      .Append(",\"remoteWorld\":[").Append(cs.RemoteWorldCount).Append(',').Append(Json(cs.RemoteWorldHash)).Append(']')
                      .Append(",\"error\":").Append(Json(cs.Error != "" ? cs.Error : coop.Error));
                }
                sb.Append(",\"guestSlot\":").Append(CoopGame.GuestSlotActive ? "true" : "false");
                var cal = SceneCache.Find<Restory.Gameplay.TimeSystems.GameCalendar>(false);
                var shut = SceneCache.Find<Restory.Gameplay.Equipment.WindowShuttersStoreInteractiveItem>(false);
                var npcs = SceneCache.Find<Restory.Gameplay.NPCs.NpcServiceMain>(false);
                sb.Append(",\"clock\":").Append(cal != null ? Json(cal.CurrentDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) : "null")
                  .Append(",\"clockTicks\":").Append(cal != null ? cal.CurrentDateTime.Ticks : -1)
                  .Append(",\"clockRunning\":").Append(CoopShop.ClockRunning ? "true" : "false")
                  .Append(",\"shuttersOpen\":").Append(shut != null && shut.IsWindowOpen ? "true" : "false")
                  .Append(",\"visit\":").Append(npcs != null ? Json(npcs.CurrentVisitState.ToString()) : "null")
                  .Append(",\"guestPlaying\":").Append(CoopShop.GuestPlaying ? "true" : "false")
                  .Append(",\"guestSkipped\":").Append(CoopShop.Skipped)
                  .Append(",\"hostStates\":").Append(cs != null ? cs.HostStatesReceived : 0)
                  .Append(",\"room\":{\"reported\":").Append(CoopRoom.Reported).Append(",\"applied\":").Append(CoopRoom.Applied)
                  .Append(",\"missing\":").Append(CoopRoom.Missing).Append(",\"pending\":").Append(cs != null ? cs.PendingMoves : 0)
                  .Append(",\"botStep\":").Append(coopBotStep).Append('}')
                  .Append(",\"devices\":{\"count\":").Append(System.Linq.Enumerable.Count(CoopDevices.All, x => x != null)).Append(",\"reported\":").Append(CoopDevices.Reported)
                  .Append(",\"applied\":").Append(CoopDevices.Applied).Append(",\"hiddenApplied\":").Append(CoopDevices.HiddenApplied)
                  .Append(",\"failed\":").Append(CoopDevices.Failed).Append(",\"remoteHidden\":").Append(CoopDevices.RemoteHiddenCount).Append(",\"counterAnnounced\":").Append(CoopDevices.ShipmentsAnnounced).Append('}')
                  .Append(",\"launches\":").Append(cs != null ? cs.Launches : 0)
                  .Append(",\"daySaves\":").Append(coop.DaySaves).Append(",\"guestDaySwitches\":").Append(coop.GuestDaySwitches)
                  .Append(",\"hostEndingDay\":").Append(cs != null && cs.HostEndingDay ? "true" : "false")
                  .Append(",\"day\":").Append(cal != null ? cal.CurrentDayNumber : -1)
                  .Append(",\"stock\":{\"total\":").Append(CoopStock.StockTotal).Append(",\"hash\":").Append(Json(CoopStock.StockHash))
                  .Append(",\"changesSent\":").Append(CoopStock.Stock?.ChangesSent ?? 0).Append(",\"changesApplied\":").Append(CoopStock.Stock?.ChangesApplied ?? 0)
                  .Append(",\"itemsApplied\":").Append(CoopStock.ItemsApplied).Append(",\"removeMisses\":").Append(CoopStock.RemoveMisses)
                  .Append(",\"buysForwarded\":").Append(CoopStock.PurchasesForwarded).Append(",\"buysApplied\":").Append(CoopStock.PurchasesApplied)
                  .Append(",\"deliveryWaiting\":").Append(SceneCache.Find<Restory.Gameplay.Delivery.DeliveryService>(true) is var dsv && dsv != null && dsv.IsThereAnythingToDeliver ? "true" : "false").Append('}')
                  .Append(",\"held\":{\"posesSent\":").Append(CoopHeld.PosesSent).Append(",\"posesFollowed\":").Append(CoopHeld.PosesFollowed)
                  .Append(",\"locksSeen\":").Append(CoopHeld.LocksSeen).Append(",\"releases\":").Append(CoopHeld.Releases).Append(",\"locked\":").Append(CoopHeld.Locked).Append('}')
                  .Append(",\"delivery\":{\"statesSent\":").Append(CoopDelivery.StatesSent).Append(",\"statesApplied\":").Append(CoopDelivery.StatesApplied)
                  .Append(",\"takesSent\":").Append(CoopDelivery.TakesSent).Append(",\"takesApplied\":").Append(CoopDelivery.TakesApplied)
                  .Append(",\"hash\":").Append(Json(coopBot ? CoopDelivery.Hash : "")).Append(",\"created\":").Append(CoopRoom.Created).Append('}')
                  .Append(",\"census\":{\"sent\":").Append(CoopCensus.Sent).Append(",\"received\":").Append(CoopCensus.Received)
                  .Append(",\"asked\":").Append(CoopCensus.Asked).Append(",\"answered\":").Append(CoopCensus.Answered)
                  .Append(",\"hidden\":").Append(CoopCensus.Hidden).Append(",\"removed\":").Append(CoopCensus.Removed).Append('}')
                  .Append(",\"boxes\":{\"sent\":").Append(CoopServiceBoxes.StatesSent).Append(",\"applied\":").Append(CoopServiceBoxes.StatesApplied)
                  .Append(",\"hashes\":").Append(Json(coopBot ? CoopServiceBoxes.Hashes : "")).Append('}')
                  .Append(",\"dayReady\":{\"waits\":").Append(CoopDayReady.Waits).Append(",\"both\":").Append(CoopDayReady.BothReady).Append('}')
                  .Append(",\"email\":{\"sent\":").Append(CoopEmail.StatesSent).Append(",\"applied\":").Append(CoopEmail.StatesApplied)
                  .Append(",\"asked\":").Append(CoopEmail.OrdersAsked).Append(",\"takenForGuest\":").Append(CoopEmail.OrdersTakenForGuest).Append('}')
                  .Append(",\"cash\":{\"forwarded\":").Append(CoopCash.DepositsForwarded).Append(",\"made\":").Append(CoopCash.DepositsMade).Append('}')
                  .Append(",\"pause\":{\"byOther\":").Append(CoopPause.TimesPausedByOther).Append(",\"resumedByOther\":").Append(CoopPause.TimesResumedByOther).Append('}')
                  .Append(",\"net\":{\"sent\":").Append(coop.Transport != null ? coop.Transport.BytesSent : 0).Append(",\"received\":").Append(coop.Transport != null ? coop.Transport.BytesReceived : 0)
                  .Append(",\"packets\":").Append(coop.Transport != null ? coop.Transport.PacketsSent : 0).Append(",\"resent\":").Append(coop.Transport != null ? coop.Transport.PacketsResent : 0).Append('}')
                  .Append(",\"furniture\":{\"sent\":").Append(CoopFurniture.Sent).Append(",\"applied\":").Append(CoopFurniture.Applied)
                  .Append(",\"hash\":").Append(Json(coopBot ? CoopFurniture.Hash : "")).Append('}')
                  .Append(",\"tex\":{\"sent\":").Append(CoopTextures.Sent).Append(",\"received\":").Append(CoopTextures.Received)
                  .Append(",\"sentBytes\":").Append(CoopTextures.SentBytes).Append(",\"bot\":").Append(Json(CoopTextures.BotTexture)).Append(",\"guestRange\":").Append(Json(CoopTextures.GuestRange())).Append('}')
                  .Append(",\"rejoins\":").Append(coop.Rejoins).Append(",\"guestExits\":").Append(coop.GuestExits)
                  .Append(",\"money\":").Append(SceneCache.Find<Restory.Gameplay.Inventory.Wallet>(false) is var wl && wl != null ? wl.MoneyAvailable : -1).Append('}');
                sb.Append(",\"timeMs\":").Append(Now).Append('}');
                var tmp = statusPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(statusPath)) File.Delete(statusPath);
                File.Move(tmp, statusPath);
            }
            catch (Exception e) { Log.LogWarning("status write failed: " + e.Message); }
        }

        private static string SnapJson(RacerSnapshot s) =>
            "{\"phase\":\"" + s.Phase + "\",\"race\":" + s.RaceNo + ",\"device\":" + Json(s.DeviceId) + ",\"placed\":" + s.PartsPlaced + ",\"total\":" + s.PartsTotal
            + ",\"resets\":" + s.Resets + ",\"elapsedMs\":" + s.ElapsedMs + ",\"inGameSeconds\":" + s.InGameSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "}";

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        // ----- what the menu (VersusMenu) reads and calls -----

        internal RaceSession Session => session;
        internal NetTransport Transport => transport;
        internal string RacerName => racerName;
        internal string NetError => netError;
        internal int Port => cfgPort.Value;
        internal float ResetPenaltySeconds => cfgResetPenalty.Value;
        internal string JoinAddress { get; set; } = "127.0.0.1";
        internal VersusMenu Menu => menu;
        internal bool CoopBarNoticesOnly { get => cfgCoopBarNoticesOnly.Value; set => cfgCoopBarNoticesOnly.Value = value; }
        internal const float JoinTimeoutSeconds = 8f;
        // Steam's relays take a few seconds to find a route the first time.
        internal static float JoinTimeout(ITransport t) => t != null && t.Route == "Steam" ? 30f : JoinTimeoutSeconds;
        internal CoopGame Coop => coop;
        internal long NowMs => Now;
        internal string DataDir => dataDir;

        // In the lobby: connected (or hosting) and no race running, launching or wrapping up.
        internal bool InLobby => transport != null && session.Local.Phase == RacePhase.Idle && !Lobby.Launching && !StartGate.IsHolding && !returning;

        internal string Avatar
        {
            get
            {
                // Default: the first of the game's profile icons.
                if (string.IsNullOrEmpty(cfgAvatar.Value))
                {
                    var icons = VersusProfile.Icons();
                    if (icons.Count > 0) cfgAvatar.Value = icons[0].Key;
                }
                return cfgAvatar.Value;
            }
        }

        internal void SetRacerName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == racerName) return;
            racerName = name;
            cfgName.Value = name;
            session?.SetLocalProfile(racerName, Avatar);
            coop?.Session?.SetLocalProfile(racerName, Avatar);
        }

        internal void SetAvatar(string id)
        {
            if (string.IsNullOrEmpty(id) || id == cfgAvatar.Value) return;
            cfgAvatar.Value = id;
            session?.SetLocalProfile(racerName, id);
            coop?.Session?.SetLocalProfile(racerName, id);
        }

        private static string Fmt(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

        // ----- command line -----

        private static string Arg(string name, bool allowFlagOnly = false)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) return args[i + 1];
                return allowFlagOnly ? "" : null;
            }
            return null;
        }

        private static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrEmpty(s) ? "player" : s;
        }
    }
}
