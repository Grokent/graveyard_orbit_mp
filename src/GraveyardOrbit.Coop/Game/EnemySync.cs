using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Enemy sync. Host keeps authority over enemy HP/death via snapshots, but
    /// each player's own EnemyManager simulates attacks locally, so enemies and
    /// attack swarms engage the host and every client equally. Client damage is
    /// forwarded while host snapshots are flowing; without them the client
    /// falls back to fully local enemy damage.
    /// </summary>
    internal static class EnemySync
    {
        private const float SnapshotInterval = 1f / 15f;
        private const float StaleAfter = 0.6f;
        private const float AuthorityTimeout = 3f;

        private static float _lastSnapshot;
        private static float _lastApply;
        private static bool _gotFirst;
        private static bool _applyingKill;
        private static readonly Dictionary<long, float> _lastSeen = new Dictionary<long, float>();
        private static readonly HashSet<long> _hidden = new HashSet<long>();

        private static FieldInfo _activeField;
        private static Type _enemyType;
        private static FieldInfo _cellF, _worldF, _hpF, _maxHpF, _visF, _ringF, _barF, _barFracF, _rotorF;
        private static FieldInfo _hexF;
        private static FieldInfo _dormantF, _dormantCellF;
        private static MethodInfo _tickBars, _killEnemy;
        private static bool _refsTried;
        private static bool _refsOk;

        /// <summary>
        /// True while host snapshots are flowing: enemy HP is host-authoritative,
        /// so client damage is forwarded instead of applied locally, and enemies
        /// the host has not revealed/has killed do not attack locally.
        /// </summary>
        internal static bool HostAuthority =>
            CoopSession.Mode == SessionMode.Client && CoopSession.Sortie != null &&
            _gotFirst && Time.unscaledTime - _lastApply < AuthorityTimeout;

        internal static void Reset()
        {
            _gotFirst = false;
            _lastSnapshot = 0f;
            _lastApply = 0f;
            _applyingKill = false;
            _lastSeen.Clear();
            _hidden.Clear();
        }

        /// <summary>Client guard: skip attacks from enemies the host does not have live.</summary>
        internal static bool AllowAttack(object enemy)
        {
            try
            {
                if (!HostAuthority || enemy == null) return true;
                EnsureRefs();
                int hp = _hpF != null ? (int)_hpF.GetValue(enemy) : 1;
                if (hp <= 0) return false;
                if (_cellF == null) return true;
                var cell = (Vector2Int)_cellF.GetValue(enemy);
                return !_hidden.Contains(Key(cell.x, cell.y));
            }
            catch { return true; }
        }

        // ------------------------------------------------------------ host

        internal static void TickHost()
        {
            if (!CoopSession.IsHost || CoopSession.Transport == null) return;
            if (Time.unscaledTime - _lastSnapshot < SnapshotInterval) return;
            _lastSnapshot = Time.unscaledTime;

            EnemyManager mgr = Manager();
            if (mgr == null) return;
            IList list = Active(mgr);
            if (list == null) return;

            var q = new List<int>();
            var r = new List<int>();
            var hp = new List<int>();
            var max = new List<int>();
            var x = new List<float>();
            var z = new List<float>();

            foreach (object e in list)
            {
                if (e == null) continue;
                int health = (int)_hpF.GetValue(e);
                if (health <= 0) continue;
                var cell = (Vector2Int)_cellF.GetValue(e);
                var world = (Vector3)_worldF.GetValue(e);
                q.Add(cell.x); r.Add(cell.y);
                hp.Add(health); max.Add((int)_maxHpF.GetValue(e));
                x.Add(world.x); z.Add(world.z);
            }

            var msg = new NetMessage
            {
                kind = MessageKind.EnemySnapshot.ToString(),
                eq = q.ToArray(), er = r.ToArray(),
                ehp = hp.ToArray(), emax = max.ToArray(),
                ex = x.ToArray(), ez = z.ToArray()
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        internal static void Apply(NetMessage msg)
        {
            if (msg == null || msg.eq == null || CoopSession.IsHost) return;

            EnemyManager mgr = Manager();
            if (mgr == null) return;
            IList list = Active(mgr);
            if (list == null) return;

            var byCell = new Dictionary<long, object>();
            foreach (object e in list)
            {
                if (e == null) continue;
                var cell = (Vector2Int)_cellF.GetValue(e);
                byCell[Key(cell.x, cell.y)] = e;
            }

            float now = Time.unscaledTime;
            var present = new HashSet<long>();
            float hex = _hexF != null ? (float)_hexF.GetValue(mgr) : 1f;

            int n = msg.eq.Length;
            for (int i = 0; i < n; i++)
            {
                long key = Key(msg.eq[i], msg.er[i]);
                present.Add(key);

                if (!byCell.TryGetValue(key, out object e) || e == null) continue;

                var world = (Vector3)_worldF.GetValue(e);
                world.x = msg.ex != null && i < msg.ex.Length ? msg.ex[i] : world.x;
                world.z = msg.ez != null && i < msg.ez.Length ? msg.ez[i] : world.z;
                _worldF.SetValue(e, world);
                _hpF.SetValue(e, msg.ehp != null && i < msg.ehp.Length ? msg.ehp[i] : 1);
                if (msg.emax != null && i < msg.emax.Length) _maxHpF.SetValue(e, msg.emax[i]);

                _lastSeen[key] = now;
                Show(e, world, hex);
                _hidden.Remove(key);
            }

            if (_gotFirst)
            {
                foreach (var kv in byCell)
                {
                    if (present.Contains(kv.Key)) continue;
                    if (_lastSeen.TryGetValue(kv.Key, out float seen) && now - seen <= StaleAfter) continue;

                    if (_lastSeen.Remove(kv.Key))
                    {
                        // It was live in an earlier snapshot and the host has since
                        // dropped it: that is a kill. Remove it properly (death FX,
                        // destroy visuals, record the cell) instead of only hiding
                        // it, so the kill registers here too.
                        ApplyKillCell(kv.Key);
                    }
                    else
                    {
                        // Never seen live: dormant / not revealed on the host yet.
                        Hide(kv.Value);
                        _hidden.Add(kv.Key);
                    }
                }
            }
            _gotFirst = true;
            _lastApply = Time.unscaledTime;

            try { _tickBars?.Invoke(mgr, new object[] { 0f }); } catch { }
        }

        internal static void ApplyDamage(int q, int r, int dmg)
        {
            if (!CoopSession.IsHost || dmg <= 0) return;
            EnemyManager mgr = Manager();
            if (mgr == null) return;
            IList list = Active(mgr);
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                object e = list[i];
                if (e == null) continue;
                var cell = (Vector2Int)_cellF.GetValue(e);
                if (cell.x != q || cell.y != r) continue;

                int hp = (int)_hpF.GetValue(e) - dmg;
                _hpF.SetValue(e, hp);
                if (hp <= 0)
                {
                    try { _killEnemy?.Invoke(mgr, new object[] { i }); } catch { }
                }
                else
                {
                    try { _tickBars?.Invoke(mgr, new object[] { 0f }); } catch { }
                }
                return;
            }
        }

        // ------------------------------------------------------------ kills

        /// <summary>
        /// Prefix on EnemyManager.KillEnemy: tell peers this enemy is gone. Every
        /// kill source (weapons, explosions, anomaly effects, local sims) funnels
        /// through KillEnemy, so one hook covers them all.
        /// </summary>
        internal static void OnLocalKill(EnemyManager mgr, int idx)
        {
            if (_applyingKill) return;
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || mgr == null) return;

            EnsureRefs();
            if (!_refsOk) return;
            IList list = Active(mgr);
            if (list == null || idx < 0 || idx >= list.Count) return;

            object e = list[idx];
            if (e == null) return;
            var cell = (Vector2Int)_cellF.GetValue(e);

            var msg = new NetMessage
            {
                kind = MessageKind.EnemyKilled.ToString(),
                dq = cell.x,
                dr = cell.y
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
            CoopPlugin.Log.LogDebug($"[enemy] kill broadcast at ({cell.x},{cell.y})");
        }

        /// <summary>
        /// Remove an enemy a peer killed. Replays the native kill (explosion /
        /// chain / void FX, destroyed-cell record) but suppresses the loot spawn:
        /// the peer that landed the kill already broadcast that separately.
        /// </summary>
        internal static void ApplyKill(int q, int r)
        {
            if (CoopSession.Sortie == null) return;
            EnemyManager mgr = Manager();
            if (mgr == null) return;

            EnsureRefs();
            if (!_refsOk) return;

            // If this peer has not revealed the enemy yet, drop it from the
            // dormant list so a later tile reveal cannot bring it back.
            RemoveDormant(mgr, q, r);

            IList list = Active(mgr);
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                object e = list[i];
                if (e == null) continue;
                var cell = (Vector2Int)_cellF.GetValue(e);
                if (cell.x != q || cell.y != r) continue;

                long key = Key(q, r);
                _applyingKill = true;
                Action<Vector3, Dictionary<string, int>> loot = mgr.OnBehaviorLoot;
                mgr.OnBehaviorLoot = null;
                try
                {
                    _killEnemy?.Invoke(mgr, new object[] { i });
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[enemy] remote kill failed: " + ex.Message);
                }
                finally
                {
                    mgr.OnBehaviorLoot = loot;
                    _applyingKill = false;
                }

                _hidden.Remove(key);
                _lastSeen.Remove(key);
                CoopPlugin.Log.LogDebug($"[enemy] remote kill applied at ({q},{r})");
                return;
            }
            // Not found: already dead here, or this peer never had it. The
            // snapshot fallback in Apply() covers the latter case.
        }

        private static void ApplyKillCell(long key)
        {
            ApplyKill((int)(key >> 32), (int)(key & 0xFFFFFFFFL));
        }

        private static void RemoveDormant(EnemyManager mgr, int q, int r)
        {
            try
            {
                if (_dormantF == null || _dormantCellF == null) return;
                var list = _dormantF.GetValue(mgr) as IList;
                if (list == null || list.Count == 0) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object item = list[i];
                    if (item == null) continue;
                    if (!(_dormantCellF.GetValue(item) is Vector2Int cell)) continue;
                    if (cell.x == q && cell.y == r) list.RemoveAt(i);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ client damage

        /// <summary>Called from the DamageTarget prefix. Returns true when handled.</summary>
        internal static bool InterceptClientDamage(EnemyManager mgr, int kind, int idx, int dmg)
        {
            if (!HostAuthority || kind != 1 || dmg <= 0) return false;
            if (mgr == null) return false;
            IList list = Active(mgr);
            if (list == null || idx < 0 || idx >= list.Count) return false;

            object e = list[idx];
            if (e == null) return false;
            var cell = (Vector2Int)_cellF.GetValue(e);
            CoopSession.SendEnemyDamage(cell.x, cell.y, dmg);
            return true;
        }

        // ------------------------------------------------------------ helpers

        private static EnemyManager Manager() => UnityEngine.Object.FindAnyObjectByType<EnemyManager>();

        /// <summary>Host's projectile hit radius (30 px, where px = hexSize/32).</summary>
        internal static float ShotHitRadius(EnemyManager mgr)
        {
            EnsureRefs();
            float hex = 1f;
            try { if (_hexF != null && mgr != null) hex = (float)_hexF.GetValue(mgr); }
            catch { }
            return hex * 30f / 32f;
        }

        private static IList Active(EnemyManager mgr)
        {
            EnsureRefs();
            if (!_refsOk) return null;
            return _activeField?.GetValue(mgr) as IList;
        }

        private static void EnsureRefs()
        {
            if (_refsTried) return;
            _refsTried = true;

            _activeField = AccessTools.Field(typeof(EnemyManager), "_active");
            _hexF = AccessTools.Field(typeof(EnemyManager), "_hexSize");
            if (_activeField == null)
            {
                CoopPlugin.Log.LogError("[enemy] _active not found - enemy sync disabled");
                return;
            }

            // Dormant (not yet revealed) enemies: a kill must remove the entry or
            // the peer would reveal it later as alive.
            _dormantF = AccessTools.Field(typeof(EnemyManager), "_dormant");
            try
            {
                Type dormantList = _dormantF?.FieldType;
                Type tuple = dormantList != null && dormantList.IsGenericType
                    ? dormantList.GetGenericArguments()[0]
                    : null;
                _dormantCellF = tuple != null ? AccessTools.Field(tuple, "Item1") : null;
            }
            catch { }

            _enemyType = _activeField.FieldType.GetGenericArguments()[0];
            _cellF = AccessTools.Field(_enemyType, "cell");
            _worldF = AccessTools.Field(_enemyType, "world");
            _hpF = AccessTools.Field(_enemyType, "hp");
            _maxHpF = AccessTools.Field(_enemyType, "maxHp");
            _visF = AccessTools.Field(_enemyType, "vis");
            _ringF = AccessTools.Field(_enemyType, "ring");
            _barF = AccessTools.Field(_enemyType, "bar");
            _barFracF = AccessTools.Field(_enemyType, "barFrac");
            _rotorF = AccessTools.Field(_enemyType, "rotor");
            _tickBars = AccessTools.Method(typeof(EnemyManager), "TickHealthBars");
            _killEnemy = AccessTools.Method(typeof(EnemyManager), "KillEnemy");

            _refsOk = _cellF != null && _worldF != null && _hpF != null && _maxHpF != null &&
                      _visF != null && _tickBars != null && _killEnemy != null;
            if (!_refsOk)
            {
                CoopPlugin.Log.LogError(
                    $"[enemy] reflection incomplete - sync disabled (cell={_cellF != null}, world={_worldF != null}, " +
                    $"hp={_hpF != null}, max={_maxHpF != null}, vis={_visF != null}, bars={_tickBars != null}, kill={_killEnemy != null})");
            }
            else
            {
                CoopPlugin.Log.LogInfo("[enemy] sync hooks ready");
            }
        }

        private static void Show(object e, Vector3 world, float hex)
        {
            var vis = _visF?.GetValue(e) as GameObject;
            if (vis != null)
            {
                vis.SetActive(true);
                vis.transform.position = world + Vector3.up * (hex * 0.3f);
            }
            var ring = _ringF?.GetValue(e) as GameObject;
            if (ring != null)
            {
                ring.SetActive(true);
                ring.transform.position = world;
            }
            var bar = _barF?.GetValue(e) as GameObject;
            if (bar != null) bar.SetActive(true);

            var rotor = _rotorF?.GetValue(e);
            if (rotor is Component rotorComponent && rotorComponent != null)
            {
                rotorComponent.gameObject.SetActive(true);
            }
        }

        private static void Hide(object e)
        {
            // Park the enemy far outside every attack radius so a client-side
            // EnemyManager cannot fire from an enemy the host has already lost.
            try
            {
                if (_worldF != null)
                {
                    var parked = (Vector3)_worldF.GetValue(e);
                    parked.x = 1e6f;
                    parked.z = 1e6f;
                    _worldF.SetValue(e, parked);
                }
            }
            catch { }

            var vis = _visF?.GetValue(e) as GameObject;
            if (vis != null) vis.SetActive(false);
            var ring = _ringF?.GetValue(e) as GameObject;
            if (ring != null) ring.SetActive(false);
            var bar = _barF?.GetValue(e) as GameObject;
            if (bar != null) bar.SetActive(false);

            // Rotating lightning drones keep firing from their own component;
            // disabling the GameObject stops damage/beams after death.
            var rotor = _rotorF?.GetValue(e);
            if (rotor is Component rotorComponent && rotorComponent != null)
            {
                rotorComponent.gameObject.SetActive(false);
            }
        }

        private static long Key(int q, int r) => ((long)q << 32) ^ (uint)r;
    }
}
