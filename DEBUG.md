# DEBUG 记录 (InfiniteUpgradeSystem)

## 技能系统实施记录（Phase S1-S4，2026-08-04）

- **S1** SkillRegistry（等级化 Dictionary<string,int>，MaxLevel=1）+ skills_<seed>.json 检查点持久化 + 技能分区标签页 UI
- **S2** 打出牌触发 9 项：
  - **Harmony 参数名绑定坑**：Postfix 参数名必须与原方法一致（combatState/choiceContext/cardPlay），
    否则抛 `Parameter "state" not found` 导致 PatchAll 失败、整个模组初始化中止
  - **Power 叠加修复（IL 反汇编实锤）**：`Creature.ApplyPowerInternal` 对已存在同类 Power 抛异常
    （重复应用检查）——第二次触发同类技能失败。修复：`HasPower<T>()` 已有则 `SetAmount` 叠加
    （触发 PowerModified 更新显示与数值）
- **S2.5** 局内只读面板：战斗内 P 可开、加号隐藏不可操作；计数类技能值列显示 **N%4** 余数
- **S3** 事件触发 6 项：消耗/弃牌/受伤/中毒/回合开始/回合结束（AfterSideTurnStart + CombatSide.Enemy 判定）
  - 部分钩子无 PlayerChoiceContext → `SkillContextCache` 复用最近一次事件的 context
  - block_on_poison 数值：1 格挡 → 3 格挡（用户调整，一次施加多层只触发 1 次）
- **S4** 休息处任意选项：Postfix on `Hook.ShouldDisableRemainingRestSiteOptions` → 技能已购时强制 false
  （与微型帐篷 MiniatureTent 同机制）
- 技能均为本局有效（按 seed 持久化）；新增技能流程见 UI设计.md §六

---

## UI v2 重构技术决策记录（Phase 2-5，2026-08-04）

本段汇总 UI v2 重构中踩过的坑与最终方案（供后续维护参考）：

### 1. 折叠分区渲染不可靠 → 可见性切换
- 症状：逻辑层全对（调试日志证明行数/高度/在树），但「裁剪容器 + min-size Tween」方案内容不显示
- 结论：min-size/Tween/锚点组合在模组环境下渲染不可靠，弃用
- 最终：内容直接子节点 + `Visible` 切换（容器自动重排）+ `modulate` 淡入 + 箭头绕中心旋转

### 2. Button 无文本时最小尺寸 ≈12px
- 症状：标题文字溢出按钮矩形、点击落在按钮外 → Pressed 不触发
- 修复：显式 `CustomMinimumSize (0, 34)`；**凡"无文本按钮 + 子节点提供视觉"必须显式给高度**

### 3. ScrollContainer 子节点必须显式撑宽
- 症状：行内 ExpandFill 名称标签被压到 0 宽（只有数字/加号可见——它们有最小尺寸）
- 修复：`SizeFlagsHorizontal = ExpandFill`

### 4. 游戏 CancelSelection 空选牌必炸（游戏本体 bug）
- IL 反汇编：`CancelSelection` 对已选牌集合调 `First()`，0 张时抛 `Sequence contains no elements`，
  异常中断任务完成 → 选牌屏永远关不掉（返回图标同样触发）
- 最终：自接管取消链路 —— `Task.WhenAny(游戏任务, 取消信号)`；Esc → 强制 `Remove` 选牌屏 + 触发信号；
  选牌发起时隐藏游戏返回按钮（防挂起陷阱）

### 5. 游戏不加载我们的本地化表（无 pck）
- BaseLib 文件夹本地化不生效（manifest 扫描会跳过非 manifest 的 json）
- LocString 解析失败返回 "Missing localization key" 占位文本；`GetTable` 未知表返回空表但 `GetRawText` 仍返回错误文本
- 最终：`LocStringPromptPatch`（Harmony Postfix on `GetFormattedText`）——key 带 `INFINITEUPGRADESYSTEM-` 前缀时用 `UpgradeLoc` 解析

### 6. Label 不支持 BBCode（Godot 4.5 官方文档确认）
- 搜索高亮必须用 RichTextLabel（`BbcodeEnabled` + `FitContent` + `ScrollActive=false` 与 Label 等宽等高）

### 7. 拖拽逻辑吞掉顶栏按钮点击
- 症状：✕ 关闭按钮无反应——顶栏 40px 内所有按下都命中拖拽命中区并被 `SetInputAsHandled`
- 修复：拖拽命中排除 `CloseButton` 区域；**顶栏新增交互控件必须同步排除**

### 8. 常用 Godot 4.5 API 勘误
- `Transform2D.GetScale()` 不存在 → 用基底向量长度
- `CallDeferred(Callable)` 不存在 → `CallDeferred(StringName, args)` 具名方法
- `Label.BbcodeEnabled` 不存在（Label 无 BBCode）
- `FontFile` 无 `BaseSize`/`VariationOpentype` 属性 → `SetVariationCoordinates(0, {"wght": N})`
- 旋转默认绕左上角 → 必须设 `PivotOffset`

---

## Phase 0.5: 2K 分辨率 UI 模糊修复

- **日期**: 2026-08-04
- **现象**: 2560×1440 下模组面板文字发糊；1080p 正常
- **排查过程**:
  1. 排除系统缩放：日志确认 `Screen info: Size: (2560,1440) Scale: 1 DPI: 96`（无 Windows DPI 缩放）
  2. 运行时探针（`显示探针(open)`）实锤：`window=(2560,1440) contentScaleSize=(1920,1080) mode=1 aspect=1 finalScale=1.333,1.333`
     - mode=1 = **canvas_items** 内容缩放（游戏 UI 以 1920×1080 为基准，2K 下全部 2D 内容放大 1.333×）
     - **CanvasLayer 受内容缩放影响**（finalScale=1.333 证明），面板渲染为 640×1.333=853 屏幕像素
  3. **根因**: 普通动态字体的字形贴图按画布单位尺寸栅格化，再被画布变换双线性放大（纹理放大而非矢量重渲染）→ 发糊
  4. LCD 亚像素抗锯齿 + OneQuarter 亚像素定位：无效（救不了被放大的贴图）
  5. **MSDF（有符号距离场）**：生效，任意缩放锐利
