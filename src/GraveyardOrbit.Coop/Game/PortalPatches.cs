using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Aligns the jump/warp portal with this player's exit angle and tints it
    /// with the player's slot colour.
    /// </summary>
    [HarmonyPatch(typeof(MiningSortie), "OpenJumpPortal")]
    internal static class PortalAlignPatch
    {
        private static void Postfix(MiningSortie __instance)
        {
            try
            {
                float angle = CoopSession.LocalSpawnAngle;
                if (Mathf.Abs(angle) < 0.01f) return;
                if (Refs.PortalPos == null || Refs.PlanetCenter == null) return;

                var center = (Vector3)Refs.PlanetCenter.GetValue(__instance);
                var pos = (Vector3)Refs.PortalPos.GetValue(__instance);
                pos = SlotMath.RotateAroundY(pos, center, angle);
                Refs.PortalPos.SetValue(__instance, pos);

                var go = Refs.PortalGo?.GetValue(__instance) as GameObject;
                if (go != null) go.transform.position = pos;

                Refs.AnimatePortal?.Invoke(__instance, new object[] { 0f });
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MiningSortie), "AnimatePortal")]
    internal static class PortalColourPatch
    {
        private static void Postfix(MiningSortie __instance)
        {
            try
            {
                Color c = CoopSession.SlotColor;
                var outer = Refs.PortalOuter?.GetValue(__instance) as LineRenderer;
                if (outer != null)
                {
                    outer.startColor = c;
                    outer.endColor = c;
                }

                if (Refs.PortalArcs?.GetValue(__instance) is LineRenderer[] arcs)
                {
                    Color a = new Color(c.r, c.g, c.b, 0.8f);
                    foreach (LineRenderer arc in arcs)
                    {
                        if (arc == null) continue;
                        arc.startColor = a;
                        arc.endColor = a;
                    }
                }
            }
            catch { }
        }
    }
}
