# DEBUG 记录 (InfiniteUpgradeSystem)

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
