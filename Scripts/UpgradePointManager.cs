using System;
using System.Threading.Tasks;
using InfiniteUpgradeSystem.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 升级点数管理器。
/// 修改写入每玩家 store（RitsuLib），CurrentPoints 仅是本地玩家缓存（UI 读取）。
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
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;
        AddPoints(player, amount);
    }

    /// <summary>给指定玩家加点（方案 B：写入该玩家 store，Mutate 刷新本地缓存）。</summary>
    public static void AddPoints(Player player, int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        if (player == null) return;
        UpgradeDataStore.Mutate(player, d => d.Points += amount);
        SaveCheckpoint(); // 点数变化立即写盘，避免中途退出丢失
    }

    public static bool TrySpendPoints(int amount)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return false;
        return TrySpendPoints(player, amount);
    }

    /// <summary>给指定玩家扣点（校验+扣减）。由同步 action / 确定性 hook 两端执行，保证两端一致。</summary>
    public static bool TrySpendPoints(Player player, int amount)
    {
        if (amount < 0)
            throw new ArgumentException("Amount must be non-negative.", nameof(amount));
        if (player == null) return false;
        if (UpgradeDataStore.GetPoints(player) < amount) return false;
        UpgradeDataStore.Mutate(player, d => d.Points -= amount);
        SaveCheckpoint(); // 点数变化立即写盘，避免中途退出丢失
        return true;
    }

    /// <summary>UI 触发的扣点（走同步 action，跨端一致）。</summary>
    public static Task<bool> TrySpendPointsAsync(int amount) => UpgradePurchaseFlow.EnqueueSpendPoints(amount);

    /// <summary>UI 触发的加点（走同步 action，跨端一致）。</summary>
    public static Task AddPointsAsync(int amount) => UpgradePurchaseFlow.EnqueueAddPoints(amount);

    /// <summary>保存检查点（战斗开始/结束时调用）。</summary>
    public static void SaveCheckpoint()
    {
        PointsPersistence.SavePoints(CurrentPoints, CurrentSeed());
    }
}
