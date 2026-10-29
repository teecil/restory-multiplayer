using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.SaveLoad.Services;
using Restory.Gameplay.TimeSystems;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Co-op step 1, game side (see local/coop-design.md):
    //   host:  main menu, guest connected, Start -> the save "Continue" would load (last used slot, highest
    //          iteration) goes to the guest as it is on disk -> guest confirms -> host presses Continue
    //   guest: gets the save, writes it as slot 8 (a slot the game's menus never show) -> selects 8 -> Continue
    // The guest's own saves are never touched: while slot 8 is selected every save goes to slot 8, and the
    // main menu without a co-op game gives the guest their own slot back (also after a crash).
    // In the shop both report the ids of the objects they loaded, so a test can tell the two shops match.
    internal sealed class CoopGame
    {
        public const int Slot = 8;
        private static ConfigEntry<int> returnSlot;

        private readonly Plugin plugin;
        private NetTransport transport;
        private CoopSession session;
        private bool launching;
        private float worldReportAt = -1;
        private string lastIdsFile;

        public CoopSession Session => session;
        public NetTransport Transport => transport;
        public string Error { get; private set; } = "";
        public int LocalWorldCount { get; private set; } = -1;
        public string LocalWorldHash { get; private set; } = "";
        internal static InteractiveObjectRegistry Registry => RegistryCapture.Instance;
        private static float shopSince = -1;

        // In the shop's game loop for at least this long (the save has finished loading).
        public static bool InShopFor(float seconds)
        {
            bool now = Lobby.Global != null && Lobby.Global.IsInGameLoop && Registry != null;
            if (!now) { shopSince = -1; return false; }
            if (shopSince < 0) shopSince = Time.unscaledTime;
            return Time.unscaledTime - shopSince >= seconds;
        }
        public static bool GuestSlotActive => VersusProfile.Profiles != null && SaveScheme.Selected(VersusProfile.Profiles) == Slot;

        public CoopGame(Plugin plugin)
        {
            this.plugin = plugin;
        }

        public static void Init(ConfigFile config)
        {
            returnSlot = config.Bind("Internal", "CoopReturnSlot", -1, "Save slot to go back to after a co-op game as a guest (set by the mod).");
        }

        // ----- connection -----

        public void Host(int port, string name, string build, string avatar)
        {
            Stop();
            try
            {
                transport = NetTransport.Host(port);
                Begin(name, build, avatar);
                Plugin.Log.LogInfo("co-op: hosting on UDP " + transport.LocalPort);
            }
            catch (Exception e) { Plugin.Log.LogError("co-op: host failed: " + e); Fail(NetErrors.Classify(e, true)); }
        }

        public void Join(string address, int defaultPort, string name, string build, string avatar)
        {
            Stop();
            try
            {
                var parts = (address ?? "").Trim().Split(':');
                int port = parts.Length > 1 ? int.Parse(parts[1]) : defaultPort;
                transport = NetTransport.Connect(parts[0], port);
                Begin(name, build, avatar);
                Plugin.Log.LogInfo("co-op: joining " + parts[0] + ":" + port);
            }
            catch (Exception e) { Plugin.Log.LogError("co-op: join failed: " + e); Fail(NetErrors.Classify(e, false)); }
        }

        public void HostSteam(string name, string build, string avatar)
        {
            Stop();
            if (!SteamNet.Available) { Fail("noSteam"); return; }
            try
            {
                var link = new SteamLink(0);
                transport = NetTransport.Over(link, true, null);
                Begin(name, build, avatar);
                SteamNet.CreateLobby(link, "coop", Fail);
                Plugin.Log.LogInfo("co-op: hosting through Steam");
            }
            catch (Exception e) { Plugin.Log.LogError("co-op: steam host failed: " + e); Fail("steamLobby"); }
        }

        public void JoinSteam(ulong host, string name, string build, string avatar)
        {
            Stop();
            try
            {
                var link = new SteamLink(host);
                transport = NetTransport.Over(link, false, link.HostPeer);
                Begin(name, build, avatar);
                Plugin.Log.LogInfo("co-op: joining " + host + " through Steam");
            }
            catch (Exception e) { Plugin.Log.LogError("co-op: steam join failed: " + e); Fail("steamJoin"); }
        }

        private void Begin(string name, string build, string avatar)
        {
            Error = "";
            session = new CoopSession(transport, name, build);
            session.SetLocalProfile(name, avatar);
            session.OnLog += line => Plugin.Log.LogInfo("[coop] " + line);
            session.OnSaveReceived += (file, bytes) =>
            {
                Plugin.Log.LogInfo("co-op: the host's save " + file + " is here");
                if (session.Playing) GuestDaySave(); // later saves are the host's next days
            };
            session.OnGuestJoinedWhilePlaying += () => plugin.StartCoroutine(SendCurrentSave());
            // The first launch only; each next day goes through the day switch instead.
            session.OnLaunch += () => { beenInShop = false; if (session.Launches == 1) plugin.StartCoroutine(Launch()); };
            CoopRoom.Attach(session);
            CoopDevices.Attach(session);
            CoopStock.Attach(session);
            CoopHeld.Attach(session);
            CoopTextures.Attach(session);
            CoopDelivery.Attach(session);
            CoopFurniture.Attach(session);
            CoopCensus.Attach(session);
            CoopServiceBoxes.Attach(session);
            CoopPause.Attach(session);
            CoopCash.Attach(session);
            CoopSettings.Attach(session);
            CoopEmail.Attach(session);
            CoopDayReady.Attach(session);
            CoopToDo.Attach(session);
        }

        private void Fail(string why)
        {
            Error = why;
            Plugin.Log.LogError("co-op: " + why);
            Stop();
            Error = why;
        }

        public void Stop()
        {
            if (session != null) { session.Leave(); session.Tick(plugin.NowMs); }
            transport?.Dispose();
            transport = null;
            session = null;
            CoopRoom.Attach(null);
            CoopDevices.Attach(null);
            CoopStock.Attach(null);
            CoopHeld.Attach(null);
            CoopTextures.Attach(null);
            CoopDelivery.Attach(null);
            CoopFurniture.Attach(null);
            CoopCensus.Attach(null);
            CoopServiceBoxes.Attach(null);
            CoopPause.Attach(null);
            CoopCash.Attach(null);
            CoopSettings.Attach(null);
            CoopEmail.Attach(null);
            CoopDayReady.Attach(null);
            CoopToDo.Attach(null);
            launching = false;
            worldReportAt = -1;
            lostSince = -1;
            beenInShop = false;
            LeftNotice = "";
            LocalWorldCount = -1;
            LocalWorldHash = "";
        }

        // ----- host: Start -----

        // From the main menu (the save "Continue" loads) or from inside the host's running game (saved first).
        public bool CanStart => session != null && session.CanStart && (Lobby.InMainMenu(out _) || Lobby.InGame) && !Plugin.Broken && !starting;
        private bool starting;

        public void Start()
        {
            if (!CanStart) return;
            if (Lobby.InGame) { plugin.StartCoroutine(StartFromGame()); return; }
            var profiles = VersusProfile.Profiles;
            int slot = profiles != null ? SaveScheme.Selected(profiles) : -1;
            var path = slot >= 0 ? VersusProfile.LatestStory(slot) : null;
            if (path == null) { Error = "noSave"; Plugin.Log.LogError("co-op: no save in " + SaveScheme.Name + " " + slot); return; }
            Error = "";
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e) { Plugin.Log.LogError("co-op: reading the save: " + e); Error = "saveFailed"; return; }
            Plugin.Log.LogInfo("co-op: sending " + Path.GetFileName(path) + " (" + bytes.Length + " bytes) from " + SaveScheme.Name + " " + slot);
            session.StartWithSave(Path.GetFileName(path), bytes);
        }

        // The host is in its save: save it now, send that; the host stays where it is (see Launch).
        private IEnumerator StartFromGame()
        {
            starting = true;
            try
            {
                var saver = UnityEngine.Object.FindAnyObjectByType<GameplaySaveLoadService>(FindObjectsInactive.Include);
                var profiles = VersusProfile.Profiles;
                if (saver == null || profiles == null) { Plugin.Log.LogError("co-op: no save service"); Error = "saveFailed"; yield break; }
                bool saved = false;
                SaveScheme.SaveCareer(saver, profiles, () => saved = true);
                float until = Time.realtimeSinceStartup + 30f;
                while (!saved && Time.realtimeSinceStartup < until) yield return null;
                var path = NewestOwnStory();
                if (!saved || path == null) { Plugin.Log.LogError("co-op: the save before hosting did not happen"); Error = "saveFailed"; yield break; }
                var bytes = File.ReadAllBytes(path);
                Plugin.Log.LogInfo("co-op: hosting from inside the game: sending " + Path.GetFileName(path) + " (" + bytes.Length + " bytes)");
                session?.StartWithSave(Path.GetFileName(path), bytes);
            }
            finally { starting = false; }
        }

        // ----- guest: install the host's save -----

        private void InstallHostSave(string name, byte[] bytes)
        {
            var profiles = VersusProfile.Profiles;
            if (profiles == null) { Plugin.Log.LogError("co-op: no save system yet"); Error = "saveFailed"; return; }
            int current = SaveScheme.Selected(profiles);
            if (current != Slot && current != VersusProfile.Slot) returnSlot.Value = current;
            VersusProfile.InstallStory(Slot, name, bytes);
            SaveScheme.Select(profiles, Slot);
            Plugin.Log.LogInfo("co-op: the host's save installed as " + SaveScheme.Name + " " + Slot + " (own " + SaveScheme.Name + " " + returnSlot.Value + " remembered)");
        }

        // Main menu without a co-op game: give the guest their own slot back.
        public static void RestoreInMainMenu()
        {
            var profiles = VersusProfile.Profiles;
            if (profiles == null || !GuestSlotActive) return;
            int back = returnSlot.Value >= SaveScheme.LowestOwn && returnSlot.Value < Slot ? returnSlot.Value : 1;
            SaveScheme.Select(profiles, back);
            returnSlot.Value = -1;
            Plugin.Log.LogInfo("co-op: back to the player's own " + SaveScheme.Name + " " + back);
        }

        // ----- both: the first load -----

        // The first start, from the main menu. The guest writes the host's save into slot 8 only now, right before
        // Continue loads it.
        private IEnumerator Launch()
        {
            if (launching) yield break;
            launching = true;
            try
            {
                // A host that started from inside its game is already where it should be.
                if (session != null && session.IsHost && Lobby.InGame) yield break;
                // A guest that joined from inside its own game leaves it first (its own save is kept as usual).
                if (session != null && !session.IsHost && Lobby.InGame)
                {
                    Plugin.Log.LogInfo("co-op: leaving the guest's own game to load the host's");
                    yield return plugin.StartCoroutine(VersusProfile.ExitWorkshopToMainMenu());
                }
                float deadline = Time.realtimeSinceStartup + 90f;
                while (!Lobby.InMainMenu(out _))
                {
                    if (Time.realtimeSinceStartup > deadline) { Plugin.Log.LogError("co-op: the main menu did not come"); Error = "menuStuck"; yield break; }
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                if (session != null && !session.IsHost && session.ReceivedSave != null) InstallHostSave(session.SaveFileName, session.ReceivedSave);
                while (!Lobby.ContinueNow())
                {
                    if (Time.realtimeSinceStartup > deadline) { Plugin.Log.LogError("co-op: the main menu did not respond"); Error = "menuStuck"; yield break; }
                    yield return new WaitForSecondsRealtime(0.25f);
                }
            }
            finally { launching = false; }
        }

        // Host: a guest joined (or came back) while the host plays: save now and send that save.
        public int Rejoins { get; private set; }

        private IEnumerator SendCurrentSave()
        {
            var saver = UnityEngine.Object.FindAnyObjectByType<GameplaySaveLoadService>(FindObjectsInactive.Include);
            var profiles = VersusProfile.Profiles;
            if (saver == null || profiles == null || session == null) yield break;
            bool saved = false;
            SaveScheme.SaveCareer(saver, profiles, () => saved = true);
            float until = Time.realtimeSinceStartup + 30f;
            while (!saved && Time.realtimeSinceStartup < until) yield return null;
            var path = NewestOwnStory();
            if (!saved || path == null || session == null) { Plugin.Log.LogError("co-op: no save for the returning guest"); yield break; }
            var bytes = File.ReadAllBytes(path);
            bool sent = session.Resync(Path.GetFileName(path), bytes);
            if (sent) Rejoins++;
            Plugin.Log.LogInfo("co-op: a guest joined mid-game: sent " + Path.GetFileName(path) + " (" + bytes.Length + " bytes): " + sent);
        }

        // ----- the end of the day, in one flow for both -----
        // Host: the game's own day switch (bicycle -> store closed -> TransitionToEndOfDayService) autosaves, then
        // shows the end-of-day screens and loads the next morning from that save. The autosave goes to the guest
        // as soon as it is written.
        // Guest: when that save arrives, the guest's game runs the same day switch (same screens), except that its
        // autosave step writes the host's save into slot 8 instead of saving the guest's copy of the shop. The next
        // morning then loads, on both, from the same file. No main menu on either side.

        // ----- leaving and drop-outs -----
        // Guest: the host is gone (left, crashed, timed out) -> after a short notice, back to the main menu,
        // where the guest's own slot comes back. Host: the guest is gone -> a notice; the guest can rejoin.
        // Either: back in the main menu after having been in the shop (pause menu -> main menu) ends co-op.
        public string LeftNotice { get; private set; } = "";
        public int LeaveCountdown { get; private set; }
        public int GuestExits { get; private set; }
        private float lostSince = -1;
        private bool beenInShop;
        private bool leaving;
        // The host said it leaves: a short notice. The host went silent: wait longer, a long load on its side
        // (a new day's scenes) can stall its game for seconds.
        private const float GuestLeaveAfterBye = 6f;
        private const float GuestLeaveAfterSilence = 25f;

        private void WatchPresence()
        {
            if (session == null || !session.Playing) { LeftNotice = ""; return; }
            if (InShopFor(3f)) beenInShop = true;
            if (beenInShop && Lobby.InMainMenu(out _) && !leaving)
            {
                Plugin.Log.LogInfo("co-op: back in the main menu: co-op ends");
                Stop();
                return;
            }
            bool lost = session.Connection != ConnectionState.Connected;
            if (!lost) { lostSince = -1; LeftNotice = ""; return; }
            if (lostSince < 0) lostSince = Time.unscaledTime;
            if (session.IsHost) { LeftNotice = "guestLeft"; return; }
            LeftNotice = "hostLeft";
            float after = session.PeerLeft ? GuestLeaveAfterBye : GuestLeaveAfterSilence;
            LeaveCountdown = Mathf.Max(0, Mathf.CeilToInt(after - (Time.unscaledTime - lostSince)));
            if (LeaveCountdown == 0 && !leaving) plugin.StartCoroutine(GuestLeaves());
        }

        private IEnumerator GuestLeaves()
        {
            leaving = true;
            GuestExits++;
            Plugin.Log.LogInfo("co-op: the host is gone: the guest goes back to the main menu");
            try
            {
                if (Lobby.Global != null && Lobby.Global.IsInGameLoop)
                    yield return plugin.StartCoroutine(VersusProfile.ExitWorkshopToMainMenu());
                float until = Time.realtimeSinceStartup + 60f;
                while (!Lobby.InMainMenu(out _) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.25f);
                Stop();
                Error = "hostLeft";
            }
            finally { leaving = false; }
        }

        public int DaySaves { get; private set; }
        public int GuestDaySwitches { get; private set; }
        private static bool hostSwitchingDay;
        private static bool guestSwitchingDay;
        private static TransitionToEndOfDayService transition;

        // Host: the day switch starts (store closed, skip week).
        public void HostDayEnding()
        {
            if (session == null || !session.IsHost || !session.Playing) return;
            hostSwitchingDay = true;
            CoopDayReady.HostDayEnds();
            session.DayEnding();
            Plugin.Log.LogInfo("co-op: the host ended the day; its end-of-day save goes to the guest");
        }

        // Host: the day switch's autosave is on disk.
        private void SendDaySave()
        {
            hostSwitchingDay = false;
            var path = NewestOwnStory();
            if (path == null || session == null) { Plugin.Log.LogError("co-op: no end-of-day save to send"); return; }
            var bytes = File.ReadAllBytes(path);
            bool sent = session.Resync(Path.GetFileName(path), bytes);
            if (sent) DaySaves++;
            Plugin.Log.LogInfo("co-op: end-of-day save " + Path.GetFileName(path) + " (" + bytes.Length + " bytes) sent: " + sent);
        }

        // The newest Story save of the player's own slots (the day switch autosaves into the autosave slot).
        private static string NewestOwnStory() => VersusProfile.NewestOwnStory();

        // Guest: the host's end-of-day save is here: run the game's day switch with it.
        private void GuestDaySave()
        {
            if (session == null || session.IsHost || !session.Playing) return;
            if (transition == null) { Plugin.Log.LogError("co-op: no day switch service on the guest"); return; }
            guestSwitchingDay = true;
            GuestDaySwitches++;
            Plugin.Log.LogInfo("co-op: the host's end-of-day save is here: the guest's day ends too");
            transition.PerformDaySwitchingOperation();
        }

        [HarmonyPatch(typeof(Restory.Data.SaveLoad.SaveSlotService), nameof(Restory.Data.SaveLoad.SaveSlotService.AutoSaveGame))]
        private static class DaySwitchAutosave
        {
            private static bool Prefix(ref Action onComplete)
            {
                var coop = Plugin.Instance?.Coop;
                if (coop == null) return true;
                if (hostSwitchingDay)
                {
                    var then = onComplete;
                    onComplete = () => { coop.SendDaySave(); then?.Invoke(); };
                    return true;
                }
                if (guestSwitchingDay && coop.session != null && coop.session.ReceivedSave != null)
                {
                    guestSwitchingDay = false;
                    coop.InstallHostSave(coop.session.SaveFileName, coop.session.ReceivedSave);
                    onComplete?.Invoke();
                    return false; // the guest's copy of the shop is not saved: the host's save is the next day
                }
                return true;
            }
        }

        [HarmonyPatch]
        private static class TransitionCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.FirstConstructor(typeof(TransitionToEndOfDayService), c => c.GetParameters().Length > 0);
            private static void Postfix(TransitionToEndOfDayService __instance) => transition = __instance;
        }

        // ----- per frame -----

        private float joinStartedAt = -1;

        public void Tick(long nowMs)
        {
            if (session != null && !session.IsHost && session.Connection == ConnectionState.Waiting)
            {
                if (joinStartedAt < 0) joinStartedAt = Time.unscaledTime;
                else if (Time.unscaledTime - joinStartedAt > Plugin.JoinTimeout(transport)) { Fail(transport.Route == "Steam" ? "noHostSteam" : "noHost"); joinStartedAt = -1; return; }
            }
            else joinStartedAt = -1;
            if (session == null) { CoopShop.Tick(null); return; }
            CoopShop.Tick(session);
            session.Tick(nowMs);
            WatchPresence();
            if (session == null) return;
            CoopRoom.Tick();
            CoopDevices.Tick();
            CoopStock.Tick();
            CoopHeld.Tick();
            CoopDelivery.Tick();
            CoopFurniture.Tick();
            CoopCensus.Tick();
            CoopServiceBoxes.Tick();
            CoopPause.Tick();
            CoopSettings.Tick();
            CoopEmail.Tick();
            CoopDayReady.Tick();

            // A few seconds into the shop: report what was loaded (ids of every registered room object).
            bool inShop = session.Playing && Lobby.Global != null && Lobby.Global.IsInGameLoop && RegistryCapture.Instance != null;
            if (!inShop) { worldReportAt = -1; return; }
            if (worldReportAt < 0) worldReportAt = Time.unscaledTime + 5f;
            if (Time.unscaledTime < worldReportAt) return;
            worldReportAt = Time.unscaledTime + 5f;
            ReportWorld();
        }

        private void ReportWorld()
        {
            var ids = new List<string>();
            foreach (var kv in RegistryCapture.Instance.All)
                if (kv.Key != null && !string.IsNullOrEmpty(kv.Key.UniqueId)) ids.Add(kv.Key.UniqueId + " " + kv.Key.State + " " + kv.Key.name);
            ids.Sort(StringComparer.Ordinal);
            string joined = string.Join("\n", ids.Select(l => l.Split(' ')[0]));
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(joined))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            if (hash != LocalWorldHash)
            {
                // For tests: the full list next to the status file, to diff the two shops.
                try
                {
                    lastIdsFile = Path.Combine(plugin.DataDir, "coop_ids.txt");
                    File.WriteAllText(lastIdsFile, string.Join("\n", ids));
                }
                catch (Exception e) { Plugin.Log.LogWarning("co-op: ids file: " + e.Message); }
                Plugin.Log.LogInfo("co-op: in the shop, " + ids.Count + " objects, ids " + hash);
            }
            LocalWorldCount = ids.Count;
            LocalWorldHash = hash;
            session.SetLocalWorld(ids.Count, hash);
        }

        [HarmonyPatch(typeof(InteractiveObjectRegistry), nameof(InteractiveObjectRegistry.Register))]
        private static class RegistryCapture
        {
            public static InteractiveObjectRegistry Instance;
            private static void Postfix(InteractiveObjectRegistry __instance) => Instance = __instance;
        }

        // While the guest plays the host's save, every save (autosave, end of day) stays in slot 8.
        [HarmonyPatch]
        private static class KeepGuestSavesInCoopSlot
        {
            private static System.Reflection.MethodBase target;

            private static bool Prepare()
            {
                target = AccessTools.Method(typeof(GameplaySaveLoadService), "SaveProgressAsync", new[] { typeof(int), typeof(Action) });
                return target != null;
            }

            private static System.Reflection.MethodBase TargetMethod() => target;

            private static void Prefix(ref int slotIndex)
            {
                if (!GuestSlotActive || slotIndex == Slot) return;
                Plugin.Log.LogInfo("co-op: save for slot " + slotIndex + " kept in the co-op slot " + Slot);
                slotIndex = Slot;
            }
        }
    }
}
