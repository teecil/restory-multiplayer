using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using RestoryVersus.Core;
using Steamworks;
using UnityEngine;

namespace RestoryVersus
{
    // Playing through Steam instead of an IP address. The host opens a friends-only lobby and invites from the Steam
    // overlay; the friend accepts the invite (or "Join game" in the friends list) and enters the lobby, then talks to
    // the lobby's owner over SteamNetworkingMessages (Steam's relays: no addresses, no open ports). The mod's own
    // reliability layer (NetTransport) runs on top, the same as over UDP.
    // The game starts Steam itself and runs its callbacks every frame (Alekrus Steam_Main.Update ->
    // SteamAPI.RunCallbacks), so the mod only registers its own callbacks once Steam is up.
    internal static class SteamNet
    {
        private const string KeyMode = "restory_mode";
        private const string KeyProto = "restory_proto";
        private const float AcceptWindow = 20f; // seconds a session request waits for its sender to show up in the lobby

        private static bool callbacksMade;
        private static Callback<GameLobbyJoinRequested_t> lobbyJoinRequested;
        private static Callback<GameRichPresenceJoinRequested_t> presenceJoinRequested;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequested;
        private static CallResult<LobbyCreated_t> lobbyCreated;
        private static CallResult<LobbyEnter_t> lobbyEntered;
        private static readonly Dictionary<ulong, float> waitingRequests = new Dictionary<ulong, float>();

        internal static SteamLink ActiveLink;

        // A lobby to join that came from an invite (a callback, or "+connect_lobby <id>" when Steam started the game).
        public static ulong PendingInvite { get; set; }

        private static bool broken;
        private static bool available;
        private static float availableCheckedAt = -10f;

        // Steam is up for this game (checked once a second); false for good after a Steamworks failure.
        public static bool Available
        {
            get
            {
                if (broken) return false;
                if (Time.unscaledTime - availableCheckedAt < 1f) return available;
                availableCheckedAt = Time.unscaledTime;
                try { available = SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid(); }
                catch { available = false; }
                return available;
            }
        }

