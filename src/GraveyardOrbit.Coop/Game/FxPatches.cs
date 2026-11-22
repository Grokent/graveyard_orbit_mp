using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>Dynamo (sweeper) stream beams and orbs are mirrored too.</summary>
    [HarmonyPatch(typeof(SweeperSystem), "ShowStream")]
    internal static class SweeperStreamFxPatch
    {
        private static void Postfix(int i, Vector3 from, Vector3 to)
        {
            try { WeaponFxSync.BroadcastBeamRaw(1000 + i, from, to, CoopSession.SlotColor); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SweeperSystem), "TickEmit")]
    internal static class SweeperOrbFxPatch
    {
        private static int _before;
        private static FieldInfo _orbs;
        private static Type _orbType;
        private static FieldInfo _pos, _dir, _speed, _life;

        private static void Prefix(SweeperSystem __instance)
        {
            _before = Count(__instance);
        }

        private static void Postfix(SweeperSystem __instance)
        {
            try
            {
                IList list = Orbs(__instance);
                if (list == null) return;
                for (int i = Mathf.Max(0, _before); i < list.Count; i++)
                {
                    object orb = list[i];
                    if (orb == null) continue;
                    Vector3 pos = (Vector3)_pos.GetValue(orb);
                    Vector3 dir = (Vector3)_dir.GetValue(orb);
                    float speed = (float)_speed.GetValue(orb);
                    float life = (float)_life.GetValue(orb);
                    WeaponFxSync.BroadcastOrb(pos, dir, speed, speed * life,
                        "WeaponFX/ProtonMissileBlue", 0.9f, new Color(0.35f, 0.75f, 1f));
                }
            }
            catch { }
        }

        private static void EnsureRefs()
        {
            if (_orbs != null) return;
            _orbs = AccessTools.Field(typeof(SweeperSystem), "_orbs");
            if (_orbs == null) return;
            _orbType = _orbs.FieldType.GetGenericArguments()[0];
            _pos = AccessTools.Field(_orbType, "pos");
            _dir = AccessTools.Field(_orbType, "dir");
            _speed = AccessTools.Field(_orbType, "speed");
            _life = AccessTools.Field(_orbType, "life");
        }

        private static int Count(SweeperSystem s)
        {
            IList list = Orbs(s);
            return list?.Count ?? 0;
        }

        private static IList Orbs(SweeperSystem s)
        {
            EnsureRefs();
            return _orbs?.GetValue(s) as IList;
        }
    }

    /// <summary>Chain-lightning arcs from weapons and enemy stuns.</summary>
    [HarmonyPatch(typeof(EnemyManager), "SpawnArc")]
    internal static class SpawnArcFxPatch
    {
        private static void Postfix(Vector3 a, Vector3 b)
        {
            if (CoopSession.Mode == SessionMode.Off) return;
            try { WeaponFxSync.BroadcastBeamRaw(-1, a, b, new Color(0.62f, 0.32f, 1f)); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ProjectileSystem), nameof(ProjectileSystem.SpawnStraight))]
    internal static class SpawnStraightFxPatch
    {
        private static void Prefix(Vector3 origin, Vector3 dir, float speed, float maxDist, Color color,
            string visualFx, float visualScale)
        {
            try { WeaponFxSync.BroadcastBolt(origin, dir, speed, maxDist, color, visualFx, visualScale); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ProjectileSystem), nameof(ProjectileSystem.SpawnProbe))]
    internal static class SpawnProbeFxPatch
    {
        private static void Prefix(Vector3 origin, Vector3 dir, float speed, float maxDist, Color color,
            string visualFx, float visualScale)
        {
            try { WeaponFxSync.BroadcastBolt(origin, dir, speed, maxDist, color, visualFx, visualScale); }
            catch { }
        }
    }
}
