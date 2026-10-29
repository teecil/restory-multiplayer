using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Data.Devices.Condition;
using Restory.Data.SaveLoad.Containers;
using Restory.Data.SaveLoad.FullSerializerWrappers;
using Restory.Gameplay.Devices;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.Licenses;
using Restory.Gameplay.Shipment;
using Restory.Gameplay.Storages;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Co-op step 4: devices (boxes). They are not in the room-object registry; the game keeps them in DeviceRegistry
    // and saves each with its parts (DeviceSaveData). Owner's rules: a box moved around the room is seen by both; a
    // box on a player's own bench is gone from the room for the other player, its insides private; when it comes
    // back to the room (a shelf, the delivery spot, the counter) the other side gets it as it is now.
    //   moves of a device both have      -> same as room objects (where it sits), plus its data
    //   put on the own bench             -> Hidden on the other side
    //   back in the room / at the counter -> the other side rebuilds it from the data, the way loading a save does
    //   new on the host (a customer brought it) / gone on the host (shipped away, sold) -> same messages, from the
    //                                       host's DeviceRegistry Register/Unregister
    // Guest drops are requests (CoopSession), so a device the guest puts on the counter is rebuilt on the host,
    // where the host's own order logic sees it, sends the customer and pays into the host's save.
    internal static class CoopDevices
    {
        private static CoopSession session;
        private static DeviceRegistry registry;
        private static ShipmentService shipment;
        private static GameEntityFullSerializer serializer;
        internal static GameEntityFullSerializer Serializer => serializer;
        private static bool applying;
        private static readonly HashSet<string> dirty = new HashSet<string>();
        private static readonly Dictionary<string, DeviceContainer> lastKnown = new Dictionary<string, DeviceContainer>();

        public static int Reported { get; private set; }
        public static int Applied { get; private set; }
        public static int Failed { get; private set; }
        public static int HiddenApplied { get; private set; }
        // The device the test bot works on: the host's freshly spawned box, the guest's copy of it.
        public static string BotId { get; private set; } = "";
        // Messages that arrived while this copy was still loading the shop: applied once it is in.
        private static readonly List<ObjState> early = new List<ObjState>();

        // In the shop with everything the rebuild needs. Before that both copies hold the same loaded save,
        // so nothing has to be reported, and what arrives waits.
        public static bool Ready => Active && registry != null && serializer != null && Devices != null
            && Lobby.Global != null && Lobby.Global.IsInGameLoop && CoopGame.InShopFor(3f);

        public static void Attach(CoopSession s)
        {
            session = s;
            dirty.Clear();
            early.Clear();
            RemoteHidden.Clear();
            if (s != null) s.OnApplyObject += o => { if (o.Kind == ObjState.KindDevice) { if (Ready) Apply(o); else early.Add(o); } };
        }

        private static bool Active => session != null && session.Playing;
        private static DeviceService Devices => SceneCache.Find<DeviceService>(true);

        public static IEnumerable<DeviceContainer> All => registry != null ? registry.All : Enumerable.Empty<DeviceContainer>();

        public static DeviceContainer Find(string id) => All.FirstOrDefault(d => d != null && d.UniqueId == id);

        // The object that sits in the room for a device: its pack (box) if packed, else the device itself.
        public static Transform Top(DeviceContainer d)
        {
            var pack = d.GetComponentInParent<DevicePack>(true);
            return pack != null ? pack.transform : d.transform;
        }

        private static InteractiveObjectState TopState(DeviceContainer d)
        {
            var pack = d.GetComponentInParent<DevicePack>(true);
            return pack != null ? pack.State : d.State;
        }

        // A player's drop involved a device (CoopRoom calls this): report it on the next tick, once settled.
        public static void Dropped(InteractiveObject obj)
        {
            var d = obj as DeviceContainer ?? (obj as DevicePack)?.DeviceContainer;
            if (d != null && !string.IsNullOrEmpty(d.UniqueId)) { dirty.Add(d.UniqueId); lastKnown[d.UniqueId] = d; }
        }

        public static void Tick()
        {
            // Out of the shop (a new day loading): both load the host's save, so nothing is on the guest's bench any more.
            if (!Ready || applying) { if (!Ready) { lastSig.Clear(); RemoteHidden.Clear(); } return; }
            KeepGuestBenchHidden();
            WatchForChanges();
            if (early.Count > 0)
            {
                var waiting = early.ToList();
                early.Clear();
                Plugin.Log.LogInfo("co-op devices: applying " + waiting.Count + " change(s) that arrived while loading");
                foreach (var o in waiting) Apply(o);
            }
            if (dirty.Count == 0) return;
            var ids = dirty.ToList();
            dirty.Clear();
            foreach (var id in ids)
            {
                // A texture of this device is still being encoded (a stroke just ended): next tick.
                var dev = Find(id);
                if (dev != null && CoopTextures.Busy(TextureIds(dev))) { dirty.Add(id); continue; }
                try { Report(id); }
                catch (Exception e) { Failed++; Plugin.Log.LogError("co-op devices: report of " + id + " failed: " + e); }
            }
        }

        // Where each device was last seen here (its room-list signature). A device also moves without a drop the mod
        // sees: the game puts it on the bench by a button or by unpacking a box (seen in the owner's game: the guest's
        // device stayed on the host's desk, and the host could carry it off). Twice a second every device is compared
        // with where it was, and a change made here is reported like a drop. Changes applied from the other side
        // update this first, so they are not sent back.
        private static readonly Dictionary<string, string> lastSig = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> appliedAt = new Dictionary<string, float>();
        private static float nextWatch;

        // The game's own bench actions: reported at once (the watcher above is the fallback).
        [HarmonyPatch(typeof(DeviceService), nameof(DeviceService.PlaceDeviceContainer), new[] { typeof(DeviceContainer) })]
        private static class PlacePatch
        {
            private static void Postfix(DeviceContainer deviceContainer) { if (!applying && Ready) Dropped(deviceContainer); }
        }

        [HarmonyPatch(typeof(DeviceService), nameof(DeviceService.PlaceDeviceContainer), new[] { typeof(DevicePack) })]
        private static class PlacePackPatch
        {
            private static void Postfix(DevicePack devicePack) { if (!applying && Ready && devicePack != null) Dropped(devicePack.DeviceContainer); }
        }

        [HarmonyPatch(typeof(DeviceService), nameof(DeviceService.GrabPlacedDeviceContainer))]
        private static class GrabPatch
        {
            private static void Prefix(DeviceService __instance, out DeviceContainer __state) => __state = __instance.PlacedDeviceContainer;
            // Taken off the bench into the hand: the drop that follows reports where it went.
            private static void Postfix(DeviceContainer __state) { if (__state != null && !string.IsNullOrEmpty(__state.UniqueId)) lastSig.Remove(__state.UniqueId); }
        }

        private static void WatchForChanges()
        {
            if (Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + 0.5f;
            foreach (var d in All)
            {
                if (d == null || string.IsNullOrEmpty(d.UniqueId)) continue;
                var top = Top(d).GetComponent<InteractiveObject>();
                if (top != null && top.DragState != InteractiveObjectDragState.None) continue; // the drop reports it
                string sig = Sig(d);
                if (!lastSig.TryGetValue(d.UniqueId, out var was)) { lastSig[d.UniqueId] = sig; continue; }
                if (was == sig) continue;
                lastSig[d.UniqueId] = sig;
                if (session.IsHost && RemoteHidden.Contains(d.UniqueId)) continue;
                if (sig == "h") continue; // hidden here because of the other player: theirs to report
                if (appliedAt.TryGetValue(d.UniqueId, out var at) && Time.unscaledTime - at < 1.5f) continue; // settling after a remote change
                Plugin.Log.LogInfo("co-op devices: " + d.Device.Info.ID + " moved here without a drop (" + (sig == "b" ? "onto the bench" : sig) + ")");
                dirty.Add(d.UniqueId);
            }
        }

        // ----- for the room list (CoopCensus) -----

        // Host: devices the guest has on its bench.
        internal static readonly HashSet<string> RemoteHidden = new HashSet<string>();

        internal static InteractiveObjectState StateOf(DeviceContainer d) => TopState(d);

        // The bench of this player's own (what the player is working on): placed and showing.
        // The game knows what is on this player's bench: DeviceService.PlacedDeviceContainer. Not the box's state: after
        // unpacking on the bench the box object can stay a parent with its old "stored" state, and the device was
        // then reported as lying on the desk (the owner's game, 0.11.2).
        internal static bool OnOwnBench(DeviceContainer d)
        {
            var devices = Devices;
            return devices != null && d != null && ReferenceEquals(devices.PlacedDeviceContainer, d);
        }

        // "b" on this player's bench, "s" at the counter, "h" not showing here (the other player's bench), else where.
        // For the room list: a bench is a bench, whose it is the list says otherwise ("g").
        internal static string ListSig(DeviceContainer d) { var s = Sig(d); return s == "b" ? "h" : s; }

        internal static string Sig(DeviceContainer d)
        {
            if (OnOwnBench(d)) return "b";
            var state = TopState(d);
            var top = Top(d);
            if (state == InteractiveObjectState.Shipment) return "s";
            if (state == InteractiveObjectState.Placed || !top.gameObject.activeInHierarchy || top.parent == null) return "h";
            var p = top.localPosition;
            return ((int)state).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + CoopRoom.PathOf(top.parent) + "|"
                + string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", p.x, p.y, p.z);
        }

        internal static void ForceReport(string id) => dirty.Add(id);

        internal static void RemoveLocal(DeviceContainer d)
        {
            applying = true;
            try { Destroy(Devices, d); }
            finally { applying = false; }
        }

        // Host: what the guest has on its own bench stays out of this room, whatever else touches it (a unpacked
        // box, a registry change): the guest's moves alone bring it back (Apply).
        private static void KeepGuestBenchHidden()
        {
            if (session == null || !session.IsHost || RemoteHidden.Count == 0) return;
            foreach (var id in RemoteHidden)
            {
                var d = Find(id);
                if (d == null) continue;
                var top = Top(d);
                if (top.gameObject.activeSelf) top.gameObject.SetActive(false);
                if (d.gameObject.activeSelf && d.gameObject != top.gameObject) d.gameObject.SetActive(false);
            }
        }

        private static void Report(string id)
        {
            var d = Find(id);
            // Host: a device the guest holds on its bench is not ours to report or move.
            if (session != null && session.IsHost && RemoteHidden.Contains(id)) return;
            var s = new ObjState { Id = id, Kind = ObjState.KindDevice };
            if (d == null) s.Gone = true;
            else
            {
                var state = TopState(d);
                s.State = (byte)state;
                if (OnOwnBench(d) || state == InteractiveObjectState.Placed) s.Hidden = true; // on this player's own bench
                else
                {
                    s.Data = Pack(d, out string json);
                    CoopTextures.SendFor(CoopTextures.Referenced(json)); // before the data, on the same ordered channel
                    var top = Top(d);
                    if (state != InteractiveObjectState.Shipment && top.parent != null)
                    {
                        s.Parent = CoopRoom.PathOf(top.parent);
                        var p = top.localPosition;
                        var q = top.localRotation;
                        s.X = p.x; s.Y = p.y; s.Z = p.z;
                        s.Qx = q.x; s.Qy = q.y; s.Qz = q.z; s.Qw = q.w;
                    }
                }
            }
            Reported++;
            if (d != null) lastSig[id] = Sig(d); else lastSig.Remove(id);
            Plugin.Log.LogInfo("co-op devices: " + Describe(d, s) + " -> sent (" + s.Data.Length + " bytes of data)");
            session.ReportLocalMove(s);
        }

        private static string Describe(DeviceContainer d, ObjState s) =>
            (d != null ? d.Device.Info.ID : s.Id) + (s.Gone ? " gone" : s.Hidden ? " on a bench" : " " + (InteractiveObjectState)s.State + (s.Parent != "" ? " on " + s.Parent : ""));

        // ----- the device as data -----

        private static IEnumerable<int> TextureIds(DeviceContainer d)
        {
            var ids = new List<int>();
            if (d.CachedInstalledElements != null)
                foreach (var e in d.CachedInstalledElements) if (e != null && e.DirtMaskTextureId > 0) ids.Add(e.DirtMaskTextureId);
            if (d.Device.TryGetComponent<Restory.Gameplay.Equipment.DevicePaintingTools.PaintableDevice>(out var paint) && paint.PaintTextureId > 0) ids.Add(paint.PaintTextureId);
            return ids;
        }

        private static byte[] Pack(DeviceContainer d, out string json)
        {
            if (serializer == null) throw new InvalidOperationException("no game serializer captured yet");
            var data = Devices.CreateDeviceSaveData(d);
            json = serializer.ToJson(data);
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    gz.Write(bytes, 0, bytes.Length);
                }
                return ms.ToArray();
            }
        }

        private static DeviceSaveData Unpack(byte[] packed, out string json)
        {
            using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                json = reader.ReadToEnd();
                return serializer.FromJsonUnsafe<DeviceSaveData>(json);
            }
        }

        // ----- remote outcome -> local world -----

        private static void Apply(ObjState s)
        {
            applying = true;
            try
            {
                var devices = Devices;
                var existing = Find(s.Id);
                if (existing != null && existing.DragState != InteractiveObjectDragState.None) { Plugin.Log.LogInfo("co-op devices: " + s.Id + " is in the player's hand, skipped"); return; }
                // On this player's own bench (being repaired): the other side does not move it, hide it or take it.
                if (existing != null && OnOwnBench(existing)) { Plugin.Log.LogInfo("co-op devices: " + s.Id + " is on this player's bench, left alone"); return; }
                if (session.IsHost) { if (s.Hidden) RemoteHidden.Add(s.Id); else RemoteHidden.Remove(s.Id); }
                if (s.Hidden)
                {
                    if (existing != null) Top(existing).gameObject.SetActive(false);
                    HiddenApplied++;
                }
                else if (s.Gone)
                {
                    if (existing != null) Destroy(devices, existing);
                }
                else
                {
                    if (serializer == null) throw new InvalidOperationException("no game serializer captured yet");
                    var saved = Unpack(s.Data, out string json);
                    // Destroying the old copy also drops its textures, which the new data may point at too.
                    var kept = CoopTextures.Keep(CoopTextures.Referenced(json));
                    if (existing != null) Destroy(devices, existing);
                    CoopTextures.Restore(kept);
                    var dd = saved.RestoreState();
                    var d = devices.CreateStoredDeviceContainer(dd);
                    if (existing == null && BotId == "") BotId = d.UniqueId; // the first device new on this side
                    var state = (InteractiveObjectState)s.State;
                    if (state == InteractiveObjectState.Shipment)
                    {
                        if (shipment == null) throw new InvalidOperationException("no shipment service captured");
                        shipment.RestoreDevicePackInShipmentStorage(d);
                        // Restoring does not announce the change the way a player's shipping does. On the host the
                        // order logic must see it (customer to claim it, payment into the host's save).
                        if (session.IsHost) RaiseShipmentChanged();
                    }
                    else
                    {
                        var top = Top(d);
                        var parent = CoopRoom.Resolve(s.Parent);
                        if (parent == null) throw new InvalidOperationException("storage " + s.Parent + " not in this shop");
                        top.SetParent(parent, false);
                        top.localPosition = new Vector3(s.X, s.Y, s.Z);
                        top.localRotation = new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw);
                        if (state == InteractiveObjectState.Delivery)
                        {
                            d.SetState(InteractiveObjectState.Delivery);
                            var box = parent.GetComponentInParent<DevicesStorage>();
                            if (box != null) box.AddDeviceToStorage(d);
                        }
                    }
                }
                Applied++;
                Plugin.Log.LogInfo("co-op devices: " + Describe(Find(s.Id), s) + " <- applied");
            }
            catch (Exception e) { Failed++; Plugin.Log.LogError("co-op devices: apply of " + s.Id + " failed: " + e); }
            finally
            {
                applying = false;
                var now = Find(s.Id);
                if (now != null) lastSig[s.Id] = Sig(now); else lastSig.Remove(s.Id);
                appliedAt[s.Id] = Time.unscaledTime;
            }
        }

        private static void Destroy(DeviceService devices, DeviceContainer d)
        {
            var pack = d.GetComponentInParent<DevicePack>(true);
            if (pack != null && pack.State == InteractiveObjectState.Shipment)
            {
                // At the counter: let the shipment stack forget it, then the pack goes like any packed device.
                shipment?.RetrieveInteractiveObject(pack as IShipmentPack);
            }
            pack = d.GetComponentInParent<DevicePack>(true);
            if (pack != null) devices.DestroyPackedDeviceContainer(pack);
            else devices.DestroyDeviceContainer(d);
        }

        private static void RaiseShipmentChanged()
        {
            var handler = AccessTools.Field(typeof(ShipmentService), nameof(ShipmentService.OnShipmentStorageContentChanged))?.GetValue(shipment) as Action;
            handler?.Invoke();
            ShipmentsAnnounced++;
            Plugin.Log.LogInfo("co-op devices: the counter changed, the host's order logic notified");
        }

        public static int ShipmentsAnnounced { get; private set; }

        // ----- captures -----

        [HarmonyPatch(typeof(DeviceRegistry), nameof(DeviceRegistry.Register))]
        private static class RegisterCapture
        {
            private static void Postfix(DeviceRegistry __instance, DeviceContainer device)
            {
                registry = __instance;
                // The host tells the guest about devices that appear (a customer's box, a bought device).
                if (Ready && session.IsHost && !applying && device != null && !string.IsNullOrEmpty(device.UniqueId)) dirty.Add(device.UniqueId);
            }
        }

        // The registry is known as soon as the scene makes it. Waiting for the first Register left it unknown on a
        // guest whose shop loaded without devices: Ready stayed false, and every device from the host waited in
        // "early" for good (seen in the owner's first two-PC game: a customer's phone never reached the guest).
        [HarmonyPatch]
        private static class RegistryCtorCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.Constructor(typeof(DeviceRegistry));
            private static void Postfix(DeviceRegistry __instance) => registry = __instance;
        }

        [HarmonyPatch(typeof(DeviceRegistry), nameof(DeviceRegistry.Unregister))]
        private static class UnregisterCapture
        {
            private static void Postfix(DeviceContainer device)
            {
                if (Ready && session.IsHost && !applying && device != null && !string.IsNullOrEmpty(device.UniqueId)) dirty.Add(device.UniqueId);
            }
        }

        [HarmonyPatch(typeof(ShipmentService), nameof(ShipmentService.Initialize))]
        private static class ShipmentCapture
        {
            private static void Postfix(ShipmentService __instance) => shipment = __instance;
        }

        [HarmonyPatch]
        private static class SerializerCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.FirstConstructor(typeof(GameEntityFullSerializer), c => c.GetParameters().Length > 0);
            private static void Postfix(GameEntityFullSerializer __instance) => serializer = __instance;
        }

        // ----- test bot -----

        // Host: a licensed, ordinary device straight onto a shelf (the way a customer's box would appear).
        public static string BotSpawn()
        {
            var devices = Devices;
            var licenses = SceneCache.Find<LicensesService>(true);
            if (devices == null || licenses == null) return "no device service yet";
            var cond = Resources.FindObjectsOfTypeAll<DeviceCondition>()
                .Where(c => c != null && !c.IsPartOfCompetition && c.DeviceInfo != null && licenses.IsLicensed(c.DeviceInfo))
                .OrderBy(c => c.name, StringComparer.Ordinal).FirstOrDefault();
            if (cond == null) return "no licensed device condition";
            var shelf = UnityEngine.Object.FindObjectsByType<StorageBase>(FindObjectsSortMode.None)
                .Where(t => t.StorageGridPositions.Count > 0).OrderBy(t => CoopRoom.PathOf(t.transform), StringComparer.Ordinal).LastOrDefault();
            if (shelf == null) return "no shelf";
            var data = devices.CreateDeviceData(cond, shelf.transform);
            var d = devices.CreateStoredDeviceContainer(data);
            var top = Top(d);
            top.SetParent(shelf.transform, true);
            top.position = shelf.StorageGridPositions[0];
            dirty.Add(d.UniqueId);
            BotId = d.UniqueId;
            return cond.name + " (" + d.UniqueId + ") on " + CoopRoom.PathOf(shelf.transform);
        }

        // Host: a customer leaves a device the way the game does it (DevicesFromNpcsService), nothing reported by
        // hand: the registry hook alone has to tell the guest.
        public static string BotCustomerDevice()
        {
            var npcs = SceneCache.Find<DevicesFromNpcsService>(true);
            var licenses = SceneCache.Find<LicensesService>(true);
            if (npcs == null || licenses == null) return "no customer device service yet";
            var cond = Resources.FindObjectsOfTypeAll<DeviceCondition>()
                .Where(c => c != null && !c.IsPartOfCompetition && c.DeviceInfo != null && licenses.IsLicensed(c.DeviceInfo))
                .OrderBy(c => c.name, StringComparer.Ordinal).Skip(1).FirstOrDefault();
            if (cond == null) return "no licensed device condition";
            var obj = npcs.AddInteractiveObject(cond);
            return obj == null ? cond.name + " went into the customers' box (no free spot)" : cond.name + " left at " + CoopRoom.PathOf(obj.transform.parent) + " (" + obj.UniqueId + ")";
        }

        public static DeviceContainer BotDevice() => BotId == "" ? null : Find(BotId);

        // Test bot (guest): a device onto this player's bench the way the game's buttons do it, no drop: the host
        // must stop showing it (the owner's game: it stayed on the host's desk and the host could carry it off).
        public static string BotBenchWithoutDrop()
        {
            var devices = Devices;
            if (devices == null || devices.PlacedDeviceContainer != null) return "no device service, or the bench is busy";
            var d = All.Where(x => x != null && x.UniqueId != BotId && Top(x).gameObject.activeInHierarchy
                    && (TopState(x) == InteractiveObjectState.Stored || TopState(x) == InteractiveObjectState.Delivery))
                .OrderBy(x => x.UniqueId, StringComparer.Ordinal).FirstOrDefault();
            if (d == null) return "no device in the room";
            var top = Top(d).GetComponent<InteractiveObject>();
            if (top is DevicePack pack) devices.PlaceDeviceContainer(pack); else devices.PlaceDeviceContainer(d);
            return d.UniqueId + " " + TopState(d);
        }

        public static int RemoteHiddenCount => RemoteHidden.Count;
        public static bool ShowsHere(string id) { var d = Find(id); return d != null && Top(d).gameObject.activeInHierarchy; }

        // Test bot on the guest: put the bot's device on the counter. A broken test device would fail the game's
        // "ready for shipment" check, so this does what the game does after that check passes, then reports it
        // the way a player's drop on the counter is reported.
        public static string BotShip()
        {
            var d = BotDevice();
            if (d == null || shipment == null) return "no bot device or shipment service";
            if (d.GetComponentInParent<DevicePack>(true) != null) return "the device is packed";
            var stacker = AccessTools.Field(typeof(ShipmentService), "packageStacker").GetValue(shipment) as PackageStacker;
            var packer = AccessTools.Field(typeof(ShipmentService), "devicePacker").GetValue(shipment) as DevicePacker;
            if (stacker == null || packer == null || !stacker.HasAvailablePlace(out var point)) return "no free place at the counter";
            var pack = packer.PackStoredDeviceContainerForShipment(d);
            point.SetPackage(pack);
            pack.SetState(InteractiveObjectState.Shipment);
            Dropped(d);
            return d.name + " at the counter (" + CoopRoom.PathOf(point.transform) + ")";
        }

        // Test bot on the host: the bench through the player's drop code; if the game turns it down (its own
        // readiness rules for the bench), the way versus puts a device there, reported like a drop.
        public static string BotToBench()
        {
            var d = BotDevice();
            if (d == null) return "no bot device";
            var top = Top(d).GetComponent<InteractiveObject>();
            string viaDrop = CoopRoom.BotToBench(top);
            if (TopState(d) == InteractiveObjectState.Placed) return viaDrop;
            var devices = Devices;
            if (devices.PlacedDeviceContainer != null) return viaDrop + "; bench busy";
            if (top is DevicePack pack) devices.PlaceDeviceContainer(pack); else devices.PlaceDeviceContainer(d);
            Dropped(top);
            return viaDrop + "; the game refused the drop, placed directly: " + TopState(d);
        }
    }
}
