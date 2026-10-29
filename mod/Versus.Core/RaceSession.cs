using System;
using System.Collections.Generic;

namespace RestoryVersus.Core
{
    public enum RacePhase : byte
    {
        Idle = 0,      // no contest device on the bench
        Prepared = 1,  // contest device placed, clock not running
        Racing = 2,
        Finished = 3,
        Abandoned = 4, // contest stopped without finishing
    }

    public enum ConnectionState { Offline, Waiting, Connected, Lost }

    // Normal: a part dropped outside its socket just stays on the table. Hardcore: the game's own contest
    // rule, the whole device comes apart again, plus a time penalty for each such reset.
    public enum RaceMode : byte { Normal = 0, Hardcore = 1 }

    // The lobby has two stages. Menu: players meet in the main menu, set name and avatar, the client gets
    // ready, and the host takes both to the versus workshop. Workshop: the host picks device and mode there
    // (the device list only exists in the workshop), the client gets ready again, the host starts.
    public enum LobbyStage : byte { Menu = 0, Workshop = 1 }

    public sealed class RaceResult
    {
        public bool Decided;
        public bool LocalWins;
        public bool Draw;
        public long LocalScoreMs;
        public long RemoteScoreMs;
        public string Reason = "";
    }

    // Owns the local racer's state, mirrors the opponent's, and decides the result.
    // Pure logic: the game feeds it events, a transport carries its messages, tests drive both.
    public sealed class RaceSession
    {
        public const long ProgressIntervalMs = 200;
        public const long ProgressKeepaliveMs = 1000;

        private readonly ITransport transport;
        private string localName;
        private readonly string gameBuild;
        private readonly long resetPenaltyMs;
        private long lastProgressSentMs = long.MinValue / 2;
        private RacerSnapshot lastSent;
        private long raceStartMs = -1;
        private bool helloSent;
        private long countdownAtMs = -1;
        private int raceCounter;   // host: last race number handed out
        private int pendingRaceNo; // race number the scheduled countdown belongs to
        private int startedRaceNo; // client: the last race whose countdown went off here
        private long lastStartRepeatMs;
        public const long StartRepeatMs = 100;
        private bool startArmed;   // the next ContestStarted comes from our countdown

        public const int StartDelayMs = 1500;

        public RacerSnapshot Local { get; } = new RacerSnapshot();
        public RacerSnapshot Remote { get; private set; }
        public string RemoteName { get; private set; } = "";
        public string RemoteGameBuild { get; private set; } = "";
        public ConnectionState Connection { get; private set; }
        public bool LocalReady { get; private set; }
        public bool RemoteReady { get; private set; }
        public string RemoteReadyDevice { get; private set; } = "";
        public bool CountdownScheduled => countdownAtMs >= 0;
        // Wall clock (UTC ms) when the last countdown fired; lets a same-PC test measure the real start gap.
        public long CountdownWallMs { get; private set; } = -1;
        public readonly List<string> Log = new List<string>();

        public event Action<string> OnLog;
        // Both copies fire this at (nearly) the same moment; the game then plays its own 3-2-1-GO.
        public event Action OnCountdown;
        // The host pressed Start in the lobby; fires on host and client with the chosen device.
        public event Action<string> OnLaunch;

        // ----- lobby -----
        // Host picks the device; the client sees it and marks itself ready for exactly that device.
        // Start is only possible while the client is ready for the current choice; changing the
        // device withdraws the client's ready.
        public LobbyStage Stage { get; private set; } = LobbyStage.Menu;
        public string LocalName => localName;
        public string LocalAvatar { get; private set; } = "";
        public string RemoteAvatar { get; private set; } = "";
        public string LobbyDevice { get; private set; } = "";
        public RaceMode LobbyMode { get; private set; } = RaceMode.Normal;
        // Rules of the race that was launched last (set on both sides by the launch message).
        public RaceMode RaceMode { get; private set; } = RaceMode.Normal;
        public bool LocalLobbyReady { get; private set; }  // client
        public bool RemoteLobbyReady { get; private set; } // host
        public bool CanLaunch => IsHost && Stage == LobbyStage.Workshop && Connection == ConnectionState.Connected && !string.IsNullOrEmpty(LobbyDevice) && RemoteLobbyReady && !BuildMismatch;
        public bool CanGoWorkshop => IsHost && Stage == LobbyStage.Menu && Connection == ConnectionState.Connected && RemoteLobbyReady && !BuildMismatch;