- **二次问题（噪音）**: Google Fonts 转换的 Noto Serif SC 变量字体有重叠字形 + 变量字体的 MSDF 兼容问题 → MSDF 渲染出噪音
  - 解决：换 **Adobe 官方构建** `SourceHanSerifSC-Regular.otf` + `SourceHanSerifSC-SemiBold.otf`（2.003R，静态 OTF），噪音消失
- **三次问题（漏网节点）**: 提示行「[P] 打开/关闭」和「关闭」按钮未应用主题字体（只用游戏默认字体渲染，非 MSDF）→ 仍然发糊
  - 解决：补 `AddThemeFontOverride("font", UpgradeTheme.Regular)`
  - **教训: 每个新建文本节点必须应用主题字体，否则回退游戏默认字体（非 MSDF）→ 高分辨率下必糊。Phase 1 重构应把文本创建收拢到统一构建方法**
- **关键结论**: `UpgradeTheme.LoadFont` 中 MSDF 配置（`MultichannelSignedDistanceField=true, MsdfSize=48, MsdfPixelRange=16`）是本面板在高分辨率下清晰的必要条件；**必须使用官方静态 OTF，禁止换回 Google 转换版或变量字体**

---

## Phase 0: UI 主题基建（字体 + 色板）

- **日期**: 2026-08-04
- **功能**: 贴原版质感主题基建 — 打包 Noto Serif SC（思源宋体，OFL）+ 集中色板 `UpgradeTheme`
- **实现**:
  - `Scripts/UiComponents/UpgradeTheme.cs` — 新建：色板常量 + 字体加载（`FontFile.LoadDynamicFont` + `SetVariationCoordinates(0, {"wght": 600})` 实现 SemiBold）
  - `Scripts/UpgradeUIHandler.cs` — 现有 v1 UI 换皮（面板 StyleBoxFlat 金边、标题/点数金色衬线、正文/搜索/按钮字体）
  - `resources/fonts/NotoSerifSC.ttf` — 25MB 可变字重字体
- **技术决策**:
  1. **字体加载路径基于 `Assembly.Location`**（DLL 同目录 `resources/fonts/`），与 mod 所在位置解耦 —— 游戏从 `mods/`、workshop 目录加载都能找到
  2. **csproj 部署目标已加字体复制**（`CopyToOutputDirectory` + 部署 Target 内 Copy）
  3. **部署与测试路径：本地 `mods/` 目录**（Steam 优先加载本地 mod；workshop 目录无需改动）。csproj 的 `CopyToModsFolderOnBuild` 目标已在每次构建后自动部署 DLL + 字体到 `mods/InfiniteUpgradeSystem/`。workshop 目录仅在上传发布时更新
  4. 本地化 JSON 走 `res://` 路径（pck 挂载），与 DLL 目录无关；但字体是纯 C# 读取文件，不受此限制

---

## v26: 新增"能量跨回合不消失"能力

- **日期**: 2026-07-21
- **功能**: 消耗 25 点，购买后未使用的能量在回合结束时保留，参考原版 IceCream 遗物
- **实现**: Harmony Postfix on `Hook.ShouldPlayerResetEnergy` → 检查 `s_boosts["energyKeep"]`
- **修改**:
  - `Scripts/Patches/EnergyKeepPatch.cs` — 新建，Harmony Postfix
  - `Scripts/UpgradeUIHandler.cs` — 新增 UI 按钮（25 点）
  - `Entry.cs` — BUILD=v26-20260721
  - `mod_manifest.json` — 1.0.4 → 1.0.5
- **技术决策**: 选择 Harmony 补丁而非自定义 PowerModel，因为 ApplyOnePower 使用反射从游戏程序集加载类型，自定义 mod Power 无法被 `ModelDb.Power<T>()` 发现

---

## v25: 快速重启后丢失升级的修复

- **日期**: 2026-07-20
- **现象**: 快速重启（退出到主菜单再继续）后，之前花费点数购买的卡牌修改（升级、减费、加伤等）部分丢失
- **复现**: 战斗中修改多张卡牌 → 退出到主菜单 → 继续游戏 → 部分修改消失
- **日志特征**:
  ```
  [WARN] InfiniteUpgrade: ReapplyAll cannot find card BASH (was at index=8); modifications lost.
  [WARN] InfiniteUpgrade: ReapplyAll cannot find card BASH (was at index=9); modifications lost.
  [WARN] InfiniteUpgrade: ReapplyAll cannot find card BREAK (was at index=13); modifications lost.
  [WARN] InfiniteUpgrade: ReapplyAll cannot find card BREAK (was at index=16); modifications lost.
  ```
- **原因**:
  1. `CardUpgradeTracker` 使用牌组下标 (deck index) 作为记录的 key
  2. 同一张卡在不同时间被修改时，如果下标发生变化（被其他卡牌增删挤到不同位置），会产生多条记录
  3. 快速重启后游戏从检查点重建牌组，卡牌顺序与修改时的下标不一致
  4. `ReapplyAll` 的匹配算法对每张牌只能匹配一条记录（用 `matchedDeckIndices` 去重），导致同一张卡的多条记录只有第一条能匹配，其余全部丢失
