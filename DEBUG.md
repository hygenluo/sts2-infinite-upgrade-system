# DEBUG 记录 (InfiniteUpgradeSystem)

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
