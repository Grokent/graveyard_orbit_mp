# AGENTS.md — Graveyard Orbit Co-op Mod

## Purpose

BepInEx/Harmony co-op multiplayer mod for **Graveyard Orbit** (Unity, Mono).
Steam P2P lobbies, up to 4 players, shared host save, host-authoritative enemies,
synced tiles/loot/deaths/radar/weapon FX, and a merged 50,499-cell contract.

- **Repo:** https://github.com/Grokent/graveyard_orbit_mp (branch `main`)
- **License:** MIT (`LICENSE`); BepInEx LGPL-2.1 notices in `THIRD_PARTY_NOTICES.md`
- **Current build tag:** `MP BUILD 12` — bump `BuildTag` in `CoopPlugin.cs` for
  every release; mismatched builds are blocked in the lobby.

## Environment (assume these, don't re-ask)

- Linux (CachyOS), Hyprland; game runs through **Steam/Proton**, not Wine (Wine
  is only for quick load tests and cannot test Steam features).
- Game: `~/.local/share/Steam/steamapps/common/Graveyard Orbit`
  - Steam AppID **4654120**, Unity **6000.5.0f1**, Mono (`MonoBleedingEdge`).
- Launch option required for BepInEx under Proton:
  `WINEDLLOVERRIDES="winhttp=n,b" %command%` (already configured for this user).
