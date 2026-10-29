using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace RestoryVersus.Core
{
    // Lobby: the guest joins. Transfer: the host's save travels to the guest. Launch: both load it.
    public enum CoopStage : byte { Lobby = 0, Transfer = 1, Launch = 2 }

    // Co-op step 1: the guest joins the host's save. The host sends the save it is about to load (the file as
    // it is on disk, in chunks over the reliable channel), the guest checks its SHA-256, then both load it and
    // report what they see (object count and a hash of the object ids) so the two shops can be compared.
    // Pure logic like RaceSession: the plugin feeds it, a transport carries it, tests drive both sides.
    public sealed class CoopSession
    {
        public const int ChunkSize = 1000;
        public const int Window = 32;                  // chunks in flight
        public const int MaxSaveBytes = 16 * 1024 * 1024;
        public const long HostStateIntervalMs = 250;
        private const int MaxNameLength = 24;
        private const int MaxIdLength = 128;

        private readonly ITransport transport;
        private string localName;
        private readonly string gameBuild;
        private bool helloSent;

        // host: the save being sent
        private byte[] outgoing;
        private int nextOffset;
        private int outgoingTotal;
        // guest: the save being received
        private byte[] incoming;
        private byte[] incomingHash;
        private int received;

        private int sentWorldCount = -1;
        private string sentWorldHash;

        // host: the shared state of the shop, sent while playing
        private bool hostStateSet;
        private long hostClockTicks;
        private bool hostClockRunning;
        private bool hostShuttersOpen;
        private int hostMoney;
        private long lastHostStateSentMs = long.MinValue / 2;
        private long newestHostStateStamp = long.MinValue;

        public ConnectionState Connection { get; private set; }
        public CoopStage Stage { get; private set; } = CoopStage.Lobby;
        public string LocalName => localName;
        public string LocalAvatar { get; private set; } = "";
        public string RemoteName { get; private set; } = "";
        public string RemoteAvatar { get; private set; } = "";
        public string RemoteGameBuild { get; private set; } = "";
        public bool PeerInVersus { get; private set; }
        // The other side said Bye (left on purpose), as opposed to going silent.
        public bool PeerLeft { get; private set; }
        public string LeftNotice() => PeerLeft ? "left" : "";
        public int MalformedMessages { get; private set; }
        public string Error { get; private set; } = "";
        public readonly List<string> Log = new List<string>();

        // Transfer progress in bytes (host: handed to the transport; guest: received).
        public int TransferDone => IsHost ? nextOffset : received;
        public int TransferTotal => IsHost ? outgoingTotal : (incoming?.Length ?? ReceivedSave?.Length ?? 0);
        public string SaveFileName { get; private set; } = "";
        // Guest: the verified save once it has arrived.
        public byte[] ReceivedSave { get; private set; }

        // What the other side reported from inside the shop (-1 = not there yet).
        public int RemoteWorldCount { get; private set; } = -1;
        public string RemoteWorldHash { get; private set; } = "";

        public event Action<string> OnLog;
        // Guest: the host's shop state (game clock ticks, clock running, shutters open); -1 ticks = none yet.
        public long HostClockTicks { get; private set; } = -1;
        public bool HostClockRunning { get; private set; }
        public bool HostShuttersOpen { get; private set; }
        public int HostMoney { get; private set; } = -1;
        public int HostStatesReceived { get; private set; }

        // Room objects (see ObjState): apply this state to the local world.
        public event Action<ObjState> OnApplyObject;
        // The object in the other player's hand right now (unreliable; a release comes as the action "release").
        public event Action<HeldPose> OnHeld;

        public void SendHeld(HeldPose p)
        {
            if (transport == null || Launches == 0 || p == null) return;
            transport.SendUnreliable(MsgKind.CoopHeld, Packet.Build(w => p.Write(w)));
        }

        // A named action from the other side (name, data), once both are playing.
        public event Action<string, byte[]> OnAction;
        public const int MaxActionData = 4 * 1024 * 1024;
        public int ObjectsSent { get; private set; }
        public int ObjectsApplied { get; private set; }
        // guest: own moves the host has not confirmed yet, by object id -> our sequence number
        private readonly Dictionary<string, int> pendingMoves = new Dictionary<string, int>();
        private int objectSeq;
        public int PendingMoves => pendingMoves.Count;
        // Guest: this object was moved here and the host has not confirmed it yet.
        public bool IsPending(string id) => pendingMoves.ContainsKey(id);

        // Guest: the save arrived intact (file name, bytes); install it before OnLaunch.
        public event Action<string, byte[]> OnSaveReceived;
        // Both: load the shop now.
        public event Action OnLaunch;
        // Host: a guest arrived while the host is already playing; send it the current save (Resync).
        public event Action OnGuestJoinedWhilePlaying;
        // Guest: the host ended the day and is on the end-of-day screens; cleared when the next day launches.
        public bool HostEndingDay { get; private set; }
        public int Launches { get; private set; }
        private bool resyncing;

        public bool IsHost => transport != null && transport.IsHost;
        // Launched at least once: both are (or are getting back) into the shared shop.
        public bool Playing => Launches > 0;
        public bool BuildMismatch => RemoteGameBuild != "" && RemoteGameBuild != gameBuild;
        public int ForeignModVersion => transport != null ? transport.ForeignVersion : -1;
        public bool CanStart => IsHost && Stage == CoopStage.Lobby && Connection == ConnectionState.Connected && !BuildMismatch && !PeerInVersus;

        public CoopSession(ITransport transport, string localName, string gameBuild)
        {
            this.transport = transport;
            this.localName = localName ?? "";
            this.gameBuild = gameBuild ?? "";
            Connection = transport == null ? ConnectionState.Offline : ConnectionState.Waiting;
        }

        private static string Clip(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max));

        public void SetLocalProfile(string name, string avatar)
        {
            localName = Clip(name, MaxNameLength);
            LocalAvatar = Clip(avatar, MaxIdLength);
            if (Connection == ConnectionState.Connected) SendPlayerInfo();
        }

        // Host: send this save (the file "Continue" is about to load) and launch once the guest has it.
        public bool StartWithSave(string fileName, byte[] save)
        {
            if (!CanStart) return false;
            resyncing = false;
            return BeginTransfer(fileName, save);
        }

        // Host, while playing: the guest reloads from this save (the next day after the end-of-day screens, or to
        // bring a drifted shop back in line). The host itself keeps playing.
        public bool Resync(string fileName, byte[] save)
        {
            if (!IsHost || Stage != CoopStage.Launch || Connection != ConnectionState.Connected) return false;
            resyncing = true;
            return BeginTransfer(fileName, save);
        }

        // Host: the day is over here; the guest waits for the next day's save.
        public void DayEnding()
        {
            if (IsHost && Stage == CoopStage.Launch && Connection == ConnectionState.Connected)
                transport.SendReliable(MsgKind.CoopDayEnd, Array.Empty<byte>());
        }

        private bool BeginTransfer(string fileName, byte[] save)
        {
            if (save == null || save.Length == 0 || save.Length > MaxSaveBytes) return false;
            outgoing = save;
            outgoingTotal = save.Length;
            nextOffset = 0;
            SaveFileName = Clip(fileName, 200);
            Error = "";
            Stage = CoopStage.Transfer;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(save);
            var name = SaveFileName;
            transport.SendReliable(MsgKind.CoopSaveBegin, Packet.Build(w =>
            {
                w.Write(name);
                w.Write(save.Length);
                w.Write(hash.Length);
                w.Write(hash);
            }));
            Emit("sending the save " + name + " (" + save.Length + " bytes)");
            return true;
        }

        // Both, from inside the shop: what this copy loaded. Sent when it changes.
        public void SetLocalWorld(int count, string hash)
        {
            if (transport == null || (count == sentWorldCount && hash == sentWorldHash)) return;
            sentWorldCount = count;
            sentWorldHash = hash ?? "";
            var h = sentWorldHash;
            transport.SendReliable(MsgKind.CoopWorld, Packet.Build(w => { w.Write(count); w.Write(h); }));
        }

        // Host, from inside the shop: the state the guest's shop follows.
        public void SetHostState(long clockTicks, bool clockRunning, bool shuttersOpen, int money = -1)
        {
            hostMoney = money;
            hostStateSet = true;
            hostClockTicks = clockTicks;
            hostClockRunning = clockRunning;
            hostShuttersOpen = shuttersOpen;
        }

        // A room object settled on this side after the local player moved it (already applied locally).
        // Host: tell the guest. Guest: ask the host, who applies it and confirms; until then the host's older news
        // about this object is ignored, so the guest's move is not undone by a message sent before it arrived.
        public void ReportLocalMove(ObjState s)
        {
            if (transport == null || Launches == 0 || s == null || string.IsNullOrEmpty(s.Id)) return;
            s.FromGuest = !IsHost;
            s.Seq = ++objectSeq;
            if (!IsHost) pendingMoves[s.Id] = s.Seq;
            SendObject(s);
        }

        public void SendAction(string name, byte[] data)
        {
            if (transport == null || Launches == 0 || string.IsNullOrEmpty(name) || data == null || data.Length > MaxActionData) return;
            SendBig(MsgKind.CoopAction, Packet.Build(w =>
            {
                w.Write(name);
                w.Write(data.Length);
                w.Write(data);
            }));
        }

        private void SendObject(ObjState s)
        {
            ObjectsSent++;
            SendBig(MsgKind.CoopObject, Packet.Build(w => s.Write(w)));
        }

        // Reliable messages arrive in order, so the pieces of one big message arrive one after another.
        public const int FragmentSize = 1000;
        private const int MaxBigMessage = MaxActionData + 4096;
        private byte[] assembling;
        private MsgKind assemblingKind;
        private int assembled;

        private void SendBig(MsgKind kind, byte[] payload)
        {
            if (payload.Length <= FragmentSize + 100) { transport.SendReliable(kind, payload); return; }
            for (int offset = 0; offset < payload.Length; offset += FragmentSize)
            {
                int o = offset, len = Math.Min(FragmentSize, payload.Length - offset);
                transport.SendReliable(MsgKind.CoopFragment, Packet.Build(w =>
                {
                    w.Write((byte)kind);
                    w.Write(payload.Length);
                    w.Write(o);
                    w.Write(len);
                    w.Write(payload, o, len);
                }));
            }
        }

        private void ReadFragment(System.IO.BinaryReader r)
        {
            var kind = (MsgKind)r.ReadByte();
            int total = r.ReadInt32();
            int offset = r.ReadInt32();
            int len = r.ReadInt32();
            if (total <= 0 || total > MaxBigMessage || len <= 0 || len > FragmentSize || offset + len > total) throw new ArgumentException("bad fragment");
            if (offset == 0) { assembling = new byte[total]; assemblingKind = kind; assembled = 0; }
            if (assembling == null || assembling.Length != total || assemblingKind != kind || offset != assembled) throw new ArgumentException("fragment out of place");
            var data = r.ReadBytes(len);
            if (data.Length != len) throw new System.IO.EndOfStreamException();
            Buffer.BlockCopy(data, 0, assembling, offset, len);
            assembled += len;
            if (assembled < total) return;
            var whole = new Packet { Kind = kind, Payload = assembling };
            assembling = null;
            HandleUnchecked(whole);
        }

        private void Apply(ObjState s)
        {
            ObjectsApplied++;
            OnApplyObject?.Invoke(s);
        }

        public void Tick(long nowMs)
        {
            if (transport == null) return;
            if (!helloSent)
            {
                helloSent = true;
                SendHello();
            }

            foreach (var p in transport.Poll(nowMs))
                Handle(p);

            if (Connection == ConnectionState.Connected && transport.PeerTimedOut)
            {
                Connection = ConnectionState.Lost;
                Emit("the other player timed out");
                if (Stage == CoopStage.Transfer) AbortTransfer("transferLost", "connection lost during the transfer");
            }
            else if (Connection == ConnectionState.Lost && !PeerLeft && transport.HasPeer && !transport.PeerTimedOut && RemoteGameBuild != "")
            {
                // Only silent, not gone (a window that froze, a long load): heard again, so connected again.
                Connection = ConnectionState.Connected;
                Emit("the other player is back");
            }

            if (IsHost && hostStateSet && Launches > 0 && Connection == ConnectionState.Connected
                && nowMs - lastHostStateSentMs >= HostStateIntervalMs)
            {
                lastHostStateSentMs = nowMs;
                long ticks = hostClockTicks, stamp = nowMs;
                bool running = hostClockRunning, shutters = hostShuttersOpen;
                int money = hostMoney;
                transport.SendUnreliable(MsgKind.CoopHostState, Packet.Build(w =>
                {
                    w.Write(stamp);
                    w.Write(ticks);
                    w.Write(running);
                    w.Write(shutters);
                    w.Write(money);
                }));
            }

            // Host: feed the save into the reliable channel, a window at a time.
            if (IsHost && Stage == CoopStage.Transfer && outgoing != null)
            {
                while (nextOffset < outgoing.Length && transport.PendingReliableCount < Window)
                {
                    int offset = nextOffset;
                    int len = Math.Min(ChunkSize, outgoing.Length - offset);
                    var data = outgoing;
                    transport.SendReliable(MsgKind.CoopSaveChunk, Packet.Build(w =>
                    {
                        w.Write(offset);
                        w.Write(len);
                        w.Write(data, offset, len);
                    }));
                    nextOffset += len;
                }
            }
        }

        public void Leave()
        {
            transport?.SendReliable(MsgKind.Bye, Array.Empty<byte>());
        }

        private void SendHello()
        {
            var n = localName;
            var b = gameBuild;
            transport.SendReliable(MsgKind.CoopHello, Packet.Build(w => { w.Write(n); w.Write(b); }));
        }

        private void SendPlayerInfo()
        {
            var n = localName;
            var a = LocalAvatar;
            transport.SendReliable(MsgKind.PlayerInfo, Packet.Build(w => { w.Write(n); w.Write(a); }));
        }

        // code: what the menu shows (an error code); why: what the log says.
        private void AbortTransfer(string code, string why)
        {
            outgoing = null;
            incoming = null;
            nextOffset = 0;
            received = 0;
            Stage = CoopStage.Lobby;
            Error = code;
            Emit(why);
        }

        private void Handle(Packet p)
        {
            try
            {
                HandleUnchecked(p);
            }
            catch (Exception e) when (e is System.IO.EndOfStreamException || e is System.IO.IOException || e is ArgumentException || e is FormatException || e is OverflowException)
            {
                MalformedMessages++;
                if (MalformedMessages <= 5) Emit("ignored a malformed " + p.Kind + " message (" + e.GetType().Name + ")");
            }
        }

        private void HandleUnchecked(Packet p)
        {
            using (var r = p.Reader())
            {
                switch (p.Kind)
                {
                    case MsgKind.CoopHello:
                        RemoteName = Clip(r.ReadString(), MaxNameLength);
                        RemoteGameBuild = Clip(r.ReadString(), MaxIdLength);
                        if (Connection != ConnectionState.Connected) Emit("connected: " + RemoteName);
                        Connection = ConnectionState.Connected;
                        PeerInVersus = false;
                        PeerLeft = false;
                        if (!IsHost) newestHostStateStamp = long.MinValue; // a restarted host counts from zero
                        pendingMoves.Clear();
                        if (IsHost)
                        {
                            // A (new) guest. A transfer to an earlier guest is void; a running game stays running
                            // (joining a game in progress comes later).
                            if (Stage == CoopStage.Transfer)
                            {
                                if (Playing) { Stage = CoopStage.Launch; outgoing = null; resyncing = false; }
                                else AbortTransfer("guestRejoined", "the guest reconnected: press Start again");
                            }
                            RemoteWorldCount = -1;
                            RemoteWorldHash = "";
                            SendHello();
                            if (Playing && !BuildMismatch) OnGuestJoinedWhilePlaying?.Invoke();
                        }
                        SendPlayerInfo();
                        if (BuildMismatch) Emit("WARNING game build differs: " + RemoteGameBuild);
                        break;
                    case MsgKind.Hello:
                        // A versus player: tell them we are co-op, do not play with them.
                        if (!PeerInVersus) Emit("the other player is in versus mode");
                        PeerInVersus = true;
                        Connection = ConnectionState.Connected;
                        if (IsHost) SendHello();
                        break;
                    case MsgKind.PlayerInfo:
                        RemoteName = Clip(r.ReadString(), MaxNameLength);
                        RemoteAvatar = Clip(r.ReadString(), MaxIdLength);
                        break;
                    case MsgKind.Bye:
                        Connection = ConnectionState.Lost;
                        PeerLeft = true;
                        RemoteWorldCount = -1;
                        RemoteWorldHash = "";
                        if (IsHost) transport.ForgetPeer();
                        if (Stage == CoopStage.Transfer) AbortTransfer("transferLost", "the other player left during the transfer");
                        Emit("the other player left");
                        break;
                    case MsgKind.CoopSaveBegin:
                    {
                        var name = Clip(r.ReadString(), 200);
                        int length = r.ReadInt32();
                        int hashLen = r.ReadInt32();
                        var hash = r.ReadBytes(hashLen);
                        if (IsHost) break;
                        if (length <= 0 || length > MaxSaveBytes || hash.Length != 32) throw new ArgumentException("bad save header");
                        SaveFileName = name;
                        pendingMoves.Clear(); // a new save replaces whatever was in flight
                        incoming = new byte[length];
                        incomingHash = hash;
                        received = 0;
                        ReceivedSave = null;
                        Error = "";
                        Stage = CoopStage.Transfer;
                        Emit("receiving the host's save " + name + " (" + length + " bytes)");
                        break;
                    }
                    case MsgKind.CoopSaveChunk:
                    {
                        int offset = r.ReadInt32();
                        int len = r.ReadInt32();
                        if (IsHost || incoming == null) break;
                        // Reliable messages arrive in order, so each chunk continues where the last one ended.
                        if (offset != received || len <= 0 || len > ChunkSize || offset + len > incoming.Length) throw new ArgumentException("chunk out of place");
                        var data = r.ReadBytes(len);
                        if (data.Length != len) throw new System.IO.EndOfStreamException();
                        Buffer.BlockCopy(data, 0, incoming, offset, len);
                        received += len;
                        if (received == incoming.Length) FinishReceiving();
                        break;
                    }
                    case MsgKind.CoopSaveDone:
                    {
                        bool ok = r.ReadBoolean();
                        if (!IsHost || Stage != CoopStage.Transfer) break;
                        if (!ok) { AbortTransfer("saveDamaged", "the guest got a damaged save: press Start again"); break; }
                        outgoing = null;
                        Stage = CoopStage.Launch;
                        transport.SendReliable(MsgKind.CoopLaunch, Array.Empty<byte>());
                        if (resyncing)
                        {
                            // The host is already in the shop: only the guest loads.
                            resyncing = false;
                            Emit("the guest has the new save: it reloads");
                            break;
                        }
                        Emit("the guest has the save: launching");
                        Launches++;
                        OnLaunch?.Invoke();
                        break;
                    }
                    case MsgKind.CoopDayEnd:
                        if (IsHost) break;
                        HostEndingDay = true;
                        Emit("the host ended the day");
                        break;
                    case MsgKind.CoopLaunch:
                        if (IsHost || ReceivedSave == null) break;
                        Stage = CoopStage.Launch;
                        HostEndingDay = false;
                        Launches++;
                        Emit("the host launched");
                        OnLaunch?.Invoke();
                        break;
                    case MsgKind.CoopHostState:
                    {
                        long stamp = r.ReadInt64();
                        long ticks = r.ReadInt64();
                        bool running = r.ReadBoolean();
                        bool shutters = r.ReadBoolean();
                        int money = r.ReadInt32();
                        // Unreliable: an older state arriving late must not wind the clock back.
                        if (IsHost || stamp <= newestHostStateStamp) break;
                        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new ArgumentException("bad clock");
                        newestHostStateStamp = stamp;
                        HostClockTicks = ticks;
                        HostClockRunning = running;
                        HostShuttersOpen = shutters;
                        HostMoney = money;
                        HostStatesReceived++;
                        break;
                    }
                    case MsgKind.CoopAction:
                    {
                        var name = Clip(r.ReadString(), 64);
                        int len = r.ReadInt32();
                        if (len < 0 || len > MaxActionData) throw new ArgumentException("bad action length");
                        var data = r.ReadBytes(len);
                        if (data.Length != len) throw new System.IO.EndOfStreamException();
                        if (Launches == 0) break;
                        OnAction?.Invoke(name, data);
                        break;
                    }
                    case MsgKind.CoopHeld:
                    {
                        var pose = HeldPose.Read(r);
                        if (Launches == 0) break;
                        OnHeld?.Invoke(pose);
                        break;
                    }
                    case MsgKind.CoopFragment:
                        ReadFragment(r);
                        break;
                    case MsgKind.CoopObject:
                    {
                        var o = ObjState.Read(r);
                        if (Launches == 0) break;
                        if (IsHost)
                        {
                            // The guest's move: the host's world takes it, then the guest hears it back as the outcome.
                            if (!o.FromGuest) break;
                            Apply(o);
                            SendObject(o);
                        }
                        else if (o.FromGuest)
                        {
                            // The host confirming one of our moves (already applied here).
                            if (pendingMoves.TryGetValue(o.Id, out var seq) && seq == o.Seq) pendingMoves.Remove(o.Id);
                        }
                        else if (!pendingMoves.ContainsKey(o.Id))
                        {
                            // The host's own move. Skipped while our later move of the same object is on its way:
                            // the host applies ours after this one, so ours is the outcome on both sides.
                            Apply(o);
                        }
                        break;
                    }
                    case MsgKind.CoopWorld:
                        RemoteWorldCount = r.ReadInt32();
                        RemoteWorldHash = Clip(r.ReadString(), MaxIdLength);
                        Emit("the other player is in the shop: " + RemoteWorldCount + " objects, ids " + RemoteWorldHash);
                        break;
                }
            }
        }

        private void FinishReceiving()
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(incoming);
            bool ok = hash.Length == incomingHash.Length;
            for (int i = 0; ok && i < hash.Length; i++) ok = hash[i] == incomingHash[i];
            transport.SendReliable(MsgKind.CoopSaveDone, Packet.Build(w => w.Write(ok)));
            if (!ok)
            {
                AbortTransfer("saveDamaged", "the save arrived damaged");
                return;
            }
            ReceivedSave = incoming;
            incoming = null;
            Emit("the save arrived intact");
            OnSaveReceived?.Invoke(SaveFileName, ReceivedSave);
        }

        private void Emit(string line)
        {
            Log.Add(line);
            if (Log.Count > 50) Log.RemoveAt(0);
            OnLog?.Invoke(line);
        }
    }
}
