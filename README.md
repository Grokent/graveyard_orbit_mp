# Graveyard Orbit Co-op Mod

Cooperative multiplayer for **Graveyard Orbit** (Steam AppID 4654120).
BepInEx 5 + Harmony plugin with Steam P2P lobbies for up to 4 players: shared
host save, synced contract, tiles, loot, deaths, radar, weapon FX, remote ships,
and enemies that engage every player.

## ⚠️ Disclaimer

This project is **100% vibe coded** and **100% the product of AI use**. It is an
unofficial fan mod. I make **no claims of ownership** over Graveyard Orbit, its
code, assets, names, or anything derived from it. **All credit and all rights
belong to Anchorhead Games.** If you own the game, you are welcome to use this
mod for private co-op play. No game code or game asset files are redistributed
here.

## Install (Windows and Linux are the same)

You only need the release zip — no compiler, no setup, no separate Linux build.

1. Close Graveyard Orbit.
2. In Steam, right-click **Graveyard Orbit** → **Manage** → **Browse local files**.
   That opens the game folder (the one containing `GraveyardOrbit.exe`).
3. Download the latest release zip from
   **[Releases](https://github.com/Grokent/graveyard_orbit_mp/releases/latest)**
   (asset `GraveyardOrbit-Co-op-0.1.0-MP<build>.zip`).
4. Extract **everything** from the zip into the game folder that opened in step 2.
   It only adds files (`winhttp.dll`, `doorstop_config.ini`,
   `.doorstop_version`, `changelog.txt`, `VERSION.txt`, `BepInEx/`,
   `Report.*`, `Update.*`); nothing in the original game is replaced.
5. **Linux / Proton only:** add this launch option once (game → Properties →
   Launch Options). Windows needs no launch options:

   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

6. Launch the game through Steam normally.

Both players need the mod, and both must run the **same MP build** (shown on the
MULTIPLAYER menu entry and in the lobby header). The lobby blocks START on a
build mismatch.

## Update

The zip ships a self-updater, so after the first install you do not need to
download zips by hand. It finds your Steam copy of the game, asks GitHub for the
latest release, compares the build number in `VERSION.txt` and copies the new
files over the old ones. Settings in `BepInEx/config/` and your save are left
alone; the previous plugin DLL is kept as `GraveyardOrbit.Coop.dll.bak`.

- **Windows:** double-click **`Update.bat`** in the game folder.
- **Linux / Steam Deck:** in the game folder, run `./Update.sh`
  (`chmod +x Update.sh` once if needed).

Both scripts accept:

```
--force                reinstall even if already on the latest build
--game-dir "<path>"    use a specific game folder if detection fails
```

Close the game first. The script prints the installed build when it finishes;
both players must then be on that same build. Installs from MP16 or older do not
contain the script yet — update once with the zip from the release page, then
use the script from then on.

### Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`
and the `BepInEx` folder from the game folder.

## Play

1. Title screen → **MULTIPLAYER** (between NEW GAME and SETTINGS). Opening it
   hosts a friends-only Steam lobby and lists your online friends.
2. Use **INVITE OTHERS** or a friend row's **INVITE**. Invites carry a lobby
   connect string, so the friend joins even if their game was closed.
3. The host picks a contract and presses **START**. Clients automatically
   receive the host's save, load the contract, and spawn beside the host.
4. Launch is independent: each player can return to base or launch again without
   pulling anyone else out of the base or the upgrade screen. A structure clear
   is mirrored to everyone.

Logs and settings:

- Log: `BepInEx/LogOutput.log`
- Settings: `BepInEx/config/agentbox.graveyardorbit.coop.cfg`

## What is synced

- **Spawn fan:** each player launches on their own angle
  `(slot - (n-1)/2) * 15°` around the planet.
- **Host save is the session save**, including forced level/anomaly seeds.
- **Tiles:** every destroyed cell is reported and applied; destruction persists
  even if the other player is in the base when it happens.
- **Loot:** shared resource motes.
- **Enemies:** HP/death is host-authoritative via snapshots, while every player
  simulates enemy attacks locally — turrets, spiral shots and attack swarms
  engage the host and all clients equally. Dormant enemies are revealed by
  synced tile destruction.
- **Weapon FX:** beams, pulses, dynamo streams/orbs/blasts, cannon projectiles,
  probes, chain arcs and enemy shots use the game's real VFX on peers. Each
  shot carries its weapon and mods, so beam width, colour and pulse shape match
  what the shooter sees.
- **Remote ships** are rebuilt from each player's synced loadout; remote players
  appear as dots on the SENSOR MAP.
- **Deaths:** a destroyed ship disappears for everyone and returns on relaunch.

### Known limitations

- Client progression changes are local and are not written back to the host save.
- Tile HP/crack stages are local (destroyed/alive state is synced).
- Loot collection is per-player; only spawns are shared.
- Enemy HP/positions are host-authoritative, but attack cooldowns and swarm units
  are simulated per player, so swarm instances are not bit-identical across
  machines.
- Pulse waves are rebuilt from their parameters on peers; the particle trail is
  approximated.
- Early-access co-op: expect rough edges, and please send a report (below).

## Build from source (developers)

Requires a legitimate copy of the game, plus `unzip`, `curl` and Python 3.

```bash
./setup.sh      # project-local .NET SDK + BepInEx compile references
./build.sh      # builds the plugin
./deploy.sh     # additively installs BepInEx and copies the plugin
./package.sh    # rebuilds release/GraveyardOrbit-Co-op-<version>-MP<build>.zip
```

Set `GAME_DIR` if the game is not in the default Steam library. Everything is
installed project-locally under `tools/`; nothing is installed system-wide.

`decompiled/` is ILSpy output of the installed game and is gitignored — never
committed or redistributed. `docs/HOOKS.md` is the hook map; a game update can
move things and require re-recon.

## Reporting problems

Close the game and run the bundled collector:

- **Windows:** double-click `Report.bat`
- **Linux / Steam Deck:** `./Report.sh` from the game folder

It bundles the log, config, save seed summary and system info into a single
archive (`GraveyardOrbit-Co-op-Report-<date>` on the Desktop / in your home
folder). Send that archive.

## Layout

```
src/GraveyardOrbit.Coop/     plugin source
release/                     local package output (zips are release assets)
packaging/                   report collectors + update scripts shipped in the zip
docs/HOOKS.md                decompiled hook map
tools/                       project-local .NET SDK, BepInEx zip (gitignored)
decompiled/                  ILSpy output (gitignored, never redistributed)
```

## License and credits

The mod's own code is MIT licensed (see `LICENSE`). The release zip bundles
BepInEx, which is LGPL-2.1 (see `THIRD_PARTY_NOTICES.md` and
`licenses/LGPL-2.1.txt`).

Graveyard Orbit and all related content are © Anchorhead Games. This is an
unofficial, fan-made, AI-generated mod with no affiliation with or endorsement
from Anchorhead Games. All rights to the game and its assets remain with them.

Repository: <https://github.com/Grokent/graveyard_orbit_mp>
