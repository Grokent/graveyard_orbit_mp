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
        EnemyKilled,
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
        public string weapon;    // firing part/family id (laser, dynamo, pulse, cannon...)
        public string mods;      // comma-joined mod ids on the firing mount
        public float w, w2;      // beam core width / wave halo width
        public float a0, a1;     // wave arc start/end angle (radians)
        public float delay;      // wave start delay (pulse echo mod)
        public bool boom;        // spawn the impact FX at the beam endpoint

        // batched chain-arc segments: x1,y1,z1, x2,y2,z2 per arc
        public float[] arcs;

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
        public string fx2;       // impact/splash prefab played at the end of a bolt
        public float fxScale2 = 1f;
        public float hit;       // client-side hit radius for mirrored enemy shots
        public float lx, ly, lz;
        public int lv;
        public string lmat;
    }

    public static class Protocol
    {
        public const byte Version = 1;

        // Binary FX packets start with this byte; JSON objects start with '{'
        // (0x7B), so a single byte is enough to tell the formats apart.
        internal const byte FxMagic = 0xF1;

        internal const byte FxBeam = 1;
        internal const byte FxBolt = 2;
        internal const byte FxWave = 3;
        internal const byte FxStatic = 4;
        internal const byte FxEnemy = 5;
        internal const byte FxArcs = 6;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        public static byte[] Encode(NetMessage msg)
        {
            if (msg != null && msg.kind == nameof(MessageKind.FxShot))
            {
                return EncodeFx(msg);
            }
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg, Settings));
        }

        public static bool TryDecode(byte[] data, out NetMessage msg)
        {
            msg = null;
            if (data == null || data.Length == 0) return false;

            if (data[0] == FxMagic)
            {
                try
                {
                    return DecodeFx(data, out msg);
                }
                catch (Exception ex)
                {
                    CoopPlugin.Log?.LogWarning("[fx] bad binary packet: " + ex.Message);
                    return false;
                }
            }

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

        // ------------------------------------------------------- binary FX

        /// <summary>
        /// FxShot is the only high-frequency message kind, so it gets a compact
        /// binary layout: no reflection, no JSON strings, one small byte[].
        /// </summary>
        private static byte[] EncodeFx(NetMessage m)
        {
            var w = FxWriter.Rent();
            try
            {
                w.U8(FxMagic);

                byte type;
                switch (m.fx)
                {
                    case "beam": type = FxBeam; break;
                    case "wave": type = FxWave; break;
                    case "vfx": type = FxStatic; break;
                    case "enemy": type = FxEnemy; break;
                    case "arcs": type = FxArcs; break;
                    default: type = FxBolt; break;
                }
                w.U8(type);

                switch (type)
                {
                    case FxBeam:
                        w.Vec3(m.ox, m.oy, m.oz);
                        w.Vec3(m.tx, m.ty, m.tz);
                        w.Col(m.cr, m.cg, m.cb);
                        w.F32(m.w);
                        w.Bool(m.boom);
                        w.Str(m.fxName);
                        w.Str(m.weapon);
                        w.Str(m.mods);
                        break;

                    case FxBolt:
                        w.Vec3(m.ox, m.oy, m.oz);
                        w.Vec3(m.dx, m.dy, m.dz);
                        w.F32(m.speed);
                        w.F32(m.dist);
                        w.Col(m.cr, m.cg, m.cb);
                        w.Str(m.fxName);
                        w.F32(m.fxScale);
                        w.Str(m.fx2);
                        w.F32(m.fxScale2);
                        w.Str(m.weapon);
                        w.Str(m.mods);
                        break;

                    case FxWave:
                        w.Vec3(m.ox, m.oy, m.oz);
                        w.F32(m.speed);
                        w.F32(m.dist);
                        w.Col(m.cr, m.cg, m.cb);
                        w.F32(m.w);
                        w.F32(m.w2);
                        w.F32(m.a0);
                        w.F32(m.a1);
                        w.F32(m.delay);
                        w.F32(m.hit);
                        w.F32(m.fxScale);
                        w.Str(m.weapon);
                        w.Str(m.mods);
                        break;

                    case FxStatic:
                        w.Vec3(m.ox, m.oy, m.oz);
                        w.F32(m.fxScale);
                        w.F32(m.dist);
                        w.Str(m.fxName);
                        w.Str(m.weapon);
                        w.Str(m.mods);
                        break;

                    case FxEnemy:
                        w.Vec3(m.ox, m.oy, m.oz);
                        w.Vec3(m.dx, m.dy, m.dz);
                        w.F32(m.speed);
                        w.F32(m.dist);
                        w.I32(m.dmg);
                        w.F32(m.fxScale);
                        w.F32(m.hit);
                        break;

                    case FxArcs:
                        float[] arcs = m.arcs ?? Array.Empty<float>();
                        int n = arcs.Length / 6;
                        w.I32(n);
                        for (int i = 0; i < n * 6; i++) w.F32(arcs[i]);
                        break;
                }

                return w.ToArray();
            }
            finally
            {
                FxWriter.Return(w);
            }
        }

        private static bool DecodeFx(byte[] data, out NetMessage msg)
        {
            msg = null;
            var r = new FxReader(data, 1);
            byte type = r.U8();

            var m = new NetMessage { kind = nameof(MessageKind.FxShot) };

            switch (type)
            {
                case FxBeam:
                    m.fx = "beam";
                    m.ox = r.F32(); m.oy = r.F32(); m.oz = r.F32();
                    m.tx = r.F32(); m.ty = r.F32(); m.tz = r.F32();
                    m.cr = r.F32(); m.cg = r.F32(); m.cb = r.F32();
                    m.w = r.F32();
                    m.boom = r.Bool();
                    m.fxName = r.Str();
                    m.weapon = r.Str();
                    m.mods = r.Str();
                    break;

                case FxBolt:
                    m.fx = "bolt";
                    m.ox = r.F32(); m.oy = r.F32(); m.oz = r.F32();
                    m.dx = r.F32(); m.dy = r.F32(); m.dz = r.F32();
                    m.speed = r.F32();
                    m.dist = r.F32();
                    m.cr = r.F32(); m.cg = r.F32(); m.cb = r.F32();
                    m.fxName = r.Str();
                    m.fxScale = r.F32();
                    m.fx2 = r.Str();
                    m.fxScale2 = r.F32();
                    m.weapon = r.Str();
                    m.mods = r.Str();
                    break;

                case FxWave:
                    m.fx = "wave";
                    m.ox = r.F32(); m.oy = r.F32(); m.oz = r.F32();
                    m.speed = r.F32();
                    m.dist = r.F32();
                    m.cr = r.F32(); m.cg = r.F32(); m.cb = r.F32();
                    m.w = r.F32();
                    m.w2 = r.F32();
                    m.a0 = r.F32();
                    m.a1 = r.F32();
                    m.delay = r.F32();
                    m.hit = r.F32();
                    m.fxScale = r.F32();
                    m.weapon = r.Str();
                    m.mods = r.Str();
                    break;

                case FxStatic:
                    m.fx = "vfx";
                    m.ox = r.F32(); m.oy = r.F32(); m.oz = r.F32();
                    m.fxScale = r.F32();
                    m.dist = r.F32();
                    m.fxName = r.Str();
                    m.weapon = r.Str();
                    m.mods = r.Str();
                    break;

                case FxEnemy:
                    m.fx = "enemy";
                    m.ox = r.F32(); m.oy = r.F32(); m.oz = r.F32();
                    m.dx = r.F32(); m.dy = r.F32(); m.dz = r.F32();
                    m.speed = r.F32();
                    m.dist = r.F32();
                    m.dmg = r.I32();
                    m.fxScale = r.F32();
                    m.hit = r.F32();
                    break;

                case FxArcs:
                    m.fx = "arcs";
                    int n = r.I32();
                    if (n < 0 || n > 256) throw new InvalidOperationException("arc count " + n);
                    m.arcs = new float[n * 6];
                    for (int i = 0; i < n * 6; i++) m.arcs[i] = r.F32();
                    break;

                default:
                    throw new InvalidOperationException("unknown fx type " + type);
            }

            msg = m;
            return true;
        }

        /// <summary>Allocation-light little-endian writer with a pooled buffer.</summary>
        private sealed class FxWriter
        {
            private static readonly Stack<FxWriter> _pool = new Stack<FxWriter>();
            private byte[] _buf = new byte[512];
            private int _len;

            public static FxWriter Rent()
            {
                if (_pool.Count > 0)
                {
                    FxWriter w = _pool.Pop();
                    w._len = 0;
                    return w;
                }
                return new FxWriter();
            }

            public static void Return(FxWriter w)
            {
                if (w != null && _pool.Count < 8) _pool.Push(w);
            }

            private void Need(int n)
            {
                if (_len + n > _buf.Length)
                {
                    Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
                }
            }

            public void U8(byte v)
            {
                Need(1);
                _buf[_len++] = v;
            }

            public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);

            public void I32(int v)
            {
                Need(4);
                _buf[_len++] = (byte)v;
                _buf[_len++] = (byte)(v >> 8);
                _buf[_len++] = (byte)(v >> 16);
                _buf[_len++] = (byte)(v >> 24);
            }

            public void F32(float v) => I32(BitConverter.SingleToInt32Bits(v));

            public void Vec3(float x, float y, float z)
            {
                F32(x); F32(y); F32(z);
            }

            public void Col(float r, float g, float b)
            {
                F32(r); F32(g); F32(b);
            }

            public void Str(string s)
            {
                if (string.IsNullOrEmpty(s))
                {
                    U8(0);
                    return;
                }
                int n = Encoding.UTF8.GetByteCount(s);
                if (n > 255)
                {
                    U8(0);
                    return;
                }
                U8(1);
                U8((byte)n);
                Need(n);
                Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _len);
                _len += n;
            }

            public byte[] ToArray()
            {
                var outArr = new byte[_len];
                Buffer.BlockCopy(_buf, 0, outArr, 0, _len);
                return outArr;
            }
        }

        private struct FxReader
        {
            private readonly byte[] _b;
            private int _pos;

            public FxReader(byte[] b, int pos)
            {
                _b = b;
                _pos = pos;
            }

            public byte U8() => _b[_pos++];

            public bool Bool() => _b[_pos++] != 0;

            public int I32()
            {
                int v = _b[_pos]
                      | (_b[_pos + 1] << 8)
                      | (_b[_pos + 2] << 16)
                      | (_b[_pos + 3] << 24);
                _pos += 4;
                return v;
            }

            public float F32() => BitConverter.Int32BitsToSingle(I32());

            public string Str()
            {
                if (_b[_pos++] == 0) return null;
                int n = _b[_pos++];
                string s = Encoding.UTF8.GetString(_b, _pos, n);
                _pos += n;
                return s;
            }
        }
    }
}
