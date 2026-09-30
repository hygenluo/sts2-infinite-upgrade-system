# 代码阅读指南 (InfiniteUpgradeSystem)

> 配套文档：[点数系统.md](点数系统.md)（点数逻辑清单）、[UI设计.md](UI设计.md)（UI 契约）、
> [DEBUG.md](DEBUG.md)（每个坑的原因与修复）。代码注释里凡引用「DEBUG.md xxx」处，都能在那边找到完整排查过程。

## 项目概览

```
Scripts/
├── Entry.cs                      # 模组入口 (ModInitializer + Harmony + UI 创建)
├── UpgradePointManager.cs        # 点数：本地缓存 + 扣点/加点（写 store）
├── PointsPersistence.cs          # 点数 JSON 镜像（points_<seed>.json）
├── UpgradeDataStore.cs           # ★ 每玩家数据权威层（RitsuLib PlayerRunSavedData，按 NetId 分槽）
├── UpgradeUIHandler.cs           # ★ UI 主控制器：视图状态机/分区/搜索/拖拽/动效/条目注册表
├── CardOperationHelper.cs        # ★ 卡牌操作引擎（选牌 + CardMod/AddCard action 执行 + 逆操作回退）
├── CardUpgradeTracker.cs         # ★ 卡牌修改追踪（按 NetId 分桶 + 读档重放 + 回退记录读写）
├── CardModCosts.cs               # 卡牌操作成本表（购买与回退的唯一来源）
├── UpgradeRefundService.cs       # ★ 回退服务（返还 floor(0.8×已付) / 流水项 / 同步执行）
├── AbilityOperationHelper.cs     # 能力操作引擎（Power 应用/初始 boost/即时生效与回退）
├── RunStateHook.cs               # 游戏生命周期订阅 + 检查点持久化 + 房间/战斗点数发放
├── CompositeEnchantment.cs       # 合成附魔（多附魔共存）+ 2 个克隆/文本补丁
├── SavePaths.cs                  # 存档路径（user://mod_data/...）+ 旧档迁移
├── Skills/
│   ├── SkillRegistry.cs          # 技能注册表（等级化 + skills_<seed>.json）
│   ├── SkillEffectsPower.cs      # ★ 技能/能力效果统一承载（隐藏 Power，hook 在游戏管线内 await）
│   └── SkillEventPatches.cs      # 仅休息处任意选项
├── Multiplayer/
│   └── UpgradePurchaseAction.cs  # ★ 同步 action：INetAction + GameAction + 入队入口
├── Patches/
│   ├── EnergyKeepPatch.cs        # 能量跨回合保留
│   ├── LocStringPromptPatch.cs   # 模组 key 本地化解析（选牌提示 + 合成附魔文本）
│   ├── NCardEnchantMarkersPatch.cs # 卡面多附魔标记
│   └── DivergenceDiagnostics.cs  # checksum 分歧时的状态转储
└── UiComponents/
    ├── UpgradeTheme.cs           # ★ UI v3 主题：游戏色板 + MegaLabel/样式框工厂 + 游戏字体解析
    ├── UiIcons.cs                # 矢量图标（三角/拖拽点/菱形/叉号/箭头/减号）
    ├── UpgradeItemData.cs        # UpgradeItemDef 数据模型（Kind/MaxLevel/LevelProvider/RefundTarget）
    ├── UpgradeItemRow.cs         # 条目行（名称/值/成本徽标/加号/回退按钮/MAX/悬停/抖动）
    ├── RefundItemRow.cs          # 回退流水行（已付/返还/不可回退徽标/二次确认）
    ├── CollapsibleSection.cs     # 折叠分区（可见性切换 + 淡入 + 三角方向）
    ├── ClassTabBar.cs            # 技能 6 职业标签页（含 已拥有/总数）
    ├── UpgradeTopBar.cs          # 顶栏（拖拽柄/标题/点数徽标/只读徽标/关闭）
    ├── EnchantSelectPanel.cs     # 附魔选择弹窗
    └── UpgradeLoc.cs             # 自包含双语本地化 + 全部 key 常量 + 格式化
```

## 文件导航（关键方法）

