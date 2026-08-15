# update06 设计与测试方案

> **状态**：设计草案，待确认后分步实现
> **范围**：多人联机适配（确认 + 持久化约束）→ BUG 修复（2 项）→ 新增技能（12 项）
> **铁律**：**所有项目（属性/能力/技能/卡牌操作）必须同时兼容单人模式与多人联机模式**。以后任何修改都遵守。

---

## 一、现状分析（架构结论）

模组已是 **v2.0 多人兼容架构**，本次新增内容无需推翻重建，只需沿用既有范式：

| 层 | 机制 | 多人如何保证一致 |
|---|---|---|
| 数据 | `UpgradeDataStore`（RitsuLib `PlayerRunSavedData<PlayerUpgradeData>`，Points/Skills/Boosts/CombatPlayCount） | host 权威，按 NetId 分槽 |
| 效果 | `SkillEffectsPower`（隐藏 CustomPowerModel，官方范式） | hook 在游戏 action 管线内被 await，两端确定性一致 |
| 购买 | `UpgradePurchaseAction`（INetAction） | 两端同一 action id 执行，权威校验 |
| 随机 | `player.RunState.Rng.*` | 游戏同步的确定性 RNG 流 |

**结论**：本次「核心修改」主要是**确认并持久化**该约束（已写入长期记忆），新内容一律沿用 `SkillEffectsPower` 范式，不新增 fire-and-forget Harmony Postfix。

---

## 二、BUG 修复（2 项）

### BUG-1 每回合辉星 +1（能力 key=`stars`，成本 15）

- **现象**：只第一回合 +1 辉星，后续不变。
- **根因**：`AbilityOperationHelper.ApplyStarsAtCombatStart` 在 `RunStateHook.OnCombatSetUp`（战斗开始）**一次性** `PlayerCmd.GainStars`，从未按回合触发。
- **修复**：删掉 `OnCombatSetUp` 里对 `ApplyStarsAtCombatStart` 的调用；在 `SkillEffectsPower.AfterPlayerTurnStart` 里按主题玩家读 `UpgradeDataStore.GetBoost(player,"stars")`，`await PlayerCmd.GainStars(stars, player)`（每回合 +1×层数）。
- **多人**：`AfterPlayerTurnStart` 是模型 hook，两端一致；读 store 不读本地缓存。
- **验证**：单人 3 回合，每回合开始辉星 +1；多人 host/client 同屏辉星计数一致。

### BUG-2 每回合铸造 +5（能力 key=`forge`，成本 15）

- **现象**：购买后无任何效果。
- **根因**：`forge` boost 只被写入 store，**从无任何代码应用它**。
- **修复**：在 `SkillEffectsPower.AfterPlayerTurnStart` 读 `GetBoost(player,"forge")`，`await ForgeCmd.Forge(5m * 层数, player, null)`（每回合 +5×层数铸造）。
- **多人**：同上，模型 hook + store 读。
- **验证**：单人买「每回合铸造+5」后，每回合开始君王之剑铸造值 +5；多人一致。

---

## 三、新增技能（12 项）

> 每项都注册 4 处：`UpgradeUIHandler.PopulateItems` 加一行 `SkillItem`、`UpgradeLoc.cs` 加 loc key、`localization/{zhs,eng}/cards.json` 加双语、`SkillEffectsPower` 加 hook 效果。UI 零改动（§六扩展指南）。

### 通用

| id | 效果 | 成本 | 参考实现 | Hook | 关键点 |
|---|---|---|---|---|---|
| `free_next_card_on_power` | 每打出1张能力牌，下一张牌免费 | 10 | 遗物「干瘪之手」MummifiedHand | `AfterCardPlayed` | `card.Type==Power` → 手牌中取一张「耗能量/辉星的牌」`SetToFreeThisTurn()`（见下方干瘪之手模式） |
| `dmg_x_times_at_turn_start` | 回合开始对所有敌人造成 x 伤害 y 次（x=力量+1,y=敏捷+1,最低1） | 15 | `CreatureCmd.Damage` 多次 | `AfterPlayerTurnStart` | x=`Math.Max(1, 力量+1)`，y=`Math.Max(1, 敏捷+1)`；y 次循环 `CreatureCmd.Damage(context,enemies,x,default,creature)` |