- **修改**:
  - **`CardUpgradeTracker.cs`** (完全重写):
    - 存储 key 从 `int deckIndex` 改为 `string cardIdentity = "{TemplateId}__{实例序号}"`
    - 实例序号 = 在牌组中从头扫描，到目标卡为止同类卡牌的计数 (0-based)
    - 同一张卡的所有修改合并在一条记录中（不再按多次下标分散）
    - `ReapplyAll` 改用身份精确匹配 + TemplateId 回退匹配（处理向后兼容和边界情况）
    - 保存格式 v2: `{"Id":"BASH__0","T":"BASH","E":[...]}` (v1 `{"I":9,...}` 向后兼容)
    - v1 旧格式加载时自动按 TemplateId 合并多条记录
    - `OnCardRemoved` 重写：删牌后按新牌组顺序重建身份
    - `GetCardIdentity()`: 用 ReferenceEquals 找到目标卡在牌组中的位置，计算同类序号
  - **`Entry.cs`**: BUILD 版本号更新为 v25-20260720
  - **`mod_manifest.json`**: 版本 1.0.3 → 1.0.4
- **验证结果**: 编译通过 (0 errors)。需在游戏内测试：修改卡牌 → 退出到主菜单 → 继续 → 确认所有修改保留。

---

## 持久化方案演进

### v1-v5: 全局文件
- 问题：跨 Run 数据污染（新局继承旧局数据）
- 尝试过：TotalFloor 检测、Harmony NGame 拦截（async 方法匹配失败）

### v6+: 每 Run 独立文件
- 方案：`points_{seed}.json`，按 `RunState.Rng.StringSeed` 区分
- 新局 = 新 seed = 新文件 = 5 点起步
- 读档 = 同 seed = 同文件 = 恢复

### 检查点机制
- 问题：每次修改立即写盘，退出重进无法回滚
- 方案：战斗开始/结束时保存，中间修改只在内存

## 卡牌持久化

### CardModel.ToSerializable 限制
- 只存 Id + CurrentUpgradeLevel + Enchantment
- DynamicVar 修改不被保存 → 需要 CardUpgradeTracker 独立追踪

### CurrentUpgradeLevel 重置
- 问题：不重置 → MaxUpgradeLevel 校验 → FromSerializable 循环翻倍
- 方案：升级后重置为 0，CardUpgradeTracker 记录次数，读档时重放

### 同名卡牌区分
- 问题：templateId 追踪 → 所有同名卡牌共享升级
- 方案：deckIndex 追踪 + templateId 校验

### JSON 反序列化
- 问题：System.Text.Json 默认 camelCase，C# 属性 PascalCase → 反序列化失败
- 方案：PropertyNameCaseInsensitive = true

## 能力 Power 应用

### PowerCmd.Apply 运行时签名不匹配
- 问题：编译通过但运行时找不到重载（需要 PlayerChoiceContext）
- 方案：ModelDb.Power<T>().MutableClone().ApplyInternal(creature, amount)

### ToMutable() → MutableClone()
- 问题：AbstractModel.ToMutable() 不存在
- 方案：使用 MutableClone() 方法

### 每场战斗重新应用
- 问题：Power 在战斗结束后清除，s_appliedThisRun 阻止重新应用
- 方案：CombatWon 时 ResetForNextCombat()

## UI

### has_pck=false 本地化不加载
- 问题：LocString 自定义 key 无法解析
- 方案：使用游戏内置 CardSelectorPrefs 静态 LocString

### UI 遮罩阻塞卡牌选择
- 问题：ColorRect MouseFilter.Stop 拦截原生选择界面
- 方案：SetUIVisible(false) → SelectCard → SetUIVisible(true)

### Harmony async 方法匹配
- 问题：NGame.StartRun/LoadRun 是 async → Prefix 不触发
- 方案：改用 C# 事件订阅 (CombatWon, RunStarted)

---

## update04 实施记录（2026-08-10，每个功能一个 git commit）

### C1 数值调整
- 初始点数 6 → 7：真实生效点在 `PointsPersistence.LoadPoints` 默认值（新局无 seed 文件时返回 7），`UpgradePointManager` 的 `InitializeDirect` 从未被调用、不生效
- 休息处任意选项 12 → 10（`UpgradeUIHandler` rest_all_options cost）

**测试**：新开局按 P → 点数显示 7；购买「你可以在休息处选择任意数量的选项」扣 10 点。

### C2 技能「每打出一张牌都给予所有敌人一层中毒」（静默猎人，15 点，id=poison_all_on_card_play）
- `ApplyOnePower` 新增 `poison` → PoisonPower 映射
- `Hook.AfterCardPlayed` 中遍历 `combatState.Enemies` 各施加 1 层中毒

**测试**：静默猎人开局 → 攒点购买该技能 → 进入战斗 → 打出任意牌 → 所有敌人头顶各出现 1 层中毒；再打出牌中毒叠加。

### C3 技能「回合结束时，你的格挡翻倍」（铁甲战士，15 点，id=double_block_at_turn_end）
- `Hook.BeforeSideTurnEnd` + `CombatSide.Player`（格挡清零前触发）
- 读取 `Creature.Block`，`CreatureCmd.GainBlock(block)` 补到 2 倍；翻倍后按游戏规则正常结算（无格挡保留则下回合开始清零）

**测试**：铁甲战士开局 → 购买该技能 → 战斗内本回合先获得一些格挡（如打出防御牌）→ 点「结束回合」→ 回合结束瞬间格挡数字翻倍（敌方回合可见翻倍后的格挡）；配合「格挡跨回合不消失」能力验证翻倍后保留。

### C4 技能「每铸造一次，君王之剑永久获取1格挡」（储君，12 点，id=sovereign_blade_block_on_forge）
- **实现（v2，参考招架能力牌）**：君王之剑 OnPlay 仅在玩家有招架层数（`GetOwnerParryAmount > 0`）时才 GainBlock（IL 反编译确认）。初版 bump `CalculationBase` 被此门禁挡住 → 无效
- 修复：`Hook.AfterForge` → `AbilityOperationHelper.AddSovereignBladeForgeParry`：铸造时玩家 +1 招架，存入 `s_boosts["parry"]`（持久化），每场战斗开始由 `ApplyInitialBoosts` 重新应用 → 君王之剑按招架层数获得格挡 = 铸造次数，跨战斗永久
- `ApplyOnePower` 新增 `parry` → ParryPower 映射（通用 Power 应用方法，后续易伤/力量等效果同法扩展）

