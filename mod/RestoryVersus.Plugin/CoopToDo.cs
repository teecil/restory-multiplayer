using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Data.ToDoList;
using Restory.Gameplay.ToDoList;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // The to-do list (the game's quests) for both. Each item is closed by an event in the game where it happened, so
    // a quest the guest finished closed only in the guest's game and never reached the host's save. Now:
    //   an item completed on either side is completed on the other ("todo:done", the item's ID);
    //   items come and go on the host (its customers' dialogues and scripts): it tells the guest ("todo:add",
    //   "todo:remove"), so both see the same list during the day.
    internal static class CoopToDo
    {
        private static CoopSession session;
        private static bool applying;

        public static int CompletionsSent { get; private set; }
        public static int CompletionsApplied { get; private set; }

        private static ToDoListService List => SceneCache.Find<ToDoListService>(true);

        public static void Attach(CoopSession s)
        {
            session = s;
            if (s != null) s.OnAction += OnAction;
        }

        private static bool Linked => session != null && session.Playing && CoopGame.InShopFor(2f);

        private static ToDoItem Asset(string id) =>
            Resources.FindObjectsOfTypeAll<ToDoItem>().FirstOrDefault(i => i != null && i.ID == id);

        private static void OnAction(string name, byte[] data)
        {
            if (!name.StartsWith("todo:")) return;
            var list = List;
            if (list == null || !Linked) return;
            string id = Encoding.UTF8.GetString(data);
            applying = true;
            try
            {
                if (name == "todo:done")
                {
                    var item = list.Items.FirstOrDefault(i => i != null && i.ID == id);
                    if (item == null) { Plugin.Log.LogWarning("co-op to-do: the other player completed " + id + ", not in this list"); return; }
                    if (list.IsCompleted(item)) return;
                    list.CompleteItem(item);
                    CompletionsApplied++;
                    Plugin.Log.LogInfo("co-op to-do: the other player completed " + id);
                }
                else if (name == "todo:add" && !session.IsHost)
                {
                    var item = Asset(id);
                    if (item != null) { list.AddItem(item); Plugin.Log.LogInfo("co-op to-do: the host gave the quest " + id); }
                }
                else if (name == "todo:remove" && !session.IsHost)
                {
                    var item = list.Items.FirstOrDefault(i => i != null && i.ID == id);
                    if (item != null) list.RemoveItem(item);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("co-op to-do: " + name + ": " + e); }
            finally { applying = false; }
        }

        private static void Send(string what, ToDoItem item)
        {
            if (applying || item == null || !Linked) return;
            session.SendAction(what, Encoding.UTF8.GetBytes(item.ID));
        }

        [HarmonyPatch(typeof(ToDoListService), nameof(ToDoListService.CompleteItem))]
        private static class CompletePatch
        {
            private static void Prefix(ToDoListService __instance, ToDoItem item, out bool __state) => __state = item != null && __instance.IsCompleted(item);

            private static void Postfix(ToDoListService __instance, ToDoItem item, bool __state)
            {
                if (__state || item == null || !__instance.IsCompleted(item) || applying || !Linked) return;
                CompletionsSent++;
                Plugin.Log.LogInfo("co-op to-do: completed here: " + item.ID);
                Send("todo:done", item);
            }
        }

        // Before the game adds it: adding runs the item's check, which can complete it at once, and the other side
        // has to have the item before it hears that it is completed.
        [HarmonyPatch(typeof(ToDoListService), nameof(ToDoListService.AddItem))]
        private static class AddPatch
        {
            private static void Prefix(ToDoListService __instance, ToDoItem item)
            {
                if (session != null && session.IsHost && item != null && !__instance.Items.Contains(item)) Send("todo:add", item);
            }
        }

        [HarmonyPatch(typeof(ToDoListService), nameof(ToDoListService.RemoveItem))]
        private static class RemovePatch
        {
            private static void Postfix(ToDoItem item) { if (session != null && session.IsHost) Send("todo:remove", item); }
        }

        // Test bot (host): a quest the story would give (one of the game's to-do items not in the list yet).
        public static string BotAddOne()
        {
            var list = List;
            if (list == null) return "no to-do list";
            // One that stays open: some complete at once when their condition already holds (BuyPainter did).
            var candidates = Resources.FindObjectsOfTypeAll<ToDoItem>()
                .Where(i => i != null && !string.IsNullOrEmpty(i.ID) && !list.Items.Contains(i) && !list.CompletedItems.Contains(i))
                .OrderBy(i => i.ID, StringComparer.Ordinal).ToList();
            foreach (var item in candidates.Take(12))
            {
                list.AddItem(item);
                if (!list.IsCompleted(item)) return "added " + item.ID;
            }
            return "no to-do item that stays open (" + candidates.Count + " tried at most 12)";
        }

        // Test bot (guest): complete the first open item, as finishing that quest would.
        public static string BotCompleteOne()
        {
            var list = List;
            if (list == null) return "no to-do list";
            var item = list.Items.FirstOrDefault(i => i != null && !list.IsCompleted(i));
            if (item == null) return "no open item in the list";
            list.CompleteItem(item);
            return "completed " + item.ID;
        }
    }
}
