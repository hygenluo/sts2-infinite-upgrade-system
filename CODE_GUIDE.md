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
   c. **Hides UI** (`SetUIVisible(false)`) to prevent backdrop from blocking card selection (Trap 1 fix)
   d. Opens deck grid via `CardSelectCmd.FromDeckGeneric(...)` with custom LocString
   e. If player cancels: refunds points, restores UI
   f. Calls `PerformInfiniteUpgrade(card)`:
      - `card.UpgradeInternal()` — applies OnUpgrade() effects
      - `card.FinalizeUpgradeInternal()` — finalizes dynamic vars
      - Resets upgrade level via reflection for infinite upgrades
      - Updates NCard visuals
      - Plays upgrade VFX
   g. Closes UI

### Key Design Decisions

#### Trap 1 Fix: UI Backdrop Blocking
`OnUpgradeClicked()` hides the custom UI (`SetUIVisible(false)`) before calling `CardSelectCmd.FromDeckGeneric`. Otherwise, the `ColorRect` background with `MouseFilter.Stop` intercepts mouse events, making the card selection screen unclickable. After selection completes, the UI is either restored (on cancel) or closed (on success).

#### Localization Strategy
- Uses the `cards` table (an existing base-game table) for all mod text
- `CardSelectorPrefs` prompt uses `new LocString("cards", "INFINITEUPGRADESYSTEM-UPGRADE_SELECT_PROMPT")`
- Maintains both `zhs/cards.json` and `eng/cards.json`

### Key Game APIs Used

- `new LocString("cards", "...")` — Custom localization from cards.json
- `CardSelectCmd.FromDeckGeneric(player, prefs)` — Opens deck selection grid
- `CardModel.UpgradeInternal()` + `FinalizeUpgradeInternal()` — Standard upgrade pipeline
- `NCardUpgradeVfx.Create(card)` — Upgrade visual effect
- `LocalContext.GetMe(IPlayerCollection)` — Get local player from run state
- `RunManager.Instance.DebugOnlyGetState()` — Access current run state

## Known Technical Debt

| Item | Description | Planned Fix |
|------|-------------|-------------|
| Static point storage | `UpgradePointManager.CurrentPoints` is `public static` — not reset on new run | Replace with BaseLib `RunSavedData` |
| Reflection for upgrade level | `ResetUpgradeLevel()` uses reflection on `_currentUpgradeLevel` | Find public API or use Publicizer |
| UI on SceneTree.Root | Node persists across scene transitions | Listen for scene change events |

## Extending

To add new operations:
1. Add a new button in `BuildUI()` in `UpgradeUIHandler.cs`
2. Create a handler method following the `OnUpgradeClicked()` pattern
3. Define point costs as constants
4. Add localized text in `zhs/cards.json` and `eng/cards.json`
