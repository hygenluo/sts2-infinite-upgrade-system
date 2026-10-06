# DEBUG 记录 (InfiniteUpgradeSystem)

## v2.4（第二部分）：模组配置项「是否显示悬浮窗」（2026-10-02）

### 需求
在游戏内**模组设置**里加一个开关，控制是否显示悬浮窗；并让悬浮窗位置也持久化。

### 关键决策与坑

#### E1 配置作用域选 `SaveScope.Profile`（按存档独立，用户指定）
- 数据槽：`ModDataStore.For(ModId).Register<UpgradeSettings>(key:"settings",
  fileName:"settings.json", scope:SaveScope.Profile, defaultFactory:..., autoCreateIfMissing:true)`，
  外层包 `RitsuLibFramework.BeginModDataRegistration(ModId)`（与 `UpgradeDataStore` 同一套注册作用域）。
- 注意 `ModDataStore.Register` 有 4 个重载，区别在手中是 `defaultFactory` 还是
  `syncToCloud`/`contextProvider` 在前。按教程的**具名参数**写法（`key:` / `fileName:` /
  `scope:` / `defaultFactory:` / `autoCreateIfMissing:`）编译无歧义。

#### E2 `float.NaN` 不能进配置文件（踩坑）
- 位置「未保存」最初想用 `float.NaN` 表示，但持久化走 System.Text.Json，
  默认**拒绝** NaN/Infinity（除非开 `JsonNumberHandling.AllowNamedFloatingPointLiterals`）
  —— 一旦触发保存就会抛异常，而且是在游戏存档流程里炸。
- 修复：模型里加显式标记 `HasFloatingPosition`，`FloatingX/Y` 默认 0，
  由标记决定要不要用。**规则：配置模型里只放能安全 JSON 往返的值。**

#### E3 配置生效用「写入事件 + 轮询」双保险
- `ModSettingsBindingWriteEvents.SubscribeValueWrittenWhileNodeAlive(anchor, cb)`
  能在设置页拨动开关后**同步**通知；但它需要一个场景树里的锚点节点。
  所有模组节点都是 `CallDeferred` 挂上去的，`Entry.Init()` 时还不在树里。
- 做法：锚点用 `SceneTree.Root`（永不退出场景树 = 进程级订阅，无生命周期风险）。
- 再加**兜底轮询**：悬浮窗 `_Process` 每 20 帧 `ModSettings.Refresh()` 一次。
  理由：事件订阅可能因 RitsuLib 版本或「配置读取早于档案初始化」而错过，
  轮询保证配置**最终一定生效**（最坏 0.33s，玩家感知不到）。

#### E4 档案未初始化时读配置会抛异常 → 只记一次日志
- `Entry.Init()` 早于 profile 加载，`ModSettingsValueBinding.Read()` 在这段时间可能抛异常。
- 缓存在失败时**保持上一次的值**（默认 true），并且**只记一次 Warn** ——
  否则主菜单停留期间每 20 帧刷一条日志。

#### E5 设置页文本必须用 `ModSettingsText.Dynamic`
- `ModSettingsText.Literal("显示悬浮窗")` 在解析时就把当前语言写死，语言切换后设置页不变。
- 改用 `ModSettingsText.Dynamic(() => UpgradeLoc.Get(key, fallback))`：
  复用本模组自加载的双语 `cards.json`（`has_pck:false`，游戏 LocString 表里没有本模组的 key，
  见陷阱 5），每次解析现算 → 跟随语言。

#### E6 位置读取只在「首次布局」发生
- `UpgradeFloatingButton.ApplyPosition()` 只在运行期静态位置为 NaN 时才去问配置。
- 这样 `Refresh()`（每 20 帧）读回的位置**永远不会和玩家正在进行的拖拽打架**。

### 涉及文件
- 新增：`Scripts/ModSettings.cs`（`UpgradeSettings` 模型 + `ModSettings` 读写/注册）
- 修改：`Scripts/Entry.cs`（`Register()` + `SubscribeWriteEvents()`，版本号 v57）、
  `Scripts/UiComponents/UpgradeFloatingButton.cs`（配置轮询 + 开关接入 + 位置持久化）、
  `Scripts/UiComponents/UpgradeLoc.cs`（3 个设置页 key）、
  `localization/{zhs,eng}/cards.json`、`mod_manifest.json`（2.3.0 → **2.4.0**）

### 验证
- `dotnet build -c Debug`：0 警告 0 错误，自动部署到 `mods/InfiniteUpgradeSystem/`
- 游戏内：待用户验证（设置页出现开关 / 关掉后悬浮窗消失 / 重开仍生效 / 位置跨局保持）

---

## v2.4（第一部分）：可移动悬浮窗（2026-10-02）

### 需求
屏幕上一个**可移动的小悬浮窗**，点击它唤出系统界面；并加一个模组配置项控制「是否显示悬浮窗」。

### 关键决策与坑

#### D1 悬浮窗用独立 CanvasLayer(127)，不进主面板的节点树
- 主面板是 `CanvasLayer(128)`，且 `SetUIVisible(false)` 会把**整个层**降到 `-1`。
  悬浮窗必须独立存在，否则面板一关它就跟着消失、也没法用来「点开面板」。
- 副产物：面板打开时遮罩（`ColorRect` + `MouseFilter.Stop`）在 128 层，会盖住 127 层的悬浮窗。
  由于我们是在 `_Input` 里**手动命中测试**并 `SetInputAsHandled()`，`_Input` 早于 GUI 处理，
  所以面板打开时点悬浮窗仍然有效（用来收起面板）。

#### D2 不可见时必须把 `MouseFilter` 切回 `Ignore`（踩坑）
- 第一版只在 `_Draw` 里 `if (!_active) return;`，但 `MouseFilter = Stop` 依然生效 ——
  等于在主菜单/读档界面留了一块**看不见但吃得掉鼠标事件**的 56×84 矩形。
- 修复：`Active` 属性的 setter 里同步切换 `MouseFilter`（`Stop` ↔ `Ignore`），
  并把 `TooltipText` 清空。

