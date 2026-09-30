# 无限升级系统 UI 重构设计方案

> **状态**：设计契约已与用户确认（2026-08-04，grill-me 访谈产出）
> **当前版本**：UI v1（扁平按钮列表）→ UI v2（属性面板式）→ **UI v3（游戏原生字体 + 冷灰蓝/金配色，2026-10-01）**
> **实现进度**：Phase 0-5 全部完成（2026-08-04）；技能系统 Phase S1-S4 全部完成；
> **v3 字体/配色/排版重构已完成并编译通过（2026-10-01）**，详见下方 §零。
> **用途**：本文件是 UI 重构的实现依据 + 未来新增「技能」等内容的扩展指南。后续 AI 会话涉及本 mod UI 或技能设计时，以此文档为准。
> **v3 变更摘要**：字体改用游戏自带 `MegaLabel` / `MegaRichTextLabel`（不再打包字体），
> 配色改为游戏原生（金 #EFC851 / 米白 #FFF6E2 / 冷灰蓝 #20242B），排版与图标全面重做。

---

## 零、UI v3：游戏原生字体与配色（2026-10-01）

### 0.1 动机
v2 自备并打包了 49MB 思源宋体（MSDF 手动加载）来解决高分屏发糊。v3 改为**直接使用游戏自带字体体系**：
体积从 49MB 降到 0，且与游戏其它界面完全统一（含语言切换）。

### 0.2 字体
| 用途 | 来源 |
|---|---|
| 文本控件 | 游戏自带 `MegaCrit.Sts2.addons.mega_text.MegaLabel` / `MegaRichTextLabel` |
| 非 CJK 语言 | `res://themes/kreon_regular_shared.tres`（Regular）/ `kreon_bold_shared.tres`（Bold）/ `bitter_medium_italic_glyph_space_one.tres`（Italic） |
| zhs / jpn / kor / rus / pol / tha | 游戏 `FontManager.GetSubstituteFont()`（zhs Regular = Noto Sans Mono CJK SC，Bold = 思源宋体 SC Bold） |
| 兜底 | `ThemeDB.FallbackFont` |

> **重要**：`MegaLabel._Ready()` 断言 `font` 主题覆盖存在，否则抛异常 →
> 所有文本必须经 `UpgradeTheme.Label/AutoLabel/RichLabel/TextButton` 工厂创建（加入场景树前设好覆盖）。
> **为什么不用 `GetThemeDefaultFont()`**：游戏没有设置项目默认 Theme（pck 里没有 `gui/theme/custom`），
> 该 API 只能拿到 Godot 内置字体，不是游戏字体。

### 0.3 色板（`UpgradeTheme`，可一处切换风格）
| 用途 | 值 | 来源 |
|---|---|---|
| 全屏遮罩 | `#000000` @72% | 游戏 `screenBackdrop`（80%）略轻 |
| 面板底 | `#20242B` | 游戏控件底 `#363D4A` 的暗化版 |
| 顶栏/分区头 | `#272D37` | 同上略亮一档 |
| 条目行（斑马纹） | `#242932` / `#282E38` | — |
| 条目行悬停 | `#333C49` | — |
| 控件底（按钮/输入框） | `#363D4A` | pck 中 StyleBoxFlat 实测（Button/LineEdit normal） |
| 描边 | `#454E5C` | — |
| 强调金 | `#EFC851` | `StsColors.gold` |
| 正文米白 | `#FFF6E2` | `StsColors.cream` |
| 次级文字 | `#A7B0BD` | — |
| 成本数字 | `#D9C58A` | — |
| 危险红 | `#FF5555` | `StsColors.red` |
| 成功绿 | `#8ED46A` | — |