        // ----- rematch and the series -----
        // After a race each player can vote for a rematch: same device, same mode. When both have voted, both go
        // back to the lobby and the race starts again by itself (the plugin readies the client, the host launches).
        public bool LocalRematch { get; private set; }
        public bool RemoteRematch { get; private set; }
        public bool RematchAgreed => LocalRematch && RemoteRematch && Connection == ConnectionState.Connected;
        // Races decided since the two connected: this player's wins, the opponent's, draws.
        public int SeriesWins { get; private set; }
        public int SeriesLosses { get; private set; }
        public int SeriesDraws { get; private set; }
        private int scoredRace = -1;

        public void VoteRematch(bool want)
        {
            if (transport == null || LocalRematch == want) return;
            LocalRematch = want;
            transport.SendReliable(MsgKind.Rematch, Packet.Build(w => w.Write(want)));
            Emit(want ? "wants a rematch" : "no rematch");
        }

        // Counts a decided race once into the series.
        private void ScoreSeries()
        {
            var r = Result();
            if (!r.Decided || Local.RaceNo == scoredRace) return;
            scoredRace = Local.RaceNo;
            if (r.Draw) SeriesDraws++;
            else if (r.LocalWins) SeriesWins++;
            else SeriesLosses++;
        }

        private void ClearRematch()
        {
            LocalRematch = false;
            RemoteRematch = false;
        }

        // Both: we are going to the versus workshop (host pressed "Choose device").
        public event Action OnGoWorkshop;

        public bool IsHost => transport != null && transport.IsHost;
        public bool BothReadySameDevice => LocalReady && RemoteReady && !string.IsNullOrEmpty(Local.DeviceId) && RemoteReadyDevice == Local.DeviceId;

        public RaceSession(ITransport transport, string localName, string gameBuild, long resetPenaltyMs)
        {
            this.transport = transport;
            this.localName = localName ?? "";
            this.gameBuild = gameBuild ?? "";
            this.resetPenaltyMs = resetPenaltyMs;
            Connection = transport == null ? ConnectionState.Offline : ConnectionState.Waiting;
        }

        // Different game builds race on different code and data: no Ready, no start.
        public bool BuildMismatch => Remote != null && RemoteGameBuild != gameBuild;
        public int ForeignModVersion => transport != null ? transport.ForeignVersion : -1;
        public int MalformedMessages { get; private set; }
        // The other side is in co-op mode (it said CoopHello): no race with it.
        public bool PeerInCoop { get; private set; }

        private const int MaxNameLength = 24;
        private const int MaxIdLength = 128;

        private static string Clip(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max));

        // ----- game events -----

        public void ContestPrepared(string deviceId, int partsPlaced, int partsTotal)
        {
            if (Local.Phase == RacePhase.Racing) return;
            Local.Phase = RacePhase.Prepared;
            Local.DeviceId = deviceId ?? "";
            Local.PartsPlaced = partsPlaced;
            Local.PartsTotal = partsTotal;
            Local.Resets = 0;
            Local.ElapsedMs = 0;
            Local.InGameSeconds = 0;
            Emit("prepared " + Local.DeviceId + " " + partsPlaced + "/" + partsTotal);
        }

        // At the bench with a contest device and waiting for GO (true), or left the bench (false).
        public void SetLocalReady(bool ready, string deviceId)
        {
            if (ready && !string.IsNullOrEmpty(deviceId)) Local.DeviceId = deviceId;
            if (LocalReady == ready) return;
            LocalReady = ready;
            if (!ready) CancelCountdown("you left the bench");
            Emit(ready ? "ready at the bench with " + Local.DeviceId : "not ready");
            transport?.SendReliable(MsgKind.Ready, Packet.Build(w =>
            {
                w.Write(ready);
                w.Write(Local.DeviceId ?? "");
            }));
        }

        // Host: choose the race device.
        public void SelectDevice(string deviceId)
        {
            if (!IsHost || string.IsNullOrEmpty(deviceId) || deviceId == LobbyDevice) return;
            LobbyDevice = deviceId;
            if (RemoteLobbyReady) Emit("opponent must confirm the new device");
            RemoteLobbyReady = false;
            SendLobbySelect();
            Emit("lobby device: " + deviceId);
        }