**测试**：储君开局 → 购买该技能 → 铸造一次（如打出带铸造的牌）→ 君王之剑卡面格挡 +1（无招架时原为 0）；多次铸造格挡累加；进入下一场战斗后格挡加成仍在（招架每场战斗重新应用）。

### C5 技能「奥斯提会额外攻击一次」（亡灵契约师，15 点，id=osty_extra_attack）
- `Hook.ModifyAttackHitCount`：`attackCommand.Attacker?.Monster is Osty` 时 `__result *= 2`
- 实现为**奥斯提攻击命中次数翻倍** = 攻击两次（每次独立结算），与「这张牌额外打出1次」等效；卡牌除奥斯提攻击外的其它效果只触发一次

**测试**：亡灵契约师开局 → 购买该技能 → 战斗中打出奥斯提攻击牌 → 奥斯提连续攻击两次（伤害跳两次）；一回合内再打一张奥斯提攻击牌 → 再攻击两次。请验证伤害结算是否为两次独立攻击（格挡分别结算）。

### C6 技能「每当你抽到能力牌时，自动打出」（故障机器人，20 点，id=auto_play_power_on_draw）
- `Hook.AfterCardDrawn` + `CardType.Power` → `CardCmd.AutoPlay(choiceContext, card, null)`（参照地狱狂徒 HellraiserPower，免费自动打出，不耗能量）
- 按用户要求最小实现：不加循环防护。若与「每打出1张能力牌抽1张」同时拥有，自动打出的能力牌会触发抽牌 → 再抽到能力牌则再自动打出，连锁直至抽牌堆空（概率低、自然终止）

**测试**：故障机器人开局 → 购买该技能 → 进入战斗 → 抽到能力牌（如「冰冷」等 Power）→ 该能力牌被自动打出、不消耗能量、立即生效；手牌中不再停留。验证多张能力牌连续抽到时的自动打出表现。

---

## update05 实施记录（2026-08-10，每个功能一个 git commit）

### C1 点数获取随机化
- `OnCombatWon`：普通怪 1→随机1-3、精英 5→随机5-7、Boss 20→随机25-30（`Rng.Chaotic.NextInt(min, max+1)`，含两端；用全局 Chaotic 避免扰动 run RNG 各流确定性）
- 新增 `[HarmonyPatch(Hook.AfterRoomEntered)]` `RoomEntryPointsPatch`：进入问号房+1-5、商店+1-5、火堆+2-6；`AddPoints` 内部立即写盘（退出重进不丢失）
- **问号房只获取问号房点数**（update05 规则）：`RunStateHook.s_enteredEventRoom` 标记位——进入 Event 房置 true（已发点数），CombatWon 时若为 true 则跳过战斗点数并复位；`AfterRoomEntered` 非 Event 房复位；`OnRunStarted` 复位。无论事件战斗内部报什么房间类型，都不会双发
- 随机用 `RunStateHook.Roll`（internal，供补丁类调用）

**测试**（新开一局，按 P 观察点数变化）：
1. **普通小怪**：战斗胜利后点数 +1~3（多次战斗验证在区间内波动）
2. **精英**：胜利 +5~7
3. **Boss**：胜利 +25~30
4. **问号房（含战斗）**：进入问号房 → 立即 +1~5；若事件遭遇战斗，战斗胜利后**不再**额外获得怪点数（总获得 = 进入时的 1~5）
5. **商店**：进入商店 → +1~5
6. **火堆**：进入火堆 → +2~6
7. **立即写盘**：进商店/问号房拿到点数后立刻退出游戏重进 → 点数保留（不回到上次战斗检查点）
8. **读档/新局**：从地图进入新房间，标记位复位，后续战斗点数恢复正常

### C2 能力「每当你击败一名敌人时，获取20金币」（7 点，key=gold_on_kill）
- UI 注册：`StatItem("能力", ..., ItemGoldOnKill, 7, GetBoost("gold_on_kill"), Purchase("gold_on_kill",7), maxLevel:1)`（能力效果分类，第 6 项）
- 补丁：`Scripts/Patches/GoldOnKillPatch.cs` — `[HarmonyPatch(Hook.AfterDeath)]` Postfix：`creature.IsEnemy`（排除友方宠物/奥斯提死亡）+ `wasRemovalPrevented` 为 false（死亡未被阻止）+ 已购能力 → `PlayerCmd.GainGold(20m, player)`
- 击杀来源不限：攻击/中毒/自爆/Doom 致死的敌人都会触发（update05 访谈已确认接受此副作用）
- `s_boosts["gold_on_kill"]` 走 abilities_<seed>.json 持久化，读档恢复

**测试**（新开一局攒 7 点 → 购买该能力 → 进入战斗）：
1. **普通击杀**：击杀 1 个普通怪 → 金币 +20
2. **多怪战斗**：一场战斗击杀多个敌人 → 每个敌人 +20（2 个 = +40）
3. **精英/Boss**：击杀同样触发 +20
4. **事件战斗**：问号房遭遇战斗，击杀敌人也 +20
5. **非攻击击杀**：毒杀/自爆击杀的敌人是否也 +20（验证副作用是否符合预期）
6. **读档**：退出重进 → 能力仍在（金币能力生效）
7. **上限**：该能力为一次性（购买后该行变已拥有/满级，不可重复购买）

### C3 技能「你的回合开始时，获取2力量」（通用，12 点，id=str_at_turn_start）
- 注册：`SkillItem(ClassTabBar.Generic, ..., ItemSkillStrAtTurnStart, "str_at_turn_start", 12)`（通用标签页，在「每回合开始时获取消耗牌堆数等量格挡」之后）
- 补丁：`SkillEventPatches.cs` `SkillStrAtTurnStartPatch` — `[HarmonyPatch(Hook.AfterPlayerTurnStart)]` → `ApplyOnePower(player.Creature, "strength", 2)`
- 与已有 `block_at_turn_start` 同款钩子（每回合触发，非仅首回合）；`ApplyOnePower` 已有同类 Power 时 SetAmount 叠加

