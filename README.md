# Graveyard Orbit Co-op Mod

Cooperative multiplayer mod for **Graveyard Orbit** (Steam AppID 4654120).
BepInEx + Harmony plugin, Steam P2P lobbies, up to 4 players.

## Status

| Phase | State |
|---|---|
| 0 — Recon / hook map | ✅ `docs/HOOKS.md` |
| 1 — BepInEx scaffolding | ✅ plugin builds & deploys |
| 2 — Steam lobby + transport | ✅ lobby, invites, friends list, roster, P2P |
| 3 — Co-op sortie + spawn fan | 🟡 exit-angle fan implemented; remote ship puppets v1; loadout sync stubbed |
| 4 — World sync (enemies/projectiles) | 🟡 host-authoritative enemies; all weapon FX mirrored; tile destruction, loot, deaths, radar, ships synced |
| 5 — Lobby / menu UI | ✅ MULTIPLAYER entry, lobby window, contract picker, invites |
| 6 — Shared host save + auto-launch | ✅ host save is compressed, chunked and applied by clients on START |

## Multiplayer lobby

`PLAY → MULTIPLAYER → CONTRACTS → SETTINGS` on the title screen. Created by
`UI/MenuPatches.cs` (Harmony postfix on `MainMenuController.BuildUI`), reusing the
menu's own classes, font and `SlabFrame` borders.

The lobby (`UI/MultiplayerLobby.cs`) contains:

- **CREW** — 4 slots, host tag, avatar, you-marker.
- **FRIENDS** — live Steam friends list (avatars, persona state, in-game flag),
  per-friend INVITE (direct Steam invite), plus **INVITE OTHERS** (Steam overlay).
- **CONTRACT** — inline list from `StructureCatalog.I.Contracts`; the host's pick
  broadcasts live to every client.
- **START** — host applies the contract and loads gameplay; clients wait for the
  host-save transfer (next phase).

Opening the window hosts a friends-only Steam lobby (max 4). Accepting an invite
joins automatically and opens the lobby at the menu.

## The spawn fix

Vanilla `MiningSortie.BeginSortie()` launches every ship along the same +Z radial
line (`_launchStart` / `_spawnPos` both have `x = 0`). The mod's postfix rotates
both points around `_planetCenter` per player slot, and sets the same angle as the
ship's initial heading:

```
angle(slot, n) = (slot - (n-1)/2) * 15°
n=4 → -22.5°, -7.5°, +7.5°, +22.5°
```

A solo player keeps angle 0 — vanilla behaviour is byte-identical.

## Combined contract: WRECKED SPACE ROCK

`Game/CombinedContract.cs` merges the **Wrecked Destroyer** (`destroyer_25k`) and
**Space Rock** (`vein_rock`) hex fields at runtime and injects the result into the
in-memory `StructureCatalog` — no game assets are modified.

- **50,499 cells** in one sortie
- the two shapes hug at the closest legal contact: **2 empty hex cells**
  between them (minimum hex distance 3)
- the whole field is **centred on the planet's x axis** (spawn line), so the
  planet and launch path sit in the middle instead of off to one side
- **200 extra tier-1/tier-2 cells (black/gold)** in five small asteroid clumps
  between the map's north edge and the planet, as a location marker — styled
  after the destroyer's own debris clumps
- rooms and green shards from both maps are shifted and re-numbered
- appears **first** in the contract list (lobby and the game's contract screen)
- validated through the game's own `MegastructureData.FromJson` on creation

Bands are inherited from the destroyer's definitions (both maps use layers 1–5);
material/enemy seeding is therefore destroyer-flavoured across the merged field.

## Setup (fresh clone)

```bash
./setup.sh          # portable .NET SDK + BepInEx compile references
./build.sh
./deploy.sh
```

`setup.sh` installs everything project-locally (`tools/`); nothing is installed
system-wide. Set `GAME_DIR` if the game is not in the default Steam library.

## Build & install

```bash
./build.sh          # builds with tools/dotnet (project-local SDK)
./deploy.sh         # installs BepInEx additively + copies plugin
```

`deploy.sh` never replaces an existing game file: BepInEx adds `winhttp.dll`,
`doorstop_config.ini` and `BepInEx/`; the mod itself is one DLL.

Steam → Graveyard Orbit → Properties → Launch Options (already configured on this machine):

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

First run generates `BepInEx/config/agentbox.graveyardorbit.coop.cfg`.

## Using it (dev build)

- **F8** — host (Steam: creates a friends-only lobby and opens the invite dialog)
- **F9** — join (Steam: opens the friends overlay / joins `SteamLobbyId` if set)
- **F10** — leave session

Dev/testing without Steam (two instances on one machine): set
`UseSteam = false` in the config, F8 on one instance, F9 on the other
(`127.0.0.1:27015`).

Offline angle preview without a session: set `DebugSlot` (0..3) and
`DebugPlayerCount` (e.g. 4) to watch the exit fan in single-player.

## Layout

```
src/GraveyardOrbit.Coop/     plugin source
docs/HOOKS.md                decompiled hook map
decompiled/                  ILSpy output (gitignored, never redistributed)
tools/                       project-local dotnet SDK, ilspycmd, BepInEx zip
```

## Legal

Distribute only this mod's DLL. Never redistribute decompiled game code or game
assemblies. Modding a purchased single-player game for private co-op play.


## Repository

https://github.com/Grokent/graveyard_orbit_mp

## License

The mod's own code is MIT licensed (see `LICENSE`). The redistributable ZIP
bundles BepInEx, which is LGPL-2.1; see `THIRD_PARTY_NOTICES.md` and
`licenses/LGPL-2.1.txt`. This is an unofficial fan mod; Graveyard Orbit and its
assets belong to Anchorhead Games.
