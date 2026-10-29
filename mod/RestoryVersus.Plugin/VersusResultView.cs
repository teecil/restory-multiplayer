using System.Collections.Generic;
using System.Globalization;
using RestoryVersus.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RestoryVersus
{
    // The race result, laid out like the game's end-of-day screen (GUI_ResultWindow): an open notebook
    // (NotebookBig), the left page with a heading line and a block per player, the right page with a till receipt
    // (Check, Nunito) holding the outcome and the series score, and the two ways on: Rematch (same device and
    // mode, once both press it) and Change device (back to the lobby, where device and mode are chosen).
    // The day-end sprites live in the end-of-day scene; whatever is not loaded falls back to the mod's paper look.
    internal sealed partial class VersusMenu
    {
        private GameObject resultWin;
        private TextMeshProUGUI rHeading, rReceiptTitle, rReceiptBody, rSeries, rReason, rRematchHint;
        private readonly List<ResultBlock> rBlocks = new List<ResultBlock>();
        private Button rRematch, rChange;
        private TextMeshProUGUI rRematchLabel;

        // A sprite of the end-of-day screen: already loaded, or loaded from the game's own Addressables by its file
        // name (those screens live in another scene, so in the workshop they are not in memory).
        private static Sprite DayEndSprite(string name)
        {
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sp != null && sp.name == name) return sp;
            try
            {
                string suffix = "/" + name + ".png";
                foreach (var loc in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                    foreach (var key in loc.Keys)
                    {
                        if (!(key is string k) || !k.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)) continue;
                        var sprite = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>(k).WaitForCompletion();
                        if (sprite != null) { Plugin.Log.LogInfo("result screen: loaded " + k); return sprite; }
                    }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("result screen: " + name + " not loadable: " + e.Message); }
            return null;
        }

        private static TMP_FontAsset FontNamed(string name)
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && f.name == name) return f;
            return null;
        }

        private sealed class ResultBlock
        {
            public Image Avatar;
            public TextMeshProUGUI Name, Badge, Values;
        }

        private void EnsureResultWindow()
        {
            if (resultWin != null) return;
            var notebook = DayEndSprite("NotebookBig");
            var check = DayEndSprite("Check");
            var line = DayEndSprite("Line");
            var dash = DayEndSprite("DashLine");
            var cat = DayEndSprite("RichCat");
            var semiBold = FontNamed("ShantellSans-SemiBold SDF");
            var receiptFont = FontNamed("Nunito-Regular SDF");
            // Nunito has no CJK; the receipt keeps the menu's font where it lacks the glyphs.
            if (receiptFont != null && !receiptFont.HasCharacters(T("win") + T("lose") + T("seriesScore"), out uint[] _, true, true)) receiptFont = null;
            if (semiBold != null && !semiBold.HasCharacters(T("raceHeading") + T("you"), out uint[] _, true, true)) semiBold = null;
            Plugin.Log.LogInfo("result screen kit: notebook=" + (notebook != null) + " receipt=" + (check != null) + " line=" + (line != null)
                + " dash=" + (dash != null) + " cat=" + (cat != null) + " semibold=" + (semiBold != null) + " nunito=" + (receiptFont != null));

            resultWin = Rect(root.transform, "RaceResult", new Color32(39, 39, 39, 235), false);
            Stretch(resultWin);
            resultWin.transform.SetSiblingIndex(0);

            var book = new GameObject("Notebook", typeof(RectTransform), typeof(Image));
            book.transform.SetParent(resultWin.transform, false);
            var brt = book.GetComponent<RectTransform>();
            brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
            brt.sizeDelta = new Vector2(1252, 897);
            var bimg = book.GetComponent<Image>();
            if (notebook != null) bimg.sprite = notebook;
            else { bimg.sprite = round; bimg.type = Image.Type.Sliced; bimg.color = new Color32(242, 237, 225, 255); }
            bimg.raycastTarget = true;

            // Left page: what each player did.
            var left = Column(book.transform, "LeftSide", 10);
            var lrt = left.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0.125f, 0.12f); lrt.anchorMax = new Vector2(0.475f, 0.86f);
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var lv = left.GetComponent<VerticalLayoutGroup>();
            lv.childAlignment = TextAnchor.UpperLeft;
            rHeading = Label(left.transform, "", 26, TextMain, TextAlignmentOptions.Center, false);
            var rule = new GameObject("Line", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            rule.transform.SetParent(left.transform, false);
            rule.GetComponent<LayoutElement>().preferredHeight = line != null ? 3 : 2;
            var ri = rule.GetComponent<Image>();
            if (line != null) ri.sprite = line; else ri.color = TextMain;
            ri.raycastTarget = false;
            rBlocks.Clear();
            for (int i = 0; i < 2; i++)
            {
                var block = new ResultBlock();
                var head = Row(left.transform, 10);
                block.Avatar = AvatarImage(head.transform, 40);
                block.Name = Label(head.transform, "", 25, TextMain, TextAlignmentOptions.Left, false);
                if (semiBold != null) block.Name.font = semiBold;
                block.Name.textWrappingMode = TextWrappingModes.NoWrap;
                block.Name.overflowMode = TextOverflowModes.Ellipsis;
                Flex(block.Name.gameObject, 1);
                block.Badge = Label(head.transform, "", 20, Green, TextAlignmentOptions.Right, false);
                block.Badge.gameObject.AddComponent<LayoutElement>().preferredWidth = 150;
                block.Values = Label(left.transform, "", 22, TextMain, TextAlignmentOptions.Left, false);
                block.Values.lineSpacing = 8;
                rBlocks.Add(block);
            }
            if (cat != null)
            {
                var c = new GameObject("Cat", typeof(RectTransform), typeof(Image));
                c.transform.SetParent(book.transform, false);
                var crt = c.GetComponent<RectTransform>();
                crt.anchorMin = crt.anchorMax = new Vector2(0.4f, 0.1f);
                crt.sizeDelta = new Vector2(67, 85);
                var ci = c.GetComponent<Image>();
                ci.sprite = cat;
                ci.color = new Color32(63, 58, 58, 204);
                ci.raycastTarget = false;
            }

            // Right page: the receipt and the buttons.
            var right = Column(book.transform, "RightSide", 14);
            var rrt = right.GetComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0.575f, 0.1f); rrt.anchorMax = new Vector2(0.915f, 0.88f);
            rrt.offsetMin = rrt.offsetMax = Vector2.zero;
            var rv = right.GetComponent<VerticalLayoutGroup>();
            rv.childAlignment = TextAnchor.UpperCenter;
            rv.childForceExpandWidth = true;
            var receipt = Rect(right.transform, "Receipt", Color.white, check == null);
            var rimg = receipt.GetComponent<Image>();
            if (check != null) { rimg.sprite = check; rimg.type = Image.Type.Sliced; rimg.pixelsPerUnitMultiplier = 2f; }
            else rimg.color = new Color32(236, 231, 220, 255);
            var rcv = receipt.AddComponent<VerticalLayoutGroup>();
            rcv.padding = new RectOffset(28, 28, 26, 30);
            rcv.spacing = 8;
            rcv.childControlWidth = rcv.childControlHeight = true;
            rcv.childForceExpandHeight = false;
            receipt.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rReceiptTitle = Label(receipt.transform, "", 40, TextMain, TextAlignmentOptions.Center, false);
            rReceiptTitle.enableAutoSizing = true;
            rReceiptTitle.fontSizeMin = 24;
            rReceiptTitle.fontSizeMax = 40;
            rReceiptTitle.textWrappingMode = TextWrappingModes.NoWrap;
            var bar = new GameObject("Rule", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            bar.transform.SetParent(receipt.transform, false);
            bar.GetComponent<LayoutElement>().preferredHeight = 3;
            bar.GetComponent<Image>().color = new Color32(62, 58, 57, 255);
            rReceiptBody = Label(receipt.transform, "", 21, TextMain, TextAlignmentOptions.Left, false);
            rReceiptBody.lineSpacing = 10;
            var d = new GameObject("DashLine", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            d.transform.SetParent(receipt.transform, false);
            d.GetComponent<LayoutElement>().preferredHeight = dash != null ? 3 : 2;
            var di = d.GetComponent<Image>();
            if (dash != null) { di.sprite = dash; di.color = new Color(1, 1, 1, 0.5f); } else di.color = TextMuted;
            rSeries = Label(receipt.transform, "", 22, TextMain, TextAlignmentOptions.Left, false);
            if (receiptFont != null) { rReceiptTitle.font = receiptFont; rReceiptBody.font = receiptFont; rSeries.font = receiptFont; }
            foreach (var g in receipt.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            rReason = Label(right.transform, "", 19, TextMuted, TextAlignmentOptions.Center, false);
            rRematchHint = Label(right.transform, "", 19, Accent, TextAlignmentOptions.Center, false);
            rRematch = Btn(right.transform, "rematch", T("rematch"), true, () => plugin.Session.VoteRematch(!plugin.Session.LocalRematch));
            rRematchLabel = rRematch.GetComponentInChildren<TextMeshProUGUI>();
            // "back" keeps the id the test bot presses after each race.
            rChange = Btn(right.transform, "back", T("changeDevice"), false, () => { plugin.Session.VoteRematch(false); plugin.BackToLobby(); });
            resultWin.SetActive(false);
        }

        private void RefreshResultWindow()
        {
            var s = plugin.Session;
            var r = s.Result();
            string device = DeviceName(s.Local.DeviceId != "" ? s.Local.DeviceId : s.LobbyDevice);
            rHeading.text = T("raceHeading").Replace("{n}", s.Local.RaceNo.ToString(CultureInfo.InvariantCulture))
                + "  ·  " + device + "  ·  " + (s.RaceMode == RaceMode.Hardcore ? T("modeHardcore") : T("modeNormal"));

            FillBlock(rBlocks[0], plugin.RacerName, plugin.Avatar, s.Local, s, r.Decided && !r.Draw && r.LocalWins);
            bool remote = s.Remote != null;
            rBlocks[1].Name.transform.parent.gameObject.SetActive(remote);
            rBlocks[1].Values.gameObject.SetActive(remote);
            if (remote) FillBlock(rBlocks[1], string.IsNullOrEmpty(s.RemoteName) ? T("opponent") : s.RemoteName, s.RemoteAvatar, s.Remote, s, r.Decided && !r.Draw && !r.LocalWins);

            if (r.Decided)
            {
                rReceiptTitle.text = r.Draw ? T("draw") : r.LocalWins ? T("win") : T("lose");
                rReceiptTitle.color = r.Draw ? TextMain : r.LocalWins ? Green : Red;
            }
            else
            {
                rReceiptTitle.text = s.Connection == ConnectionState.Lost ? T("opponentLeft")
                    : s.Local.Phase == RacePhase.Abandoned ? T("abandoned") : T("waitingOpp");
                rReceiptTitle.color = TextMuted;
            }
            string you = Score(s.Local, s), them = remote ? Score(s.Remote, s) : "-";
            rReceiptBody.text = T("you") + "<pos=62%>" + you + "\n" + (remote ? (string.IsNullOrEmpty(s.RemoteName) ? T("opponent") : s.RemoteName) : T("opponent")) + "<pos=62%>" + them;
            rSeries.text = T("seriesScore") + "<pos=62%><b>" + s.SeriesWins + " : " + s.SeriesLosses + "</b>" + (s.SeriesDraws > 0 ? "  (" + s.SeriesDraws + ")" : "");
            rReason.text = Reason(r);

            bool canRematch = r.Decided && s.Connection == ConnectionState.Connected;
            rRematch.interactable = canRematch;
            rRematchLabel.text = s.LocalRematch ? T("rematchCancel") : T("rematch");
            rRematchHint.text = !canRematch ? ""
                : s.RematchAgreed ? T("rematchGo")
                : s.LocalRematch ? T("rematchWaiting")
                : s.RemoteRematch ? T("rematchWanted") : "";
            rRematchHint.gameObject.SetActive(rRematchHint.text != "");
        }

        private void FillBlock(ResultBlock b, string name, string avatar, RacerSnapshot snap, RaceSession s, bool winner)
        {
            SetAvatar(b.Avatar, avatar);
            b.Name.text = name;
            b.Badge.text = winner ? T("winnerBadge") : "";
            string time = snap.Phase == RacePhase.Finished ? Fmt(snap.ElapsedMs) : snap.Phase == RacePhase.Abandoned ? T("gaveUp") : T("stillGoing");
            var lines = new List<string>
            {
                T("timeLabel") + "<pos=70%>" + time,
                T("partsLabel") + "<pos=70%>" + (snap.Phase == RacePhase.Finished ? snap.PartsTotal : snap.PartsPlaced) + " / " + snap.PartsTotal,
            };
            if (s.RaceMode == RaceMode.Hardcore)
            {
                lines.Add(T("missesLabel") + "<pos=70%>" + snap.Resets);
                lines.Add(T("penaltyLabel") + "<pos=70%>+" + (snap.Resets * s.PenaltyPerResetMs / 1000) + " " + T("secondsShort"));
            }
            lines.Add("<b>" + T("totalLabel") + "<pos=70%>" + (snap.Phase == RacePhase.Finished ? Fmt(s.ScoreMs(snap)) : "-") + "</b>");
            b.Values.text = string.Join("\n", lines.ToArray());
        }

        private string Score(RacerSnapshot snap, RaceSession s) =>
            snap.Phase == RacePhase.Finished ? Fmt(s.ScoreMs(snap)) : snap.Phase == RacePhase.Abandoned ? T("gaveUp") : "…";
    }
}
