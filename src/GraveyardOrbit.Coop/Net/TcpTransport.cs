using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Direct TCP transport for dev testing two instances on one machine.
    /// Host id = 0; clients get ids 1..n. Reliable-only (Delivery is ignored).
    /// </summary>
    public sealed class TcpTransport : NetTransport
    {
        private sealed class Conn
        {
            public ulong Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
        }

        private readonly bool _isHost;
        private readonly ulong _localId;
        private readonly int _port;
        private readonly ConcurrentQueue<(PeerId from, byte[] data)> _inbox = new();
        private readonly List<PeerId> _pendingJoin = new();
        private readonly List<PeerId> _pendingLeave = new();
        private readonly Dictionary<ulong, Conn> _conns = new();
        private readonly HashSet<ulong> _announced = new();
        private TcpListener _listener;
        private TcpClient _client;
        private CancellationTokenSource _cts;
        private int _nextIdInt = 1;        private bool _disposed;

        public override bool IsHost => _isHost;
        public override PeerId LocalId => new PeerId(_localId);
        public override string Name => "tcp";

        public override IReadOnlyCollection<PeerId> Peers
        {
            get
            {
                lock (_conns)
                {
                    var list = new List<PeerId>(_conns.Count);
                    foreach (Conn c in _conns.Values) list.Add(new PeerId(c.Id));
                    return list;
                }
            }
        }

        public static TcpTransport Host(int port)
        {
            var t = new TcpTransport(isHost: true, localId: 0, port: port);
            t.StartHost();
            return t;
        }

        public static TcpTransport Connect(string host, int port)
        {
            var t = new TcpTransport(isHost: false, localId: 1, port: port);
            t.StartClient(host);
            return t;
        }

        private TcpTransport(bool isHost, ulong localId, int port)
        {
            _isHost = isHost;
            _localId = localId;
            _port = port;
        }

        private void StartHost()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            CoopPlugin.Log.LogInfo($"[tcp] listening on port {_port}");
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    var conn = new Conn
                    {
                        Id = (ulong)Interlocked.Increment(ref _nextIdInt),
                        Client = client,
                        Stream = client.GetStream()
                    };
                    lock (_conns) _conns[conn.Id] = conn;
                    lock (_pendingJoin) _pendingJoin.Add(new PeerId(conn.Id));
                    CoopPlugin.Log.LogInfo($"[tcp] peer {conn.Id} connected");
                    _ = Task.Run(() => ReadLoop(conn));
                }
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) CoopPlugin.Log.LogWarning("[tcp] accept failed: " + ex.Message);
            }
        }

        private void StartClient(string host)
        {
            _cts = new CancellationTokenSource();
            _client = new TcpClient { NoDelay = true };
            _client.Connect(host, _port);
            var conn = new Conn { Id = 0, Client = _client, Stream = _client.GetStream() };
            lock (_conns) _conns[conn.Id] = conn;
            lock (_pendingJoin) _pendingJoin.Add(new PeerId(0));
            CoopPlugin.Log.LogInfo($"[tcp] connected to {host}:{_port}");
            _ = Task.Run(() => ReadLoop(conn));
        }

        private async Task ReadLoop(Conn conn)
        {
            byte[] header = new byte[4];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    bool ok = await ReadExact(conn.Stream, header, 4).ConfigureAwait(false);
                    if (!ok) break;
                    int len = BitConverter.ToInt32(header, 0);
                    if (len <= 0 || len > 1 << 20) break;
                    byte[] payload = new byte[len];
                    if (!await ReadExact(conn.Stream, payload, len).ConfigureAwait(false)) break;
                    _inbox.Enqueue((new PeerId(conn.Id), payload));
                }
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) CoopPlugin.Log.LogInfo($"[tcp] peer {conn.Id} read ended: {ex.Message}");
            }
            finally
            {
                DropConn(conn);
            }
        }

        private static async Task<bool> ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = await s.ReadAsync(buf, off, count - off).ConfigureAwait(false);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private void DropConn(Conn conn)
        {
            lock (_conns)
            {
                if (!_conns.Remove(conn.Id)) return;
            }
            try { conn.Client.Close(); } catch { }
            lock (_pendingLeave) _pendingLeave.Add(new PeerId(conn.Id));
        }

        public override void Send(PeerId to, byte[] data, Delivery delivery)
        {
            Conn conn;
            lock (_conns)
            {
                if (!_conns.TryGetValue(to.Value, out conn)) return;
            }
            try
            {
                byte[] header = BitConverter.GetBytes(data.Length);
                lock (conn.WriteLock)
                {
                    conn.Stream.Write(header, 0, 4);
                    conn.Stream.Write(data, 0, data.Length);
                    conn.Stream.Flush();
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning($"[tcp] send to {to} failed: {ex.Message}");
                DropConn(conn);
            }
        }

        public override void Poll()
        {
            while (_inbox.TryDequeue(out var item))
            {
                RaisePayload(item.from, item.data);
            }

            List<PeerId> joins;
            lock (_pendingJoin)
            {
                joins = new List<PeerId>(_pendingJoin);
                _pendingJoin.Clear();
            }
            foreach (PeerId p in joins)
            {
                if (_announced.Add(p.Value)) RaiseJoined(p);
            }

            List<PeerId> leaves;
            lock (_pendingLeave)
            {
                leaves = new List<PeerId>(_pendingLeave);
                _pendingLeave.Clear();
            }
            foreach (PeerId p in leaves)
            {
                if (_announced.Remove(p.Value)) RaiseLeft(p);
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            lock (_conns)
            {
                foreach (Conn c in _conns.Values) { try { c.Client.Close(); } catch { } }
                _conns.Clear();
            }
            try { _client?.Close(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }
    }
}
