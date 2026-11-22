using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>Replicates the local ship's weapon fire as cosmetic tracers on peers.</summary>
    internal static class WeaponFxSync
    {
        private static Material _lineMat;
        private static readonly Dictionary<int, float> _lastBeam = new Dictionary<int, float>();
        private static int _sendLogs;
        private static int _recvLogs;

        public static void BroadcastBolt(Vector3 origin, Vector3 dir, float speed, float maxDist, Color color,
            string visualFx = null, float visualScale = 1f)
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
                fxScale = visualScale
            };
            if (_sendLogs < 4)
            {
                _sendLogs++;
                CoopPlugin.Log.LogInfo($"[fx] sending bolt fx from ({origin.x:0.0},{origin.z:0.0})");
            }
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Beam weapon fired by the local ship (throttled per mount/slot).</summary>
        public static void BroadcastBeam(WeaponSystem ws, int i, Vector3 to, int slot)
        {            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || ws == null) return;

            float now = Time.unscaledTime;
            int key = i * 4 + slot;
            if (_lastBeam.TryGetValue(key, out float last) && now - last < 0.1f) return;
            _lastBeam[key] = now;

            Transform mount = (i >= 0 && i < ws.mounts.Count) ? ws.mounts[i] : null;
            if (mount == null) return;

            Color color = new Color(1f, 0.6f, 0.149f);
            try
            {
                if (AccessTools.Field(typeof(WeaponSystem), "_boltColor")?.GetValue(ws) is Color[] cols &&
                    i >= 0 && i < cols.Length)
                {
                    color = cols[i];
                }
            }
            catch { }

            Vector3 from = mount.position;
            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "beam",
                ox = from.x, oy = from.y, oz = from.z,
                dx = 0f, dy = 0f, dz = 0f,
                tx = to.x, ty = to.y, tz = to.z,
                cr = color.r, cg = color.g, cb = color.b
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Generic beam/arc between two world points (throttled by key).</summary>
        public static void BroadcastBeamRaw(int key, Vector3 from, Vector3 to, Color color)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null) return;

            float now = Time.unscaledTime;
            if (key >= 0)
            {
                if (_lastBeam.TryGetValue(key, out float last) && now - last < 0.1f) return;
                _lastBeam[key] = now;
            }

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "beam",
                ox = from.x, oy = from.y, oz = from.z,
                tx = to.x, ty = to.y, tz = to.z,
                cr = color.r, cg = color.g, cb = color.b
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
        }

        /// <summary>Dynamo orb / other projectile with a real VFX prefab.</summary>
        public static void BroadcastOrb(Vector3 origin, Vector3 dir, float speed, float dist,
            string fxName, float scale, Color color)
        {
            if (CoopSession.Mode == SessionMode.Off || CoopSession.Transport == null) return;
            if (CoopSession.Sortie == null || dir.sqrMagnitude < 1e-6f) return;

            var msg = new NetMessage
            {
                kind = MessageKind.FxShot.ToString(),
                fx = "bolt",
                ox = origin.x, oy = origin.y, oz = origin.z,
                dx = dir.x, dy = dir.y, dz = dir.z,
                speed = Mathf.Max(1f, speed),
                dist = Mathf.Max(1f, dist),
                cr = color.r, cg = color.g, cb = color.b,
                fxName = fxName,
                fxScale = scale
            };
            CoopSession.Transport.Broadcast(Protocol.Encode(msg), Delivery.Unreliable);
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

        public static void Play(NetMessage msg)
        {
            try
            {
                // Never spawn weapon visuals in the base scene.
                if (CoopSession.Sortie == null) return;

                var origin = new Vector3(msg.ox, msg.oy, msg.oz);
                var dir = new Vector3(msg.dx, msg.dy, msg.dz);
                var color = new Color(msg.cr, msg.cg, msg.cb, 1f);
                if (dir.sqrMagnitude < 1e-6f && msg.fx != "beam") return;

                if (_recvLogs < 4)
                {
                    _recvLogs++;
                    CoopPlugin.Log.LogInfo($"[fx] received remote {msg.fx} at ({origin.x:0.0},{origin.z:0.0})");
                }

                if (msg.fx == "beam")
                {
                    PlayBeam(origin, new Vector3(msg.tx, msg.ty, msg.tz), color);
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
                        msg.fxName, msg.fxScale);
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
            string fx, float scale)
        {
            GameObject go = VfxSpawner.Play(fx, origin, Quaternion.LookRotation(dir, Vector3.up),
                dist / Mathf.Max(1f, speed) + 0.25f);
            if (go == null)
            {
                PlayBolt(origin, dir, speed, dist, new Color(1f, 0.6f, 0.15f));
                return;
            }

            go.transform.localScale = Vector3.one * Mathf.Max(0.1f, scale);
            var mover = go.AddComponent<RemoteVisual>();
            mover.Setup(origin, dir, speed, dist);
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

        private static void PlayBolt(Vector3 origin, Vector3 dir, float speed, float dist, Color color)
        {
            var go = new GameObject("CoopRemoteBolt");
            var lr = go.AddComponent<LineRenderer>();
            Material mat = LineMaterial(color);
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.startWidth = 0.12f;
            lr.endWidth = 0.04f;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = color;
            lr.endColor = new Color(color.r, color.g, color.b, 0.25f);

            var bolt = go.AddComponent<RemoteBolt>();
            bolt.Setup(origin, dir, speed, dist, mat);
        }

        private static void PlayBeam(Vector3 from, Vector3 to, Color color)
        {
            var go = new GameObject("CoopRemoteBeam");
            var lr = go.AddComponent<LineRenderer>();
            Material mat = LineMaterial(color);
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.startWidth = 0.14f;
            lr.endWidth = 0.14f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = color;
            lr.endColor = color;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);

            var fade = go.AddComponent<RemoteBeamFade>();
            fade.Setup(0.18f, mat);
        }

        private static Material LineMaterial(Color color)
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
    }

    internal sealed class RemoteVisual : MonoBehaviour
    {
        private Vector3 _origin;
        private Vector3 _dir;
        private float _speed;
        private float _dist;
        private float _t;

        public void Setup(Vector3 origin, Vector3 dir, float speed, float dist)
        {
            _origin = origin;
            _dir = dir;
            _speed = speed;
            _dist = dist;
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
        private LineRenderer _line;
        private Material _mat;

        public void Setup(Vector3 origin, Vector3 dir, float speed, float dist, Material mat)
        {
            _origin = origin;
            _dir = dir;
            _speed = speed;
            _dist = dist;
            _mat = mat;
            _line = GetComponent<LineRenderer>();
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
            _line.SetPosition(0, tail);
            _line.SetPosition(1, head);
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
