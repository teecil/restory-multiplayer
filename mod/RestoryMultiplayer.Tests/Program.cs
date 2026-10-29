using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RestoryVersus.Core;

// Headless tests for the mod core (versus and co-op): two real NetTransports on loopback, a fake clock,
// scripted racer events. Exit code 0 = all pass. Prints "RESULT: PASS|FAIL".
internal static class Program
{
    private static int failures;
    private static int checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    }

    private static int Main()
    {
        try
        {
            Run("codec round trip", CodecRoundTrip);
            Run("hardcore race over loopback, no loss", () => Race(0.0, RaceMode.Hardcore, hostResets: 0, clientResets: 2, hostFinishAt: 9000, clientFinishAt: 8000, penaltyMs: 1000, expectHostWins: true));
            Run("hardcore race over loopback, 30% loss", () => Race(0.3, RaceMode.Hardcore, hostResets: 1, clientResets: 0, hostFinishAt: 7000, clientFinishAt: 9000, penaltyMs: 5000, expectHostWins: false));
            // Same times and resets as the 30% case, normal rules: no penalty, so the faster host wins.
            Run("normal race: resets cost nothing", () => Race(0.0, RaceMode.Normal, hostResets: 1, clientResets: 0, hostFinishAt: 7000, clientFinishAt: 9000, penaltyMs: 5000, expectHostWins: true));
            Run("abandon decides", Abandon);
            Run("different devices never decide", DifferentDevices);
            Run("client started before host", ClientFirst);
            Run("synced countdown, then a rematch", () => SyncedStartAndRematch(0.0));
            Run("synced countdown with 20% loss", () => SyncedStartAndRematch(0.2));
            Run("ready on different devices never counts down", ReadyDifferentDevices);
            Run("leaving the bench cancels the countdown", UnreadyCancels);
            Run("a cancelled countdown does not use up the race number", CancelKeepsRaceNumber);
            Run("lobby: start needs the client ready for the current device", LobbyFlow);
            Run("lobby: client joining late sees the choice", LobbyLateJoin);
            Run("lobby: names and avatars reach the other side", Profiles);
            Run("malformed messages are dropped, the session keeps working", Malformed);
            Run("rapid lobby changes under 30% loss arrive in order", OrderUnderLoss);
            Run("reconnect after the client left", () => Reconnect(silent: false));
            Run("reconnect after the client vanished (timeout)", () => Reconnect(silent: true));
            Run("different game builds cannot get ready", BuildMismatch);
            Run("a different mod version is noticed", ForeignVersion);
            Run("rtt stays small after a long stall", RttAfterStall);
            Run("rematch: both vote, the same device and mode start again; the series counts", RematchFlow);
            Run("co-op: the host's save reaches the guest intact, both launch", () => CoopTransfer(0.0, 72409));
            Run("co-op: save transfer under 30% loss", () => CoopTransfer(0.3, 150000));
            Run("co-op: a guest reconnecting mid-transfer voids it, a new start works", CoopReconnectMidTransfer);
            Run("co-op: versus and co-op players recognise each other", CoopMeetsVersus);
            Run("co-op: different game builds cannot start", CoopBuildMismatch);
            Run("co-op: the guest follows the host's clock and shutters, never backwards", () => CoopHostState(0.3));
            Run("co-op: object state codec", CoopObjCodec);
            Run("co-op: a held object follows the hand; late poses after the release are dropped", CoopHeldTracking);
            Run("co-op: held poses cross the network while dragging", () => CoopHeldOverNet(0.3));
            Run("co-op: named actions arrive in order, big ones too, under loss", () => CoopActions(0.3));
            Run("co-op: shared parts stock converges, nothing duplicated (fuzz, 30% loss)", () => CoopStockFuzz(0.3, 11));
            Run("co-op: shared parts stock converges (fuzz, no loss)", () => CoopStockFuzz(0.0, 12));
            Run("co-op: the next day: the host resends its save, only the guest reloads", () => CoopResync(0.3));
            Run("co-op: a guest that drops and comes back mid-game gets the current save", () => CoopRejoin(0.2));
            Run("co-op: a frozen window comes back without reconnecting", CoopFreezeRecovers);
            Run("network errors become plain codes for the menu", NetErrorCodes);
            Run("co-op: room moves converge (fuzz, no loss)", () => CoopRoomFuzz(0.0, 1));
            Run("co-op: room moves converge (fuzz, 30% loss)", () => CoopRoomFuzz(0.3, 2));
            Run("co-op: room moves converge (fuzz, 30% loss, other seed)", () => CoopRoomFuzz(0.3, 3));
            Run("steam-like link: a race between account ids, 30% loss", () => Race(0.3, RaceMode.Hardcore, hostResets: 1, clientResets: 0, hostFinishAt: 7000, clientFinishAt: 9000, penaltyMs: 5000, expectHostWins: false, steam: true));
            Run("steam-like link: the host's save reaches the guest, 30% loss", () => CoopTransfer(0.3, 150000, steam: true));
            Run("steam-like link: a friend who restarted the game (same account) gets the current save", () => CoopRejoin(0.2, steam: true));
            Run("steam-like link: strangers and other runs are not heard", SteamStrangers);
            Run("traffic over a 200 ms round trip: little is sent twice", () => Traffic(0.0, 1.25));
            Run("traffic over a 200 ms round trip with 10% loss", () => Traffic(0.1, 1.6));
            Run("shared stock: only changes travel after the first whole stock", StockTraffic);
        }
        catch (Exception e)
        {
            Console.WriteLine("  FAIL setup error: " + e);
            failures++;
        }
        Console.WriteLine($"{checks - failures}/{checks} checks passed");
        Console.WriteLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        Console.WriteLine(name);
        test();
    }

    private static void CodecRoundTrip()
    {
        var s = new RacerSnapshot { Phase = RacePhase.Racing, DeviceId = "dev_psp_01", PartsPlaced = 7, PartsTotal = 12, Resets = 2, ElapsedMs = 123456, InGameSeconds = 99.5f };
        var p = new Packet { Kind = MsgKind.Progress, Seq = 42, Ack = 7, Payload = Packet.Build(w => s.Write(w)) };
        var bytes = p.Encode();
        var d = Packet.Decode(bytes, bytes.Length);
        Check(d != null && d.Kind == MsgKind.Progress && d.Seq == 42 && d.Ack == 7, "header survives");
        var s2 = RacerSnapshot.Read(d.Reader());
        Check(s2.DeviceId == "dev_psp_01" && s2.PartsPlaced == 7 && s2.PartsTotal == 12 && s2.Resets == 2 && s2.ElapsedMs == 123456 && s2.InGameSeconds == 99.5f && s2.Phase == RacePhase.Racing, "snapshot survives");
        bytes[2] = 99;
        Check(Packet.Decode(bytes, bytes.Length) == null, "wrong protocol version is dropped");
        Check(Packet.Decode(new byte[] { 1, 2, 3 }, 3) == null, "short garbage is dropped");
    }

    private sealed class Pair : IDisposable
    {
        public NetTransport HostT, ClientT;
        public RaceSession Host, Client;
        public long Now;

        public Pair(double loss, long penaltyMs, bool clientFirst = false, bool steam = false)
        {
            if (steam)
            {
                var net = new FakeSteam();
                HostT = net.Host(100);
                ClientT = net.Guest(200, 100);
                Host = new RaceSession(HostT, "A", "build-1", penaltyMs);
                Client = new RaceSession(ClientT, "B", "build-1", penaltyMs);
            }
            else if (clientFirst)
            {
                // Pick a free port, close it, start the client towards it, then the host.
                var probe = NetTransport.Host(0);
                int port = probe.LocalPort;
                probe.Dispose();
                ClientT = NetTransport.Connect("127.0.0.1", port);
                Client = new RaceSession(ClientT, "B", "build-1", penaltyMs);
                Pump(20);
                HostT = NetTransport.Host(port);
                Host = new RaceSession(HostT, "A", "build-1", penaltyMs);
            }
            else
            {
                HostT = NetTransport.Host(0);
                ClientT = NetTransport.Connect("127.0.0.1", HostT.LocalPort);
                Host = new RaceSession(HostT, "A", "build-1", penaltyMs);
                Client = new RaceSession(ClientT, "B", "build-1", penaltyMs);
            }
            HostT.SendLossRate = loss;
            ClientT.SendLossRate = loss;
        }

        // Advance the fake clock in 50 ms steps, ticking both sides; real sleeps let UDP deliver.
        public void Pump(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Now += 50;
                Host?.Tick(Now);
                Client?.Tick(Now);
                Thread.Sleep(2);
            }
        }

        public void PumpUntil(Func<bool> cond, int maxSteps = 400)
        {
            for (int i = 0; i < maxSteps && !cond(); i++) Pump(1);
        }

        public void Dispose()
        {
            HostT?.Dispose();
            ClientT?.Dispose();
        }
    }

    // Lobby start with the given rules: host picks device and mode, client confirms, host launches.
    // Menu stage: client ready, host takes both to the workshop.
    private static void ToWorkshop(Pair pair)
    {
        if (pair.Host.Stage == LobbyStage.Workshop) return;
        pair.Client.SetLobbyReady(true);
        pair.PumpUntil(() => pair.Host.CanGoWorkshop, 200);
        Check(pair.Host.GoWorkshop(), "host takes both to the workshop");
        pair.PumpUntil(() => pair.Client.Stage == LobbyStage.Workshop, 200);
    }

    private static void LaunchFromLobby(Pair pair, string device, RaceMode mode)
    {
        ToWorkshop(pair);
        pair.Host.SelectDevice(device);
        pair.Host.SelectMode(mode);
        pair.PumpUntil(() => pair.Client.LobbyDevice == device && pair.Client.LobbyMode == mode, 200);
        pair.Client.SetLobbyReady(true);
        pair.PumpUntil(() => pair.Host.CanLaunch, 200);
        bool launched = pair.Host.Launch();
        if (!launched)
            Console.WriteLine($"    state: host stage={pair.Host.Stage} dev={pair.Host.LobbyDevice} mode={pair.Host.LobbyMode} remoteReady={pair.Host.RemoteLobbyReady} conn={pair.Host.Connection} mismatch={pair.Host.BuildMismatch} | client stage={pair.Client.Stage} dev={pair.Client.LobbyDevice} mode={pair.Client.LobbyMode} ready={pair.Client.LocalLobbyReady}");
        if (!launched)
        {
            Console.WriteLine("    host log: " + string.Join(" | ", pair.Host.Log));
            Console.WriteLine("    client log: " + string.Join(" | ", pair.Client.Log));
        }
        Check(launched, "launched " + device + " (" + mode + ")");
        pair.PumpUntil(() => pair.Client.RaceMode == mode && !pair.Client.LocalLobbyReady, 200);
        Check(pair.Host.RaceMode == mode && pair.Client.RaceMode == mode, "both race with " + mode + " rules");
    }

    private static void Race(double loss, RaceMode mode, int hostResets, int clientResets, long hostFinishAt, long clientFinishAt, long penaltyMs, bool expectHostWins, bool steam = false)
    {
        using (var pair = new Pair(loss, penaltyMs, steam: steam))
        {
            pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected);
            Check(pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected, "both sides connect");
            LaunchFromLobby(pair, "dev_ds_lite", mode);
            Check(pair.Host.RemoteName == "B" && pair.Client.RemoteName == "A", "names exchanged");
            Check(!pair.Host.BuildMismatch && !pair.Client.BuildMismatch, "same build accepted");

            pair.Host.ContestPrepared("dev_ds_lite", 0, 12);
            pair.Client.ContestPrepared("dev_ds_lite", 0, 12);
            long start = pair.Now;
            pair.Host.ContestStarted(start);
            pair.Client.ContestStarted(start);

            var events = new List<(long at, Action act)>();
            for (int i = 1; i <= 12; i++)
            {
                int n = i;
                events.Add((start + hostFinishAt * i / 13, () => pair.Host.PartsChanged(n, 12)));
                events.Add((start + clientFinishAt * i / 13, () => pair.Client.PartsChanged(n, 12)));
            }
            for (int i = 0; i < hostResets; i++) events.Add((start + 1000 + i * 300, () => pair.Host.DeviceReset()));
            for (int i = 0; i < clientResets; i++) events.Add((start + 1100 + i * 300, () => pair.Client.DeviceReset()));
            events.Add((start + hostFinishAt, () => pair.Host.ContestFinished(pair.Now, 1234f)));
            events.Add((start + clientFinishAt, () => pair.Client.ContestFinished(pair.Now, 1111f)));
            events.Sort((a, b) => a.at.CompareTo(b.at));

            bool sawMidRaceProgress = false;
            int next = 0;
            while (next < events.Count)
            {
                while (next < events.Count && events[next].at <= pair.Now) events[next++].act();
                pair.Pump(1);
                if (pair.Client.Remote != null && pair.Client.Remote.Phase == RacePhase.Racing && pair.Client.Remote.PartsPlaced > 0 && pair.Client.Remote.PartsPlaced < 12)
                    sawMidRaceProgress = true;
            }
            pair.PumpUntil(() => pair.Host.Result().Decided && pair.Client.Result().Decided);

            Check(sawMidRaceProgress, "opponent progress visible mid-race");
            Check(pair.Host.Remote.Resets == clientResets && pair.Client.Remote.Resets == hostResets, "reset counts mirrored");
            var hr = pair.Host.Result();
            var cr = pair.Client.Result();
            Check(hr.Decided && cr.Decided, "both sides decide");
            Check(hr.LocalWins == expectHostWins && cr.LocalWins == !expectHostWins, $"winner is {(expectHostWins ? "host" : "client")} (host {hr.LocalScoreMs} ms vs client {hr.RemoteScoreMs} ms)");
            Check(hr.LocalScoreMs == cr.RemoteScoreMs && hr.RemoteScoreMs == cr.LocalScoreMs, "both sides agree on both scores");
            pair.PumpUntil(() => pair.HostT.PendingReliableCount == 0 && pair.ClientT.PendingReliableCount == 0, maxSteps: 100);
            Check(pair.HostT.PendingReliableCount == 0 && pair.ClientT.PendingReliableCount == 0, "all reliable messages acked within 5 s");
        }
    }

    private static void Abandon()
    {
        using (var pair = new Pair(0.0, 0))
        {
            pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected);
            pair.Host.ContestPrepared("dev_flip", 0, 5);
            pair.Client.ContestPrepared("dev_flip", 0, 5);
            pair.Host.ContestStarted(pair.Now);
            pair.Client.ContestStarted(pair.Now);
            pair.Pump(10);
            pair.Client.ContestStopped();
            pair.Pump(10);
            pair.Host.ContestFinished(pair.Now, 50f);
            pair.PumpUntil(() => pair.Client.Result().Decided);
            Check(pair.Host.Result().Decided && pair.Host.Result().LocalWins, "finisher wins against abandoner (host view)");
            Check(pair.Client.Result().Decided && !pair.Client.Result().LocalWins, "abandoner loses (client view)");
        }
    }

    private static void DifferentDevices()
    {
        using (var pair = new Pair(0.0, 0))
        {
            pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected);
            pair.Host.ContestPrepared("dev_a", 0, 3);
            pair.Client.ContestPrepared("dev_b", 0, 3);
            pair.Host.ContestStarted(pair.Now);
            pair.Client.ContestStarted(pair.Now);
            pair.Pump(5);
            pair.Host.ContestFinished(pair.Now, 1f);
            pair.Client.ContestFinished(pair.Now, 1f);
            pair.Pump(20);
            Check(!pair.Host.Result().Decided && pair.Host.Result().Reason.StartsWith("different devices"), "host refuses to score different devices");
            Check(!pair.Client.Result().Decided, "client refuses to score different devices");
        }
    }

    private static Pair Connected(double loss)
    {
        var pair = new Pair(loss, 0);
        pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected);
        // A few reliable round trips so the client has an RTT estimate.
        pair.Pump(10);
        return pair;
    }

    // Both ready on the same device -> both countdowns fire; returns the gap between them in fake ms.
    private static long ReadyAndCountdown(Pair pair, string device, out bool hostFired, out bool clientFired)
    {
        long hostAt = -1, clientAt = -1;
        Action h = () => hostAt = pair.Now;
        Action c = () => clientAt = pair.Now;
        pair.Host.OnCountdown += h;
        pair.Client.OnCountdown += c;
        pair.Host.ContestPrepared(device, 0, 6);
        pair.Client.ContestPrepared(device, 0, 6);
        pair.Host.SetLocalReady(true, device);
        pair.Client.SetLocalReady(true, device);
        pair.PumpUntil(() => hostAt >= 0 && clientAt >= 0, 200);
        pair.Host.OnCountdown -= h;
        pair.Client.OnCountdown -= c;
        hostFired = hostAt >= 0;
        clientFired = clientAt >= 0;
        return Math.Abs(hostAt - clientAt);
    }

    private static void SyncedStartAndRematch(double loss)
    {
        using (var pair = Connected(loss))
        {
            long gap = ReadyAndCountdown(pair, "dev_psp", out var hf, out var cf);
            Check(hf && cf, "countdown fires on both");
            // Real UDP plus 50 ms ticks; under loss a resent StartRace arrives late, so the bound is looser.
            long bound = loss > 0 ? 400 : 150;
            Check(gap <= bound, $"countdowns within {bound} ms of each other (gap {gap} ms, rtt {pair.ClientT.RttMs} ms)");
            Check(pair.Host.Local.RaceNo == 1 && pair.Client.Local.RaceNo == 1, "both in race 1");
            pair.Host.ContestStarted(pair.Now);
            pair.Client.ContestStarted(pair.Now);
            pair.Pump(20);
            pair.Host.ContestFinished(pair.Now, 10f);
            pair.Pump(10);
            pair.Client.ContestFinished(pair.Now, 12f);
            pair.PumpUntil(() => pair.Host.Result().Decided && pair.Client.Result().Decided);
            Check(pair.Host.Result().Decided && pair.Host.Result().LocalWins && !pair.Client.Result().LocalWins, "race 1: host finished first and wins on both sides");

            // Rematch on the same model: the old result must not leak into race 2.
            gap = ReadyAndCountdown(pair, "dev_psp", out hf, out cf);
            Check(hf && cf && pair.Host.Local.RaceNo == 2 && pair.Client.Local.RaceNo == 2, "rematch counts down as race 2");
            pair.Host.ContestStarted(pair.Now);
            pair.Client.ContestStarted(pair.Now);
            pair.Host.ContestFinished(pair.Now + 1, 5f);
            Check(!pair.Host.Result().Decided, "host does not reuse the client's race-1 finish");
            pair.Pump(10);
            pair.Client.ContestFinished(pair.Now, 4f);
            pair.PumpUntil(() => pair.Host.Result().Decided && pair.Client.Result().Decided);
            Check(pair.Host.Result().Decided && pair.Client.Result().Decided, "race 2 decided on both");
            Check(pair.Host.Result().LocalWins != pair.Client.Result().LocalWins, "race 2 has exactly one winner");
        }
    }

    private static void ReadyDifferentDevices()
    {
        using (var pair = Connected(0.0))
        {
            bool fired = false;
            pair.Host.OnCountdown += () => fired = true;
            pair.Client.OnCountdown += () => fired = true;
            pair.Host.SetLocalReady(true, "dev_a");
            pair.Client.SetLocalReady(true, "dev_b");
            pair.Pump(80);
            Check(!fired, "no countdown in 4 s");
            Check(pair.Host.RemoteReady && pair.Host.RemoteReadyDevice == "dev_b", "host sees the client's device");
        }
    }

    private static void CancelKeepsRaceNumber()
    {
        using (var pair = Connected(0.0))
        {
            pair.Host.ContestPrepared("dev", 0, 5);
            pair.Client.ContestPrepared("dev", 0, 5);
            pair.Host.SetLocalReady(true, "dev");
            pair.Client.SetLocalReady(true, "dev");
            pair.PumpUntil(() => pair.Host.CountdownScheduled, 200);
            pair.Client.SetLocalReady(false, "");   // dropped out of the bench before GO
            pair.PumpUntil(() => !pair.Host.CountdownScheduled, 200);
            int raceNo = 0;
            pair.Client.OnCountdown += () => raceNo = pair.Client.Local.RaceNo;
            pair.Client.SetLocalReady(true, "dev");   // sat back down
            pair.PumpUntil(() => raceNo != 0, 300);
            Check(raceNo == 1, $"the race that finally starts is race 1 ({raceNo})");
        }
    }

    private static void UnreadyCancels()
    {
        using (var pair = Connected(0.0))
        {
            bool fired = false;
            pair.Host.OnCountdown += () => fired = true;
            pair.Client.OnCountdown += () => fired = true;
            pair.Host.SetLocalReady(true, "dev_a");
            pair.Client.SetLocalReady(true, "dev_a");
            pair.PumpUntil(() => pair.Host.CountdownScheduled && pair.Client.CountdownScheduled, 100);
            Check(pair.Host.CountdownScheduled && pair.Client.CountdownScheduled, "countdown scheduled on both");
            pair.Client.SetLocalReady(false, "dev_a");
            pair.Pump(60);
            Check(!fired, "neither side starts after the client left the bench");
            Check(!pair.Host.CountdownScheduled && !pair.Client.CountdownScheduled, "both countdowns cancelled");
        }
    }

    private static void LobbyFlow()
    {
        using (var pair = Connected(0.0))
        {
            string onHost = null, onClient = null;
            pair.Host.OnLaunch += id => onHost = id;
            pair.Client.OnLaunch += id => onClient = id;

            bool wentHost = false, wentClient = false;
            pair.Host.OnGoWorkshop += () => wentHost = true;
            pair.Client.OnGoWorkshop += () => wentClient = true;
            Check(pair.Host.Stage == LobbyStage.Menu && pair.Client.Stage == LobbyStage.Menu, "both start in the menu stage");
            Check(!pair.Host.CanGoWorkshop && !pair.Host.GoWorkshop(), "host cannot go to the workshop before the client is ready");
            Check(!pair.Host.CanLaunch && !pair.Host.Launch(), "no start in the menu stage");
            pair.Client.SetLobbyReady(true);
            Check(pair.Client.LocalLobbyReady, "client can be ready in the menu without a device");
            pair.PumpUntil(() => pair.Host.CanGoWorkshop, 100);
            Check(pair.Host.CanGoWorkshop, "host's Choose device unlocks once the client is ready");
            Check(pair.Host.GoWorkshop(), "host takes both to the workshop");
            pair.PumpUntil(() => wentClient, 100);
            Check(wentHost && wentClient && pair.Client.Stage == LobbyStage.Workshop, "both go to the workshop");
            Check(!pair.Client.LocalLobbyReady && !pair.Host.CanLaunch, "ready is used up: the client confirms again in the workshop");

            Check(!pair.Host.CanLaunch && !pair.Host.Launch(), "no start before a device is chosen");
            pair.Client.SetLobbyReady(true);
            Check(!pair.Client.LocalLobbyReady, "client cannot be ready in the workshop before the host chose a device");

            pair.Host.SelectDevice("dev_gba");
            pair.PumpUntil(() => pair.Client.LobbyDevice == "dev_gba", 100);
            Check(pair.Client.LobbyDevice == "dev_gba", "client sees the chosen device");
            Check(!pair.Host.CanLaunch && !pair.Host.Launch(), "host cannot start while the client is not ready");

            pair.Client.SetLobbyReady(true);
            pair.PumpUntil(() => pair.Host.CanLaunch, 100);
            Check(pair.Host.CanLaunch, "host can start once the client is ready");

            pair.Host.SelectMode(RaceMode.Hardcore);
            Check(!pair.Host.CanLaunch, "changing the mode withdraws the client's ready at once");
            pair.PumpUntil(() => pair.Client.LobbyMode == RaceMode.Hardcore, 100);
            Check(pair.Client.LobbyMode == RaceMode.Hardcore && !pair.Client.LocalLobbyReady, "client sees the new mode and must confirm it");
            pair.Client.SetLobbyReady(true);
            pair.PumpUntil(() => pair.Host.CanLaunch, 100);
            Check(pair.Host.CanLaunch, "ready again for the new mode");

            pair.Host.SelectDevice("dev_psp");
            Check(!pair.Host.CanLaunch, "changing the device withdraws the client's ready at once");
            pair.PumpUntil(() => pair.Client.LobbyDevice == "dev_psp", 100);
            Check(!pair.Client.LocalLobbyReady, "client must confirm the new device");
            pair.Pump(20);
            Check(!pair.Host.CanLaunch, "a ready for the old device does not count");

            pair.Client.SetLobbyReady(true);
            pair.PumpUntil(() => pair.Host.CanLaunch, 100);
            Check(pair.Host.Launch(), "host starts");
            pair.PumpUntil(() => onClient != null, 100);
            Check(onHost == "dev_psp" && onClient == "dev_psp", "start fires on both with the same device");
            Check(pair.Host.RaceMode == RaceMode.Hardcore && pair.Client.RaceMode == RaceMode.Hardcore, "start carries the hardcore mode to the client");
            Check(pair.Host.PenaltyPerResetMs == 0 && pair.Client.PenaltyPerResetMs == 0, "penalty setting is 0 ms in this test pair");
            Check(!pair.Host.CanLaunch && !pair.Client.LocalLobbyReady, "lobby ready is used up by the start");

            pair.Client.ResetForLobby();
            pair.Host.ResetForLobby();
            Check(pair.Host.Local.Phase == RacePhase.Idle && pair.Client.Local.Phase == RacePhase.Idle, "back in the lobby");
        }
    }

    private static void LobbyLateJoin()
    {
        var hostT = NetTransport.Host(0);
        var host = new RaceSession(hostT, "A", "build-1", 0);
        NetTransport clientT = null;
        try
        {
            // The host chooses before anyone has joined (no peer yet: the select waits in the queue).
            host.SelectDevice("dev_ds");
            long now = 0;
            for (int i = 0; i < 10; i++) { now += 50; host.Tick(now); Thread.Sleep(2); }
            clientT = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
            var client = new RaceSession(clientT, "B", "build-1", 0);
            for (int i = 0; i < 100 && client.LobbyDevice != "dev_ds"; i++) { now += 50; host.Tick(now); client.Tick(now); Thread.Sleep(2); }
            Check(client.LobbyDevice == "dev_ds", "late client sees the host's earlier choice");
        }
        finally
        {
            hostT.Dispose();
            clientT?.Dispose();
        }
    }

    // In the first two-copy run one copy was loading its save (multi-second frames) while samples were
    // taken; the averaged RTT reached 755 ms on loopback and the client started ~380 ms early.
    private static void RttAfterStall()
    {
        using (var pair = Connected(0.0))
        {
            pair.Pump(20);
            // The host sends a reliable message, then stalls for 3 s of fake time (only the client ticks),
            // so the ack is processed 3 s after the send: one huge, un-resent sample.
            pair.Now += 50;
            pair.Host.SetLocalReady(true, "dev_x");
            pair.Host.Tick(pair.Now);
            for (int i = 0; i < 60; i++)
            {
                pair.Now += 50;
                pair.Client.Tick(pair.Now);
                Thread.Sleep(2);
            }
            pair.Pump(4);
            Check(pair.ClientT.RttMs <= 100 && pair.HostT.RttMs <= 100, $"rtt after stall: client {pair.ClientT.RttMs} ms, host {pair.HostT.RttMs} ms (50 ms ticks)");
        }
    }

    private static void Profiles()
    {
        using (var pair = Connected(0.0))
        {
            pair.Host.SetLocalProfile("Alex", "icon_cat");
            pair.Client.SetLocalProfile("Friend", "icon_robot");
            pair.PumpUntil(() => pair.Host.RemoteAvatar == "icon_robot" && pair.Client.RemoteAvatar == "icon_cat", 100);
            Check(pair.Host.RemoteName == "Friend" && pair.Host.RemoteAvatar == "icon_robot", "host sees the client's name and avatar");
            Check(pair.Client.RemoteName == "Alex" && pair.Client.RemoteAvatar == "icon_cat", "client sees the host's name and avatar");
            pair.Client.SetLocalProfile("Friend2", "icon_dog");
            pair.PumpUntil(() => pair.Host.RemoteName == "Friend2", 100);
            Check(pair.Host.RemoteName == "Friend2" && pair.Host.RemoteAvatar == "icon_dog", "a change in the lobby reaches the host");
            pair.Client.SetLocalProfile("   ", "icon_dog");
            Check(pair.Client.LocalName == "Friend2", "an empty name keeps the old one");
        }
    }

    // Reliable messages used to be resent in dictionary order, so a newer choice could be overtaken by an
    // older one (found when the hardcore race tests launched with the client stuck on the old mode).
    private static void OrderUnderLoss()
    {
        using (var pair = Connected(0.3))
        {
            pair.Client.SetLobbyReady(true);
            pair.PumpUntil(() => pair.Host.CanGoWorkshop, 300);
            pair.Host.GoWorkshop();
            pair.PumpUntil(() => pair.Client.Stage == LobbyStage.Workshop, 300);
            for (int i = 0; i < 20; i++)
            {
                pair.Host.SelectDevice("dev_" + i);
                pair.Host.SelectMode(i % 2 == 0 ? RaceMode.Hardcore : RaceMode.Normal);
                pair.Pump(1);
            }
            pair.PumpUntil(() => pair.HostT.PendingReliableCount == 0, 600);
            pair.Pump(10);
            Check(pair.Client.LobbyDevice == pair.Host.LobbyDevice && pair.Client.LobbyMode == pair.Host.LobbyMode,
                  $"client ends on the host's last choice (host {pair.Host.LobbyDevice}/{pair.Host.LobbyMode}, client {pair.Client.LobbyDevice}/{pair.Client.LobbyMode})");
        }
    }

    private static void Malformed()
    {
        using (var pair = Connected(0.0))
        {
            var rnd = new Random(7);
            int sent = 0;
            foreach (MsgKind kind in Enum.GetValues(typeof(MsgKind)))
            {
                if (kind == MsgKind.Ack || kind == MsgKind.Heartbeat || kind == MsgKind.Bye) continue;
                // Empty, one byte, a lying string length, and random noise of a few sizes.
                var payloads = new List<byte[]> { new byte[0], new byte[] { 1 }, new byte[] { 0xFF, 0xFF, 0xFF, 0x7F } };
                for (int i = 0; i < 6; i++)
                {
                    var b = new byte[rnd.Next(2, 300)];
                    rnd.NextBytes(b);
                    payloads.Add(b);
                }
                foreach (var payload in payloads) { pair.ClientT.SendReliable(kind, payload); sent++; }
            }
            Exception thrown = null;
            try { pair.Pump(100); }
            catch (Exception e) { thrown = e; }
            Check(thrown == null, "no exception from " + sent + " malformed messages" + (thrown == null ? "" : ": " + thrown.GetType().Name));
            Check(pair.Host.MalformedMessages > 0, $"malformed messages were counted ({pair.Host.MalformedMessages})");
            Check(pair.Host.Connection == ConnectionState.Connected, "still connected");
            Check(pair.Host.RemoteName.Length <= 24, $"opponent name stays within 24 characters ({pair.Host.RemoteName.Length})");
            // Garbage may have changed lobby state; a clean lobby round must still work.
            pair.Host.SelectDevice("dev_after_noise");
            pair.Host.SelectMode(RaceMode.Normal);
            pair.PumpUntil(() => pair.Client.LobbyDevice == "dev_after_noise", 200);
            Check(pair.Client.LobbyDevice == "dev_after_noise", "lobby still works after the noise");
        }
    }

    private static void RematchFlow()
    {
        using (var pair = Connected(0.2))
        {
            LaunchFromLobby(pair, "dev_tv", RaceMode.Hardcore);
            // A quick race: both finish, the host faster.
            pair.Host.ContestPrepared("dev_tv", 0, 10);
            pair.Client.ContestPrepared("dev_tv", 0, 10);
            pair.Host.SetLocalReady(true, "dev_tv");
            pair.Client.SetLocalReady(true, "dev_tv");
            int countdowns = 0;
            pair.Host.OnCountdown += () => { countdowns++; pair.Host.ContestStarted(pair.Now); };
            pair.Client.OnCountdown += () => pair.Client.ContestStarted(pair.Now);
            pair.PumpUntil(() => pair.Host.Local.Phase == RacePhase.Racing && pair.Client.Local.Phase == RacePhase.Racing, 400);
            pair.Pump(20);
            pair.Host.ContestFinished(pair.Now, 1f);
            pair.Pump(10);
            pair.Client.ContestFinished(pair.Now, 1f);
            pair.PumpUntil(() => pair.Host.Result().Decided && pair.Client.Result().Decided, 400);
            pair.Pump(5);
            Check(pair.Host.SeriesWins == 1 && pair.Client.SeriesLosses == 1 && pair.Host.SeriesLosses == 0, $"the series counts the race once (host {pair.Host.SeriesWins}:{pair.Host.SeriesLosses}, client {pair.Client.SeriesWins}:{pair.Client.SeriesLosses})");

            pair.Host.VoteRematch(true);
            pair.PumpUntil(() => pair.Client.RemoteRematch, 300);
            Check(pair.Client.RemoteRematch && !pair.Host.RematchAgreed, "one vote: the other side sees it, no rematch yet");
            pair.Client.VoteRematch(true);
            pair.PumpUntil(() => pair.Host.RematchAgreed && pair.Client.RematchAgreed, 300);
            Check(pair.Host.RematchAgreed && pair.Client.RematchAgreed, "both voted: the rematch is agreed on both sides");

            // What the plugin does then: both back in the lobby, the client ready, the host launches.
            pair.Host.ResetForLobby();
            pair.Client.ResetForLobby();
            string launched = null;
            pair.Client.OnLaunch += id => launched = id;
            pair.Client.SetLobbyReady(true);
            pair.PumpUntil(() => pair.Host.CanLaunch, 300);
            Check(pair.Host.Launch(), "the host relaunches");
            pair.PumpUntil(() => launched != null, 300);
            Check(launched == "dev_tv" && pair.Client.RaceMode == RaceMode.Hardcore, $"same device and mode ({launched}, {pair.Client.RaceMode})");
            Check(!pair.Host.LocalRematch && !pair.Client.RemoteRematch && !pair.Client.LocalRematch, "the votes are cleared for the next race");
            Check(pair.Host.SeriesWins == 1, "the series is still counted once");
        }
    }

    private static void Reconnect(bool silent)
    {
        var hostT = NetTransport.Host(0);
        var host = new RaceSession(hostT, "A", "build-1", 0);
        NetTransport c1T = null, c2T = null;
        long now = 0;
        Action<RaceSession, RaceSession, int> pump = (x, y, steps) =>
        {
            for (int i = 0; i < steps; i++) { now += 50; x.Tick(now); y?.Tick(now); Thread.Sleep(2); }
        };
        try
        {
            c1T = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
            var c1 = new RaceSession(c1T, "B", "build-1", 0);
            for (int i = 0; i < 100 && host.Connection != ConnectionState.Connected; i++) pump(host, c1, 1);
            Check(host.Connection == ConnectionState.Connected && host.RemoteName == "B", "first client connected");
            c1.SetLobbyReady(true);
            for (int i = 0; i < 100 && !host.CanGoWorkshop; i++) pump(host, c1, 1);

            if (silent)
            {
                c1T.Dispose();                 // its game crashed: no Bye
                pump(host, null, 120);         // 6 s of fake time: past the 5 s timeout
                Check(host.Connection == ConnectionState.Lost, "host notices the silent client");
            }
            else
            {
                c1.Leave();
                pump(host, c1, 20);
                c1T.Dispose();
                Check(host.Connection == ConnectionState.Lost, "host sees the client leave");
            }
            Check(!host.CanGoWorkshop, "the old client's Ready is gone");

            c2T = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
            var c2 = new RaceSession(c2T, "C", "build-1", 0);
            for (int i = 0; i < 200 && !(host.Connection == ConnectionState.Connected && c2.Connection == ConnectionState.Connected); i++) pump(host, c2, 1);
            Check(host.Connection == ConnectionState.Connected && host.RemoteName == "C", "a new client takes the seat (" + host.RemoteName + ")");
            Check(c2.Connection == ConnectionState.Connected && c2.RemoteName == "A", "the new client sees the host");
            c2.SetLobbyReady(true);
            for (int i = 0; i < 100 && !host.CanGoWorkshop; i++) pump(host, c2, 1);
            Check(host.GoWorkshop(), "the lobby works with the new client");
        }
        finally
        {
            hostT.Dispose();
            c1T?.Dispose();
            c2T?.Dispose();
        }
    }

    private static void BuildMismatch()
    {
        var hostT = NetTransport.Host(0);
        var cT = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
        try
        {
            var host = new RaceSession(hostT, "A", "1.0.005r/aaa", 0);
            var c = new RaceSession(cT, "B", "1.0.006r/bbb", 0);
            long now = 0;
            for (int i = 0; i < 100 && !(host.Connection == ConnectionState.Connected && c.Connection == ConnectionState.Connected); i++) { now += 50; host.Tick(now); c.Tick(now); Thread.Sleep(2); }
            Check(host.BuildMismatch && c.BuildMismatch, "both see the build mismatch");
            c.SetLobbyReady(true);
            for (int i = 0; i < 20; i++) { now += 50; host.Tick(now); c.Tick(now); Thread.Sleep(2); }
            Check(!c.LocalLobbyReady && !host.CanGoWorkshop && !host.GoWorkshop(), "no Ready and no Choose device across builds");
        }
        finally
        {
            hostT.Dispose();
            cT.Dispose();
        }
    }

    private static void ForeignVersion()
    {
        var hostT = NetTransport.Host(0);
        try
        {
            using (var raw = new System.Net.Sockets.UdpClient())
            {
                // Our magic, protocol version 99.
                var datagram = new byte[] { (byte)(Packet.Magic & 0xFF), (byte)(Packet.Magic >> 8), 99, (byte)MsgKind.Hello, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
                raw.Send(datagram, datagram.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, hostT.LocalPort));
            }
            var host = new RaceSession(hostT, "A", "b", 0);
            long now = 0;
            for (int i = 0; i < 40 && host.ForeignModVersion < 0; i++) { now += 50; host.Tick(now); Thread.Sleep(5); }
            Check(host.ForeignModVersion == 99 && !hostT.HasPeer, $"version 99 noticed and not accepted as a peer ({host.ForeignModVersion})");
        }
        finally
        {
            hostT.Dispose();
        }
    }

    private static void ClientFirst()
    {
        using (var pair = new Pair(0.0, 0, clientFirst: true))
        {
            pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected);
            Check(pair.Host.Connection == ConnectionState.Connected && pair.Client.Connection == ConnectionState.Connected, "client that started first still connects");
        }
    }
    // ----- a stand-in for Steam: players are account ids, datagrams go through queues -----

    private sealed class FakeSteam
    {
        private readonly Dictionary<ulong, Queue<(ulong from, byte[] data, long at)>> boxes = new Dictionary<ulong, Queue<(ulong, byte[], long)>>();
        // One-way delay on a fake clock (0: instant).
        public long DelayMs;
        public Func<long> Clock = () => 0;

        public NetTransport Host(ulong me) => NetTransport.Over(new Link(this, me, 0), true, null);

        public NetTransport Guest(ulong me, ulong host)
        {
            var link = new Link(this, me, host);
            return NetTransport.Over(link, false, link.HostPeer);
        }

        public Link Raw(ulong me, ulong host) => new Link(this, me, host);

        public sealed class Link : IdLink
        {
            private readonly FakeSteam net;
            private readonly ulong me;
            private readonly Queue<(ulong from, byte[] data, long at)> box = new Queue<(ulong, byte[], long)>();

            public Link(FakeSteam net, ulong me, ulong host) : base(host)
            {
                this.net = net;
                this.me = me;
                net.boxes[me] = box; // a restarted game takes the account's mailbox over
            }

            public override string Route => "Steam";

            protected override void SendRaw(ulong to, byte[] data)
            {
                if (net.boxes.TryGetValue(to, out var q)) q.Enqueue((me, data, net.Clock() + net.DelayMs));
            }

            protected override bool ReceiveRaw(out ulong from, out byte[] data)
            {
                if (box.Count > 0 && box.Peek().at <= net.Clock()) { (from, data, _) = box.Dequeue(); return true; }
                from = 0; data = null;
                return false;
            }

            public override void Dispose()
            {
                if (net.boxes.TryGetValue(me, out var q) && q == box) net.boxes.Remove(me);
            }
        }
    }

    private static void SteamStrangers()
    {
        var net = new FakeSteam();
        var hostLink = net.Raw(100, 0);
        var guest = net.Raw(200, 100);
        var stranger = net.Raw(300, 0);
        object hostSeen = null;
        guest.Send(guest.HostPeer, new byte[] { 1, 2, 3 });
        Check(hostLink.TryReceive(out var d, out hostSeen) && d.Length == 3 && d[2] == 3 && hostSeen is IdLink.Peer hp && hp.Id == 200,
              "the host hears a guest by account id, the bytes intact");
        stranger.Send(new IdLink.Peer(200, 0), new byte[] { 9 });
        Check(!guest.TryReceive(out _, out _), "a guest does not hear anyone but its host");
        hostLink.Send(hostSeen, new byte[] { 7 });
        Check(guest.TryReceive(out var back, out var from) && back[0] == 7 && from.Equals(guest.HostPeer), "the host's answer reaches that run of the guest");
        var restarted = net.Raw(200, 100); // the friend restarted the game
        hostLink.Send(hostSeen, new byte[] { 8 }); // the host still answers the old run
        Check(!restarted.TryReceive(out _, out _), "what the host sends to the old run is not heard by the new one");
        restarted.Send(restarted.HostPeer, new byte[] { 5 });
        Check(hostLink.TryReceive(out _, out var newPeer) && !newPeer.Equals(hostSeen), "to the host the restarted friend is a new peer");
    }

    // Bytes on the wire for a save transfer and a burst of named actions, over a link with a 200 ms round trip:
    // the first real two-PC game filled the host's upload because everything unacked was resent every 150 ms.
    private static void Traffic(double loss, double maxRatio)
    {
        using (var pair = new CoopPair(loss, steam: true, oneWayDelayMs: 100))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 400);
            var save = FakeSave(150000, 77);
            pair.Host.StartWithSave("Story.slot1.release-No-21.restory", save);
            int launched = 0;
            pair.Guest.OnLaunch += () => launched++;
            pair.PumpUntil(() => launched > 0, 4000);
            Check(launched == 1, $"the save got through ({pair.Now / 1000.0:0.0} s on the fake clock)");
            long before = pair.HostT.BytesSent;
            int got = 0;
            pair.Guest.OnAction += (name, data) => { if (name == "blob") got++; };
            for (int i = 0; i < 40; i++) pair.Host.SendAction("blob", FakeSave(5000, i));
            pair.PumpUntil(() => got == 40, 4000);
            long payload = 150000 + 40 * 5000;
            long sent = pair.HostT.BytesSent;
            double ratio = (double)sent / payload;
            Check(got == 40, $"all 40 actions arrived ({got})");
            Check(ratio <= maxRatio, $"the host sent {sent / 1024} KB for {payload / 1024} KB of data ({ratio:0.00}x, at most {maxRatio}x), resent {pair.HostT.PacketsResent} of {pair.HostT.PacketsSent} datagrams");
        }
    }

    private static void StockTraffic()
    {
        using (var pair = new CoopPair(0.0))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("s", FakeSave(1000, 1));
            pair.PumpUntil(() => pair.Host.Playing && pair.Guest.Playing, 600);
            var hs = new SharedStock(pair.Host);
            var gs = new SharedStock(pair.Guest);
            Dictionary<string, int> guestStock = null;
            gs.OnReplace += st => guestStock = st;
            // 200 kinds of parts, each key as long as the game's (an item's JSON).
            var stock = new Dictionary<string, int>();
            for (int i = 0; i < 200; i++) stock["inv#{\"$type\":\"StorageItemElement\",\"ElementData\":{\"Info\":\"part_" + i + "\",\"Condition\":0.75,\"DirtMaskTextureId\":" + i + "}}"] = 3;
            gs.Tick(new Dictionary<string, int>(stock));
            hs.Tick(new Dictionary<string, int>(stock));
            pair.Pump(10);
            long first = hs.BytesSent;
            for (int n = 0; n < 20; n++)
            {
                var key = "inv#{\"$type\":\"StorageItemElement\",\"ElementData\":{\"Info\":\"part_" + n + "\",\"Condition\":0.75,\"DirtMaskTextureId\":" + n + "}}";
                stock[key] = stock[key] - 1;
                hs.Tick(new Dictionary<string, int>(stock));
                pair.Pump(2);
            }
            pair.Pump(10);
            long changes = hs.BytesSent - first;
            Check(guestStock != null && guestStock.Count == stock.Count && guestStock.All(kv => stock[kv.Key] == kv.Value), "the guest holds the host's stock after 20 changes");
            Check(changes < 20 * 400, $"20 one-part changes took {changes} bytes (the whole stock once: {first} bytes)");
            // A guest that joined again has no whole stock: its first change from the host asks for one.
            var gs2 = new SharedStock(pair.Guest);
            Dictionary<string, int> second = null;
            gs2.OnReplace += st => second = st;
            stock["inv#extra"] = 1;
            hs.Tick(new Dictionary<string, int>(stock));
            pair.Pump(10);
            hs.Tick(new Dictionary<string, int>(stock));
            pair.Pump(10);
            Check(second != null && second.Count == stock.Count, "a guest without the whole stock asks for it and gets it");
        }
    }

    // ----- co-op -----

    private sealed class CoopPair : IDisposable
    {
        public NetTransport HostT, GuestT;
        public CoopSession Host, Guest;
        public long Now;

        public CoopPair(double loss, string guestBuild = "build-1", bool steam = false, long oneWayDelayMs = 0)
        {
            if (steam)
            {
                var net = new FakeSteam { DelayMs = oneWayDelayMs, Clock = () => Now };
                HostT = net.Host(100);
                GuestT = net.Guest(200, 100);
            }
            else
            {
                HostT = NetTransport.Host(0);
                GuestT = NetTransport.Connect("127.0.0.1", HostT.LocalPort);
            }
            Host = new CoopSession(HostT, "A", "build-1");
            Guest = new CoopSession(GuestT, "B", guestBuild);
            HostT.SendLossRate = loss;
            GuestT.SendLossRate = loss;
        }

        public void Pump(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Now += 50;
                Host.Tick(Now);
                Guest?.Tick(Now);
                Thread.Sleep(2);
            }
        }

        public void PumpUntil(Func<bool> cond, int maxSteps = 400)
        {
            for (int i = 0; i < maxSteps && !cond(); i++) Pump(1);
        }

        public void Dispose()
        {
            HostT?.Dispose();
            GuestT?.Dispose();
        }
    }

    private static byte[] FakeSave(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void CoopTransfer(double loss, int length, bool steam = false)
    {
        using (var pair = new CoopPair(loss, steam: steam))
        {
            int hostLaunch = 0, guestLaunch = 0, received = 0;
            byte[] got = null;
            string gotName = null;
            pair.Host.OnLaunch += () => hostLaunch++;
            pair.Guest.OnLaunch += () => guestLaunch++;
            pair.Guest.OnSaveReceived += (name, bytes) => { received++; got = bytes; gotName = name; };
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            Check(pair.Host.CanStart && !pair.Guest.CanStart, "host can start once the guest is in, the guest cannot");
            var save = FakeSave(length, length);
            Check(pair.Host.StartWithSave("Story.slot1.release-No-21.restory", save), "host starts with its save");
            Check(!pair.Host.StartWithSave("again", save), "a second start during the transfer is refused");
            pair.PumpUntil(() => hostLaunch > 0 && guestLaunch > 0, 2000);
            bool same = got != null && got.Length == save.Length;
            for (int i = 0; same && i < save.Length; i++) same = got[i] == save[i];
            Check(received == 1 && same && gotName == "Story.slot1.release-No-21.restory", $"the guest has the exact save once ({received}x, {got?.Length ?? 0} of {length} bytes)");
            Check(hostLaunch == 1 && guestLaunch == 1, $"both launch exactly once (host {hostLaunch}, guest {guestLaunch})");
            Check(pair.Host.Stage == CoopStage.Launch && pair.Guest.Stage == CoopStage.Launch, "both in the launch stage");
            pair.Host.SetLocalWorld(321, "abc");
            pair.Guest.SetLocalWorld(321, "abc");
            pair.PumpUntil(() => pair.Host.RemoteWorldCount == 321 && pair.Guest.RemoteWorldCount == 321, 300);
            Check(pair.Host.RemoteWorldHash == "abc" && pair.Guest.RemoteWorldHash == "abc", "each side sees what the other loaded");
        }
    }

    private static void CoopReconnectMidTransfer()
    {
        var hostT = NetTransport.Host(0);
        var host = new CoopSession(hostT, "A", "build-1");
        NetTransport g1T = null, g2T = null;
        long now = 0;
        Action<CoopSession, CoopSession, int> pump = (x, y, steps) =>
        {
            for (int i = 0; i < steps; i++) { now += 50; x.Tick(now); y?.Tick(now); Thread.Sleep(2); }
        };
        try
        {
            g1T = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
            var g1 = new CoopSession(g1T, "B", "build-1");
            for (int i = 0; i < 100 && !host.CanStart; i++) pump(host, g1, 1);
            g1T.SendLossRate = 1.0; // the guest goes deaf-mute: nothing it sends arrives
            Check(host.StartWithSave("s", FakeSave(400000, 1)), "transfer starts");
            pump(host, g1, 3);
            g1T.Dispose();
            pump(host, null, 120);
            Check(host.Stage == CoopStage.Lobby && host.Error != "", "a guest that vanished mid-transfer voids it (" + host.Error + ")");

            g2T = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
            var g2 = new CoopSession(g2T, "C", "build-1");
            int launches = 0;
            g2.OnLaunch += () => launches++;
            for (int i = 0; i < 200 && !host.CanStart; i++) pump(host, g2, 1);
            Check(host.CanStart && host.RemoteName == "C", "a new guest takes the seat");
            Check(host.StartWithSave("s2", FakeSave(5000, 2)), "a new start works");
            for (int i = 0; i < 400 && launches == 0; i++) pump(host, g2, 1);
            Check(launches == 1 && g2.ReceivedSave != null && g2.ReceivedSave.Length == 5000, "the new guest gets the new save");
        }
        finally
        {
            hostT.Dispose();
            g1T?.Dispose();
            g2T?.Dispose();
        }
    }

    private static void CoopMeetsVersus()
    {
        var hostT = NetTransport.Host(0);
        var cT = NetTransport.Connect("127.0.0.1", hostT.LocalPort);
        try
        {
            var host = new CoopSession(hostT, "A", "build-1");
            var versus = new RaceSession(cT, "B", "build-1", 0);
            long now = 0;
            for (int i = 0; i < 100 && !(host.PeerInVersus && versus.PeerInCoop); i++) { now += 50; host.Tick(now); versus.Tick(now); Thread.Sleep(2); }
            Check(host.PeerInVersus && versus.PeerInCoop, "each side reports the other's mode");
            Check(!host.CanStart, "no co-op start with a versus player");
        }
        finally
        {
            hostT.Dispose();
            cT.Dispose();
        }
    }

    private static void CoopHostState(double loss)
    {
        using (var pair = new CoopPair(loss))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            int launched = 0;
            pair.Guest.OnLaunch += () => launched++;
            pair.Host.StartWithSave("s", FakeSave(3000, 4));
            pair.PumpUntil(() => launched > 0, 600);
            Check(pair.Guest.HostClockTicks == -1, "no host state before the host is in the shop");
            long t0 = new DateTime(2024, 5, 1, 9, 0, 0).Ticks;
            bool backwards = false;
            long seen = -1;
            for (int i = 0; i < 80; i++)
            {
                pair.Host.SetHostState(t0 + i * TimeSpan.TicksPerMinute, true, i >= 40, 1000 + i);
                pair.Pump(1);
                if (pair.Guest.HostClockTicks >= 0)
                {
                    if (pair.Guest.HostClockTicks < seen) backwards = true;
                    seen = pair.Guest.HostClockTicks;
                }
            }
            pair.Pump(20);
            Check(!backwards, "the guest's copy of the clock never went backwards under loss");
            Check(pair.Guest.HostClockTicks == t0 + 79 * TimeSpan.TicksPerMinute && pair.Guest.HostClockRunning && pair.Guest.HostShuttersOpen,
                  $"the guest ends on the host's latest state ({new DateTime(Math.Max(0, pair.Guest.HostClockTicks)):HH:mm}, shutters {pair.Guest.HostShuttersOpen})");
            Check(pair.Guest.HostStatesReceived > 0 && pair.Host.HostClockTicks == -1, "only the guest takes host states");
            Check(pair.Guest.HostMoney == 1079, $"the guest sees the host's money ({pair.Guest.HostMoney})");
        }
    }

    private static void CoopObjCodec()
    {
        var o = new ObjState { Id = "1b2c-guid", State = 2, Parent = "Shop:0/Shelf:3/Storage:1", X = 1.5f, Y = -2f, Z = 0.25f, Qx = 0.1f, Qy = 0.2f, Qz = 0.3f, Qw = 0.9f, Hidden = true, FromGuest = true, Seq = 77 };
        var bytes = Packet.Build(w => o.Write(w));
        var back = ObjState.Read(new System.IO.BinaryReader(new System.IO.MemoryStream(bytes)));
        Check(back.SamePlace(o) && back.FromGuest && back.Seq == 77 && !back.Gone, "object state survives the wire");
        bool threw = false;
        try { ObjState.Read(new System.IO.BinaryReader(new System.IO.MemoryStream(Packet.Build(w => new ObjState { Id = "" }.Write(w))))); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "an empty id is refused");
        var dev = new ObjState { Id = "dev-1", Kind = ObjState.KindDevice, State = 1, Data = FakeSave(5000, 9) };
        var dback = ObjState.Read(new System.IO.BinaryReader(new System.IO.MemoryStream(Packet.Build(w => dev.Write(w)))));
        Check(dback.Kind == ObjState.KindDevice && dback.SameData(dev) && dback.Data.Length == 5000, "a device state carries its data");
    }

    // Two fake worlds (id -> state). Both sides move the same few objects at random, often at the same moment;
    // each local move is applied at once and reported, each remote one applied when the session says so.
    // In the end both worlds must hold the same state for every object.
    private static void CoopRoomFuzz(double loss, int seed)
    {
        using (var pair = new CoopPair(loss))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("s", FakeSave(2000, seed));
            pair.PumpUntil(() => pair.Host.Stage == CoopStage.Launch && pair.Guest.Stage == CoopStage.Launch, 600);
            var hostWorld = new Dictionary<string, ObjState>();
            var guestWorld = new Dictionary<string, ObjState>();
            string[] ids = { "clock", "radio", "lamp", "cat", "bonsai", "vhs" };
            foreach (var id in ids)
            {
                hostWorld[id] = new ObjState { Id = id, Parent = "shelf0" };
                guestWorld[id] = new ObjState { Id = id, Parent = "shelf0" };
            }
            pair.Host.OnApplyObject += o => hostWorld[o.Id] = o.Clone();
            pair.Guest.OnApplyObject += o => guestWorld[o.Id] = o.Clone();
            var rnd = new Random(seed);
            int moves = 0;
            for (int step = 0; step < 300; step++)
            {
                for (int side = 0; side < 2; side++)
                {
                    if (rnd.NextDouble() > 0.35) continue;
                    var id = ids[rnd.Next(ids.Length)];
                    var o = new ObjState { Id = id, Parent = "shelf" + rnd.Next(4), X = rnd.Next(100), Y = rnd.Next(100), Z = rnd.Next(100), Hidden = rnd.NextDouble() < 0.1 };
                    if (rnd.NextDouble() < 0.2) { o.Kind = ObjState.KindDevice; o.Data = FakeSave(rnd.Next(1, 6000), rnd.Next()); } // some carry device data, often several packets long
                    if (side == 0) { hostWorld[id] = o.Clone(); pair.Host.ReportLocalMove(o); }
                    else { guestWorld[id] = o.Clone(); pair.Guest.ReportLocalMove(o); }
                    moves++;
                }
                pair.Pump(1);
            }
            pair.PumpUntil(() => pair.HostT.PendingReliableCount == 0 && pair.GuestT.PendingReliableCount == 0 && pair.Guest.PendingMoves == 0, 2000);
            pair.Pump(10);
            int differ = 0;
            foreach (var id in ids) if (!hostWorld[id].SamePlace(guestWorld[id]) || !hostWorld[id].SameData(guestWorld[id])) differ++;
            Check(differ == 0, $"{moves} moves from both sides: every object ends in the same place on both ({differ} differ)");
            Check(pair.Guest.PendingMoves == 0, "no guest move left unconfirmed");
        }
    }

    private static void CoopResync(double loss)
    {
        using (var pair = new CoopPair(loss))
        {
            int hostLaunch = 0, guestLaunch = 0;
            pair.Host.OnLaunch += () => hostLaunch++;
            pair.Guest.OnLaunch += () => guestLaunch++;
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("day1", FakeSave(8000, 5));
            pair.PumpUntil(() => hostLaunch == 1 && guestLaunch == 1, 800);
            pair.PumpUntil(() => pair.Host.Stage == CoopStage.Launch, 800);
            pair.Host.DayEnding();
            pair.PumpUntil(() => pair.Guest.HostEndingDay, 300);
            Check(pair.Guest.HostEndingDay, "the guest hears the host ended the day");
            var day2 = FakeSave(20000, 6);
            pair.PumpUntil(() => pair.Host.Stage == CoopStage.Launch && guestLaunch >= 2, 800);
            int before = guestLaunch;
            Check(pair.Host.Resync("day2", day2), "the host resends its save");
            pair.PumpUntil(() => guestLaunch > before && pair.Host.Stage == CoopStage.Launch, 1500);
            bool same = pair.Guest.ReceivedSave != null && pair.Guest.ReceivedSave.Length == day2.Length;
            for (int i = 0; same && i < day2.Length; i++) same = pair.Guest.ReceivedSave[i] == day2[i];
            Check(same && pair.Guest.SaveFileName == "day2", "the guest has the next day's save");
            Check(hostLaunch == 1, $"the host did not reload ({hostLaunch} launches)");
            Check(!pair.Guest.HostEndingDay && pair.Guest.Stage == CoopStage.Launch, "the guest is back in the shop stage");
        }
    }

    private static void CoopActions(double loss)
    {
        using (var pair = new CoopPair(loss))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("s", FakeSave(2000, 7));
            pair.PumpUntil(() => pair.Host.Stage == CoopStage.Launch && pair.Guest.Stage == CoopStage.Launch, 600);
            var got = new List<string>();
            byte[] big = null;
            pair.Host.OnAction += (n, b) => { got.Add(n + ":" + b.Length); if (n == "big") big = b; };
            var bigSent = FakeSave(50000, 8);
            for (int i = 0; i < 10; i++) pair.Guest.SendAction("a" + i, FakeSave(i + 1, i));
            pair.Guest.SendAction("big", bigSent);
            pair.Guest.SendAction("last", new byte[0]);
            pair.PumpUntil(() => got.Count == 12, 1500);
            bool order = got.Count == 12 && got[10] == "big:50000" && got[11] == "last:0";
            for (int i = 0; order && i < 10; i++) order = got[i] == "a" + i + ":" + (i + 1);
            bool same = big != null && big.Length == bigSent.Length;
            for (int i = 0; same && i < big.Length; i++) same = big[i] == bigSent[i];
            Check(order && same, $"12 actions in order, the 50 KB one intact ({got.Count} arrived)");
        }
    }

    // Both players take parts out and put parts in at random; the host applies the guest's changes to its stock.
    // In the end both stocks are equal and the total equals what was put in minus what was taken, except for
    // removals the host could not make because the host had taken that very part at the same time (counted).
    private static void CoopStockFuzz(double loss, int seed)
    {
        using (var pair = new CoopPair(loss))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("s", FakeSave(2000, seed));
            pair.PumpUntil(() => pair.Host.Stage == CoopStage.Launch && pair.Guest.Stage == CoopStage.Launch, 600);
            var hostStock = new Dictionary<string, int> { { "inv|screw", 5 }, { "inv|chip", 3 }, { "inv|belt", 2 } };
            var guestStock = new Dictionary<string, int>(hostStock);
            var hs = new SharedStock(pair.Host);
            var gs = new SharedStock(pair.Guest);
            int conflicts = 0;
            hs.OnApplyChange += change =>
            {
                foreach (var kv in change)
                {
                    hostStock.TryGetValue(kv.Key, out int have);
                    int now = have + kv.Value;
                    if (now < 0) { conflicts += -now; now = 0; }
                    hostStock[kv.Key] = now;
                }
            };
            gs.OnReplace += stock => { guestStock.Clear(); foreach (var kv in stock) guestStock[kv.Key] = kv.Value; };
            var rnd = new Random(seed);
            string[] keys = { "inv|screw", "inv|chip", "inv|belt", "inv|lens", "bin|screw" };
            int net = 0;
            hs.Tick(hostStock); // in the game the first tick comes when the shop is entered, before any player action
            gs.Tick(guestStock);
            for (int step = 0; step < 400; step++)
            {
                for (int side = 0; side < 2; side++)
                {
                    if (rnd.NextDouble() > 0.3) continue;
                    var stock = side == 0 ? hostStock : guestStock;
                    var key = keys[rnd.Next(keys.Length)];
                    stock.TryGetValue(key, out int have);
                    if (rnd.NextDouble() < 0.5 && have > 0) { stock[key] = have - 1; net--; }
                    else { stock[key] = have + 1; net++; }
                }
                hs.Tick(hostStock);
                gs.Tick(guestStock);
                pair.Pump(1);
            }
            for (int i = 0; i < 300; i++) { hs.Tick(hostStock); gs.Tick(guestStock); pair.Pump(1); }
            int Total(Dictionary<string, int> d) { int t = 0; foreach (var v in d.Values) t += v; return t; }
            var diff = SharedStock.Diff(hostStock, guestStock);
            Check(diff.Count == 0, $"both stocks equal ({diff.Count} keys differ)");
            if (Total(hostStock) != 10 + net + conflicts)
                Console.WriteLine($"    debug: malformed host {pair.Host.MalformedMessages} guest {pair.Guest.MalformedMessages}; host snapshots {hs.SnapshotsSent}, guest took {gs.SnapshotsTaken}; host pending {pair.HostT.PendingReliableCount} guest pending {pair.GuestT.PendingReliableCount}");
            Check(Total(hostStock) == 10 + net + conflicts, $"total {Total(hostStock)} = 10 + {net} net changes + {conflicts} simultaneous takes of the same last part");
            Check(gs.ChangesSent > 20 && hs.ChangesApplied == gs.ChangesSent, $"every guest change reached the host ({gs.ChangesSent} sent, {hs.ChangesApplied} applied)");
        }
    }

    private static void CoopRejoin(double loss, bool steam = false)
    {
        var net = new FakeSteam();
        var hostT = steam ? net.Host(100) : NetTransport.Host(0);
        var host = new CoopSession(hostT, "A", "build-1");
        NetTransport g1T = null, g2T = null;
        long now = 0;
        Action<CoopSession, CoopSession, int> pump = (x, y, steps) =>
        {
            for (int i = 0; i < steps; i++) { now += 50; x.Tick(now); y?.Tick(now); Thread.Sleep(2); }
        };
        try
        {
            int rejoins = 0;
            host.OnGuestJoinedWhilePlaying += () => { rejoins++; host.Resync("now", FakeSave(30000, 9)); };
            g1T = steam ? net.Guest(200, 100) : NetTransport.Connect("127.0.0.1", hostT.LocalPort);
            var g1 = new CoopSession(g1T, "B", "build-1");
            for (int i = 0; i < 200 && !host.CanStart; i++) pump(host, g1, 1);
            host.StartWithSave("first", FakeSave(4000, 10));
            for (int i = 0; i < 400 && !(host.Playing && g1.Playing); i++) pump(host, g1, 1);
            Check(host.Playing && g1.Playing && rejoins == 0, "both playing after the start");
            g1T.Dispose();                     // the guest's game crashed
            pump(host, null, 120);
            g2T = steam ? net.Guest(200, 100) : NetTransport.Connect("127.0.0.1", hostT.LocalPort);  // same account, new run
            g2T.SendLossRate = loss;
            hostT.SendLossRate = loss;
            var g2 = new CoopSession(g2T, "B", "build-1");
            int launches = 0;
            g2.OnLaunch += () => launches++;
            for (int i = 0; i < 1500 && launches == 0; i++) pump(host, g2, 1);
            Check(rejoins == 1, $"the host saw the guest come back while playing ({rejoins})");
            Check(launches == 1 && g2.Launches == 1 && g2.ReceivedSave != null && g2.ReceivedSave.Length == 30000 && g2.SaveFileName == "now",
                  "the returning guest got the current save and launches once");
            Check(host.Launches == 1, "the host did not launch again");
        }
        finally
        {
            hostT.Dispose();
            g1T?.Dispose();
            g2T?.Dispose();
        }
    }

    private static void CoopHeldTracking()
    {
        var t = new HeldTracker();
        Check(t.Accept(new HeldPose { Id = "clock", DragId = 1, X = 1 }, 0) && t.IsHeld("clock"), "a pose makes the object held");
        Check(t.Accept(new HeldPose { Id = "clock", DragId = 1, X = 2 }, 50) && t.Held["clock"].X == 2, "later poses move it");
        t.Release(1, "clock");
        Check(!t.IsHeld("clock"), "the release frees it");
        Check(!t.Accept(new HeldPose { Id = "clock", DragId = 1, X = 9 }, 100), "a late pose of the ended drag is dropped");
        Check(t.Accept(new HeldPose { Id = "clock", DragId = 2, X = 3 }, 200), "the next drag is followed again");
        Check(!t.Accept(new HeldPose { Id = "clock", DragId = 1, X = 9 }, 210), "an older drag's pose never overrides a newer one");
        var gone = t.Expire(200 + HeldTracker.TimeoutMs + 1);
        Check(gone.Count == 1 && gone[0] == "clock" && !t.IsHeld("clock"), "a hold that went silent expires");
    }

    private static void CoopHeldOverNet(double loss)
    {
        using (var pair = new CoopPair(loss))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("s", FakeSave(1000, 13));
            pair.PumpUntil(() => pair.Host.Playing && pair.Guest.Playing, 600);
            var tracker = new HeldTracker();
            int got = 0;
            pair.Host.OnHeld += p => { if (tracker.Accept(p, pair.Now)) got++; };
            pair.Host.OnAction += (n, b) => { if (n == "release") { var r = new System.IO.BinaryReader(new System.IO.MemoryStream(b)); tracker.Release(r.ReadInt32(), r.ReadString()); } };
            for (int i = 0; i < 40; i++)
            {
                pair.Guest.SendHeld(new HeldPose { Id = "radio", Kind = ObjState.KindObject, DragId = 7, X = i });
                pair.Pump(1);
            }
            Check(got > 10 && tracker.IsHeld("radio"), $"the host follows the guest's hand ({got} of 40 poses under loss)");
            pair.Guest.SendAction("release", Packet.Build(w => { w.Write(7); w.Write("radio"); }));
            pair.Guest.SendHeld(new HeldPose { Id = "radio", Kind = ObjState.KindObject, DragId = 7, X = 99 }); // late
            pair.PumpUntil(() => !tracker.IsHeld("radio"), 300);
            pair.Pump(20);
            Check(!tracker.IsHeld("radio"), "after the release the object is free and stays free");
        }
    }

    private static void CoopFreezeRecovers()
    {
        using (var pair = new CoopPair(0.0))
        {
            pair.PumpUntil(() => pair.Host.CanStart, 300);
            pair.Host.StartWithSave("s", FakeSave(1000, 14));
            pair.PumpUntil(() => pair.Host.Playing && pair.Guest.Playing, 600);
            // The guest's window freezes for 7 s: only the host keeps ticking.
            for (int i = 0; i < 140; i++) { pair.Now += 50; pair.Host.Tick(pair.Now); Thread.Sleep(2); }
            Check(pair.Host.Connection == ConnectionState.Lost && pair.Host.LeftNotice() == "", "the host notices the silence");
            pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Guest.Connection == ConnectionState.Connected, 200);
            Check(pair.Host.Connection == ConnectionState.Connected && pair.Guest.Connection == ConnectionState.Connected, "both are connected again once the guest ticks");
            int got = 0;
            pair.Host.OnAction += (n, b) => got++;
            pair.Guest.SendAction("after", new byte[] { 1 });
            pair.PumpUntil(() => got > 0, 200);
            Check(got == 1, "and messages flow again");
        }
    }

    private static void NetErrorCodes()
    {
        var first = NetTransport.Host(0);
        try
        {
            string code = "";
            try { NetTransport.Host(first.LocalPort).Dispose(); code = "no error"; }
            catch (Exception e) { code = NetErrors.Classify(e, true); }
            Check(code == "portBusy", $"hosting on a port that is taken -> portBusy ({code})");
        }
        finally { first.Dispose(); }
        string Join(string address)
        {
            try
            {
                var parts = address.Split(':');
                int port = parts.Length > 1 ? int.Parse(parts[1]) : 27515;
                NetTransport.Connect(parts[0], port).Dispose();
                return "no error";
            }
            catch (Exception e) { return NetErrors.Classify(e, false); }
        }
        Check(Join("192.168.0.10:abc") == "badAddress", "a port that is not a number -> badAddress");
        Check(Join("no-such-host.invalid:27515") == "badAddress", "a host name that does not exist -> badAddress");
        Check(Join("127.0.0.1:99999") == "badAddress", "a port out of range -> badAddress");
        // The menu field: the IP only, whatever is pasted around it.
        Check(HostAddress.Clean("26.14.7.9") == "26.14.7.9", "a plain IP stays");
        Check(HostAddress.Clean(" 26.14.7.9:27515 ") == "26.14.7.9", "a pasted port is dropped");
        Check(HostAddress.Clean("udp://26.14.7.9:5000/") == "26.14.7.9", "a scheme and a slash are dropped");
        Check(HostAddress.Clean("26.14. 7.9") == "26.14.7.9", "spaces inside are dropped");
        Check(HostAddress.Clean("fe80::1") == "fe80::1", "an IPv6 address is left whole");
        Check(HostAddress.Clean(null) == "", "nothing typed -> empty");
        Check(Join(HostAddress.Clean("127.0.0.1:1")) == "no error", "a cleaned address joins on the mod's port");
    }

    private static void CoopBuildMismatch()
    {
        using (var pair = new CoopPair(0.0, guestBuild: "build-2"))
        {
            pair.PumpUntil(() => pair.Host.Connection == ConnectionState.Connected && pair.Guest.Connection == ConnectionState.Connected, 200);
            pair.Pump(5);
            Check(pair.Host.BuildMismatch && pair.Guest.BuildMismatch, "both see the build mismatch");
            Check(!pair.Host.CanStart && !pair.Host.StartWithSave("s", FakeSave(10, 3)), "no start across builds");
        }
    }
}