| 文件 | 关键方法 | 说明 |
|---|---|---|
| `Entry.cs` | `Init()` | `Harmony.PatchAll` + `ScriptManagerBridge.LookupScriptsInAssembly` + `UpgradeDataStore.Register()` + `RunStateHook.Subscribe()` + `UpgradeUIHandler.CreateInstance()` |
| `UpgradeUIHandler.cs` | `_Ready`, `PopulateItems()`, `BuildUI()`, `ShowUI()/HideUI()`, `SwitchView()`, `RefreshAllRows()` | 面板生命周期与全部条目注册；`_sectionCategory` 驱动分区摘要 |
| `CardOperationHelper.cs` | `SelectCardFromDeck()`, `TryApplyCardMod()`, `TryApplyAddCard()`, `EnsureMutableInDeck()`, `ApplyEnchantmentToCard()` | 选牌（含取消链路）与 action 执行体 |
| `CardUpgradeTracker.cs` | `RecordModification()`, `ReapplyAllPlayers()`, `ReapplyAll()`, `OnCardRemoved()`, `GetCardIdentity()` | 卡牌修改的持久化与重放（按 NetId 分桶） |
| `UpgradeDataStore.cs` | `Register()`, `For()`, `Mutate()`, `SyncOnRunStarted()` | 每玩家数据的唯一权威入口 |
| `UpgradePurchaseAction.cs` | `UpgradePurchaseFlow.Enqueue*()`, `UpgradeDataAction.ExecuteAction()` | 所有跨端操作的统一入口（购买/加点/卡牌修改/加牌/**回退**） |
| `UpgradeRefundService.cs` | `RefundOf()`, `Enqueue()`, `Execute()`, `RemoveOneLevel()` | 回退规则与执行（80% 下取整；boost/skill/card 三类） |
| `CardModCosts.cs` | `For(type, arg)`, `KeywordAdd/KeywordRemove()` | 卡牌操作成本表（购买 + 旧记录回退反查的唯一来源） |
| `RunStateHook.cs` | `Subscribe()`, `OnCombatSetUp`, `OnCombatWon`, `OnRunStarted`, `Roll()`, `RoomEntryPointsPatch` | 生命周期 + 点数发放 + 检查点 |
| `UpgradeTheme.cs` | `Label()`, `AutoLabel()`, `RichLabel()`, `TextButton()`, `StyleButton()`, `*Stylebox()` | 所有 UI 文本/样式的唯一工厂 |

## 核心流程

### 启动流程
1. 游戏 `OneTimeInitialization.ExecuteEssential()` → `ModManager.Initialize()` → 调用 `Entry.Init()`
   （此时 `LocManager` **还没**初始化 → UI 建树要等本地化就绪，见 `UpgradeUIHandler._Ready`）
2. `Harmony.PatchAll` + 注册脚本 → `UpgradeDataStore.Register()` → `RunStateHook.Subscribe()`
3. `UpgradeUIHandler.CreateInstance()`：建 `CanvasLayer(128)` + 面板 Control，`CallDeferred` 挂到根
4. 面板 `_Ready` → 等 `UpgradeTheme.LocaleReady` → `PopulateItems()`（注册全部条目）→ `BuildUI()`

### 新局 / 读档流程
1. `RunStarted` → `seed = runState.Rng.StringSeed`
2. `PointsPersistence.LoadPoints(seed)`（无文件 → 7）+ `CardUpgradeTracker.Load` +
   `AbilityOperationHelper.Load` + `SkillRegistry.Load`（本机 JSON 镜像）
3. `UpgradeDataStore.SyncOnRunStarted(runState)`：**store 权威优先**；空则从旧 JSON 迁移，
   非本地玩家填默认值（保证两端对同一玩家数据一致）
4. `CardUpgradeTracker.ReapplyAllPlayers(runState)`：为**所有玩家**重放卡牌修改
5. 每个玩家的 `RefreshAllVisuals()` 刷新卡面

### 加点流程（都以同步 action 收口）
```
UI 点击 [+]
 ├─ 能力/技能：UpgradePurchaseFlow.EnqueuePurchase(key, cost, isSkill, isImmediate)
 │     → UpgradeDataAction.ExecuteAction（两端同一 action id）
 │        → AbilityOperationHelper.TryPurchase / SkillRegistry.TryPurchase（校验+扣点+写 store）
 │        → （hp/energy/orbSlot）AbilityOperationHelper.ApplyImmediate
 ├─ 卡牌操作：选牌（本地 UI）→ 校验 → EnqueueCardMod(cost, identity, modType, arg, add)
 │     → CardOperationHelper.TryApplyCardMod（两端按身份找卡 → 扣点 → 改卡 → 记录）
 ├─ 加牌：本地选 ModelId → EnqueueAddCard(modelId)
 │     → CardOperationHelper.TryApplyAddCard（两端 RunState.CreateCard + CardPileCmd.Add）
 ├─ 回退：UI 点 [−] / 回退分区的 [−] → UpgradeRefundService.Enqueue(entry)
 │     → EnqueueRefund(target, paidCost, modType, isSkill, isCardMod)
 │        → UpgradeRefundService.Execute（两端）
 │           ├─ Boost/Skill：store 计数 -1（归零移除）+ 返点 + 即时效果逆操作
 │           └─ CardMod：TryRefundCardMod → 逆操作改卡 → 删记录 → 返点
 └─ 测试加点：EnqueueAddPoints(amount)
UI 等待 action.CompletionTask → 成功则刷新点数标签 + 飘字
```

### 点数与回退规则（改数值时只看这两处）
- **初始点数**：`PointsPersistence.StartingPoints`（其余位置全部引用该常量）。
- **获取区间**：`RunStateHook.OnCombatWon` 的 `Roll(player, min, max)` + `RoomEntryPointsPatch` 的三元组。
- **返还比例**：`UpgradeRefundService.RefundRatio = 0.8f`，`RefundOf(cost) = floor(cost × 0.8)`。
- **成本表**：卡牌操作 → `CardModCosts`；属性/能力/技能 → `UpgradeItemDef.Cost`（`PopulateItems`）。

### 卡牌修改持久化与重放
- 游戏存档只保存 `Id / CurrentUpgradeLevel / Enchantment / Props / FloorAddedToDeck`
  （`SerializableCard`），**不保存** +1 攻击/词缀/合成附魔子项 → 由本模组重放恢复。
- 记录：`NetId → {卡牌身份} → [修改条目...]`，身份 = `{TemplateId}__{同类序号}`
  （牌组顺序即身份来源，`List<SerializableCard>` 按序序列化，两端一致）。
- 重放：精确匹配 → 未匹配则按 TemplateId 回退匹配（并迁移身份）→ 应用全部条目。

### 多人一致性三原则（改代码时务必遵守）
1. **数据都按"主题玩家"从 `UpgradeDataStore.For(player)` 读**，绝不读本地静态缓存
   （静态缓存只服务 UI）。
2. **写只能发生在同步 action 或两端都会执行的确定性 hook 里**；随机必须用
   `ModRunRngRegistry` 的每玩家确定性流，或者把随机结果作为 action 参数传输。
3. **UI 触发的任何状态变更都要走 `UpgradePurchaseFlow`**（不要直接改 store /
   直接改卡牌），否则两端不一致。

## 关键 API 说明

### UI（v3 游戏原生）
- **文本一律用 `UpgradeTheme` 工厂**：`Label`（固定字号）/ `AutoLabel`（自适应）/
  `RichLabel`（支持 BBCode，用于搜索高亮）/ `TextButton`（Flat 按钮 + 子 MegaLabel）。
  原因：`MegaLabel._Ready` 会断言 `font` 主题覆盖存在，**必须**在加入场景树前设好。
- 字体：`UpgradeTheme.FontRegular/FontBold/FontItalic` —— 语言替换字体 → 游戏 Kreon → Godot 兜底。
- 非 Label 控件（Button/LineEdit）用 `UpgradeTheme.ApplyGameFont(control)` 套游戏字体 + 语言替换。
- 图标：`ChevronIcon / DragGripIcon / DiamondIcon / CloseIcon / ArrowRightIcon`
  （`UiIcons.cs`，`_Draw` 矢量绘制、无字形依赖；必须是**顶级类**，嵌套的 GodotObject 子类有注册不上的风险）。
- 本地化：`UpgradeLoc.Get/Format/ResolveDisplayName`，key 常量集中在 `UpgradeLoc`，
  文本在 `InfiniteUpgradeSystem/localization/{zhs,eng}/cards.json`
  （无 pck → 由 `UpgradeLoc` 自己读 DLL 同目录 JSON；游戏 `LocString` 路径由
  `LocStringPromptPatch` 兜底）。

### 卡牌修改
- `card.DynamicVars.Damage.BaseValue += 1m` — 攻击+1（Block/Cards 同理）
- `card.BaseReplayCount += 1` — 重放+1
- `card.AddKeyword/RemoveKeyword(CardKeyword.X)` — 词条
- `card.EnergyCost.SetCustomBaseCost(n)` — 费用
- `CardOperationHelper.UpgradeWithoutTracking(card)` — 无限升级（升级后恢复原 level）
- `CardUpgradeTracker.GetCardIdentity(card, deck)` — 跨端一致的卡牌身份

### 能力 / Power
- `AbilityOperationHelper.ApplyPower(creature, key, count)` — 走游戏原生 `PowerCmd.Apply`（进 Hook 链）
- `ApplyPowerSync(...)` — 无 await 的同步施加（用于必须两端严格同帧的场合）
- `blockKeep` → `BarricadePower`；`freeFirstCard` → `VoidFormPower`；`fanOfKnives` → `FanOfKnivesPower`

### 持久化
- 权威：RitsuLib `PlayerRunSavedData<PlayerUpgradeData>`（NetId 分槽，随 run 存档）
- 镜像：`user://mod_data/InfiniteUpgradeSystem/runs/{points,abilities,skills,card_upgrades}_<seed>.json`
- 卡牌修改存档格式：**v3**（带 `P` = NetId）；兼容读取 v2（扁平身份）/ v1（牌组下标）

## 已知技术债务
1. 能力 Power 应用里的 `card.UpgradeInternal` / `_currentUpgradeLevel` 反射写值
   （`CardOperationHelper.WriteUpgradeLevelField`）—— 依赖游戏内部字段名。
2. 卡牌身份是**相对身份**（同类卡序号），游戏存档没有卡牌实例唯一 id；
   若两端牌组**顺序或构成**不同则会错配（已加日志告警，但无法自愈）。
3. 合成附魔的子附魔不进游戏存档（`Subs` 非 JsonProperty），完全依赖本模组重放；
   一旦 `card_upgrades_<seed>.json` 丢失，附魔会退化。
4. 卡牌选择界面（`CardSelectCmd.FromDeckGeneric`）与游戏原生取消链路的兼容补丁
   （禁用返回按钮 + 自接管 Esc）依赖游戏内部节点类型 `NDeckCardSelectScreen`。
5. 加牌（`AddCardFromPool`）尚未接入 UI（策划中的「向牌组添加普通/罕贵/稀有牌」），
   同步 action 通路（`UpgradeDataOp.AddCard`）已就绪。

## 扩展指南

### 新增一个加点项（属性/能力）
1. `UpgradeUIHandler.PopulateItems()` 加一行 `StatItem(...)`（或 `SkillItem(...)`）；
2. 效果侧：能力 → `AbilityOperationHelper.ApplyPower` 加 key 映射；即时生效 → `ApplyImmediate`；
   **若新增即时生效类** → 同时在 `ApplyImmediateRefund` 加逆操作（否则回退后效果残留）；
3. **若要支持回退**：`StatItem(..., refundTarget: "<store key>")` 或 `SkillItem(...)` 自动带上；
4. 本地化：`UpgradeLoc` 加 key 常量 + `localization/{zhs,eng}/cards.json` 各加一条；
5. **UI 组件代码零改动**（分区/标签页/MAX/禁用/回退按钮状态由数据驱动）。

### 新增一项卡牌操作
1. `CardOperationHelper` 加方法：选牌 → `CanApplyCardMod` 校验 → `EnqueueCardMod(...)`；
   `CardUpgradeTracker.ModType` 加枚举 + `ApplyModification` 加应用分支
   （**注意：读档重放也要能复原这个修改**，否则读档后丢失）；
2. **若要支持回退**：在 `RevertCardMod` 加对应的精确逆操作 + `IsRevertible` 放行 +
   `CardModCosts.For` 补上成本（旧记录回退反查用）；
3. `PopulateItems()` 加 `ActionItem(..., promptKey, ...)`；
4. 本地化：ITEM_ 名称 key + PROMPT_ 提示 key（中英各一份）。

### 新增一个技能
见 [UI设计.md](UI设计.md) §六；要点：`SkillItem(...)` 一行 + `SkillEffectsPower` 里接线效果，
UI/持久化零改动。