**测试**（新开一局攒 12 点 → 购买该技能 → 进战斗）：
1. **每回合触发**：第 1 回合开始力量 +2，第 2 回合再 +2（累计 4），第 3 回合 6——验证**每回合都加**而非仅首回合
2. **战斗内持久**：力量面板/攻击伤害随力量叠加递增
3. **跨战斗重置**：下场战斗力量回到 2（技能每场战斗重新从 0 施加）
4. **读档**：退出重进 → 技能仍在

### C4 技能「你的回合开始时，回复3点生命值」（铁甲战士，8 点，id=heal_at_turn_start）
- 注册：`SkillItem(ClassTabBar.Ironclad, ..., ItemSkillHealAtTurnStart, "heal_at_turn_start", 8)`（铁甲标签页，格挡翻倍之后）
- 补丁：`SkillHealAtTurnStartPatch` — `[HarmonyPatch(Hook.AfterPlayerTurnStart)]` → `CreatureCmd.Heal(player.Creature, 3)`
- 与 `str_at_turn_start` 同款钩子（每回合触发，非仅首回合）

**测试**（铁甲战士开局攒 8 点 → 购买该技能 → 进战斗）：
1. **每回合回血**：受到伤害后进入下一回合 → 生命值 +3（第 1 回合开始 +3，第 2 回合开始再 +3）
2. **满血不溢出**：满血时回合开始不超上限（生命值不变或不超过上限）
3. **跨战斗重置**：技能每场战斗都生效

### C5 技能「你的回合开始时，额外摸1张牌」（静默猎手，8 点，id=draw_at_turn_start）
- 注册：`SkillItem(ClassTabBar.Silent, ..., ItemSkillDrawAtTurnStart, "draw_at_turn_start", 8)`（静默标签页，中毒技能之后）
- 补丁：`SkillDrawAtTurnStartPatch` — `[HarmonyPatch(Hook.AfterPlayerTurnStart)]` → `CardPileCmd.Draw(choiceContext, player)`
- 与 `str/heal_at_turn_start` 同款钩子（每回合触发）；抽牌堆空时自然抽 0 张不报错

**测试**（静默猎手开局攒 8 点 → 购买该技能 → 进战斗）：
1. **每回合多摸1张**：回合开始手牌 = 正常 5 张 + 1 = 6 张；第 2 回合同样 6 张
2. **抽牌堆空**：多回合消耗后抽牌堆见底，回合开始抽 0 张，不报错、不崩溃
3. **与其它摸牌技能叠加**：若同时拥有「每打出1张能力牌抽1张」等，可叠加

### C6 技能「你的回合开始时，免费打出第一张牌」（储君，8 点，id=free_first_card）
- **v2 借鉴原生 VoidFormPower（虚空形态）**：不再用自写的 ModifyEnergyCostInCombat hack（已删除 SkillFreeFirstCardPatch.cs）
- `ApplyOnePower` 新增映射 `freeFirstCard → VoidFormPower`；`RunStateHook.OnCombatSetUp` 战斗开始时若拥有该技能则应用 `VoidFormPower(1)`
- **VoidFormPower 原生能力**（IL 反编译确认）：
  - `TryModifyEnergyCostInCombat`（能量归零）+ `TryModifyStarCost`（辉星归零）——首牌能量与辉星都免费
  - 自带每回合计数：`AfterCardPlayed` 自增（非自动打出、末次结算才计），`BeforeSideTurnStart` 归零
  - **原生绿色荧光显示**（临时0费视觉效果）；辉星免费为蓝色荧光
- 与虚空形态卡叠加：技能 VoidFormPower(1) + 卡 VoidFormPower(2) = 前 3 张免费

**测试**（储君开局攒 8 点 → 购买该技能 → 进战斗）：
1. **首牌能量免费**：打第一张牌不消耗能量
2. **首牌辉星免费**（重点，v1 缺陷修复）：打第一张带辉星费的牌（如储君的王牌类）→ 辉星不消耗；第二张才扣
3. **绿色/蓝色荧光**：首牌打出前，手牌中带费用的牌应显示**绿色荧光**（能量免费）；带辉星费的牌显示**蓝色荧光**（辉星免费）——**与虚空形态打出后的显示一致**
4. **每回合重置**：第 2 回合第一张牌再次免费
5. **次牌原价**：第二张牌正常扣能量/辉星
6. **自动打出不消耗名额**：若同时拥有「抽到能力牌自动打出」，自动打出的牌不计入首牌名额

### C7 技能「你的回合开始时，额外召唤2」（亡灵契约师，8 点，id=summon_at_turn_start）
- 注册：`SkillItem(ClassTabBar.Necrobinder, ..., ItemSkillSummonAtTurnStart, "summon_at_turn_start", 8)`（亡灵标签页，奥斯提攻击技能之后）
- 补丁：`SkillSummonAtTurnStartPatch` — `[HarmonyPatch(Hook.AfterPlayerTurnStart)]` → `OstyCmd.Summon(context, player, 2m, null)`
- **召唤机制说明**（IL 反编译确认）：奥斯提是单只宠物，`Summon` 的 amount = 给奥斯提 +MaxHp（体型/强度）；"召唤2" = 奥斯提 +2 MaxHp，与「每打出1张牌召唤1」同模式。奥斯提死亡时以 2 点 MaxHp 复活

**测试**（亡灵契约师开局攒 8 点 → 购买该技能 → 进战斗）：
1. **每回合召唤2**：回合开始 → 奥斯提 MaxHp +2（体型变大/生命上限增加）
2. **死亡复活**：若奥斯提死亡，回合开始以 2 点 MaxHp 复活（而非死着）
3. **与打出牌召唤叠加**：同时拥有「每打出1张牌召唤1」时，两者都生效
4. **跨战斗重置**：每场战斗重新从基础召唤数开始

