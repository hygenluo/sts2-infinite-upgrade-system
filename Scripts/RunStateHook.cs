using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 直接订阅游戏 C# 事件（非 Harmony）：
/// - CombatManager.Instance.CombatWon → 发放点数
/// - RunManager.Instance.RunStarted  → 重置点数
///
/// CombatManager.Instance 和 RunManager.Instance 都是 static readonly 字段，
/// 在游戏启动时就已存在，因此可以直接在 Entry.Init() 中订阅事件。
/// </summary>
public static class RunStateHook
{
    private static int _combatWonFireCount;

    /// <summary>
    /// 在 Entry.Init() 中调用，订阅游戏生命周期事件。
    /// </summary>
    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        Log.Info("InfiniteUpgrade: subscribed to CombatWon + RunStarted.");
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

        _combatWonFireCount++;

        if (points > 0)
        {
            UpgradePointManager.AddPoints(points);
            Log.Info($"InfiniteUpgrade: CombatWon #{_combatWonFireCount} — +{points} pts from {room.RoomType} → total {UpgradePointManager.CurrentPoints}");
        }
        else
        {
            Log.Info($"InfiniteUpgrade: CombatWon #{_combatWonFireCount} — {room.RoomType}, no points awarded.");
        }
    }

    private static void OnRunStarted(RunState runState)
    {
        _combatWonFireCount = 0;

        // TotalFloor > 1 表示读档（已有地图移动记录），从文件恢复点数；
        // TotalFloor <= 1 表示新局，重置为 5。
        if (runState.TotalFloor > 1)
        {
            int savedPoints = PointsPersistence.LoadPoints();
            UpgradePointManager.SetPoints(savedPoints);
            Log.Info($"InfiniteUpgrade: RunStarted (loaded save) — restored {savedPoints} points.");
        }
        else
        {
            PointsPersistence.DeleteSavedPoints();
            UpgradePointManager.Initialize();
            Log.Info("InfiniteUpgrade: RunStarted (new run) — points reset to 5.");
        }
    }
}
