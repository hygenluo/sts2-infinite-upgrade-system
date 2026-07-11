# DEBUG (InfiniteUpgradeSystem)

## YYYY-MM-DD: Initial setup

- **Phenomenon**: N/A (first record)
- **Cause**: N/A
- **Fix**: N/A
- **Result**: Build passes, deployed to mods.

## Known issues

- `DebugOnlyGetState()` is used to access the run state. This may change in future BaseLib versions.
- Card upgrade level reset uses reflection on `_currentUpgradeLevel`. The field name could change between game versions.
- The UI attaches to `SceneTree.Root` — on scene transitions, the node persists, which may cause layer-ordering issues.