### C8 技能「你的回合开始时，随机获取1个充能球」（故障机器人，8 点，id=orb_at_turn_start）
- 注册：`SkillItem(ClassTabBar.Defect, ..., ItemSkillOrbAtTurnStart, "orb_at_turn_start", 8)`（故障标签页，自动打出能力牌之后）
- 补丁：`SkillOrbAtTurnStartPatch` — `[HarmonyPatch(Hook.AfterPlayerTurnStart)]` → `OrbCmd.Channel(context, OrbModel.GetRandomOrb(player.RunState.Rng.CombatOrbGeneration).ToMutable(), player)`
- **参照混沌卡 Chaos**（原生随机充能球机制）：5 种球（闪电/冰霜/暗黑/玻璃/等离子）随机，走 `CombatOrbGeneration` RNG 流不扰动其它随机

**测试**（故障机器人开局攒 8 点 → 购买该技能 → 进战斗）：
1. **随机充能球**：回合开始 → 充能球栏出现 1 个球（闪电/冰霜/暗黑/玻璃/等离子随机）；多次战斗验证随机性
2. **每回合一个**：第 2 回合再充能 1 个（栏位足够时）
3. **栏位不足**：充能球栏满时继续充能 → 按游戏原生规则（虚空/替换）表现，不崩溃
4. **与「回合结束每球伤害」叠加**：同时拥有该技能时，充能球越多，回合结束伤害越高

### C9 卡牌附魔「为一张卡牌新增附魔」（12 点，卡牌操作区）
**实现（9a 合成模型 + 9b UI 流程）**：
- `CompositeEnchantment`（新模型）：挂卡牌单 Enchantment 槽位，内部 `Subs` 子附魔列表，转发 14 个效果方法 + 战斗 Hook（OnPlay/AfterCardPlayed/AfterCardDrawn/AfterPlayerTurnStart/BeforeFlush/ModifyShuffleOrder）
- 持久化：`CardUpgradeTracker` 新增 `ModType.Enchant`（Keyword=附魔类型），每次购买记一条；ReapplyAll 幂等重建合成模型
- 序列化安全：游戏存档写合成附魔未知 Id → 反序列化回退 DeprecatedEnchantment（不崩溃）→ ReapplyAll 覆盖重建
- 上限按稀有度：普通 2 / 罕见 3 / 稀有 5 / 其它 3 种；同种购买 +1 层；**已达上限选新种 → 替换最早的那个**（删旧记录+合成模型删子附魔）
- UI：选卡（游戏选牌屏）→ `EnchantSelectPanel` 弹窗列出兼容附魔（类型门禁，上限时提示替换），Esc/取消退款
- 显示：卡片图标取首个子附魔，标题统一"多重附魔"（LocStringPromptPatch 扩展），悬停聚合所有子附魔描述

**测试**（新开一局攒点 → 卡牌操作「为一张卡牌新增附魔」）：
1. **基础**：选一张牌 → 弹窗列出可用附魔（攻击牌只能选攻击类附魔如 Sharp/腐蚀 等）→ 选一个 → 卡片出现附魔图标+层数，附魔效果生效（如 Sharp=攻击伤害+）
2. **多种共存**：同一张牌再买一次 → 选**不同**附魔 → 两个附魔同时存在、效果都生效（如 Sharp 加伤害 + Nimble 加格挡）
3. **同种叠层**：选已存在的附魔 → 层数 +1（如 Sharp ×2）
4. **上限**：普通牌（如初始打击）最多 2 种附魔 → 第 3 种新附魔时弹窗提示"已达上限，选新附魔将替换最早的那个" → 选后最早的被替换，总数保持 2
5. **稀有度上限**：罕见牌 3 种 / 稀有牌 5 种
6. **读档**：附魔后退出重进 → 附魔保留（多种附魔都还原）
7. **悬停显示**：卡片悬停 → 显示"多重附魔"标题 + 各子附魔描述；卡片图标 = 首个子附魔图标
8. **有战斗 Hook 的附魔**：选一个有 Hook 的附魔（如 Glam 打牌后效果 / Goopy 等）→ 战斗中效果正常触发
9. **取消退款**：选牌后 Esc/取消 → 不扣点、附魔不应用
10. **已知限制**：替换掉的附魔若有一次性 OnEnchant 副作用（如 TezcatarasEmber 减费）不会回滚

> ⚠️ **测试重点**：合成附魔是本次 update05 风险最高的功能。请重点验证 6（读档还原）、8（战斗 Hook 附魔）、以及多种附魔同时生效时数值是否正确叠加。

### C9 修复：附魔面板 GetTypes 崩溃
- **现象**：点击「为一张卡牌新增附魔」→ 选完牌后报 `AddEnchantment: Unable to load one or more of the requested types`
- **根因**：`EnchantSelectPanel.GetEnchantTypes()` 对**所有**程序集调用 `Assembly.GetTypes()`；Steamworks.NET 的 `OptionValue` 类型不可加载（对象字段对齐错误），`GetTypes()` 抛 `ReflectionTypeLoadException`（RitsuLib 日志 906 行同问题，它优雅跳过）
- **修复**：只枚举游戏主程序集 `sts2`（附魔类型都在其中）+ try/catch 用 `ex.Types` 跳过不可加载项
- **教训**：模组代码避免对 `AppDomain.GetAssemblies()` 全量 `GetTypes()`；用 `GetType(name)` 或限定程序集

