using System;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Rotates each player's launch/spawn path around the planet so ships exit
    /// the base on separate radial lines (15° apart by default) instead of all
    /// sharing vanilla's single +Z line.
    /// </summary>
    internal static class SpawnFan
    {
        internal static void Apply(MiningSortie sortie)
        {
            if (sortie == null || !Refs.Valid) return;

            ShipController ship = sortie.Ship;
            if (ship == null) return;

            int count = CoopSession.SpawnPlayerCount;
            int slot = CoopSession.SpawnSlotIndex;
            if (count <= 1)
            {
                // Solo / vanilla: leave the original launch line untouched.
                return;
            }

            float separation = Mathf.Max(0f, CoopPlugin.Cfg.SpawnSeparationDeg.Value);
            float angle = SlotMath.AngleDeg(slot, count, separation);

            var center = (Vector3)Refs.PlanetCenter.GetValue(sortie);
            var launchStart = SlotMath.RotateAroundY((Vector3)Refs.LaunchStart.GetValue(sortie), center, angle);
            var spawnPos = SlotMath.RotateAroundY((Vector3)Refs.SpawnPos.GetValue(sortie), center, angle);

            Refs.LaunchStart.SetValue(sortie, launchStart);
            Refs.SpawnPos.SetValue(sortie, spawnPos);
            Refs.CamTarget.SetValue(sortie, spawnPos);

            ship.transform.position = launchStart;
            ship.ResetOrientation(angle);

            CoopPlugin.Log.LogInfo(
                $"[spawnfan] slot {slot + 1}/{count} → exit angle {angle:0.##}° " +
                $"(launch {Fmt(launchStart)}, spawn {Fmt(spawnPos)})");
        }

        private static string Fmt(Vector3 v) => $"({v.x:0.0}, {v.z:0.0})";
    }
}
