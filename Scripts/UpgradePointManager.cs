using System;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 升级点数管理器
/// 管理玩家当前拥有的升级点数，支持点数增减和消费。
/// 点数每局有效（本局内跨战斗保留）。
/// </summary>
public static class UpgradePointManager
{
    /// <summary>
    /// 当前可用点数，默认初始值为 5
    /// </summary>
    public static int CurrentPoints { get; private set; } = 5;

    /// <summary>
    /// 初始化/重置点数为默认值（5 点）
    /// </summary>
    public static void Initialize()
    {
        CurrentPoints = 5;
    }

    /// <summary>
    /// 增加点数（仅允许非负值）
    /// </summary>
    public static void AddPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        CurrentPoints += amount;
    }

    /// <summary>
    /// 尝试消费点数。
    /// 如果点数足够则扣除并返回 true，否则返回 false。
    /// </summary>
    public static bool TrySpendPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));

        if (CurrentPoints >= amount)
        {
            CurrentPoints -= amount;
            return true;
        }
        return false;
    }
}
