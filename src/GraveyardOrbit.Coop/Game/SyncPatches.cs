using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>Host owns enemy AI; clients apply snapshots instead.</summary>
    [HarmonyPatch(typeof(EnemyManager), "Update")]
    internal static class EnemyUpdateSuppressPatch
    {
        private static bool Prefix() => !EnemySync.SuppressLocalSim;
    }

    /// <summary>Client damage to enemies is forwarded to the host.</summary>
    [HarmonyPatch(typeof(EnemyManager), "DamageTarget")]
    internal static class EnemyDamageInterceptPatch
    {
        private static bool Prefix(int kind, int idx, int dmg)
        {
            try { return !EnemySync.InterceptClientDamage(kind, idx, dmg); }
            catch { return true; }
        }
    }

    /// <summary>Mirror host enemy shots as cosmetic bolts on clients.</summary>
    [HarmonyPatch(typeof(EnemyManager), "SpawnShot")]
    internal static class EnemySpawnShotFxPatch
    {
        private static void Prefix(Vector3 from, Vector3 dir, float speed, float maxDist)
        {
            if (!CoopSession.IsHost) return;
            try { WeaponFxSync.BroadcastEnemyShot(from, dir, speed, maxDist); }
            catch { }
        }
    }

    /// <summary>Mirror the local ship's beam weapons to peers.</summary>
    [HarmonyPatch(typeof(WeaponSystem), "DrawBeam")]
    internal static class DrawBeamFxPatch
    {
        private static void Postfix(WeaponSystem __instance, int i, Vector3 to, int slot)
        {
            try { WeaponFxSync.BroadcastBeam(__instance, i, to, slot); }
            catch { }
        }
    }
}
