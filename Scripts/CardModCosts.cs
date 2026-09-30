namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌操作的成本表（**唯一来源**）。
///
/// 为什么单独抽出来：成本在两个地方需要——
/// 1. 购买：`UpgradeUIHandler.PopulateItems()` 构建条目时取成本；
/// 2. 回退：旧版存档里的卡牌修改记录**没有存成本**（`CardModEntry.Cost` 是 v2.3 新增字段），
///    回退这类旧记录时需要用同样的表反查「当初花了多少点」。
/// 两处若各写一份字面量，改价时必然不同步 → 因此统一放这里。
/// </summary>
public static class CardModCosts
{
    /// <summary>升级卡牌（不参与回退，见表尾说明）。</summary>
    public const int Upgrade = 5;

    public const int DamagePlus = 1;
    public const int BlockPlus = 1;
    public const int DrawPlus = 7;
    public const int ReplayPlus = 7;
    public const int EnergyReduce = 12;
    public const int Enchant = 12;
    public const int DeckRemove = 20;

    /// <summary>添加词条成本（按词条区分）。</summary>
    public static int KeywordAdd(string keyword) => keyword switch
    {
        "Exhaust" => 12,
        "Sly" => 7,
        "Retain" => 7,
        "Innate" => 7,
        "Ethereal" => 8,
        "Eternal" => 10,
        _ => 7,
    };

    /// <summary>移除词条成本（按词条区分）。</summary>
    public static int KeywordRemove(string keyword) => keyword switch
    {
        "Exhaust" => 15,
        "Retain" => 7,
        "Innate" => 7,
        "Ethereal" => 8,
        "Eternal" => 15,
        _ => 7,
    };

    /// <summary>按修改类型 + 参数取成本（回退旧记录时用于反查已付点数）。</summary>
    public static int For(CardUpgradeTracker.ModType type, string? arg) => type switch
    {
        CardUpgradeTracker.ModType.Upgrade => Upgrade,
        CardUpgradeTracker.ModType.DamagePlus => DamagePlus,
        CardUpgradeTracker.ModType.BlockPlus => BlockPlus,
        CardUpgradeTracker.ModType.DrawPlus => DrawPlus,
        CardUpgradeTracker.ModType.ReplayPlus => ReplayPlus,
        CardUpgradeTracker.ModType.EnergyReduce => EnergyReduce,
        CardUpgradeTracker.ModType.Enchant => Enchant,
        CardUpgradeTracker.ModType.DeckRemove => DeckRemove,
        CardUpgradeTracker.ModType.KeywordAdd => KeywordAdd(arg ?? ""),
        CardUpgradeTracker.ModType.KeywordRemove => KeywordRemove(arg ?? ""),
        _ => 0,
    };
}
