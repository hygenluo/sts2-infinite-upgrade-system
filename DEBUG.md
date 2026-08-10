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
- 初始点数 6 → 7（`UpgradePointManager.CurrentPoints` / `InitializeDirect`）
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
- `Hook.AfterForge` → `ForgeCmd.GetSovereignBlades(forger, true)` 拿所有君王之剑
- `DynamicVars.CalculationBase.BaseValue += 1`（君王之剑格挡 = Base + Extra×Parry，bump 基础值永久 +1，随卡牌跨战斗持久化，同游戏自身 AddDamage 机制）

**测试**：储君开局 → 购买该技能 → 铸造一次（如打出带铸造的牌或触发「每打出1张牌铸造1」）→ 手牌/牌组中的君王之剑格挡 +1（铸造动画后查看卡牌数值）；多次铸造格挡累加；进入下一场战斗后格挡加成仍在（永久）。

### C5 技能「奥斯提会额外攻击一次」（亡灵契约师，15 点，id=osty_extra_attack）
- `Hook.ModifyAttackHitCount`：`attackCommand.Attacker?.Monster is Osty` 时 `__result *= 2`
- 实现为**奥斯提攻击命中次数翻倍** = 攻击两次（每次独立结算），与「这张牌额外打出1次」等效；卡牌除奥斯提攻击外的其它效果只触发一次

**测试**：亡灵契约师开局 → 购买该技能 → 战斗中打出奥斯提攻击牌 → 奥斯提连续攻击两次（伤害跳两次）；一回合内再打一张奥斯提攻击牌 → 再攻击两次。请验证伤害结算是否为两次独立攻击（格挡分别结算）。
