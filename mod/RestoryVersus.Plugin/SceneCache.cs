using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace RestoryVersus
{
    // Scene lookups for objects the mod asks about every frame. FindAnyObjectByType with inactive objects
    // walks the whole scene; the mod used to do that several times per frame (bench, workshop, main menu
    // checks). Found objects are kept until Unity destroys them; a missing one is searched for at most once
    // per second.
    internal static class SceneCache
    {
        private sealed class Entry
        {
            public UnityEngine.Object Obj;
            public float NextTry;
        }

        private static readonly Dictionary<Type, Entry> entries = new Dictionary<Type, Entry>();

        public static T Find<T>(bool includeInactive) where T : UnityEngine.Object
        {
            if (!entries.TryGetValue(typeof(T), out var e)) entries[typeof(T)] = e = new Entry();
            if (e.Obj != null) return (T)e.Obj; // Unity's null check: false once the object is destroyed
            float now = Time.realtimeSinceStartup;
            if (now < e.NextTry) return null;
            e.NextTry = now + 1f;
            e.Obj = UnityEngine.Object.FindAnyObjectByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
            return (T)e.Obj;
        }
    }

    // How much of each frame the mod itself takes. Logged every 30 s (and in the status file), so lag can be
    // put on the mod or on the game with numbers.
    internal static class Perf
    {
        private static readonly Stopwatch watch = new Stopwatch();
        private static double modMsTotal, modMsMax, frameMsTotal;
        private static int frames;
        private static float nextLog = 30f;

        public static double LastModMsAvg { get; private set; }
        public static double LastFrameMsAvg { get; private set; }

        public static void Begin() => watch.Restart();

        public static void End()
        {
            watch.Stop();
            double ms = watch.Elapsed.TotalMilliseconds;
            modMsTotal += ms;
            if (ms > modMsMax) modMsMax = ms;
        }

        // Once per frame, from the plugin.
        public static void Frame()
        {
            frames++;
            frameMsTotal += Time.unscaledDeltaTime * 1000.0;
            if (Time.realtimeSinceStartup < nextLog) return;
            nextLog = Time.realtimeSinceStartup + 30f;
            if (frames == 0) return;
            LastModMsAvg = modMsTotal / frames;
            LastFrameMsAvg = frameMsTotal / frames;
            Plugin.Log.LogInfo($"perf: mod {LastModMsAvg:0.000} ms/frame (max {modMsMax:0.00} ms), frame {LastFrameMsAvg:0.0} ms ({1000.0 / LastFrameMsAvg:0} fps) over {frames} frames");
            LogNet(Plugin.Instance?.Coop?.Transport, "co-op");
            LogNet(Plugin.Instance?.Transport, "versus");
            modMsTotal = modMsMax = frameMsTotal = 0;
            frames = 0;
        }

        // Network use since the last line, so a player's log shows what the mod sends (the first two-PC game
        // filled the host's upload; this is what to look at if that ever comes back).
        private static RestoryVersus.Core.NetTransport netSeen;
        private static long sentSeen, receivedSeen, packetsSeen, resentSeen;
        private static float netSince;

        private static void LogNet(RestoryVersus.Core.NetTransport t, string what)
        {
            if (t == null) return;
            if (!ReferenceEquals(t, netSeen)) { netSeen = t; sentSeen = receivedSeen = packetsSeen = resentSeen = 0; netSince = Time.realtimeSinceStartup - 30f; }
            float seconds = Math.Max(1f, Time.realtimeSinceStartup - netSince);
            long packets = t.PacketsSent - packetsSeen, resent = t.PacketsResent - resentSeen;
            Plugin.Log.LogInfo($"net ({what}, {t.Route}): sent {(t.BytesSent - sentSeen) / 1024.0 / seconds:0.0} KB/s, received {(t.BytesReceived - receivedSeen) / 1024.0 / seconds:0.0} KB/s, resent {resent} of {packets} datagrams, rtt {t.RttMs} ms, waiting {t.PendingReliableCount}");
            sentSeen = t.BytesSent; receivedSeen = t.BytesReceived; packetsSeen = t.PacketsSent; resentSeen = t.PacketsResent;
            netSince = Time.realtimeSinceStartup;
        }
    }
}