### 0.4 排版
```
┌───────────────────────────────────────────────────────────────┐
│ ⣿  无限升级系统           [只读]   ◆ 42            ✕          │ 顶栏(可拖拽, 固定)
├───────────────────────────────────────────────────────────────┤
│ [ 搜索加点项目…                                             ] │ 搜索框(固定)
│                                                               │
│ ▶ 基础属性                        8 项 · 已投入 3            │ 分区头(三角+标题+摘要)
│ ▼ 能力                            6 项 · 已投入 1            │
│   初始力量              3    [ 10 ]   ( + )                   │ 条目行: 名称/值/成本徽标/加号
│   格挡跨回合不消失      已拥有 [MAX]                          │ 满级 → 金色 MAX 徽标
│ ▶ 技能                            12/43                      │
│ ▶ 牌组                            24 张            →          │ 入口行
│ ▶ 测试                            3 项                       │ 弱化样式
├───────────────────────────────────────────────────────────────┤
│ [P] 打开/关闭    [Esc] 关闭                                   │ 页脚提示
└───────────────────────────────────────────────────────────────┘
```
- 面板 **720×640**；分区/条目行圆角 5，面板圆角 8 + 投影
- 成本为**徽标**（金边小圆片）：点数不足 → 红边红字 + 加号灰化（仍可点，触发抖动提示）
- 满级（技能 / 格挡跨回合 / 能量跨回合）→ 加号替换为**实心金 MAX 徽标**
- **装饰符号全部矢量绘制**（`UiIcons`：三角/拖拽点/菱形/叉号/箭头）——
  英文语言下游戏主字体 Kreon 没有 `▸ ▾ ◆ ✕ ⋮⋮ →` 这些字形，用文字会渲染成缺字方框

### 0.5 本地化
UI 文本全部走 `UpgradeLoc`（27 条新 key，`localization/{zhs,eng}/cards.json` 双语），
分区名 / 职业名 / 按钮 / 占位符 / 提示 / 附魔弹窗文案不再硬编码中文。

---


## 一、设计目标

1. **氛围感优先**：贴 STS2 原版质感（暗色羊皮纸 + 金色描边 + 符文感），与游戏界面风格一致
2. **信息架构重构**：从「扁平按钮列表」改为「属性面板」——名称 + 当前值 + 成本 + 加号
3. **钻入式导航**：主面板（属性类）↔ 牌组子面板（卡牌操作类）整页切换
4. 打包中文字体，解决 Godot 默认字体渲染中文导致的简陋感

---

## 二、设计契约（已确认，不可随意更改）

| 项 | 决定 |
|---|---|
| 视觉 | 贴原版质感；打包中文字体（**Adobe 官方思源宋体 SC 静态 OTF** + **MSDF 渲染**，见 DEBUG.md Phase 0.5） |
| 面板尺寸 | 固定尺寸（640×620 级），固定高度，内容内部滚动 |
| 顶部栏 | 拖拽手柄 + 标题 + **金色点数计数器** + 关闭按钮（固定不随内容滚动） |
| 分区 | 基础属性(8) / 能力(5) / 技能(6 标签页, 空态) / 牌组(入口) / 测试(3, 弱化) |
| 条目行 | 名称 + 当前值 + **成本小字（始终可见）** + 加号按钮 |
| 成本状态 | 点数不足：成本变红 + 加号置灰禁用 |
| 限等级项 | 用 **int 等级**存储（最大等级=1，**不用 bool 类型**，便于未来支持多级技能）；达到最大等级后加号消失，变为 **MAX 徽标** |
| 折叠 | 分区**默认全部收起**；展开/收起状态在游戏运行期间记忆 |
| 面板位置 | 拖拽后的位置在游戏运行期间记忆 |
| 牌组子面板 | 整页切换 + 「← 返回」；**操作优先**（先点操作再选牌）；29 项卡牌操作 + 删除卡牌 |
| 选牌提示 | 每项操作**专属提示文案**（本地化 key），不再统一显示「选择一张牌升级」 |
| 取消链路 | 游戏选牌界面自带取消（左侧返回图标 / Esc）→ 点数自动返还 → **恢复牌组子面板** |
| 搜索 | 全局搜索保留；命中自动展开分区 + 滚动定位 + 高亮命中文本 |
| 动效 | 全做：开合淡入上移 / 分区展开动画 / 悬停金色描边 / 购买成功 +1 飘字 / 点数不足加号抖动 |
| 技能分区 | 16 项初步设想已入档（见 §4.4，来源 `update/技能.md`），全部限等级=1；6 个二级标签页 |
| 测试分区 | 默认收起，样式弱化 |

---

## 三、界面结构

### 3.1 主面板

