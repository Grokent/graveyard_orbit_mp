using HarmonyLib;
using System.Reflection;

namespace GraveyardOrbit.Coop
{
    /// <summary>Cached reflection handles into MiningSortie's private launch state.</summary>
    internal static class Refs
    {
        internal static readonly FieldInfo LaunchStart = AccessTools.Field(typeof(MiningSortie), "_launchStart");
        internal static readonly FieldInfo SpawnPos = AccessTools.Field(typeof(MiningSortie), "_spawnPos");
        internal static readonly FieldInfo PlanetCenter = AccessTools.Field(typeof(MiningSortie), "_planetCenter");
        internal static readonly FieldInfo CamTarget = AccessTools.Field(typeof(MiningSortie), "_camTarget");
        internal static readonly MethodInfo TryLaunch = AccessTools.Method(typeof(GameRoot), "TryLaunch");
        internal static readonly FieldInfo GameRootState = AccessTools.Field(typeof(GameRoot), "_state");

        internal static readonly FieldInfo PortalPos = AccessTools.Field(typeof(MiningSortie), "_portalPos");
        internal static readonly FieldInfo PortalGo = AccessTools.Field(typeof(MiningSortie), "_portal");
        internal static readonly FieldInfo PortalOuter = AccessTools.Field(typeof(MiningSortie), "_portalOuter");
        internal static readonly FieldInfo PortalArcs = AccessTools.Field(typeof(MiningSortie), "_portalArcs");
        internal static readonly MethodInfo AnimatePortal = AccessTools.Method(typeof(MiningSortie), "AnimatePortal");

        internal static readonly FieldInfo Radar = AccessTools.Field(typeof(GameRoot), "_radar");
        internal static readonly MethodInfo RadarTileDestroyed =
            AccessTools.Method(typeof(GraveyardOrbit.UI.RadarController), "OnTileDestroyed");

        internal static bool Valid =>
            LaunchStart != null && SpawnPos != null && PlanetCenter != null && CamTarget != null;
    }
}
