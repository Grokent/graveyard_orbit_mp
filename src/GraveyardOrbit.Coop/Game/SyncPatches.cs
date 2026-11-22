using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Every player simulates enemy attacks locally (so enemies and swarms engage
    /// clients, not only the host). Guard attacks from enemies the host does not
    /// have live while host snapshots are authoritative.
    /// </summary>
    [HarmonyPatch(typeof(EnemyManager), "DoAttack")]
    internal static class EnemyDoAttackGuardPatch
    {
        private static bool Prefix(object __0)
        {
            try { return EnemySync.AllowAttack(__0); }
            catch { return true; }
        }
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

    /// <summary>Mirror host enemy shots as real projectiles (and damage) on clients.</summary>
    [HarmonyPatch(typeof(EnemyManager), "SpawnShot")]
    internal static class EnemySpawnShotFxPatch
    {
        private static void Prefix(EnemyManager __instance, Vector3 from, Vector3 dir, float speed,
            int dmg, float maxDist, float scale)
        {
            if (!CoopSession.IsHost) return;
            try
            {
                WeaponFxSync.BroadcastEnemyShot(from, dir, speed, maxDist, dmg, scale,
                    EnemySync.ShotHitRadius(__instance));
            }
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
