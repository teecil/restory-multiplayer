using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Data.Furniture;
using Restory.Data.SaveLoad.Containers;
using Restory.Gameplay.Furniture;
using Restory.Gameplay.UserInterface;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // The shop's furniture and wall colour, shared. The game keeps them in StoreFurniture (which piece stands in
    // each slot, and the walls' colour) and saves them as one record; that record is compared once a second, and
    // when a player's change shows up in it (a new piece dragged onto a slot, the walls painted) it goes to the other
    // side, which restores it the way loading a save does. The host is the referee: it applies the guest's record and
    // sends its own back, so when both change something at the same moment both end up with what the host has.
    // Nothing is compared while a piece is in hand or the colour dialogue is open (the game previews choices on the
    // furniture itself then).
    internal static class CoopFurniture
    {
        private static CoopSession session;
        private static FurnitureService service;
        private static StoreFurniture lastStore;
        private static string lastSent;
        private static string waiting;
        private static float nextTick;

        public static int Sent { get; private set; }
        public static int Applied { get; private set; }

        private static StoreFurniture Store => SceneCache.Find<StoreFurniture>(true);

        public static void Attach(CoopSession s)
        {
            session = s;
            lastStore = null;
            lastSent = null;
            waiting = null;
            if (s != null) s.OnAction += OnAction;
        }

        private static bool Ready => session != null && session.Playing && CoopGame.InShopFor(3f) && CoopDevices.Serializer != null && Store != null;

        private static bool Busy
        {
            get
            {
                if (CoopHeld.LocalDragging) return true;
                if (service != null && service.ActivatedFurniture.Count > 0) return true;
                var dialogue = SceneCache.Find<GUI_ColorSelectionDialogue>(true);
                return dialogue != null && dialogue.gameObject.activeInHierarchy;
            }
        }

        private static string StateJson(StoreFurniture store) => CoopDevices.Serializer.ToJson(store.CaptureState());

        public static string Hash
        {
            get
            {
                if (!Ready) return "";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(StateJson(Store)))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
        }

        public static void Tick()
        {
            if (!Ready || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            if (Busy) return;
            try
            {
                var store = Store;
                var json = StateJson(store);
                if (store != lastStore)
                {
                    // A shop just loaded (the start, a new day): both load the same save, so the guest takes what it
                    // has as agreed; the host sends its own, which the guest applies if it differs.
                    lastStore = store;
                    if (!session.IsHost) { lastSent = json; return; }
                }
                if (waiting != null)
                {
                    var theirs = waiting;
                    waiting = null;
                    if (theirs != json)
                    {
                        store.RestoreState(CoopDevices.Serializer.FromJsonUnsafe<StorageFurnitureSaveData>(theirs));
                        store.PostRestore();
                        Applied++;
                        Plugin.Log.LogInfo("co-op furniture: the other player's furniture applied");
                        json = StateJson(store);
                    }
                    // The guest takes the host's record as agreed; the host sends the result back below.
                    if (!session.IsHost) { lastSent = json; return; }
                }
                if (json == lastSent) return;
                lastSent = json;
                Sent++;
                session.SendAction("furn", Encoding.UTF8.GetBytes(json));
                Plugin.Log.LogInfo("co-op furniture: furniture changed here, sent");
            }
            catch (Exception e) { Plugin.Log.LogError("co-op furniture: " + e); nextTick = Time.unscaledTime + 5f; }
        }

        private static void OnAction(string name, byte[] data)
        {
            if (name != "furn") return;
            waiting = Encoding.UTF8.GetString(data);
        }

        [HarmonyPatch]
        private static class ServiceCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.FirstConstructor(typeof(FurnitureService), c => c.GetParameters().Length > 0);
            private static void Postfix(FurnitureService __instance) => service = __instance;
        }

        // ----- test bots -----

        // Host: another piece of the same kind in one slot. Guest: the walls in another colour.
        public static string BotChange(bool host)
        {
            var store = Store;
            if (store == null) return "no furniture here";
            var infos = Resources.FindObjectsOfTypeAll<FurnitureInfo>();
            if (host)
            {
                foreach (var f in store.AllFurniture.Values.OrderBy(f => f.UniqueId, StringComparer.Ordinal))
                {
                    var other = infos.Where(i => i != null && !(i is FurnitureColorInfo) && i.Category == f.Category && i != f.Info)
                        .OrderBy(i => i.ID, StringComparer.Ordinal).FirstOrDefault();
                    if (other == null) continue;
                    var was = f.Info != null ? f.Info.ID : "none";
                    f.RestoreFurniture(other);
                    return f.UniqueId + ": " + was + " -> " + other.ID;
                }
                return "no slot with another piece to put in";
            }
            var walls = SceneCache.Find<StoreWalls>(true);
            if (walls == null) return "no walls";
            var colour = infos.OfType<FurnitureColorInfo>().Where(c => c != walls.ColorInfo).OrderBy(c => c.ID, StringComparer.Ordinal).FirstOrDefault();
            if (colour == null) return "no other colour";
            var before = walls.ColorInfo != null ? walls.ColorInfo.ID : "none";
            walls.ApplyColor(colour);
            return "walls: " + before + " -> " + colour.ID;
        }
    }
}
