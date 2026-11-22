using UnityEngine;

namespace GraveyardOrbit.Coop
{
    public static class SlotMath
    {
        /// <summary>
        /// Centered fan: adjacent players are exactly <paramref name="separationDeg"/>
        /// apart. slot 0..n-1 → e.g. n=4 @15° → -22.5, -7.5, +7.5, +22.5.
        /// A single player keeps angle 0 (vanilla launch line).
        /// </summary>
        public static float AngleDeg(int slot, int count, float separationDeg)
        {
            if (count <= 1) return 0f;
            slot = Mathf.Clamp(slot, 0, count - 1);
            return (slot - (count - 1) * 0.5f) * separationDeg;
        }

        /// <summary>Rotate a world point around a vertical axis through <paramref name="center"/>.</summary>
        public static Vector3 RotateAroundY(Vector3 point, Vector3 center, float angleDeg)
        {
            Vector3 d = point - center;
            float rad = angleDeg * Mathf.Deg2Rad;
            float s = Mathf.Sin(rad);
            float c = Mathf.Cos(rad);
            return center + new Vector3(d.x * c + d.z * s, d.y, -d.x * s + d.z * c);
        }
    }
}