        public static void ReadLaunchArgs()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    PendingInvite = id;
                    Plugin.Log.LogInfo("steam: started from an invite to lobby " + id);
                }
        }

        public static void Tick()
        {
            if (broken) return;
            try { TickSteam(); }
            catch (Exception e)
            {
                broken = true;
                Plugin.Log.LogError("steam: turned off after an error, play by IP: " + e);
            }
        }

        private static void TickSteam()
        {
            if (!callbacksMade)
            {
                if (!Available) return;
                callbacksMade = true;
                lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(r =>
                {
                    Plugin.Log.LogInfo("steam: invite to lobby " + r.m_steamIDLobby.m_SteamID + " accepted");
                    PendingInvite = r.m_steamIDLobby.m_SteamID;
                });
                presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(r =>
                {
                    var parts = (r.m_rgchConnect ?? "").Split(' ');
                    for (int i = 0; i + 1 < parts.Length; i++)
                        if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        {
                            Plugin.Log.LogInfo("steam: join from the friends list, lobby " + id);
                            PendingInvite = id;
                        }
                });
                sessionRequested = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(r =>
                {
                    ulong from = r.m_identityRemote.GetSteamID64();
                    if (!TryAccept(from)) waitingRequests[from] = Time.unscaledTime;
                });
                lobbyCreated = CallResult<LobbyCreated_t>.Create();
                lobbyEntered = CallResult<LobbyEnter_t>.Create();
                Plugin.Log.LogInfo("steam: ready (" + SteamFriends.GetPersonaName() + ")");
            }
            // The friend's first datagram can come before the lobby tells us it joined: keep asking for a while.
            if (waitingRequests.Count > 0)
                foreach (var kv in new List<KeyValuePair<ulong, float>>(waitingRequests))
                    if (TryAccept(kv.Key) || Time.unscaledTime - kv.Value > AcceptWindow) waitingRequests.Remove(kv.Key);
        }

        // Only players in this game's lobby are heard.
        private static bool TryAccept(ulong from)
        {
            var link = ActiveLink;
            if (link == null || link.Disposed || !link.Lobby.IsValid()) return false;
            bool member = false;
            int n = SteamMatchmaking.GetNumLobbyMembers(link.Lobby);
            for (int i = 0; i < n && !member; i++) member = SteamMatchmaking.GetLobbyMemberByIndex(link.Lobby, i).m_SteamID == from;
            if (!member) return false;
            var id = new SteamNetworkingIdentity();
            id.SetSteamID64(from);
            SteamNetworkingMessages.AcceptSessionWithUser(ref id);
            Plugin.Log.LogInfo("steam: talking to " + SteamFriends.GetFriendPersonaName(new CSteamID(from)));
            return true;
        }

        // Host: a friends-only lobby for two, tagged with the mode, then the Steam invite window.
        public static void CreateLobby(SteamLink link, string mode, Action<string> failed)
        {
            ActiveLink = link;
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
            lobbyCreated.Set(call, (r, ioFailure) =>
            {
                if (ioFailure || r.m_eResult != EResult.k_EResultOK)
                {
                    Plugin.Log.LogError("steam: lobby not created (" + r.m_eResult + ")");
                    if (!link.Disposed) failed("steamLobby");
                    return;
                }
                var lobby = new CSteamID(r.m_ulSteamIDLobby);
                if (link.Disposed) { SteamMatchmaking.LeaveLobby(lobby); return; }
                link.Lobby = lobby;
                SteamMatchmaking.SetLobbyData(lobby, KeyMode, mode);
                SteamMatchmaking.SetLobbyData(lobby, KeyProto, Packet.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + lobby.m_SteamID.ToString(CultureInfo.InvariantCulture));
                Plugin.Log.LogInfo("steam: lobby " + lobby.m_SteamID + " for " + mode + " is open");
                Invite();
            });
        }

        public static bool CanInvite => ActiveLink != null && !ActiveLink.Disposed && ActiveLink.Lobby.IsValid();

        public static void Invite()
        {
            if (!CanInvite) return;
            SteamFriends.ActivateGameOverlayInviteDialog(ActiveLink.Lobby);
        }

        // Guest: enter the friend's lobby; ok(host's account, mode) or failed(error code).
        public static void JoinLobby(ulong lobbyId, Action<ulong, string> ok, Action<string> failed)
        {
            var call = SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            lobbyEntered.Set(call, (r, ioFailure) =>
            {
                var lobby = new CSteamID(r.m_ulSteamIDLobby);
                if (ioFailure || r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    Plugin.Log.LogError("steam: could not enter lobby " + lobbyId + " (" + r.m_EChatRoomEnterResponse + ")");
                    failed("steamJoin");
                    return;
                }
                string mode = SteamMatchmaking.GetLobbyData(lobby, KeyMode);
                string proto = SteamMatchmaking.GetLobbyData(lobby, KeyProto);
                var owner = SteamMatchmaking.GetLobbyOwner(lobby);
                if (proto != Packet.ProtocolVersion.ToString(CultureInfo.InvariantCulture))
                {
                    Plugin.Log.LogError("steam: the host runs protocol " + proto + ", this mod " + Packet.ProtocolVersion);
                    SteamMatchmaking.LeaveLobby(lobby);
                    failed("steamVersion");
                    return;
                }
                Plugin.Log.LogInfo("steam: in " + SteamFriends.GetFriendPersonaName(owner) + "'s lobby (" + mode + ")");
                ok(owner.m_SteamID, mode);
                if (ActiveLink != null && ActiveLink.HostPeer != null && ActiveLink.HostPeer.Id == owner.m_SteamID) ActiveLink.Lobby = lobby;
                else SteamMatchmaking.LeaveLobby(lobby); // the join was dropped meanwhile
            });
        }

        internal static void Left(SteamLink link)
        {
            if (link.Lobby.IsValid())
            {
                SteamMatchmaking.LeaveLobby(link.Lobby);
                Plugin.Log.LogInfo("steam: left lobby " + link.Lobby.m_SteamID);
            }
            if (ReferenceEquals(ActiveLink, link))
            {
                ActiveLink = null;
                SteamFriends.ClearRichPresence();
            }
        }
    }

    // Datagrams through SteamNetworkingMessages, unreliable (the reliability is NetTransport's), on channel 0.
    internal sealed class SteamLink : IdLink
    {
        private const int Channel = 0;
        private const int SendFlags = Constants.k_nSteamNetworkingSend_UnreliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
        private readonly IntPtr[] received = new IntPtr[64];
        private int receivedCount, receivedNext;

        public CSteamID Lobby = CSteamID.Nil;
        public bool Disposed { get; private set; }
        public override string Route => "Steam";

        // hostId: 0 for the host, the host's account for a guest.
        public SteamLink(ulong hostId) : base(hostId)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            SteamNet.ActiveLink = this;
        }

        protected override void SendRaw(ulong to, byte[] data)
        {
            if (Disposed) return;
            var id = new SteamNetworkingIdentity();
            id.SetSteamID64(to);
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { SteamNetworkingMessages.SendMessageToUser(ref id, pin.AddrOfPinnedObject(), (uint)data.Length, SendFlags, Channel); }
            catch (InvalidOperationException) { } // Steam shut down
            finally { pin.Free(); }
        }

        protected override bool ReceiveRaw(out ulong from, out byte[] data)
        {
            from = 0;
            data = null;
            if (Disposed) return false;
            if (receivedNext >= receivedCount)
            {
                receivedNext = 0;
                try { receivedCount = Math.Max(0, SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, received, received.Length)); }
                catch (InvalidOperationException) { receivedCount = 0; }
                if (receivedCount == 0) return false;
            }
            var ptr = received[receivedNext++];
            try
            {
                var m = SteamNetworkingMessage_t.FromIntPtr(ptr);
                data = new byte[m.m_cbSize];
                Marshal.Copy(m.m_pData, data, 0, m.m_cbSize);
                from = m.m_identityPeer.GetSteamID64();
            }
            finally { SteamNetworkingMessage_t.Release(ptr); }
            return true;
        }

        public override void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            while (receivedNext < receivedCount) SteamNetworkingMessage_t.Release(received[receivedNext++]);
            try { SteamNet.Left(this); }
            catch (InvalidOperationException) { }
        }
    }
}
