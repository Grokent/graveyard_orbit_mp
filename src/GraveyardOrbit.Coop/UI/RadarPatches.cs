using GraveyardOrbit.UI;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraveyardOrbit.Coop
{
    /// <summary>Draws remote players as extra dots on the SENSOR MAP.</summary>
    [HarmonyPatch(typeof(RadarElement), "OnGenerateOverlay")]
    internal static class RadarRemoteDotsPatch
    {
        private static MethodInfo _worldToPixel;
        private static FieldInfo _layoutDirty;
        private static bool _refsTried;

        private static void Postfix(RadarElement __instance, MeshGenerationContext ctx)
        {
            if (CoopSession.Mode == SessionMode.Off) return;

            List<Vector3> remotes = CoopSession.RemotePositions();
            if (remotes.Count == 0) return;

            EnsureRefs();
            if (_worldToPixel == null) return;
            if (_layoutDirty != null && (bool)_layoutDirty.GetValue(__instance)) return;

            try
            {
                Painter2D painter = ctx.painter2D;
                Rect size = __instance.layout;

                foreach (Vector3 world in remotes)
                {
                    var px = (Vector2)_worldToPixel.Invoke(__instance, new object[] { world });
                    if (px.x < 0f || px.y < 0f || px.x > size.width || px.y > size.height) continue;

                    painter.fillColor = new Color(0.45f, 1f, 0.68f, 0.95f);
                    painter.BeginPath();
                    painter.Arc(px, 3.2f, 0f, 360f);
                    painter.Fill();

                    painter.strokeColor = new Color(0.05f, 0.25f, 0.12f, 0.95f);
                    painter.lineWidth = 1.2f;
                    painter.BeginPath();
                    painter.Arc(px, 4.8f, 0f, 360f);
                    painter.Stroke();
                }
            }
            catch { }
        }

        private static void EnsureRefs()
        {
            if (_refsTried) return;
            _refsTried = true;
            _worldToPixel = AccessTools.Method(typeof(RadarElement), "WorldToPixel");
            _layoutDirty = AccessTools.Field(typeof(RadarElement), "_layoutDirty");
        }
    }
}
