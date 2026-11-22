# HOOKS — Graveyard Orbit co-op mod

Derived from ILSpy decompilation of `Assembly-CSharp.dll` (Unity 6000.5.0f1, Mono).
Build GUID `68d3fa3f71724f0e824dc537725bc77f`. Line numbers refer to `decompiled/Assembly-CSharp`.

## Mode flow (GameRoot)

```
State.Planet → State.Build → State.Process → State.TakingOff → State.Sortie → State.Landing
```
- `GameRoot` (private enum `State`, field `_state`) — `GraveyardOrbit/GameRoot.cs:16`.
- Launch trigger: `GameRoot.TryLaunch()` (private), wired at `GameRoot.cs:544` (`_planetHud.OnLaunch`) and `:663` (`_baseLaunch.OnLaunch`).
- `TryLaunch()` sets `State.TakingOff`, starts the pad-ship cinematic (`_takeoffPhase` 0→1→2, `GameRoot.cs:2021-2074`).
- At the end of the cinematic: `_mining.BeginSortie()` (`GameRoot.cs:2076`).

## Ship spawn / the single launch line (THE overlap source)

`MiningSortie.BeginSortie()` (`GraveyardOrbit/MiningSortie.cs:1989`):
- calls `ReanchorPlanet()` which computes:
  ```
  _planetCenter = new Vector3(0, 0, num2);
  _launchStart  = new Vector3(0, 1, num3 - R * 0.45);   // num3 = num2 + R
  _spawnPos     = new Vector3(0, 1, num3 + hexSize*4);
  ```
  (`MiningSortie.cs:1971-1982`)
- sets `ship.transform.position = _launchStart` and `ship.ResetOrientation(0f)` (`:2052-2054`), `_camTarget = _spawnPos` (`:2060`), `_phase = Phase.Intro`.

`UpdatePhase` Intro lerps the ship `_launchStart → _spawnPos` over 1.1 s (`:3703-3722`), then `Phase.Play` enables control.

**Result:** every player spawns on the same +Z radial line at the same point. **Hook: postfix `BeginSortie`** and rotate `_launchStart`, `_spawnPos`, `_camTarget` around `_planetCenter` by the slot angle, then `ship.transform.position = launchStart` and `ship.ResetOrientation(angleDeg)` (yaw 0 = +Z, so the fan is radial and consistent).

`_portalPos` is derived later in `OpenJumpPortal()` from `_spawnPos` (`:3156-3167`), so it follows automatically.
`BeginReturnHome()` computes `_exitToCenter = _planetCenter - _exitStart`, also angle-agnostic.

## Ship API

`ShipController` (`GraveyardOrbit/ShipController.cs`):
- `ResetOrientation(float yawDeg)` — sets `_heading`, zeroes bank, sets rotation (`:369`).
- `TeleportTo(Vector3 world)` — sets position, zeroes velocity, clears trails/particles.
- `ControlEnabled` (`:133`) — when false, `Update` early-returns (`:1-40`).
- `AwaitMouseAbove` (`:136`), `AlignHeading`/`AlignTurnRate` (`:227-229`).

`MiningSortie` accessors: `public ShipController Ship` (`:619`), `public Transform ShipTransform` (`:605`).

## Ship build (visuals)

`MiningSortie.BuildShip()` (`:1784`): destroys `_shipGo`, creates `new GameObject("Ship")` with `WeaponSystem`, `SweeperSystem`, `ShipController`, `ProjectileSystem`, `ResourceSystem`, `DroneFleet` etc. Visual mesh is produced by `ShipController.Init(...)`.

**Remote ships (v1):** clone the finished `_shipGo` hierarchy after `BuildShip`, destroy all `MonoBehaviour`s + colliders on the clone, keep renderers/trails, drive `transform` from snapshots. Later: rebuild from synced `AccretionShip` loadout.

## Loadout / save data

`AccretionShip` (`GraveyardOrbit/AccretionShip.cs`): fully public serializable model — `List<PlacedPart> placed`, each `PlacedPart { uid, type, cells: List<Vector2Int>, stats, level, paid, tier, mods, powerups }`. `GameState.I.Ship` (`GameState.cs:275`). JSON via bundled `Newtonsoft.Json.dll`.

## World determinism

`MiningSortie._data` (`MegastructureData`) is authored data passed at `Init` (`:967`); no run-seed randomization except optional sandbox seeding. Host and clients loading the same structure see the same layout.

## Candidates for later phases

- `GameRoot.TryLaunch` prefix → host broadcast "launch" + client auto-launch (reflection invoke).
- `EnemyManager`, `ProjectileSystem`, `ResourceSystem` — host-authoritative events.
- `MiningSortie.BeginReturnHome` / `JumpGateSequence` / `TeleportOutSequence` — extraction sync.
- `UI.ModeSelectController` — co-op menu entry.

## Menu / lobby hooks (added for the multiplayer UI)

- `MainMenuController.BuildUI(PanelSettings, VisualTreeAsset, Sprite, Sprite, string)` — private; Harmony postfix injects the MULTIPLAYER button before `NewGameBtn` (CONTRACTS) and registers it in the controller's private `_items` list so hover/selection styling behaves like a native entry. Button inherits UXML classes/inline size from `SettingsBtn`.
- `MainMenuController.SelectItem(Button)` — private; invoked on hover for the injected entry.
- `MainMenuController.Root` — public accessor used for the modal host.
- `SettingsModal` / `MetaUi` / `MenuStyles` / `SlabFrame` — public styling building blocks reused for the lobby (same font, palette, notched borders).
- `SteamBridge.EnsureInit()` / `SteamBridge.Initialized` — the game's Steam init + callback pump; the mod reuses it.
- `StructureCatalog.I.Contracts` → `StructureDef { id, DisplayName, DisplayDescription, cellCount, startingMoney }` — inline contract list.
- `MainMenuBoot.StartContract(StructureDef, bool)` — private; host START reflects into it after `SaveSlots.Switch(SaveSlot.Custom)`.
- Steam: `SteamMatchmaking.CreateLobby(FriendsOnly, 4)`, `SetLobbyJoinable`, `Callback<LobbyCreated_t>`, `Callback<LobbyEnter_t>`, `Callback<GameLobbyJoinRequested_t>`; `SteamFriends.InviteUserToGame`, `ActivateGameOverlayInviteDialog`, friends list + medium avatars.
