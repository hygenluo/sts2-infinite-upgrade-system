using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 游戏生命周期事件管理 + 持久化。
///
/// 核心原理：每个 Run 有唯一的 Rng.StringSeed。
/// 持久化文件 = points_{seed}.json + card_upgrades_{seed}.json
/// 新局 → 新 seed → 文件不存在 → 5 点起步
/// 读档 → 同 seed → 文件存在 → 恢复点数和升级
///
/// 完全不需要检测"新局 vs 读档"——文件系统本身就是答案。
/// </summary>
public static class RunStateHook
{
    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        GD.Print("[InfiniteUpgrade] Subscribed.");
    }

    private static void OnCombatWon(CombatRoom room)
    {
        if (room == null) return;

        int points = room.RoomType switch
        {
            RoomType.Monster => 1,
            RoomType.Elite => 5,
            RoomType.Boss => 20,
            _ => 0
        };

        if (points > 0)
        {
            UpgradePointManager.AddPoints(points);
            GD.Print($"[InfiniteUpgrade] CombatWon +{points} → {UpgradePointManager.CurrentPoints}");
        }
    }

    private static void OnRunStarted(RunState runState)
    {
        var seed = runState.Rng.StringSeed;
        GD.Print($"[InfiniteUpgrade] RunStarted — seed={seed}");

        // 加载点数（按 seed 的文件存在就恢复，不存在就是 5）
        int points = PointsPersistence.LoadPoints(seed);
        UpgradePointManager.SetPointsDirect(points);
        GD.Print($"[InfiniteUpgrade] Points = {points}");

        // 加载卡牌升级
        CardUpgradeTracker.Load(seed);
        var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault();
        if (player != null)
        {
            CardUpgradeTracker.ReapplyAllUpgrades(player);
        }
    }
}
