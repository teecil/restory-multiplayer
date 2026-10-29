using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Data.SaveLoad.Containers;
using Restory.Gameplay.Devices;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.Storages;
using Restory.Gameplay.Work.Dragging;
using RestoryVersus.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RestoryVersus
{
    // Co-op step 3: the room is shared (owner's rule: moved by one, moved for all). Every drop of a room object
    // goes through the game's DragResolver.ResolveDragResult; right after it the object's outcome is reported:
    //   on a shelf / storage (State Stored)   -> the storage it sits under (a scene path) and its pose there
    //   on the player's own bench, or anything else that takes it out of the room -> Hidden for the other player
    //   gone from the world (recycled, used up) -> Gone, hidden for the other player
    // The other side finds the object by its save id and puts it there. Host authority and the ordering rules
    // live in CoopSession (ReportLocalMove / OnApplyObject), tested with a fuzzer.
    internal static class CoopRoom
    {
        private static readonly AccessTools.FieldRef<DragResolver, InteractiveObject> ResolverSelected =
            AccessTools.FieldRefAccess<DragResolver, InteractiveObject>("selectedObject");
        private static readonly AccessTools.FieldRef<DragResolver, DragObjectInitialDataHolder> ResolverInitial =
            AccessTools.FieldRefAccess<DragResolver, DragObjectInitialDataHolder>("initialDataHolder");
        private static readonly AccessTools.FieldRef<DragResolver, DragHandler> ResolverHandler =
            AccessTools.FieldRefAccess<DragResolver, DragHandler>("dragHandler");
        private static readonly System.Reflection.MethodInfo SetParentStorage =
            AccessTools.PropertySetter(typeof(DragHandler), nameof(DragHandler.ParentStorage));

        private static CoopSession session;
        private static DragResolver resolver;
        private static readonly List<ObjState> early = new List<ObjState>();

        public static int Reported { get; private set; }
        public static int Applied { get; private set; }
        public static int Missing { get; private set; }
        public static int Created { get; private set; }

        public static void Attach(CoopSession s)
        {
            session = s;
            early.Clear();
            RemoteHidden.Clear();
            if (s != null) s.OnApplyObject += o => { if (CoopGame.InShopFor(3f)) Apply(o); else early.Add(o); };
        }

        // Moves that arrived while this copy was still loading the shop.
        public static void Tick()
        {
            if (!CoopGame.InShopFor(3f)) { lastSig.Clear(); RemoteHidden.Clear(); return; }
            if (early.Count > 0)
            {
                var waiting = early.ToList();
                early.Clear();
                foreach (var o in waiting) Apply(o);
            }
            if (Active && CoopGame.Registry != null) WatchForChanges();
        }

        // Where each object was last seen here. Objects also move without a drop the mod sees (a button, the game
        // itself); twice a second every room object is compared with where it was, and a change made here is reported
        // like a drop. Changes applied from the other side update this first, so they are not sent back.
        private static readonly Dictionary<string, string> lastSig = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> appliedAt = new Dictionary<string, float>();
        private static float nextWatch;

        private static void WatchForChanges()
        {
            if (Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + 0.5f;
            foreach (var o in CoopGame.Registry.All.Keys.ToList())
            {
                if (o == null || string.IsNullOrEmpty(o.UniqueId) || o is DeviceContainer || o is DevicePack) continue;
                if (o.DragState != InteractiveObjectDragState.None) continue; // the drop reports it
                string sig = Sig(o);
                if (!lastSig.TryGetValue(o.UniqueId, out var was)) { lastSig[o.UniqueId] = sig; continue; }
                if (was == sig) continue;
                lastSig[o.UniqueId] = sig;
                if (session.IsHost && RemoteHidden.Contains(o.UniqueId)) continue;
                if (!o.gameObject.activeSelf) continue; // hidden here because of the other player: theirs to report
                if (appliedAt.TryGetValue(o.UniqueId, out var at) && Time.unscaledTime - at < 1.5f) continue; // settling after a remote change
                Plugin.Log.LogInfo("co-op room: " + o.name + " moved here without a drop");
                try { Report(o); }
                catch (Exception e) { Plugin.Log.LogError("co-op room: report failed: " + e); }
            }
        }

        private static bool Active => session != null && session.Playing;

        // ----- local drop -> report -----

        [HarmonyPatch(typeof(DragResolver), nameof(DragResolver.ResolveDragResult))]
        private static class DropPatch
        {
            private static void Prefix(DragResolver __instance, out InteractiveObject __state)
            {
                resolver = __instance;
                __state = ResolverSelected(__instance);
            }

            private static void Postfix(InteractiveObject __state, ref bool isWaitingForConfirmation)
            {
                if (!Active) return;
                // Paint for the walls waits for a colour choice first: reported once the player has answered.
                if (isWaitingForConfirmation) { awaitingColour = __state; return; }
                // Devices (and their boxes) have their own registry and data: CoopDevices reports them.
                if (__state is DeviceContainer || __state is DevicePack) { CoopDevices.Dropped(__state); return; }
                try { Report(__state); }
                catch (Exception e) { Plugin.Log.LogError("co-op room: report failed: " + e); }
            }
        }

        private static InteractiveObject awaitingColour;

        [HarmonyPatch(typeof(DragResolver), nameof(DragResolver.OnConfirmationResponse))]
        private static class ColourAnswerPatch
        {
            private static void Postfix()
            {
                var obj = awaitingColour;
                awaitingColour = null;
                if (!Active || ReferenceEquals(obj, null)) return;
                try { Report(obj); }
                catch (Exception e) { Plugin.Log.LogError("co-op room: report failed: " + e); }
            }
        }

        private static void Report(InteractiveObject obj)
        {
            if (ReferenceEquals(obj, null)) return;
            string id = obj != null ? obj.UniqueId : null;
            if (string.IsNullOrEmpty(id)) return;
            // Host: an object the guest holds on its bench is not ours to report.
            if (session != null && session.IsHost && RemoteHidden.Contains(id)) return;
            var s = new ObjState { Id = id };
            bool registered = obj != null && CoopGame.Registry != null && CoopGame.Registry.All.ContainsKey(obj);
            if (obj == null || !registered) s.Gone = true;
            else
            {
                s.State = (byte)obj.State;
                // In the room in any state (on a shelf, at a customer's spot, in the delivery zone): where it is.
                if (InRoom(obj))
                {
                    s.Parent = PathOf(obj.transform.parent);
                    var p = obj.transform.localPosition;
                    var q = obj.transform.localRotation;
                    s.X = p.x; s.Y = p.y; s.Z = p.z;
                    s.Qx = q.x; s.Qy = q.y; s.Qz = q.z; s.Qw = q.w;
                    // The object as the save holds it, so the other side can make it if it has none (something
                    // taken out of a box, a courier box moved off the delivery spot).
                    if (CoopDevices.Serializer != null)
                        s.Data = Pack(CoopDevices.Serializer.ToJson(new InteractiveObjectData
                        {
                            InteractiveObjectInfo = CoopGame.Registry.All[obj],
                            InteractiveObjectTransform = new SerializableTransform(obj.transform),
                            State = obj.State,
                            UniqueId = obj.UniqueId,
                            HasChanged = obj.HasChanged,
                            InteractiveObjectAdditionalProperties = obj.AdditionalProperties.CaptureState()
                        }));
                }
                else s.Hidden = true; // on this player's bench, or otherwise out of the shared room
            }
            Reported++;
            if (!s.Gone) lastSig[id] = Sig(obj); else lastSig.Remove(id);
            Plugin.Log.LogInfo("co-op room: " + Describe(obj, s) + " -> sent");
            if (!s.Gone) CoopCash.BeforeReport(obj);
            session.ReportLocalMove(s);
        }

        // ----- remote outcome -> local world -----

        private static void Apply(ObjState s)
        {
            if (s.Kind != ObjState.KindObject) return;
            try
            {
                var obj = Find(s.Id) ?? CoopDelivery.Adopt(s.Id);
                if (obj == null && !s.Gone && !s.Hidden) obj = Create(s);
                if (obj == null) { Missing++; Plugin.Log.LogWarning("co-op room: object " + s.Id + " not in this shop"); return; }
                // Being dragged here right now: this player's drop will be reported and settle it for both.
                if (obj.DragState != InteractiveObjectDragState.None) { Plugin.Log.LogInfo("co-op room: " + obj.name + " is in the player's hand, skipped"); return; }
                if (obj.State == InteractiveObjectState.Placed && obj.gameObject.activeInHierarchy) { Plugin.Log.LogInfo("co-op room: " + obj.name + " is on this player's bench, left alone"); return; }
                if (session.IsHost)
                {
                    // The guest's bench: kept hidden here, and the room list says so instead of "out of the room".
                    if (s.Hidden) RemoteHidden.Add(s.Id); else RemoteHidden.Remove(s.Id);
                }
                if (s.Gone)
                {
                    // Thrown away, recycled, used up: removed, not hidden. A hidden object stays in the room
                    // registry and so in the host's save: three thrown-away courier boxes came back the next day
                    // in the owner's game.
                    Applied++;
                    lastSig.Remove(s.Id);
                    Plugin.Log.LogInfo("co-op room: " + Describe(obj, s) + " <- applied");
                    RemoveLocal(obj);
                    return;
                }
                if (s.Hidden)
                {
                    obj.gameObject.SetActive(false);
                }
                else
                {
                    var parent = Resolve(s.Parent);
                    if (parent == null) { Missing++; Plugin.Log.LogWarning("co-op room: storage " + s.Parent + " not in this shop"); return; }
                    obj.gameObject.SetActive(true);
                    obj.transform.SetParent(parent, false);
                    obj.SetState((InteractiveObjectState)s.State);
                    if (obj is DeviceContainer dc && obj.State == InteractiveObjectState.Stored) dc.SetStoragePoint();
                    obj.transform.localPosition = new Vector3(s.X, s.Y, s.Z);
                    obj.transform.localRotation = new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw);
                    FoldLikeTheOther(obj, s);
                }
                CoopCash.AfterApply(obj);
                Applied++;
                lastSig[s.Id] = Sig(obj);
                appliedAt[s.Id] = Time.unscaledTime;
                Plugin.Log.LogInfo("co-op room: " + Describe(obj, s) + " <- applied");
            }
            catch (Exception e) { Plugin.Log.LogError("co-op room: apply failed: " + e); }
        }

        // A box carried once folds flat for good (FoldableObject, HasChanged); the copy here folds too.
        private static readonly System.Reflection.MethodInfo Fold = AccessTools.Method(typeof(FoldableObject), "SwapToFoldedObject");

        private static void FoldLikeTheOther(InteractiveObject obj, ObjState s)
        {
            if (obj.HasChanged || s.Data == null || s.Data.Length == 0 || CoopDevices.Serializer == null) return;
            var data = CoopDevices.Serializer.FromJsonUnsafe<InteractiveObjectData>(Unpack(s.Data));
            if (data == null || !data.HasChanged) return;
            obj.HasChanged = true;
            if (obj.TryGetComponent<FoldableObject>(out var foldable)) Fold?.Invoke(foldable, null);
        }

        // ----- for the room list (CoopCensus) -----

        // Host: objects the guest has on its bench (hidden here because of that, not gone).
        internal static readonly HashSet<string> RemoteHidden = new HashSet<string>();

        private static bool InRoom(InteractiveObject o) =>
            o.gameObject.activeSelf && o.State != InteractiveObjectState.Placed && o.transform.parent != null;

        // Where an object is, short: state, storage path, position to 1 cm; "h" when out of the room.
        internal static string Sig(InteractiveObject o)
        {
            if (!InRoom(o)) return "h";
            var p = o.transform.localPosition;
            return ((int)o.State).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + PathOf(o.transform.parent) + "|"
                + string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", p.x, p.y, p.z)
                + CoopCash.SigPart(o);
        }

        internal static void ForceReport(InteractiveObject o) => Report(o);

        internal static void RemoveLocal(InteractiveObject o)
        {
            CoopGame.Registry?.Unregister(o);
            if (Factory != null) Factory.DestroyInteractiveObject(o);
            else UnityEngine.Object.Destroy(o.gameObject);
        }

        // Not in this shop yet: made from the data the other side sent, the way loading a save makes it.
        private static InteractiveObject Create(ObjState s)
        {
            if (s.Data == null || s.Data.Length == 0 || Factory == null || CoopDevices.Serializer == null || CoopGame.Registry == null) return null;
            var parent = Resolve(s.Parent);
            if (parent == null) return null;
            var data = CoopDevices.Serializer.FromJsonUnsafe<InteractiveObjectData>(Unpack(s.Data));
            if (data == null || data.InteractiveObjectInfo == null) return null;
            // Never a second object with an id that is already here (a box still held by its service, an object
            // outside the room registry): that one becomes the room object instead. Two courier boxes on one spot,
            // acting as one, in the owner's game.
            var twin = UnityEngine.Object.FindObjectsByType<InteractiveObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(x => x != null && x.UniqueId == s.Id);
            if (twin != null)
            {
                if (CoopDelivery.Adopt(s.Id) == null && !CoopGame.Registry.All.ContainsKey(twin)) CoopGame.Registry.Register(twin, data.InteractiveObjectInfo);
                Plugin.Log.LogInfo("co-op room: " + twin.name + " (" + s.Id + ") was already here outside the room list, used it instead of making another");
                return twin;
            }
            var obj = Factory.CreateInteractiveObject(data.InteractiveObjectInfo, parent);
            CoopGame.Registry.Register(obj, data.InteractiveObjectInfo);
            obj.Init(data.State, data.UniqueId, data.HasChanged, data.InteractiveObjectAdditionalProperties?.RestoreState());
            Created++;
            Plugin.Log.LogInfo("co-op room: made " + obj.name + " (" + s.Id + "), the other player brought it into the room");
            return obj;
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

        internal static InteractiveObjectFactory Factory { get; private set; }

        [HarmonyPatch(typeof(InteractiveObjectFactory), nameof(InteractiveObjectFactory.CreateInteractiveObject))]
        private static class FactoryCapture
        {
            private static void Postfix(InteractiveObjectFactory __instance) => Factory = __instance;
        }

        internal static InteractiveObject Find(string id)
        {
            if (CoopGame.Registry == null) return null;
            foreach (var kv in CoopGame.Registry.All)
                if (kv.Key != null && kv.Key.UniqueId == id) return kv.Key;
            return null;
        }

        private static string Describe(InteractiveObject obj, ObjState s) =>
            (obj != null ? obj.name : s.Id) + (s.Gone ? " gone" : s.Hidden ? " out of the room" : " on " + s.Parent);

        // ----- scene paths: scene name, then name:sibling index down from the root -----

        public static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name + ":" + x.GetSiblingIndex());
            parts.Reverse();
            return t.gameObject.scene.name + "|" + string.Join("/", parts);
        }

        public static Transform Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int bar = path.IndexOf('|');
            if (bar < 0) return null;
            var scene = SceneManager.GetSceneByName(path.Substring(0, bar));
            if (!scene.IsValid() || !scene.isLoaded) return null;
            var parts = path.Substring(bar + 1).Split('/');
            Transform cur = null;
            foreach (var part in parts)
            {
                int colon = part.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(part.Substring(colon + 1), out int index)) return null;
                string name = part.Substring(0, colon);
                Transform next = null;
                if (cur == null)
                {
                    foreach (var go in scene.GetRootGameObjects())
                        if (go.name == name && go.transform.GetSiblingIndex() == index) { next = go.transform; break; }
                }
                else if (index < cur.childCount && cur.GetChild(index).name == name) next = cur.GetChild(index);
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        // ----- tests -----

        // The room as this copy sees it: id, active, parent path, local position (rounded), one line per object.
        public static void WriteRoom(string path)
        {
            if (CoopGame.Registry == null) return;
            var lines = new List<string>();
            foreach (var kv in CoopGame.Registry.All)
            {
                var o = kv.Key;
                if (o == null) continue;
                var p = o.transform.localPosition;
                lines.Add(o.UniqueId + " " + o.name + " " + (o.gameObject.activeSelf ? "shown" : "hidden") + " " + o.State + " "
                    + (o.transform.parent != null ? PathOf(o.transform.parent) : "-")
                    + string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0:0.000},{1:0.000},{2:0.000}", p.x, p.y, p.z));
            }
            foreach (var d in CoopDevices.All)
            {
                if (d == null) continue;
                var top = CoopDevices.Top(d);
                var p = top.localPosition;
                lines.Add(d.UniqueId + " device:" + d.Device.Info.ID.Replace(' ', '_') + " " + (top.gameObject.activeInHierarchy ? "shown" : "hidden") + " "
                    + (top.parent != null ? PathOf(top.parent) : "-")
                    + string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0:0.000},{1:0.000},{2:0.000}", p.x, p.y, p.z));
            }
            lines.Sort(StringComparer.Ordinal);
            File.WriteAllText(path, string.Join("\n", lines));
        }

        // Test bot: move an object to another storage through the game's own drop code (the same DragResolver the
        // player's drop uses), so the report hook is exercised too. Returns what it did, or why it could not.
        public static string BotMove(string objectName, int salt)
        {
            var obj = CoopGame.Registry?.All.Keys.FirstOrDefault(o => o != null && o.name.StartsWith(objectName) && o.gameObject.activeInHierarchy && o.State == InteractiveObjectState.Stored);
            if (obj == null) return "no stored " + objectName;
            return BotMove(obj, salt);
        }

        // Test bot: drop onto the player's own bench, through the same resolver.
        public static string BotToBench(InteractiveObject obj)
        {
            if (resolver == null) return "no drag resolver yet";
            var handler = ResolverHandler(resolver);
            var initial = ResolverInitial(resolver);
            var overSurface = AccessTools.PropertySetter(typeof(DragHandler), nameof(DragHandler.IsOverSurface));
            obj.StartDrag();
            initial.Init(obj);
            handler.Init(obj);
            SetParentStorage.Invoke(handler, new object[] { null });
            overSurface.Invoke(handler, new object[] { true });
            resolver.Init(obj);
            resolver.ResolveDragResult(out _);
            resolver.Cleanup();
            handler.Cleanup();
            initial.Cleanup();
            overSurface.Invoke(handler, new object[] { false });
            return obj.name + " -> bench: " + obj.State;
        }

        public static string BotMove(InteractiveObject obj, int salt)
        {
            if (resolver == null) return "no drag resolver yet (nothing dragged since the shop loaded)";
            if (obj == null) return "no object";
            string objectName = obj.name;
            var targets = UnityEngine.Object.FindObjectsByType<StorageBase>(FindObjectsSortMode.None)
                .Where(t => t.transform != obj.transform.parent && t.StorageGridPositions.Count > 0)
                .OrderBy(t => PathOf(t.transform), StringComparer.Ordinal).ToList();
            if (targets.Count == 0) return "no other storage";
            var target = targets[(Math.Abs(objectName.GetHashCode()) + salt) % targets.Count];
            var handler = ResolverHandler(resolver);
            var initial = ResolverInitial(resolver);
            obj.StartDrag();
            initial.Init(obj);
            handler.Init(obj);
            SetParentStorage.Invoke(handler, new object[] { target.transform });
            obj.transform.position = target.StorageGridPositions[target.StorageGridPositions.Count / 2];
            obj.DragState = InteractiveObjectDragState.Storable;
            resolver.Init(obj);
            resolver.ResolveDragResult(out _);
            resolver.Cleanup();
            handler.Cleanup();
            initial.Cleanup();
            return obj.name + " -> " + PathOf(target.transform);
        }

        // The resolver is created with the drag state; the bot needs it before any real drag happened.
        [HarmonyPatch]
        private static class ResolverCtorCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.FirstConstructor(typeof(DragResolver), c => c.GetParameters().Length > 0);
            private static void Postfix(DragResolver __instance) => resolver = __instance;
        }
    }
}
