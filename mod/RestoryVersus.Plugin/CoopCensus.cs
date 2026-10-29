using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Restory.Gameplay.Devices;
using Restory.Gameplay.InteractiveObjects;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // The room checked as a whole, so whatever the game makes or removes on the host without a player's drop (a
    // customer's device, a bill, money, a licence, an order taken out of a box) still reaches the guest. Events
    // (CoopRoom, CoopDevices) stay the fast path; this catches what they miss.
    //   host  -> every 2 s, when it changed (and every 10 s anyway): every room object and device by id with a short
    //            signature (state, storage path, position to 1 cm; "h" out of the room; "g" hidden because the
    //            guest has it on its bench).
    //   guest -> compares with its own room. Missing or different: asks the host for those ("need"), and the host
    //            reports them the usual way (data included, so missing ones are made). Out of the room on the host:
    //            hidden here. Here but not on the host in two lists in a row: removed here. Never touched: what is
    //            in this player's hand, on this player's bench, or moved by this player and not confirmed yet.
    internal static class CoopCensus
    {
        private const float Interval = 2f;
        private const float ForcedInterval = 10f;
        private const float AskAgainAfter = 8f;

        private static CoopSession session;
        private static float nextCensus, lastSentAt;
        private static string lastSent = "";
        private static readonly Dictionary<string, int> missingStreak = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> askedAt = new Dictionary<string, float>();

        public static int Sent { get; private set; }
        public static int Received { get; private set; }
        public static int Asked { get; private set; }
        public static int Hidden { get; private set; }
        public static int Removed { get; private set; }
        public static int Answered { get; private set; }

        public static void Attach(CoopSession s)
        {
            session = s;
            lastSent = "";
            missingStreak.Clear();
            askedAt.Clear();
            if (s != null) s.OnAction += OnAction;
        }

        private static bool Ready => session != null && session.Playing && !session.HostEndingDay && CoopGame.InShopFor(4f)
            && CoopGame.Registry != null && CoopDevices.Ready;

        public static void Tick()
        {
            if (!Ready || !session.IsHost || Time.unscaledTime < nextCensus) return;
            nextCensus = Time.unscaledTime + Interval;
            try
            {
                var text = Build();
                if (text == lastSent && Time.unscaledTime - lastSentAt < ForcedInterval) return;
                lastSent = text;
                lastSentAt = Time.unscaledTime;
                Sent++;
                session.SendAction("census", Pack(text));
            }
            catch (Exception e) { Plugin.Log.LogError("co-op census: " + e); nextCensus = Time.unscaledTime + 10f; }
        }

        // One line per thing: kind (o/d), id, signature.
        private static string Build()
        {
            var lines = new List<string>();
            foreach (var o in CoopGame.Registry.All.Keys)
            {
                if (o == null || string.IsNullOrEmpty(o.UniqueId)) continue;
                lines.Add("o\t" + o.UniqueId + "\t" + (CoopRoom.RemoteHidden.Contains(o.UniqueId) ? "g" : CoopRoom.Sig(o)));
            }
            foreach (var d in CoopDevices.All)
            {
                if (d == null || string.IsNullOrEmpty(d.UniqueId)) continue;
                lines.Add("d\t" + d.UniqueId + "\t" + (CoopDevices.RemoteHidden.Contains(d.UniqueId) ? "g" : CoopDevices.ListSig(d)));
            }
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        private static void OnAction(string name, byte[] data)
        {
            try
            {
                if (name == "census" && !session.IsHost) Compare(Unpack(data));
                else if (name == "need" && session.IsHost) Answer(Encoding.UTF8.GetString(data));
            }
            catch (Exception e) { Plugin.Log.LogError("co-op census: " + name + ": " + e); }
        }

        // ----- guest -----

        private static bool Untouchable(string id, InteractiveObject top, InteractiveObjectState state)
        {
            if (session.IsPending(id)) return true;
            if (top != null && top.DragState != InteractiveObjectDragState.None) return true;
            return state == InteractiveObjectState.Placed && top != null && top.gameObject.activeInHierarchy; // this player's bench
        }

        private static void Compare(string text)
        {
            if (!Ready) return; // loading: the next list comes in a few seconds
            Received++;
            var host = new Dictionary<string, (char kind, string sig)>();
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3) host[parts[1]] = (parts[0][0], parts[2]);
            }
            var need = new List<string>();
            int hidden = 0, removed = 0;

            foreach (var kv in host)
            {
                string id = kv.Key, sig = kv.Value.sig;
                if (sig == "g") continue;
                if (kv.Value.kind == 'o')
                {
                    var o = CoopRoom.Find(id);
                    if (o == null) { if (sig != "h") need.Add(id); continue; }
                    if (Untouchable(id, o, o.State)) continue;
                    if (CoopRoom.Sig(o) == sig) continue;
                    if (sig == "h") { o.gameObject.SetActive(false); hidden++; }
                    else need.Add(id);
                }
                else
                {
                    var d = CoopDevices.Find(id);
                    if (d == null) { if (sig != "h") need.Add(id); continue; }
                    var top = CoopDevices.Top(d).GetComponent<InteractiveObject>();
                    if (Untouchable(id, top, CoopDevices.StateOf(d)) || CoopDevices.OnOwnBench(d)) continue;
                    if (CoopDevices.ListSig(d) == sig) continue;
                    if (sig == "h") { CoopDevices.Top(d).gameObject.SetActive(false); hidden++; }
                    else need.Add(id);
                }
            }

            // Here but not on the host: gone there (sold, recycled, used up) in a way no event told us.
            var seen = new HashSet<string>();
            foreach (var o in CoopGame.Registry.All.Keys.ToList())
            {
                if (o == null || string.IsNullOrEmpty(o.UniqueId) || host.ContainsKey(o.UniqueId)) continue;
                seen.Add(o.UniqueId);
                if (Untouchable(o.UniqueId, o, o.State)) continue;
                if (Streak(o.UniqueId) < 2) continue;
                Plugin.Log.LogInfo("co-op census: " + o.name + " (" + o.UniqueId + ") is not in the host's shop, removed here");
                CoopRoom.RemoveLocal(o);
                removed++;
            }
            foreach (var d in CoopDevices.All.ToList())
            {
                if (d == null || string.IsNullOrEmpty(d.UniqueId) || host.ContainsKey(d.UniqueId)) continue;
                seen.Add(d.UniqueId);
                var top = CoopDevices.Top(d).GetComponent<InteractiveObject>();
                if (Untouchable(d.UniqueId, top, CoopDevices.StateOf(d)) || CoopDevices.OnOwnBench(d)) continue;
                if (Streak(d.UniqueId) < 2) continue;
                Plugin.Log.LogInfo("co-op census: device " + d.UniqueId + " is not in the host's shop, removed here");
                CoopDevices.RemoveLocal(d);
                removed++;
            }
            foreach (var id in missingStreak.Keys.ToList()) if (!seen.Contains(id)) missingStreak.Remove(id);

            need = need.Where(id => !askedAt.TryGetValue(id, out var t) || Time.unscaledTime - t > AskAgainAfter).ToList();
            foreach (var id in need) askedAt[id] = Time.unscaledTime;
            Hidden += hidden;
            Removed += removed;
            if (need.Count > 0)
            {
                Asked += need.Count;
                session.SendAction("need", Encoding.UTF8.GetBytes(string.Join("\n", need)));
            }
            if (need.Count > 0 || hidden > 0 || removed > 0)
                Plugin.Log.LogInfo("co-op census: asked the host for " + need.Count + ", hid " + hidden + ", removed " + removed);
        }

        private static int Streak(string id)
        {
            missingStreak.TryGetValue(id, out int n);
            missingStreak[id] = ++n;
            return n;
        }

        // ----- host -----

        private static void Answer(string ids)
        {
            if (!Ready) return;
            int n = 0;
            foreach (var id in ids.Split('\n'))
            {
                if (string.IsNullOrEmpty(id) || CoopRoom.RemoteHidden.Contains(id) || CoopDevices.RemoteHidden.Contains(id)) continue;
                var o = CoopRoom.Find(id);
                if (o != null) { CoopRoom.ForceReport(o); n++; continue; }
                if (CoopDevices.Find(id) != null) { CoopDevices.ForceReport(id); n++; }
            }
            Answered += n;
            if (n > 0) Plugin.Log.LogInfo("co-op census: the guest asked for " + n + " thing(s), sent");
        }

        // ----- test bots (host) -----

        private static InteractiveObject silent;

        // A copy of a room object put on a shelf through the game's own service, nothing reported.
        public static string BotSilentSpawn()
        {
            var service = SceneCache.Find<InteractiveObjectService>(true);
            var model = CoopGame.Registry?.All.FirstOrDefault(kv => kv.Key != null && kv.Key.name.StartsWith("Radio") && kv.Value != null);
            if (service == null || model == null || model.Value.Key == null) return "no service or no radio to copy";
            var shelf = UnityEngine.Object.FindObjectsByType<Restory.Gameplay.Storages.StorageBase>(FindObjectsSortMode.None)
                .Where(t => t.StorageGridPositions.Count > 1).OrderBy(t => CoopRoom.PathOf(t.transform), StringComparer.Ordinal).FirstOrDefault();
            if (shelf == null) return "no shelf";
            var target = new GameObject("CensusTarget").transform;
            target.SetParent(shelf.transform, false);
            target.position = shelf.StorageGridPositions[1];
            try { silent = service.CreateNewInteractiveObject(model.Value.Value, target); }
            finally { UnityEngine.Object.Destroy(target.gameObject); }
            return silent.name + " " + silent.UniqueId + " on " + CoopRoom.PathOf(silent.transform.parent);
        }

        // The same object gone again, nothing reported.
        public static string BotSilentRemove()
        {
            if (silent == null) return "nothing made";
            string id = silent.UniqueId;
            CoopRoom.RemoveLocal(silent);
            silent = null;
            return id;
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