#### D3 不用 `Visible` 开关，用 `_active` 标志
- 依赖「节点 `Visible = false` 时 `_Process` 还会不会跑」是不确定的（不同引擎版本/节点类型
  行为不一致），一旦停跑就再也回不到可见状态。
- 做法：节点常驻 + `_active` 每帧由 `_Process` 重算；`_active == false` 时 `_Draw` 空绘制。

#### D4 拖拽 vs 点击的阈值判定
- 只在「按下」和「松手」都成立时才算点击会误判：按住微小抖动也会被当成拖拽，玩家点不开面板。
- 做法：记录按下点，位移超过 **5px** 才置 `_draggedOverThreshold`；
  松手时按该标志二选一（拖拽 → 记忆位置；点击 → 开关面板）。

#### D5 悬浮窗文字不用 `MegaLabel`，直接 `DrawString`
- `MegaLabel._Ready()` 断言 `font` 主题覆盖存在（见 UI v3 记录），所有**文本节点**必须经
  `UpgradeTheme` 工厂创建。
- 悬浮窗只需要画一串数字 + 一个矢量菱形：直接 `DrawString(UpgradeTheme.FontBold, ...)` +
  `DrawColoredPolygon/DrawPolyline`，既绕开断言又不引入节点开销，且照样跟随语言字体替换。

#### D6 选牌进行中点悬浮窗要忽略
- 选牌时面板是「隐藏但 `_isOpen` 保持」的状态（`IsSelectionInProgress`）。
  此时若走 `ToggleUI()` → `HideUI()` 会把 `_isOpen` 清掉，选牌取消链路失效（同 UI 陷阱 1）。
- 做法：新增 `UpgradeUIHandler.ToggleFromFloatingButton()`，内部先判 `IsSelectionInProgress` 直接返回。

### 涉及文件
- 新增：`Scripts/UiComponents/UpgradeFloatingButton.cs`
- 修改：`Scripts/Entry.cs`（创建实例 + 版本号 v56）、`Scripts/UpgradeUIHandler.cs`
  （`ToggleFromFloatingButton()` / `IsOpen`）、`Scripts/UiComponents/UpgradeLoc.cs`
  （`UiFloatingTooltip`）、`localization/{zhs,eng}/cards.json`

### 验证
- `dotnet build -c Debug`：0 警告 0 错误，自动部署到 `mods/InfiniteUpgradeSystem/`
- 游戏内：待用户验证（进局出现 / 可拖拽 / 点击唤出 / 回主菜单消失）

---

## v2.3：点数数值调整 + 回退系统（2026-10-01）

### C1 点数数值下调
- 新局 7 → **3**；普通怪 1~3 → **1~2**；精英 5~7 → **5~6**；BOSS 25~30 → **25~27**；
  问号房 1~5 → **1~3**；商店 1~5 → **1~3**；休息处 2~6 → **2~4**。
- 做法：把初始点数收敛成**一个常量** `PointsPersistence.StartingPoints = 3`，
  `UpgradePointManager.CurrentPoints` / `InitializeDirect` / `PlayerUpgradeData.Points` /
  `SyncOnRunStarted` 的非本地默认值全部改引用它（此前 4 处各写死 7，改一次要动四个地方）。
- 随机区间只改 `RunStateHook` 里的 `Roll(...)` 参数与 `RoomEntryPointsPatch` 的元组区间。

### C2 回退系统（返还 80% 下取整）
- **需求**：加错了 / 不需要的加点可以撤回，返还 `floor(已付 × 80%)`（10 → 8，7 → 5）。
- **实现分层**（`UpgradeRefundService` + `UpgradeDataOp.Refund` + 两个 UI 入口）：
  1. 基础属性 / 能力 / 技能：store 计数器减 1（归零则移除 key），即时生效类能力
     （hp/energy/orbSlot）额外走 `AbilityOperationHelper.ApplyImmediateRefund` 撤销即时效果；
  2. 卡牌操作：`CardOperationHelper.TryRefundCardMod` 做**精确逆操作**改卡 → 删记录 → 返点。
- **踩坑 1：升级卡牌无法安全回退**。第一直觉是用游戏的 `CardModel.DowngradeInternal()` 反向一次升级，
  但反编译（`CardModel.cs:1633`）显示它会把 `CurrentUpgradeLevel = 0` 并把
  `_dynamicVars` / `EnergyCost` / `_keywords` **整份从 canonical 重新克隆** —— 等于把整张卡重置为原型，
  连带清空同一张卡上其它所有修改（加伤、词条、耗能、附魔）。因此**升级卡牌不参与回退**，
  UI 上仍列出该行但显示「不可回退」+ 原因 tooltip。
- **踩坑 2：乱序回退会与「读档重放」结果不一致**。记录是「按顺序应用」的列表，读档时按列表重放。
  若允许乱序回退，逆操作作用在**当前状态**上会与重放结果分叉：
  例 `[添加永恒, 移除永恒]`，先回退「添加」→ 取消一个已不存在的词条（no-op），此时列表剩
  `[移除永恒]`；之后再回退「移除永恒」→ 加回永恒 → 当前状态有永恒，但重放空列表得到"无永恒" →
  **当前状态与读档后不一致**。修复：**同一张卡只允许先回退最近一次修改**（后进先出），
  这样「当前状态」恒等于「重放剩余记录」。UI 上每张卡的回退流水只列最近一次 + 备注「还有 N 次更早修改」。
- **踩坑 3：80% 下取整会让成本 1 的项返还 0**。`floor(0.8) = 0`（攻击+1 / 格挡+1）。
  设计取舍：**仍允许回退**（玩家有时就是想撤销加错的项），按钮小字显示 `+0`，
  tooltip 写明「返还 0 点（{成本} × 80% 下取整）」。
- **踩坑 4：误触损失 20%**。返还 > 0 的按钮改为**二次点击确认**：首次点击进入确认态
  （按钮转红 + tooltip 变「再次点击确认回退」），2.5 秒后自动取消（Tween 回调）。
  返还 = 0 的按钮不需要确认（没有点数可损失）。