### C9 修复 v2：DuplicateModelException + 附魔描述格式/BBCode
- **根因 1（致命）**：`ModelDb` 初始化时遍历 `AllAbstractModelSubtypes`（**所有 AbstractModel 子类，含模组的 CompositeEnchantment**）用 `Activator.CreateInstance` 注册到 `_contentById` → 启动后 `ModelDb.Contains(CompositeEnchantment)` 恒为真 → **任何 `new CompositeEnchantment()` 都抛 DuplicateModelException**（"already contains ID ENCHANTMENT.COMPOSITE_ENCHANTMENT"）
  - 初版静态模板修复无效（首次 `new` 就抛）。**正确修复**：`ModelDb.Enchantment<CompositeEnchantment>().ToMutable()` 取已注册 canonical（内部走 MutableClone，不调用构造函数）；`DeepCloneFields` 重建 `Subs` 独立列表；try/catch 回退 `new`+MutableClone
  - 副作用：CompositeEnchantment 会出现在调试控制台 `EnchantConsoleCmd` 的附魔列表（`DebugEnchantments` 全量枚举）——仅调试工具，游戏事件都用特定附魔 Id，不会随机选中
- **根因 2**：附魔面板 tooltip 用 `canonical.Description.GetFormattedText()`，描述含 `{Block}/{Damage}/{Amount}` 变量但无 DynamicVar 来源 → `No source extension could handle the selector` 格式错误；且 `[gold][/gold]` 自定义 BBCode 在 Godot 按钮 tooltip 不渲染
  - **修复**：改用 `canonical.DynamicDescription`（提供变量来源）+ 正则剥离 `[...]` BBCode 标记
- **根因 3**：`LocStringPromptPatch` 匹配的 entry 名写错——`ModelDb.GetEntry` 用 `Slugify(type.Name)` → `CompositeEnchantment` → `COMPOSITE_ENCHANTMENT`（大写蛇形），合成附魔标题 key 应为 `enchantments/COMPOSITE_ENCHANTMENT.title`
  - **修复**：改为 `OrdinalIgnoreCase` 匹配 `COMPOSITE_ENCHANTMENT.`
- **教训**：publicized 程序集里 `protected` 成员都是 `public`（OnEnchant/CanonicalVars/DeepCloneFields 等），override 时必须用 `public`；**模组自定义 AbstractModel 子类会被 ModelDb 自动注册，绝不能 `new`，要用 `ModelDb.Xxx<T>().ToMutable()`**

### C9 修复 v4：卡牌克隆丢失子附魔 → 附魔失效 + 36 点伤害
- **根因**：`CardModel.DeepCloneFields`（卡牌克隆时）会克隆附魔 → `CompositeEnchantment.ClonePreservingMutability()` → 我的 `DeepCloneFields` 把 `Subs` 重置为空 → **克隆出的卡牌附魔是空合成附魔**（无子附魔）
  - 附魔效果全部失效（Instinct 减费 / Momentum 伤害加成等不触发）
  - `EnchantDamageMultiplicative` 空时返回 `originalDamage`（6）而非恒等 1 → `Hook.ModifyDamage: 6 × 6 = 36` 点伤害
- **修复**：
  - `EnchantDamageMultiplicative`/`EnchantBlockMultiplicative`：空时返回恒等 1，聚合用乘法
  - `DeepCloneFields`：改为**深拷贝子附魔**（克隆每个子附魔），不再清空
  - `CompositeEnchantmentCardClonePatch`（Harmony on `CardModel.DeepCloneFields` Postfix）：克隆后把子附魔 Card 重新挂到克隆卡（子附魔克隆后 Card 被清空，依赖 base.Card 的 Hook 会空引用）
  - `ApplyEnchantmentToCard` 返回 bool：子附魔创建失败不挂空合成附魔，`AddEnchantment` 退款不记录
  - 移除 `CanonicalVars` 聚合（子附魔描述由 ExtraHoverTips 展示，聚合有变量名冲突风险）
- **教训**：合成附魔必须正确处理 `DeepCloneFields`（克隆子附魔而非清空）；乘性修饰符空聚合时返回恒等值

### C9 修复 v5：卡面附魔文本显示原始 key → 显示子附魔名称
- **现象**：附魔腐化(Corrupted)后，卡面附加文本显示 `COMPOSITE_ENCHANTMENT.extraCardText` 而非「腐化」
- **根因**：合成附魔的 `ExtraCardText` key（`enchantments/COMPOSITE_ENCHANTMENT.extraCardText`）不存在于游戏表 → RitsuLib 返回 key 占位；`LocStringPromptPatch` 的 Postfix 未能覆盖（可能被 RitsuLib 等其它补丁的更高优先级 Postfix 覆盖，或 UpgradeLoc 加载失败时 `Get` 返回回退值）
- **修复**：
  - `LocStringPromptPatch` 加 `[HarmonyPriority(Priority.First)]`（最高优先级，确保最后执行覆盖其它 Postfix）
  - 合成附魔 title/description 用硬编码「多重附魔」回退（`UpgradeLoc.Get(key, null) ?? "多重附魔"`，不依赖 UpgradeLoc 加载）
  - `extraCardText` 经 `CompositeEnchantment.LastSummary`（子附魔名称，如「腐化」「腐化、动量」）解析——`DynamicExtraCardText` 非虚不能重写，用 Harmony Postfix on `EnchantmentModel.get_DynamicExtraCardText` 生成摘要
- **一次性诊断**：首次解析合成附魔 key 时 `GD.Print` 打印 key 与前置结果（`[IU-Loc]`），用于确认补丁是否命中

### C9 修复 v6：卡面 extraCardText 仍显示原始 key + 多附魔标记
- **根因 1（extraCardText）**：v5 的 `GetFormattedText` Postfix 只命中悬停 title（诊断 `[IU-Loc]` 只打了 title），卡面附加文本（extraCardText）的解析路径不走 `GetFormattedText` 直接入口——`SmartFormat` 内部调用 `locString.GetRawText()`（LocManager.cs:238）。补丁 `GetRawText` 才能覆盖两条路径（直接调用 + SmartFormat 内部）
  - **修复**：新增 `LocStringRawTextPatch`（Harmony on `LocString.GetRawText`，Priority.First），`ResolveComposite` 供两补丁共用；extraCardText → `CompositeEnchantment.LastSummary`（子附魔名称），title/description → 硬编码「多重附魔」
