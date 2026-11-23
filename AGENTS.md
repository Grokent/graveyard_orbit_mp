# AGENTS.md — Graveyard Orbit Co-op Mod

## Purpose

BepInEx/Harmony co-op multiplayer mod for **Graveyard Orbit** (Unity, Mono).
Steam P2P lobbies, up to 4 players, shared host save, host-authoritative enemies,
synced tiles/loot/deaths/radar/weapon FX. No game assets are redistributed.

- **Repo:** https://github.com/Grokent/graveyard_orbit_mp (branch `main`)
- **License:** MIT (`LICENSE`); BepInEx LGPL-2.1 notices in `THIRD_PARTY_NOTICES.md`
- **Current build tag:** `MP BUILD 20` — bump `BuildTag` in `CoopPlugin.cs` for
  every functional release; mismatched builds are blocked in the lobby.

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
  Game/EnemySync.cs        enemy snapshots, host-authority guard, kill sync + dormant removal
  Game/TileSync.cs         destroyed-tile sync + GameState/radar bookkeeping
  Game/LootSync.cs         resource mote sync
  Game/WeaponFxSync.cs     remote projectile/beam/orb FX (binary, pooled, frame-budgeted)
  Game/FxPatches.cs        weapon-fire hooks -> WeaponFxSync (arcs queued/batched)
  Game/SyncPatches.cs      DamageTarget forward + KillEnemy broadcast
  Game/ReturnDiagnostics.cs return logging + StructureCleared mirror
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
- **Kills are mirrored explicitly.** A `KillEnemy` prefix broadcasts
  `EnemyKilled` (reliable, cell-keyed) so peers run the native kill — death FX,
  destroyed visuals, `GameState.RecordDestroyed` — instead of only hiding the
  enemy. Peers suppress `OnBehaviorLoot` during the replay (the killer already
  broadcast the loot spawn) and remove the enemy from `_dormant` if they had
  not revealed it yet. Snapshot omission stays as a fallback: an enemy that was
  live and then vanishes is killed, never-seen ones are just parked/hidden.
- **Dormant-enemy reveals follow destroyed tiles:** remote tile applies call
  `EnemyManager.OnTileDestroyed`, otherwise host/receiver enemy sets diverge.
- **Tiles:** both sides simulate local damage; every destruction is reported.
  Remote apply uses `StripCamouflage` + `GameState.RecordDestroyed` + radar
  `OnTileDestroyed`. No loot/echo on remote apply. Destructions that arrive
  while a player is in the base are recorded in `GameState` and replayed into
  the next sortie's `TileField` (pending set in `TileSync`).
- **Returns are independent.** Host return/launch must NOT move clients. Launch
  mirroring was removed deliberately. Only `cleared` (win) is mirrored.
- **Weapon FX:** every `FxShot` carries the firing `weapon` and its `mods`, so
  peers pick the same visual the shooter sees.
  - Laser/pulse beams: `WeaponSystem.DrawBeam` sends the **real beam prefab**
    (`SciFiBeams/BeamLaserOrange`) plus the mount's actual
    `widthMultiplier` and material colour/impact flag. Never use a fixed line
    width: the shipped remote line looked much thicker than the local beam.
  - Pulse (resonators) is **not** a projectile: `ResonatorWave.Step` sends the
    expanding wave (centre, radius, speed, core/halo widths, trail width, colour
    and cone arc) once per wave, after pulse mods set the style.
  - Dynamo: `SweeperSystem.ShowStream` sends the blue `BeamLaserBlue` stream
    with the sweeper's own width/colour; `SweeperSystem.TickEmit` sends each
    `ProtonMissileBlue` orb (surge-scaled); `SweeperSystem.PlayBlast` sends
    `DomeNukeBlue` nova rings.
  - Cannon/shotgun/probe: `ProjectileSystem.SpawnStraight/Probe` send the real
    `visualFx`, `visualScale` and `impactFx`, read from `SpawnFamily` /
    `SpawnPartUid` so the weapon and its mods travel with the shot.
  - Chain arcs: `EnemyManager.SpawnArc`, thin lightning line.
  - Enemy shots: `EnemyManager.SpawnShot` reuses the host's
    `WeaponFX/LavaMissile` body and applies local hull damage on clients that
    fly into them.
  - Patch classes are applied one at a time in `CoopPlugin.Awake`; a broken
    target logs an error and does not disable the other hooks.
- **UI style:** menu/lobby use MetaUi/MenuStyles/SlabFrame look. Lobby footer
  buttons are plain clickable VisualElements with dark fill + coloured border
  (Button theme states turn green otherwise).
- **Controller navigation:** the base game ships `GraveyardOrbit.UI.PadNav`
  (stick/D-pad spatial nav, focus ring, glyph hints, B-to-close). Any new
  lobby/menu window must join it, not reimplement it: add `pad-modal` to the
  scrim (scopes focus + `SetModalBack`), register painted VisualElement actions
  with `PadNav.Register` + `pad-ring`, mark one `pad-default`, and never
  `Clear()` a list that PadNav is focused in — rebuild rows in place or
  `PadNav.FocusOn` the replacement.