### 干瘪之手参考模式（`free_next_card_on_power` 直接照搬）

反编译 `MummifiedHand.cs` 的官方实现如下，**逐行照搬**即可（多人确定性靠 `RunState.Rng.CombatCardSelection` 同步流）：

```csharp
// 在 SkillEffectsPower.AfterCardPlayed 内：
if (card.Type == CardType.Power && UpgradeDataStore.HasSkill(player, "free_next_card_on_power"))
{
    IReadOnlyList<CardModel> cards = PileType.Hand.GetPile(player).Cards; // 或 player.PlayerCombatState.Hand.Cards
    Rng rng = player.RunState.Rng.CombatCardSelection;
    CardModel? target = rng.NextItem(cards.Where(c => c.CostsEnergyOrStars(includeGlobalModifiers: false)));
    if (target == null)
        rng.NextItem(cards.Where(c => c.CostsEnergyOrStars(includeGlobalModifiers: true))); // 空选也要耗 RNG，保持两端同步
    target?.SetToFreeThisTurn();
}
```

**关键点**：`SetToFreeThisTurn()` 是本效果唯一机制（非 VoidFormPower）；`if (target==null) 再抽一次` 是为保证两端 RNG 消耗次数一致（MummifiedHand 原文，勿删）。

### 铁甲战士

| id | 效果 | 成本 | 参考 | Hook | 关键点 |
|---|---|---|---|---|---|
| `vulnerable_on_exhaust` | 每消耗1张牌，全体敌人 +2 易伤 | 10 | `VulnerablePower` | `AfterCardExhausted` | `ApplyPower` 加 `"vulnerable"` 映射；foreach 敌人 +2 |
| `str_from_vulnerable_at_turn_start` | 回合开始获「全体敌人易伤总和」点力量 | 15 | `StrengthPower` | `AfterPlayerTurnStart` | sum = `enemies.Sum(e => e.GetPower<VulnerablePower>()?.Amount ?? 0)` → `ApplyPower(creature,"strength",sum)` |

### 静默猎手

| id | 效果 | 成本 | 参考 | Hook | 关键点 |
|---|---|---|---|---|---|
| `shiv_all_enemies` | 你的[小刀]攻击所有敌人 | 10 | `FanOfKnivesPower`（Shiv 检测 `HasPower<FanOfKnivesPower>` 即切 `TargetType.AllEnemies`） | 战斗开始施加（同 `freeFirstCard`） | `ApplySkillCombatStartPowers` 里 `PowerCmd.Apply<FanOfKnivesPower>`，纯被动零 hook |
| `dexterity_on_discard` | 每丢弃1张牌 +1 敏捷 | 15 | `DexterityPower` | `AfterCardDiscarded` | `ApplyPower(creature,"dexterity",1)` |

### 储君

| id | 效果 | 成本 | 参考 | Hook | 关键点 |
|---|---|---|---|---|---|
| `add_colorless_card_at_turn_start` | 回合开始手牌加1张「升级过的」无色牌，本回合免费 | 10 | `Distraction`/`InfernalBlade` | `AfterPlayerTurnStart` | `CardFactory.GetDistinctForCombat(player, ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(...),1, player.RunState.Rng.CombatCardGeneration)` → `CardCmd.Upgrade(card)`（无升级等级时跳过）→ `card.SetToFreeThisTurn()` → `CardPileCmd.AddGeneratedCardToCombat(card,PileType.Hand,addedByPlayer:true)` |
| `enemy_lose_str_on_star` | 每获1点辉星，全体敌人 -1 力量 | 15 | `StrengthPower.AllowNegative` + `AfterStarsGained` | `AfterStarsGained(int,Player)` | foreach 敌人 `PowerCmd.Apply<StrengthPower>(ctx,enemy,-1,creature,null)`（需允许负值辅助路径） |