- **根因 2（多附魔标记）**：NCard 只显示一个附魔标记（`%Enchantment/Icon` 单图标）
  - **修复**：`NCardEnchantMarkersPatch`（Harmony on `NCard.UpdateEnchantmentVisuals` Postfix）——合成附魔有多个子附魔时，为第 2 个起每个子附魔追加一个图标标记堆叠在下方；`ConditionalWeakTable` 按卡牌实例跟踪，可视化更新时先清理旧标记
- **遗留风险**：`%Enchantment` 若为自动布局容器，手动定位可能被覆盖（需实测）；多标记无独立层数标签

### C9 修复 v7：附魔数值同步游戏原版
- **根因**：`CreateEnchantmentSub` 固定 `ApplyInternal(card, 1m)` → 所有附魔都是 1 层；而游戏原版施加附魔带特定数值
- **原版数值**（反编译游戏施加源确认）：
  - 动量 Momentum=**5**（PunchDagger 遗物 `DynamicVar("Momentum", 5m)`）
  - 锋利 Sharp=**2**、敏捷 Nimble=**2**（SelfHelpBook 事件）
  - 迅捷 Swift=**3**（BeautifulBracelet 遗物）
  - 阿德罗伊特 Adroit=**3**（Kifuda 遗物）
  - 强壮 Vigorous=**8**（StoneOfAllTime 事件）
  - 克隆 Clone=**4**（PaelsGrowth 遗物）
  - 其余（腐化/招架/青睐/沉眠精华等数值不随层数变化的）默认 **1**
- **修复**：`GetOriginalAmount(type)` 查表；`CreateEnchantmentSub` 用原版数值；同种叠加 `existing.Amount += sub.Amount`（每次购买施加原版数值）；附魔面板 tooltip 用 `ToMutable()` + 原版数值显示描述（否则 canonical Amount=0 显示 +0）
- **生效**：描述 `{Amount}` 与效果都显示原版数值，一致

---

## 多人状态分歧修复（方案 B，2026-08-12）

- **现象**：联机（host=储君 / client=铁甲战士，seed=JLJKNR5E036E）出现状态分歧，
  RitsuLib 报告 checksum 130 分歧（host 3280196070 / client 2200132544），唯一实质差异是
  **host 的储君身上多了 `PARRY_POWER(招架):1`，client 没有**。
- **根因**：
  1. `ApplyOnePower`（AbilityOperationHelper）反射直调 `PowerModel.ApplyInternal`，绕过游戏原生
     同步入口 `PowerCmd.Apply<T>`（后者走 Before/AfterPowerAmountChanged 等 Hook 链）
  2. `RunStateHook.OnCombatSetUp → ApplyInitialBoosts`（async void）用 `GetLocalPlayer()` 只给
     "本地玩家的角色"施加 boost，数据来自各端本地 `abilities_<seed>.json`；两端 GetLocalPlayer
     返回不同玩家 + 数据不同 → 对同一角色状态认知不同
  3. 追加分歧源：`Rng.Chaotic` 点数随机（两端值不同）、`s_combatPlayCount` 静态计数、
     多个 GetLocalPlayer 分叉（EnergyKeep/GoldOnKill/毒格挡/自动打牌等）
  4. 环境：对端缺 RitsuLib（non-gameplay，游戏不强校验），两端同步协议不对称
- **方案 B（已实施）**：把 mod 状态变成「确定性 hook / 同步 action 的派生结果」，存储只是镜像
  1. 每玩家数据（点数/技能/能力）存 RitsuLib `PlayerRunSavedData<PlayerUpgradeData>`（按 NetId 分槽）
     → `Scripts/UpgradeDataStore.cs`；静态容器降级为本地玩家缓存（Mutate 自动刷新）
  2. `ApplyOnePower` → `ApplyPower`：走 `PowerCmd.Apply<T>`（publicized 签名带 PlayerChoiceContext，
     context 兜底 SkillContextCache.Last / BlockingPlayerChoiceContext）
  3. 战斗开始三件套（boosts/stars/free_first_card）改遍历所有玩家按各自 store 施加
  4. 全部 skill/ability hook 分叉修复：主题玩家 = card.Owner/target.Player/applier.Player/forger/
     hook 参数；无主题遍历所有玩家
  5. 购买走自定义同步 action（`Scripts/Multiplayer/UpgradePurchaseAction.cs`）：
     `NetUpgradePurchaseAction`（INetAction+IPacketSerializable）+ `UpgradePurchaseAction`
     （GameAction，ActionType.Any，两端同 action id 执行同一购买）；UI 改走
     `UpgradePurchaseFlow.EnqueuePurchase`
  6. 点数随机改确定性哈希：`HashCode.Combine(StringSeed, player.NetId, ActFloor, CurrentMapCoord)`
  7. store 权威优先：`SyncOnRunStarted` —— store 有数据（RitsuLib 从 run save 恢复）用其刷新缓存，
     否则从旧 JSON（静态容器）迁移；修复读档后缓存与 store 不一致
- **行为变化（需验证）**：
  - `PowerCmd.Apply` 会触发 AfterPowerAmountChanged 等 Hook（旧反射 ApplyInternal 不触发）
    → 中毒施加可能额外触发 block_on_poison 等技能，需逐技能回归
  - 购买从同步回调改为同步 action（异步入队 + await CompletionTask），UI 有短暂等待
  - 点数发放从 Rng.Chaotic 改为确定性哈希，同 seed 重开点数一致
- **依赖**：新增 RitsuLib（NuGet `STS2.RitsuLib`，版本 `*`；manifest 声明 `STS2-RitsuLib` v0.5.11）；
  **联机双方必须装齐 mod（含 RitsuLib）**，否则 store 不可用（降级为空，购买/点数失效）
- **待办**：单人回归测试 + 双端多人联调（Step 6）