- **踩坑 5：旧记录没有成本**。`CardModEntry` 此前只有 `Type` / `Keyword`，
  回退时算不出「当初付了多少」。新增 `Cost` 字段（v2.3 起写入）；旧记录 `Cost == 0` 时
  用新抽出的 `CardModCosts.For(type, arg)` 反查 —— 该表同时是**购买与回退的唯一成本来源**
  （此前成本字面量只写在 `PopulateItems` 里，回退再写一份必然不同步）。
- **多人一致性**：回退走同步 action（参数只有「目标 + 已付点数」），两端各自按已一致的
  store / 记录做同样的校验与撤销（目标不存在 / 类型不匹配 → 两端同样 no-op），
  因此点数与卡牌状态两端一致。战斗内面板是只读模式 → 回退按钮不显示（也避免在战斗中改 MaxHp/MaxEnergy）。
- **UI 设计**：见 UI设计.md §0.6（行内 `[−]` 按钮 + 「回退」分区流水 + 二次确认）。

---

## UI v3：字体切换为游戏自带 MegaLabel + 配色/排版重做（2026-10-01）

- **需求**：界面字体由 Godot `Label` 换成游戏自带 `MegaCrit.Sts2.addons.mega_text.MegaLabel`，
  并重新设计配色与排版。
- **调研结论（反编译 `_decompiled_sts2` + 直接扫描 `SlayTheSpire2.pck`）**：
  1. `MegaLabel._Ready()` 会执行 `MegaLabelHelper.AssertThemeFontOverride(this, "font")`
     —— **没有 `font` 主题覆盖就直接抛异常**。因此所有文本节点必须在**加入场景树之前**用工厂设好覆盖。
  2. `MegaLabel.RefreshFont()` → `ApplyLocaleFontSubstitution(FontType.Regular, "font")`：
     语言需要替换时（zhs/jpn/kor/rus/pol/tha）用 `FontManager.GetSubstituteFont()` 换字体。
     zhs Regular = `noto_sans_mono_cjksc_regular_shared.tres`（游戏中文正文），
     Bold = `source_han_serif_sc_bold_shared.tres`（思源宋体粗）。
  3. **游戏没有设置项目默认 Theme**：扫描 pck 中 `project.binary` 未找到 `gui/theme/custom`
     → `GetThemeDefaultFont()` / `GetThemeFont("font","Label")` 拿到的是 **Godot 内置字体**，
     不是游戏字体。要"游戏原生字体"必须自己按路径加载：
     主 UI 字体 = `res://themes/kreon_regular_shared.tres`（pck 内引用数 62，最多），
     Bold = `res://themes/kreon_bold_shared.tres`，Italic = `bitter_medium_italic_glyph_space_one.tres`。
  4. **`ThemeConstants` 成员名在不同游戏版本不一致**：反编译（beta）里是 camelCase
     （`ThemeConstants.Label.font`），但实际编译引用的 stable `sts2.dll` 里找不到该成员
     （CS0117，XML 里只文档化了 `RichTextLabel.AllFontSizes`）→ 代码改为直接写 Godot 标准主题项字符串
     （`"font"` / `"font_size"` / `"font_color"` / `"normal_font"` / `"default_color"`），版本无关。
  5. **字体语言替换依赖 `LocManager`，而它的初始化时机在模组加载之后**：
     `OneTimeInitialization.ExecuteEssential()` 的顺序是
     `ModManager.Initialize(...)`（→ 调用模组 `Entry.Init()`）→ **然后才** `LocManager.Initialize()`。
     模组初始化瞬间 `LocManager.Instance == null`。
- **实现**：
  - `UpgradeTheme` 重写：色板（`StsColors` 金 `#EFC851` / 米白 `#FFF6E2` / 冷灰蓝面板 `#20242B` /
    控件底 `#363D4A`，来自 pck 中 StyleBoxFlat 实测值）+ `MegaLabel`/`MegaRichTextLabel`/按钮工厂
    + 样式框工厂。字体解析：语言替换字体 → 游戏 Kreon → `ThemeDB.FallbackFont`。
  - 新增 `UiIcons`：折叠三角 / 拖拽点 / 菱形 / 叉号 / 右箭头改为 `_Draw` 矢量绘制。
  - 全部组件（条目行 / 折叠分区 / 顶栏 / 职业标签页 / 附魔弹窗 / 主面板）改用工厂创建文本。
  - 27 条新 UI 本地化 key（中英双语），分区名/职业名/按钮文字不再硬编码中文。
  - `csproj` 不再复制部署自备字体（模组 49MB → 380KB）。
- **踩坑与对策**：
  1. **几何符号字形缺失**：英文语言用 Kreon（纯拉丁字体），`▸ ▾ ◆ ✕ ⋮⋮ →` 全部缺字形 →
     渲染成缺字方框；中文语言替换成 Noto Sans Mono CJK 又有这些字形 →
     "中英表现不一致"。**对策：装饰符号一律 `_Draw` 矢量绘制**（`UiIcons`）。
  2. **字体缓存不能缓存"本地化未就绪"的结果**：`UpgradeTheme.Pick` 在 `LocManager` 未就绪时
     返回 Kreon 但**不写缓存**，下次调用重试；否则中文界面会把无 CJK 字形的 Kreon 当最终字体。
  3. **UI 建树时机**：`UpgradeUIHandler._Ready` 检测 `UpgradeTheme.LocaleReady`，
     未就绪时 `CallDeferred` 下一帧重试（上限 600 帧 ≈10s），避免中文界面出现缺字方框。
  4. **`MegaRichTextLabel` 与 `FitContent` 互斥**：`AutoSizeEnabled=true` 且 `FitContent=true` 时
     引擎会告警并强制关闭 AutoSize —— 构造时直接 `AutoSizeEnabled=false` + 显式字号覆盖。
  5. **`RichTextLabel` 需要 5 个字号主题项**（normal/bold/italics/bold_italics/mono），
     只设 `normal_font_size` 时 `[b]` 等内容会落到默认字号。
