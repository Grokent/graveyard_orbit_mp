using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Temporary diagnostics for "host returns -> client is pulled back to base".
    /// Logs every local sortie exit reason so the next report pinpoints the cause.
    /// </summary>
    [HarmonyPatch(typeof(MiningSortie), "BeginReturnHome")]
    internal static class ReturnHomeDiagnosticsPatch
    {
        private static void Postfix(MiningSortie __instance, float speed, float accel)
        {
            try
            {
                ShipController ship = __instance.Ship;
                Vector3 p = ship != null ? ship.transform.position : Vector3.zero;
                CoopPlugin.Log.LogInfo(
                    $"[return] BeginReturnHome mode={CoopSession.Mode} dead={CoopSession.IsLocalDead} " +
                    $"speed={speed:0.##} accel={accel:0.##} pos=({p.x:0.0},{p.z:0.0})");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MiningSortie), "OnShipDied")]
    internal static class ShipDiedDiagnosticsPatch
    {        private static void Postfix(MiningSortie __instance)
        {
            try
            {
                ShipController ship = __instance.Ship;
                Vector3 p = ship != null ? ship.transform.position : Vector3.zero;
                CoopPlugin.Log.LogInfo(
                    $"[return] OnShipDied mode={CoopSession.Mode} pos=({p.x:0.0},{p.z:0.0})");
            }
            catch { }
        }
    }

    /// <summary>Marks the win for peers when the host clears the structure.</summary>
    [HarmonyPatch(typeof(MiningSortie), "StructureCleared")]
    internal static class StructureClearedWinPatch
    {
        private static void Postfix()
        {
            try
            {
                if (CoopSession.IsHost) CoopSession.NotifyStructureCleared();
            }
            catch { }
        }
    }

    /// <summary>Logs when a peer death message arrives and a remote ship is removed.</summary>
    internal static class DeathDiagnostics
    {
        internal static void LogPeerDeath(ulong peer)
        {
            CoopPlugin.Log.LogInfo($"[return] peer death received for {peer}");
        }
    }
}
