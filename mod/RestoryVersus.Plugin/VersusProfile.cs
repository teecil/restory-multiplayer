using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Restory.Achievements;
using Restory.AssetManagement.References;
using Restory.Data.Equipment;
using Restory.Data.SaveLoad;
using Restory.Gameplay.Common;
using Restory.Gameplay.Equipment;
using Restory.Gameplay.SaveLoad.Services;
using Restory.Gameplay.TimeSystems;
using Restory.Infrastructure.StateMachine;
using Restory.Infrastructure.StateMachine.States.InitializationStates;
using Restory.UI.Presenters.CompetitionStates;
using Restory.UI.Presenters.PauseMenu;
using UnityEngine;

namespace RestoryVersus
{
    // The versus workshop: save slot 9 of its own (the game's UI never shows it; file names take any
    // number), restored from a template that ships with the mod on every connect, so both players start from
    // the same clean workshop whatever their own saves hold, and their career is never touched.
    //   connect   -> (career workshop: save it, back to the main menu) -> template into slot 9 -> select 9
    //                -> Continue -> versus workshop: time stopped, every tool owned
    //   leave     -> main menu -> back to the player's own slot
    // A crash inside versus leaves 9 selected; the next main menu without a connection switches back.
    //
    // Two save schemes (see SaveScheme): 1.0.005r selects a profile (SetCurrentProfile, "Continue" loads the
    // current profile); 1.0.015r dropped profiles for save slots (SetLastUsedSlot, "Continue" loads the last
    // used slot, autosaves go to slot 0). One DLL handles both.
    internal static class VersusProfile
    {
        public const int Slot = 9;

        private static ConfigEntry<int> returnProfile;
        private static PlayerProfileService profiles;
        private static TimeSystem timeSystem;
        private static readonly TimeBlocker blocker = new TimeBlocker();
        private static bool timeBlocked;
        private static bool toolsGranted;

        // Player avatars (AvatarArt): the game's ProfilesInfo has a single icon in this build (checked with
        // -versus-diag), so the mod draws its own set.
        public static List<KeyValuePair<string, Sprite>> Icons()
        {
            var list = new List<KeyValuePair<string, Sprite>>();
            foreach (var id in AvatarArt.Ids()) list.Add(new KeyValuePair<string, Sprite>(id, AvatarArt.Get(id)));
            return list;
        }

        public static Sprite IconSprite(string id) => AvatarArt.Get(id);

        public static string TemplateDir => Path.Combine(Path.GetDirectoryName(typeof(VersusProfile).Assembly.Location), "template");
        // A template shipped next to the mod, or else the player's own newest save: the published mod ships no save
        // (game data may not be published), and the versus workshop only needs a shop to race in.
        public static bool HasTemplate => FindTemplate() != null || NewestOwnStory() != null;
        public static bool Active => profiles != null && SaveScheme.Selected(profiles) == Slot;
        // The selected profile (1.0.005r) or last used save slot (1.0.015r).
        public static int CurrentProfile => profiles != null ? SaveScheme.Selected(profiles) : 0;
        public static bool Busy { get; private set; }
        internal static PlayerProfileService Profiles => profiles;
        public static int ToolsGranted { get; private set; }

        private sealed class TimeBlocker : IActiveStateSwitchRequester { }

        public static void Init(ConfigFile config)
        {
            returnProfile = config.Bind("Internal", "ReturnProfile", -1, "Save profile/slot to go back to after versus (set by the mod).");
        }

        [HarmonyPatch(typeof(PlayerProfileService), nameof(PlayerProfileService.Initialize))]
        private static class ProfilesCapture
        {
            private static void Postfix(PlayerProfileService __instance) => profiles = __instance;
        }