- **验证**：`dotnet build -c Debug` 0 warning 0 error；游戏内需人工确认
  （字体/配色/排版/只读/搜索/加号/MAX 徽标/附魔弹窗）。

---

## 多人联机：卡牌修改按玩家分桶 + 全玩家重放（v3 修复，2026-10-01）

- **背景**：`card_upgrades_<seed>.json` 记录本模组对卡牌做过的修改（游戏存档本身**不保存**
  "+1 攻击/词缀/合成附魔子项"这些派生修改，靠本模组重放恢复）。旧实现有两个多人缺陷。
- **缺陷 1：记录不区分玩家**。旧 `s_records` 是 `Dictionary<卡牌身份, 记录>` 的**单个扁平字典**，
  身份 = `{TemplateId}__{同类序号}`（如 `STRIKE__0`）。
  → 玩家 A 与玩家 B 的"第一张打击"身份完全相同，记录互相合并/串味
  （A 的加伤会出现在 B 的同名卡上）。
- **缺陷 2（严重）：只给本地玩家重放**。旧 `RunStateHook.OnRunStarted`：
  `var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault(); ReapplyAll(player);`
  → 读档时 **host 端只恢复 host 的卡牌修改、client 端只恢复 client 的** →
  两端牌组状态不一致 → checksum 分歧；且对端的卡牌修改在该端**永久丢失**
  （影响伤害/格挡计算，不只是显示问题）。
- **修复**：
  1. `CardUpgradeTracker` 记录改为 **NetId → 卡牌身份 → 记录** 两级字典；存档格式升到 v3
     （每条记录带 `P` = NetId），保留 v2/v1 读取兼容（旧扁平档在 RunStarted 时迁移给本地玩家）。
  2. 新增 `CardUpgradeTracker.ReapplyAllPlayers(RunState)`：**两端都为所有玩家**重放。
     重放只依赖「该玩家牌组顺序 + 该玩家自己的记录」，两端确定性一致。
  3. `GetUpgradeCount(card)` 改用 `card.Owner`（不再用本地玩家）。
  4. 所有 `RecordModification / OnCardRemoved / RemoveEnchantmentEntries` 按被修改卡的 owner 分桶。
- **顺带修复：加牌两端不一致**。旧 `CardOperationHelper.AddCardFromPool` 用
  `new System.Random()` 在**本地**选牌并直接建牌入组 → 两端各摇一次 → 加到不同的牌。
  现改为：调用端本地选牌（`System.Random` 结果仅作参数，不影响确定性模拟）→
  `UpgradePurchaseFlow.EnqueueAddCard(modelId)` → 两端用 `RunState.CreateCard` 从同一 canonical
  模型创建并 `CardPileCmd.Add(..., PileType.Deck)`（新 op `UpgradeDataOp.AddCard`）。
- **诊断增强**：CardMod action 失败（找不到卡 / 不能应用 / 点数不足）都会
  `Log.Warn("IU CardMod ...")` 打印 NetId、身份、点数 —— 两端日志可直接比对定位分歧。

---

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

---

## 多人休息处黑屏修复 + v1.4.3 优化（2026-08-12）

- **黑屏现象**：host 有 rest_all_options（休息处任意选项），联机休息处做 3 次操作（HEAL/SMITH/MEND）后退出，黑屏。
- **根因**：`SkillRestSitePatch`（ShouldDisableRemainingRestSiteOptions Postfix）用本地缓存 `SkillRegistry.Has("rest_all_options")`
  判断，但游戏按**每个玩家**调用该 hook（RestSiteSynchronizer.ChooseOption）。host 端处理 friend 的 rest site 时返回 false
  （host 缓存有技能），friend 端处理 friend 时返回 true（friend 缓存无）→ 两端对 friend 选项状态判定不同 →
  RestSiteSynchronizer 分歧（checksum 127）→ friend 断开 → host RestSiteRoom.Exit 抛 `Could not find connection for peer` → 黑屏。
- **修复**：Postfix 增加 `Player player` 参数，改用 `UpgradeDataStore.HasSkill(player, "rest_all_options")`（两端按同一玩家 store 判断）。
- **v1.4.3 优化**：
  1. **合并网络 action**：3 个 INetAction（购买/加点/扣点）合并为 1 个通用 `NetUpgradeDataAction`（Op 枚举 + 通用字段），
     `UpgradeDataAction` 按 Op 分发。mod 仅 1 个 INetAction 类型 → 两端类型集合稳定，新增操作不破坏 net-id（减少版本不兼容崩溃）。
  2. **卡牌操作同步化**：升级/攻击+/格挡+/抽牌+/重放+/耗能-/词条/删牌/附魔 全部改为「选卡 → 预校验 → 入队 CardMod action
     （原子扣点+按 CardIdentity 找卡+改卡+记录）」，两端一致执行。`CardUpgradeTracker` 方法增加 Player 参数（按 owner 记录）。
- **行为变化**：卡牌操作从「先扣点后选卡（取消退款）」改为「选卡后 action 原子扣点」（取消选卡不再扣点）。


---

## 多人中途分歧：每4张+1敏捷/力量（CombatPlayCount static 计数器不可靠）（2026-08-12）

- **现象**：v1.4.3 联机玩到中途（checksum 413）提示数据不同步；分歧为 host 玩家身上
  client 端多一个 `DEXTERITY_POWER:1`（host 端没有）。
- **根因**：host 玩家购买 `agility_every_4_plays`（每4张牌+1敏捷）技能。触发判断用
  `SkillRegistry.CombatPlayCount`（**static 每端独立计数器**）——多人下两端对"已打牌数"
  计数不可靠（ResetCombatPlay/AddCombatPlay 时序或某端漏触发），`%4` 触发时机不一致 →
  一端加敏捷、另一端不加 → 分歧。
