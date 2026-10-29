using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RestoryVersus.Core
{
    public interface ITransport : IDisposable
    {
        bool IsHost { get; }
        // Protocol version seen from someone running a different version of the mod, or -1.
        int ForeignVersion { get; }
        // Host: drop the current peer so a new one (or the same player from a new game) can join.
        void ForgetPeer();
        bool HasPeer { get; }
        bool PeerTimedOut { get; }
        long RttMs { get; }
        // Reliable messages sent and not acknowledged yet (flow control for bulk transfers).
        int PendingReliableCount { get; }
        void SendReliable(MsgKind kind, byte[] payload);
        void SendUnreliable(MsgKind kind, byte[] payload);
        // Main thread: resend reliable messages, return what arrived since last call.
        List<Packet> Poll(long nowMs);
        // How the players are connected, for the menu ("UDP 27515", "Steam").
        string Route { get; }
    }

    // Where datagrams actually go: a UDP socket (an address and port per player) or Steam (an account per player).
    // Peers are opaque objects compared with Equals. TryReceive is called on the main thread.
    public interface IDatagramLink : IDisposable
    {
        string Route { get; }
        void Send(object to, byte[] data);
        bool TryReceive(out byte[] data, out object from);
    }

    // Two peers with a tiny reliability layer (resend until acked, dedupe by seq) over any datagram link.
    // Host adopts the first peer that says Hello; client knows the host.
    public sealed class NetTransport : ITransport
    {
        public const long PeerTimeoutMs = 5000;
        public const long HeartbeatIntervalMs = 500;

        private sealed class Pending
        {
            public Packet Packet;
            public long FirstSentMs = -1;
            public long LastSentMs;
            public bool Resent;
            public int Resends;
        }

        // Flow control. Over the internet a round trip can take longer than a fixed resend interval, and resending
        // everything unacked on a timer sent each datagram two or three times and filled a host's upload in the
        // first real two-PC game. Now: at most MaxInFlight unacked datagrams are out at once (the rest wait their
        // turn), a datagram is resent only after a timeout taken from the measured round trips, and each further
        // resend of the same datagram waits twice as long.
        public const int MaxInFlight = 32;
        public const long MinResendMs = 120;
        public const long MaxResendMs = 2000;

        // Traffic, for the log and the status file.
        public long BytesSent { get; private set; }
        public long BytesReceived { get; private set; }
        public long PacketsSent { get; private set; }
        public long PacketsResent { get; private set; }

        private readonly IDatagramLink link;
        // Sorted, so resends go out in sequence order.
        private readonly SortedDictionary<uint, Pending> pending = new SortedDictionary<uint, Pending>();
        // Reliable messages are delivered in sequence order: one that arrives early waits here until the gap
        // is filled. (Without it a resent "device + Normal" could land after a newer "device + Hardcore".)
        private readonly SortedDictionary<uint, Packet> early = new SortedDictionary<uint, Packet>();
        private uint expectedSeq = 1;
        private const int MaxEarly = 256;
        private readonly bool isHost;
        private object peer;
        private uint nextSeq = 1;
        private long lastHeardMs = -1;
        private long lastHeartbeatMs;
        private long lastPollMs;
        private bool closed;
        private int foreignVersion = -1;

        public int ForeignVersion => foreignVersion;

        // Test hook: drop this fraction of outgoing datagrams (0..1).
        public double SendLossRate;
        private readonly Random lossRandom = new Random(1234);

        private readonly long[] rttSamples = new long[16];
        private int rttNext, rttCount;
        private long minRttMs = -1;
        private long peerStamp = -1;
        private long peerStampHeardMs;

        public bool IsHost => isHost;
        // Minimum of the last 16 round-trip samples (heartbeat echoes, and reliable sends acked on their
        // first try); 0 until measured.
        public long RttMs => minRttMs < 0 ? 0 : minRttMs;
        public bool HasPeer => peer != null;
        public bool PeerTimedOut => lastHeardMs >= 0 && lastPollMs - lastHeardMs > PeerTimeoutMs;
        public int LocalPort => link is UdpLink u ? u.Port : 0;
        public int PendingReliableCount => pending.Count;
        public string Route => link.Route;
        public IDatagramLink Link => link;

        private NetTransport(IDatagramLink link, bool isHost, object peer)
        {
            this.link = link;
            this.isHost = isHost;
            this.peer = peer;
        }

        public static NetTransport Host(int port) => new NetTransport(UdpLink.Bind(port), true, null);

        public static NetTransport Connect(string host, int port)
        {
            var addresses = Dns.GetHostAddresses(host);
            IPAddress address = null;
            foreach (var a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetwork) { address = a; break; }
            if (address == null) throw new ArgumentException("no IPv4 address for " + host);
            return new NetTransport(UdpLink.Bind(0), false, new IPEndPoint(address, port));
        }

        // Over another link (Steam): the host waits for a Hello, the client is given the host's peer object.
        public static NetTransport Over(IDatagramLink link, bool isHost, object hostPeer) => new NetTransport(link, isHost, isHost ? null : hostPeer);

        public void SendReliable(MsgKind kind, byte[] payload)
        {
            var p = new Packet { Kind = kind, Seq = nextSeq++, Payload = payload };
            pending[p.Seq] = new Pending { Packet = p, LastSentMs = long.MinValue / 2 };
        }

        public void SendUnreliable(MsgKind kind, byte[] payload)
        {
            if (peer == null) return;
            Send(new Packet { Kind = kind, Payload = payload });
        }

        public List<Packet> Poll(long nowMs)
        {
            lastPollMs = nowMs;
            var result = new List<Packet>();
            while (!closed && link.TryReceive(out var data, out var from))
            {
                BytesReceived += data.Length;
                var p = Packet.Decode(data, data.Length);
                if (p == null)
                {
                    int v = Packet.PeekVersion(data, data.Length);
                    if (v >= 0 && v != Packet.ProtocolVersion) foreignVersion = v;
                    continue;
                }
                var item = (packet: p, from);
                if (peer == null)
                {
                    // Host: only a Hello (versus or co-op) may open the session.
                    if (!isHost || !IsHello(p.Kind)) continue;
                    peer = item.from;
                }
                else if (!peer.Equals(item.from))
                {
                    // Two-player only. A Hello from a new address replaces a peer that has gone silent
                    // (its game crashed or was restarted); anything else from a third party is ignored.
                    if (!(isHost && IsHello(p.Kind) && PeerTimedOut)) continue;
                    ForgetPeer();
                    peer = item.from;
                }
                lastHeardMs = nowMs;
                if (p.Ack != 0 && pending.TryGetValue(p.Ack, out var acked))
                {
                    pending.Remove(p.Ack);
                    if (!acked.Resent && acked.FirstSentMs >= 0) AddRttSample(nowMs - acked.FirstSentMs);
                }
                if (p.Kind == MsgKind.Ack) continue;
                if (p.Seq == 0)
                {
                    Deliver(p, nowMs, result);
                    continue;
                }
                Send(new Packet { Kind = MsgKind.Ack, Ack = p.Seq });
                if (p.Seq < expectedSeq || early.ContainsKey(p.Seq)) continue; // duplicate after a lost ack
                if (p.Seq > expectedSeq)
                {
                    if (early.Count < MaxEarly) early[p.Seq] = p;
                    continue;
                }
                Deliver(p, nowMs, result);
                expectedSeq++;
                while (early.TryGetValue(expectedSeq, out var next))
                {
                    early.Remove(expectedSeq);
                    Deliver(next, nowMs, result);
                    expectedSeq++;
                }
            }

            if (peer != null)
            {
                long timeout = ResendTimeoutMs;
                int index = 0;
                foreach (var entry in pending.Values)
                {
                    if (index++ >= MaxInFlight) break; // the rest waits for acks
                    if (entry.FirstSentMs >= 0)
                    {
                        long wait = Math.Min(MaxResendMs, timeout << Math.Min(entry.Resends, 4));
                        if (nowMs - entry.LastSentMs < wait) continue;
                        entry.Resent = true;
                        entry.Resends++;
                        PacketsResent++;
                    }
                    else entry.FirstSentMs = nowMs;
                    entry.LastSentMs = nowMs;
                    Send(entry.Packet);
                }
                if (nowMs - lastHeartbeatMs >= HeartbeatIntervalMs)
                {
                    lastHeartbeatMs = nowMs;
                    long echo = peerStamp, held = peerStamp >= 0 ? nowMs - peerStampHeardMs : 0;
                    Send(new Packet { Kind = MsgKind.Heartbeat, Payload = Packet.Build(w =>
                    {
                        w.Write(nowMs);
                        w.Write(echo);
                        w.Write(held);
                    }) });
                }
            }
            return result;
        }

        private static bool IsHello(MsgKind kind) => kind == MsgKind.Hello || kind == MsgKind.CoopHello;

        // Heartbeats carry [our clock][the peer's last clock we heard][how long we held it]; an echo of
        // our own clock gives a round trip with the peer's holding time removed.
        private void ReadHeartbeat(Packet p, long nowMs)
        {
            if (p.Payload.Length < 24) return;
            using (var r = p.Reader())
            {
                peerStamp = r.ReadInt64();
                peerStampHeardMs = nowMs;
                long echo = r.ReadInt64();
                long held = r.ReadInt64();
                if (echo >= 0) AddRttSample(nowMs - echo - held);
            }
        }

        // Stalls (loading, a busy frame) only ever add delay, so the minimum of recent samples is the
        // best estimate. An average let multi-second loading frames inflate it to 755 ms on loopback.
        private void AddRttSample(long sample)
        {
            if (sample < 0) sample = 0;
            rttSamples[rttNext] = sample;
            rttNext = (rttNext + 1) % rttSamples.Length;
            if (rttCount < rttSamples.Length) rttCount++;
            long min = long.MaxValue, max = 0;
            for (int i = 0; i < rttCount; i++)
            {
                if (rttSamples[i] < min) min = rttSamples[i];
                if (rttSamples[i] > max) max = rttSamples[i];
            }
            minRttMs = min;
            maxRttMs = max;
        }

        // How long an unacked datagram waits before its first resend: the slowest recent round trip with a margin
        // (a resend that comes before the ack could is wasted upload). Unknown yet: a cautious 300 ms.
        private long maxRttMs = -1;
        public long ResendTimeoutMs => maxRttMs < 0 ? 300 : Math.Max(MinResendMs, Math.Min(MaxResendMs, maxRttMs * 3 / 2 + 50));

        private void Deliver(Packet p, long nowMs, List<Packet> result)
        {
            if (p.Kind == MsgKind.Heartbeat) ReadHeartbeat(p, nowMs);
            else result.Add(p);
        }

        public void ForgetPeer()
        {
            if (!isHost) return;
            peer = null;
            pending.Clear();
            early.Clear();
            expectedSeq = 1;
            nextSeq = 1; // a new peer expects our numbering to start at 1
            rttCount = 0;
            rttNext = 0;
            minRttMs = -1;
            maxRttMs = -1;
            peerStamp = -1;
            lastHeardMs = -1;
        }

        private void Send(Packet p)
        {
            if (closed || peer == null) return;
            if (SendLossRate > 0 && lossRandom.NextDouble() < SendLossRate) return;
            var bytes = p.Encode();
            BytesSent += bytes.Length;
            PacketsSent++;
            link.Send(peer, bytes);
        }

        public void Dispose()
        {
            closed = true;
            link.Dispose();
        }
    }

    // UDP: receives run on a background thread into a queue the main thread drains.
    public sealed class UdpLink : IDatagramLink
    {
        private readonly UdpClient udp;
        private readonly ConcurrentQueue<(byte[] data, IPEndPoint from)> inbox = new ConcurrentQueue<(byte[], IPEndPoint)>();
        private volatile bool closed;

        public int Port => ((IPEndPoint)udp.Client.LocalEndPoint).Port;
        public string Route => "UDP " + Port;

        private UdpLink(UdpClient udp)
        {
            this.udp = udp;
            // Windows reports ICMP port unreachable as a receive error; ignore it so a
            // client started before its host keeps listening.
            try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
            new Thread(ReceiveLoop) { IsBackground = true, Name = "RestoryVersus.Udp" }.Start();
        }

        public static UdpLink Bind(int port) => new UdpLink(new UdpClient(new IPEndPoint(IPAddress.Any, port)));

        public void Send(object to, byte[] data)
        {
            if (closed || !(to is IPEndPoint ep)) return;
            try { udp.Send(data, data.Length, ep); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        public bool TryReceive(out byte[] data, out object from)
        {
            if (inbox.TryDequeue(out var item)) { data = item.data; from = item.from; return true; }
            data = null; from = null;
            return false;
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!closed)
            {
                try
                {
                    var from = any;
                    var data = udp.Receive(ref from);
                    inbox.Enqueue((data, from));
                }
                catch (SocketException) { if (closed) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        public void Dispose()
        {
            closed = true;
            udp.Close();
        }
    }

    // Players known by an account id (Steam) instead of an address. Each run of the game has a random number, and
    // every datagram carries the sender's run and the run it is meant for (0 = not known yet: a client's words to
    // its host). A friend who restarted the game is a new peer to the host (as a new UDP port would be), and what
    // the host still sends to the old run is dropped by the new one instead of mixing into its numbering.
    // A client only listens to its host.
    public abstract class IdLink : IDatagramLink
    {
        public sealed class Peer
        {
            public readonly ulong Id;
            public readonly uint Run;
            public Peer(ulong id, uint run) { Id = id; Run = run; }
            public override bool Equals(object o) => o is Peer p && p.Id == Id && p.Run == Run;
            public override int GetHashCode() => Id.GetHashCode() ^ (int)Run;
            public override string ToString() => Id + "/" + Run;
        }

        private readonly uint run;
        private readonly Peer host; // client side: the only peer heard, whatever run it is on

        public abstract string Route { get; }

        protected IdLink(ulong hostId)
        {
            run = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0) | 1u;
            host = hostId != 0 ? new Peer(hostId, 0) : null;
        }

        // The peer object a client hands to NetTransport.Over.
        public Peer HostPeer => host;

        protected abstract void SendRaw(ulong to, byte[] data);
        protected abstract bool ReceiveRaw(out ulong from, out byte[] data);

        public void Send(object to, byte[] data)
        {
            if (!(to is Peer p)) return;
            var framed = new byte[data.Length + 8];
            BitConverter.GetBytes(run).CopyTo(framed, 0);
            BitConverter.GetBytes(p.Run).CopyTo(framed, 4);
            Buffer.BlockCopy(data, 0, framed, 8, data.Length);
            SendRaw(p.Id, framed);
        }

        public bool TryReceive(out byte[] data, out object from)
        {
            while (ReceiveRaw(out var id, out var framed))
            {
                if (framed == null || framed.Length < 8) continue;
                if (host != null && id != host.Id) continue;
                uint to = BitConverter.ToUInt32(framed, 4);
                if (to != 0 && to != run) continue; // meant for an earlier run of this game
                data = new byte[framed.Length - 8];
                Buffer.BlockCopy(framed, 8, data, 0, data.Length);
                from = host ?? new Peer(id, BitConverter.ToUInt32(framed, 0));
                return true;
            }
            data = null; from = null;
            return false;
        }

        public abstract void Dispose();
    }
}