```
┌─────────────────────────────────────────────┐
│ ⋮⋮ 无限升级系统           ◆ 当前点数: 42   ✕ │ ← 顶部栏(固定, 可拖拽)
├─────────────────────────────────────────────┤
│ [🔍 搜索加点项目...              ]           │ ← 搜索框(固定)
├─────────────────────────────────────────────┤
│ ▶ 基础属性                    (收起,默认)   │ ← 折叠分区头
│ ▶ 能力                                       │
│ ▶ 技能                        (6 标签页)    │
│ ▶ 牌组                     当前卡牌数: 12  → │ ← 入口行,点击切入子面板
│ ▶ 测试                        (弱化样式)    │
└─────────────────────────────────────────────┘

展开后的分区内：
│ ▼ 基础属性                                   │
│   ├─ 初始力量        3     10    [+ ]        │  ← 名称 + 当前值 + 成本 + 加号
│   ├─ 初始敏捷        1     10    [+ ]        │
│   ├─ 格挡跨回合不消失   ✓    25    [MAX]      │  ← 布尔项已购 → MAX 徽标
│   └─ ...                                     │
```

### 3.2 牌组子面板（整页切换）

```
┌─────────────────────────────────────────────┐
│ ← 返回主面板                     ✕           │ ← 顶部返回栏
├─────────────────────────────────────────────┤
│ [🔍 搜索卡牌操作...            ]             │
├─────────────────────────────────────────────┤
│ 升级卡牌       [5]  [+ ]                     │
│ 攻击+1         [1]  [+ ]                     │
│ 格挡+1         [1]  [+ ]                     │
│ ...（29 项卡牌操作 + 从牌组删除一张牌）        │
└─────────────────────────────────────────────┘
点击操作项 → 隐藏本面板 → 游戏选牌界面（显示该操作的专属提示文案）
  → 选中卡牌生效 | 取消（左侧返回/Esc）→ 点数返还 → 回到本子面板
```

---

## 四、数据模型设计

### 4.1 条目定义（`Scripts/UiComponents/UpgradeItemData.cs` 扩展）

```csharp
public enum UpgradeItemKind { Stat, Action }   // Stat: 数值型（可带最大等级）; Action: 一次性动作（卡牌操作）

public sealed class UpgradeItemDef
{
    public string Category;        // 基础属性 / 能力 / 技能 / 牌组 / 测试
    public string? SubCategory;    // 仅技能：通用/铁甲战士/静默猎手/储君/亡灵契约师/故障机器人
    public string DisplayName;     // 显示名称（无本地化 key 时回退）
    public string? LocKey;         // 名称本地化 key（可选）
    public int Cost;               // 点数消耗
    public UpgradeItemKind Kind;   // 条目类型
    public int MaxLevel;           // 最大等级；<=0 = 无上限（基础属性/能力）；技能 = 1
    public Func<int>? LevelProvider; // 当前等级：能力 → AbilityOperationHelper.GetBoost(key)；
                                   // 技能 → SkillRegistry.GetLevel(id)
    public Func<string>? ValueText;  // 当前值显示文本（缺省时由 LevelProvider 自动生成）
    public string? PromptKey;        // 仅 Action 型：选牌专属提示本地化 key
    public Func<Task<bool>> OnClick; // 返回 false = 取消/失败（内部负责退款）
}
```

> **等级化原则（重要）**：所有条目统一用 **int 等级**存储与显示，**不存在 bool 类型**。
> - 无上限项：等级即已购次数（力量 = GetBoost("strength")）
> - 限等级项：等级达到 `MaxLevel`（当前技能均为 1）→ 渲染 MAX 徽标
> - 未来技能要支持叠层/升级（如「每打出1张牌获得2格挡」），只需把该技能 `MaxLevel` 提到 2+ 并让效果逻辑读取等级，**UI 与持久化零改动**。

### 4.2 条目类型的渲染规则

| Kind | 当前值显示 | 加号 | 满级 |
|---|---|---|---|
| Stat 无上限 | 等级数字，如「3」 | 可重复购买，永不加满 | — |
| Stat 限等级（`MaxLevel>0`） | 等级数字，如「1 / 1」 | `等级 >= MaxLevel` 时加号消失 | 变 **MAX 徽标**（金底深字） |
| Action（卡牌操作） | 显示「—」或不显示 | 可重复购买 | — |

禁用规则（所有类型）：`当前点数 < Cost` → 成本变红 + 加号置灰；点击时抖动提示。

### 4.3 现有 46 项 → 新分区归属（成本沿用当前代码值，不调整）

**基础属性（8 项，Stat）**——value 来源 `AbilityOperationHelper.GetBoost(key)`：

