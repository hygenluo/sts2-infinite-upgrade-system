using System;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 升级点数管理器。
/// 修改只在内存中进行，SaveCheckpoint() 才写入磁盘。
/// 配合 RunStateHook 在战斗开始/结束时保存检查点。
/// </summary>
public static class UpgradePointManager
{
    public static int CurrentPoints { get; private set; } = 7;

    private static string CurrentSeed() =>
        RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";

    /// <summary>从检查点恢复点数（不写盘）。</summary>
    public static void SetPointsDirect(int points) => CurrentPoints = points;

    /// <summary>新局初始化（不写盘）。</summary>
    public static void InitializeDirect() => CurrentPoints = 7;

    public static void AddPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        // 方案 B：点数写入每玩家 store（Mutate 内部刷新本地缓存 CurrentPoints）
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;
        UpgradeDataStore.Mutate(player, d => d.Points += amount);
        SaveCheckpoint(); // 点数变化立即写盘，避免中途退出丢失
    }

    public static bool TrySpendPoints(int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return false;
        if (UpgradeDataStore.GetPoints(player) < amount) return false;
        UpgradeDataStore.Mutate(player, d => d.Points -= amount);
        SaveCheckpoint(); // 点数变化立即写盘，避免中途退出丢失
        return true;
    }

    /// <summary>保存检查点（战斗开始/结束时调用）。</summary>
    public static void SaveCheckpoint()
    {
        PointsPersistence.SavePoints(CurrentPoints, CurrentSeed());
    }
}