- **修复**：`CombatPlayCount` 从 static 改为**每玩家 store 字段**（PlayerUpgradeData.CombatPlayCount）：
  `AddCombatPlay(player)` 写该玩家 store（AfterCardPlayed 两端执行 → 计数一致），
  `ResetCombatPlay()` 在 BeforeCombatStart 遍历所有玩家置 0（两端执行）；触发判断改
  `GetCombatPlayCount(player) % 4`。UI 显示保留 static 本地缓存。
- **版本**：1.4.4 / BUILD v34。


---

## 多人中途分歧：每4张+1敏捷/力量（client 端点数不足 → 购买失败 → store 不一致）（2026-08-12）

- **现象**：v1.4.4 联机中途（checksum 208）提示不同步；host 玩家身上 host 端多 `STRENGTH:1`。
- **根因链**：
  1. `RoomEntryPointsPatch` 用 `RunManager.Instance?.State` 遍历玩家 —— **client 端 AfterRoomEntered 触发时
     RunManager.State 可能未就绪** → client 端不进房加点 → host 玩家点数在 client 端 store 偏少；
  2. host 玩家购买 `strength_every_4_plays`（10 点）走同步 action，但 client 端校验
     `GetPoints(host玩家) < 10` 失败 → 购买失败 → client store 无该技能；
  3. 打牌到第 4 张时 host 端触发力量、client 端不触发 → STRENGTH 分歧。
- **修复**：
  1. `Roll` 改用 `player.RunState`（不依赖 RunManager.State 是否就绪）；
  2. `RoomEntryPointsPatch` 用 hook 的 `runState.Players` 遍历加点（client 端也能加点，点数两端一致）；
  3. 加诊断日志（SyncOnRunStarted 初始数据 / RoomEntry 加点 / 购买结果 / %4 触发）便于下次定位。
- **版本**：1.4.5 / BUILD v35。


---

## 多人中途分歧（每4张触发，client 端 CombatWon 可能不加点）（2026-08-12）

- **现象**：v1.4.5 联机中途（checksum 420）提示不同步；host 玩家身上 host 端多 `STRENGTH:1`。
- **host 端诊断**（v1.4.5 日志已确认正常）：
  - store init：两端玩家均 pts=7；
  - IU RoomEntry +3（给所有玩家）；
  - IU purchase strength_every_4_plays OK（pts_after=1，host 端点数 11）；
  - %4 hit count=8 str=True（host 端触发力量）。
- **推断根因**：`OnCombatWon` 仍用 `RunManager.Instance?.State` 遍历玩家 —— client 端 CombatWon
  触发时 RunManager.State 可能未就绪 → client 端不加战斗点数 → host 玩家点数在 client 端偏少 →
  购买 strength_every_4_plays（10）在 client 端校验失败 → 打牌第 4/8 张只 host 端触发力量 → 分歧。
- **修复**：`OnCombatWon` 玩家源改用 `RunManager.State?.Players ?? CombatManager.DebugOnlyGetState()?.Players`
  （CombatWon 时战斗状态可用），并加诊断日志（IU CombatWon）。
- **版本**：1.4.6 / BUILD v36。


---

## 多人中途分歧（Roll 用 CurrentMapCoord 两端不同步 → 点数分叉）（2026-08-13）

- **现象**：v1.4.6 联机中途（checksum 45）提示不同步；host 玩家身上 host 端多 STRENGTH/敏捷。
- **朋友日志对比（决定性证据）**：
  - friend 为 BUILD v36（v1.4.6），store init 两端均 pts=7（初始一致）；
  - **RoomEntry Event 加点：host 端 host 玩家 +2，friend 端 host 玩家 +1** → 两端对同一玩家 Roll 值不同；
  - 此后 host 玩家点数在两端分叉 → 后续购买（strength/agility）在 friend 端校验结果可能不同 →
    每4张触发只一端 → STRENGTH 分歧。
- **根因**：`Roll` 用 `HashCode.Combine(seed, NetId, ActFloor, CurrentMapCoord)`，但 **AfterRoomEntered 触发瞬间
  两端 CurrentMapCoord/ActFloor 可能未同步**（一端已进新房、另一端还没）→ Roll 值两端不同。
- **修复**：`Roll` 改用 RitsuLib **每玩家确定性 RNG 流** `ModRunRngRegistry.Get(player, modId, "points")`
  （随 run save 保存、两端派生一致、不依赖坐标同步）；RoomEntry/CombatWon 两端同步触发 → 消耗同一流一致。
- **版本**：1.4.7 / BUILD v37。


---

## 多人分歧排查工具：分歧状态转储 + 策略反思（2026-08-13）

- **现象**：v1.4.7 后 Roll/点数/购买两端已一致，但分歧仍发生在「每4张触发施加 str+agi 后」（checksum 12）。
  RitsuLib 分歧 zip 未生成 → 无字段。
- **新增排查工具**（`Scripts/Patches/DivergenceDiagnostics.cs`）：Harmony patch
  `ChecksumTracker.CompareChecksums`（host 检测分歧）/ `OnReceivedStateDivergenceMessage`（client 收到分歧），
  在分歧时打印本端各玩家 powers / 点数 / 技能 / 计数，两端日志对比即可精确定位。
- **策略反思（官方同步模型）**：STS2 多人 = host 权威 action 广播 + 两端确定性模拟 + checksum 校验；
  「状态 = action 流的确定性函数」，任何只在单端执行/依赖单端独有数据的修改必然分歧。
  本 mod 数据（点数/技能/能力/计数）一致性依赖「所有修改两端严格同步」的脆弱链条（点数发放不走 action、
  购买校验依赖 store、有状态计数），任何一环单端出错即永久分叉。**根本方向**：所有数据变更收口到
  host 权威 action + 确定性初始强制（方案待诊断结果后实施）。
- **版本**：1.4.8 / BUILD v38。


---

## 多人分歧：每4张+力量（fire-and-forget async 施加与 checksum 时序竞争）（2026-08-13）

