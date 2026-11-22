using HarmonyLib;
using System;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    /// <summary>Host-side session start: applies the chosen contract and loads gameplay.</summary>
    internal static class GameFlow
    {
        internal static bool TryStartHostGame(out string error)
        {
            error = null;
            string id = CoopSession.SelectedContractId;
            if (string.IsNullOrEmpty(id))
            {
                error = "select a contract";
                return false;
            }

            StructureDef def = Resolve(id);
            if (def == null)
            {
                error = "unknown contract " + id;
                return false;
            }

            var boot = UnityEngine.Object.FindAnyObjectByType<MainMenuBoot>();
            if (boot == null)
            {
                error = "menu not found";
                return false;
            }

            try
            {
                SaveSlots.Switch(SaveSlot.Custom);

                var start = AccessTools.Method(typeof(MainMenuBoot), "StartContract");
                if (start == null)
                {
                    error = "StartContract hook missing";
                    return false;
                }

                string scene = boot.gameplayScene;
                start.Invoke(boot, new object[] { def, false });

                // NewContract() replaces Root, so seeds must be forced after the
                // contract starts and before the (async) scene load initialises.
                EnsureSessionSeeds();

                int levelSeed = 0, anomalySeed = 0;
                try
                {
                    levelSeed = GameState.I != null ? GameState.I.LevelSeed : 0;
                    anomalySeed = GameState.I != null ? AnomalyState.Seed(GameState.I) : 0;
                }
                catch (Exception ex) { CoopPlugin.Log.LogWarning("[flow] seed read failed: " + ex.Message); }

                string saveJson = null;
                try { saveJson = GameState.I?.Root?.ToString(Newtonsoft.Json.Formatting.None); }
                catch (Exception ex) { CoopPlugin.Log.LogWarning("[flow] save capture failed: " + ex.Message); }

                CoopSession.NotifyHostStarted(def.id, saveJson, scene, levelSeed, anomalySeed);
                CoopPlugin.Log.LogInfo($"[flow] host started contract '{def.id}' — peers notified");
                return true;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[flow] start failed: " + ex);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// LevelSeed/AnomalySeed generate lazily and land in the save. Force them
        /// before we snapshot the save, or each side rolls its own enemies/materials.
        /// </summary>
        private static void EnsureSessionSeeds()
        {
            try
            {
                GameState gs = GameState.I;
                if (gs == null) return;

                if (((int?)gs.Root["level_seed"]).GetValueOrDefault() == 0)
                {
                    gs.Root["level_seed"] = new System.Random().Next(1, int.MaxValue);
                }
                int anomaly = AnomalyState.Seed(gs);
                gs.Save();
                CoopPlugin.Log.LogInfo(
                    $"[flow] session seeds — level={gs.Root["level_seed"]}, anomaly={anomaly}");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[flow] seed setup failed: " + ex.Message);
            }
        }

        private static StructureDef Resolve(string id)
        {
            StructureDef def = StructureCatalog.Resolve(id);
            if (def != null) return def;

            var catalog = StructureCatalog.I;
            var list = catalog != null ? catalog.Contracts : null;
            if (list != null)
            {
                foreach (StructureDef d in list)
                {
                    if (d != null && d.id == id) return d;
                }
            }
            return null;
        }
    }
}