        [HarmonyPatch]
        private static class TimeSystemCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(TimeSystem), nameof(TimeSystem.Initialize));
            private static void Postfix(TimeSystem __instance)
            {
                timeSystem = __instance;
                timeBlocked = false;
                toolsGranted = false;
            }
        }

        // 1.0.015r: while versus is selected, every save (the periodic autosave and the end-of-day one write to
        // slot 0, the player's autosave) goes to the versus slot instead. Not present in 1.0.005r, where saves
        // follow the selected profile anyway.
        [HarmonyPatch]
        private static class KeepSavesInVersusSlot
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
                if (!Active || slotIndex == Slot) return;
                Plugin.Log.LogInfo("versus: save for slot " + slotIndex + " kept in the versus slot " + Slot);
                slotIndex = Slot;
            }
        }

        // No Steam achievements from versus races.
        [HarmonyPatch(typeof(AchievementsManager), nameof(AchievementsManager.UnlockAchievement))]
        private static class NoAchievementsPatch
        {
            private static bool Prefix() => !Active;
        }

        // Time is stopped in versus, so the game would keep writing 00:00 into its contest timer on the mat;
        // the plugin writes the race time there instead (ContestHooks.ShowRaceTimeOnMat).
        [HarmonyPatch(typeof(GUI_CompetitionTimer), nameof(GUI_CompetitionTimer.UpdateTimer))]
        private static class RaceTimeOnMatPatch
        {
            private static bool Prefix() => !Active;
        }

        internal static string SaveDir => Path.Combine(Application.persistentDataPath, "SaveData");

        private static string FindTemplate()
        {
            if (!Directory.Exists(TemplateDir)) return null;
            var files = Directory.GetFiles(TemplateDir, "Story.*.restory");
            return files.Length > 0 ? files[0] : null;
        }

        // Writes the template as profile 9's only Story save, plus the player's own game settings (language,
        // graphics) for that profile.
        private static void InstallTemplate()
        {
            var template = FindTemplate() ?? NewestOwnStory();
            if (template == null) throw new FileNotFoundException("no versus template in " + TemplateDir + " and no save of the player's own");
            InstallStory(Slot, Path.GetFileName(template), File.ReadAllBytes(template));
            Plugin.Log.LogInfo("versus template installed as " + SaveScheme.Name + " " + Slot + " from "
                + (FindTemplate() != null ? Path.GetFileName(template) : "the player's own save " + Path.GetFileName(template)));
        }

        // The newest Story save of the player's own slots (not the mod's slots 8 and 9).
        internal static string NewestOwnStory()
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            for (int slot = SaveScheme.LowestOwn; slot < CoopGame.Slot; slot++)
            {
                var f = LatestStory(slot);
                if (f == null) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { bestTime = t; best = f; }
            }
            return best;
        }

        // Writes a Story save as the given slot's only save, plus the player's own game settings (language,
        // graphics) for that slot. Used for the versus template and for the host's save on a co-op guest.
        internal static void InstallStory(int slot, string sourceName, byte[] story)
        {
            Directory.CreateDirectory(SaveDir);
            foreach (var f in Directory.GetFiles(SaveDir, "*.slot" + slot + ".*"))
                File.Delete(f);

            // Story.<anything>-No-<iteration>.restory -> Story.slot<slot>.<postfix>-No-<iteration>.restory
            var m = Regex.Match(sourceName, @"-No-(\d+)\.restory$");
            string iteration = m.Success ? m.Groups[1].Value : "1";
            string postfix = "release";
            string ownSettings = null;
            foreach (var f in Directory.GetFiles(SaveDir, "gameSettings.slot*.restory"))
            {
                var mm = Regex.Match(Path.GetFileName(f), @"^gameSettings\.slot(\d+)\.(.+)\.restory$");
                if (!mm.Success) continue;
                postfix = mm.Groups[2].Value;
                // Settings follow the profile (always 1 in 1.0.015r), not the save slot.
                if (mm.Groups[1].Value == profiles.CurrentProfile.ToString()) ownSettings = f;
            }
            File.WriteAllBytes(Path.Combine(SaveDir, $"Story.slot{slot}.{postfix}-No-{iteration}.restory"), story);
            if (ownSettings != null)
                File.Copy(ownSettings, Path.Combine(SaveDir, $"gameSettings.slot{slot}.{postfix}.restory"), true);
        }

        // The file "Continue" loads for a slot: its Story save with the highest iteration (the game's own pick,
        // GameplayReadWriteCombinedDataService.GetLatestSaveFilePath). Null if the slot has none.
        internal static string LatestStory(int slot)
        {
            if (!Directory.Exists(SaveDir)) return null;
            string best = null;
            int bestIteration = -1;
            foreach (var f in Directory.GetFiles(SaveDir, "Story.slot" + slot + ".*-No-*.restory"))
            {
                var m = Regex.Match(Path.GetFileName(f), @"^Story\.slot" + slot + @"\..+-No-(\d+)\.restory$");
                if (m.Success && int.Parse(m.Groups[1].Value) > bestIteration) { bestIteration = int.Parse(m.Groups[1].Value); best = f; }
            }
            return best;
        }

        // Main menu, connected: switch to a fresh versus profile. True once profile 9 is selected.
        public static bool EnterFromMainMenu()
        {
            if (profiles == null) return false;
            if (Active) return true;
            if (!HasTemplate) { Plugin.Log.LogError("no versus template: racing in the current profile"); return true; }
            returnProfile.Value = SaveScheme.Selected(profiles);
            InstallTemplate();
            SaveScheme.Select(profiles, Slot);
            Plugin.Log.LogInfo("versus " + SaveScheme.Name + " " + Slot + " selected (player " + SaveScheme.Name + " " + returnProfile.Value + " remembered)");
            return true;
        }

        // Main menu without a versus connection: give the player their own profile back.
        public static void RestoreInMainMenu()
        {
            if (profiles == null || !Active) return;
            int back = returnProfile.Value >= SaveScheme.LowestOwn && returnProfile.Value < Slot ? returnProfile.Value : 1;
            SaveScheme.Select(profiles, back);
            returnProfile.Value = -1;
            Plugin.Log.LogInfo("back to the player's " + SaveScheme.Name + " " + back);
        }

        // Career workshop when a versus connection starts (or versus workshop when leaving): save if it is the
        // career, then load the main menu the way the pause menu does.
        public static IEnumerator ExitWorkshopToMainMenu()
        {
            if (Busy) yield break;
            Busy = true;
            try
            {
                var g = Lobby.Global;
                if (g == null || !Lobby.InGame) yield break;
                if (!Active)
                {
                    var saver = UnityEngine.Object.FindAnyObjectByType<GameplaySaveLoadService>(FindObjectsInactive.Include);
                    if (saver != null)
                    {
                        bool saved = false;
                        SaveScheme.SaveCareer(saver, profiles, () => saved = true);
                        float until = Time.realtimeSinceStartup + 20f;
                        while (!saved && Time.realtimeSinceStartup < until) yield return null;
                        Plugin.Log.LogInfo(saved ? "career saved before versus" : "career save did not confirm in 20 s");
                    }
                }
                var pause = UnityEngine.Object.FindAnyObjectByType<GUI_PauseMenu>(FindObjectsInactive.Include);
                var menuRef = pause != null ? AccessTools.FieldRefAccess<GUI_PauseMenu, GameScenesAssetRef>("mainMenuPresetToLoadRef")(pause) : null;
                if (menuRef == null) { Plugin.Log.LogError("main menu preset not found: cannot leave the workshop"); yield break; }
                Plugin.Log.LogInfo("leaving the workshop for the main menu");
                g.Enter<StartLoadingPresetListState, GameScenesAssetRef>(menuRef);
            }
            finally
            {
                Busy = false;
            }
        }

        // Once per versus workshop load: stop the clock (no customers, no end of day) and own every tool.
        public static void PrepareWorkshop()
        {
            if (!Active || !Lobby.InWorkshop) return;
            if (!timeBlocked && timeSystem != null)
            {
                timeSystem.BlockTimeSystem(blocker);
                timeBlocked = true;
                Plugin.Log.LogInfo("versus workshop: game time stopped");
            }
            if (!toolsGranted)
            {
                toolsGranted = true;
                var tracker = UnityEngine.Object.FindAnyObjectByType<AvailableToolsTrackingService>(FindObjectsInactive.Include);
                if (tracker == null) { Plugin.Log.LogError("tool tracker not found"); return; }
                int added = 0;
                foreach (var tool in Resources.FindObjectsOfTypeAll<ToolInfo>())
                {
                    if (tool == null) continue;
                    try
                    {
                        if (!tracker.IsToolAvailable(tool) && tracker.AddTool(tool)) added++;
                        tracker.RefillToolUses(tool);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("tool " + tool.name + ": " + e.Message); }
                }
                ToolsGranted = tracker.AvailableTools.Count;
                Plugin.Log.LogInfo($"versus workshop: {added} tools added, {ToolsGranted} owned");
            }
        }
    }
}

