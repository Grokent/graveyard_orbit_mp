using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    public enum SessionMode
    {
        Off,
        Host,
        Client
    }

    /// <summary>
    /// Session/roster state plus per-frame pump. Gameplay hooks only read
    /// SpawnSlotIndex / SpawnPlayerCount and the sortie reference from here.
    /// </summary>
    public static class CoopSession
    {
        public static SessionMode Mode { get; private set; } = SessionMode.Off;
        public static NetTransport Transport { get; private set; }
        public static readonly List<PlayerEntry> Players = new List<PlayerEntry>();

        public static MiningSortie Sortie { get; private set; }

        public static string SelectedContractId { get; private set; }

        public static ulong ActiveLobbyId { get; private set; }

        public static event Action SessionChanged;
        public static event Action RosterChanged;
        public static event Action<string> ContractChanged;

        public static PeerId LocalPeer { get; private set; }
        public static bool IsHost => Mode == SessionMode.Host;

        private static CoopConfig _cfg;
        private static float _sendT;
        private static uint _tick;
        private static float _lastHelloSent;
        private static bool _rosterReceived;
        private static bool _localDead;
        private static bool _returnSent;
        private static bool _returnApplied;
        private static bool _clearedSent;
        private static readonly ConditionalWeakTable<ShipController, object> _deathHooked =
            new ConditionalWeakTable<ShipController, object>();
        private static readonly Dictionary<ulong, string> _peerBuilds = new Dictionary<ulong, string>();

        public static string HostBuild { get; private set; }

        public static bool HasVersionMismatch
        {
            get
            {
                if (IsHost)
                {
                    foreach (string b in _peerBuilds.Values)
                    {
                        if (b != CoopPlugin.BuildTag) return true;
                    }
                    return false;
                }
                return !string.IsNullOrEmpty(HostBuild) && HostBuild != CoopPlugin.BuildTag;
            }
        }

        public static bool IsLocalDead => _localDead;

        public static Color SlotColor
        {
            get
            {
                switch (Mathf.Clamp(SpawnSlotIndex, 0, 3))
                {
                    case 0: return new Color(1f, 0.62f, 0.16f);
                    case 1: return new Color(0.32f, 0.95f, 0.8f);
                    case 2: return new Color(0.72f, 0.45f, 1f);
                    default: return new Color(0.55f, 1f, 0.45f);
                }
            }
        }

        public static bool MarkRemoteReturn()
        {
            if (_returnApplied) return false;
            _returnApplied = true;
            return true;
        }
        private static UnityEngine.InputSystem.Key _hostKey, _joinKey, _leaveKey;
        private static bool _keysParsed;

        private static readonly Dictionary<ulong, RemoteShipView> _views = new Dictionary<ulong, RemoteShipView>();
        private static readonly Dictionary<ulong, (Vector3 pos, float yaw, string name)> _pending =
            new Dictionary<ulong, (Vector3, float, string)>();
        private static readonly Dictionary<ulong, List<LoadoutPart>> _loadouts = new Dictionary<ulong, List<LoadoutPart>>();

        public static void Configure(CoopConfig cfg)
        {
            _cfg = cfg;
            _keysParsed = false;
        }

        // ---------------------------------------------------------------- spawn data

        public static int SpawnPlayerCount =>
            Mode == SessionMode.Off
                ? Mathf.Max(1, _cfg.DebugCount.Value)
                : Mathf.Max(1, Players.Count);

        public static int SpawnSlotIndex =>
            Mode == SessionMode.Off
                ? Mathf.Max(0, _cfg.DebugSlot.Value)
                : LocalSlot();

        public static int LocalSlot()
        {
            for (int i = 0; i < Players.Count; i++)
            {
                if (Players[i].id == LocalPeer.Value) return Players[i].slot;
            }
            return 0;
        }

        public static float LocalSpawnAngle =>
            SlotMath.AngleDeg(SpawnSlotIndex, SpawnPlayerCount, _cfg.SpawnSeparationDeg.Value);

        // ---------------------------------------------------------------- contract

        public static void SetContract(string id, bool broadcast)
        {
            if (string.IsNullOrEmpty(id)) return;
            SelectedContractId = id;
            ContractChanged?.Invoke(id);
            if (broadcast && IsHost) SendContract();
            CoopPlugin.Log.LogInfo($"[coop] contract = {id}");
        }

        private static void SendContract()
        {
            if (Transport == null || !IsHost) return;
            var msg = new NetMessage { kind = MessageKind.Contract.ToString(), contract = SelectedContractId };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        public static void NotifyHostStarted(string contractId, string saveJson, string scene,
            int levelSeed, int anomalySeed)
        {
            SelectedContractId = contractId;
            ContractChanged?.Invoke(contractId);
            TileSync.Reset();
            if (Transport == null || !IsHost) return;

            SendContract();

            if (!string.IsNullOrEmpty(saveJson))
            {
                string payload = Compress(saveJson);
                const int chunkSize = 24000;
                int chunks = (payload.Length + chunkSize - 1) / chunkSize;
                string saveId = Guid.NewGuid().ToString("N");

                Transport.Broadcast(Protocol.Encode(new NetMessage
                {
                    kind = MessageKind.SaveBegin.ToString(),
                    saveId = saveId,
                    chunks = chunks,
                    contract = contractId
                }), Delivery.Reliable);

                for (int i = 0; i < chunks; i++)
                {
                    int offset = i * chunkSize;
                    int length = Math.Min(chunkSize, payload.Length - offset);
                    Transport.Broadcast(Protocol.Encode(new NetMessage
                    {
                        kind = MessageKind.SaveChunk.ToString(),
                        saveId = saveId,
                        chunk = i,
                        chunks = chunks,
                        data = payload.Substring(offset, length)
                    }), Delivery.Reliable);
                }

                CoopPlugin.Log.LogInfo(
                    $"[coop] sending host save: {saveJson.Length:N0} chars → {chunks} chunk(s)");
            }

            Transport.Broadcast(Protocol.Encode(new NetMessage
            {
                kind = MessageKind.Phase.ToString(),
                phase = "start",
                contract = contractId,
                count = Players.Count,
                scene = scene,
                levelSeed = levelSeed,
                anomalySeed = anomalySeed
            }), Delivery.Reliable);
        }

        public static List<LoadoutPart> GetLoadout(ulong peer)
        {
            return _loadouts.TryGetValue(peer, out List<LoadoutPart> parts) ? parts : null;
        }

        /// <summary>Client → host damage request for a host-authoritative enemy.</summary>
        public static void SendEnemyDamage(int q, int r, int dmg)
        {
            if (Mode != SessionMode.Client || Transport == null) return;
            var msg = new NetMessage
            {
                kind = MessageKind.EnemyDamage.ToString(),
                dq = q, dr = r, dmg = dmg
            };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        // ---------------------------------------------------------------- deaths / tiles

        public static void NotifyLocalShipDeath()
        {
            if (Mode == SessionMode.Off || _localDead) return;
            _localDead = true;
            CoopPlugin.Log.LogInfo("[coop] local ship destroyed — notifying peers");
            if (Transport != null)
            {
                var msg = new NetMessage { kind = MessageKind.PlayerDead.ToString() };
                Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
            }
        }

        /// <summary>Host left the sortie: bring every client home normally.</summary>
        public static void NotifyHostReturn()
        {
            if (!IsHost || Transport == null || _returnSent) return;
            _returnSent = true;
            CoopPlugin.Log.LogInfo("[coop] host returning to base — peers notified");
            Transport.Broadcast(Protocol.Encode(new NetMessage
            {
                kind = MessageKind.Phase.ToString(),
                phase = "return",
                count = Players.Count
            }), Delivery.Reliable);
        }

        /// <summary>Host cleared the structure: mirror the win so clients end too.</summary>
        public static void NotifyStructureCleared()
        {
            if (!IsHost || Transport == null || _clearedSent) return;
            _clearedSent = true;
            CoopPlugin.Log.LogInfo("[coop] structure cleared — notifying peers");
            Transport.Broadcast(Protocol.Encode(new NetMessage
            {
                kind = MessageKind.Phase.ToString(),
                phase = "cleared",
                count = Players.Count
            }), Delivery.Reliable);
        }

        public static void SendTileDestroyed(int q, int r)
        {
            if (Mode != SessionMode.Client || Transport == null) return;
            var msg = new NetMessage { kind = MessageKind.TileDestroyed.ToString(), dq = q, dr = r };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        public static void BroadcastTileDestroyed(int q, int r)
        {
            if (!IsHost || Transport == null) return;
            var msg = new NetMessage { kind = MessageKind.TileDestroyed.ToString(), dq = q, dr = r };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        public static List<Vector3> RemotePositions()
        {
            var list = new List<Vector3>(_views.Count);
            foreach (RemoteShipView view in _views.Values)
            {
                if (view != null) list.Add(view.transform.position);
            }
            return list;
        }

        private static void HookShipDeath(MiningSortie sortie)
        {
            ShipController ship = sortie != null ? sortie.Ship : null;
            if (ship == null) return;
            if (_deathHooked.TryGetValue(ship, out _)) return;
            _deathHooked.Add(ship, null);
            ship.OnDied += NotifyLocalShipDeath;
        }

        // ---------------------------------------------------------------- save transfer
        private static readonly Dictionary<int, string> _saveParts = new Dictionary<int, string>();
        private static string _saveId;
        private static int _saveChunks;
        private static bool _startPending;
        private static string _startContract;
        private static string _startScene;
        private static int _startLevelSeed;
        private static int _startAnomalySeed;

        private static void OnSaveBegin(NetMessage msg)
        {
            _saveParts.Clear();
            _saveId = msg.saveId;
            _saveChunks = Math.Max(0, msg.chunks);
            CoopPlugin.Log.LogInfo($"[coop] receiving host save: {_saveChunks} chunk(s)");
        }

        private static void OnSaveChunk(NetMessage msg)
        {
            if (msg.saveId != _saveId) return;
            _saveParts[msg.chunk] = msg.data;
        }

        private static void OnPhaseStart(NetMessage msg)
        {
            _startPending = true;
            _startContract = msg.contract;
            _startScene = msg.scene;
            _startLevelSeed = msg.levelSeed;
            _startAnomalySeed = msg.anomalySeed;
            TryApplyStart();
        }

        private static void TryApplyStart()
        {
            if (!_startPending) return;

            if (_saveChunks > 0 && _saveParts.Count < _saveChunks) return;

            string saveJson = null;
            if (_saveChunks > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _saveChunks; i++)
                {
                    if (!_saveParts.TryGetValue(i, out string part)) return;
                    sb.Append(part);
                }
                try
                {
                    saveJson = Decompress(sb.ToString());
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogError("[coop] save decompress failed: " + ex);
                }
            }

            _startPending = false;
            _saveParts.Clear();
            _saveChunks = 0;
            _saveId = null;

            TileSync.Reset();
            CoopPlugin.Log.LogInfo("[coop] host started — entering the session");
            ClientJoin.Enter(_startScene, _startContract, saveJson, _startLevelSeed, _startAnomalySeed);
        }

        private static string Compress(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }
                return Convert.ToBase64String(output.ToArray());
            }
        }

        private static string Decompress(string base64)
        {
            byte[] packed = Convert.FromBase64String(base64);
            using (var input = new MemoryStream(packed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // ---------------------------------------------------------------- lifecycle

        public static void Shutdown()
        {
            try { Transport?.Dispose(); } catch { }
            Transport = null;
            Mode = SessionMode.Off;
            Players.Clear();
            _pending.Clear();
            _loadouts.Clear();
            _peerBuilds.Clear();
            HostBuild = null;
            DestroyAllViews();
            Sortie = null;
            TileSync.Reset();
            SelectedContractId = null;
            ActiveLobbyId = 0;
            _saveParts.Clear();
            _saveChunks = 0;
            _saveId = null;
            _startPending = false;
            _rosterReceived = false;
            _lastHelloSent = 0f;
            SessionChanged?.Invoke();
        }

        public static void StartHostTcp(int port)
        {
            Shutdown();
            Transport = TcpTransport.Host(port);
            Transport.PeerJoined += OnPeerJoined;
            Transport.PeerLeft += OnPeerLeft;
            Transport.PayloadReceived += OnPayload;
            Mode = SessionMode.Host;
            LocalPeer = Transport.LocalId;
            Players.Add(new PlayerEntry { id = LocalPeer.Value, slot = 0, name = LocalName() });
            CoopPlugin.Log.LogInfo($"[coop] hosting on tcp:{port} as {LocalName()}");
            SessionChanged?.Invoke();
        }

        public static void StartClientTcp(string host, int port)
        {
            Shutdown();
            Transport = TcpTransport.Connect(host, port);
            Transport.PeerJoined += OnPeerJoined;
            Transport.PeerLeft += OnPeerLeft;
            Transport.PayloadReceived += OnPayload;
            Mode = SessionMode.Client;
            LocalPeer = Transport.LocalId;
            SendHello();
            CoopPlugin.Log.LogInfo($"[coop] connecting to tcp {host}:{port} as {LocalName()}");
            SessionChanged?.Invoke();
        }

        public static void HostSteam()
        {
            if (!SteamLobby.EnsureSteam()) return;
            Shutdown();
            SteamLobby.CreateLobby();
        }

        public static void JoinSteam(ulong lobbyId)
        {
            if (!SteamLobby.EnsureSteam()) return;
            Shutdown();
            SteamLobby.JoinLobby(lobbyId);
        }

        /// <summary>Called by SteamLobby when a lobby is created or entered.</summary>
        public static void OnLobbyEntered(CSteamID lobby, bool isHost)
        {
            try
            {
                if (Transport != null && ActiveLobbyId == lobby.m_SteamID)
                {
                    CoopPlugin.Log.LogInfo($"[coop] already in lobby {lobby.m_SteamID} — ignoring duplicate enter");
                    return;
                }

                Shutdown();
                ActiveLobbyId = lobby.m_SteamID;
                _rosterReceived = false;
                _lastHelloSent = 0f;
                var t = new SteamTransport(lobby);
                t.PeerJoined += OnPeerJoined;
                t.PeerLeft += OnPeerLeft;
                t.PayloadReceived += OnPayload;
                Transport = t;
                Mode = isHost ? SessionMode.Host : SessionMode.Client;
                LocalPeer = t.LocalId;

                if (isHost)
                {
                    Players.Add(new PlayerEntry { id = LocalPeer.Value, slot = 0, name = LocalName() });
                    CoopPlugin.Log.LogInfo($"[coop] hosting steam lobby {lobby.m_SteamID}");
                    BroadcastRoster();
                }
                else
                {
                    CoopPlugin.Log.LogInfo($"[coop] joined steam lobby {lobby.m_SteamID}, saying hello");
                    SendHello();
                }
                SessionChanged?.Invoke();
                MultiplayerLobby.TryAutoOpen();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[coop] lobby enter failed: " + ex);
            }
        }

        public static void Leave()
        {
            if (Mode == SessionMode.Off) return;
            CoopPlugin.Log.LogInfo("[coop] leaving session");
            Shutdown();
        }

        // ---------------------------------------------------------------- pump

        public static void Tick(float dt)
        {
            if (_cfg == null || !_cfg.Enabled.Value) return;

            try { SteamLobby.TickAutoInit(); } catch { }
            HandleHotkeys();

            try { Transport?.Poll(); }
            catch (Exception ex) { CoopPlugin.Log.LogError("[coop] poll failed: " + ex); }

            if (Sortie == null)
            {
                Sortie = UnityEngine.Object.FindAnyObjectByType<MiningSortie>();
            }

            FlushPendingViews();

            if (Mode != SessionMode.Off)
            {
                SendShipState(dt);
            }

            if (IsHost && Sortie != null)
            {
                EnemySync.TickHost();
            }

            // Handshake resilience: keep saying hello until the host's roster lands.
            if (Mode == SessionMode.Client && Transport != null && !_rosterReceived &&
                Time.unscaledTime - _lastHelloSent > 2f)
            {
                SendHello();
            }
        }

        private static void HandleHotkeys()
        {
            if (!_keysParsed)
            {
                _keysParsed = true;
                if (!CoopConfig.TryParseKey(_cfg.HostKey.Value, out _hostKey) || _hostKey == UnityEngine.InputSystem.Key.None)
                    _hostKey = UnityEngine.InputSystem.Key.F8;
                if (!CoopConfig.TryParseKey(_cfg.JoinKey.Value, out _joinKey) || _joinKey == UnityEngine.InputSystem.Key.None)
                    _joinKey = UnityEngine.InputSystem.Key.F9;
                if (!CoopConfig.TryParseKey(_cfg.LeaveKey.Value, out _leaveKey) || _leaveKey == UnityEngine.InputSystem.Key.None)
                    _leaveKey = UnityEngine.InputSystem.Key.F10;
            }

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            var hostKey = keyboard[_hostKey];
            var joinKey = keyboard[_joinKey];
            var leaveKey = keyboard[_leaveKey];
            bool host = hostKey != null && hostKey.wasPressedThisFrame;
            bool join = joinKey != null && joinKey.wasPressedThisFrame;
            bool leave = leaveKey != null && leaveKey.wasPressedThisFrame;

            if (host)
            {
                if (_cfg.UseSteam.Value) HostSteam();
                else StartHostTcp(_cfg.TcpPort.Value);
            }
            else if (join)
            {
                if (_cfg.UseSteam.Value)
                {
                    if (ulong.TryParse(_cfg.SteamLobbyId.Value, out ulong id) && id != 0) JoinSteam(id);
                    else SteamLobby.OpenInviteDialog();
                }
                else
                {
                    StartClientTcp("127.0.0.1", _cfg.TcpPort.Value);
                }
            }
            else if (leave)
            {
                Leave();
            }
        }

        private static void SendShipState(float dt)
        {
            if (Transport == null) return;
            if (Sortie == null) return;
            if (_localDead) return;

            ShipController ship = Sortie.Ship;
            if (ship == null || !ship.gameObject.activeInHierarchy) return;

            float hz = Mathf.Clamp(_cfg.SyncHz.Value, 5, 60);
            if (Time.unscaledTime - _sendT < 1f / hz) return;
            _sendT = Time.unscaledTime;

            Vector3 p = ship.transform.position;
            var msg = new NetMessage
            {
                kind = MessageKind.ShipState.ToString(),
                x = p.x, y = p.y, z = p.z,
                yaw = ship.transform.eulerAngles.y,
                tick = ++_tick
            };
            byte[] payload = Protocol.Encode(msg);

            if (Mode == SessionMode.Client)
            {
                // send to host (transport.Broadcast on a client goes to the host)
                Transport.Broadcast(payload, Delivery.Unreliable);
            }
            else
            {
                Transport.Broadcast(payload, Delivery.Unreliable);
            }
        }

        // ---------------------------------------------------------------- transport events

        private static void OnPeerJoined(PeerId peer)
        {
            CoopPlugin.Log.LogInfo($"[coop] peer joined: {peer}");
            if (Mode == SessionMode.Client)
            {
                SendHello();
            }
            else if (IsHost)
            {
                // Proactively publish the current roster so the newcomer sees us
                // even if their Hello is still in flight.
                BroadcastRoster();
                if (!string.IsNullOrEmpty(SelectedContractId)) SendContract();
            }
        }

        private static void OnPeerLeft(PeerId peer)
        {
            CoopPlugin.Log.LogInfo($"[coop] peer left: {peer}");
            Players.RemoveAll(p => p.id == peer.Value);
            _peerBuilds.Remove(peer.Value);
            _loadouts.Remove(peer.Value);
            _pending.Remove(peer.Value);
            RemoveView(peer.Value);
            if (IsHost) BroadcastRoster();
            RosterChanged?.Invoke();
        }

        private static void OnPayload(PeerId from, byte[] data)
        {
            if (!Protocol.TryDecode(data, out NetMessage msg))
            {
                CoopPlugin.Log.LogWarning($"[coop] undecodable packet from {from} ({data.Length} bytes)");
                return;
            }

            if (_cfg.Verbose.Value)
            {
                CoopPlugin.Log.LogDebug($"[coop] <- {msg.kind} from {from}");
            }

            switch (msg.kind)
            {
                case nameof(MessageKind.Hello):
                    if (IsHost) HandleHello(from, msg);
                    break;

                case nameof(MessageKind.Roster):
                    if (!IsHost) ApplyRoster(msg);
                    break;

                case nameof(MessageKind.Loadout):
                    if (msg.loadout != null) _loadouts[from.Value] = msg.loadout;
                    if (_views.TryGetValue(from.Value, out RemoteShipView oldView))
                    {
                        oldView?.DestroyView();
                        _views.Remove(from.Value);
                    }
                    CoopPlugin.Log.LogInfo($"[coop] loadout from {from}: {msg.loadout?.Count ?? 0} parts");
                    break;

                case nameof(MessageKind.ShipState):
                    HandleShipState(from, msg);
                    if (IsHost) Relay(from, data);
                    break;

                case nameof(MessageKind.Phase):
                    CoopPlugin.Log.LogInfo($"[coop] phase from host: {msg.phase} (players={msg.count})");
                    if (!IsHost)
                    {
                        if (msg.phase == "start") OnPhaseStart(msg);
                        else if (msg.phase == "cleared") ClientJoin.HandleCleared();
                    }
                    break;

                case nameof(MessageKind.Contract):
                    if (!string.IsNullOrEmpty(msg.contract) && msg.contract != SelectedContractId)
                    {
                        SelectedContractId = msg.contract;
                        ContractChanged?.Invoke(msg.contract);
                    }
                    break;

                case nameof(MessageKind.SaveBegin):
                    if (!IsHost) OnSaveBegin(msg);
                    break;

                case nameof(MessageKind.SaveChunk):
                    if (!IsHost) OnSaveChunk(msg);
                    break;

                case nameof(MessageKind.FxShot):
                    WeaponFxSync.Play(msg);
                    if (IsHost) Relay(from, data);
                    break;

                case nameof(MessageKind.EnemySnapshot):
                    if (!IsHost) EnemySync.Apply(msg);
                    break;

                case nameof(MessageKind.EnemyDamage):
                    if (IsHost) EnemySync.ApplyDamage(msg.dq, msg.dr, msg.dmg);
                    break;

                case nameof(MessageKind.PlayerDead):
                    RemoveView(from.Value);
                    _pending.Remove(from.Value);
                    CoopPlugin.Log.LogInfo($"[coop] peer died: {from}");
                    if (IsHost) Relay(from, data, Delivery.Reliable);
                    break;

                case nameof(MessageKind.TileDestroyed):
                    try { TileSync.Apply(msg.dq, msg.dr); }
                    catch (Exception ex) { CoopPlugin.Log.LogDebug("[tile] apply failed: " + ex.Message); }
                    if (IsHost) Relay(from, data, Delivery.Reliable);
                    break;

                case nameof(MessageKind.LootSpawn):
                    LootSync.Apply(msg);
                    if (IsHost) Relay(from, data, Delivery.Reliable);
                    break;
            }
        }

        private static void HandleHello(PeerId from, NetMessage msg)
        {
            PlayerEntry existing = Players.FirstOrDefault(p => p.id == from.Value);
            if (existing == null)
            {
                existing = new PlayerEntry { id = from.Value, name = string.IsNullOrEmpty(msg.name) ? from.ToString() : msg.name };
                Players.Add(existing);
                CoopPlugin.Log.LogInfo($"[coop] hello from {existing.name} ({from}) — roster={Players.Count}");

                string peerBuild = msg.coopBuild ?? "";
                _peerBuilds[from.Value] = peerBuild;
                if (!string.IsNullOrEmpty(peerBuild) && peerBuild != CoopPlugin.BuildTag)
                {
                    CoopPlugin.Log.LogWarning(
                        $"[coop] BUILD MISMATCH: {existing.name} runs {peerBuild}, host runs {CoopPlugin.BuildTag}");
                }
            }
            for (int i = 0; i < Players.Count; i++) Players[i].slot = i;
            BroadcastRoster();
            if (!string.IsNullOrEmpty(SelectedContractId)) SendContract();
            RosterChanged?.Invoke();
        }

        private static void ApplyRoster(NetMessage msg)
        {
            if (msg.players == null) return;
            _rosterReceived = true;
            HostBuild = msg.coopBuild;
            if (!string.IsNullOrEmpty(HostBuild) && HostBuild != CoopPlugin.BuildTag)
            {
                CoopPlugin.Log.LogWarning(
                    $"[coop] BUILD MISMATCH: host runs {HostBuild}, you run {CoopPlugin.BuildTag}");
            }
            Players.Clear();
            foreach (PlayerEntry p in msg.players)
            {
                Players.Add(new PlayerEntry { id = p.id, slot = p.slot, name = p.name });
            }
            // keep roster order by slot
            Players.Sort((a, b) => a.slot.CompareTo(b.slot));
            CoopPlugin.Log.LogInfo(
                $"[coop] roster: {string.Join(", ", Players.Select(p => $"slot{p.slot}={p.name}"))} " +
                $"(local slot {LocalSlot()})");
            RosterChanged?.Invoke();
        }

        private static void HandleShipState(PeerId from, NetMessage msg)
        {
            var pos = new Vector3(msg.x, msg.y, msg.z);
            string name = Players.FirstOrDefault(p => p.id == from.Value)?.name ?? from.ToString();

            if (_views.TryGetValue(from.Value, out RemoteShipView view) && view != null)
            {
                view.SetTarget(pos, msg.yaw);
            }
            else
            {
                _pending[from.Value] = (pos, msg.yaw, name);
            }
        }

        private static void Relay(PeerId from, byte[] data)
        {
            Relay(from, data, Delivery.Unreliable);
        }

        private static void Relay(PeerId from, byte[] data, Delivery delivery)
        {
            foreach (PeerId p in Transport.Peers)
            {
                if (p != from && p != LocalPeer) Transport.Send(p, data, delivery);
            }
        }

        private static void FlushPendingViews()
        {
            if (_pending.Count == 0) return;
            if (Sortie == null || Sortie.Ship == null) return;

            foreach (ulong peer in _pending.Keys.ToArray())
            {
                if (_views.TryGetValue(peer, out RemoteShipView existing) && existing != null)
                {
                    _pending.Remove(peer);
                    continue;
                }
                var state = _pending[peer];
                RemoteShipView view = RemoteShipView.Spawn(Sortie, peer, state.name);
                if (view != null)
                {
                    view.SetTarget(state.pos, state.yaw);
                    _views[peer] = view;
                    _pending.Remove(peer);
                }
            }
        }

        private static void RemoveView(ulong peer)
        {
            if (_views.TryGetValue(peer, out RemoteShipView view))
            {
                view?.DestroyView();
                _views.Remove(peer);
            }
        }

        private static void DestroyAllViews()
        {
            foreach (RemoteShipView view in _views.Values)
            {
                view?.DestroyView();
            }
            _views.Clear();
        }

        // ---------------------------------------------------------------- roster / messaging

        private static void BroadcastRoster()
        {
            if (Transport == null || !IsHost) return;
            var msg = new NetMessage
            {
                kind = MessageKind.Roster.ToString(),
                coopBuild = CoopPlugin.BuildTag,
                count = Players.Count,
                players = Players.Select(p => new PlayerEntry { id = p.id, slot = p.slot, name = p.name }).ToList()
            };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        private static void SendHello()
        {
            if (Transport == null) return;
            _lastHelloSent = Time.unscaledTime;
            var msg = new NetMessage
            {
                kind = MessageKind.Hello.ToString(),
                version = CoopPlugin.PluginVersion,
                build = ApplicationVersion(),
                coopBuild = CoopPlugin.BuildTag,
                name = LocalName()
            };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
            CoopPlugin.Log.LogInfo(
                $"[coop] hello sent to {Transport.Peers.Count} peer(s) as {LocalName()}");
            SendLoadout();
        }

        private static void SendLoadout()
        {
            if (Transport == null) return;
            var msg = new NetMessage
            {
                kind = MessageKind.Loadout.ToString(),
                loadout = CaptureLoadout()
            };
            Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        private static List<LoadoutPart> CaptureLoadout()
        {
            var list = new List<LoadoutPart>();
            try
            {
                AccretionShip ship = GraveyardOrbit.GameState.I != null ? GraveyardOrbit.GameState.I.Ship : null;
                if (ship == null) return list;
                foreach (AccretionShip.PlacedPart p in ship.placed)
                {
                    list.Add(new LoadoutPart
                    {
                        uid = p.uid,
                        type = p.type,
                        level = p.level,
                        tier = p.tier,
                        cells = p.cells != null ? p.cells.Select(c => c.x + "," + c.y).ToArray() : null,
                        mods = p.mods?.ToArray(),
                        powerups = p.powerups?.ToArray()
                    });
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[coop] loadout capture failed: " + ex.Message);
            }
            return list;
        }

        /// <summary>Called from the BeginSortie hook.</summary>
        public static void NotifySortieStarted(MiningSortie sortie)
        {
            Sortie = sortie;
            _localDead = false;
            _returnSent = false;
            _returnApplied = false;
            _clearedSent = false;
            DestroyAllViews();
            _pending.Clear();
            EnemySync.Reset();
            SendLoadout();
            if (sortie != null)
            {
                TileSync.Hook(sortie.Field);
                HookShipDeath(sortie);
            }

            if (IsHost && Transit() != null)
            {
                var msg = new NetMessage
                {
                    kind = MessageKind.Phase.ToString(),
                    phase = "sortie",
                    count = SpawnPlayerCount
                };
                Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
            }
        }

        private static NetTransport Transit() => Transport;

        private static string LocalName()
        {
            try
            {
                if (Mode == SessionMode.Off) return "Player";
                if (_cfg.UseSteam.Value && SteamLobby.EnsureSteam())
                {
                    string n = SteamFriends.GetPersonaName();
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            return IsHost ? "Host" : "Client";
        }

        private static string ApplicationVersion()
        {
            try { return Application.version ?? "?"; }
            catch { return "?"; }
        }
    }
}
