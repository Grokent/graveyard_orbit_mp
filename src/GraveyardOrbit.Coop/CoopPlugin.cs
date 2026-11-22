using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    [BepInPlugin(PluginGuid, "Graveyard Orbit Co-op", PluginVersion)]
    public sealed class CoopPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "agentbox.graveyardorbit.coop";
        public const string PluginVersion = "0.1.0";
        public const string BuildTag = "15";
        public const string BuildLabel = "MP BUILD " + BuildTag;

        internal static CoopPlugin Instance;
        internal static ManualLogSource Log;
        internal static CoopConfig Cfg;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Cfg = new CoopConfig(Config);

            CoopSession.Configure(Cfg);

            _harmony = new Harmony(PluginGuid);
            try
            {
                _harmony.PatchAll(typeof(CoopPlugin).Assembly);
                Log.LogInfo("Harmony patches applied.");
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony PatchAll failed (mod will run without game hooks):\n" + ex);
            }

            Log.LogInfo($"Graveyard Orbit Co-op {PluginVersion} loaded.");
            LogSpawnAngles();
        }

        private static void LogSpawnAngles()
        {
            float sep = Cfg.SpawnSeparationDeg.Value;
            for (int n = 1; n <= 4; n++)
            {
                var parts = new System.Collections.Generic.List<string>();
                for (int s = 0; s < n; s++)
                {
                    parts.Add($"{SlotMath.AngleDeg(s, n, sep):0.##}°");
                }
                Log.LogInfo($"[spawnfan] {n} player(s) → [{string.Join(", ", parts)}]");
            }
        }

        private void Update()
        {
            try { CoopSession.Tick(Time.deltaTime); }
            catch (Exception ex) { Log.LogError("Coop tick failed: " + ex); }
        }

        private void OnDestroy()
        {
            try { CoopSession.Shutdown(); } catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
        }
    }
}
