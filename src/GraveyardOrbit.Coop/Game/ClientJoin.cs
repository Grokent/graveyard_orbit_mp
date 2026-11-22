using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Client-side handoff: write the host's save, reload GameState, and load the
    /// gameplay scene so the client leaves the lobby with the host.
    /// </summary>
    internal static class ClientJoin
    {
        public static void Enter(string scene, string contractId, string saveJson,
            int levelSeed, int anomalySeed)
        {
            try
            {
                SaveSlots.Switch(SaveSlot.Custom);

                if (!string.IsNullOrEmpty(saveJson))
                {
                    string path = SaveSlots.SavePathFor(SaveSlot.Custom);
                    Backup(path);
                    if (!SaveFile.Write(path, saveJson))
                    {
                        CoopPlugin.Log.LogError("[join] could not write host save to " + path);
                        return;
                    }
                    GameState.Load();
                    CoopPlugin.Log.LogInfo($"[join] applied host save ({saveJson.Length:N0} chars) and reloaded GameState");
                    try
                    {
                        var root = GameState.I?.Root;
                        var prof = root?["profile"] as JObject;
                        CoopPlugin.Log.LogInfo(
                            $"[join] seeds — level={root?["level_seed"]}, anomaly={prof?["anomaly_seed"]}");
                    }
                    catch { }
                }
                else
                {
                    // Fallback: no save transfer — apply the host's contract locally.
                    if (GameState.I == null) GameState.Load();
                    StructureDef def = StructureCatalog.Resolve(contractId);
                    if (def != null && GameState.I != null)
                    {
                        GameState.I.NewContract(def.id, def.startingMoney);
                        CoopPlugin.Log.LogWarning("[join] no host save received — started contract on local save");
                    }
                }

                ForceSeeds(levelSeed, anomalySeed);

                string target = string.IsNullOrEmpty(scene) ? "MiningScene" : scene;
                MultiplayerLobby.Close();
                CoopPlugin.Log.LogInfo($"[join] loading scene '{target}'");
                if (CoopPlugin.Instance != null) CoopPlugin.Instance.StartCoroutine(Load(target));
                else SceneManager.LoadScene(target);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[join] client enter failed: " + ex);
            }
        }

        private static void ForceSeeds(int levelSeed, int anomalySeed)
        {
            try
            {
                GameState gs = GameState.I;
                if (gs == null) return;

                if (levelSeed != 0) gs.Root["level_seed"] = levelSeed;
                if (anomalySeed != 0 && gs.Root["profile"] is JObject profile)
                {
                    profile["anomaly_seed"] = anomalySeed;
                }
                gs.Save();

                CoopPlugin.Log.LogInfo(
                    $"[join] forced seeds — level={levelSeed}, anomaly={anomalySeed} " +
                    $"(now level={gs.Root["level_seed"]}, anomaly={(gs.Root["profile"] as JObject)?["anomaly_seed"]})");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogError("[join] forcing seeds failed: " + ex);
            }
        }

        private static IEnumerator Load(string scene)
        {
            // Wait a moment for the lobby modal to close, then load directly.
            // FlowManager may still be busy with the menu transition, which is
            // why we do not rely on it here.
            yield return new WaitForSeconds(0.3f);
            CoopPlugin.Log.LogInfo($"[join] loading scene '{scene}'");
            SceneManager.LoadScene(scene);
        }

        // Launch is intentionally not mirrored. Each player launches their own
        // ship, so the host's return/relaunch cannot close a client's upgrade
        // screen or force them out of the base.

        /// <summary>Host cleared the structure: mirror the win locally.</summary>
        public static void HandleCleared()
        {
            MiningSortie sortie = CoopSession.Sortie;
            if (sortie == null) return;
            try { sortie.Field?.DevClearAll(); } catch { }
            try
            {
                HarmonyLib.AccessTools.Method(typeof(MiningSortie), "StructureCleared")
                    ?.Invoke(sortie, null);
                CoopPlugin.Log.LogInfo("[join] host cleared the structure - mirroring win");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[join] mirror win failed: " + ex.Message);
            }
        }

        /// <summary>Host returned to base: follow with a normal return (keeps cargo).</summary>
        public static void ReturnHome()
        {
            if (CoopSession.Mode != SessionMode.Client) return;
            if (!CoopSession.MarkRemoteReturn()) return;
            MiningSortie sortie = CoopSession.Sortie;
            if (sortie == null) return;
            try
            {
                sortie.BeginReturnHome();
                CoopPlugin.Log.LogInfo("[join] host returned - mirroring return home");
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[join] return home failed: " + ex.Message);
            }
        }

        private static IEnumerator LaunchRoutine()
        {
            for (int i = 0; i < 24; i++)
            {
                GameRoot root = UnityEngine.Object.FindAnyObjectByType<GameRoot>();
                if (root != null)
                {
                    var method = HarmonyLib.AccessTools.Method(typeof(GameRoot), "TryLaunch");
                    if (method != null)
                    {
                        object state = Refs.GameRootState?.GetValue(root);
                        string name = state != null ? state.ToString() : "?";
                        if (name != "Planet" && name != "Build" && name != "Process")
                        {
                            CoopPlugin.Log.LogInfo(
                                $"[join] ignoring launch request while in state '{name}' (prevents mid-sortie relaunch)");
                            yield break;
                        }

                        try
                        {
                            method.Invoke(root, null);
                            CoopPlugin.Log.LogInfo("[join] host launched - mirrored TryLaunch on client");
                        }
                        catch (Exception ex)
                        {
                            CoopPlugin.Log.LogWarning("[join] TryLaunch failed: " + ex.Message);
                        }
                        yield break;
                    }
                }
                yield return new WaitForSeconds(0.5f);
            }
            CoopPlugin.Log.LogInfo("[join] launch request timed out - launch manually when ready");
        }

        private static void Backup(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string backup = path + ".coop-backup";
                    if (!File.Exists(backup)) File.Copy(path, backup);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[join] save backup failed: " + ex.Message);
            }
        }
    }
}