namespace RestoryVersus
{
    // The game's two save schemes, reached by reflection so one DLL runs on both builds (a direct call to a
    // method one build lacks fails when the calling method is compiled, i.e. at runtime on that build).
    internal static class SaveScheme
    {
        private static readonly System.Reflection.MethodInfo SetLastUsedSlot =
            AccessTools.Method(typeof(PlayerProfileService), "SetLastUsedSlot", new[] { typeof(int) });
        private static readonly System.Reflection.PropertyInfo LastUsedSlot =
            AccessTools.Property(typeof(PlayerProfileService), "LastUsedSlot");
        private static readonly System.Reflection.MethodInfo SaveToSlot =
            AccessTools.Method(typeof(GameplaySaveLoadService), "SaveProgressAsync", new[] { typeof(int), typeof(Action) });
        private static readonly System.Reflection.MethodInfo SaveCurrent =
            AccessTools.Method(typeof(GameplaySaveLoadService), "SaveProgressAsync", new[] { typeof(Action) });

        // 1.0.015r and later: save slots instead of profiles.
        public static bool Slots => SetLastUsedSlot != null && LastUsedSlot != null;
        public static string Name => Slots ? "save slot" : "profile";
        // Slot 0 is the autosave in 1.0.015r; profiles start at 1.
        public static int LowestOwn => Slots ? 0 : 1;

        public static int Selected(PlayerProfileService p) => Slots ? (int)LastUsedSlot.GetValue(p, null) : p.CurrentProfile;

        public static void Select(PlayerProfileService p, int n)
        {
            if (Slots) SetLastUsedSlot.Invoke(p, new object[] { n });
            else p.SetCurrentProfile(n);
        }

        // Save the career where the player is playing: the last used slot (1.0.015r) or the current profile.
        public static void SaveCareer(GameplaySaveLoadService saver, PlayerProfileService p, Action done)
        {
            if (SaveToSlot != null && p != null) SaveToSlot.Invoke(saver, new object[] { Selected(p), done });
            else if (SaveCurrent != null) SaveCurrent.Invoke(saver, new object[] { done });
            else done();
        }
    }
}
