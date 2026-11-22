using System.Runtime.CompilerServices;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Tile destruction sync. Both sides keep simulating local damage, but every
    /// destroyed tile is reported: clients to the host, host to everyone.
    /// </summary>
    internal static class TileSync
    {
        private static readonly ConditionalWeakTable<TileField, object> Hooked =
            new ConditionalWeakTable<TileField, object>();

        private static bool _applyingRemote;

        public static void Hook(TileField field)
        {
            if (field == null) return;
            if (Hooked.TryGetValue(field, out _)) return;
            Hooked.Add(field, null);
            field.OnTileDestroyed += (idx, world) => OnLocalDestroyed(field, world);
            CoopPlugin.Log.LogInfo("[tile] destruction sync hooked");
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

        public static void Apply(TileField field, int q, int r)
        {
            if (field == null) return;
            int idx = field.IndexOfAxial(q, r);
            if (idx < 0) return;

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

            // Remote kills must also count for the radar and win/quota state.
            try { GameState.I?.RecordDestroyed(q, r); } catch { }
            try
            {
                GameRoot root = UnityEngine.Object.FindAnyObjectByType<GameRoot>();
                object radar = Refs.Radar?.GetValue(root);
                if (radar != null) Refs.RadarTileDestroyed?.Invoke(radar, new object[] { q, r });
            }
            catch { }
        }
    }
}
