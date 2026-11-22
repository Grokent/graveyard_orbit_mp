using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Builds a visual-only ship from a peer's synced loadout by temporarily
    /// swapping GameState's ship and running ShipController.Init, then stripping
    /// all behaviours. Falls back to cloning the local ship if that fails.
    /// </summary>
    internal static class RemoteShipBuilder
    {
        private static FieldInfo _gsShip;

        internal static GameObject Build(MiningSortie sortie, ulong peer)
        {
            try
            {
                List<LoadoutPart> parts = CoopSession.GetLoadout(peer);
                if (parts == null || parts.Count == 0 || sortie == null || GameState.I == null) return null;

                var ship = new AccretionShip();
                int maxUid = 0;
                foreach (LoadoutPart p in parts)
                {
                    if (p == null || string.IsNullOrEmpty(p.type)) continue;
                    var placed = new AccretionShip.PlacedPart
                    {
                        uid = p.uid,
                        type = p.type,
                        level = p.level,
                        tier = p.tier,
                        cells = ParseCells(p.cells),
                        mods = p.mods != null ? new List<string>(p.mods) : new List<string>(),
                        powerups = p.powerups != null ? new List<string>(p.powerups) : new List<string>()
                    };
                    ship.placed.Add(placed);
                    maxUid = Math.Max(maxUid, p.uid);
                }
                ship.nextUid = maxUid + 1;
                if (ship.placed.Count == 0) return null;

                _gsShip ??= AccessTools.Field(typeof(GameState), "_ship");
                object previous = _gsShip.GetValue(GameState.I);

                GameObject go = null;
                try
                {
                    _gsShip.SetValue(GameState.I, ship);

                    go = new GameObject("CoopRemoteShip");
                    go.transform.SetParent(sortie.transform, worldPositionStays: false);

                    var weapons = go.AddComponent<WeaponSystem>();
                    var ctrl = go.AddComponent<ShipController>();

                    Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                    Material hull = MakeMat(shader, new Color(0.72f, 0.74f, 0.8f));
                    Material mount = MakeMat(shader, new Color(1f, 0.55f, 0.2f));
                    Camera cam = Camera.main != null ? Camera.main : UnityEngine.Object.FindAnyObjectByType<Camera>();
                    float hex = sortie.hexSize;

                    ctrl.Init(cam, weapons, hex * 0.32f * 0.75f, hex, hull, mount, sortie.Field);

                    UnityEngine.Object.DestroyImmediate(ctrl);
                    UnityEngine.Object.DestroyImmediate(weapons);
                }
                finally
                {
                    _gsShip.SetValue(GameState.I, previous);
                }

                if (go == null) return null;
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null) UnityEngine.Object.DestroyImmediate(c);
                }
                return go;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[ship] remote build failed, using clone: " + ex.Message);
                return null;
            }
        }

        private static List<Vector2Int> ParseCells(string[] cells)
        {
            var list = new List<Vector2Int>();
            if (cells == null) return list;
            foreach (string s in cells)
            {
                if (string.IsNullOrEmpty(s)) continue;
                string[] xy = s.Split(',');
                if (xy.Length == 2 &&
                    int.TryParse(xy[0], out int x) &&
                    int.TryParse(xy[1], out int y))
                {
                    list.Add(new Vector2Int(x, y));
                }
            }
            return list;
        }

        private static Material MakeMat(Shader shader, Color color)
        {
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            return mat;
        }
    }
}