| 条目 | key | 成本 | 当前值 |
|---|---|---|---|
| 初始力量 | strength | 10 | 已购层数 |
| 初始敏捷 | dexterity | 10 | 已购层数 |
| 初始集中 | focus | 15 | 已购层数 |
| 初始覆甲 | plating | 8 | 已购层数 |
| 初始荆棘 | thorns | 10 | 已购层数 |
| 初始人工制品 | artifact | 20 | 已购层数 |
| 生命上限 | hp | 5 | 已购层数 |
| 充能球栏位 | orbSlot | 10 | 已购层数 |

**能力（5 项）**：

| 条目 | key | 成本 | 类型 | 当前值 |
|---|---|---|---|---|
| 每回合能量 | energy | 30 | Stat | 已购层数 |
| 每回合辉星 | stars | 15 | Stat | 已购层数 |
| 每回合铸造 | forge | 15 | Stat | 已购层数 |
| 格挡跨回合不消失 | blockKeep | 25 | 限等级(Max=1) | `GetBoost("blockKeep") >= 1` → MAX |
| 能量跨回合不消失 | energyKeep | 25 | 限等级(Max=1) | `GetBoost("energyKeep") >= 1` → MAX |

**牌组（入口行）**：右侧显示当前卡牌数量（`player.Deck.Cards.Count`）。

**测试（3 项，弱化样式，默认收起）**：点数+1 / +5 / +10。

**牌组子面板（30 项，Action）**：29 项卡牌操作 + 「从牌组删除一张牌」，每项带专属 `PromptKey`（见 §5.4）。

### 4.4 技能（16 项，全部 MaxLevel=1）— 来源 `update/技能.md`（2026-08-04 初步设想）

> 技能数值只影响局内；level 取数来源 `SkillRegistry.GetLevel(id)`；id 为建议命名，实现时统一。

**通用**

| id | 条目 | 成本 | 效果实现要点（建议） |
|---|---|---|---|
| block_on_play | 每打出1张牌，都获得1格挡 | 10 | 打出牌 Command Postfix → `PlayerCmd.GainBlock` |
| agility_every_4_plays | 每当打出4张牌，获得1敏捷 | 10 | 打出牌 Postfix + 计数器（局内计数值，战斗内累计） |
| strength_every_4_plays | 每当打出4张牌，获得1力量 | 10 | 同上 |
| draw_when_no_hand | 在你的回合，当你没有手牌时，抽1张牌 | 7 | 参考遗物「**不休陀螺**」；**无需每回合保护**——费用限制天然防止永动机 |
| rest_all_options | 你可以在休息处选择任意数量的选项 | 12 | 参考遗物「**微型帐篷**」（休息处 Hook，非战斗内效果，机制不同） |
| block_at_turn_start | 每回合开始时，获取消耗牌堆数等量格挡 | 10 | 回合开始事件 → `ExhaustPile.Cards.Count` 格挡 |

**铁甲战士**

| id | 条目 | 成本 | 效果实现要点 |
|---|---|---|---|
| draw_on_exhaust | 每当有一张牌被消耗时，抽一张牌 | 10 | 消耗事件（Exhaust 相关 Command Postfix） |
| draw_on_hp_loss | 每当失去生命值时，抽一张牌 | 10 | 生命值变化事件，**每段伤害触发一次**（多段攻击 6×3 触发 3 次） |

**静默猎手**

| id | 条目 | 成本 | 效果实现要点 |
|---|---|---|---|
| weak_on_discard | 每当丢弃一张牌时，给予所有敌人1层虚弱 | 10 | 弃牌事件 Postfix → 全体敌人 WeakPower |
| block_on_poison | 每当给予敌人中毒时，获得1格挡 | 10 | 中毒应用事件 Postfix |

**储君**

| id | 条目 | 成本 | 效果实现要点 |
|---|---|---|---|
| forge_on_play | 每打出1张牌时，铸造1 | 10 | 打出牌 Postfix → 铸造（参考每回合铸造实现） |
| vigor_on_skill_play | 每打出1张技能牌时，获得2点活力 | 6 | 打出牌 Postfix + 卡牌类型判定（Skill）→ VigorPower |

**亡灵契约师**

| id | 条目 | 成本 | 效果实现要点 |
|---|---|---|---|
| summon_on_play | 每打出1张牌时，召唤1 | 10 | 打出牌 Postfix → 召唤 |
| dmg_on_ethereal_play | 每打出1张虚无牌，对所有敌人造成3点伤害 | 7 | 打出牌 Postfix + 词条判定（Ethereal）→ 全体伤害 |

**故障机器人**

