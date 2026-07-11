using System;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 升级点数管理器 — 通过 PointsPersistence 按 Run seed 持久化。
/// </summary>
public static class UpgradePointManager
{
    public static int CurrentPoints { get; private set; } = 5;

    private static string CurrentSeed() =>
        RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";

    public static void Initialize()
    {
        CurrentPoints = 5;
        PointsPersistence.SavePoints(CurrentPoints, CurrentSeed());
    }

    public static void SetPointsDirect(int points)
    {
        CurrentPoints = points;
    }

    public static void InitializeDirect()
    {
        CurrentPoints = 5;
    }

    public static void AddPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        CurrentPoints += amount;
        PointsPersistence.SavePoints(CurrentPoints, CurrentSeed());
    }

    public static bool TrySpendPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));

        if (CurrentPoints >= amount)
        {
            CurrentPoints -= amount;
            PointsPersistence.SavePoints(CurrentPoints, CurrentSeed());
            return true;
        }
        return false;
    }
}
