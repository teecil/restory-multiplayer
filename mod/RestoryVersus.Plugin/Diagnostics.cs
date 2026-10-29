using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RestoryVersus
{
    // -versus-diag: logs which pictures could serve as avatars, once in the main menu and once in the
    // workshop. ProfilesInfo turned out empty in this build (no profile screen), so the avatar source is
    // chosen from what this reports.
    internal static class Diagnostics
    {
        public static IEnumerator Run()
        {
            float until = Time.realtimeSinceStartup + 120f;
            while (!Lobby.InMainMenu(out _) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
            Report("main menu");
            try
            {
                foreach (var cfg in Resources.FindObjectsOfTypeAll<Restory.Data.GameConfigs.GameConfig>())
                {
                    var langs = HarmonyLib.AccessTools.Field(cfg.GetType(), "supportedLocalizations").GetValue(cfg) as SystemLanguage[];
                    Plugin.Log.LogInfo("[diag] game languages (" + cfg.name + "): " + (langs == null ? "-" : string.Join(", ", langs.Select(l => l.ToString()).ToArray())));
                }
                var gs = UnityEngine.Object.FindAnyObjectByType<Restory.Gameplay.GameSettings.GameSettingsManager>(FindObjectsInactive.Include);
                Plugin.Log.LogInfo("[diag] current language: " + (gs != null ? gs.Localization.ToString() : "-") + ", PlayerPrefs Language '" + PlayerPrefs.GetString("Language", "") + "'");
            }
            catch (Exception e) { Plugin.Log.LogError("[diag] languages: " + e); }
            // The "Network game" sign: screenshot the menu with it, press it, screenshot the opened window.
            float signUntil = Time.realtimeSinceStartup + 10f;
            while (VersusMenu.Instance != null && !VersusMenu.Instance.GameMenuButtonPresent && Time.realtimeSinceStartup < signUntil)
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot("diag_menu_sign.png");
            bool clicked = VersusMenu.Instance != null && VersusMenu.Instance.ClickGameMenuButton();
            Plugin.Log.LogInfo("[diag] network game sign clicked: " + clicked);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot("diag_menu_window.png");

            // The game's own panels, to build the versus window from the same parts.
            var mainMenu = UnityEngine.Object.FindAnyObjectByType<Restory.UI.Presenters.MainMenu.GUI_MainMenu>();
            if (VersusMenu.Instance != null) VersusMenu.Instance.HideForDiagnostics();
            if (mainMenu != null)
            {
                HarmonyLib.AccessTools.Method(mainMenu.GetType(), "ResolveLoadGameMenu")?.Invoke(mainMenu, null);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot("diag_native_loadgame.png");
                var slots = UnityEngine.Object.FindAnyObjectByType<Restory.UI.Presenters.SaveSlots.GUI_SaveSlotsPanel>();
                if (slots != null) { Dump(slots.transform, "loadgame"); slots.Hide(); }
                yield return new WaitForSecondsRealtime(1f);
                HarmonyLib.AccessTools.Method(mainMenu.GetType(), "ResolveLanguageMenu")?.Invoke(mainMenu, null);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot("diag_native_settings.png");
                var settings = UnityEngine.Object.FindAnyObjectByType<Restory.UI.Presenters.SettingsMenu.GUI_SettingsMenu>();
                if (settings != null) { Dump(settings.transform, "settings"); settings.Hide(); }
            }

            // The versus pages built from those parts: the connect page, then the lobby of a host waiting alone.
            yield return new WaitForSecondsRealtime(1f);
            if (VersusMenu.Instance != null)
            {
                var m = VersusMenu.Instance;
                m.ClickGameMenuButton();
                yield return new WaitForSecondsRealtime(1.2f);
                yield return Shot("diag_page_choose.png");
                Plugin.Log.LogInfo("[diag] co-op card: " + m.Click("pick:coop"));
                yield return new WaitForSecondsRealtime(1.2f);
                yield return Shot("diag_page_coop.png");
                Plugin.Log.LogInfo("[diag] co-op back: " + m.Click("tab"));
                yield return new WaitForSecondsRealtime(0.5f); // the choice page shows on the next frame
                Plugin.Log.LogInfo("[diag] versus card: " + m.Click("pick:versus"));
                yield return new WaitForSecondsRealtime(1.2f);
                yield return Shot("diag_page_connect.png");
                // Both routes of the connect page (without Steam the Steam one says so and keeps the address).
                Plugin.Log.LogInfo("[diag] steam available: " + SteamNet.Available + ", route steam: " + Plugin.Instance.UseSteam);
                m.Click("route:steam");
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot("diag_page_connect_steam.png");
                m.Click("route:ip");
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot("diag_page_connect_ip.png");
                m.DiagHost();
                yield return new WaitForSecondsRealtime(2f);
                yield return Shot("diag_page_lobby.png");
                Plugin.Log.LogInfo("[diag] lobby back: " + m.Click("leave"));
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot("diag_page_after_back.png");
                // The co-op lobby of a host waiting for a friend.
                m.Click("tab");
                yield return new WaitForSecondsRealtime(0.5f);
                Plugin.Log.LogInfo("[diag] co-op card again: " + m.Click("pick:coop"));
                yield return new WaitForSecondsRealtime(0.5f);
                Plugin.Log.LogInfo("[diag] co-op host: " + m.Click("host"));
                yield return new WaitForSecondsRealtime(2f);
                yield return Shot("diag_page_coop_lobby.png");
                m.Click("coop:leave");
                // Errors as the player sees them: a bad address, then a host that does not answer.
                yield return new WaitForSecondsRealtime(1f);
                Plugin.Instance.StartCoopJoin("192.168.0.10:abc");
                yield return new WaitForSecondsRealtime(1.2f);
                yield return Shot("diag_error_address.png");
                Plugin.Instance.StartCoopJoin("127.0.0.1:27599");
                yield return new WaitForSecondsRealtime(Plugin.JoinTimeoutSeconds + 2f);
                yield return Shot("diag_error_nohost.png");
            }
            until = Time.realtimeSinceStartup + 120f;
            while (!RaceDevices.InGameplay && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(5f);
            Report("workshop");
        }

        // -versus-diag-dayend: load the test copy's save, end the day, photograph and dump the end-of-day screen
        // (the look the versus result screen borrows).
        public static IEnumerator DayEnd()
        {
            while (!Lobby.InMainMenu(out _)) yield return new WaitForSecondsRealtime(0.5f);
            // Where the end-of-day art could be loaded from outside its scene: the game's Addressables keys.
            try
            {
                int n = 0;
                foreach (var loc in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                    foreach (var key in loc.Keys)
                    {
                        var k = key as string;
                        if (k == null || !System.Text.RegularExpressions.Regex.IsMatch(k, "Notebook|ResultWindow|DayEnd|EndOfDay|Check|DashLine|RichCat|Stamp", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                        if (n++ < 60) Plugin.Log.LogInfo("[diag] addressable key: " + k);
                    }
                Plugin.Log.LogInfo("[diag] addressable keys matching: " + n);
            }
            catch (Exception e) { Plugin.Log.LogError("[diag] addressables: " + e); }
            yield return new WaitForSecondsRealtime(1f);
            Lobby.ContinueNow();
            while (!CoopGame.InShopFor(5f)) yield return new WaitForSecondsRealtime(0.5f);
            var days = UnityEngine.Object.FindAnyObjectByType<Restory.TimeSystems.MainDayTimeSwitchingService>(FindObjectsInactive.Include);
            Plugin.Log.LogInfo("[diag] ending the day: " + (days != null));
            days?.ForceEndDay();
            Restory.UI.Presenters.DayEndWindow.GUI_DayEndWindow w = null;
            float until = Time.realtimeSinceStartup + 120f;
            while ((w == null || !w.isActiveAndEnabled) && Time.realtimeSinceStartup < until)
            {
                yield return new WaitForSecondsRealtime(1f);
                w = UnityEngine.Object.FindAnyObjectByType<Restory.UI.Presenters.DayEndWindow.GUI_DayEndWindow>();
            }
            yield return new WaitForSecondsRealtime(6f);
            yield return Shot("diag_dayend.png");
            if (w != null) Dump(w.transform.root, "dayend");
            yield return new WaitForSecondsRealtime(4f);
            yield return Shot("diag_dayend2.png");
        }

        private static IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tex.SetPixels32(px);
            var path = System.IO.Path.Combine(Application.persistentDataPath, name);
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Plugin.Log.LogInfo("[diag] screenshot " + path);
        }

        // Main menu buttons: where they sit, what lays them out, what writes their labels.
        private static void DumpMainMenu()
        {
            var log = Plugin.Log;
            var view = UnityEngine.Object.FindAnyObjectByType<Restory.UI.Views.MainMenu.GUI_MainMenuView>();
            if (view == null) { log.LogInfo("[diag] no GUI_MainMenuView"); return; }
            foreach (var name in new[] { "continueButton", "loadGameButton", "newGameButton", "optionsButton", "quitGameButton" })
            {
                var b = HarmonyLib.Traverse.Create(view).Field(name).GetValue<UnityEngine.UI.Button>();
                if (b == null) { log.LogInfo("[diag] " + name + ": null"); continue; }
                var rt = (RectTransform)b.transform;
                var parent = rt.parent;
                log.LogInfo($"[diag] {name}: path={Path(rt)} active={b.gameObject.activeSelf} sibling={rt.GetSiblingIndex()} anchored={rt.anchoredPosition} size={rt.sizeDelta} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} pivot={rt.pivot}");
                log.LogInfo("[diag]   components: " + string.Join(", ", b.GetComponents<Component>().Select(c => c.GetType().Name)));
                log.LogInfo("[diag]   children: " + string.Join(" | ", b.GetComponentsInChildren<Component>(true).Where(c => !(c is Transform)).Select(c => c.gameObject.name + ":" + c.GetType().Name)));
                var label = b.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (label != null) log.LogInfo("[diag]   label: '" + label.text + "' font=" + label.font?.name + " size=" + label.fontSize);
                log.LogInfo("[diag]   parent " + parent.name + " components: " + string.Join(", ", parent.GetComponents<Component>().Select(c => c.GetType().Name)));
            }
        }

        // Visual parts of a panel: images (sprite, slicing, colour), texts (font, size, colour), buttons.
        private static void Dump(Transform root, string tag)
        {
            var log = Plugin.Log;
            int lines = 0;
            void Walk(Transform t, int depth)
            {
                if (lines > 400 || depth > 12 || !t.gameObject.activeInHierarchy) return;
                var parts = new List<string>();
                var rt = t as RectTransform;
                if (rt != null) parts.Add("size=" + rt.rect.size);
                var img = t.GetComponent<UnityEngine.UI.Image>();
                if (img != null) parts.Add("Image(" + (img.sprite != null ? img.sprite.name + " border=" + img.sprite.border : "no sprite") + " type=" + img.type + " color=#" + ColorUtility.ToHtmlStringRGBA(img.color) + ")");
                var raw = t.GetComponent<UnityEngine.UI.RawImage>();
                if (raw != null) parts.Add("RawImage(" + (raw.texture != null ? raw.texture.name : "-") + ")");
                var txt = t.GetComponent<TMPro.TMP_Text>();
                if (txt != null) parts.Add("Text('" + (txt.text.Length > 24 ? txt.text.Substring(0, 24) : txt.text) + "' font=" + (txt.font != null ? txt.font.name : "-") + " size=" + txt.fontSize + " color=#" + ColorUtility.ToHtmlStringRGBA(txt.color) + ")");
                if (t.GetComponent<UnityEngine.UI.Button>() != null) parts.Add("Button");
                if (t.GetComponent<UnityEngine.UI.Toggle>() != null) parts.Add("Toggle");
                if (t.GetComponent<UnityEngine.UI.Slider>() != null) parts.Add("Slider");
                if (t.GetComponent<TMPro.TMP_Dropdown>() != null) parts.Add("Dropdown");
                if (t.GetComponent<TMPro.TMP_InputField>() != null) parts.Add("InputField");
                if (t.GetComponent<UnityEngine.UI.LayoutGroup>() != null) parts.Add(t.GetComponent<UnityEngine.UI.LayoutGroup>().GetType().Name);
                var game = t.GetComponents<MonoBehaviour>().Where(m => m != null && m.GetType().Namespace != null && m.GetType().Namespace.StartsWith("Restory")).Select(m => m.GetType().Name);
                var g = string.Join(",", game);
                if (g.Length > 0) parts.Add("[" + g + "]");
                log.LogInfo("[diag:" + tag + "] " + new string(' ', depth * 2) + t.name + "  " + string.Join("  ", parts));
                lines++;
                foreach (Transform c in t) Walk(c, depth + 1);
            }
            Walk(root, 0);
        }

        private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        private static void Report(string where)
        {
            var log = Plugin.Log;
            log.LogInfo("[diag] ---- " + where);
            if (where == "main menu") { try { DumpMainMenu(); } catch (Exception e) { log.LogError("[diag] menu dump: " + e); } }
            try
            {
                log.LogInfo("[diag] profile icons via ProfilesInfo: " + VersusProfile.Icons().Count);
                var icons = Resources.FindObjectsOfTypeAll<Restory.Data.Profiles.ProfileIcon>();
                log.LogInfo("[diag] ProfileIcon assets loaded: " + icons.Length + " " + string.Join(", ", icons.Take(12).Select(i => i.Id)));
                var infos = Resources.FindObjectsOfTypeAll<Restory.Data.Profiles.ProfilesInfo>();
                log.LogInfo("[diag] ProfilesInfo assets loaded: " + infos.Length + " icons " + string.Join(",", infos.Select(i => i.ProfilesIcons == null ? -1 : i.ProfilesIcons.Count)));

                var dm = Type.GetType("PixelCrushers.DialogueSystem.DialogueManager, DialogueSystem");
                var dbProp = dm?.GetProperty("masterDatabase");
                var db = dbProp?.GetValue(null, null);
                var actors = db?.GetType().GetField("actors")?.GetValue(db) as IList;
                int withSprite = 0, withTex = 0;
                if (actors != null)
                {
                    foreach (var a in actors)
                    {
                        var t = a.GetType();
                        var sp = t.GetField("spritePortrait")?.GetValue(a) as Sprite;
                        var tx = t.GetField("portrait")?.GetValue(a) as Texture2D;
                        var name = t.GetField("Name")?.GetValue(a) ?? t.GetProperty("Name")?.GetValue(a, null);
                        if (sp != null) withSprite++;
                        if (tx != null) withTex++;
                        if (sp != null || tx != null) log.LogInfo("[diag]   actor " + name + " sprite=" + (sp != null ? sp.name : "-") + " tex=" + (tx != null ? tx.name : "-"));
                    }
                }
                log.LogInfo("[diag] dialogue actors: " + (actors == null ? "no database" : actors.Count + ", with sprite portrait " + withSprite + ", with texture portrait " + withTex));

                var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
                var named = sprites.Where(s => s != null && System.Text.RegularExpressions.Regex.IsMatch(s.name, "avatar|portrait|profile|face|icon_npc|character", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Take(40).Select(s => s.name);
                log.LogInfo("[diag] sprites loaded: " + sprites.Length + "; matching avatar/portrait/profile/face: " + string.Join(", ", named));
            }
            catch (Exception e) { log.LogError("[diag] " + e); }
        }
    }
}