| id | 条目 | 成本 | 效果实现要点 |
|---|---|---|---|
| draw_on_power_play | 每打出1张能力牌，抽一张牌 | 10 | 打出牌 Postfix + 卡牌类型判定（Power） |
| orb_dmg_at_turn_end | 回合结束时，每有一个充气球，对所有敌人造成1点伤害 | 10 | 回合结束事件 → `player.OrbCount` 次全体伤害 |

---

## 五、实现计划（分阶段，每阶段完成需游戏内验证后再进行下一步）

> 遵循 策划.md 实验要求：详细分步实现，每步测试通过再继续。

### Phase 0 — 基建：主题与字体

**文件**：新增 `Scripts/UiComponents/UpgradeTheme.cs`；新增 `resources/fonts/` 字体文件

1. 集中定义主题色板常量（贴原版质感）：
   - 面板底：暗羊皮纸褐 `#1E1B16`；金边 `#C9A45C`；标题金 `#E8C97A`
   - 正文 `#E8E2D4` / 次级 `#9A927F` / 成本 `#A9A193`
   - 危险红 `#D46A5A`；MAX 徽标金底深字；悬停发光 `#F0D48A`
   - 分区头底色 `#262019`
2. 字体：**已完成** — Adobe 官方思源宋体 SC 静态 OTF（Regular + SemiBold，OFL 协议）放 `resources/fonts/`；
   从 DLL 目录加载（`Assembly.Location` → `FontFile.LoadDynamicFont`）；**必须开启 MSDF**
   （`MultichannelSignedDistanceField=true`）——游戏以 canvas_items 内容缩放渲染（2K=1.333×），
   非 MSDF 字体会被画布变换双线性放大导致发糊。禁止换用 Google 转换版/变量字体（MSDF 出噪音）。
   详见 DEBUG.md「Phase 0.5: 2K 分辨率 UI 模糊修复」。
3. 验证：游戏内打开面板，中文渲染为衬线体、色板正常。

### Phase 1 — 数据模型与本地化

**文件**：`Scripts/UiComponents/UpgradeItemData.cs`（按 §4.1 扩展）、`Scripts/AbilityOperationHelper.cs`（无需改，`GetBoost` 已满足）、`InfiniteUpgradeSystem/localization/{zhs,eng}/cards.json`

1. `UpgradeItemDef` 落地（Kind / MaxLevel / LevelProvider / ValueText / PromptKey）。
2. 新增条目注册表：`UpgradeUIHandler.PopulateItems()` 重构为按「分类 → 子类 → 条目」的字典结构，供 UI 遍历渲染与搜索使用。
3. 本地化 key 规范：
   - 名称：`INFINITEUPGRADESYSTEM-ITEM_<ID>`
   - 选牌提示：`INFINITEUPGRADESYSTEM-PROMPT_<OP_ID>`，例：`…PROMPT_ATTACK_PLUS` = 「选择一张牌，令其攻击+1」
   - 中英双语各一份，沿用现有 `cards.json` 合并机制
4. 验证：搜索可命中中文/英文名称。

### Phase 2 — 组件重构

**文件**：`Scripts/UpgradeUIHandler.cs` 大改；新增 `Scripts/UiComponents/CollapsibleSection.cs`、`UpgradeItemRow.cs`、`ClassTabBar.cs`、`UpgradeTopBar.cs`；`Scripts/Entry.cs`（BUILD 版本号 +1）

1. **视图状态**：`enum UiView { Main, CardOps }`，`UpgradeUIHandler` 记录当前视图；`SetUIVisible(true)` 恢复上次视图（取消链路的关键）。
2. **CollapsibleSection**：分区头（箭头 + 标题 + 右侧摘要）+ 内容容器；高度 Tween 展开/收起；箭头旋转动画。
3. **UpgradeItemRow**：按 Kind 渲染（§4.2）；状态机：普通 / 悬停(金边发光) / 禁用(灰+红成本) / MAX。
4. **ClassTabBar**：技能分区内 6 个二级标签页（当前为空态占位）。
5. **UpgradeTopBar**：标题 + 金色点数计数器（宝石图标 + 数字）+ 关闭按钮。
6. 测试分区样式弱化：次级色、字号减小。
7. 验证：四个分区折叠/展开正常，牌组入口行显示卡牌数。

### Phase 3 — 交互：搜索 / 记忆 / 取消链路

**文件**：`Scripts/UpgradeUIHandler.cs`

