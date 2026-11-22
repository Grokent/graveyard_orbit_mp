using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Runtime-only combined contract: merges "Wrecked Destroyer" and "Space Rock"
    /// into one hex field, separated by at least 2 empty cells, centres it on the
    /// planet's x axis, and adds tier-1/2 (black/gold) asteroid clumps between the
    /// planet and the map. Original assets are never modified.
    /// </summary>
    internal static class CombinedContract
    {
        public const string Id = "wrecked_space_rock";
        public const string Name = "WRECKED SPACE ROCK";
        public const string Description =
            "Wrecked Destroyer and Space Rock, fused into one graveyard field.";

        private const int AsteroidCells = 200;
        private static bool _injected;

        private sealed class HexMap
        {
            public int count;
            public int[] q, r, alt, layer;
            public int[] roomQ, roomR, roomId;
            public int[] greenQ, greenR;
            public int qMin, qMax, rMin, rMax;

            public static HexMap Parse(StructureDef def)
            {
                var map = new HexMap();
                JObject o = JObject.Parse(def.cells.text);
                map.count = (int?)o["count"] ?? 0;
                map.q = o["q"]?.ToObject<int[]>() ?? Array.Empty<int>();
                map.r = o["r"]?.ToObject<int[]>() ?? Array.Empty<int>();
                map.alt = o["alt"]?.ToObject<int[]>() ?? new int[map.q.Length];
                map.layer = o["layer"]?.ToObject<int[]>() ?? new int[map.q.Length];
                map.count = Math.Min(map.count, map.q.Length);

                if (o["rooms"] is JObject rooms)
                {
                    map.roomQ = rooms["q"]?.ToObject<int[]>();
                    map.roomR = rooms["r"]?.ToObject<int[]>();
                    map.roomId = rooms["id"]?.ToObject<int[]>();
                }
                if (o["roomGreen"] is JObject green)
                {
                    map.greenQ = green["q"]?.ToObject<int[]>();
                    map.greenR = green["r"]?.ToObject<int[]>();
                }

                map.ComputeBounds();
                return map;
            }

            public void ComputeBounds()
            {
                int qMin = int.MaxValue, qMax = int.MinValue, rMin = int.MaxValue, rMax = int.MinValue;
                for (int i = 0; i < count; i++)
                {
                    qMin = Math.Min(qMin, q[i]); qMax = Math.Max(qMax, q[i]);
                    rMin = Math.Min(rMin, r[i]); rMax = Math.Max(rMax, r[i]);
                }
                this.qMin = qMin; this.qMax = qMax; this.rMin = rMin; this.rMax = rMax;
            }
        }

        // ------------------------------------------------------------------ inject

        public static void EnsureInjected()
        {
            if (_injected) return;
            try
            {
                StructureCatalog catalog = StructureCatalog.I;
                if (catalog == null) return;

                if (AlreadyPresent(catalog))
                {
                    _injected = true;
                    return;
                }

                StructureDef a = FindSource(catalog, "wreckeddestroyer", "destroyer");
                StructureDef b = FindSource(catalog, "spacerock", "veinrock");
                if (a == null || b == null || a.cells == null || b.cells == null)
                {
                    CoopPlugin.Log.LogWarning(
                        $"[contract] could not find sources (wrecked destroyer={NameOf(a)}, space rock={NameOf(b)})");
                    LogCatalog(catalog);
                    return;
                }

                StructureDef merged = Build(a, b, ReadPrebuiltMap());
                if (merged == null) return;

                Append(catalog, merged);
                Cache(merged);
                _injected = true;

                CoopPlugin.Log.LogInfo(
                    $"[contract] injected '{merged.id}' ({merged.cellCount:N0} cells) from " +
                    $"'{a.id}' + '{b.id}'.");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[contract] injection failed: " + ex);
            }
        }

        private static StructureDef Build(StructureDef aDef, StructureDef bDef, string prebuiltJson)
        {
            HexMap a = HexMap.Parse(aDef);
            HexMap b = HexMap.Parse(bDef);

            var aSet = new HashSet<long>(a.count);
            for (int i = 0; i < a.count; i++) aSet.Add(Key(a.q[i], a.r[i]));

            var bCells = new List<(int q, int r)>(b.count);
            for (int i = 0; i < b.count; i++) bCells.Add((b.q[i], b.r[i]));

            // Tightest placement keeping >= 2 empty cells between the two maps.
            (int dq, int dr) best = FindBestShift(a, b, aSet, bCells);

            var q = new List<int>(a.count + b.count);
            var r = new List<int>(a.count + b.count);
            var alt = new List<int>(a.count + b.count);
            var layer = new List<int>(a.count + b.count);

            for (int i = 0; i < a.count; i++)
            {
                q.Add(a.q[i]); r.Add(a.r[i]); alt.Add(a.alt[i]); layer.Add(a.layer[i]);
            }
            for (int i = 0; i < b.count; i++)
            {
                q.Add(b.q[i] + best.dq); r.Add(b.r[i] + best.dr);
                alt.Add(b.alt[i]); layer.Add(b.layer[i]);
            }

            // Centre the field on the planet's x axis (spawn line x = 0).
            // world x = 1.71875 * (q + r/2), so shifting q shifts x uniformly.
            double meanX = 0;
            for (int i = 0; i < q.Count; i++) meanX += q[i] + r[i] * 0.5;
            meanX /= Math.Max(1, q.Count);
            int centreShift = (int)Math.Round(-meanX, MidpointRounding.AwayFromZero);
            if (centreShift != 0)
            {
                for (int i = 0; i < q.Count; i++) q[i] += centreShift;

                // keep the A-cell lookup in the same frame for the gap check/log
                aSet = new HashSet<long>(a.count);
                for (int i = 0; i < a.count; i++) aSet.Add(Key(a.q[i] + centreShift, a.r[i]));
            }

            // Planet-side asteroid clumps: tier 1 (black) / tier 2 (gold).
            int rMaxBefore = r.Max();
            AddAsteroids(q, r, alt, layer, rMaxBefore);

            int n = q.Count;
            var boundsMap = new HexMap
            {
                q = q.ToArray(), r = r.ToArray(), alt = alt.ToArray(),
                layer = layer.ToArray(), count = n
            };
            boundsMap.ComputeBounds();
            int qMin = boundsMap.qMin, qMax = boundsMap.qMax;
            int rMin = boundsMap.rMin, rMax = boundsMap.rMax;

            var o = new JObject
            {
                ["source"] = Id,
                ["count"] = n,
                ["q"] = new JArray(q),
                ["r"] = new JArray(r),
                ["alt"] = new JArray(alt),
                ["layer"] = new JArray(layer),
                ["bounds"] = new JObject
                {
                    ["q"] = new JArray(qMin, qMax),
                    ["r"] = new JArray(rMin, rMax)
                }
            };
            // rooms (B renumbered so ids stay unique)
            var roomQ = new List<int>(); var roomR = new List<int>(); var roomId = new List<int>();
            if (a.roomQ != null)
            {
                roomQ.AddRange(a.roomQ); roomR.AddRange(a.roomR); roomId.AddRange(a.roomId);
            }
            if (b.roomQ != null)
            {
                int offset = (roomId.Count > 0 ? roomId.Max() : -1) + 1;
                for (int i = 0; i < b.roomQ.Length; i++)
                {
                    roomQ.Add(b.roomQ[i] + best.dq + centreShift);
                    roomR.Add(b.roomR[i] + best.dr);
                    roomId.Add(b.roomId[i] + offset);
                }
            }
            if (a.roomQ != null)
            {
                for (int i = 0; i < a.roomQ.Length; i++) roomQ[i] += centreShift;
            }
            if (roomQ.Count > 0)
            {
                o["rooms"] = new JObject
                {
                    ["q"] = new JArray(roomQ),
                    ["r"] = new JArray(roomR),
                    ["id"] = new JArray(roomId)
                };
            }

            // green shards
            var greenQ = new List<int>(); var greenR = new List<int>();
            if (a.greenQ != null) { greenQ.AddRange(a.greenQ); greenR.AddRange(a.greenR); }
            if (b.greenQ != null)
            {
                for (int i = 0; i < b.greenQ.Length; i++)
                {
                    greenQ.Add(b.greenQ[i] + best.dq + centreShift);
                    greenR.Add(b.greenR[i] + best.dr);
                }
            }
            if (a.greenQ != null)
            {
                for (int i = 0; i < a.greenQ.Length; i++) greenQ[i] += centreShift;
            }
            if (greenQ.Count > 0)
            {
                o["roomGreen"] = new JObject { ["q"] = new JArray(greenQ), ["r"] = new JArray(greenR) };
            }

            // artifact cells, shifted with B
            var artifacts = new List<Vector2Int>();
            if (aDef.artifactCells != null)
            {
                foreach (Vector2Int cell in aDef.artifactCells)
                {
                    artifacts.Add(new Vector2Int(cell.x + centreShift, cell.y));
                }
            }
            if (bDef.artifactCells != null)
            {
                foreach (Vector2Int cell in bDef.artifactCells)
                {
                    artifacts.Add(new Vector2Int(cell.x + best.dq + centreShift, cell.y + best.dr));
                }
            }

            var def = ScriptableObject.CreateInstance<StructureDef>();
            def.id = Id;
            def.displayName = Name;
            def.description = Description;

            string json = o.ToString(Newtonsoft.Json.Formatting.None);
            if (!string.IsNullOrEmpty(prebuiltJson))
            {
                try
                {
                    MegastructureData pre = MegastructureData.FromJson(prebuiltJson);
                    json = prebuiltJson;
                    def.cellCount = pre.count;
                    CoopPlugin.Log.LogInfo(
                        $"[contract] using prebuilt map '{MapPath()}' ({pre.count:N0} cells)");
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[contract] prebuilt map invalid, regenerating: " + ex.Message);
                    def.cellCount = n;
                }
            }
            else
            {
                def.cellCount = n;
                WriteMapFile(json);
            }

            def.cells = new TextAsset(json);
            def.sealedLayers = aDef.sealedLayers;
            def.startingMoney = aDef.startingMoney + bDef.startingMoney;
            def.bands = MergeBands(aDef.bands, bDef.bands);
            def.artifactCells = artifacts.ToArray();
            def.glyphCount = aDef.glyphCount + bDef.glyphCount;
            def.greenCount = (aDef.greenCount >= 0 && bDef.greenCount >= 0)
                ? aDef.greenCount + bDef.greenCount
                : -1;

            int minDistance = MinGap(q, r, a, aSet, best.dq, best.dr);
            CoopPlugin.Log.LogInfo(
                $"[contract] merged bounds q[{qMin},{qMax}] r[{rMin},{rMax}], " +
                $"offset ({best.dq},{best.dr}), centre x{centreShift:+0;-0;0}, " +
                $"min gap {minDistance} cell(s), asteroids {AsteroidCells}, {n:N0} cells.");

            try
            {
                MegastructureData check = MegastructureData.FromJson(def.cells.text);
                CoopPlugin.Log.LogInfo(
                    $"[contract] self-check OK: {check.count:N0} cells, " +
                    $"q[{check.qMin},{check.qMax}] r[{check.rMin},{check.rMax}], " +
                    $"rooms={check.roomQ.Length}, green={check.greenQ.Length}");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[contract] self-check FAILED: " + ex);
            }

            return def;
        }

        // ------------------------------------------------------------------ placement

        private static readonly (int dq, int dr)[] Disk2 = BuildDisk2();

        private static (int dq, int dr)[] BuildDisk2()
        {
            var list = new List<(int, int)>();
            for (int dq = -2; dq <= 2; dq++)
            {
                for (int dr = -2; dr <= 2; dr++)
                {
                    if (HexDistance(dq, dr) <= 2) list.Add((dq, dr));
                }
            }
            return list.ToArray();
        }

        private static (int dq, int dr) FindBestShift(
            HexMap a, HexMap b, HashSet<long> aSet, List<(int q, int r)> bCells)
        {
            (int dq, int dr) best = (0, 0);
            int bestGap = int.MaxValue;
            long bestExtent = long.MaxValue;
            long bestArea = long.MaxValue;
            double bestCentroid = double.MaxValue;

            // Keep the map's r-range tight: rMax drives the planet's size and
            // distance (planet radius = 1.4 * rMax), so only small vertical
            // offsets are allowed; the two shapes are placed side by side.
            for (int dr = -30; dr <= 10; dr++)
            {
                // right of A: start outside, then hug inward until contact
                int dq = a.qMax + 2 - b.qMin;
                while (!Clear(a, aSet, bCells, dq, dr)) dq++;
                while (Clear(a, aSet, bCells, dq - 1, dr)) dq--;
                Consider(a, b, dq, dr);

                // left of A
                dq = a.qMin - 2 - b.qMax;
                while (!Clear(a, aSet, bCells, dq, dr)) dq--;
                while (Clear(a, aSet, bCells, dq + 1, dr)) dq++;
                Consider(a, b, dq, dr);
            }

            void Consider(HexMap a, HexMap b, int dq, int dr)
            {
                long qMin = Math.Min(a.qMin, b.qMin + dq);
                long qMax = Math.Max(a.qMax, b.qMax + dq);
                long rMin = Math.Min(a.rMin, b.rMin + dr);
                long rMax = Math.Max(a.rMax, b.rMax + dr);
                long extent = Math.Max(Math.Max(Math.Abs(qMin), Math.Abs(qMax)),
                                       Math.Max(Math.Abs(rMin), Math.Abs(rMax)));
                long area = (qMax - qMin + 1) * (rMax - rMin + 1);
                double centroid = CentroidDistanceSq(a, b, dq, dr);
                int gap = GapAt(a, aSet, bCells, dq, dr);

                // Closest legal contact first, then the most compact placement.
                if (gap < bestGap ||
                    (gap == bestGap && centroid < bestCentroid) ||
                    (gap == bestGap && centroid == bestCentroid && extent < bestExtent) ||
                    (gap == bestGap && centroid == bestCentroid && extent == bestExtent && area < bestArea))
                {
                    bestGap = gap;
                    bestExtent = extent;
                    bestArea = area;
                    bestCentroid = centroid;
                    best = (dq, dr);
                }
            }

            return best;
        }

        private static bool Clear(HexMap a, HashSet<long> aSet, List<(int q, int r)> bCells, int dq, int dr)
        {
            int guardQMin = a.qMin - 2, guardQMax = a.qMax + 2;
            int guardRMin = a.rMin - 2, guardRMax = a.rMax + 2;

            foreach (var cell in bCells)
            {
                int q = cell.q + dq;
                int r = cell.r + dr;
                // Cheap reject: can't be within distance 2 of A outside its guarded box.
                if (q < guardQMin || q > guardQMax || r < guardRMin || r > guardRMax) continue;

                for (int i = 0; i < Disk2.Length; i++)
                {
                    if (aSet.Contains(Key(q + Disk2[i].dq, r + Disk2[i].dr))) return false;
                }
            }
            return true;
        }

        private static double CentroidDistanceSq(HexMap a, HexMap b, int dq, int dr)
        {
            double ax = 0, az = 0, bx = 0, bz = 0;
            for (int i = 0; i < a.count; i++) { ax += a.q[i] + a.r[i] * 0.5; az += a.r[i]; }
            for (int i = 0; i < b.count; i++) { bx += b.q[i] + dq + (b.r[i] + dr) * 0.5; bz += b.r[i] + dr; }
            ax /= a.count; az /= a.count; bx /= b.count; bz /= b.count;
            return (ax - bx) * (ax - bx) + (az - bz) * (az - bz);
        }

        private static int GapAt(HexMap a, HashSet<long> aSet, List<(int q, int r)> bCells, int dq, int dr)
        {
            for (int radius = 3; radius <= 6; radius++)
            {
                int qMin = a.qMin - radius, qMax = a.qMax + radius;
                int rMin = a.rMin - radius, rMax = a.rMax + radius;
                foreach (var off in Disk(radius))
                {
                    foreach (var cell in bCells)
                    {
                        int q = cell.q + dq + off.dq;
                        int r = cell.r + dr + off.dr;
                        if (q < qMin || q > qMax || r < rMin || r > rMax) continue;
                        if (aSet.Contains(Key(q, r))) return radius;
                    }
                }
            }
            return 7;
        }

        private static int MinGap(List<int> q, List<int> r, HexMap a, HashSet<long> aSet, int dq, int dr)
        {
            for (int radius = 0; radius <= 4; radius++)
            {
                foreach (var off in Disk(radius))
                {
                    for (int i = a.count; i < q.Count; i++)
                    {
                        if (aSet.Contains(Key(q[i] + off.dq, r[i] + off.dr))) return radius;
                    }
                }
            }
            return 5;
        }

        private static IEnumerable<(int dq, int dr)> Disk(int radius)
        {
            for (int dq = -radius; dq <= radius; dq++)
            {
                for (int dr = -radius; dr <= radius; dr++)
                {
                    if (HexDistance(dq, dr) == radius) yield return (dq, dr);
                }
            }
        }

        private static int HexDistance(int dq, int dr) =>
            (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;

        private static long Key(int q, int r) => ((long)q << 32) ^ (uint)r;

        // ------------------------------------------------------------------ asteroids

        /// <summary>~200 tier-1/2 cells as small clumps between the map's north edge and the planet.</summary>
        private static void AddAsteroids(
            List<int> q, List<int> r, List<int> alt, List<int> layer, int mapRMax)
        {
            var occupied = new HashSet<long>(q.Count);
            for (int i = 0; i < q.Count; i++) occupied.Add(Key(q[i], r[i]));

            var rng = new System.Random(unchecked(0x0C0FFEE ^ q.Count));
            // (world-x in hex units, r centre, cell count) — 5 clumps, 200 cells total.
            var specs = new (double x, int rc, int size)[]
            {
                (-27, mapRMax + 7, 34),
                (-13, mapRMax + 12, 48),
                (1, mapRMax + 5, 42),
                (15, mapRMax + 11, 44),
                (28, mapRMax + 8, 32)
            };

            int rMin = mapRMax + 3;
            int rMax = mapRMax + 15;

            foreach (var spec in specs)
            {
                var clump = new HashSet<(int, int)>();
                int qc = (int)Math.Round(spec.x / 1.71875 - spec.rc * 0.5);
                clump.Add((qc, spec.rc));

                int guard = 0;
                while (clump.Count < spec.size && guard++ < 4000)
                {
                    var list = clump.ToList();
                    var seed = list[rng.Next(list.Count)];
                    int dir = rng.Next(6);
                    var (dq, dr) = Neighbor(dir);
                    int nq = seed.Item1 + dq, nr = seed.Item2 + dr;
                    if (nr < rMin || nr > rMax) continue;
                    if (occupied.Contains(Key(nq, nr))) continue;
                    clump.Add((nq, nr));
                }

                foreach (var cell in clump)
                {
                    if (!occupied.Add(Key(cell.Item1, cell.Item2))) continue;
                    q.Add(cell.Item1);
                    r.Add(cell.Item2);
                    alt.Add(0);
                    // Mostly black (tier 1) with gold (tier 2) veins.
                    layer.Add(rng.NextDouble() < 0.27 ? 2 : 1);
                }
            }
        }

        private static (int dq, int dr) Neighbor(int i)
        {
            switch (i)
            {
                case 0: return (1, 0);
                case 1: return (1, -1);
                case 2: return (0, -1);
                case 3: return (-1, 0);
                case 4: return (-1, 1);
                default: return (0, 1);
            }
        }

        // ------------------------------------------------------------------ map file

        private static string MapPath()
        {
            string dir = Path.GetDirectoryName(typeof(CombinedContract).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = Path.Combine(BepInEx.Paths.PluginPath, "GraveyardOrbit.Coop"); }
                catch { dir = "."; }
            }
            return Path.Combine(dir, "maps", Id + ".json");
        }

        private static string ReadPrebuiltMap()
        {
            try
            {
                string path = MapPath();
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[contract] could not read prebuilt map: " + ex.Message);
            }
            return null;
        }

        private static void WriteMapFile(string json)
        {
            try
            {
                string path = MapPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path)) File.WriteAllText(path, json);
                CoopPlugin.Log.LogInfo("[contract] map file written to " + path);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[contract] could not write map file: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ catalog

        private static BandDef[] MergeBands(BandDef[] a, BandDef[] b)
        {
            var list = new List<BandDef>();
            if (a != null) list.AddRange(a.Where(x => x != null));
            if (b != null)
            {
                foreach (BandDef band in b)
                {
                    if (band != null && !list.Any(x => x.layer == band.layer)) list.Add(band);
                }
            }
            list.Sort((x, y) => x.layer.CompareTo(y.layer));
            return list.ToArray();
        }

        private static bool AlreadyPresent(StructureCatalog catalog)
        {
            return Contains(catalog.campaign) || Contains(catalog.ladder) || Contains(catalog.chapters);
        }

        private static bool Contains(StructureDef[] arr)
        {
            if (arr == null) return false;
            foreach (StructureDef def in arr)
            {
                if (def != null && def.id == Id) return true;
            }
            return false;
        }

        private static void Append(StructureCatalog catalog, StructureDef def)
        {
            catalog.campaign = Prepend(catalog.campaign, def);
            catalog.ladder = Prepend(catalog.ladder, def);
        }

        private static StructureDef[] Prepend(StructureDef[] arr, StructureDef def)
        {
            if (arr == null) return new[] { def };
            var list = new List<StructureDef> { def };
            list.AddRange(arr);
            return list.ToArray();
        }

        private static void Cache(StructureDef def)
        {
            try
            {
                var field = AccessTools.Field(typeof(StructureCatalog), "_cache");
                if (field?.GetValue(null) is Dictionary<string, StructureDef> cache)
                {
                    cache[def.id] = def;
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogDebug("[contract] cache insert failed: " + ex.Message);
            }
        }

        private static StructureDef FindSource(StructureCatalog catalog, params string[] keys)
        {
            foreach (StructureDef[] arr in new[]
            {
                catalog.campaign, catalog.ladder, catalog.chapters,
                catalog.demoCampaign, catalog.demoTeasers, catalog.testLevels
            })
            {
                if (arr == null) continue;
                foreach (StructureDef def in arr)
                {
                    if (def == null) continue;
                    string norm = Norm(def.id + def.displayName);
                    foreach (string key in keys)
                    {
                        if (norm.Contains(key)) return def;
                    }
                }
            }
            return null;
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static string NameOf(StructureDef def) => def == null ? "?" : $"{def.id} ({def.DisplayName})";

        private static void LogCatalog(StructureCatalog catalog)
        {
            foreach (StructureDef[] arr in new[] { catalog.campaign, catalog.ladder, catalog.chapters })
            {
                if (arr == null) continue;
                CoopPlugin.Log.LogInfo("[contract] catalog: " +
                    string.Join(", ", arr.Where(d => d != null).Select(d => $"{d.id}={d.DisplayName}")));
            }
        }
    }
}
