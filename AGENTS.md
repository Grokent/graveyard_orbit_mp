# AGENTS.md — Graveyard Orbit Co-op Mod

## Purpose

BepInEx/Harmony co-op multiplayer mod for **Graveyard Orbit** (Unity, Mono).
Steam P2P lobbies, up to 4 players, shared host save, host-authoritative enemies,
synced tiles/loot/deaths/radar/weapon FX, and a runtime-generated merged
contract (no game assets are redistributed).

- **Repo:** https://github.com/Grokent/graveyard_orbit_mp (branch `main`)
- **License:** MIT (`LICENSE`); BepInEx LGPL-2.1 notices in `THIRD_PARTY_NOTICES.md`
- **Current build tag:** `MP BUILD 14` — bump `BuildTag` in `CoopPlugin.cs` for
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
./deploy.sh     # copies the built DLL into BepInEx/plugins/GraveyardOrbit.Coop
./package.sh    # release/GraveyardOrbit-Co-op-0.1.0-MP<build>.zip (BepInEx + plugin + licenses)
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
  Game/CombinedContract.cs WRECKED SPACE ROCK runtime merge (map generated locally)
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
  release/                 local package output (gitignored; zips are release assets)
```

## Design rules that must not regress

- **Never modify base game files.** Everything is additive (`winhttp.dll`,
  `doorstop_config.ini`, `BepInEx/`) plus Harmony patches in memory.
- **Spawn fan:** centred, `(slot - (n-1)/2) * 15°`; solo = 0° (vanilla).
- **Host save is the session save.** On START the host forces `level_seed` and
  `anomaly_seed` **after** `StartContract` (NewContract resets `Root`), then
  sends a gzipped/chunked save. Clients write it, force the explicit seeds, and
  load `MiningScene`.
- **Enemy HP is host-authoritative, enemy attacks are local:** the host sends
  15 Hz snapshots by hex cell; clients apply them (and `rotor` toggles) and
  forward damage. Do **not** suppress `EnemyManager.Update` on clients — each
  player's own manager runs the attack/swarm simulation against their own ship,
  so turrets, spiral shots and attack swarms engage every player equally. A
  `DoAttack` prefix blocks attacks from enemies the host does not have live
  while snapshots are fresh (`EnemySync.HostAuthority`, 3 s timeout). Without
  fresh snapshots (e.g. host in base) clients fall back to fully local enemy
  damage.
- **Dormant-enemy reveals follow destroyed tiles:** remote tile applies call
  `EnemyManager.OnTileDestroyed`, otherwise host/receiver enemy sets diverge.
- **Tiles:** both sides simulate local damage; every destruction is reported.
  Remote apply uses `StripCamouflage` + `GameState.RecordDestroyed` + radar
  `OnTileDestroyed`. No loot/echo on remote apply. Destructions that arrive
  while a player is in the base are recorded in `GameState` and replayed into
  the next sortie's `TileField` (pending set in `TileSync`).
- **Returns are independent.** Host return/launch must NOT move clients. Launch
  mirroring was removed deliberately. Only `cleared` (win) is mirrored.
- **Weapon FX:** real VFX prefab when available (visualFx/fxScale), thin line
  fallback; beams from `DrawBeam`; dynamo orbs from `SweeperSystem.TickEmit`;
  chain arcs from `EnemyManager.SpawnArc`; enemy shots from `SpawnShot`, which
  reuse the host's `WeaponFX/LavaMissile` body and apply local hull damage on
  clients that fly into them.
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
- Host enemies target the host's ship; each client's local enemy simulation
  targets that client's ship, so all players are engaged.
- Enemy HP/position stay host-authoritative; attack cooldowns and swarm units
  are simulated per player, so swarm instances are not bit-identical between
  machines.
- Loot collection is per-player; only loot spawns are shared.
- Sortie start/launch timing is independent after the initial START handoff.
- Probe/beam visual polish may still need tuning.

## Working conventions

- Read the decompiled source before changing hooks; keep `docs/HOOKS.md` current.
- Wrap every Harmony patch in try/catch; never let a mod error break gameplay.
- Functional changes ship with a `BuildTag` bump, a rebuilt zip, and an updated
  `INSTALL.txt`; commit and push to the repo when asked.