        // Name and avatar, shown to the opponent in the lobby and next to the race progress.
        public void SetLocalProfile(string name, string avatar)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) name = localName;
            if (name == localName && (avatar ?? "") == LocalAvatar && profileSent) return;
            localName = name;
            LocalAvatar = avatar ?? "";
            SendPlayerInfo();
        }

        private bool profileSent;

        private void SendPlayerInfo()
        {
            if (transport == null) return;
            profileSent = true;
            var n = localName;
            var a = LocalAvatar;
            transport.SendReliable(MsgKind.PlayerInfo, Packet.Build(w =>
            {
                w.Write(n);
                w.Write(a);
            }));
        }

        // Host: take both players to the versus workshop. Only once the client is ready in the menu stage.
        public bool GoWorkshop()
        {
            if (!CanGoWorkshop) return false;
            Stage = LobbyStage.Workshop;
            RemoteLobbyReady = false;
            transport.SendReliable(MsgKind.LobbyGoWorkshop, Array.Empty<byte>());
            Emit("going to the versus workshop");
            OnGoWorkshop?.Invoke();
            return true;
        }

        // Host: choose the rules. Like a new device, a new mode withdraws the opponent's Ready.
        public void SelectMode(RaceMode mode)
        {
            if (!IsHost || mode == LobbyMode) return;
            LobbyMode = mode;
            if (RemoteLobbyReady) Emit("opponent must confirm the new mode");
            RemoteLobbyReady = false;
            SendLobbySelect();
            Emit("lobby mode: " + mode);
        }

        private void SendLobbySelect()
        {
            var id = LobbyDevice;
            var mode = LobbyMode;
            transport.SendReliable(MsgKind.LobbySelect, Packet.Build(w =>
            {
                w.Write(id);
                w.Write((byte)mode);
            }));
        }

        // Client: ready (or not) for the host's current choice.
        public void SetLobbyReady(bool ready)
        {
            if (IsHost || transport == null) return;
            if (ready && Stage == LobbyStage.Workshop && string.IsNullOrEmpty(LobbyDevice)) return;
            if (ready && BuildMismatch) { Emit("cannot be ready: the host runs a different game version"); return; }
            if (LocalLobbyReady == ready) return;
            LocalLobbyReady = ready;
            var id = LobbyDevice;
            var mode = LobbyMode;
            var stage = Stage;
            transport.SendReliable(MsgKind.LobbyReady, Packet.Build(w =>
            {
                w.Write(ready);
                w.Write((byte)stage);
                w.Write(id);
                w.Write((byte)mode);
            }));
            Emit(ready ? (stage == LobbyStage.Menu ? "lobby: ready in the menu" : "lobby: ready for " + id) : "lobby: not ready");
        }

        // Host: start the race for both. False while the client is not ready for the current device.
        public bool Launch()
        {
            if (!CanLaunch) return false;
            ClearRematch();
            var id = LobbyDevice;
            var mode = LobbyMode;
            RemoteLobbyReady = false;
            RaceMode = mode;
            transport.SendReliable(MsgKind.LobbyLaunch, Packet.Build(w =>
            {
                w.Write(id);
                w.Write((byte)mode);
            }));
            Emit("launching " + id + " (" + mode + ")");
            OnLaunch?.Invoke(id);
            return true;
        }

        // Back to the lobby after a race (or an aborted launch): forget the local race state.
        public void ResetForLobby()
        {
            SetLocalReady(false, "");
            countdownAtMs = -1;
            Local.Phase = RacePhase.Idle;
            Local.DeviceId = "";
            Local.PartsPlaced = 0;
            Local.PartsTotal = 0;
            Local.Resets = 0;
            Local.ElapsedMs = 0;
            Local.InGameSeconds = 0;
            Emit("back in the lobby");
        }

        private void CancelCountdown(string why)
        {
            if (countdownAtMs < 0) return;
            countdownAtMs = -1;
            // A race that never started does not use up its number: the next countdown gets it again.
            if (IsHost && pendingRaceNo == raceCounter) raceCounter--;
            Emit("countdown cancelled: " + why);
        }

        public void ContestStarted(long nowMs)
        {
            if (!startArmed) Local.RaceNo = 0; // started without the shared countdown: not part of a race
            startArmed = false;
            Local.Phase = RacePhase.Racing;
            Local.Resets = 0;
            Local.ElapsedMs = 0;
            raceStartMs = nowMs;
            Emit("started " + Local.DeviceId);
        }

        public void PartsChanged(int placed, int total)
        {
            Local.PartsPlaced = placed;
            Local.PartsTotal = total;
        }

        public void DeviceReset()
        {
            if (Local.Phase != RacePhase.Racing) return;
            Local.Resets++;
            Emit("reset #" + Local.Resets);
        }

        public void ContestFinished(long nowMs, float inGameSeconds)
        {
            if (Local.Phase != RacePhase.Racing) return;
            Local.ElapsedMs = nowMs - raceStartMs;
            Local.InGameSeconds = inGameSeconds;
            Local.PartsPlaced = Local.PartsTotal;
            Local.Phase = RacePhase.Finished;
            Emit("finished in " + Local.ElapsedMs + " ms, " + Local.Resets + " resets");
            if (transport != null)
                transport.SendReliable(MsgKind.Finish, Packet.Build(w => Local.Write(w)));
        }

        public void ContestStopped()
        {
            if (Local.Phase == RacePhase.Racing)
            {
                Local.Phase = RacePhase.Abandoned;
                Emit("abandoned");
            }
        }

        public void InGameSecondsChanged(float seconds) => Local.InGameSeconds = seconds;

        // ----- per frame -----

        public void Tick(long nowMs)
        {
            if (Local.Phase == RacePhase.Racing) Local.ElapsedMs = nowMs - raceStartMs;
            ScoreSeries();
            if (transport == null) return;

            if (!helloSent)
            {
                helloSent = true;
                transport.SendReliable(MsgKind.Hello, Packet.Build(w =>
                {
                    w.Write(localName);
                    w.Write(gameBuild);
                }));
            }

            foreach (var p in transport.Poll(nowMs))
                Handle(p, nowMs);

            if (Connection == ConnectionState.Connected && transport.PeerTimedOut)
            {
                Connection = ConnectionState.Lost;
                CancelCountdown("opponent timed out");
                Emit("opponent timed out");
            }

            if (IsHost && countdownAtMs < 0 && BothReadySameDevice && Connection == ConnectionState.Connected)
            {
                pendingRaceNo = ++raceCounter;
                countdownAtMs = nowMs + StartDelayMs;
                int raceNo = pendingRaceNo;
                transport.SendReliable(MsgKind.StartRace, Packet.Build(w =>
                {
                    w.Write(raceNo);
                    w.Write(StartDelayMs);
                }));
                Emit("race " + raceNo + " starts in " + StartDelayMs + " ms");
            }

            // While counting down the host repeats the time left (unreliable, 10 a second). A start message that was
            // lost and resent arrives late, and the client counted from its arrival, so it started late by the
            // resend wait; the latest repeat puts it right.
            if (IsHost && countdownAtMs >= 0 && nowMs - lastStartRepeatMs >= StartRepeatMs)
            {
                lastStartRepeatMs = nowMs;
                int repeatNo = pendingRaceNo;
                int left = (int)Math.Max(0, countdownAtMs - nowMs);
                transport.SendUnreliable(MsgKind.StartRace, Packet.Build(w => { w.Write(repeatNo); w.Write(left); }));
            }

            if (countdownAtMs >= 0 && nowMs >= countdownAtMs)
            {
                countdownAtMs = -1;
                if (!IsHost) startedRaceNo = pendingRaceNo;
                LocalReady = false;
                RemoteReady = false;
                Local.RaceNo = pendingRaceNo;
                startArmed = true;
                CountdownWallMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                Emit("countdown for race " + pendingRaceNo);
                OnCountdown?.Invoke();
            }

            if (!transport.HasPeer) return;
            bool changed = lastSent == null || lastSent.Phase != Local.Phase || lastSent.PartsPlaced != Local.PartsPlaced
                || lastSent.Resets != Local.Resets || lastSent.DeviceId != Local.DeviceId || lastSent.PartsTotal != Local.PartsTotal;
            long since = nowMs - lastProgressSentMs;
            if ((changed && since >= ProgressIntervalMs) || since >= ProgressKeepaliveMs)
            {
                lastProgressSentMs = nowMs;
                lastSent = Local.Clone();
                transport.SendUnreliable(MsgKind.Progress, Packet.Build(w => Local.Write(w)));
            }
        }

        private void Handle(Packet p, long nowMs)
        {
            try
            {
                HandleUnchecked(p, nowMs);
            }
            catch (Exception e) when (e is System.IO.EndOfStreamException || e is System.IO.IOException || e is ArgumentException || e is FormatException || e is OverflowException)
            {
                // A truncated or garbled message (or one from a newer field layout): drop it, keep the session.
                MalformedMessages++;
                if (MalformedMessages <= 5) Emit("ignored a malformed " + p.Kind + " message (" + e.GetType().Name + ")");
            }
        }

        private void HandleUnchecked(Packet p, long nowMs)
        {
            using (var r = p.Reader())
            {
                switch (p.Kind)
                {
                    case MsgKind.Hello:
                        RemoteName = Clip(r.ReadString(), MaxNameLength);
                        RemoteGameBuild = Clip(r.ReadString(), MaxIdLength);
                        if (Connection != ConnectionState.Connected) Emit("opponent connected: " + RemoteName);
                        Connection = ConnectionState.Connected;
                        if (IsHost)
                        {
                            // A (new) client: start its lobby state from scratch and greet it, so a client that
                            // joins after a reconnect also gets our name and build.
                            RemoteLobbyReady = false;
                            RemoteReady = false;
                            ClearRematch();
                            CancelCountdown("opponent (re)joined");
                            Remote = new RacerSnapshot();
                            var n = localName;
                            var b = gameBuild;
                            transport.SendReliable(MsgKind.Hello, Packet.Build(w =>
                            {
                                w.Write(n);
                                w.Write(b);
                            }));
                        }
                        // Our name/avatar for the newcomer; a client that (re)joins after the host moved on must follow.
                        SendPlayerInfo();
                        if (IsHost && Stage == LobbyStage.Workshop) transport.SendReliable(MsgKind.LobbyGoWorkshop, Array.Empty<byte>());
                        if (IsHost && !string.IsNullOrEmpty(LobbyDevice)) SendLobbySelect();
                        if (Remote == null) Remote = new RacerSnapshot();
                        if (RemoteGameBuild != gameBuild) Emit("WARNING game build differs: " + RemoteGameBuild);
                        break;
                    case MsgKind.CoopHello:
                        if (!PeerInCoop) Emit("the other player is in co-op mode");
                        PeerInCoop = true;
                        if (IsHost) transport.SendReliable(MsgKind.Hello, Packet.Build(w => { w.Write(localName); w.Write(gameBuild); }));
                        break;
                    case MsgKind.Progress:
                        var snap = RacerSnapshot.Read(r);
                        // A late unreliable Progress must not undo a reliable Finish of the same race.
                        if (Remote != null && Remote.Phase == RacePhase.Finished && snap.Phase == RacePhase.Racing
                            && Remote.RaceNo == snap.RaceNo && Remote.DeviceId == snap.DeviceId) break;
                        Remote = snap;
                        if (Connection == ConnectionState.Lost) Connection = ConnectionState.Connected;
                        break;
                    case MsgKind.Finish:
                        Remote = RacerSnapshot.Read(r);
                        Emit("opponent finished in " + Remote.ElapsedMs + " ms, " + Remote.Resets + " resets");
                        break;
                    case MsgKind.Rematch:
                        RemoteRematch = r.ReadBoolean();
                        Emit(RemoteRematch ? "the opponent wants a rematch" : "the opponent does not want a rematch");
                        break;
                    case MsgKind.Bye:
                        ClearRematch();
                        Connection = ConnectionState.Lost;
                        RemoteReady = false;
                        RemoteLobbyReady = false;
                        // Host: the seat is free again for the same friend (new game, new port) or someone else.
                        if (IsHost) transport.ForgetPeer();
                        CancelCountdown("opponent left");
                        Emit("opponent left");
                        break;
                    case MsgKind.Ready:
                        RemoteReady = r.ReadBoolean();
                        RemoteReadyDevice = r.ReadString();
                        Emit(RemoteReady ? "opponent ready with " + RemoteReadyDevice : "opponent not ready");
                        if (!RemoteReady) CancelCountdown("opponent left the bench");
                        break;
                    case MsgKind.StartRace:
                        int raceNo = r.ReadInt32();
                        int delay = r.ReadInt32();
                        if (IsHost || raceNo <= startedRaceNo) break; // that race has started here already
                        bool first = countdownAtMs < 0 || pendingRaceNo != raceNo;
                        if (!LocalReady) { if (first) Emit("ignored start of race " + raceNo + ": not ready"); break; }
                        pendingRaceNo = raceNo;
                        // The host's time left was measured when it sent; we heard it about half a round trip later.
                        countdownAtMs = nowMs + Math.Max(0, delay - transport.RttMs / 2);
                        if (first) Emit("race " + raceNo + " starts in " + (countdownAtMs - nowMs) + " ms (rtt " + transport.RttMs + ")");
                        break;
                    case MsgKind.LobbySelect:
                        var chosen = r.ReadString();
                        var chosenMode = (RaceMode)r.ReadByte();
                        if (IsHost || (chosen == LobbyDevice && chosenMode == LobbyMode)) break;
                        LobbyDevice = chosen;
                        LobbyMode = chosenMode;
                        // A choice only withdraws a Ready given for a choice (workshop stage).
                        if (Stage == LobbyStage.Workshop)
                        {
                            if (LocalLobbyReady) Emit("host changed the device or mode: confirm again");
                            LocalLobbyReady = false;
                        }
                        Emit("lobby: " + chosen + " (" + chosenMode + ")");
                        break;
                    case MsgKind.LobbyReady:
                        bool lobbyReady = r.ReadBoolean();
                        var forStage = (LobbyStage)r.ReadByte();
                        var forDevice = r.ReadString();
                        var forMode = (RaceMode)r.ReadByte();
                        if (!IsHost) break;
                        // A ready from another stage, or for a device or mode the host has since replaced, does not count.
                        RemoteLobbyReady = lobbyReady && forStage == Stage
                            && (Stage == LobbyStage.Menu || (forDevice == LobbyDevice && forMode == LobbyMode));
                        Emit(RemoteLobbyReady ? "opponent is ready" : "opponent is not ready");
                        break;
                    case MsgKind.PlayerInfo:
                        RemoteName = Clip(r.ReadString(), MaxNameLength);
                        RemoteAvatar = Clip(r.ReadString(), MaxIdLength);
                        Emit("opponent profile: " + RemoteName + " / " + RemoteAvatar);
                        break;
                    case MsgKind.LobbyGoWorkshop:
                        if (IsHost || Stage == LobbyStage.Workshop) break;
                        Stage = LobbyStage.Workshop;
                        LocalLobbyReady = false;
                        Emit("host is taking us to the versus workshop");
                        OnGoWorkshop?.Invoke();
                        break;
                    case MsgKind.LobbyLaunch:
                        var launchId = r.ReadString();
                        var launchMode = (RaceMode)r.ReadByte();
                        if (IsHost) break;
                        ClearRematch();
                        LocalLobbyReady = false;
                        LobbyDevice = launchId;
                        LobbyMode = launchMode;
                        RaceMode = launchMode;
                        Emit("host started the race with " + launchId + " (" + launchMode + ")");
                        OnLaunch?.Invoke(launchId);
                        break;
                }
            }
        }

        public void Leave()
        {
            transport?.SendReliable(MsgKind.Bye, Array.Empty<byte>());
        }

        // ----- result -----

        public long PenaltyPerResetMs => RaceMode == RaceMode.Hardcore ? resetPenaltyMs : 0;

        public long ScoreMs(RacerSnapshot s) => s.ElapsedMs + s.Resets * PenaltyPerResetMs;

        public RaceResult Result()
        {
            var res = new RaceResult();
            if (Remote == null) { res.Reason = "no opponent"; return res; }
            if (Local.RaceNo != Remote.RaceNo) { res.Reason = "not in the same race"; return res; }
            if (!string.IsNullOrEmpty(Local.DeviceId) && !string.IsNullOrEmpty(Remote.DeviceId) && Local.DeviceId != Remote.DeviceId)
            {
                res.Reason = "different devices (" + Local.DeviceId + " vs " + Remote.DeviceId + ")";
                return res;
            }
            bool lf = Local.Phase == RacePhase.Finished, rf = Remote.Phase == RacePhase.Finished;
            res.LocalScoreMs = ScoreMs(Local);
            res.RemoteScoreMs = ScoreMs(Remote);
            if (lf && rf)
            {
                res.Decided = true;
                if (res.LocalScoreMs == res.RemoteScoreMs)
                {
                    // Tie on adjusted time: fewer resets wins, then a draw.
                    res.Draw = Local.Resets == Remote.Resets;
                    res.LocalWins = Local.Resets < Remote.Resets;
                }
                else res.LocalWins = res.LocalScoreMs < res.RemoteScoreMs;
                res.Reason = "both finished";
            }
            else if (lf && Remote.Phase == RacePhase.Abandoned) { res.Decided = true; res.LocalWins = true; res.Reason = "opponent abandoned"; }
            else if (rf && Local.Phase == RacePhase.Abandoned) { res.Decided = true; res.LocalWins = false; res.Reason = "you abandoned"; }
            else res.Reason = "race in progress";
            return res;
        }

        private void Emit(string line)
        {
            Log.Add(line);
            if (Log.Count > 50) Log.RemoveAt(0);
            OnLog?.Invoke(line);
        }
    }
}
