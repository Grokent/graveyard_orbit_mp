using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>Mirrors dropped resource motes so every player sees the loot.</summary>
    internal static class LootSync
    {
        private static bool _applyingRemote;

        internal static void OnLocalSpawn(Vector3 pos, int value, string mat)
        {
            if (_applyingRemote || CoopSession.Mode == SessionMode.Off) return;
            if (CoopSession.Transport == null || CoopSession.Sortie == null) return;

            var msg = new NetMessage
            {
                kind = MessageKind.LootSpawn.ToString(),
                lx = pos.x, ly = pos.y, lz = pos.z,
                lv = Mathf.Max(1, value),
                lmat = mat
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
        }

        internal static void Apply(NetMessage msg)
        {
            try
            {
                MiningSortie sortie = CoopSession.Sortie;
                if (sortie == null || sortie.Ship == null) return;
                var resources = sortie.Ship.GetComponent<ResourceSystem>();
                if (resources == null) return;

                var pos = new Vector3(msg.lx, msg.ly, msg.lz);
                int tile = sortie.Field != null ? sortie.Field.WorldToTileIndex(pos) : -1;

                _applyingRemote = true;
                resources.Spawn(tile, pos, msg.lv, msg.lmat);
            }
            catch { }
            finally
            {
                _applyingRemote = false;
            }
        }
    }

    [HarmonyPatch(typeof(ResourceSystem), "Spawn")]
    internal static class ResourceSpawnLootPatch
    {
        private static void Prefix(Vector3 pos, int value, string mat)
        {
            try { LootSync.OnLocalSpawn(pos, value, mat); }
            catch { }
        }
    }
}
