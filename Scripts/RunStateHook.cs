using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 游戏生命周期 + 检查点持久化。
///
/// 检查点策略：
/// - 战斗开始 (CombatSetUp)  → 保存检查点
/// - 战斗胜利 (CombatWon)    → 保存检查点（含奖励点数）
/// - 新局 (RunStarted + 无文件) → 初始 5 点
/// - 读档 (RunStarted + 有文件) → 恢复到最近检查点
///
/// 修改只存在于内存中，仅在检查点时写盘。
/// 退出重进 → 回到最近检查点 → 未到检查点的修改自动回滚。
/// </summary>
public static class RunStateHook
{
    public static void Subscribe()
    {
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        GD.Print("[InfiniteUpgrade] Subscribed.");
    }

    /// <summary>战斗开始 → 应用能力 + 保存检查点</summary>
    private static void OnCombatSetUp(CombatState state)
    {
        AbilityOperationHelper.ApplyInitialBoosts();
        AbilityOperationHelper.ApplyStarsAtCombatStart();
        UpgradePointManager.SaveCheckpoint();
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.SaveCheckpoint(seed);
        AbilityOperationHelper.SaveCheckpoint(seed);
        GD.Print($"[InfiniteUpgrade] Checkpoint SAVED (combat start) — pts={UpgradePointManager.CurrentPoints}");
    }

    /// <summary>战斗胜利 → 发放点数 → 保存检查点</summary>
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
            UpgradePointManager.AddPoints(points);

        UpgradePointManager.SaveCheckpoint();
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.SaveCheckpoint(seed);
        AbilityOperationHelper.SaveCheckpoint(seed);
        AbilityOperationHelper.ResetForNextCombat();  // 下场战斗重新应用初始能力
        GD.Print($"[InfiniteUpgrade] Checkpoint SAVED (combat won, +{points}) — pts={UpgradePointManager.CurrentPoints}");
    }

    private static void OnRunStarted(RunState runState)
    {
        var seed = runState.Rng.StringSeed;
        GD.Print($"[InfiniteUpgrade] RunStarted — seed={seed}");

        int points = PointsPersistence.LoadPoints(seed);
        UpgradePointManager.SetPointsDirect(points);
        GD.Print($"[InfiniteUpgrade] Points = {points}");

        CardUpgradeTracker.Load(seed);
        AbilityOperationHelper.Load(seed);
        AbilityOperationHelper.ResetForNewRun();
        var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault();
        if (player != null)
        {
            CardUpgradeTracker.ReapplyAll(player);
            CardOperationHelper.RefreshAllVisuals(player);
        }
    }
}