1. **搜索跳转**：全局过滤全部条目 → 命中时确保所属分区已展开 → `ScrollContainer` 滚动定位到首条命中 → 命中文本高亮（金底深字，清除搜索后恢复）；无结果显示空态「未找到相关项目」。
2. **状态记忆**：静态字段记录各分区展开/收起状态 + 面板位置（运行期间有效，跨打开/关闭保持）。
3. **取消链路**：选牌取消（左侧返回图标 / Esc）→ `OnClick` 返回 false（现有 helper 已退款 + `SetUIVisible(true)`）→ 恢复视图为 CardOps（而非 Main）。需给所有 Action 项的 helper 增加 `promptKey` 参数透传。
4. 验证：搜索「力量」自动展开基础属性并高亮；取消选牌后回到牌组子面板且点数已返还。

### Phase 4 — 牌组子面板 + 专属提示

**文件**：`Scripts/CardOperationHelper.cs`（全部方法加 `promptKey` 参数）、`Scripts/UpgradeUIHandler.cs`、本地化文件（30 条 PROMPT key）

1. 子面板布局：返回栏 + 搜索 + 30 项操作列表。
2. 每项操作定义携带 `PromptKey`，透传至 `SelectCardFromDeck(player, promptKey)`。
3. 视图切换动画（主 ↔ 子面板左右滑入）。
4. 验证：点「攻击+1」→ 选牌界面提示「选择一张牌，令其攻击+1」；取消后回子面板。

### Phase 5 — 动效全量 + 回归测试

**文件**：`Scripts/UpgradeUIHandler.cs` 及组件

1. 动效清单（全做）：
   - 面板开合：淡入 + 上移 16px 起；关闭反向
   - 分区展开/收起：高度 Tween 150ms + 箭头旋转
   - 悬停：金色描边发光
   - 购买成功：+1 金色飘字 + 点数计数器跳动
   - 点数不足：加号抖动（水平 offset 200ms）+ 红色提示
2. 回归测试：46 项现有功能全部可用（见 §八）。

---

## 六、扩展指南：如何新增一个技能

技能 = **局内触发的被动效果**（数值只影响本局，不修改点数系统的初始属性），**限等级条目（MaxLevel=1）**。参照 策划.md 技能分类：通用 / 铁甲战士 / 静默猎手 / 储君 / 亡灵契约师 / 故障机器人。技能内容见 §4.4。

### 6.1 完整步骤（以「每打出 1 张牌，获得 1 点格挡」为例）

**第 1 步：实现效果（游戏侧逻辑）**

优先复用游戏内已有实现（如格挡跨回合已有 `BarricadePower` 反射应用模式）。示例——打出牌时触发：

```csharp
// Scripts/Skills/SkillBlockOnPlay.cs（新目录 Scripts/Skills/）
[HarmonyPatch(typeof(PlayCardCmd))]          // 具体 Command 类以游戏 API 为准
public static class SkillBlockOnPlay
{
    [HarmonyPostfix]
    public static async void AfterPlay(CardModel card, Player player)
    {
        if (!SkillRegistry.Has("block_on_play")) return;
        await PlayerCmd.GainBlock(1, player);
    }
}
```

**第 2 步：注册数据项（UI 自动呈现，无需改 UI 代码）**

```csharp
// UpgradeUIHandler.PopulateItems() 的技能分区添加一行：
_allItems.Add(new UpgradeItemDef
{
    Category = "技能",
    SubCategory = "通用",           // 自动落入「通用」标签页
    DisplayName = "每打出1张牌，获得1点格挡",
    LocKey = "INFINITEUPGRADESYSTEM-ITEM_BLOCK_ON_PLAY",
    Cost = 10,
    Kind = UpgradeItemKind.Stat,
    MaxLevel = 1,                   // 限等级：达到 1 级 → MAX 徽标（不用 bool）
    LevelProvider = () => SkillRegistry.GetLevel("block_on_play"),
    OnClick = () => SkillRegistry.Purchase("block_on_play", 10),
});
```

**第 3 步：本地化**：`zhs`/`eng` 的 `cards.json` 各加名称 key（技能无选牌流程，不需要 PROMPT key）。

### 6.2 技能注册表与持久化（新文件 `Scripts/Skills/SkillRegistry.cs`）

