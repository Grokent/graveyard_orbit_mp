using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace GraveyardOrbit.Coop
{
    public enum MessageKind
    {
        Hello,
        Roster,
        Loadout,
        ShipState,
        Phase,
        Contract,
        SaveBegin,
        SaveChunk,
        FxShot,
        EnemySnapshot,
        EnemyDamage,
        PlayerDead,
        TileDestroyed,
        LootSpawn,
        Ping
    }

    public sealed class PlayerEntry
    {
        public ulong id;
        public int slot;
        public string name;
    }

    public sealed class LoadoutPart
    {
        public int uid;
        public string type;
        public int level;
        public int tier;
        public string[] cells;   // "x,y"
        public string[] mods;
        public string[] powerups;
    }

    public sealed class NetMessage
    {
        public string kind;
        public string version;
        public string build;
        public string coopBuild;
        public string name;

        public int slot;
        public int count;
        public List<PlayerEntry> players;

        public List<LoadoutPart> loadout;

        // ship state
        public float x, y, z, yaw;
        public uint tick;

        public string phase;
        public string contract;
        public string scene;

        public string saveId;
        public int chunk;
        public int chunks;
        public string data;

        // weapon fire FX
        public string fx;
        public float ox, oy, oz;
        public float dx, dy, dz;
        public float tx, ty, tz;
        public float speed, dist;
        public float cr, cg, cb;

        public int levelSeed;
        public int anomalySeed;

        // enemy snapshot
        public int[] eq, er, ehp, emax;
        public float[] ex, ez;

        // enemy damage request
        public int dq, dr, dmg;

        // projectile visual + loot
        public string fxName;
        public float fxScale = 1f;
        public float hit;       // client-side hit radius for mirrored enemy shots
        public float lx, ly, lz;
        public int lv;
        public string lmat;
    }

    public static class Protocol
    {
        public const byte Version = 1;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        public static byte[] Encode(NetMessage msg)
        {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg, Settings));
        }

        public static bool TryDecode(byte[] data, out NetMessage msg)
        {
            msg = null;
            try
            {
                msg = JsonConvert.DeserializeObject<NetMessage>(Encoding.UTF8.GetString(data), Settings);
                return msg != null && !string.IsNullOrEmpty(msg.kind);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log?.LogWarning("Bad network message: " + ex.Message);
                return false;
            }
        }
    }
}
