# DEBUG (InfiniteUpgradeSystem)

## 2026-07-12: Phase 0-1 -- CardOperationHelper 解耦 + 点数系统

- **改动**：
  - 新建 `CardOperationHelper.cs`：抽取升级逻辑为独立工具类（SelectCardFromDeck, PerformInfiniteUpgrade, ResetUpgradeLevel, ShowUpgradeVfx）
  - 新建 `RunStateHook.cs`：Harmony patch CombatManager.EndCombatInternal 发放点数 + RunManager.Launch 重置点数
  - UpgradeUIHandler 精简了 ~80 行，移除内联的升级方法和重复 import
- **已知限制（技术债务）**：
  - 点数使用 static 变量存储。存档读档后会丢失（新局 RunStarted 事件会重置为 5）。后续需接入 BaseLib 或游戏内置 SaveManager 持久化
  - 升级等级重置使用反射访问 `_currentUpgradeLevel`（CardOperationHelper.ResetUpgradeLevel），字段名可能随游戏更新变化
- **验证结果**：编译 0 Error 0 Warning，部署成功

## YYYY-MM-DD: Initial setup

- **Phenomenon**: N/A (first record)
- **Cause**: N/A
- **Fix**: N/A
- **Result**: Build passes, deployed to mods.

## 2026-07-12: 修复 Trap 1 — UI 遮罩层阻塞卡牌选择界面

- **Phenomenon**: 在无限升级UI中点击升级按钮后，游戏原生卡牌选择界面（NDeckCardSelectScreen）被弹出，但鼠标点击卡牌无响应，无法完成选择。
- **Cause**: 自定义 UI 的 `ColorRect` 背景遮罩设置了 `MouseFilter.Stop`，该遮罩层始终存在于场景树中。当 `CardSelectCmd.FromDeckGeneric` 唤起原生卡牌选择界面时，该界面作为覆盖层（Overlay）被推入 `NOverlayStack`，但由于我们的遮罩层也处于事件捕获路径中，拦截了鼠标事件，导致玩家无法与卡牌交互。
- **Fix**: 在 `OnUpgradeClicked()` 中调用 `CardSelectCmd.FromDeckGeneric` 之前，先调用 `SetUIVisible(false)` 隐藏整个UI（包括背景遮罩）。选择完成后根据结果恢复UI或关闭UI。
- **Result**: 卡牌选择界面可以正常响应鼠标点击，升级流程完整可用。

## 2026-07-12: 使用自定义本地化文本作为卡牌选择提示

- **Phenomenon**: 卡牌选择界面的提示文本使用了原版内置的 `CardSelectorPrefs.UpgradeSelectionPrompt`（"选择一张牌升级"），模组自己的本地化 key 未被使用。
- **Cause**: 代码直接使用了 `CardSelectorPrefs.UpgradeSelectionPrompt`（`new LocString("card_selection", "TO_UPGRADE")`），没有使用模组自定义的 `cards.json` 中的 key。
- **Fix**: 将 `CardSelectorPrefs` 的 `Prompt` 参数改为 `new LocString("cards", "INFINITEUPGRADESYSTEM-UPGRADE_SELECT_PROMPT")`，同时在 `zhs/cards.json` 和 `eng/cards.json` 中添加对应的文本 key。
- **Result**: 卡牌选择界面显示模组自定义的提示文本，本地化文件正确生效。

## Known issues

- `DebugOnlyGetState()` is used to access the run state. This may change in future BaseLib versions.
- Card upgrade level reset uses reflection on `_currentUpgradeLevel`. The field name could change between game versions.
- The UI attaches to `SceneTree.Root` — on scene transitions, the node persists, which may cause layer-ordering issues.
- `UpgradePointManager` uses static variables (临时方案) for point storage. Points reset on game restart but NOT on new run. Will be replaced with proper `RunSavedData` in a future step. (参见常见陷阱3：全局静态变量存储本局数据)
