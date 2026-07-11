using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 游戏生命周期事件管理。
///
/// 关键发现：新局（NGame.StartRun）和读档（NGame.LoadRun）都会调用
/// RunManager.Instance.Launch()，从而触发 RunStarted 事件。
/// 仅通过 RunState.TotalFloor 无法可靠区分（刚开局就存档时 TotalFloor 也很小）。
///
/// 解决方案：Harmony Prefix 拦截 NGame.LoadRun 设置标志位，
/// 在 OnRunStarted 中根据标志位决定是恢复存档还是重置点数。
/// </summary>
public static class RunStateHook
{
    private static int _combatWonFireCount;
    private static bool _isLoadingSave;

    /// <summary>
    /// 在 Entry.Init() 中调用，订阅游戏生命周期事件。
    /// </summary>
    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        Log.Info("InfiniteUpgrade: subscribed to CombatWon + RunStarted.");
        GD.Print("[InfiniteUpgrade] Subscribed to CombatWon + RunStarted.");
    }

    /// <summary>
    /// Harmony Prefix：在 NGame.LoadRun 开始执行前设置标志位。
    /// 虽然是 async 方法，但 Prefix 在状态机启动前同步执行，安全可靠。
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame.LoadRun))]
    [HarmonyPrefix]
    public static void BeforeLoadRun()
    {
        _isLoadingSave = true;
        Log.Info("InfiniteUpgrade: [BeforeLoadRun] _isLoadingSave = TRUE");
        GD.Print("[InfiniteUpgrade] BeforeLoadRun called — will restore points from disk.");
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

        GD.Print($"[InfiniteUpgrade] RunStarted fired — _isLoadingSave={_isLoadingSave}, TotalFloor={runState.TotalFloor}");

        if (_isLoadingSave)
        {
            _isLoadingSave = false;
            int savedPoints = PointsPersistence.LoadPoints();
            UpgradePointManager.SetPointsDirect(savedPoints);
            Log.Info($"InfiniteUpgrade: RunStarted (loaded save) — restored {savedPoints} points.");
            GD.Print($"[InfiniteUpgrade] LOADED SAVE — restored {savedPoints} points.");
        }
        else
        {
            PointsPersistence.DeleteSavedPoints();
            UpgradePointManager.InitializeDirect();
            Log.Info("InfiniteUpgrade: RunStarted (new run) — points reset to 5.");
            GD.Print("[InfiniteUpgrade] NEW RUN — points reset to 5.");
        }
    }
}
