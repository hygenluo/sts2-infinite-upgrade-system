# Code Reading Guide (InfiniteUpgradeSystem)

## Architecture Overview

```
Entry.cs                  -- Mod entry point (Harmony + Godot script registration)
UpgradePointManager.cs    -- Static point storage and transaction logic
UpgradeUIHandler.cs       -- Godot Control node: UI creation, hotkey, card upgrade flow
```

No Godot `.tscn` files needed — the entire UI is built programmatically in C#.

## File Navigation

| File | Purpose | Key Types/Methods |
|------|---------|-------------------|
| `Scripts/Entry.cs` | Mod initialization | `Entry.Init()` — registers Harmony, scans scripts, creates UI |
| `Scripts/UpgradePointManager.cs` | Point management | `CurrentPoints`, `TrySpendPoints()`, `AddPoints()` |
| `Scripts/UpgradeUIHandler.cs` | UI + upgrade logic | `CreateInstance()`, `OnUpgradeClicked()`, `PerformInfiniteUpgrade()` |

## Core Flow

### Startup
1. Game loads mod DLL
2. `Entry.Init()` is called
3. Harmony patches registered
4. `UpgradeUIHandler.CreateInstance()` — creates the Control and adds it to `SceneTree.Root`

### Upgrade Flow
1. Player presses **P** outside combat
2. `_Input` detects the key, calls `ShowUI()`
3. UI displays current points and operations
4. Player clicks "upgrade" button
5. `OnUpgradeClicked()`:
   a. Checks/spends 5 points (`TrySpendPoints`)
   b. Gets local player via `LocalContext.GetMe(RunState)`
   c. Opens deck grid via `CardSelectCmd.FromDeckGeneric(...)`
   d. Calls `PerformInfiniteUpgrade(card)`
   e. Resets upgrade level via reflection for infinite upgrades
   f. Plays upgrade VFX

### Key Game APIs Used

- `CardSelectorPrefs.UpgradeSelectionPrompt` — Built-in "select card to upgrade" prompt
- `CardSelectCmd.FromDeckGeneric(player, prefs)` — Opens deck selection grid
- `CardModel.UpgradeInternal()` + `FinalizeUpgradeInternal()` — Standard upgrade pipeline
- `NCardUpgradeVfx.Create(card)` — Upgrade visual effect
- `LocalContext.GetMe(IPlayerCollection)` — Get local player from run state
- `RunManager.Instance.DebugOnlyGetState()` — Access current run state

## Extending

To add new operations:
1. Add a new button in `BuildUI()` in `UpgradeUIHandler.cs`
2. Create a handler method following the `OnUpgradeClicked()` pattern
3. Define point costs as constants