```csharp
public static class SkillRegistry
{
    // 等级化存储：与 AbilityOperationHelper.s_boosts 完全同构，不使用 bool
    private static readonly Dictionary<string, int> s_levels = new();

    public static int GetLevel(string id) => s_levels.TryGetValue(id, out int v) ? v : 0;
    public static bool Has(string id) => GetLevel(id) > 0;

    public static async Task<bool> Purchase(string id, int cost, int maxLevel = 1)
    {
        // 1. 先扣点：UpgradePointManager.TrySpendPoints(cost)，失败返回 false
        // 2. 已满级（GetLevel(id) >= maxLevel）拒绝购买
        // 3. s_levels[id] = GetLevel(id) + 1
        // 4. 立即写盘（参照 AbilityOperationHelper.TryPurchase 的 SaveCheckpoint 模式）
        return true;
    }
}
```

- 持久化文件格式：`skills_<seed>.json` → `Dictionary<string,int>`（与 abilities 文件同构），随检查点（CombatSetUp / CombatWon）一并保存；购买即写盘，读档 `Load()`。
- 与现有系统关系：技能只与「本局」相关（同点数系统按 seed 区分）；不做局外持久化。
- **未来多级技能**：把 `MaxLevel` 提到 N，`Purchase` 按上限封顶，效果逻辑读 `GetLevel(id)` 决定强度（如 2 级 = 每打出 1 张牌获得 2 格挡）。UI 与持久化零改动。

### 6.3 效果实现原则（重要）

1. **优先借用游戏内已有卡牌/能力/遗物机制**：
   - 格挡跨回合不消失 → `BarricadePower`（已实现，见 `AbilityOperationHelper.ApplyOnePower`）
   - 每回合辉星 → 参考「创世纪」卡牌实现（`PlayerCmd.GainStars`，已实现）
   - 无手牌抽牌 → 参考遗物「不休陀螺」（无需每回合保护，费用天然防永动机）
   - 休息处任意选项 → 参考遗物「微型帐篷」
   - 失去生命值抽牌 → 每段伤害触发一次（多段攻击 6×3 触发 3 次）
   - 打出牌触发类 → 找游戏内对应 Command 的 Postfix（如打牌、弃牌、消耗命令）
   - 回合开始/结束触发类 → `RunStateHook` 已有生命周期订阅模式，新增对应事件
2. **不要改游戏本体代码**：全部通过 Harmony Patch 或现有事件订阅。
3. **技能 UI 呈现零成本**：只要在 `PopulateItems()` 加一行 + 本地化，UI 自动：落入对应职业标签页、限等级渲染（MaxLevel=1）、满级后 MAX 徽标、点数不足禁用。**新增技能永远不需要改 UI 组件代码。**

---

## 七、未来扩展建议

1. **数据驱动化**：条目定义从代码迁移到 `items.json`（mod 目录），新增技能/属性只改 JSON + 注册效果，不动代码。
7. **技能等级化**：`MaxLevel` 从 1 提升到 N 即可支持技能叠层/升级（如「每打出1张牌获得2格挡」），等级化数据模型已预留，无需改 UI 与持久化。
2. **效果注册表 + 事件总线**：`SkillRegistry` 与效果实现解耦，技能 id → 效果 handler 映射，便于批量管理与测试。
3. **局内只读模式**：战斗中可打开面板仅浏览当前属性值（不可购买），需要面板增加 read-only 状态。
4. **面板位置/折叠状态持久化到磁盘**：当前为运行内存级，可后续随检查点 JSON 存盘，跨会话保持。
5. **更多职业支持**：6 个标签页按职业 id 自动生成，新职业只需加 SubCategory 常量。
6. **自定义卡牌操作项**：后续「卡牌额外效果」系统（策划中 Phase 4 未实现部分）可通过 Action 型条目扩展，`PromptKey` 机制已预留。

---

## 八、测试方案

### 8.1 现有功能回归（46 项全量）

| 类别 | 验证点 |
|---|---|
| 卡牌操作 (29) | 升级卡牌(含 MAX 级、首次/非首次)、攻击/格挡/抽牌/重放+1、耗能-1(0费边界)、词条添加/移除(重复添加提示、不存在移除提示、退款)、奇巧 |
| 能力操作 (13) | 购买即写盘、读档恢复、战斗开始生效（ApplyInitialBoosts）、hp/energy/orbSlot 立即生效 |
| 牌组操作 (1) | 删除卡牌、CardUpgradeTracker 索引修正 |
| 测试 (3) | 点数+1/+5/+10 |
| 全局 | 点数检查点保存/回滚、新局 6 点、同 seed 读档恢复 |

