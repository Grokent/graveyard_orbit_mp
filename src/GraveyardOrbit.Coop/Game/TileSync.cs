using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Tile destruction sync. Both sides keep simulating local damage, but every
    /// destroyed tile is reported: clients to the host, host to everyone.
    ///
    /// Destruction is recorded in GameState even when the local player is in the
    /// base (no MiningSortie/TileField), so a later sortie rebuilds the field
    /// with those cells already gone. Cells received while the field exists are
    /// applied immediately; anything that arrives before the field is ready is
    /// replayed when the next sortie hooks its TileField.
    /// </summary>
    internal static class TileSync
    {
        private static readonly ConditionalWeakTable<TileField, object> Hooked =
            new ConditionalWeakTable<TileField, object>();

        // Cells received while there was no live TileField to strip. Applied to
        // the next sortie's field in Hook().
        private static readonly HashSet<long> Pending = new HashSet<long>();

        private static bool _applyingRemote;

        /// <summary>New contract: drop destructions buffered for the previous map.</summary>
        public static void Reset()
        {
            Pending.Clear();
        }

        public static void Hook(TileField field)
        {
            if (field == null) return;
            if (Hooked.TryGetValue(field, out _)) return;
            Hooked.Add(field, null);
            field.OnTileDestroyed += (idx, world) => OnLocalDestroyed(field, world);
            CoopPlugin.Log.LogInfo("[tile] destruction sync hooked");
            ReplayPending(field);
        }

        private static void OnLocalDestroyed(TileField field, Vector3 world)
        {
            if (_applyingRemote || CoopSession.Mode == SessionMode.Off) return;
            try
            {
                Vector2Int cell = HexLayout.WorldToAxial(world, field.HexSize);
                if (CoopSession.IsHost) CoopSession.BroadcastTileDestroyed(cell.x, cell.y);
                else CoopSession.SendTileDestroyed(cell.x, cell.y);
            }
            catch { }
        }

        public static void Apply(int q, int r)
        {
            // Record first: the client/host may be in the base with no field at
            // all. TileField.Build() consults GameState.IsDestroyed, so this is
            // what makes the cells stay gone on the next sortie.
            try { GameState.I?.RecordDestroyed(q, r); } catch { }

            TileField field = CoopSession.Sortie != null ? CoopSession.Sortie.Field : null;
            if (field == null)
            {
                Pending.Add(Key(q, r));
                return;
            }

            int idx = field.IndexOfAxial(q, r);
            if (idx < 0)
            {
                // Field is not ready for this contract/seed — replay it later.
                Pending.Add(Key(q, r));
                return;
            }

            Strip(field, idx);

            // A remotely broken tile reveals the same dormant enemies it would
            // have revealed locally (MiningSortie calls this from OnTileDestroyed).
            try
            {
                EnemyManager mgr = UnityEngine.Object.FindAnyObjectByType<EnemyManager>();
                if (mgr != null) mgr.OnTileDestroyed(field.TileWorld(idx));
            }
            catch { }

            // Remote kills must also count for the radar and win/quota state.
            try
            {
                GameRoot root = UnityEngine.Object.FindAnyObjectByType<GameRoot>();
                object radar = Refs.Radar?.GetValue(root);
                if (radar != null) Refs.RadarTileDestroyed?.Invoke(radar, new object[] { q, r });
            }
            catch { }
        }

        private static void Strip(TileField field, int idx)
        {
            _applyingRemote = true;
            try
            {
                // StripCamouflage kills the tile (visuals, bands, alive count)
                // without loot or a local destroy callback, so it cannot echo.
                field.StripCamouflage(idx);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static void ReplayPending(TileField field)
        {
            if (Pending.Count == 0) return;
            int applied = 0;
            foreach (long key in Pending)
            {
                int q = (int)(key >> 32);
                int r = (int)(key & 0xFFFFFFFF);
                int idx = field.IndexOfAxial(q, r);
                if (idx < 0) continue;
                // Already applied by the field rebuild (GameState.IsDestroyed).
                if (!field.IsAlive(idx)) continue;
                Strip(field, idx);
                try
                {
                    EnemyManager mgr = UnityEngine.Object.FindAnyObjectByType<EnemyManager>();
                    if (mgr != null) mgr.OnTileDestroyed(field.TileWorld(idx));
                }
                catch { }
                applied++;
            }
            Pending.Clear();
            CoopPlugin.Log.LogInfo($"[tile] replayed {applied} remote destruction(s) on sortie start");
        }

        private static long Key(int q, int r) => ((long)q << 32) ^ (uint)r;
    }
}
