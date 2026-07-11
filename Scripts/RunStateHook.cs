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
/// 策略（不使用 Harmony patch NGame 方法，避免 async 方法匹配问题）：
/// - 新局检测：RunStarted 时 TotalFloor == 0 → 删除持久化文件，5 点，无升级
/// - 读档检测：RunStarted 时 TotalFloor > 0  → 加载持久化文件，恢复点数和升级
/// - 战斗奖励：CombatWon 事件
/// </summary>
public static class RunStateHook
{
    private static int _combatWonFireCount;

    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        Log.Info("InfiniteUpgrade: subscribed.");
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

        _combatWonFireCount++;

        if (points > 0)
        {
            UpgradePointManager.AddPoints(points);
            GD.Print($"[InfiniteUpgrade] CombatWon +{points} → {UpgradePointManager.CurrentPoints}");
        }
    }

    private static void OnRunStarted(RunState runState)
    {
        _combatWonFireCount = 0;

        // TotalFloor: MapPointHistory 中所有楼层数之和
        // 新局 = 尚未进入任何房间 = TotalFloor == 0
        // 读档 = 存档中有已访问的房间记录 = TotalFloor > 0
        bool isNewRun = runState.TotalFloor == 0;

        GD.Print($"[InfiniteUpgrade] RunStarted — TotalFloor={runState.TotalFloor}, isNewRun={isNewRun}");

        if (isNewRun)
        {
            // 新局：清空旧持久化文件，从 5 点零升级开始
            PointsPersistence.DeleteSavedPoints();
            CardUpgradeTracker.Delete();
            UpgradePointManager.InitializeDirect();
            GD.Print("[InfiniteUpgrade] NEW RUN — points=5, upgrades cleared.");
        }
        else
        {
            // 读档：从文件恢复
            int points = PointsPersistence.LoadPoints();
            UpgradePointManager.SetPointsDirect(points);
            GD.Print($"[InfiniteUpgrade] LOADED SAVE — points={points}");

            CardUpgradeTracker.Load();
            var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault();
            if (player != null)
            {
                CardUpgradeTracker.ReapplyAllUpgrades(player);
            }
        }
    }
}