- BepInEx 5.4.23.6 installed additively in the game folder.
- Save path: `.../compatdata/4654120/pfx/drive_c/users/steamuser/AppData/LocalLow/AnchorheadGames/Graveyard Orbit/graveyard_orbit_save.json`
  (Windows: `%USERPROFILE%\AppData\LocalLow\AnchorheadGames\Graveyard Orbit\`).

## Toolchain

Everything is project-local; no system installs.

```bash
./setup.sh      # portable .NET 9 SDK -> tools/dotnet, BepInEx refs, optional ilspycmd
./build.sh      # src/GraveyardOrbit.Coop -> bin/Release/GraveyardOrbit.Coop.dll
./deploy.sh     # copies DLL + assets/maps/wrecked_space_rock.json into BepInEx/plugins/GraveyardOrbit.Coop
./package.sh    # dist/GraveyardOrbit-Co-op-0.1.0-win64.zip (BepInEx + plugin + map + licenses)
```

- `decompiled/` = ILSpy output of `Assembly-CSharp.dll`; **gitignored and never
  redistributed**. Regenerate with `tools/ilspycmd`.
- `docs/HOOKS.md` is the hand-maintained hook map (line references may drift).
- Reference assemblies come from the installed game; a game update can break
  hooks — re-run recon and update patch names.

## Layout

```
src/GraveyardOrbit.Coop/
  CoopPlugin.cs            BepInEx entry, BuildTag, spawn-angle preview log
  CoopConfig.cs            config (transport, debug spawn, keys, options)
  CoopSession.cs           session state, roster/slots, protocol handlers, tick
  Net/Protocol.cs          NetMessage DTO + message kinds
  Net/NetTransport.cs      transport abstraction
  Net/SteamTransport.cs    Steam P2P transport + SteamLobby callbacks
  Net/TcpTransport.cs      direct TCP dev transport
  Game/Patches.cs          BeginSortie hook, TryLaunch broadcast
  Game/SpawnFan.cs         15° centred spawn fan
  Game/GameFlow.cs         host START, seed forcing, save capture
  Game/ClientJoin.cs       client save apply, seed force, scene load, win/return
  Game/CombinedContract.cs WRECKED SPACE ROCK runtime merge + map file load
  Game/EnemySync.cs        host-authoritative enemy snapshots/damage + rotor hide
  Game/TileSync.cs         destroyed-tile sync + GameState/radar bookkeeping
  Game/LootSync.cs         resource mote sync
  Game/WeaponFxSync.cs     remote projectile/beam/orb FX
  Game/RemoteShipBuilder.cs build remote ship visuals from loadout
  Game/SlotMath.cs         slot angle math
  UI/MenuPatches.cs        inject MULTIPLAYER menu entry
  UI/MultiplayerLobby.cs   lobby window (plain clickable buttons, not themed)
  UI/RadarPatches.cs       remote player dots
  packaging/Report.*       diagnostics collectors (shipped in zip)
```

## Design rules that must not regress

- **Never modify base game files.** Everything is additive (`winhttp.dll`,
  `doorstop_config.ini`, `BepInEx/`) plus Harmony patches in memory.
- **Spawn fan:** centred, `(slot - (n-1)/2) * 15°`; solo = 0° (vanilla).
- **Host save is the session save.** On START the host forces `level_seed` and
  `anomaly_seed` **after** `StartContract` (NewContract resets `Root`), then
  sends a gzipped/chunked save. Clients write it, force the explicit seeds, and
  load `MiningScene`.
- **Enemies are host-authoritative:** 15 Hz snapshots by hex cell; clients
  suppress `EnemyManager.Update` and forward damage. Also toggle the `rotor`
  GameObject (lightning drones keep firing otherwise).
- **Tiles:** both sides simulate local damage; every destruction is reported.
  Remote apply uses `StripCamouflage` + `GameState.RecordDestroyed` + radar
  `OnTileDestroyed`. No loot/echo on remote apply.
- **Returns are independent.** Host return/launch must NOT move clients. Launch
  mirroring was removed deliberately. Only `cleared` (win) is mirrored.
- **Weapon FX:** real VFX prefab when available (visualFx/fxScale), thin line
  fallback; beams from `DrawBeam`; dynamo orbs from `SweeperSystem.TickEmit`;
  chain arcs from `EnemyManager.SpawnArc`; enemy shots from `SpawnShot`.
- **UI style:** menu/lobby use MetaUi/MenuStyles/SlabFrame look. Lobby footer
  buttons are plain clickable VisualElements with dark fill + coloured border
  (Button theme states turn green otherwise).

## Steam gotchas (learned the hard way)

- The game initialises Steam in `SteamBridge`/`SplashBoot`. **Do not call
  `SteamAPI.Init()` yourself** — wait until `SteamBridge.Initialized`.
- Register callbacks early (`TickAutoInit`) so invites aren't missed.
- Accept P2P sessions (`P2PSessionRequest_t` → `AcceptP2PSessionWithUser`).
- Direct friend invites must send `+connect_lobby <lobbyId>` and set rich
  presence `connect`; handle `GameRichPresenceJoinRequested_t` and
  `GameLobbyJoinRequested_t`, plus `+connect_lobby` in launch args.
- Never use `pgrep -f GraveyardOrbit` for restarts: the command line contains
  the path and kills the shell. Use the `/proc/<pid>/comm` filter or kill the
  `steam-runtime-launch-client` PID.

## Testing workflow

```bash
./build.sh && ./deploy.sh
steam -applaunch 4654120
```

- Logs: `BepInEx/LogOutput.log`. Grep `[coop] [steam] [contract] [join] [flow]
  [return] [spawnfan] [enemy] [tile] [fx]`.
- Restart cleanly by killing the Steam launch client, not the shell.
- Screenshots: `spectacle -b -n -o shot.png` (grim lacks screencopy here);
  crop the game window region.
- Testers submit `Report.bat` output; both sides' reports include seed values,
  hashes and log highlights.
- Solo/offline testing: `[Debug] OpenLobbyOnMenu = true` auto-opens the lobby.

## Known limitations

- Tile HP/crack stages are local (destruction itself is synced).
- Host enemies target only the host's ship; clients take no enemy fire yet.
- Loot collection is per-player; only loot spawns are shared.
- Sortie start/launch timing is independent after the initial START handoff.
- Probe/beam visual polish may still need tuning.

## Working conventions

- Read the decompiled source before changing hooks; keep `docs/HOOKS.md` current.
- Wrap every Harmony patch in try/catch; never let a mod error break gameplay.
- Functional changes ship with a `BuildTag` bump, a rebuilt zip, and an updated
  `INSTALL.txt`; commit and push to the repo when asked.
