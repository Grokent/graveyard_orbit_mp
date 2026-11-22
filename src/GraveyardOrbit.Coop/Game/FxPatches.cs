using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>Dynamo (sweeper) stream beams and orbs are mirrored too.</summary>
    [HarmonyPatch(typeof(SweeperSystem), "ShowStream")]
    internal static class SweeperStreamFxPatch
    {
        private static void Postfix(SweeperSystem __instance, int i, Vector3 from, Vector3 to)
        {
            try { WeaponFxSync.BroadcastDynamoStream(__instance, i, from, to); }
            catch { }
        }
    }

    /// <summary>Dynamo nova/blast rings (surge/rupture mods).</summary>
    [HarmonyPatch(typeof(SweeperSystem), "PlayBlast")]
    internal static class SweeperBlastFxPatch
    {
        private static void Postfix(Vector3 at, float radius, float mul)
        {
            try
            {
                WeaponFxSync.BroadcastVfx("WeaponFX/DomeNukeBlue", at,
                    radius / 7.2f * 1.6f * Mathf.Max(0.01f, mul), 2.6f, "dynamo");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SweeperSystem), "TickEmit")]
    internal static class SweeperOrbFxPatch
    {
        private static int _before;
        private static FieldInfo _orbs;
        private static Type _orbType;
        private static FieldInfo _pos, _dir, _speed, _life, _surge, _partUid;

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
                    bool surge = _surge != null && (bool)_surge.GetValue(orb);
                    int uid = _partUid != null ? (int)_partUid.GetValue(orb) : -1;
                    WeaponFxSync.BroadcastOrb(pos, dir, speed, speed * life,
                        "WeaponFX/ProtonMissileBlue", 0.9f * (surge ? 1.9f : 1f),
                        new Color(0.35f, 0.75f, 1f), "dynamo", WeaponFxSync.ModsForUid(uid));
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
            _surge = AccessTools.Field(_orbType, "surge");
            _partUid = AccessTools.Field(_orbType, "partUid");
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

    /// <summary>
    /// Pulse (resonator) rings: they are not projectiles, so mirror the wave
    /// itself once it has its final style (widths set by pulse mods).
    /// </summary>
    [HarmonyPatch(typeof(ResonatorWave), "Step")]
    internal static class ResonatorWaveFxPatch
    {
        private static readonly HashSet<ResonatorWave> _seen = new HashSet<ResonatorWave>();

        private static void Prefix(ResonatorWave __instance)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Sortie == null) return;
            try
            {
                if (__instance == null || _seen.Contains(__instance)) return;
                if (_seen.Count > 1024) _seen.Clear();
                _seen.Add(__instance);
                WeaponFxSync.BroadcastWave(__instance);
            }
            catch { }
        }
    }

    /// <summary>
    /// Chain-lightning arcs from weapons and enemy stuns. These are queued and
    /// sent as one batched message per flush window: with chain-lightning mods
    /// a laser can emit a hundred arcs a second, and sending each as its own
    /// packet was the main cause of receive-side hitching.
    /// </summary>
    [HarmonyPatch(typeof(EnemyManager), "SpawnArc")]
    internal static class SpawnArcFxPatch
    {
        private static void Postfix(Vector3 a, Vector3 b)
        {
            if (CoopSession.Mode == SessionMode.Off) return;
            try { WeaponFxSync.QueueArc(a, b); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ProjectileSystem), nameof(ProjectileSystem.SpawnStraight))]
    internal static class SpawnStraightFxPatch
    {
        private static void Prefix(Vector3 origin, Vector3 dir, float speed, float maxDist, Color color,
            string visualFx, float visualScale, string impactFx, float impactScale)
        {
            try
            {
                string weapon = string.IsNullOrEmpty(ProjectileSystem.SpawnFamily)
                    ? "bolt"
                    : ProjectileSystem.SpawnFamily;
                string mods = WeaponFxSync.ModsForUid(ProjectileSystem.SpawnPartUid);
                WeaponFxSync.BroadcastBolt(origin, dir, speed, maxDist, color,
                    visualFx, visualScale, weapon, mods, impactFx, impactScale);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ProjectileSystem), nameof(ProjectileSystem.SpawnProbe))]
    internal static class SpawnProbeFxPatch
    {
        private static void Prefix(Vector3 origin, Vector3 dir, float speed, float maxDist, Color color,
            string visualFx, float visualScale, string impactFx, float impactScale)
        {
            try
            {
                string weapon = string.IsNullOrEmpty(ProjectileSystem.SpawnFamily)
                    ? "probe"
                    : ProjectileSystem.SpawnFamily;
                string mods = WeaponFxSync.ModsForUid(ProjectileSystem.SpawnPartUid);
                WeaponFxSync.BroadcastBolt(origin, dir, speed, maxDist, color,
                    visualFx, visualScale, weapon, mods, impactFx, impactScale);
            }
            catch { }
        }
    }
}
