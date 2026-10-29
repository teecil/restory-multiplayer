using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Data.Devices.Condition;
using Restory.Data.SaveLoad.Containers;
using Restory.Gameplay.Delivery;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Inventory;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.Shops.Devices;
using Restory.StorageSystem.StorageElements;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Courier boxes for both players. The courier only comes to the host (the guest has no visits), so the host's
    // DeliveryService is the truth: its state (what is on order, and the box at the delivery spot with everything in
    // it) goes to the guest whenever it changes, and the guest rebuilds the same box the way loading a save does.
    // Taking something out of a box is reported as what was taken ("boxtake": parts, palettes, sticker packs,
    // stickers, a parts box, or one kind of object), and the other side strikes the same thing from its copy, so
    // nothing comes out twice. Until the host has applied the guest's latest take, the guest ignores older host
    // states. Parts go into the shared stock (CoopStock); objects taken out reach the other side when they are put
    // down in the room (CoopRoom creates them from their data). A box moved off the delivery spot becomes an
    // ordinary room object on both sides.
    internal static class CoopDelivery
    {
        private static readonly AccessTools.FieldRef<DeliveryService, ElementsAndPalettesAndInteractiveObjectsBoxContainer> BoxField =
            AccessTools.FieldRefAccess<DeliveryService, ElementsAndPalettesAndInteractiveObjectsBoxContainer>("deliveryBox");
        private static readonly System.Reflection.MethodInfo BoxMovedOff = AccessTools.Method(typeof(DeliveryService), "ResolveDeliveryBoxDragComplete");
        private static readonly AccessTools.FieldRef<InteractiveObjectBoxContainer, InteractiveObject> TakenField =
            AccessTools.FieldRefAccess<InteractiveObjectBoxContainer, InteractiveObject>("takenObject");
        private static readonly AccessTools.FieldRef<InteractiveObjectBoxContainer, IInteractiveObjectInfo> TakenInfoField =
            AccessTools.FieldRefAccess<InteractiveObjectBoxContainer, IInteractiveObjectInfo>("takenObjectInfo");
        private static readonly AccessTools.FieldRef<InteractiveObjectBoxContainer, List<ContainedInteractiveObject>> ContentField =
            AccessTools.FieldRefAccess<InteractiveObjectBoxContainer, List<ContainedInteractiveObject>>("content");
        private static readonly AccessTools.FieldRef<ElementsAndPalettesAndInteractiveObjectsBoxContainer, List<ElementsBoxData>> ElementsBoxesField =
            AccessTools.FieldRefAccess<ElementsAndPalettesAndInteractiveObjectsBoxContainer, List<ElementsBoxData>>("containedElementsBoxes");

        private static CoopSession session;
        private static float nextTick;
        private static string lastSentJson;
        private static int sentAck = -1;
        private static int takeSeq;          // this side's takes, numbered
        private static int appliedGuestTake; // host: last guest take applied
        // Shared with CoopServiceBoxes: one numbering of takes for every box.
        internal static int TakeSeq => takeSeq;
        internal static int AppliedGuestTake => appliedGuestTake;
        private static byte[] waitingState;  // guest: the host's latest state, to apply when the box is not in use
        private static bool applying;

        public static int StatesSent { get; private set; }
        public static int StatesApplied { get; private set; }
        public static int TakesSent { get; private set; }
        public static int TakesApplied { get; private set; }
        public static bool BotDone { get; private set; }

        private static DeliveryService Delivery => SceneCache.Find<DeliveryService>(true);

        public static void Attach(CoopSession s)
        {
            session = s;
            lastSentJson = null;
            sentAck = -1;
            waitingState = null;
            if (s == null) return;
            s.OnAction += OnAction;
        }

        private static bool Ready => session != null && session.Playing && CoopGame.InShopFor(3f) && CoopDevices.Serializer != null && Delivery != null;

        private static string StateJson(DeliveryService d) => CoopDevices.Serializer.ToJson(d.CaptureState());

        // A short fingerprint of the delivery state, for the test to compare the two sides.
        public static string Hash
        {
            get
            {
                if (!Ready) return "";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(StateJson(Delivery)))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
        }

        public static void Tick()
        {
            if (!Ready || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            try
            {
                var d = Delivery;
                if (session.IsHost)
                {
                    var json = StateJson(d);
                    if (json == lastSentJson && sentAck == appliedGuestTake) return;
                    lastSentJson = json;
                    sentAck = appliedGuestTake;
                    StatesSent++;
                    var packed = Pack(json);
                    int ack = appliedGuestTake;
                    session.SendAction("deliv", Packet.Build(w => { w.Write(ack); w.Write(packed.Length); w.Write(packed); }));
                    return;
                }
                if (waitingState != null) ApplyState(d);
            }
            catch (Exception e) { Plugin.Log.LogError("co-op delivery: " + e); nextTick = Time.unscaledTime + 5f; }
        }

        private static void OnAction(string name, byte[] data)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
                {
                    if (name == "deliv" && !session.IsHost)
                    {
                        int ack = r.ReadInt32();
                        int len = r.ReadInt32();
                        var packed = r.ReadBytes(len);
                        if (ack < takeSeq) return; // our latest take is not in it yet
                        waitingState = packed;
                    }
                    else if (name == "boxtake")
                    {
                        int seq = r.ReadInt32();
                        string boxId = r.ReadString(), kind = r.ReadString(), infoId = r.ReadString();
                        int amount = r.ReadInt32();
                        ApplyTake(boxId, kind, infoId, amount);
                        if (session.IsHost) appliedGuestTake = Math.Max(appliedGuestTake, seq);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("co-op delivery: " + name + ": " + e); }
        }

        // Guest: the host's delivery state, unless the guest's box is in use right now (something in hand).
        private static void ApplyState(DeliveryService d)
        {
            var box = BoxField(d);
            if (box != null && (TakenField(box) != null || box.InteractiveObject.DragState != InteractiveObjectDragState.None)) return;
            var json = Unpack(waitingState);
            waitingState = null;
            if (json == StateJson(d)) return;
            var saved = CoopDevices.Serializer.FromJsonUnsafe<DeliveryServiceSaveData>(json);
            // The host still has at the delivery spot a box that is a room object here (moved off just now): an older
            // state, the next one will not have it.
            if (saved.DeliveryBoxData != null && CoopRoom.Find(saved.DeliveryBoxData.UniqueId) != null) return;
            applying = true;
            try
            {
                if (box != null)
                {
                    // The old box goes; RestoreState builds the host's (same id, same content).
                    BoxField(d) = null;
                    var factory = CoopRoom.Factory;
                    if (factory != null) factory.DestroyInteractiveObject(box.InteractiveObject);
                    else UnityEngine.Object.Destroy(box.gameObject);
                }
                d.RestoreState(saved);
                StatesApplied++;
                Plugin.Log.LogInfo("co-op delivery: the host's delivery state applied (box " + (BoxField(d) != null ? "at the delivery spot" : "none") + ")");
            }
            finally { applying = false; }
        }

        private static byte[] Pack(string json)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                {
                    var b = Encoding.UTF8.GetBytes(json);
                    gz.Write(b, 0, b.Length);
                }
                return ms.ToArray();
            }
        }

        private static string Unpack(byte[] packed)
        {
            using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // ----- taking things out of a box -----

        // Any box: the courier's, the customers' box, the personal box.
        private static InteractiveObjectBoxContainer FindBox(string id)
        {
            var d = Delivery;
            var spot = d != null ? BoxField(d) : null;
            if (spot != null && spot.InteractiveObject != null && spot.InteractiveObject.UniqueId == id) return spot;
            return UnityEngine.Object.FindObjectsByType<InteractiveObjectBoxContainer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(b => b.InteractiveObject != null && b.InteractiveObject.UniqueId == id);
        }

        [HarmonyPatch(typeof(ElementsAndPalettesAndInteractiveObjectsBoxContainer), "RemoveTakenObjectContentsFromTheBox")]
        private static class TakeReport
        {
            private static void Prefix(ElementsAndPalettesAndInteractiveObjectsBoxContainer __instance) => ReportTake(__instance);
        }

        // Plain boxes (customers' devices, the personal box). The courier's box calls this too for objects (its
        // override calls the base); that one is reported by TakeReport above.
        [HarmonyPatch(typeof(InteractiveObjectBoxContainer), "RemoveTakenObjectContentsFromTheBox")]
        private static class PlainTakeReport
        {
            private static void Prefix(InteractiveObjectBoxContainer __instance)
            {
                if (__instance is ElementsAndPalettesAndInteractiveObjectsBoxContainer) return;
                ReportTake(__instance);
            }
        }

        private static void ReportTake(InteractiveObjectBoxContainer __instance)
        {
            {
                if (applying || session == null || !session.Playing) return;
                var taken = TakenField(__instance);
                if (taken == null || __instance.InteractiveObject == null) return;
                string kind, infoId = "";
                int amount = 1;
                if (taken.TryGetComponent<Restory.Gameplay.Elements.ElementsContainer>(out _)) kind = "elements";
                else if (taken.TryGetComponent<Restory.Gameplay.Equipment.DevicePaintingTools.PaintingPalettesContainer>(out _)) kind = "palettes";
                else if (taken.TryGetComponent<Restory.Gameplay.Equipment.Stickers.StickerPacksContainer>(out _)) kind = "stickerpacks";
                else if (taken.TryGetComponent<Restory.Gameplay.Equipment.Stickers.StickersCollectionContainer>(out _)) kind = "stickers";
                else if (taken.TryGetComponent<ElementsBox>(out _)) { kind = "elementsbox"; infoId = TakenInfoField(__instance)?.ID ?? ""; }
                else
                {
                    kind = "object";
                    infoId = TakenInfoField(__instance)?.ID ?? "";
                    if (taken.TryGetComponent<PersonalConsumableTool>(out var tool)) amount = Math.Max(1, tool.Amount);
                }
                int seq = ++takeSeq;
                string boxId = __instance.InteractiveObject.UniqueId;
                TakesSent++;
                session.SendAction("boxtake", Packet.Build(w => { w.Write(seq); w.Write(boxId); w.Write(kind); w.Write(infoId); w.Write(amount); }));
                Plugin.Log.LogInfo("co-op delivery: took " + kind + (infoId != "" ? " " + infoId : "") + " out of box " + boxId);
            }
        }

        private static void ApplyTake(string boxId, string kind, string infoId, int amount)
        {
            var box = FindBox(boxId);
            if (box == null) { Plugin.Log.LogWarning("co-op delivery: box " + boxId + " not here"); return; }
            applying = true;
            var rich = box as ElementsAndPalettesAndInteractiveObjectsBoxContainer;
            try
            {
                switch (kind)
                {
                    case "elements": rich?.ClearElements(); break;
                    case "palettes": rich?.ClearPalettes(); break;
                    case "stickerpacks": rich?.ClearStickerPacks(); break;
                    case "stickers": rich?.ClearSeparateStickers(); break;
                    case "elementsbox":
                    {
                        if (rich == null) break;
                        var list = ElementsBoxesField(rich);
                        int i = list.FindIndex(b => b != null && b.Info != null && b.Info.ID == infoId);
                        if (i >= 0) list.RemoveAt(i);
                        break;
                    }
                    case "moved": Adopt(boxId); break;
                    default:
                    {
                        var content = ContentField(box);
                        for (int n = 0; n < amount; n++)
                        {
                            int i = content.FindIndex(c => c != null && c.InteractiveObjectInfo != null && c.InteractiveObjectInfo.ID == infoId);
                            if (i < 0) break;
                            content.RemoveAt(i);
                        }
                        break;
                    }
                }
                TakesApplied++;
                Plugin.Log.LogInfo("co-op delivery: the other player took " + kind + (infoId != "" ? " " + infoId : "") + " out of box " + boxId);
            }
            finally { applying = false; }
        }

        // A box moved off the delivery spot on the other side: the copy here becomes a room object too.
        public static InteractiveObject Adopt(string id)
        {
            var d = Delivery;
            var box = d != null ? BoxField(d) : null;
            if (box == null || box.InteractiveObject == null || box.InteractiveObject.UniqueId != id) return null;
            // What the game does when the box is dragged off the spot: unsubscribe, register it as a room object.
            applying = true;
            try { BoxMovedOff.Invoke(d, null); }
            finally { applying = false; }
            Plugin.Log.LogInfo("co-op delivery: the courier box " + id + " is a room object now");
            return box.InteractiveObject;
        }

        // The courier box dragged off the delivery spot on this side. Numbered like a take, so the guest ignores host
        // states that still have the box at the spot until the host has adopted it too.
        [HarmonyPatch(typeof(DeliveryService), "ResolveDeliveryBoxDragComplete")]
        private static class MovedReport
        {
            private static void Prefix(DeliveryService __instance)
            {
                if (applying || session == null || !session.Playing) return;
                var box = BoxField(__instance);
                if (box == null || box.InteractiveObject == null) return;
                int seq = ++takeSeq;
                string boxId = box.InteractiveObject.UniqueId;
                TakesSent++;
                session.SendAction("boxtake", Packet.Build(w => { w.Write(seq); w.Write(boxId); w.Write("moved"); w.Write(""); w.Write(0); }));
                Plugin.Log.LogInfo("co-op delivery: courier box " + boxId + " moved off the delivery spot");
            }
        }

        // ----- test bots -----

        public static InteractiveObject BotBox()
        {
            var d = Delivery;
            var box = d != null ? BoxField(d) : null;
            return box != null ? box.InteractiveObject : null;
        }

        // Host: the courier comes now with three copies of a part from the stock.
        public static string BotDeliver()
        {
            var d = Delivery;
            var inv = UnityEngine.Object.FindObjectsByType<Inventory>(FindObjectsSortMode.None).FirstOrDefault(i => i.StorageElements.Size > 0);
            if (d == null || inv == null) return "no delivery service or no part to model on";
            var item = inv.StorageElements[0].Item as StorageItemElement;
            var copy = CoopDevices.Serializer.FromJsonUnsafe<StorageItemElement>(CoopDevices.Serializer.ToJson(item));
            d.SendToDelivery(copy.ElementData, 3);
            d.ForceDeliverEverything();
            var box = BoxField(d);
            return box != null ? "box " + box.InteractiveObject.UniqueId + " at the delivery spot with " + box.ContainedElements.Sum(e => e.HeldAmount) + " parts" : "no box";
        }

        // Guest: take the parts out of the courier box into the stock (what dragging them onto the inventory does).
        public static string BotTakeParts()
        {
            var d = Delivery;
            var box = d != null ? BoxField(d) : null;
            if (box == null) { BotDone = true; return "no courier box here"; }
            var taken = box.GetContainedObject();
            if (taken == null || !taken.TryGetComponent<Restory.Gameplay.Elements.ElementsContainer>(out var parts)) { BotDone = true; return "no parts in the box"; }
            var inv = UnityEngine.Object.FindObjectsByType<Inventory>(FindObjectsSortMode.None).FirstOrDefault();
            int n = 0;
            foreach (var held in parts.HeldElements) { inv.StorageElements.AddItem(new StorageItemElement(held.ElementData), held.HeldAmount); n += held.HeldAmount; }
            taken.CompleteDrag();
            BotDone = true;
            return n + " parts from box " + box.InteractiveObject.UniqueId + " into the stock";
        }
    }
}
