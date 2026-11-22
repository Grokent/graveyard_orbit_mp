using HarmonyLib;
using System;

namespace GraveyardOrbit.Coop
{
    /// <summary>Ship spawn/teleport-in: apply the per-player exit-angle fan.</summary>
    [HarmonyPatch(typeof(MiningSortie), nameof(MiningSortie.BeginSortie))]
    internal static class MiningSortieBeginSortiePatch
    {
        private static void Postfix(MiningSortie __instance)
        {
            try
            {
                CoopSession.NotifySortieStarted(__instance);
                SpawnFan.Apply(__instance);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[patch] BeginSortie postfix failed: " + ex);
            }
        }
    }

    /// <summary>Detect a real launch (state actually moved to TakingOff) and tell peers.</summary>
    [HarmonyPatch(typeof(GameRoot), "TryLaunch")]
    internal static class GameRootTryLaunchPatch
    {
        private static void Postfix(GameRoot __instance)
        {
            try
            {
                if (!CoopSession.IsHost || CoopSession.Transport == null) return;

                // Only broadcast when the launch actually started.
                object state = Refs.GameRootState?.GetValue(__instance);
                if (state == null || state.ToString() != "TakingOff") return;

                var msg = new NetMessage
                {
                    kind = MessageKind.Phase.ToString(),
                    phase = "launch",
                    count = CoopSession.SpawnPlayerCount
                };
                CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Reliable);
                CoopPlugin.Log.LogInfo("[coop] host launched — peers notified (auto-launch arrives in phase 3)");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[patch] TryLaunch postfix failed: " + ex);
            }
        }
    }
}