### 亡灵契约师

| id | 效果 | 成本 | 参考 | Hook | 关键点 |
|---|---|---|---|---|---|
| `enemy_lose_str_on_doom` | 每给敌人灾厄，该敌人 -1 力量 | 10 | `DoomPower` | `AfterPowerAmountChanged` | `power is DoomPower && applier==玩家` → `PowerCmd.Apply<StrengthPower>(ctx, power.Owner, -1, applier, null)` |
| `play_ethereal_from_exhaust` | 回合结束打出消耗堆所有虚无牌（**排除诅咒牌与状态牌**） | 15 | `CardCmd.AutoPlay` | `BeforeTurnEnd`（side=Player） | 枚举 `ExhaustPile.Cards` 中 `Keywords.Contains(Ethereal) && Type!=Curse && Type!=Status` → 逐个 `CardCmd.AutoPlay(ctx,card,null)` |

### 故障机器人

| id | 效果 | 成本 | 参考 | Hook | 关键点 |
|---|---|---|---|---|---|
| `auto_play_random_power_at_turn_start` | 回合开始打出1张随机能力牌 | 10 | `InfernalBlade`（改 `Type==Power`） | `AfterPlayerTurnStart` | `GetDistinctForCombat(...where Type==Power...,1,rng)` → `CardCmd.AutoPlay(ctx,card,null)` |
| `exhaust_status_gain_block_at_turn_end` | 回合结束消耗手牌中状态牌，**每消耗一张 +2 格挡** | 15 | `CardCmd.Exhaust` + `CardType.Status` | `BeforeTurnEnd`（side=Player） | 找 `Hand.Cards` 中 `Type==Status` → 逐个 `CardCmd.Exhaust`；`CreatureCmd.GainBlock(creature, 2m × 消耗张数)` |

---

## 四、Power 映射 / 辅助新增（基础设施，随对应技能步骤落地）

| 项 | 位置 | 用途 |
|---|---|---|
| `"vulnerable" => VulnerablePower` | `AbilityOperationHelper.ApplyPower` switch | I1 施加易伤 |
| `"fanOfKnives" => FanOfKnivesPower`（或直接 `PowerCmd.Apply<FanOfKnivesPower>`） | 战斗开始 power | S1 小刀全体攻击 |
| 负力量辅助（允许 `count<0` 或独立 `LoseStrength` 方法） | `AbilityOperationHelper` | R2 / N1 敌人失力（现 `ApplyPower` 有 `count<=0` 早退，需放宽） |
| `AfterStarsGained` hook | `SkillEffectsPower` | R2 辉星触发 |
| `BeforeTurnEnd` hook | `SkillEffectsPower` | N2 / D2 回合结束触发 |

---

## 五、实现顺序（每步独立可测，测试通过才进下一步）

> 每步：实现 → `dotnet build -c Debug` → 单人验证 → 多人联机验证 → git commit（中文信息）→ 下一项。

| 步骤 | 内容 | 类型 |
|---|---|---|
| 1 | BUG-1 每回合辉星+1 | 修复 |
| 2 | BUG-2 每回合铸造+5 | 修复 |
| 3 | 通用 dmg_x_times_at_turn_start（x伤害y次） | 新技能 |
| 4 | 通用 free_next_card_on_power（能力牌→免费） | 新技能 |
| 5 | 铁甲 vulnerable_on_exhaust（消耗→易伤，含 `vulnerable` 映射） | 新技能 |
| 6 | 铁甲 str_from_vulnerable_at_turn_start（易伤→力量） | 新技能 |
| 7 | 静默 dexterity_on_discard（弃牌→敏捷） | 新技能 |
| 8 | 静默 shiv_all_enemies（小刀全体攻击） | 新技能 |
| 9 | 储君 enemy_lose_str_on_star（辉星→敌人失力，含负力量辅助） | 新技能 |
| 10 | 储君 add_colorless_card_at_turn_start（无色牌） | 新技能 |
| 11 | 亡灵 enemy_lose_str_on_doom（灾厄→失力） | 新技能 |
| 12 | 亡灵 play_ethereal_from_exhaust（虚无牌打出） | 新技能 |
| 13 | 故障 exhaust_status_gain_block_at_turn_end（状态牌消耗+格挡） | 新技能 |
| 14 | 故障 auto_play_random_power_at_turn_start（随机能力牌） | 新技能 |

