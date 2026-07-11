using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 游戏生命周期事件管理 + 持久化。
///
/// 新局检测：Harmony Prefix 拦截 RunState.CreateForNewRun（public static sync）。
///   这是所有新游戏（单机/联机/每日）必经的底层方法，非 async，Harmony 可靠匹配。
/// 读档检测：TotalFloor > 0 时从文件恢复点数和卡牌升级。
/// </summary>
public static class RunStateHook
{
    private static int _combatWonFireCount;
    private static bool _isNewRun;

    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        Log.Info("InfiniteUpgrade: subscribed.");
        GD.Print("[InfiniteUpgrade] Subscribed.");
    }

    /// <summary>
    /// 新游戏创建 RunState 前删除旧持久化文件。
    /// CreateForNewRun 是 public static 同步方法，无 async 匹配问题。
    /// </summary>
    [HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
    [HarmonyPrefix]
    public static void BeforeCreateNewRun()
    {
        _isNewRun = true;
        PointsPersistence.DeleteSavedPoints();
        CardUpgradeTracker.Delete();
        GD.Print("[InfiniteUpgrade] CreateForNewRun — deleted persistence files.");
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

        if (_isNewRun)
        {
            _isNewRun = false;
            UpgradePointManager.InitializeDirect();
            GD.Print("[InfiniteUpgrade] NEW RUN — points=5, upgrades cleared.");
        }
        else
        {
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

