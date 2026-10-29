using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Restory.Gameplay.Devices;
using Restory.Gameplay.TextureMasks;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Dirt on parts and paint on devices are textures in the game's TextureCacheService, referenced from device
    // and part data by number (DirtMaskTextureId, PaintTextureId). The numbers come from one counter per copy,
    // so two players would hand out the same numbers, and a part cleaned on the guest's bench would arrive at the
    // host pointing at a texture the host does not have.
    //   numbers: during co-op the host numbers below 1 000 000, the guest from 1 000 000 up (set after each load)
    //   textures: data that leaves this copy (a device, the parts stock) takes along the textures it points at
    //             that the other side has not got yet (the action "tex", sent before the data on the same ordered
    //             channel)
    internal static class CoopTextures
    {
        public const int GuestBase = 1000000;
        private static readonly Regex Ids = new Regex("\"(?:DirtMaskTextureId|PaintTextureId)\":(\\d+)");

        private static readonly AccessTools.FieldRef<TextureCacheService, Dictionary<int, byte[]>> Cache =
            AccessTools.FieldRefAccess<TextureCacheService, Dictionary<int, byte[]>>("textureCache");
        private static readonly AccessTools.FieldRef<TextureCacheService, HashSet<int>> Marked =
            AccessTools.FieldRefAccess<TextureCacheService, HashSet<int>>("markedTextures");
        private static readonly AccessTools.FieldRef<TextureCacheService, int> Next =
            AccessTools.FieldRefAccess<TextureCacheService, int>("nextTextureId");

        private static CoopSession session;
        private static TextureCacheService service;
        // The texture each number held when the other side last got it (both loaded the same save: all of it).
        private static readonly Dictionary<int, byte[]> known = new Dictionary<int, byte[]>();

        public static int Sent { get; private set; }
        public static int Received { get; private set; }
        public static int SentBytes { get; private set; }
        public static string BotTexture { get; private set; } = ""; // "id:hash" of the test bot's texture

        public static void Attach(CoopSession s)
        {
            session = s;
            if (s == null) return;
            s.OnAction += (name, data) =>
            {
                if (name != "tex") return;
                using (var r = new BinaryReader(new MemoryStream(data)))
                {
                    int id = r.ReadInt32();
                    int len = r.ReadInt32();
                    var bytes = r.ReadBytes(len);
                    Put(id, bytes);
                    Received++;
                }
            };
        }

        private static bool Playing => session != null && session.Playing;

        public static IEnumerable<int> Referenced(string json)
        {
            foreach (Match m in Ids.Matches(json ?? ""))
                if (int.TryParse(m.Groups[1].Value, out int id) && id > 0) yield return id;
        }

        // Still being encoded (a cleaning or painting stroke just ended): wait before sending what points at it.
        public static bool Busy(IEnumerable<int> ids) => service != null && ids.Any(service.IsProcessing);

        // Before data that points at these textures leaves: send the ones the other side lacks or has older.
        public static void SendFor(IEnumerable<int> ids)
        {
            if (service == null || !Playing) return;
            var cache = Cache(service);
            foreach (var id in ids.Distinct())
            {
                if (!cache.TryGetValue(id, out var bytes) || bytes == null) continue;
                if (known.TryGetValue(id, out var had) && ReferenceEquals(had, bytes)) continue;
                known[id] = bytes;
                Sent++;
                SentBytes += bytes.Length;
                session.SendAction("tex", Packet.Build(w => { w.Write(id); w.Write(bytes.Length); w.Write(bytes); }));
            }
        }

        private static void Put(int id, byte[] bytes)
        {
            if (service == null || id <= 0) return;
            Cache(service)[id] = bytes;
            Marked(service).Add(id); // saved with the game like its own
            known[id] = bytes;       // and not sent back
        }

        // Replacing a device destroys its textures with it; keep those the new copy points at.
        public static Dictionary<int, byte[]> Keep(IEnumerable<int> ids)
        {
            var kept = new Dictionary<int, byte[]>();
            if (service == null) return kept;
            var cache = Cache(service);
            foreach (var id in ids) if (cache.TryGetValue(id, out var b)) kept[id] = b;
            return kept;
        }

        public static void Restore(Dictionary<int, byte[]> kept)
        {
            if (service == null) return;
            var cache = Cache(service);
            foreach (var kv in kept)
            {
                if (cache.ContainsKey(kv.Key)) continue;
                cache[kv.Key] = kv.Value;
                Marked(service).Add(kv.Key);
            }
        }

        // After the save's textures are loaded: what both have, and this side's range of new numbers.
        [HarmonyPatch(typeof(TextureCacheService), nameof(TextureCacheService.DeserializeTextureData))]
        private static class AfterLoad
        {
            private static void Postfix(TextureCacheService __instance)
            {
                service = __instance;
                known.Clear();
                var cache = Cache(__instance);
                foreach (var kv in cache) known[kv.Key] = kv.Value;
                if (!Playing) return;
                int next = session.IsHost
                    ? cache.Keys.Where(k => k < GuestBase).DefaultIfEmpty(0).Max()
                    : Math.Max(GuestBase, cache.Keys.Where(k => k >= GuestBase).DefaultIfEmpty(GuestBase).Max());
                Next(__instance) = next;
                Plugin.Log.LogInfo("co-op textures: " + cache.Count + " loaded, new numbers from " + (next + 1) + (session.IsHost ? " (host)" : " (guest)"));
            }
        }

        // The service also exists before any save loads (a new game): capture it however it shows up.
        [HarmonyPatch(typeof(TextureCacheService), nameof(TextureCacheService.TryGetTextureData))]
        private static class Capture
        {
            private static void Postfix(TextureCacheService __instance) => service = __instance;
        }

        // ----- test bot -----

        // Guest: "clean" a part of this device, i.e. give its first installed part a fresh dirt texture.
        public static string BotDirty(DeviceContainer d)
        {
            if (service == null || d == null) return "no texture service or device";
            var element = d.CachedInstalledElements?.FirstOrDefault(e => e != null);
            if (element == null) return "no installed part";
            var bytes = new byte[2048];
            new System.Random(element.Info.GetHashCode()).NextBytes(bytes);
            element.DirtMaskTextureId = 0;
            service.CacheTextureData(element, bytes);
            BotTexture = element.DirtMaskTextureId + ":" + Hash(bytes);
            return "part texture " + BotTexture;
        }

        // Either side: the hash of the texture under that number here, for the test to compare.
        public static string HashOf(int id)
        {
            if (service == null || !Cache(service).TryGetValue(id, out var b) || b == null) return "missing";
            return Hash(b);
        }

        // Numbers in the guest's range this copy holds, as "id:hash" (for the test).
        public static string GuestRange()
        {
            if (service == null) return "";
            return string.Join(",", Cache(service).Where(kv => kv.Key >= GuestBase && kv.Value != null).OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + Hash(kv.Value)).ToArray());
        }

        private static string Hash(byte[] b)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
    }
}