- **现象**：v1.4.8 联机（checksum 5）分歧。RitsuLib report 确认：host 端 host 玩家只有
  `DEXTERITY:1`，friend 端有 `DEXTERITY:1 + STRENGTH:1`（怪物仍活着，非战斗结束）。
- **根因**：`%4 hit`（每4张+力量/敏捷）的施加在 `SkillCombatPatches.HandleAsync`（**fire-and-forget
  async**）里 `await ApplyPower`（PowerCmd.Apply 内部 `CustomScaledWait` 真实 await，跨帧）→
  **施加与打牌 action 的 checksum 生成存在时序竞争**，两端 ApplyPower 落地时机可能不同 →
  一端有力量、一端没有（分歧转储在分歧后打印，两端均已补齐，故显示一致）。
- **修复**：`%4 hit` 的施加改走**同步 action**（`UpgradeDataOp.ApplyEvery4`，host/singleplayer 入队
  `EnqueueApplyEvery4(owner)`，ActionExecutor 执行并等待 → checksum 在其后）→ 两端一致落地。
- **已知同类风险**：其他「打牌/回合触发」里 `await ApplyPower` 的效果（vigor_on_skill_play、
  poison_all_on_card_play、str_at_turn_start、free_first_card 等）同为 fire-and-forget async，
  若发生分歧按同样模式走 action（分歧转储可定位）。
- **版本**：1.4.9 / BUILD v39。


---

## 多人分歧：ApplyEvery4 fire-and-forget 入队 → 两端 action 流错位（2026-08-13）

- **现象**：v1.4.9 联机（checksum 19）分歧，context 为 `UpgradeDataAction op=ApplyEvery4`。
  两端 checksum 编号错位（friend 端 host 玩家的 ApplyEvery4=checksum19，host 端 friend 玩家的=19）
  → 两端 action 序列本身不同。
- **根因**：`%4 hit` 判断与入队放在 fire-and-forget `HandleAsync` 里（AfterCardPlayed Postfix → `_ = HandleAsync`），
  **入队时机两端可能不同**（HandleAsync 异步启动）→ 插入 action 队列的位置不同 → action 流错位 → 分歧。
- **调研结论（官方/热门模组）**：STS2 无「每玩家任意数据 + 局中实时同步」官方机制；唯一被同步+校验的是
  挂到模型（卡/遗物/附魔）的 `SavedProperties`（RitsuLib SavedAttachedState / BaseLib SavedSpireField），
  Player 不是模型无法挂。官方范式 =「效果在 action 管线内执行、计数用确定性来源」；
  游戏有确定性打牌数 `CombatManager.History.CardPlaysStarted`（官方 Normality 同款）。
- **修复**：`%4 hit` 改为**在 Postfix 同步部分**用 `History.CardPlaysStarted` 确定性计数（按打牌玩家过滤），
  由 host/singleplayer 入队 `ApplyEvery4` action → 两端在同一 action 流位置入队 → 一致。
- **已知同类风险**：其他「战斗内 fire-and-forget async 施加 power」（vigor_on_skill_play、poison_all、
  str_at_turn_start 等）因 PowerCmd.Apply 内部 CustomScaledWait 跨帧 await，理论上同样可能时序分叉；
  若发生，按同样模式走 action。
- **版本**：1.4.10 / BUILD v40。


---

## 多人分歧：ApplyEvery4 广播在 client 端丢失 → 两端执行次数不同（2026-08-13）

- **现象**：v1.4.10 联机（checksum 56）分歧。转储+report：friend 端 host 玩家 STRENGTH:2、host 端 1
  （两端 pts=4 cp=4 skills 一致，仅 power 层数不同）。
- **根因**：ApplyEvery4 由 host 入队广播，但 **friend 玩家的 action 在 client 端丢失**——host 在打牌 action
  内的 AfterCardPlayed Postfix 里入队时，`RunLocationTargetedMessageBuffer` 的 currentLocation 与消息
  Source Location 不一致（client 端尚未推进到新战斗）→ 消息被缓冲/丢弃 → client 端该 action 不入队
  → 两端 action 流数量不同 → 后续 STRENGTH 施加次数不同。
- **修复（放弃广播，两端本地确定性同步）**：`%4 hit` 改为 **Postfix 同步部分两端本地同步施加**
  `ApplyPowerSync`（publicized `ModelDb.Power<T>().ToMutable().ApplyInternal`，无 await、无广播、
  已有同类叠加）；计数用 `CombatManager.History.CardPlaysStarted`（确定性）。
  AfterCardPlayed 两端确定性触发 + 确定性计数 + 同步施加 → 两端一致。
- **关键认知**：战斗内「Hook 同步部分 + 无 await 施加」比「入队 action」更可靠（广播受 location
  缓冲时序影响；async 受 checksum 时序影响）。
- **版本**：1.4.11 / BUILD v41。


---

## 多人分歧：「自动打出能力牌」fire-and-forget AutoPlay → 官方范式 CustomPowerModel（2026-08-13）

- **现象**：v1.4.11 联机（checksum 515）分歧。report：friend 端 host 玩家多 `INFINITE_BLADES_POWER:1`
  （无尽刀刃，游戏原版能力牌）——host 玩家买了 auto_play_power_on_draw（抽到能力牌自动打出），
  抽到无尽刀刃后 friend 端自动打出了、host 端没有。
- **根因**：旧实现为 Harmony `Hook.AfterCardDrawn` Postfix（fire-and-forget）+ `await CardCmd.AutoPlay`
  （内部 OnPlayWrapper 跨帧 await）→ 与 checksum 时序竞争 → 一端自动打出、一端没有。
- **官方范式（借鉴）**：地狱狂徒 `HellraiserPower` 是 PowerModel，其 `AfterCardDrawnEarly` 在游戏抽牌管线内
  **被 await**（`Hook.AfterCardDrawn` 遍历模型并 await）→ 两端确定性一致。
