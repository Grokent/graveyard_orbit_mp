using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Steam P2P transport. All peers are lobby members; packets ride Steam's
    /// reliable/unreliable P2P channels (relay enabled, so NAT traversal is free).
    /// </summary>
    public sealed class SteamTransport : NetTransport
    {
        public const int MaxPlayers = 4;
        private const int Channel = 0;
        private const int MaxPacket = 64 * 1024;

        private readonly CSteamID _lobby;
        private readonly bool _isHost;
        private readonly HashSet<ulong> _members = new HashSet<ulong>();
        private readonly List<ulong> _joined = new List<ulong>();
        private readonly List<ulong> _left = new List<ulong>();
        private readonly byte[] _buf = new byte[MaxPacket];
        private bool _disposed;

        public override bool IsHost => _isHost;
        public override PeerId LocalId => new PeerId(SteamUser.GetSteamID().m_SteamID);
        public override string Name => "steam";

        public override IReadOnlyCollection<PeerId> Peers
        {
            get
            {
                var list = new List<PeerId>();
                foreach (ulong m in _members)
                {
                    if (m != LocalId.Value) list.Add(new PeerId(m));
                }
                return list;
            }
        }

        public SteamTransport(CSteamID lobby)
        {
            _lobby = lobby;
            _isHost = SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID();
            SteamNetworking.AllowP2PPacketRelay(true);
            RefreshMembers(initial: true);
            string ids = string.Join(",", _members.Select(m => m.ToString()));
            CoopPlugin.Log.LogInfo(
                $"[steam] transport ready — lobby {lobby.m_SteamID}, host={_isHost}, " +
                $"local={SteamUser.GetSteamID().m_SteamID}, members=[{ids}]");
        }

        private void RefreshMembers(bool initial = false)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobby);
            var now = new HashSet<ulong>();
            for (int i = 0; i < n; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i);
                if (member.IsValid()) now.Add(member.m_SteamID);
            }

            foreach (ulong m in now)
            {
                if (_members.Add(m) && m != LocalId.Value && !initial)
                {
                    _joined.Add(m);
                    CoopPlugin.Log.LogInfo($"[steam] lobby member joined: {m}");
                }
            }
            foreach (ulong m in _members.ToArray())
            {
                if (!now.Contains(m))
                {
                    _members.Remove(m);
                    if (m != LocalId.Value)
                    {
                        _left.Add(m);
                        CoopPlugin.Log.LogInfo($"[steam] lobby member left: {m}");
                    }
                }
            }
        }

        public override void Send(PeerId to, byte[] data, Delivery delivery)
        {
            if (data == null || data.Length == 0 || data.Length > MaxPacket) return;
            EP2PSend mode = delivery == Delivery.Reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
            bool ok = SteamNetworking.SendP2PPacket(new CSteamID(to.Value), data, (uint)data.Length, mode, Channel);
            if (!ok && CoopPlugin.Cfg.Verbose.Value)
            {
                CoopPlugin.Log.LogDebug($"[steam] send to {to} failed");
            }
        }

        public override void Poll()
        {
            try { SteamAPI.RunCallbacks(); }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("[steam] RunCallbacks: " + ex.Message); }

            RefreshMembers();

            while (true)
            {
                uint size;
                CSteamID remote;
                if (!SteamNetworking.ReadP2PPacket(_buf, (uint)_buf.Length, out size, out remote, Channel))
                {
                    break;
                }
                if (size == 0 || size > _buf.Length) continue;
                byte[] payload = new byte[size];
                Buffer.BlockCopy(_buf, 0, payload, 0, (int)size);
                RaisePayload(new PeerId(remote.m_SteamID), payload);
            }

            if (_joined.Count > 0)
            {
                foreach (ulong m in _joined) RaiseJoined(new PeerId(m));
                _joined.Clear();
            }
            if (_left.Count > 0)
            {
                foreach (ulong m in _left) RaiseLeft(new PeerId(m));
                _left.Clear();
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SteamMatchmaking.LeaveLobby(_lobby); } catch { }
            try { SteamFriends.SetRichPresence("connect", ""); } catch { }
        }
    }

    /// <summary>Steam lobby lifecycle (create / join / invite).</summary>
    public static class SteamLobby
    {
        private static Callback<LobbyCreated_t> _created;
        private static Callback<LobbyEnter_t> _entered;
        private static Callback<GameLobbyJoinRequested_t> _joinRequested;
        private static Callback<GameRichPresenceJoinRequested_t> _richJoin;
        private static Callback<P2PSessionRequest_t> _sessionRequest;
        private static Callback<P2PSessionConnectFail_t> _sessionFail;
        private static bool _registered;
        private static bool _steamReady;
        private static bool _autoInitDone;

        public static CSteamID Current;
        public static bool HasLobby => Current.IsValid();

        public static string ConnectString()
        {
            return HasLobby ? "+connect_lobby " + Current.m_SteamID : "";
        }

        private static ulong ParseLobbyId(string connect)
        {
            if (string.IsNullOrEmpty(connect)) return 0;
            string[] parts = connect.Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong id) && id != 0)
                {
                    return id;
                }
            }
            return ulong.TryParse(connect.Trim(), out ulong direct) ? direct : 0;
        }

        public static bool EnsureSteam()
        {
            if (_steamReady) return true;
            try
            {
                // Reuse the game's own Steam init + callback pump. Never call
                // SteamAPI.Init ourselves: it would race SplashBoot and can make
                // the game think Steam was never initialised.
                bool running = SteamAPI.IsSteamRunning();
                _steamReady = running;
                if (!running)
                {
                    CoopPlugin.Log.LogWarning(
                        "[steam] Steam API not running yet. Launch the game through Steam to use P2P.");
                }
                if (running && !_registered)
                {
                    _created = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
                    _entered = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                    _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                    _richJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
                    _sessionRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
                    _sessionFail = Callback<P2PSessionConnectFail_t>.Create(OnP2PSessionConnectFail);
                    _registered = true;
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[steam] init failed: " + ex);
                _steamReady = false;
            }
            return _steamReady;
        }

        public static void CreateLobby()
        {
            if (!EnsureSteam()) return;
            CoopPlugin.Log.LogInfo("[steam] creating lobby…");
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, SteamTransport.MaxPlayers);
        }

        /// <summary>
        /// Called every frame until Steam is ready: registers lobby/P2P callbacks
        /// early (so invites aren't missed) and honours Steam's +connect_lobby.
        /// </summary>
        public static void TickAutoInit()
        {
            if (_autoInitDone) return;
            try
            {
                // SplashBoot initialises Steam; never race it with our own Init.
                if (!GraveyardOrbit.SteamBridge.Initialized) return;
                if (!EnsureSteam()) return;
                _autoInitDone = true;
                TryConnectLobbyFromCommandLine();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogDebug("[steam] auto-init deferred: " + ex.Message);
            }
        }

        private static void TryConnectLobbyFromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    string a = args[i];
                    if ((a == "+connect_lobby" || a == "-connect_lobby" || a == "--connect_lobby") &&
                        ulong.TryParse(args[i + 1], out ulong id) && id != 0)
                    {
                        CoopPlugin.Log.LogInfo($"[steam] +connect_lobby {id}");
                        JoinLobby(id);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[steam] connect_lobby parse failed: " + ex.Message);
            }
        }

        public static void JoinLobby(ulong lobbyId)
        {
            if (!EnsureSteam()) return;
            CoopPlugin.Log.LogInfo($"[steam] joining lobby {lobbyId}…");
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
        }

        public static void OpenInviteDialog()
        {
            if (!EnsureSteam()) return;
            if (HasLobby) SteamFriends.ActivateGameOverlayInviteDialog(Current);
            else SteamFriends.ActivateGameOverlay("friends");
        }

        private static void OnLobbyCreated(LobbyCreated_t r)
        {
            if (r.m_eResult != EResult.k_EResultOK)
            {
                CoopPlugin.Log.LogError($"[steam] lobby creation failed: {r.m_eResult}");
                return;
            }
            Current = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Current, "mod", CoopPlugin.PluginVersion);
            SteamMatchmaking.SetLobbyData(Current, "gameBuild", ApplicationVersion());
            SteamMatchmaking.SetLobbyJoinable(Current, true);
            SteamFriends.SetRichPresence("connect", ConnectString());
            CoopPlugin.Log.LogInfo($"[steam] lobby created: {Current.m_SteamID}");
            // Session wiring happens in LobbyEnter_t (fired right after creation);
            // the invite overlay only opens from the lobby's INVITE OTHERS button.
        }

        private static void OnLobbyEnter(LobbyEnter_t r)
        {
            var lobby = new CSteamID(r.m_ulSteamIDLobby);
            Current = lobby;
            SteamMatchmaking.SetLobbyJoinable(lobby, true);
            SteamFriends.SetRichPresence("connect", ConnectString());
            bool isHost = SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID();
            CoopPlugin.Log.LogInfo($"[steam] entered lobby {lobby.m_SteamID} (host={isHost})");
            CoopSession.OnLobbyEntered(lobby, isHost);
        }

        /// <summary>Fires when a friend accepts an invite / clicks "Join game".</summary>
        private static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t r)
        {
            CoopPlugin.Log.LogInfo($"[steam] join requested for lobby {r.m_steamIDLobby.m_SteamID}");
            SteamMatchmaking.JoinLobby(r.m_steamIDLobby);
        }

        /// <summary>Fires when a friend accepts a rich-presence game invite.</summary>
        private static void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t r)
        {
            string connect = r.m_rgchConnect ?? "";
            CoopPlugin.Log.LogInfo(
                $"[steam] rich presence join requested from {r.m_steamIDFriend.m_SteamID}: '{connect}'");
            ulong id = ParseLobbyId(connect);
            if (id != 0) SteamMatchmaking.JoinLobby(new CSteamID(id));
        }

        /// <summary>Classic P2P: accept incoming sessions or packets are silently dropped.</summary>
        private static void OnP2PSessionRequest(P2PSessionRequest_t r)
        {
            bool accepted = SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
            CoopPlugin.Log.LogInfo(
                $"[steam] P2P session request from {r.m_steamIDRemote.m_SteamID} — accepted={accepted}");
        }

        private static void OnP2PSessionConnectFail(P2PSessionConnectFail_t r)
        {
            CoopPlugin.Log.LogWarning(
                $"[steam] P2P connection failed to {r.m_steamIDRemote.m_SteamID}: " +
                $"{(EP2PSessionError)r.m_eP2PSessionError}");
        }

        private static string ApplicationVersion()
        {
            try { return UnityEngine.Application.version ?? "?"; }
            catch { return "?"; }
        }
    }
}