### 8.2 新 UI 功能清单

1. 分区默认全部收起；展开/收起后关闭面板再打开，状态保持
2. 拖拽移动面板，关闭再打开位置保持
3. 搜索「力量」→ 自动展开基础属性 + 滚动定位 + 高亮；无结果空态
4. 点数不足：成本变红 + 加号置灰；点击抖动
5. 限等级项（技能 16 项 / 格挡跨回合 / 能量跨回合）达到 MaxLevel 后显示 MAX 徽标；MAX 后不可再购
6. 牌组入口显示卡牌数；点击整页切换；「← 返回」回主面板
7. 每项操作选牌界面显示专属提示文案（抽查 3 项）
8. 选牌取消（左侧返回图标 / Esc）→ 回到牌组子面板 + 点数已返还
9. 动效：开合淡入、分区展开、悬停金边、+1 飘字、点数不足抖动
10. 中文衬线体渲染正常（标题/正文两级）

### 8.3 测试流程建议

每个 Phase 完成后：`dotnet build -c Debug` → 启动游戏 → 非战斗按 P 打开 → 按清单逐项验证 → 通过后进入下一 Phase。

---

## 九、局内只读面板（Phase S2.5，方案已选型 2026-08-04）

> 背景：计数类技能（每4张牌触发）需要局内可见的计数反馈；面板默认仅局外可开。
> 方案评估：Power 图标方案需自定义 PowerModel + 图标资源（mod 无 pck，成本高、只解决计数）；
> **采用局内只读面板方案**——复用现有 UI，顺带获得局内查看全部属性的能力。

### 设计
- **战斗中也允许按 P 打开面板，但为只读模式**：
  - 所有条目行的**加号隐藏**（MAX 徽标照常显示），悬停提亮/抖动无效
  - 牌组入口行禁用（点击提示「战斗中不可操作」）；测试分区同样只读
  - 点数计数器、值列、计数（N/4）照常显示
- **判定**：ShowUI 时检测 `CombatManager.Instance is { IsOverOrEnding: false }` → readOnly=true（原逻辑是直接拒绝打开）
- **刷新**：面板每次打开执行 `RefreshAllRows()`（已有）——计数类技能的值列显示 `SkillRegistry.CombatPlayCount` 的「N/4」；打牌 → 关面板 → 再开 → 更新
- **输入**：全屏遮罩（MouseFilter Stop）天然挡住游戏操作——只读模式即「只能看不能动」
- **退出**：战斗结束（CombatWon）后重开面板自动回到正常模式（readOnly 按当前战斗状态判定）

### 实现清单
1. `UpgradeItemRow`：新增 `ReadOnly` 属性（隐藏加号、屏蔽交互）
2. `UpgradeUIHandler`：ShowUI 战斗拦截 → readOnly 判定；牌组入口行只读禁用
3. `SkillItem` 计数值列已接入 `SkillRegistry.CombatPlayCount`（S2 完成）

### 后续可选增强（暂不实施）
- 方案 1（角色下方常驻 Power 图标）：需自定义 PowerModel（BaseLib 注册）+ 图标资源，
  仅显示数字 N（规则靠悬停提示），价值低于只读面板，留待需要常驻显示时再做。

---

## 附：相关文件索引

| 文件 | 本次改动 |
|---|---|
| `Scripts/UpgradeUIHandler.cs` | 大改：视图状态机、分区/行/标签页渲染、搜索跳转、动效 |
| `Scripts/UiComponents/UpgradeItemData.cs` | 扩展为 `UpgradeItemDef`（Kind/MaxLevel/LevelProvider/ValueText/PromptKey） |
| `Scripts/UiComponents/UpgradeTheme.cs` | 新增：色板 + 字体 |
| `Scripts/UiComponents/CollapsibleSection.cs` 等 | 新增：折叠分区/条目行/标签页/顶栏组件 |
| `Scripts/CardOperationHelper.cs` | 各方法增加 `promptKey` 参数透传 |
| `Scripts/Skills/SkillRegistry.cs` | 新增（仅技能扩展时需要） |
| `InfiniteUpgradeSystem/localization/{zhs,eng}/cards.json` | 30 条 PROMPT key + 条目名称 key |
| `resources/fonts/` | 新增：思源宋体 SC（OFL） |
| `Scripts/Entry.cs` | BUILD 版本号 +1 |
