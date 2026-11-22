namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 技能所属角色（左侧角色列的条目 key）。
///
/// 之前这批常量散落在 `ClassTabBar` 里，且与「标签页控件」绑死；UI v4 改用
/// 左列角色选择 + 右列加点项后，把**数据**（角色 key 与顺序）与**控件**拆开：
/// 新增角色只需在 <see cref="All"/> 里加一项 + 在 <see cref="UpgradeLoc.ClassTitle"/>
/// 里加一条显示名 + 本地化 key —— 控件与布局代码零改动。
///
/// key 直接用中文常量：它同时是 <c>UpgradeItemDef.SubCategory</c> 的分组 key
/// （见 UI设计.md §4.4 / 扩展指南「如何新增一个技能」），改 key 会让历史存档里的技能
/// 归属失效，因此不要为了显示名去改这里的值。
/// </summary>
public static class SkillClasses
{
    public const string Generic = "通用";
    public const string Ironclad = "铁甲战士";
    public const string Silent = "静默猎手";
    public const string Regent = "储君";
    public const string Necrobinder = "亡灵契约师";
    public const string Defect = "故障机器人";

    /// <summary>角色列显示顺序（通用在前：它不属于任何职业，是所有人的被动）。</summary>
    public static readonly string[] All =
    [
        Generic,
        Ironclad,
        Silent,
        Regent,
        Necrobinder,
        Defect,
    ];
}
