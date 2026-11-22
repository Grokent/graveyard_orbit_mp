using BepInEx.Configuration;
using UnityEngine;

namespace GraveyardOrbit.Coop
{
    public sealed class CoopConfig
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<int> DebugSlot;
        public readonly ConfigEntry<int> DebugCount;
        public readonly ConfigEntry<float> SpawnSeparationDeg;
        public readonly ConfigEntry<int> TcpPort;
        public readonly ConfigEntry<string> SteamLobbyId;
        public readonly ConfigEntry<bool> UseSteam;
        public readonly ConfigEntry<int> SyncHz;
        public readonly ConfigEntry<string> HostKey;
        public readonly ConfigEntry<string> JoinKey;
        public readonly ConfigEntry<string> LeaveKey;
        public readonly ConfigEntry<bool> Verbose;
        public readonly ConfigEntry<bool> DebugOpenLobbyOnMenu;

        public CoopConfig(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true,
                "Master switch for the co-op mod.");

            DebugSlot = cfg.Bind("Spawn", "DebugSlot", -1,
                "Offline testing only: force a slot index (-1 = use session/vanilla).");
            DebugCount = cfg.Bind("Spawn", "DebugPlayerCount", 1,
                "Offline testing only: simulate this many players when no session is active.");
            SpawnSeparationDeg = cfg.Bind("Spawn", "SeparationDegrees", 15f,
                "Angular separation between players when exiting the base (degrees).");

            UseSteam = cfg.Bind("Network", "UseSteam", true,
                "true = Steam P2P lobbies (shipping path); false = direct TCP (dev testing).");
            TcpPort = cfg.Bind("Network", "TcpPort", 27015,
                "TCP port for direct-connect dev testing.");
            SteamLobbyId = cfg.Bind("Network", "SteamLobbyId", "",
                "Optional: lobby id to join directly (hex/uint64 string).");
            SyncHz = cfg.Bind("Network", "ShipStateHz", 20,
                "Ship transform updates per second.");

            HostKey = cfg.Bind("Hotkeys", "Host", "F8",
                "Host a session (creates a Steam lobby, or listens on TcpPort).");
            JoinKey = cfg.Bind("Hotkeys", "Join", "F9",
                "Join a session (Steam invite overlay, or 127.0.0.1:TcpPort).");
            LeaveKey = cfg.Bind("Hotkeys", "Leave", "F10",
                "Leave the current session.");

            Verbose = cfg.Bind("General", "VerboseLogging", false,
                "Log every network message.");

            DebugOpenLobbyOnMenu = cfg.Bind("Debug", "OpenLobbyOnMenu", false,
                "TESTING ONLY: auto-open the multiplayer lobby when the title menu builds.");
        }

        public static bool TryParseKey(string s, out UnityEngine.InputSystem.Key key)
        {
            return System.Enum.TryParse(s, true, out key);
        }
    }
}
