using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Restory.Gameplay.Devices;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.Work.Dragging;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // The object in the other player's hand. While a player drags a room object or a box, its world pose goes out
    // about 12 times a second (unreliable); the other copy glides the same object after it and makes it
    // unpickable until the drag ends. The drop's own outcome (CoopRoom / CoopDevices, reliable) then settles it.
    // The release is sent reliably before that outcome, so late poses of the ended drag are dropped (HeldTracker).
    internal static class CoopHeld
    {
        private const float SendInterval = 1f / 12f;
        private const float Follow = 15f; // how fast the copy closes on the hand's pose (per second)

        private static CoopSession session;
        private static readonly HeldTracker tracker = new HeldTracker();
        private static readonly Dictionary<string, InteractiveObject> locked = new Dictionary<string, InteractiveObject>();
        private static DragObjectRegistrator registrator;
        internal static bool LocalDragging => registrator != null && registrator.DraggingObject != null;
        private static InteractiveObject local;
        private static string localId;
        private static byte localKind;
        private static int dragCounter;
        private static float nextSend;

        public static int PosesSent { get; private set; }
        public static int PosesFollowed { get; private set; }
        public static int LocksSeen { get; private set; }
        public static int Releases { get; private set; }
        public static int Locked => locked.Count;

        public static void Attach(CoopSession s)
        {
            session = s;
            tracker.Reset();
            UnlockAll();
            local = null;
            if (s == null) return;
            s.OnHeld += p => tracker.Accept(p, Now);
            s.OnAction += (name, data) =>
            {
                if (name != "release") return;
                using (var r = new BinaryReader(new MemoryStream(data)))
                {
                    int drag = r.ReadInt32();
                    string id = r.ReadString();
                    tracker.Release(drag, id);
                    Unlock(id);
                    Releases++;
                }
            };
        }

        private static long Now => (long)(Time.unscaledTime * 1000f);
        private static bool Active => session != null && session.Playing && CoopGame.InShopFor(3f);

        // Which shared object this is: a room object by its save id, a box or device by its device's id.
        private static bool Identify(InteractiveObject o, out string id, out byte kind)
        {
            id = null;
            kind = ObjState.KindObject;
            if (o == null) return false;
            var device = o as DeviceContainer ?? (o as DevicePack)?.DeviceContainer;
            if (device != null)
            {
                id = device.UniqueId;
                kind = ObjState.KindDevice;
            }
            else if (CoopGame.Registry != null && CoopGame.Registry.All.ContainsKey(o)) id = o.UniqueId;
            return !string.IsNullOrEmpty(id);
        }

        private static InteractiveObject Find(string id, byte kind)
        {
            if (kind == ObjState.KindDevice)
            {
                var d = CoopDevices.Find(id);
                return d != null ? CoopDevices.Top(d).GetComponent<InteractiveObject>() : null;
            }
            if (CoopGame.Registry == null) return null;
            foreach (var o in CoopGame.Registry.All.Keys)
                if (o != null && o.UniqueId == id) return o;
            return null;
        }

        // ----- this player's drag -----

        private static void Started(InteractiveObject o)
        {
            if (!Active || !Identify(o, out localId, out localKind)) { local = null; return; }
            local = o;
            dragCounter++;
            nextSend = 0;
        }

        // Ends this player's drag for the other side; called at the drop (before its outcome) and when the game
        // stops the drag, whichever comes first.
        private static void Released()
        {
            if (local == null) return;
            var id = localId;
            int drag = dragCounter;
            local = null;
            if (session != null && session.Playing) session.SendAction("release", Packet.Build(w => { w.Write(drag); w.Write(id); }));
        }

        [HarmonyPatch(typeof(DragObjectRegistrator), nameof(DragObjectRegistrator.RegisterDraggingObject))]
        private static class DragStarts
        {
            private static void Postfix(DragObjectRegistrator __instance, InteractiveObject draggingObject)
            {
                registrator = __instance;
                Started(draggingObject);
            }
        }

        // Created with the shop's services; the test bot needs it before any real drag.
        [HarmonyPatch]
        private static class RegistratorCapture
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.Constructor(typeof(DragObjectRegistrator));
            private static void Postfix(DragObjectRegistrator __instance) => registrator = __instance;
        }

        [HarmonyPatch(typeof(DragObjectRegistrator), nameof(DragObjectRegistrator.UnregisterDraggingObject))]
        private static class DragStops
        {
            private static void Prefix() => Released();
        }

        [HarmonyPatch(typeof(DragResolver), nameof(DragResolver.ResolveDragResult))]
        private static class DropReleases
        {
            private static void Prefix() => Released();
        }

        // ----- per frame -----

        public static void Tick()
        {
            if (session == null) return;
            if (!Active) { if (locked.Count > 0) { tracker.Reset(); UnlockAll(); } return; }

            if (local != null && Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + SendInterval;
                var t = local.transform;
                var p = t.position;
                var q = t.rotation;
                session.SendHeld(new HeldPose { Id = localId, Kind = localKind, DragId = dragCounter, X = p.x, Y = p.y, Z = p.z, Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w });
                PosesSent++;
            }

            foreach (var id in tracker.Expire(Now)) Unlock(id);
            foreach (var kv in tracker.Held)
            {
                var pose = kv.Value;
                var o = Find(pose.Id, pose.Kind);
                // Not here, out of sight (on this player's bench), or in this player's own hand: leave it.
                if (o == null || !o.gameObject.activeInHierarchy || o.DragState != InteractiveObjectDragState.None) continue;
                if (!locked.ContainsKey(pose.Id))
                {
                    locked[pose.Id] = o;
                    o.IsInteractable = false;
                    LocksSeen++;
                }
                float k = 1f - Mathf.Exp(-Follow * Time.unscaledDeltaTime);
                var t = o.transform;
                t.position = Vector3.Lerp(t.position, new Vector3(pose.X, pose.Y, pose.Z), k);
                t.rotation = Quaternion.Slerp(t.rotation, new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw), k);
                PosesFollowed++;
            }
        }

        private static void Unlock(string id)
        {
            if (!locked.TryGetValue(id, out var o)) return;
            locked.Remove(id);
            if (o != null) o.IsInteractable = true;
        }

        private static void UnlockAll()
        {
            foreach (var o in locked.Values) if (o != null) o.IsInteractable = true;
            locked.Clear();
        }

        // ----- test bot -----

        // Host: pick the clock up, carry it around for two seconds, drop it on another shelf through the game's
        // drop code, the way a player's drag goes (register, move, resolve, unregister).
        public static IEnumerator BotCarry(string objectName, Action<string> log)
        {
            var o = CoopGame.Registry?.All.Keys.FirstOrDefault(x => x != null && x.name.StartsWith(objectName) && x.gameObject.activeInHierarchy && x.State == InteractiveObjectState.Stored);
            if (o == null || registrator == null) { log("nothing to carry (" + (o == null ? "no " + objectName : "no drag registrator yet") + ")"); yield break; }
            registrator.RegisterDraggingObject(o);
            o.StartDrag();
            var start = o.transform.position;
            float t0 = Time.unscaledTime;
            int sentBefore = PosesSent;
            // At least 2 s and 16 poses (a slow frame rate sends fewer per second), at most 6 s.
            while (Time.unscaledTime - t0 < 6f && (Time.unscaledTime - t0 < 2f || PosesSent - sentBefore < 16))
            {
                float a = (Time.unscaledTime - t0) * Mathf.PI;
                o.transform.position = start + new Vector3(Mathf.Sin(a) * 0.4f, 0.3f, Mathf.Cos(a) * 0.4f - 0.4f);
                yield return null;
            }
            var where = CoopRoom.BotMove(o, 6);
            registrator.UnregisterDraggingObject();
            log("carried " + objectName + " for " + (Time.unscaledTime - t0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s, " + PosesSent + " poses sent, then " + where);
        }
    }
}