- **修复**：新建 `AutoPlayPowerModel : CustomPowerModel`（IsVisibleInternal=false 隐藏），
  `AfterCardDrawnEarly` 里对能力牌 `await CardCmd.AutoPlay`；战斗开始 `ApplySkillCombatStartPowers`
  给已购技能玩家施加（PowerCmd.Apply，CombatSetUp 时无跨帧 await）；删除旧 fire-and-forget patch。
- **关键认知**：**效果做成模型（power/relic）的 hook 回调 = 官方确定性范式**（游戏管线 await）；
  Harmony Patch + fire-and-forget async = 时序竞争。后续其他技能若再分歧，应优先改为模型 hook。
- **版本**：1.4.12 / BUILD v42。


---

## v2.0：统一多人安全方案 —— 一个隐藏 Power 承载所有技能/能力效果（官方范式重构）（2026-08-13）

- **反复分歧根因**：技能/能力效果做在 Harmony Hook Postfix + **fire-and-forget async**（`_ = HandleAsync`）里，
  内部 Command（PowerCmd.Apply / CardCmd.AutoPlay / PlayerCmd.GainGold 等）有跨帧 await → 与 checksum 时序竞争 → 两端落地时机不同 → 反复分歧（每4张、自动打出、金币…）。
- **官方范式（确认）**：`Hook.AfterXxx` 遍历 `combatState.IterateHookListeners()`（含 creature 的 powers）
  并在游戏 action 管线内 await 每个模型 → 效果做成**模型（power）的 hook 回调 = 两端确定性**（参照地狱狂徒）。
- **重构**：
  - 新增 `Scripts/Skills/SkillEffectsPower.cs`（CustomPowerModel，隐藏、Single）：override 全部技能 hook
    （AfterCardPlayed / AfterHandEmptied / AfterCardExhausted / AfterPlayerTurnStart / AfterSideTurnStart /
    BeforeSideTurnEnd / AfterCardDrawnEarly / AfterDamageReceived / AfterCardDiscarded / AfterPowerAmountChanged /
    AfterForge / AfterDeath / ModifyAttackHitCount），内部按「主题玩家（power 所属玩家）已购技能」施加；
    每4张用 `CombatManager.History.CardPlaysStarted` 确定性计数 + `ApplyPowerSync`（无 await）。
  - `RunStateHook.ApplySkillCombatStartPowers` 给「有任意技能/能力」的玩家施加 SkillEffectsPower。
  - **删除**全部 fire-and-forget async patch：`SkillCombatPatches.cs`、`AutoPlayPowerModel.cs`、`GoldOnKillPatch.cs`；
    `SkillEventPatches.cs` 精简为仅 `SkillContextCache` + `SkillRestSitePatch`（同步 bool）。
  - 保留：EnergyKeepPatch（同步）、CombatSetUp 能力 boost、卡牌修改/购买/加点（action）、点数发放（确定性 RNG 流）。
- **结果**：所有技能/能力效果统一走「游戏管线内 await 的模型 hook」→ 多人确定性一致，代码集中（SkillEffectsPower ~230 行替代 3 个 patch ~500 行）。
- **版本**：2.0.0 / BUILD v50。


---

## v2.0 多人联机验证通过 + 发布（2026-08-13）

- 双端实测：完整一局正常游玩（host 多场战斗胜利、点数正常增长），**0 状态分歧、0 NullReferenceException**。
- BaseLib 版本修复生效：friend 升级 Workshop BaseLib 3.4.4 后，卡牌预览 NRE 消失（此前 3.4.0 的
  `ModifyBaseDamagePatches.AdjustBaseUnenchanted` 在刷新攻击牌时 NRE → 牌堆空、抽牌中断）。
- 部署产物：InfiniteUpgradeSystem.dll + localization（cards/powers）+ resources；manifest 2.0.0，依赖 BaseLib + STS2-RitsuLib。
- 发布 v2.0.0（创意工坊物品 3763412710 更新）。


---

## v2.0 多人联机验证通过 + 发布（2026-08-13）

- 双端实测：完整一局正常游玩（host 多场战斗胜利、点数正常增长），0 状态分歧、0 NullReferenceException。
- BaseLib 版本修复生效：friend 升级 Workshop BaseLib 3.4.4 后，卡牌预览 NRE 消失（此前 3.4.0 的 ModifyBaseDamagePatches.AdjustBaseUnenchanted 在刷新攻击牌时 NRE → 牌堆空、抽牌中断）。
- 部署产物：InfiniteUpgradeSystem.dll + localization（cards/powers）+ resources；manifest 2.0.0，依赖 BaseLib + STS2-RitsuLib。
- 发布 v2.0.0（创意工坊物品 3763412710 更新）。

---

## 奥斯提额外攻击一次不生效：attacker.Player 恒为 null（pet 无 Player）（2026-08-14）

- **现象**：亡灵契约师技能「奥斯提会额外攻击一次」（skill id `osty_extra_attack`）购买后无效果，奥斯提攻击命中次数仍是 1。
- **复现**：购买技能（log 有 `IU purchase osty_extra_attack cost=15 ... OK`）→ 打出奥斯提攻击牌（Poke/Flatten/Fetch 等）→ 奥斯提只打一次。
- **根因**：v2.0 把该技能从 Harmony `Hook.ModifyAttackHitCount` Postfix 迁移到 `SkillEffectsPower.ModifyAttackHitCount`
  （CustomPowerModel 官方范式）时，用 `attack?.Attacker?.Player` 定位主题玩家。但奥斯提是**召唤物（pet）**：
  `AttackCommand.FromOsty(osty, card)` 只设 `Attacker = osty`（Creature 的 `Player` 字段恒为 null，`Monster is Osty` 才是身份），
  所以 `attack.Attacker.Player == null` → 第一行 `owner == null` 直接 `return hitCount`，永不翻倍。
- **修复**：改为用 `attack.Attacker.PetOwner` 定位召唤者（OstyCmd.Summon → PlayerCmd.AddPet →
  PlayerCombatState.AddPetInternal 会设 `pet.PetOwner = 召唤者`；OstyCmd 里也用 `c.PetOwner == summoner` 找奥斯提）。
  判定顺序改为：先判 `Monster is Osty`，再取 `PetOwner` 比对 `OwnerPlayer`。