- **Lobby navigation model:** three stops switched with LB/RB (START is
  host-only), polled as `In.PadLBDown`/`In.PadRBDown` on a
  `schedule.Execute(...).Every(0)` like `RecordsPage`, so the D-pad only ever
  moves inside one stop: SESSION (roster on the
  left, friends/invites on the right, plus leave), CONTRACT (map card grid +
  map pane) and START (host-only launch panel). There is no separate crew/session
  info pane — anything worth showing is already on screen. Keep every focusable inside
  its stop's single scroll column — PadNav counts rows scrolled out of view as
  candidates, and a control sitting next to a scrolling list makes up/down
  bounce between them. Map detail is rendered from
  `StructureSilhouette.ThumbLazy`; focus only inspects, A commits.
  B/ESC only *hide* the lobby (the Steam lobby and session keep running, so
  Settings are reachable without dropping the party); the SESSION stop's
  leave/end row is the only teardown path. Do not fold the two together.
- **UI-scaling mod compatibility** (`Graveyard Orbit UI Scaling`, text
  100–200%): rows use `minHeight`, never a fixed height; long strings get
  `Elide()`; detail panes are built once and only their text/colour is updated,
  and font sizes are never reassigned after creation (the scaling mod rebases its
  baseline on any size change, so a rebuild makes text visibly jump). The map
  pane scrolls, including with the right stick.

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

## Fast paths and dead ends (read before debugging)

**Lag/stutter triage.** If one player stutters only while the other fires, the
problem is the *receiving* machine: `Transport.Poll` → `Protocol.TryDecode` →
`WeaponFxSync.Play`. Check the 10 s log line
`[fx] last 10s: sent=N recv=M dropped=K` before profiling anything else. Chain
lightning is the worst case (up to ~36 arcs per frame); it is queued, batched
every 50 ms, and drawn with the native `EnemyManager.SpawnArc`.

**Hot-path rules (each is a regression already fixed once):**
- Never JSON-encode per-shot messages. `FxShot` is binary (magic `0xF1`); a new
  field needs edits in **both** `EncodeFx` and `DecodeFx` in `Net/Protocol.cs` —
  no compiler error if you forget, the packet just desyncs silently.
- Never `Resources.Load`, `Instantiate`, `lr.material` or `Shader.Find` per
  received FX. Use `VfxSpawner.Play` (game-pooled), `WeaponFxSync.RentBeam`,
  `LoadPrefab`, `SharedLineMaterial`.
- Never mutate a shared material (clone/pool it), and never `AddComponent` per
  shot on a pooled object (reuse the component).
- Bound receive work per frame (`MaxFxPerFrame`, `MaxArcDrawPerFrame`) instead of
  trusting send rates.
- Any replay path needs an echo guard (`_replayingArcs`, `_applyingKill`) or it
  loops between peers forever.

**Kills.** Every death funnels through `EnemyManager.KillEnemy` (private) — hook
that, not the damage sites: `DamageTarget` only sees direct hits, while
explosions, `DamageEnemiesInBand/Disc`, anomaly effects and local sims bypass
it. Snapshot omission is only a fallback; `Hide()` is not a kill. Suppress
`OnBehaviorLoot` during a replay (loot duplicates) and drop the matching
`_dormant` entry (otherwise it respawns on the next reveal).

**Game-code facts that save a recon pass:**
- Harmony patches private methods fine, and reflection `Invoke` still runs the
  patch (safe to route our own `KillEnemy`/`DamageTarget` calls through it).
- `_active` indices shift on removal → key enemy messages by hex cell.
- `_dormant` is `List<(Vector2Int, string)>`; the cell is tuple field `Item1`.
- In a patch use `__instance`; `FindAnyObjectByType` can return another object
  and silently break index lookups.
- One `EnemyManager` per sortie; `SpawnArc` is the cheap native lightning line.

**Verify without launching the game.** `dotnet` exists only at `tools/dotnet`
(`build.sh` exports it). For the binary protocol, build a net9 console app that
references `bin/Release/GraveyardOrbit.Coop.dll` plus the game's `Managed/*.dll`,
register
`AppDomain.AssemblyResolve += (s,e) => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, new AssemblyName(e.Name).Name + ".dll"))`,
then round-trip `Protocol.Encode/TryDecode` for every `fx` kind and for
malformed packets. `Update.ps1` cannot run here (no `pwsh`) — review only; test
`Update.sh` with `--game-dir <temp dir>` containing a dummy `GraveyardOrbit.exe`.

## Known limitations

- Tile HP/crack stages are local (destruction itself is synced).
- Host enemies target the host's ship; each client's local enemy simulation
  targets that client's ship, so all players are engaged.
- Enemy HP/position stay host-authoritative; attack cooldowns and swarm units
  are simulated per player, so swarm instances are not bit-identical between
  machines.
- Loot collection is per-player; only loot spawns are shared.
- Sortie start/launch timing is independent after the initial START handoff.
- Pulse waves are recreated from their parameters (core/halo/arc); the
  particle trail/sparks are approximated on peers.

## Working conventions

- Read the decompiled source before changing hooks; keep `docs/HOOKS.md` current.
- Wrap every Harmony patch in try/catch; never let a mod error break gameplay.
- Functional changes ship with a `BuildTag` bump, a rebuilt zip, and an updated
  `INSTALL.txt`; commit and push to the repo when asked.
- Release sequence: `./build.sh && ./deploy.sh && ./package.sh`, commit,
  `git push`, then `git tag -a MP<N> -m "..." && git push origin MP<N>` and
  `gh release create MP<N> --title "Graveyard Orbit Co-op — MP BUILD <N>"
  --notes-file <file> --latest release/<zip>`. `gh release create --target
  <sha>` fails ("tag_name is not a valid tag") — create the tag first.
- Packaging-only change: `gh release upload MP<N> <zip> --clobber` (no build
  bump). `package.sh` writes `VERSION.txt` and auto-syncs the build number in
  the bundled `INSTALL.txt`, and ships `Update.bat/ps1/sh`.
