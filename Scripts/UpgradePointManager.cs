using System;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 升级点数管理器。
/// 点数本局有效，通过 PointsPersistence 在存档/读档间持久化。
/// </summary>
public static class UpgradePointManager
{
    public static int CurrentPoints { get; private set; } = 5;

    /// <summary>
    /// 初始化/重置点数为默认值并写入磁盘。
    /// </summary>
    public static void Initialize()
    {
        CurrentPoints = 5;
        PointsPersistence.SavePoints(CurrentPoints);
    }

    /// <summary>
    /// 从存档恢复点数并写入磁盘。
    /// </summary>
    public static void SetPoints(int points)
    {
        CurrentPoints = points;
        PointsPersistence.SavePoints(CurrentPoints);
    }

    /// <summary>
    /// 仅设置点数，不触发磁盘写入（供 RunStateHook 从磁盘恢复后使用）。
    /// </summary>
    public static void SetPointsDirect(int points)
    {
        CurrentPoints = points;
    }

    /// <summary>
    /// 仅重置为 5，不触发磁盘写入（供 RunStateHook 新局时使用，由调用方负责 DeleteSavedPoints）。
    /// </summary>
    public static void InitializeDirect()
    {
        CurrentPoints = 5;
    }

    /// <summary>
    /// 增加点数并写入磁盘。
    /// </summary>
    public static void AddPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        CurrentPoints += amount;
        PointsPersistence.SavePoints(CurrentPoints);
    }

    /// <summary>
    /// 尝试消费点数。如果点数足够则扣除、写入磁盘并返回 true。
    /// </summary>
    public static bool TrySpendPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));

        if (CurrentPoints >= amount)
        {
            CurrentPoints -= amount;
            PointsPersistence.SavePoints(CurrentPoints);
            return true;
        }
        return false;
    }
}