- **多人安全**：`ModifyAttackHitCount` 是同步的模型 hook（`Hook.ModifyAttackHitCount` 遍历 `IterateHookListeners`
  在内联数值计算，无 await）→ 两端确定性一致；`PetOwner` 由同一 `OstyCmd.Summon` 命令两端同步设置；
  技能等级读 `UpgradeDataStore`（RitsuLib 每玩家 store，host 权威）→ 满足多人确定性要求。
- **版本**：未 bump（bugfix）。

---

## update06：多人适配 + 2 BUG 修复 + 12 新技能（2026-08-16）

### 本此反编译确认的 API 变更 / 坑

1. **`CardPileCmd.AddGeneratedCardToCombat` 第三参数已变**：旧反编译源码是 `bool addedByPlayer`，
   当前公开化程序集是 `Player? creator`。旧源码调用报 `CS1739`（无 addedByPlayer 参数）。
   正确调用：`AddGeneratedCardToCombat(card, PileType.Hand, player)`。
   （教训：`_decompiled_sts2` 是 update04 时代的旧源码，签名可能过期，报错时以 `ilspycmd` 现反编译为准。）

2. **`CardModel.ExhaustOnNextPlay`（public set）**：设 `true` 后该牌打出即进消耗堆（`GetResultPileType()`
   检测 `ExhaustOnNextPlay || Keywords.Contains(Exhaust)`）。用于「回合结束打出消耗堆虚无牌」——否则
   `CardCmd.AutoPlay` 的虚无牌打出后进弃牌堆（用户反馈）。

3. **`PowerModel.InitInternalData` 在公开化程序集里是 `public`**：`protected override` 报 `CS0507`，
   须 `public override object InitInternalData()`。

4. **「下一张牌免费」不能用 VoidFormPower / 干瘪之手**：
   - 干瘪之手 = 随机一张手牌免费（`SetToFreeThisTurn`），不符「下一张」语义；
   - VoidFormPower = 「本回合前 N 张免费」（`cardsPlayedThisTurn` 计数所有牌），能力牌触发会错位；
   - 最终方案：`SkillEffectsPower` 内部 `freeCardCharge`（`InitInternalData`）+ 重写 `TryModifyEnergyCostInCombat` /
     `TryModifyStarCost` 把手牌置 0 费 + `AfterCardPlayed`（能力牌置1/非能力牌置0）+ `AfterPlayerTurnStart` 清空。

5. **负力量**：`StrengthPower.AllowNegative=true`，`PowerCmd.Apply<StrengthPower>(ctx, enemy, -N, ...)` 可减力量。
   封装 `AbilityOperationHelper.ApplyStrengthLoss`（现有 `ApplyPower` 有 `count<=0` 早退，负值须走独立方法）。

### 新增 Power 映射（`AbilityOperationHelper.ApplyPower` switch）
- `"vulnerable" => VulnerablePower`（铁甲：消耗→易伤、易伤总和→力量）
- `"fanOfKnives" => FanOfKnivesPower`（静默：小刀全体攻击——Shiv 检测该 power 即 `TargetType.AllEnemies`，纯被动）

### 新增/复用模型 hook
- `AfterStarsGained(int amount, Player gainer)`（储君：获辉星→全体敌人失力）
- `AfterPowerAmountChanged` 新增 `DoomPower` 分支（亡灵：给灾厄→该敌人失力）
- `BeforeSideTurnEnd` 新增「打出消耗堆虚无牌（ExhaustOnNextPlay）」「消耗手牌状态牌每张+2格挡」
- `AfterPlayerTurnStart` 新增「x伤害y次(x=力量+1,y=敏捷+1)」「升级过的无色牌」「随机能力牌」

### 官方参考实现
- 无色牌/随机能力牌：`CardFactory.GetDistinctForCombat(player, pool.GetUnlockedCards(...), 1, player.RunState.Rng.CombatCardGeneration)`
  + `CardCmd.Upgrade`（升级）/ `CardCmd.AutoPlay`（打出）——参照 WhiteNoise / InfernalBlade。
- 多人确定性：一律 `player.RunState.Rng.*` 同步流，禁止本地随机。

---

## 正式版(stable)兼容：CardPlay.Player 不存在（2026-08-16）

- **现象**：测试版验证通过；正式版加完点打一张牌后，卡牌一直停在页面中间、不进弃牌堆也不消失。
- **根因**：正式版的 `CardPlay` 类型**移除了 `Player` 属性**（测试版有）。模组按测试版编译，正式版运行时
  `CardPlay.get_Player()` 抛 `MissingMethodException`（godot.log 可见）→ `SkillEffectsPower.AfterCardPlayed` 中断
  → 打牌 action 以异常结束，卡牌流程卡死。
- **修复**：`cardPlay.Player` → `cardPlay.Card.Owner`（`CardPlay.Card` 与 `CardModel.Owner` 在测试版/正式版均存在）。
  共两处：`AfterCardPlayed` 定位主题玩家、每4张计数 `e.CardPlay?.Player` → `e.CardPlay?.Card?.Owner`。
- **教训**：测试版(beta)与正式版(stable)的 API 有差异。跨版本 API 一律优先用两端都存在的成员（如 `Card.Owner`），
  避免只存在于单一分支的便捷属性（如 beta 的 `CardPlay.Player`）。
- **另一处差异**：`CardCmd.Exhaust` 返回类型——测试版 `Task<CardPileAddResult?>`，正式版 `Task`。返回类型也是 CLR 方法签名
  的一部分，直接 `await CardCmd.Exhaust(...)` 按当前分支的返回类型生成 IL 引用，在另一分支抛 `MissingMethodException`
  （表现为"无法结束回合"）。修复：`AbilityOperationHelper.ExhaustCard` 用反射按**参数签名**查找 `Exhaust`，
  无论返回 `Task` 还是 `Task<T>` 都 await 其 `Task` 基类，两端兼容。



