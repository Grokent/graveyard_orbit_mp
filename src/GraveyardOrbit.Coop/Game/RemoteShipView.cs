using System;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Visual puppet for a remote player's ship. v1: clones the local ship's
    /// finished visual hierarchy (components stripped) and interpolates toward
    /// network snapshots. Later: rebuilt from each peer's synced loadout.
    /// </summary>
    public sealed class RemoteShipView : MonoBehaviour
    {
        private Vector3 _targetPos;
        private float _targetYaw;
        private float _lastPacket;
        private string _peerName = "?";

        public ulong Peer;

        public static RemoteShipView Spawn(MiningSortie sortie, ulong peer, string name)
        {
            ShipController master = sortie != null ? sortie.Ship : null;
            if (master == null) return null;

            GameObject built = RemoteShipBuilder.Build(sortie, peer);
            if (built != null)
            {
                try
                {
                    built.transform.position = master.transform.position;
                    built.transform.rotation = master.transform.rotation;
                    var view = built.AddComponent<RemoteShipView>();
                    view.Peer = peer;
                    view._peerName = name;
                    view._targetPos = built.transform.position;
                    view._targetYaw = built.transform.eulerAngles.y;
                    view._lastPacket = Time.time;
                    CoopPlugin.Log.LogInfo($"[view] built remote ship for {name} ({peer}) from loadout");
                    return view;
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log.LogWarning("[view] built ship setup failed: " + ex.Message);
                    UnityEngine.Object.Destroy(built);
                }
            }

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(master.gameObject, master.transform.parent, true);
                clone.name = "CoopRemote_" + peer;

                foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    UnityEngine.Object.DestroyImmediate(mb);
                }
                foreach (Collider c in clone.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null) UnityEngine.Object.DestroyImmediate(c);
                }
                foreach (Rigidbody rb in clone.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
                }

                clone.transform.position = master.transform.position;
                clone.transform.rotation = master.transform.rotation;

                var view = clone.AddComponent<RemoteShipView>();
                view.Peer = peer;
                view._peerName = name;
                view._targetPos = clone.transform.position;
                view._targetYaw = clone.transform.eulerAngles.y;
                view._lastPacket = Time.time;

                CoopPlugin.Log.LogInfo($"[view] spawned remote ship for {name} ({peer})");
                return view;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[view] clone failed, using fallback marker: " + ex);
                if (clone != null) UnityEngine.Object.Destroy(clone);

                var marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                marker.name = "CoopRemote_" + peer;
                marker.transform.position = master.transform.position;
                var view = marker.AddComponent<RemoteShipView>();
                view.Peer = peer;
                view._peerName = name;
                view._lastPacket = Time.time;
                return view;
            }
        }

        public void SetTarget(Vector3 pos, float yawDeg)
        {
            _targetPos = pos;
            _targetYaw = yawDeg;
            _lastPacket = Time.time;
        }

        private void Update()
        {
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _targetPos, k);
            float yaw = Mathf.LerpAngle(transform.eulerAngles.y, _targetYaw, k);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        public void DestroyView()
        {
            try { UnityEngine.Object.Destroy(gameObject); } catch { }
        }
    }
}
