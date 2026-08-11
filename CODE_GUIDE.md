# 代码阅读指南 (InfiniteUpgradeSystem)

## 项目概览

```
Scripts/
├── Entry.cs                      # 模组入口 (ModInitializer + Harmony)
├── UpgradePointManager.cs        # 点数管理 (Run seed 持久化)
├── UpgradeUIHandler.cs           # Godot UI v2：视图切换/折叠分区/搜索/动效
├── CardOperationHelper.cs        # 卡牌操作引擎（promptKey 透传 + Esc 取消链路）
├── CardUpgradeTracker.cs         # 卡牌修改追踪 + 读档恢复
├── AbilityOperationHelper.cs     # 能力操作引擎 (11项 + Power 应用)
├── RunStateHook.cs               # 游戏生命周期 + 检查点持久化
├── PointsPersistence.cs          # 点数 JSON 持久化
├── UpgradeDataStore.cs           # 每玩家数据（RitsuLib PlayerRunSavedData，store 权威）
├── Skills/
│   ├── SkillRegistry.cs          # 技能注册表（等级化 + 计数 + skills_<seed>.json 持久化）
│   ├── SkillCombatPatches.cs     # 打出牌触发类 9 项（AfterCardPlayed/AfterHandEmptied）
│   └── SkillEventPatches.cs      # 事件触发类 7 项（消耗/弃牌/受伤/中毒/回合/休息处）
├── UiComponents/
│   ├── UpgradeItemData.cs        # 数据模型 UpgradeItemDef（Kind/MaxLevel/等级化）
│   ├── UpgradeLoc.cs             # 自包含双语本地化 + key 常量
│   ├── UpgradeTheme.cs           # 主题：浅灰色板 + 思源宋体 MSDF（DLL 同目录加载）
│   ├── UpgradeItemRow.cs         # 条目行（名称/值/成本/加号 + MAX 徽标 + 抖动/悬停）
│   ├── CollapsibleSection.cs     # 折叠分区（可见性切换 + 淡入 + 箭头旋转）
│   ├── ClassTabBar.cs            # 技能 6 职业标签页
│   └── UpgradeTopBar.cs          # 顶栏（标题/点数/关闭）
├── Multiplayer/
│   └── UpgradePurchaseAction.cs  # 购买同步 action（INetAction+GameAction，两端执行同一购买）
└── Patches/
    ├── EnergyKeepPatch.cs        # Harmony Postfix: 能量跨回合保留
    └── LocStringPromptPatch.cs   # Harmony Postfix: 模组 key 本地化解析（专属选牌提示）
```

## 文件导航

| 文件 | 作用 | 关键方法 |
|------|------|---------|
| `Entry.cs` | 模组初始化 | `Init()` — Harmony.PatchAll + 订阅事件 + 创建 UI |
| `UpgradePointManager.cs` | 点数内存管理 | `AddPoints()`, `TrySpendPoints()`, `SaveCheckpoint()` |
| `PointsPersistence.cs` | 点数 JSON 读写 | `SavePoints()`, `LoadPoints(seed)` |
| `RunStateHook.cs` | 生命周期控制 | `Subscribe()` — 订阅 CombatSetUp/CombatWon/RunStarted |
| `UpgradeUIHandler.cs` | UI 面板 | `BuildUI()`, `PopulateItems()`, 搜索/拖拽/29按钮 |
| `CardOperationHelper.cs` | 卡牌操作 | `PerformInfiniteUpgrade()`, `ModifyDamage()`, `ToggleKeyword()` 等 |
| `CardUpgradeTracker.cs` | 修改追踪 | `RecordModification()`, `ReapplyAll()`, 按 cardIdentity (TemplateId__实例序号) 追踪 |
| `AbilityOperationHelper.cs` | 能力操作 | `TryPurchase(player,key,cost)`, `ApplyInitialBoosts(state)`, `ApplyPower()`（走 PowerCmd.Apply） |
| `UpgradeDataStore.cs` | 每玩家数据（RitsuLib） | `Register()`, `For(player)`, `Mutate()`, `SyncOnRunStarted()` |
| `Multiplayer/UpgradePurchaseAction.cs` | 购买同步 action | `UpgradePurchaseFlow.EnqueuePurchase()`, `UpgradePurchaseAction.ExecuteAction()` |
| `Patches/EnergyKeepPatch.cs` | 能量保留 | Harmony Postfix on `Hook.ShouldPlayerResetEnergy` → 否决能量重置 |

## 核心流程

### 启动流程
1. 游戏加载 DLL → `Entry.Init()`
2. `Harmony.PatchAll` + `ScriptManagerBridge.LookupScriptsInAssembly`
3. `RunStateHook.Subscribe()` — 订阅 CombatSetUp/CombatWon/RunStarted
4. `UpgradeUIHandler.CreateInstance()` — 创建 UI Control 添加到 SceneTree

### 新局流程
1. `RunStarted` 触发 → `seed = runState.Rng.StringSeed`
2. `PointsPersistence.LoadPoints(seed)` — 文件不存在返回 5
3. `CardUpgradeTracker.Load(seed)` + `AbilityOperationHelper.Load(seed)`
4. `CardUpgradeTracker.ReapplyAll(player)` — 恢复卡牌修改

### 读档流程
1. 同上，但 seed 相同 → 文件存在 → 从 JSON 恢复点数和修改

### 检查点持久化
- `CombatSetUp` → 保存 points + card_upgrades + abilities
- `CombatWon` → 同上（含奖励点数）
- 修改只在内存中进行，检查点才写盘

### 卡牌修改流程
1. 玩家在 UI 选择操作 → `CardOperationHelper.XXX()` 
2. 扣点 → 选牌 → 修改 `CardModel` 属性
3. `CardUpgradeTracker.RecordModification()` 记录修改
4. 读档时 `ReapplyAll()` 按顺序重放所有修改

## 关键 API 说明

### 卡牌修改
- `card.DynamicVars.Damage.BaseValue += 1m` — 攻击+1
- `card.AddKeyword(CardKeyword.Innate)` — 添加固有
- `card.EnergyCost.SetCustomBaseCost(n)` — 修改费用
- `card.UpgradeInternal()` + `FinalizeUpgradeInternal()` — 升级

### 能力应用
- `ModelDb.Power<T>().MutableClone().ApplyInternal(creature, amount)` — 通过反射应用 Power

### 持久化
- 所有持久化文件存储在 `mods/InfiniteUpgradeSystem/runs/` 下
- 按 `RunState.Rng.StringSeed` 区分不同 Run

## 已知技术债务
1. 能力 Power 应用使用 Reflection（`ApplyInternal`），依赖内部 API
2. 卡牌修改通过 `CardUpgradeTracker` 追踪，同名卡牌可能互相影响
3. Phase 4（卡牌额外效果）未实现，需要 `CustomEnchantmentModel` + PCK
