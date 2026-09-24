using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DragNWashCoop;

internal sealed class Peer
{
    internal CSteamID SteamId;
    internal HSteamNetConnection Connection;
    internal LocalLink? Local;          // local test peers use this instead of a Steam connection
    internal readonly Queue<byte[]> Bulk = new();   // large reliable data waiting for room in Steam's buffer
    internal bool Authenticated;
    internal bool Ready;
    internal uint LastPoseSequence;
    internal float LastHeard;
}

/// <summary>
/// Steam lobby + relay transport, plus a localhost test mode for two copies of the game on one PC.
/// All callbacks and message dispatch run on Unity's main thread.
/// </summary>
internal sealed class SteamSession : IDisposable
{
    private const int VirtualPort = 713;
    internal const int Capacity = 4;
    // Both local test copies share one Steam account, so each uses a made-up ID from its process ID.
    private const ulong LocalIdTag = 0x7F00_0000_0000_0000;
    private static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;
    private readonly CoopPlugin owner;
    private readonly Dictionary<HSteamNetConnection, Peer> peers = new();
    private readonly HashSet<ulong> removedFromLobby = new();
    private readonly IntPtr[] messagePtrs = new IntPtr[32];
    private CallResult<LobbyCreated_t>? createResult;
    private Callback<LobbyEnter_t>? lobbyEnter;
    private Callback<GameLobbyJoinRequested_t>? joinRequest;
    private Callback<GameRichPresenceJoinRequested_t>? presenceJoin;
    private bool presenceSet;                      // our "connect" rich presence, so friends see Join Game
    private Callback<LobbyChatUpdate_t>? lobbyUpdate;
    private Callback<SteamNetConnectionStatusChangedCallback_t>? connectionStatus;
    private HSteamListenSocket listenSocket = HSteamListenSocket.Invalid;
    private HSteamNetConnection hostConnection = HSteamNetConnection.Invalid;
    private CSteamID lobby = CSteamID.Nil;
    private CSteamID hostId = CSteamID.Nil;
    private bool callbacksReady;
    private uint nextSequence;
    private float nextHeartbeat;
    private readonly string modFingerprint = Fingerprint();
    private bool awaitingLobbyMetadata;
    private float lobbyMetadataDeadline;
    private TcpListener? localListener;
    private uint nextLocalHandle;
    private readonly List<byte[]> localPackets = new();
    private bool pendingCreate;                    // a lobby we asked for; late ones are left at once
    private CSteamID pendingJoin = CSteamID.Nil;   // the lobby we asked to join
    private float handshakeDeadline;
    private bool relayStarted;
    private const int BulkPendingBudget = 256 * 1024;

    internal CoopRole Role { get; private set; }
    internal string Status { get; private set; } = "Steam is starting";
    internal bool IsReady => callbacksReady;
    internal bool IsPlaying => Role != CoopRole.None && peers.Values.Any(p => p.Authenticated);
    internal bool IsLocal { get; private set; }
    internal int LocalPort { get; set; } = 47713;
    internal CSteamID Lobby => lobby;
    internal CSteamID HostId => hostId;
    internal IReadOnlyCollection<Peer> Peers => peers.Values;
    internal int GameBuild => callbacksReady ? SteamApps.GetAppBuildId() : 0;
    internal string ModFingerprint => modFingerprint;
    /// <summary>This player's ID: the Steam ID, or a made-up one in a local test.</summary>
    internal ulong SelfId => IsLocal ? LocalIdTag | ProcessId : (ulong)SteamUser.GetSteamID();
    /// <summary>The game is quitting: leaving must not load scenes, and Steam may already be gone.</summary>
    internal bool Quitting { get; set; }
    internal event Action<Peer, Envelope>? PacketReceived;
    internal event Action<Peer>? PeerReady;
    internal event Action<Peer>? PeerLeft;
    internal event Action? LobbyChanged;

    internal SteamSession(CoopPlugin owner) => this.owner = owner;

    internal static bool IsLocalTestId(ulong id) => (id & 0xFFFF_FFFF_0000_0000) == LocalIdTag;

