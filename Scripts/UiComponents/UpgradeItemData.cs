using System;
using System.Threading.Tasks;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>操作项类型。</summary>
public enum UpgradeItemKind
{
    Stat,   // 数值型（可带最大等级）：基础属性 / 能力 / 技能
    Action, // 一次性动作：卡牌操作
}

/// <summary>
/// UI 操作项定义（Phase 1 数据模型，UI设计.md §4.1）。
///
/// 等级化原则：统一 int 等级存储（不用 bool）—— MaxLevel &gt; 0 时等级达到上限
/// 渲染 MAX 徽标；未来技能叠层/升级只改 MaxLevel 与效果逻辑，UI 与持久化零改动。
/// </summary>
public sealed class UpgradeItemDef
{
    /// <summary>分区：基础属性 / 能力 / 技能 / 牌组 / 测试。</summary>
    public required string Category { get; init; }

    /// <summary>仅技能分区：职业 id（通用/铁甲战士/...），按此落入二级标签页。</summary>
    public string? SubCategory { get; init; }

    /// <summary>显示名称（本地化解析后的最终文本，解析失败回退中文）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>名称本地化 key（ITEM_*，可空；空则直接用 DisplayName）。</summary>
    public string? LocKey { get; init; }

    /// <summary>点数消耗。</summary>
    public required int Cost { get; init; }

    /// <summary>条目类型。</summary>
    public UpgradeItemKind Kind { get; init; } = UpgradeItemKind.Stat;

    /// <summary>最大等级；&lt;=0 无上限（基础属性/能力），技能=1。</summary>
    public int MaxLevel { get; init; }

    /// <summary>当前等级取数（能力 → GetBoost(key)；技能 → SkillRegistry.GetLevel(id)）。</summary>
    public Func<int>? LevelProvider { get; init; }

    /// <summary>当前值显示文本（缺省时由 LevelProvider 生成数字）。</summary>
    public Func<string>? ValueText { get; init; }

    /// <summary>仅 Action：选牌提示本地化 key（PROMPT_*）。</summary>
    public string? PromptKey { get; init; }

    /// <summary>点击处理；返回 false = 取消/失败（内部负责退款与界面恢复）。</summary>
    public required Func<Task<bool>> OnClick { get; init; }

    /// <summary>v1 UI 按钮文本（Phase 2 组件重构后移除）。</summary>
    public string ButtonLabel => Cost > 0 ? $"{DisplayName} [{Cost}点]" : DisplayName;

    /// <summary>搜索语料：分类 + 中英名称（注册表构建时填充，UI设计.md 搜索要求）。</summary>
    public string SearchText { get; set; } = "";
}
