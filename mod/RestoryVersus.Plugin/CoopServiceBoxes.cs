using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Data.SaveLoad.Containers;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.Storages;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // The other boxes a game service keeps at a spot of its own, the same way CoopDelivery does the courier's box:
    //   customers' box (DevicesFromNpcsService): what customers leave when their spots in the delivery zone are full;
    //   personal box (PersonalBoxService): the player's own things at the start of the game.
    // The host's box (its id, where it stands, what is in it) goes to the guest when it changes; the guest rebuilds its
    // copy when that box is not in use. Takes are reported per box by CoopDelivery ("boxtake", one numbering), and the
    // guest ignores host boxes that do not include its latest take yet.
    internal static class CoopServiceBoxes
    {
        private sealed class Service
        {
            public string Name;
            public Func<MonoBehaviour> Find;
            public Func<MonoBehaviour, InteractiveObjectBoxContainer> Box;
            public Func<MonoBehaviour, string> Key;        // the box as text: id, place and content
            public Action<MonoBehaviour, string> Rebuild;  // from the host's key text
            public string LastSent;
            public int SentAck = -1;
            public string Waiting;
        }

        private static readonly AccessTools.FieldRef<DevicesFromNpcsService, InteractiveObjectBoxContainer> NpcBox =
            AccessTools.FieldRefAccess<DevicesFromNpcsService, InteractiveObjectBoxContainer>("devicesBoxContainer");
        private static readonly System.Reflection.MethodInfo NpcRestoreBox = AccessTools.Method(typeof(DevicesFromNpcsService), "RestoreDevicesBox");
        private static readonly System.Reflection.MethodInfo NpcRestoreItems = AccessTools.Method(typeof(DevicesFromNpcsService), "RestoreDevicesInBox");
        private static readonly AccessTools.FieldRef<PersonalBoxService, InteractiveObjectBoxContainer> PersonalBox =
            AccessTools.FieldRefAccess<PersonalBoxService, InteractiveObjectBoxContainer>("personalBox");
        private static readonly AccessTools.FieldRef<InteractiveObjectBoxContainer, InteractiveObject> TakenField =
            AccessTools.FieldRefAccess<InteractiveObjectBoxContainer, InteractiveObject>("takenObject");

        private static CoopSession session;
        private static float nextTick;
        private static readonly List<Service> services = new List<Service>
        {
            new Service
            {
                Name = "customers",
                Find = () => SceneCache.Find<DevicesFromNpcsService>(true),
                Box = m => NpcBox((DevicesFromNpcsService)m),
                Key = m =>
                {
                    var data = (DevicesFromNpcsSpawnerSaveData)((DevicesFromNpcsService)m).CaptureState();
                    return Json(data.DevicesBoxData) + "\n" + Json(data.ItemsInBox);
                },
                Rebuild = (m, key) =>
                {
                    var npc = (DevicesFromNpcsService)m;
                    var parts = key.Split(new[] { '\n' }, 2);
                    var boxData = FromJson<InteractiveObjectData>(parts[0]);
                    var items = parts.Length > 1 ? FromJson<ContainedInteractiveObject[]>(parts[1]) : null;
                    var old = NpcBox(npc);
                    NpcBox(npc) = null;
                    if (old != null) Destroy(old);
                    NpcRestoreBox.Invoke(npc, new object[] { boxData });
                    NpcRestoreItems.Invoke(npc, new object[] { items });
                },
            },
            new Service
            {
                Name = "personal",
                Find = () => SceneCache.Find<PersonalBoxService>(true),
                Box = m => PersonalBox((PersonalBoxService)m),
                Key = m => Json(((PersonalBoxService)m).CaptureState()),
                Rebuild = (m, key) =>
                {
                    var service = (PersonalBoxService)m;
                    var data = FromJson<PersonalBoxSaveData>(key);
                    var old = PersonalBox(service);
                    PersonalBox(service) = null;
                    if (old != null) Destroy(old);
                    if (data != null && !data.IsRemoved) service.CreatePersonalBox(data.BoxContent, data.InteractiveObjectData);
                },
            },
        };

        public static int StatesSent { get; private set; }
        public static int StatesApplied { get; private set; }

        public static void Attach(CoopSession s)
        {
            session = s;
            foreach (var sv in services) { sv.LastSent = null; sv.SentAck = -1; sv.Waiting = null; }
            if (s != null) s.OnAction += OnAction;
        }

        private static bool Ready => session != null && session.Playing && CoopGame.InShopFor(3f) && CoopDevices.Serializer != null;

        private static string Json(object o) => o == null ? "null" : CoopDevices.Serializer.ToJson(o);
        private static T FromJson<T>(string json) where T : class => json == "null" ? null : CoopDevices.Serializer.FromJsonUnsafe<T>(json);

        private static void Destroy(InteractiveObjectBoxContainer box)
        {
            if (CoopRoom.Factory != null) CoopRoom.Factory.DestroyInteractiveObject(box.InteractiveObject);
            else UnityEngine.Object.Destroy(box.gameObject);
        }

        // Fingerprints of each service's box, for the test to compare the two sides.
        public static string Hashes
        {
            get
            {
                if (!Ready) return "";
                var parts = new List<string>();
                foreach (var sv in services)
                {
                    var m = sv.Find();
                    if (m == null) continue;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        parts.Add(sv.Name + ":" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sv.Key(m)))).Replace("-", "").Substring(0, 8).ToLowerInvariant());
                }
                return string.Join(",", parts);
            }
        }

        public static void Tick()
        {
            if (!Ready || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            foreach (var sv in services)
            {
                try
                {
                    var m = sv.Find();
                    if (m == null) continue;
                    if (session.IsHost)
                    {
                        var key = sv.Key(m);
                        int ack = CoopDelivery.AppliedGuestTake;
                        if (key == sv.LastSent && sv.SentAck == ack) continue;
                        sv.LastSent = key;
                        sv.SentAck = ack;
                        StatesSent++;
                        var packed = Pack(key);
                        string name = sv.Name;
                        session.SendAction("svcbox", Packet.Build(w => { w.Write(name); w.Write(ack); w.Write(packed.Length); w.Write(packed); }));
                    }
                    else if (sv.Waiting != null)
                    {
                        // Not while this player is taking something out of it, or carrying it.
                        var box = sv.Box(m);
                        if (box != null && (TakenField(box) != null || box.InteractiveObject.DragState != InteractiveObjectDragState.None)) continue;
                        var key = sv.Waiting;
                        sv.Waiting = null;
                        if (key == sv.Key(m)) continue;
                        sv.Rebuild(m, key);
                        StatesApplied++;
                        Plugin.Log.LogInfo("co-op boxes: the host's " + sv.Name + " box applied");
                    }
                }
                catch (Exception e) { Plugin.Log.LogError("co-op boxes: " + sv.Name + ": " + e); nextTick = Time.unscaledTime + 5f; }
            }
        }

        private static void OnAction(string name, byte[] data)
        {
            if (name != "svcbox" || session.IsHost) return;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
                {
                    string service = r.ReadString();
                    int ack = r.ReadInt32();
                    var packed = r.ReadBytes(r.ReadInt32());
                    if (ack < CoopDelivery.TakeSeq) return; // our latest take is not in it yet
                    var sv = services.FirstOrDefault(x => x.Name == service);
                    if (sv != null) sv.Waiting = Unpack(packed);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("co-op boxes: " + e); }
        }

        // Test bot (host): customers keep leaving devices until the spots are full and one goes into the box.
        public static string BotFillCustomersBox()
        {
            var npc = SceneCache.Find<DevicesFromNpcsService>(true);
            if (npc == null) return "no customer device service";
            for (int i = 0; i < 12 && NpcBox(npc) == null; i++) CoopDevices.BotCustomerDevice();
            var box = NpcBox(npc);
            return box == null ? "the customers' box did not appear" : "customers' box " + box.InteractiveObject.UniqueId + " with " + box.Content.Count + " item(s)";
        }

        private static byte[] Pack(string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                {
                    var b = Encoding.UTF8.GetBytes(text);
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
    }
}
