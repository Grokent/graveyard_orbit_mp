using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Replicates the local ship's weapon fire as cosmetic effects on peers.
    /// Every message carries the firing weapon and its mods so the remote side
    /// can pick the same prefab, beam width and colour the shooter sees:
    ///   laser / pulse  -> real SciFi beam prefab + impact FX
    ///   dynamo         -> blue stream beam + ProtonMissile orbs + DomeNuke blasts
    ///   pulse          -> expanding ResonatorWave rings
    ///   cannon / guns  -> real projectile prefab + impact FX
    /// </summary>
    internal static class WeaponFxSync
    {
        private const float BeamThrottle = 0.08f;
        private const string LaserBeamFx = "SciFiBeams/BeamLaserOrange";
        private const string LaserImpactFx = "SciFiBeams/BeamLaserEndOrange";
        private const string DynamoBeamFx = "SciFiBeams/BeamLaserBlue";

        private static readonly Dictionary<int, float> _lastBeam = new Dictionary<int, float>();
        private static readonly Color LaserFallback = new Color(1f, 0.6f, 0.149f);
        private static readonly Color DynamoFallback = new Color(0.25f, 0.5f, 1f);

        private static FieldInfo _beams, _beamsX, _streams;
        private static FieldInfo _wCenter, _wMaxR, _wSpeed, _wCore, _wHalo, _wTrail, _wHex, _wColor;
        private static FieldInfo _wCone, _wConeBase, _wConeHalf;
        private static int _sendLogs;
        private static int _recvLogs;

        // ---------------------------------------------------------------- send

        /// <summary>Beam weapon fired by the local ship (throttled per mount/slot).</summary>
        public static void BroadcastBeam(WeaponSystem ws, int i, Vector3 to, int slot, bool showImpact)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || ws == null || i < 0 || i >= ws.mounts.Count) return;
            Transform mount = ws.mounts[i];
            if (mount == null) return;

            float now = Time.unscaledTime;
            int key = 100 + i * 4 + slot;
            if (_lastBeam.TryGetValue(key, out float last) && now - last < BeamThrottle) return;
            _lastBeam[key] = now;

            LineRenderer lr = BeamRenderer(ws, i, slot);
            Color color = LineColor(lr, LaserFallback);
            float width = (lr != null && lr.widthMultiplier > 0f)
                ? lr.widthMultiplier
                : Mathf.Max(0.02f, ws.beamWidth);

            Vector3 from = mount.position;
            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "beam",
                fxName = LaserBeamFx,
                ox = from.x, oy = from.y, oz = from.z,
                tx = to.x, ty = from.y, tz = to.z,
                cr = color.r, cg = color.g, cb = color.b,
                w = Mathf.Max(0.005f, width),
                boom = showImpact,
                weapon = KindName(ws, i),
                mods = ModsFor(ws, i)
            };
            if (_sendLogs < 4)
            {
                _sendLogs++;
                CoopPlugin.Log.LogInfo(
                    $"[fx] sending {msg.weapon} beam w={msg.w:0.000} boom={showImpact} mods={msg.mods ?? "-"}");
            }
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Generic beam/arc between two world points (throttled by key).</summary>
        public static void BroadcastBeamRaw(int key, Vector3 from, Vector3 to, Color color,
            string prefab, float width, bool boom = false, string weapon = null, string mods = null)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null) return;

            float now = Time.unscaledTime;
            if (key >= 0)
            {
                if (_lastBeam.TryGetValue(key, out float last) && now - last < BeamThrottle) return;
                _lastBeam[key] = now;
            }

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "beam",
                fxName = prefab,
                ox = from.x, oy = from.y, oz = from.z,
                tx = to.x, ty = to.y, tz = to.z,
                cr = color.r, cg = color.g, cb = color.b,
                w = Mathf.Max(0.005f, width),
                boom = boom,
                weapon = weapon,
                mods = mods
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Dynamo (sweeper) stream: blue beam with the shooter's width/colour.</summary>
        public static void BroadcastDynamoStream(SweeperSystem sweeper, int i, Vector3 from, Vector3 to)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || sweeper == null) return;

            LineRenderer lr = StreamRenderer(sweeper, i);
            Color color = LineColor(lr, DynamoFallback);
            float width = (lr != null && lr.widthMultiplier > 0f) ? lr.widthMultiplier : 0.48f;
            BroadcastBeamRaw(1000 + i, from, to, color, DynamoBeamFx, width,
                boom: false, weapon: "dynamo");
        }

        /// <summary>Projectile with a real VFX prefab (cannon, shotgun, dynamo orbs...).</summary>
        public static void BroadcastBolt(Vector3 origin, Vector3 dir, float speed, float maxDist, Color color,
            string visualFx = null, float visualScale = 1f, string weapon = null, string mods = null,
            string impactFx = null, float impactScale = 1f)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null) return;
            if (dir.sqrMagnitude < 1e-6f) return;

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "bolt",
                ox = origin.x, oy = origin.y, oz = origin.z,
                dx = dir.x, dy = dir.y, dz = dir.z,
                speed = Mathf.Max(1f, speed),
                dist = Mathf.Max(1f, maxDist),
                cr = color.r, cg = color.g, cb = color.b,
                fxName = visualFx,
                fxScale = visualScale,
                fx2 = impactFx,
                fxScale2 = impactScale,
                weapon = weapon,
                mods = mods
            };
            if (_sendLogs < 8)
            {
                _sendLogs++;
                CoopPlugin.Log.LogInfo(
                    $"[fx] sending {weapon ?? "bolt"} ({visualFx ?? "line"}) from ({origin.x:0.0},{origin.z:0.0}) mods={mods ?? "-"}");
            }
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Dynamo orb with the real prefab + surge scale.</summary>
        public static void BroadcastOrb(Vector3 origin, Vector3 dir, float speed, float dist,
            string fxName, float scale, Color color, string weapon = "dynamo", string mods = null)
        {
            BroadcastBolt(origin, dir, speed, dist, color, fxName, scale, weapon, mods);
        }

        /// <summary>Host enemy projectile: mirrored as a real VFX bolt that can hit the client ship.</summary>
        public static void BroadcastEnemyShot(Vector3 from, Vector3 dir, float speed, float maxDist,
            int dmg, float scale, float hitDist)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || dir.sqrMagnitude < 1e-6f) return;

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "enemy",
                ox = from.x, oy = from.y, oz = from.z,
                dx = dir.x, dy = dir.y, dz = dir.z,
                speed = Mathf.Max(1f, speed),
                dist = Mathf.Max(1f, maxDist),
                dmg = dmg,
                fxScale = Mathf.Max(0.01f, scale),
                hit = Mathf.Max(0.1f, hitDist),
                cr = 1f, cg = 0.35f, cb = 0.25f
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Pulse/resonator wave: expanding ring with the shooter's style.</summary>
        public static void BroadcastWave(ResonatorWave wave)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || wave == null) return;

            EnsureWaveRefs();
            if (_wCenter == null || _wCore == null) return;

            Vector3 center = (Vector3)_wCenter.GetValue(wave);
            float maxR = Mathf.Max(0.5f, _wMaxR != null ? (float)_wMaxR.GetValue(wave) : 4f);
            float speed = Mathf.Max(0.1f, _wSpeed != null ? (float)_wSpeed.GetValue(wave) : maxR);
            float coreW = 0.1f, haloW = 0.2f, trailW = 0f, hex = 1f;
            var core = _wCore.GetValue(wave) as LineRenderer;
            var halo = _wHalo?.GetValue(wave) as LineRenderer;
            if (core != null) coreW = core.widthMultiplier;
            if (halo != null) haloW = halo.widthMultiplier;
            if (_wTrail != null) trailW = Mathf.Max(0f, (float)_wTrail.GetValue(wave));
            if (_wHex != null) hex = Mathf.Max(0.01f, (float)_wHex.GetValue(wave));
            Color color = _wColor != null ? (Color)_wColor.GetValue(wave) : new Color(0.69f, 0.5f, 1f, 1f);

            bool cone = _wCone != null && (bool)_wCone.GetValue(wave);
            float a0 = 0f, a1 = Mathf.PI * 2f;
            if (cone)
            {
                float baseAng = _wConeBase != null ? (float)_wConeBase.GetValue(wave) : 0f;
                float half = _wConeHalf != null ? (float)_wConeHalf.GetValue(wave) : Mathf.PI;
                a0 = baseAng - half;
                a1 = baseAng + half;
            }

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "wave",
                ox = center.x, oy = center.y, oz = center.z,
                speed = speed,
                dist = maxR,
                cr = Mathf.Clamp01(color.r), cg = Mathf.Clamp01(color.g), cb = Mathf.Clamp01(color.b),
                w = Mathf.Max(0.01f, coreW),
                w2 = Mathf.Max(0.01f, haloW),
                a0 = a0, a1 = a1,
                delay = Mathf.Max(0f, wave.StartDelay),
                hit = trailW,
                fxScale = hex,
                weapon = wave.Attacker,
                mods = ModsForUid(wave.PartUid)
            };
            if (_sendLogs < 12)
            {
                _sendLogs++;
                CoopPlugin.Log.LogInfo(
                    $"[fx] sending pulse wave r={maxR:0.0} speed={speed:0.0} core={coreW:0.00} halo={haloW:0.00}");
            }
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>One-shot static VFX (dynamo nova/blast, etc.).</summary>
        public static void BroadcastVfx(string fxName, Vector3 at, float scale, float life = 2.6f,
            string weapon = null, string mods = null)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || string.IsNullOrEmpty(fxName)) return;

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "vfx",
                ox = at.x, oy = at.y, oz = at.z,
                fxName = fxName,
                fxScale = Mathf.Max(0.01f, scale),
                dist = Mathf.Max(0.1f, life),
                weapon = weapon,
                mods = mods
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        // -------------------------------------------------------------- receive

        public static void Play(NetMessage msg)
        {
            try
            {
                // Never spawn weapon visuals in the base scene.
                if (CoopSession.Sortie == null) return;

                var origin = new Vector3(msg.ox, msg.oy, msg.oz);
                var dir = new Vector3(msg.dx, msg.dy, msg.dz);
                var color = new Color(msg.cr, msg.cg, msg.cb, 1f);
                bool directional = msg.fx != "beam" && msg.fx != "wave" && msg.fx != "vfx";
                if (directional && dir.sqrMagnitude < 1e-6f) return;

                if (_recvLogs < 8)
                {
                    _recvLogs++;
                    CoopPlugin.Log.LogInfo(
                        $"[fx] received remote {msg.fx} {msg.weapon ?? ""} at ({origin.x:0.0},{origin.z:0.0}) mods={msg.mods ?? "-"}");
                }

                if (msg.fx == "beam")
                {
                    PlayBeam(origin, new Vector3(msg.tx, msg.ty, msg.tz), color,
                        msg.w, msg.fxName, msg.boom, msg.weapon);
                }
                else if (msg.fx == "wave")
                {
                    PlayWave(origin, msg.dist, msg.speed, color, msg.w, msg.w2, msg.hit,
                        msg.a0, msg.a1, msg.delay);
                }
                else if (msg.fx == "vfx")
                {
                    PlayStatic(msg.fxName, origin, msg.fxScale, msg.dist);
                }
                else if (msg.fx == "enemy")
                {
                    PlayEnemyShot(origin, dir.normalized, Mathf.Max(1f, msg.speed),
                        Mathf.Max(1f, msg.dist), msg.dmg, Mathf.Max(0.01f, msg.fxScale),
                        Mathf.Max(0.1f, msg.hit));
                }
                else if (!string.IsNullOrEmpty(msg.fxName))
                {
                    PlayVisual(origin, dir.normalized, Mathf.Max(1f, msg.speed), Mathf.Max(1f, msg.dist),
                        msg.fxName, msg.fxScale, msg.fx2, msg.fxScale2);
                }
                else
                {
                    PlayBolt(origin, dir.normalized, Mathf.Max(1f, msg.speed), Mathf.Max(1f, msg.dist), color);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[fx] remote shot failed: " + ex);
            }
        }

        private static void PlayVisual(Vector3 origin, Vector3 dir, float speed, float dist,
            string fx, float scale, string impactFx, float impactScale)
        {
            GameObject go = VfxSpawner.Play(fx, origin, Quaternion.LookRotation(dir, Vector3.up),
                dist / Mathf.Max(1f, speed) + 0.25f);
            if (go == null)
            {
                PlayBolt(origin, dir, speed, dist, LaserFallback);
                return;
            }

            go.transform.localScale = Vector3.one * Mathf.Max(0.1f, scale);
            // VfxSpawner pools prefabs, so a stale mover can still be attached.
            var stale = go.GetComponent<RemoteVisual>();
            if (stale != null) stale.enabled = false;
            var mover = go.AddComponent<RemoteVisual>();
            mover.Setup(origin, dir, speed, dist, impactFx, impactScale);
        }

        private static void PlayStatic(string fx, Vector3 at, float scale, float life)
        {
            GameObject go = VfxSpawner.Play(fx, at, Quaternion.identity, Mathf.Max(0.1f, life));
            if (go != null)
            {
                go.transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
            }
        }

        private static void PlayBeam(Vector3 from, Vector3 to, Color color, float width,
            string prefab, bool impact, string weapon)
        {
            GameObject go = null;
            LineRenderer lr = null;
            GameObject source = null;
            if (!string.IsNullOrEmpty(prefab))
            {
                try { source = Resources.Load<GameObject>(prefab); } catch { }
            }

            if (source != null)
            {
                go = UnityEngine.Object.Instantiate(source);
                go.name = "CoopRemoteBeam";
                try { VfxSpawner.StripForeignAudio(go); } catch { }
                lr = go.GetComponent<LineRenderer>();
                if (lr == null) lr = go.AddComponent<LineRenderer>();
            }
            else
            {
                go = new GameObject("CoopRemoteBeam");
                lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = LineMaterial(color);
            }

            float w = width > 0f ? width : 0.05f;
            go.transform.position = from;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.widthMultiplier = w;
            // Tint the material (like the local game does); keep vertex colours
            // white so the beam is not tinted twice.
            lr.startColor = Color.white;
            lr.endColor = Color.white;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);

            // The local game tints the beam material (per mount), not the
            // shared prefab material, so clone it here too.
            Material mat = null;
            try
            {
                mat = lr.material;
                if (mat != null)
                {
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                    if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", color);
                    mat.color = color;
                }
            }
            catch { }
            try { WeaponSystem.TintFxRenderers(go, color); } catch { }

            if (impact)
            {
                try
                {
                    // Continuous beams use the beam end prefab; pop lasers use
                    // the same quick plasma impact the shooter sees.
                    string id = weapon ?? "";
                    bool continuous = id.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0
                        || id.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0
                        || id.IndexOf("spray", StringComparison.OrdinalIgnoreCase) >= 0
                        || id.IndexOf("railgun", StringComparison.OrdinalIgnoreCase) >= 0;
                    string fxId = continuous ? LaserImpactFx : "WeaponFX/ModularPlasmaImpact";
                    float scale = continuous ? 0.75f : WeaponFxTable.StandardImpactScale;
                    GameObject fx = VfxSpawner.Play(fxId, to, 0.35f);
                    if (fx != null)
                    {
                        fx.transform.localScale = Vector3.one * scale;
                        WeaponSystem.TintFxRenderers(fx, color);
                    }
                }
                catch { }
            }

            var fade = go.AddComponent<RemoteBeamFade>();
            fade.Setup(0.16f, mat);
        }

        private static void PlayWave(Vector3 center, float maxR, float speed, Color color,
            float coreW, float haloW, float trailW, float a0, float a1, float delay)
        {
            var go = new GameObject("CoopRemoteWave");
            var wave = go.AddComponent<RemoteWave>();
            wave.Setup(center, Mathf.Max(0.5f, maxR),
                Mathf.Max(0.1f, speed > 0f ? speed : maxR),
                color,
                Mathf.Max(0.01f, coreW),
                Mathf.Max(0.01f, haloW),
                Mathf.Max(0f, trailW),
                a0, a1, Mathf.Max(0f, delay));
        }

        private static void PlayBolt(Vector3 origin, Vector3 dir, float speed, float dist, Color color)
        {
            var go = new GameObject("CoopRemoteBolt");
            var lr = go.AddComponent<LineRenderer>();
            Material mat = LineMaterial(color);
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.startWidth = 0.05f;
            lr.endWidth = 0.015f;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = color;
            lr.endColor = new Color(color.r, color.g, color.b, 0.25f);

            var bolt = go.AddComponent<RemoteBolt>();
            bolt.Setup(origin, dir, speed, dist, mat);
        }

        private static void PlayEnemyShot(Vector3 origin, Vector3 dir, float speed, float dist,
            int dmg, float scale, float hitDist)
        {
            // Match the host's own shot visual: EnemyManager.SpawnShot puts a
            // LavaMissile VFX body (with its fire animation) on each projectile.
            Vector3 start = new Vector3(origin.x, 1f, origin.z);
            var go = new GameObject("CoopRemoteEnemyShot");
            go.transform.position = start;
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            go.transform.rotation = Quaternion.LookRotation(flat.normalized);

            bool visual = false;
            try
            {
                GameObject prefab = Resources.Load<GameObject>("WeaponFX/LavaMissile");
                if (prefab != null)
                {
                    EnemyManager.FxBody(prefab, go.transform, Mathf.Max(0.01f, scale) * 6f);
                    visual = true;
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogDebug("[fx] enemy shot visual failed: " + ex.Message);
            }

            if (!visual)
            {
                var lr = go.AddComponent<LineRenderer>();
                Material mat = LineMaterial(new Color(1f, 0.35f, 0.25f));
                lr.sharedMaterial = mat;
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = Mathf.Max(0.1f, hitDist * 0.4f);
                lr.endWidth = Mathf.Max(0.05f, hitDist * 0.15f);
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.startColor = new Color(1f, 0.35f, 0.25f, 1f);
                lr.endColor = new Color(1f, 0.35f, 0.25f, 0.25f);
            }

            var mover = go.AddComponent<RemoteEnemyShot>();
            mover.Setup(start, dir, speed, dist, dmg, hitDist);
        }

        // -------------------------------------------------------------- helpers

        public static Material LineMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                CoopPlugin.Log.LogWarning("[fx] no line shader found");
                shader = Shader.Find("Hidden/Internal-Colored");
            }

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }

        private static LineRenderer BeamRenderer(WeaponSystem ws, int i, int slot)
        {
            try
            {
                if (slot <= 0)
                {
                    _beams ??= AccessTools.Field(typeof(WeaponSystem), "beams");
                    var list = _beams?.GetValue(ws) as List<LineRenderer>;
                    return list != null && i >= 0 && i < list.Count ? list[i] : null;
                }
                _beamsX ??= AccessTools.Field(typeof(WeaponSystem), "_beamsX");
                var extra = _beamsX?.GetValue(ws) as List<LineRenderer>;
                int idx = i * 2 + (slot - 1);   // WeaponSystem.Ex(i, k)
                return extra != null && idx >= 0 && idx < extra.Count ? extra[idx] : null;
            }
            catch
            {
                return null;
            }
        }

        private static LineRenderer StreamRenderer(SweeperSystem sweeper, int i)
        {
            try
            {
                _streams ??= AccessTools.Field(typeof(SweeperSystem), "_stream");
                var streams = _streams?.GetValue(sweeper) as LineRenderer[];
                return streams != null && i >= 0 && i < streams.Length ? streams[i] : null;
            }
            catch
            {
                return null;
            }
        }

        private static Color LineColor(LineRenderer lr, Color fallback)
        {
            if (lr == null) return fallback;
            try
            {
                Material mat = lr.sharedMaterial;
                if (mat != null)
                {
                    Color c;
                    if (mat.HasProperty("_BaseColor"))
                    {
                        c = mat.GetColor("_BaseColor");
                        if (c.a > 0.01f) return c;
                    }
                    if (mat.HasProperty("_Color"))
                    {
                        c = mat.GetColor("_Color");
                        if (c.a > 0.01f) return c;
                    }
                    if (mat.HasProperty("_TintColor"))
                    {
                        c = mat.GetColor("_TintColor");
                        if (c.a > 0.01f) return c;
                    }
                }
                Color sc = lr.startColor;
                if (sc.a > 0.01f && sc.r + sc.g + sc.b > 0.01f) return sc;
            }
            catch { }
            return fallback;
        }

        private static string KindName(WeaponSystem ws, int i)
        {
            try
            {
                if (i >= 0 && i < ws.mountKinds.Count) return ws.mountKinds[i].ToString();
            }
            catch { }
            return "laser";
        }

        private static string ModsFor(WeaponSystem ws, int i)
        {
            try
            {
                if (i >= 0 && i < ws.mountParts.Count && ws.mountParts[i] != null)
                {
                    return JoinMods(ws.mountParts[i].mods);
                }
            }
            catch { }
            return null;
        }

        /// <summary>Mods on the local ship part with this uid (for projectile fire).</summary>
        public static string ModsForUid(int uid)
        {
            try
            {
                if (uid < 0 || GameState.I == null) return null;
                AccretionShip ship = GameState.I.Ship;
                if (ship == null || ship.placed == null) return null;
                foreach (AccretionShip.PlacedPart part in ship.placed)
                {
                    if (part != null && part.uid == uid) return JoinMods(part.mods);
                }
            }
            catch { }
            return null;
        }

        private static string JoinMods(List<string> mods)
        {
            if (mods == null || mods.Count == 0) return null;
            return string.Join(",", mods);
        }

        private static void EnsureWaveRefs()
        {
            if (_wCenter != null) return;
            Type t = typeof(ResonatorWave);
            _wCenter = AccessTools.Field(t, "_center");
            _wMaxR = AccessTools.Field(t, "_maxR");
            _wSpeed = AccessTools.Field(t, "_speed");
            _wCore = AccessTools.Field(t, "_core");
            _wHalo = AccessTools.Field(t, "_halo");
            _wTrail = AccessTools.Field(t, "_trailW");
            _wHex = AccessTools.Field(t, "_hexSize");
            _wColor = AccessTools.Field(t, "_color");
            _wCone = AccessTools.Field(t, "_coneOn");
            _wConeBase = AccessTools.Field(t, "_coneBaseAng");
            _wConeHalf = AccessTools.Field(t, "_coneHalfRad");
        }
    }

    internal sealed class RemoteVisual : MonoBehaviour
    {
        private Vector3 _origin;
        private Vector3 _dir;
        private float _speed;
        private float _dist;
        private float _t;
        private string _impactFx;
        private float _impactScale = 1f;

        public void Setup(Vector3 origin, Vector3 dir, float speed, float dist,
            string impactFx = null, float impactScale = 1f)
        {
            _origin = origin;
            _dir = dir;
            _speed = speed;
            _dist = dist;
            _impactFx = impactFx;
            _impactScale = Mathf.Max(0.01f, impactScale);
        }

        private void Update()
        {
            _t += Time.deltaTime;
            float travelled = _speed * _t;
            if (travelled >= _dist)
            {
                if (!string.IsNullOrEmpty(_impactFx))
                {
                    try
                    {
                        GameObject fx = VfxSpawner.Play(_impactFx,
                            _origin + _dir * _dist + Vector3.up * 0.4f, 2f);
                        if (fx != null) fx.transform.localScale = Vector3.one * _impactScale;
                    }
                    catch { }
                }
                Destroy(gameObject);
                return;
            }
            transform.position = _origin + _dir * travelled;
        }
    }

    internal sealed class RemoteBolt : MonoBehaviour
    {
        private Vector3 _origin;
        private Vector3 _dir;
        private float _speed;
        private float _dist;
        private float _t;
        private Material _mat;

        public void Setup(Vector3 origin, Vector3 dir, float speed, float dist, Material mat)
        {
            _origin = origin;
            _dir = dir;
            _speed = speed;
            _dist = dist;
            _mat = mat;
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }

        private void Update()
        {
            _t += Time.deltaTime;
            float travelled = _speed * _t;
            if (travelled >= _dist)
            {
                Destroy(gameObject);
                return;
            }

            Vector3 head = _origin + _dir * travelled;
            Vector3 tail = head - _dir * Mathf.Min(1.6f, Mathf.Max(0.25f, _speed * 0.06f));
            var line = GetComponent<LineRenderer>();
            if (line != null)
            {
                line.SetPosition(0, tail);
                line.SetPosition(1, head);
            }
        }
    }

    /// <summary>
    /// Client-side mirror of a pulse/resonator wave: the same expanding ring
    /// with the shooter's core/halo widths and colour.
    /// </summary>
    internal sealed class RemoteWave : MonoBehaviour
    {
        private const int Seg = 64;

        private Vector3 _center;
        private float _maxR;
        private float _speed;
        private float _coreW;
        private float _haloW;
        private float _trailW;
        private float _a0;
        private float _a1;
        private float _delay;
        private Color _color;
        private float _radius;
        private LineRenderer _core;
        private LineRenderer _halo;
        private LineRenderer _trail;
        private Material _mCore;
        private Material _mHalo;
        private Material _mTrail;

        public void Setup(Vector3 center, float maxR, float speed, Color color,
            float coreW, float haloW, float trailW, float a0, float a1, float delay)
        {
            _center = center;
            _maxR = maxR;
            _speed = speed;
            _color = color;
            _coreW = coreW;
            _haloW = haloW;
            _trailW = trailW;
            _a0 = a0;
            _a1 = a1 > a0 + 0.01f ? a1 : a0 + Mathf.PI * 2f;
            _delay = delay;
            transform.position = Vector3.zero;

            bool full = _a1 - _a0 >= Mathf.PI * 2f - 0.01f;
            _core = MakeRing("RemoteWaveCore", coreW, full, out _mCore);
            _halo = MakeRing("RemoteWaveHalo", haloW, full, out _mHalo);
            if (trailW > 0f) _trail = MakeRing("RemoteWaveTrail", trailW, full, out _mTrail);
            Draw(0f);
        }

        private LineRenderer MakeRing(string name, float width, bool loop, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, worldPositionStays: false);
            var lr = go.AddComponent<LineRenderer>();
            mat = WeaponFxSync.LineMaterial(Color.white);
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.positionCount = loop ? Seg : Seg + 1;
            lr.widthMultiplier = Mathf.Max(0.001f, width);
            lr.numCapVertices = 0;
            lr.alignment = LineAlignment.View;
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = _color;
            lr.endColor = _color;
            return lr;
        }

        private void Update()
        {
            if (_delay > 0f)
            {
                _delay -= Time.deltaTime;
                SetVisible(false);
                return;
            }
            SetVisible(true);
            _radius += _speed * Time.deltaTime;
            float k = Mathf.Clamp01(_radius / _maxR);
            if (k >= 1f)
            {
                Destroy(gameObject);
                return;
            }
            Draw(1f - k);
        }

        private void SetVisible(bool on)
        {
            if (_core != null) _core.enabled = on;
            if (_halo != null) _halo.enabled = on;
            if (_trail != null) _trail.enabled = on;
        }

        private void Draw(float fade)
        {
            float r = Mathf.Min(_radius, _maxR);
            DrawRing(_core, r, fade * 0.85f);
            DrawRing(_halo, Mathf.Max(r - _haloW, 0f), fade * 0.3f);
            if (_trail != null) DrawRing(_trail, Mathf.Max(r - _trailW, 0f), fade * 0.35f);
        }

        private void DrawRing(LineRenderer lr, float radius, float alpha)
        {
            if (lr == null) return;
            int n = lr.positionCount;
            int spans = lr.loop ? n : Mathf.Max(1, n - 1);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / spans;
                float f = Mathf.Lerp(_a0, _a1, t);
                lr.SetPosition(i, new Vector3(
                    _center.x + Mathf.Cos(f) * radius,
                    0.3f,
                    _center.z + Mathf.Sin(f) * radius));
            }
            Color c = new Color(_color.r, _color.g, _color.b, Mathf.Clamp01(alpha));
            lr.startColor = c;
            lr.endColor = c;
        }

        private void OnDestroy()
        {
            if (_mCore != null) Destroy(_mCore);
            if (_mHalo != null) Destroy(_mHalo);
            if (_mTrail != null) Destroy(_mTrail);
        }
    }

    /// <summary>
    /// Client-side mirror of a host enemy projectile. Flies the same path the
    /// host simulated and applies local hull damage when it reaches this
    /// client's ship (the host only tracks its own ship).
    /// </summary>
    internal sealed class RemoteEnemyShot : MonoBehaviour
    {
        private Vector3 _origin;
        private Vector3 _dir;
        private float _speed;
        private float _dist;
        private int _dmg;
        private float _hit;
        private float _t;
        private LineRenderer _line;

        public void Setup(Vector3 origin, Vector3 dir, float speed, float dist, int dmg, float hit)
        {
            _origin = origin;
            _dir = dir;
            _speed = speed;
            _dist = dist;
            _dmg = dmg;
            _hit = hit;
            _line = GetComponent<LineRenderer>();
        }

        private void OnDestroy()
        {
            if (_line != null && _line.sharedMaterial != null) Destroy(_line.sharedMaterial);
        }

        private void Update()
        {
            _t += Time.deltaTime;
            float travelled = _speed * _t;
            if (travelled >= _dist)
            {
                Destroy(gameObject);
                return;
            }

            Vector3 head = _origin + _dir * travelled;
            if (_line != null)
            {
                Vector3 tail = head - _dir * Mathf.Min(2.5f, Mathf.Max(0.4f, _speed * 0.08f));
                _line.SetPosition(0, tail);
                _line.SetPosition(1, head);
            }
            else
            {
                transform.position = head;
            }

            ShipController ship = CoopSession.Sortie != null ? CoopSession.Sortie.Ship : null;
            if (ship == null) return;

            Vector3 delta = head - ship.transform.position;
            delta.y = 0f;
            if (delta.sqrMagnitude > _hit * _hit) return;

            try { ship.OnEnemyHit(_dmg, head); } catch { }
            Destroy(gameObject);
        }
    }

    internal sealed class RemoteBeamFade : MonoBehaviour
    {
        private float _life;
        private float _t;
        private LineRenderer _line;
        private Color _a;
        private Color _b;
        private Material _mat;

        public void Setup(float life, Material mat)
        {
            _life = life;
            _mat = mat;
            _line = GetComponent<LineRenderer>();
            if (_line != null)
            {
                _a = _line.startColor;
                _b = _line.endColor;
            }
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }

        private void Update()
        {
            _t += Time.deltaTime;
            float k = Mathf.Clamp01(_t / _life);
            if (_line != null)
            {
                _line.startColor = new Color(_a.r, _a.g, _a.b, 1f - k);
                _line.endColor = new Color(_b.r, _b.g, _b.b, 1f - k);
            }
            if (k >= 1f) Destroy(gameObject);
        }
    }
}