---

## 六、每步测试方案（单人 + 多人）

### 通用测试方法（每步都做）

1. **单人**：`dotnet build -c Debug` → 启动游戏 → 开新局 → 攒够点数购买该技能 → 按该技能的具体触发条件验证效果。
2. **多人联机**：host 建房 + 好友加入 → 双方分别购买该技能 → 同时观察触发 → 确认**两端数值/卡牌/状态完全一致、无 divergence 报错**（查 `godot.log` 搜 `Divergence`）。
3. 每步通过后 `git add -A && git commit -m "update06: <该步中文描述>"`。

### 步骤专项验证点

| 步骤 | 单人验证点 | 多人重点 |
|---|---|---|
| 1 辉星+1 | 连续 3 回合，每回合开始辉星 +1（不再是仅首回合） | 两端辉星计数一致 |
| 2 铸造+5 | 买后每回合开始君王之剑铸造值 +5 | 两端铸造值一致 |
| 3 x伤害y次 | 力量/敏捷各为 0 时不报错；各 >0 时对全体敌人造成 y 段 x 伤害 | 伤害段数/数值一致 |
| 4 能力牌免费 | 打能力牌后，下一张牌显示 0 费且可免费打出 | 免费状态同步一致 |
| 5 消耗→易伤 | 消耗任意牌，全体敌人 +2 易伤 | 易伤层数一致 |
| 6 易伤→力量 | 回合开始获得=易伤总和的力量 | 力量值一致 |
| 7 弃牌→敏捷 | 弃牌（如结算弃牌/奇巧）后 +1 敏捷 | 敏捷值一致 |
| 8 小刀全体 | 有[小刀]时目标变全体、打全体 | 小刀目标/伤害一致 |
| 9 辉星→失力 | 获辉星后全体敌人 -1 力量（力量可为负） | 敌人力量一致 |
| 10 无色牌 | 回合开始手牌多1张升级过的无色牌且免费 | 生成的无色牌**同一张**（两端一致） |
| 11 灾厄→失力 | 给敌人灾厄时该敌人 -1 力量 | 敌人力量一致 |
| 12 虚无牌打出 | 回合结束消耗堆虚无牌被自动打出（诅咒/状态牌不打出） | 打出结果一致 |
| 13 状态牌消耗+格挡 | 回合结束手牌状态牌消失，每张 +2 格挡 | 状态牌/格挡一致 |
| 14 随机能力牌 | 回合开始自动打出1张能力牌 | 两端打出**同一张**能力牌 |

---

## 七、本地化清单（每步同步，`zhs`+`eng` 各一份）

新增 12 个技能名称 key（`INFINITEUPGRADESYSTEM-ITEM_SKILL_*`），无选牌流程不需 PROMPT key。示例命名沿用现有 `ItemSkillXxx` 常量。

---

## 八、数值调整

update06 该节为空，本次不做数值调整。

---

## 九、已确认的设计决策（2026-08-16）

1. **步骤 4「下一张牌免费」**：照搬遗物「干瘪之手」MummifiedHand 模式——打出能力牌后 `SetToFreeThisTurn()` 手牌中一张耗能量/辉星的牌（`RunState.Rng.CombatCardSelection` 同步随机），不用 VoidFormPower。
2. **步骤 13「状态牌+格挡」**：改为**每消耗一张 +2 格挡**（`2m × 消耗张数`）。
3. **步骤 12「虚无牌打出」**：过滤掉**诅咒牌（Curse）与状态牌（Status）**，只打出 `Ethereal && Type!=Curse && Type!=Status` 的牌。