    internal string NameOf(ulong id)
    {
        if (IsLocalTestId(id)) return $"Local player {(uint)id}";
        if (id == 0 || !callbacksReady) return "Unknown player";
        return SteamFriends.GetFriendPersonaName(new CSteamID(id));
    }

    internal void Tick()
    {
        if (!callbacksReady && SteamManager.Initialized)
        {
            createResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            joinRequest = Callback<GameLobbyJoinRequested_t>.Create(x => Join(x.m_steamIDLobby));
            // Join Game on a friend while this copy is running (not running: Steam puts it on the command line)
            presenceJoin = Callback<GameRichPresenceJoinRequested_t>.Create(x => JoinFromConnect(x.m_rgchConnect.Split(' ')));
            lobbyUpdate = Callback<LobbyChatUpdate_t>.Create(_ => LobbyChanged?.Invoke());
            connectionStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatus);
            callbacksReady = true;
            if (Role == CoopRole.None) Status = "Steam ready";
            TryCommandLineInvite();
        }
        if (awaitingLobbyMetadata)
        {
            TryConnectToLobby();
            if (awaitingLobbyMetadata && Time.realtimeSinceStartup >= lobbyMetadataDeadline)
            {
                awaitingLobbyMetadata = false;
                SteamMatchmaking.LeaveLobby(lobby);
                lobby = CSteamID.Nil;
                Status = "Steam lobby data did not arrive";
            }
        }
        if (Role == CoopRole.None) return;
        AcceptLocal();
        foreach (var peer in peers.Values.ToArray())
            if (peer.Connection != HSteamNetConnection.Invalid)
                PollPeer(peer);
        foreach (var peer in peers.Values)
            if (peer.Bulk.Count > 0) FlushBulk(peer);
        // a host that never answers (e.g. a different co-op version, whose packets are dropped unread)
        if (Role == CoopRole.Guest && peers.TryGetValue(hostConnection, out var hostPeer) && !hostPeer.Authenticated &&
            Time.realtimeSinceStartup >= handshakeDeadline)
        {
            Leave();
            Status = "The host did not answer. Is it running the same co-op version?";
            return;
        }
        if (Time.realtimeSinceStartup >= nextHeartbeat)
        {
            nextHeartbeat = Time.realtimeSinceStartup + 2f;
            foreach (var peer in peers.Values.ToArray())
            {
                if (!peer.Authenticated) continue;
                if (Time.realtimeSinceStartup - peer.LastHeard > 20f)
                {
                    owner.Log.LogWarning($"{NameOf((ulong)peer.SteamId)} timed out");
                    CloseLink(peer, "Timed out");
                    RemovePeer(peer);
                    if (Role == CoopRole.Guest && peer.Connection == hostConnection)
                    {
                        Leave();
                        Status = "Host connection timed out";
                        break;
                    }
                }
                else Send(peer, PacketKind.Ping, 0, w => w.Write((byte)0), false);
            }
        }
    }

    internal void Host()
    {
        if (!callbacksReady || Role != CoopRole.None) return;
        if (GameBuild <= 0) { Status = "Steam game build could not be identified"; return; }
        Role = CoopRole.Host;
        hostId = SteamUser.GetSteamID();
        StartRelay();
        pendingCreate = true;
        Status = "Creating Steam lobby";
        // friends-only, not private: a private lobby takes invites only, so Join Game on the host would fail
        createResult!.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Capacity));
    }

    internal void Join(CSteamID targetLobby)
    {
        if (!callbacksReady) { Status = "Steam is not ready"; return; }
        if (Role != CoopRole.None) Leave();
        StartRelay();
        pendingJoin = targetLobby;
        Status = "Joining lobby";
        SteamMatchmaking.JoinLobby(targetLobby);
    }

    /// <summary>Local test: listen on 127.0.0.1 for other copies of the game on this PC.</summary>
    internal void HostLocal()
    {
        if (Role != CoopRole.None) return;
        try
        {
            localListener = new TcpListener(IPAddress.Loopback, LocalPort);
            // lets the port be reused right after a previous test (Windows would allow a second listener instead)
            if (Application.platform != RuntimePlatform.WindowsPlayer)
                localListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            localListener.Start();
        }
        catch (SocketException e)
        {
            localListener = null;
            Status = $"Could not open local test port {LocalPort}: {e.SocketErrorCode}";
            owner.Log.LogWarning(Status);
            return;
        }
        IsLocal = true;
        Role = CoopRole.Host;
        hostId = new CSteamID(SelfId);
        Status = $"Local test host on port {LocalPort}. Start another copy and join.";
        owner.Log.LogInfo($"Hosting a local test as {NameOf(SelfId)} on 127.0.0.1:{LocalPort}");
        LobbyChanged?.Invoke();
    }

    /// <summary>Local test: connect to a copy of the game on this PC that is hosting.</summary>
    internal void JoinLocal()
    {
        if (Role != CoopRole.None) Leave();
        var link = LocalLink.Connect(LocalPort, out string error);
        if (link == null)
        {
            Status = $"No local test host on port {LocalPort} ({error})";
            owner.Log.LogInfo(Status);
            return;
        }
        IsLocal = true;
        Role = CoopRole.Guest;
        hostConnection = new HSteamNetConnection(++nextLocalHandle);
        var host = new Peer { Connection = hostConnection, Local = link, LastHeard = Time.realtimeSinceStartup };
        peers[hostConnection] = host;
        Status = "Checking host version";
        handshakeDeadline = Time.realtimeSinceStartup + 10f;
        owner.Log.LogInfo($"Joining the local test on 127.0.0.1:{LocalPort} as {NameOf(SelfId)}");
        SendHello(host);
        LobbyChanged?.Invoke();
    }

    private void AcceptLocal()
    {
        if (localListener == null) return;
        try
        {
            while (localListener.Pending())
            {
                var socket = localListener.AcceptSocket();
                if (peers.Count >= Capacity - 1) { socket.Close(); continue; }
                var handle = new HSteamNetConnection(++nextLocalHandle);
                peers[handle] = new Peer
                {
                    Connection = handle, Local = LocalLink.Accept(socket), LastHeard = Time.realtimeSinceStartup
                };
            }
        }
        catch (SocketException e) { owner.Log.LogWarning($"Local test accept failed: {e.SocketErrorCode}"); }
    }

    internal void InviteFriends()
    {
        if (lobby != CSteamID.Nil) SteamFriends.ActivateGameOverlayInviteDialog(lobby);
    }

    internal void Kick(Peer peer)
    {
        if (Role != CoopRole.Host) return;
        removedFromLobby.Add((ulong)peer.SteamId);
        Send(peer, PacketKind.Reject, 0, w => w.WriteShortString("Removed by host"));
        CloseLink(peer, "Removed by host", linger: true);
        RemovePeer(peer);
    }

    /// <param name="linger">Deliver what is still queued first (a Reject: without it Steam drops the reason).</param>
    private void CloseLink(Peer peer, string reason, bool linger = false)
    {
        if (peer.Local != null) peer.Local.Dispose();
        else SteamNetworkingSockets.CloseConnection(peer.Connection, 0, reason, linger);
    }

    /// <param name="returnToMenu">Load the main menu for a guest in a session (not when the game itself is
    /// already going there, e.g. its own Quit, or quitting).</param>
    internal void Leave(bool returnToMenu = true)
    {
        bool wasGuest = Role == CoopRole.Guest && owner.World.Started;
        if (returnToMenu && !Quitting && wasGuest && SceneManager.GetActiveScene().name != "StartScene")
        {
            try { SceneManager.LoadScene("StartScene"); }
            catch (Exception e) { owner.Log.LogWarning($"Could not return guest to menu: {e.Message}"); }
        }
        try
        {
            foreach (var peer in peers.Values.ToArray()) CloseLink(peer, "Session ended");
            if (listenSocket != HSteamListenSocket.Invalid)
                SteamNetworkingSockets.CloseListenSocket(listenSocket);
            if (lobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(lobby);
            if (presenceSet) SteamFriends.SetRichPresence("connect", "");   // an empty value removes the key
            presenceSet = false;
        }
        catch (Exception e) { owner.Log.LogWarning($"Steam cleanup failed while leaving: {e.Message}"); }
        foreach (var peer in peers.Values.ToArray()) RemovePeer(peer);
        localListener?.Stop();
        localListener = null;
        pendingCreate = false;
        pendingJoin = CSteamID.Nil;
        peers.Clear();
        removedFromLobby.Clear();
        awaitingLobbyMetadata = false;
        hostConnection = HSteamNetConnection.Invalid;
        listenSocket = HSteamListenSocket.Invalid;
        lobby = CSteamID.Nil;
        hostId = CSteamID.Nil;
        Role = CoopRole.None;
        IsLocal = false;
        Status = callbacksReady ? "Steam ready" : "Not connected";
        owner.World.Reset();
        if (wasGuest) Time.timeScale = 1f;
        LobbyChanged?.Invoke();
    }

    internal void Broadcast(PacketKind kind, uint epoch, Action<System.IO.BinaryWriter>? payload = null, bool reliable = true)
    {
        if (Role != CoopRole.Host) return;
        foreach (var peer in peers.Values.ToArray())
            if (peer.Authenticated) Send(peer, kind, epoch, payload, reliable);
    }

    internal void SendToHost(PacketKind kind, uint epoch, Action<System.IO.BinaryWriter>? payload = null, bool reliable = true)
    {
        if (Role != CoopRole.Guest || !peers.TryGetValue(hostConnection, out var peer) || !peer.Authenticated) return;
        Send(peer, kind, epoch, payload, reliable);
    }

    internal void Send(Peer peer, PacketKind kind, uint epoch, Action<System.IO.BinaryWriter>? payload = null, bool reliable = true)
    {
        if (peer.Connection == HSteamNetConnection.Invalid) return;
        byte[] data;
        try { data = Envelope.Encode(kind, epoch, ++nextSequence, payload); }
        catch (Exception e) { owner.Log.LogError($"Could not encode co-op packet: {e}"); return; }
        if (peer.Local != null)
        {
            peer.Local.Send(data, droppable: !reliable);   // local test: in order; stale poses dropped when backed up
            return;
        }
        SendSteam(peer, data, reliable);
    }

    private void SendSteam(Peer peer, byte[] data, bool reliable)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var flags = reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
            var result = SteamNetworkingSockets.SendMessageToConnection(peer.Connection, handle.AddrOfPinnedObject(),
                (uint)data.Length, flags, out _);
            if (result != EResult.k_EResultOK) owner.Log.LogWarning($"Steam send failed: {result}");
        }
        finally { handle.Free(); }
    }

    /// <summary>
    /// Large reliable data (mask snapshots). On Steam it is fed in only while little is waiting to go
    /// out: Steam refuses anything beyond its send buffer, and a flood would also delay everything else.
    /// </summary>
    internal void SendBulk(Peer peer, PacketKind kind, uint epoch, Action<System.IO.BinaryWriter> payload)
    {
        if (peer.Local != null) { Send(peer, kind, epoch, payload); return; }
        if (peer.Connection == HSteamNetConnection.Invalid) return;
        try { peer.Bulk.Enqueue(Envelope.Encode(kind, epoch, ++nextSequence, payload)); }
        catch (Exception e) { owner.Log.LogError($"Could not encode co-op packet: {e}"); }
    }

    internal bool HasBulkPending(Peer peer) => peer.Bulk.Count > 0;

    private void FlushBulk(Peer peer)
    {
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        while (peer.Bulk.Count > 0)
        {
            var status = default(SteamNetConnectionRealTimeStatus_t);
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(peer.Connection, ref status, 0, ref lanes) != EResult.k_EResultOK ||
                status.m_cbPendingReliable > BulkPendingBudget) return;
            SendSteam(peer, peer.Bulk.Dequeue(), true);
        }
    }

    /// <summary>Steam relays and roomier send limits, only once Steam networking is actually used.</summary>
    private void StartRelay()
    {
        if (relayStarted) return;
        relayStarted = true;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024);
        SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, 256 * 1024);
        SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 4 * 1024 * 1024);
    }

    private static void SetGlobalInt(ESteamNetworkingConfigValue key, int value)
    {
        var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            SteamNetworkingUtils.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, handle.AddrOfPinnedObject());
        }
        finally { handle.Free(); }
    }

    private void OnLobbyCreated(LobbyCreated_t value, bool ioFailure)
    {
        bool wanted = pendingCreate && Role == CoopRole.Host && !IsLocal;
        pendingCreate = false;
        if (ioFailure || value.m_eResult != EResult.k_EResultOK)
        {
            if (!wanted) return;
            Role = CoopRole.None;
            Status = $"Could not create lobby: {value.m_eResult}";
            return;
        }
        var created = new CSteamID(value.m_ulSteamIDLobby);
        if (!wanted)
        {
            SteamMatchmaking.LeaveLobby(created);   // the player left or switched to something else meanwhile
            return;
        }
        lobby = created;
        SteamMatchmaking.SetLobbyData(lobby, "dnw_coop_protocol", Envelope.Version.ToString());
        SteamMatchmaking.SetLobbyData(lobby, "dnw_coop_build", GameBuild.ToString());
        SteamMatchmaking.SetLobbyData(lobby, "dnw_coop_mod", modFingerprint);
        listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
        if (listenSocket == HSteamListenSocket.Invalid)
        {
            Leave();
            Status = "Could not open Steam P2P socket";
            return;
        }
        // Steam shows Join Game on this player in friends' lists, and hands the joiner this string
        presenceSet = SteamFriends.SetRichPresence("connect", $"+connect_lobby {lobby.m_SteamID}");
        Status = "Lobby ready. Invite friends.";
        LobbyChanged?.Invoke();
    }

    private void OnLobbyEntered(LobbyEnter_t value)
    {
        if (Role == CoopRole.Host) return;
        var entered = new CSteamID(value.m_ulSteamIDLobby);
        if (entered != pendingJoin || Role != CoopRole.None)
        {
            // a lobby we no longer want (left meanwhile, or our own lobby after leaving it)
            if (entered != lobby) SteamMatchmaking.LeaveLobby(entered);
            return;
        }
        pendingJoin = CSteamID.Nil;
        if (value.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            Status = value.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseFull
                ? "That game is full" : "Could not enter Steam lobby";
            return;
        }
        lobby = entered;
        awaitingLobbyMetadata = true;
        lobbyMetadataDeadline = Time.realtimeSinceStartup + 8f;
        TryConnectToLobby();
    }

    private void TryConnectToLobby()
    {
        if (!awaitingLobbyMetadata || lobby == CSteamID.Nil) return;
        if (Role != CoopRole.None) { awaitingLobbyMetadata = false; return; }
        var protocol = SteamMatchmaking.GetLobbyData(lobby, "dnw_coop_protocol");
        var build = SteamMatchmaking.GetLobbyData(lobby, "dnw_coop_build");
        var mod = SteamMatchmaking.GetLobbyData(lobby, "dnw_coop_mod");
        if (string.IsNullOrEmpty(protocol) || string.IsNullOrEmpty(build) || string.IsNullOrEmpty(mod)) return;
        awaitingLobbyMetadata = false;
        if (protocol != Envelope.Version.ToString() || build != GameBuild.ToString() || mod != modFingerprint)
        {
            Status = "Game build or co-op mod version differs from host";
            SteamMatchmaking.LeaveLobby(lobby);
            lobby = CSteamID.Nil;
            return;
        }
        hostId = SteamMatchmaking.GetLobbyOwner(lobby);
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(hostId);
        hostConnection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
        if (hostConnection == HSteamNetConnection.Invalid)
        {
            Leave();
            Status = "Could not connect to host";
            return;
        }
        Role = CoopRole.Guest;
        peers[hostConnection] = new Peer { SteamId = hostId, Connection = hostConnection };
        handshakeDeadline = Time.realtimeSinceStartup + 30f;   // Steam relays can take a while to connect
        Status = "Connecting to host";
        LobbyChanged?.Invoke();
    }

    private void OnConnectionStatus(SteamNetConnectionStatusChangedCallback_t change)
    {
        var state = change.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
        {
            if (Role != CoopRole.Host || change.m_info.m_hListenSocket != listenSocket) return;
            var id = change.m_info.m_identityRemote.GetSteamID();
            if (!IsLobbyMember(id) || removedFromLobby.Contains((ulong)id) || peers.Count >= Capacity - 1)
            {
                SteamNetworkingSockets.CloseConnection(change.m_hConn, 0, "Not invited or lobby full", false);
                return;
            }
            if (SteamNetworkingSockets.AcceptConnection(change.m_hConn) != EResult.k_EResultOK)
            {
                SteamNetworkingSockets.CloseConnection(change.m_hConn, 0, "Accept failed", false);
                return;
            }
            peers[change.m_hConn] = new Peer { SteamId = id, Connection = change.m_hConn };
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
        {
            if (Role == CoopRole.Guest && change.m_hConn == hostConnection && peers.TryGetValue(hostConnection, out var host))
            {
                Status = "Checking host version";
                SendHello(host);
            }
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
            state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
        {
            // read what the host sent before closing (a Reject says why) before this frame's poll would
            if (Role == CoopRole.Guest && change.m_hConn == hostConnection && peers.TryGetValue(hostConnection, out var host))
                PollPeer(host);
            SteamNetworkingSockets.CloseConnection(change.m_hConn, 0, "Connection closed", false);
            OnLinkClosed(change.m_hConn, change.m_info.m_szEndDebug);
        }
    }

    private void OnLinkClosed(HSteamNetConnection connection, string? reason = null)
    {
        if (peers.TryGetValue(connection, out var peer))
        {
            peer.Local?.Dispose();
            RemovePeer(peer);
        }
        if (Role == CoopRole.Guest && connection == hostConnection)
        {
            Leave();
            Status = string.IsNullOrEmpty(reason) || reason == "Session ended"
                ? "Host left. Return to single player or rejoin later."
                : $"Disconnected: {reason}";
        }
    }

    // A local test uses a made-up player ID and a build ID from this install; Steam uses the real ones.
    private int HandshakeBuild => IsLocal ? StableHash(Application.buildGUID) : GameBuild;

    private void SendHello(Peer host) => Send(host, PacketKind.Hello, 0, w =>
    {
        w.Write(HandshakeBuild);
        w.WriteShortString(modFingerprint);
        w.Write(SelfId);
        w.WriteShortString(IsLocal ? NameOf(SelfId) : SteamFriends.GetPersonaName());
    });

    private void PollPeer(Peer peer)
    {
        if (peer.Local != null)
        {
            localPackets.Clear();
            bool open = peer.Local.Poll(localPackets);
            foreach (var bytes in localPackets)
            {
                if (!peers.ContainsKey(peer.Connection)) return;   // removed by an earlier packet
                Dispatch(peer, bytes);
            }
            if (!open && peers.ContainsKey(peer.Connection)) OnLinkClosed(peer.Connection);
            return;
        }
        int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(peer.Connection, messagePtrs, messagePtrs.Length);
        for (int i = 0; i < count; i++)
        {
            IntPtr pointer = messagePtrs[i];
            try
            {
                var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                if (message.m_cbSize < 0 || message.m_cbSize > Envelope.MaxPacketBytes) continue;
                var bytes = new byte[message.m_cbSize];
                Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                Dispatch(peer, bytes);
            }
            catch (Exception e) { owner.Log.LogWarning($"Rejected malformed co-op packet: {e.Message}"); }
            finally { SteamNetworkingMessage_t.Release(pointer); messagePtrs[i] = IntPtr.Zero; }
        }
    }

    private void Dispatch(Peer peer, byte[] bytes)
    {
        try
        {
            if (!Envelope.TryDecode(bytes, out var envelope)) return;
            using (envelope.Reader)
            {
                peer.LastHeard = Time.realtimeSinceStartup;
                if (!peer.Authenticated) HandleHandshake(peer, envelope);
                else if (envelope.Kind == PacketKind.Reject && Role == CoopRole.Guest && peer.Connection == hostConnection)
                {
                    var reason = envelope.Reader.ReadShortString();
                    Leave();
                    Status = reason;
                }
                else if ((envelope.Kind == PacketKind.Pose || envelope.Kind == PacketKind.DragonPose) &&
                         envelope.Sequence <= peer.LastPoseSequence) { }
                else
                {
                    if (envelope.Kind == PacketKind.Pose || envelope.Kind == PacketKind.DragonPose)
                        peer.LastPoseSequence = envelope.Sequence;
                    PacketReceived?.Invoke(peer, envelope);
                }
            }
        }
        catch (Exception e) { owner.Log.LogWarning($"Rejected malformed co-op packet: {e.Message}"); }
    }

    private void HandleHandshake(Peer peer, Envelope message)
    {
        if (Role == CoopRole.Host && message.Kind == PacketKind.Hello)
        {
            int build = message.Reader.ReadInt32();
            string mod = message.Reader.ReadShortString();
            ulong claimedId = message.Reader.ReadUInt64();
            _ = message.Reader.ReadShortString();
            bool identity = peer.Local != null
                ? IsLocalTestId(claimedId) && claimedId != SelfId && !removedFromLobby.Contains(claimedId)
                : claimedId == (ulong)peer.SteamId && IsLobbyMember(peer.SteamId);
            if (build != HandshakeBuild || mod != modFingerprint || !identity)
            {
                owner.Log.LogWarning($"Rejected {NameOf(claimedId)}: game build, co-op DLL or identity differs");
                Send(peer, PacketKind.Reject, 0, w => w.WriteShortString("Game build or Steam identity mismatch"));
                CloseLink(peer, "Handshake failed", linger: true);
                RemovePeer(peer);
                return;
            }
            if (peer.Local != null) peer.SteamId = new CSteamID(claimedId);
            // the same player reconnecting (e.g. after a crash) replaces the connection that has not timed out yet
            foreach (var stale in peers.Values.Where(p => p != peer && (ulong)p.SteamId == claimedId).ToArray())
            {
                CloseLink(stale, "Replaced by a new connection");
                RemovePeer(stale);
            }
            peer.Authenticated = true;
            owner.Log.LogInfo($"{NameOf(claimedId)} joined");
            Send(peer, PacketKind.Welcome, 0, w => w.Write((ulong)hostId));
            PeerReady?.Invoke(peer);
            LobbyChanged?.Invoke();
        }
        else if (Role == CoopRole.Guest && message.Kind == PacketKind.Welcome)
        {
            ulong id = message.Reader.ReadUInt64();
            if (peer.Local != null && IsLocalTestId(id) && id != SelfId)
            {
                hostId = new CSteamID(id);
                peer.SteamId = hostId;
            }
            if (id != (ulong)hostId) { Leave(); Status = "Host identity mismatch"; return; }
            peer.Authenticated = true;
            Status = "Connected to host";
            owner.Log.LogInfo($"Connected to host {NameOf(id)}");
            PeerReady?.Invoke(peer);
        }
        else if (Role == CoopRole.Guest && message.Kind == PacketKind.Reject)
        {
            var reason = message.Reader.ReadShortString();
            owner.Log.LogWarning($"Host rejected this copy: {reason}");
            Leave();
            Status = reason;
        }
    }

    private bool IsLobbyMember(CSteamID id)
    {
        if (lobby == CSteamID.Nil) return false;
        int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < count; i++)
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id) return true;
        return false;
    }

    private void RemovePeer(Peer peer)
    {
        if (!peers.Remove(peer.Connection)) return;
        if (peer.Authenticated) PeerLeft?.Invoke(peer);   // a connection that never joined has nothing to clean up
        LobbyChanged?.Invoke();
    }

    private void TryCommandLineInvite() => JoinFromConnect(Environment.GetCommandLineArgs());

    /// <summary>Joins the lobby in "+connect_lobby &lt;id&gt;" (an invite, or a friend's connect rich presence).</summary>
    private void JoinFromConnect(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
            {
                Join(new CSteamID(id));
                return;
            }
    }

    public void Dispose()
    {
        if (callbacksReady || Role != CoopRole.None) Leave();
        createResult?.Dispose();
        lobbyEnter?.Dispose();
        joinRequest?.Dispose();
        presenceJoin?.Dispose();
        lobbyUpdate?.Dispose();
        connectionStatus?.Dispose();
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text) hash = (hash ^ c) * 16777619;
            return (int)hash;
        }
    }

    private static string Fingerprint()
    {
        try
        {
            using var stream = File.OpenRead(typeof(SteamSession).Assembly.Location);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream), 0, 12).Replace("-", "");
        }
        catch { return "unknown-build"; }
    }
}
