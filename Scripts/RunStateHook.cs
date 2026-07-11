using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 游戏生命周期事件管理 + 持久化。
///
/// 策略：
/// - 新局：Harmony Prefix 拦截 NGame.StartNewSingleplayerRun，删除 points.json 和
///   card_upgrades.json，确保从 5 点 + 无升级开始。
/// - 读档：Harmony Prefix 拦截 NGame.LoadRun，不删文件。
/// - RunStarted：总是从文件加载点数和卡牌升级数据。
/// - 卡牌升级在 RunStarted 后通过 CardUpgradeTracker.ReapplyAllUpgrades 恢复。
/// </summary>
public static class RunStateHook
{
    private static int _combatWonFireCount;
    private static bool _isLoadingSave;

    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        Log.Info("InfiniteUpgrade: subscribed.");
        GD.Print("[InfiniteUpgrade] Subscribed.");
    }

    /// <summary>
    /// 新游戏开始前：删除旧的持久化文件。
    /// 主方案：Patch NGame.StartNewSingleplayerRun（public async）。
    /// 备方案：OnRunStarted 中 TotalFloor <= 1 时再次兜底清理。
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame.StartNewSingleplayerRun))]
    [HarmonyPrefix]
    public static void BeforeNewGame()
    {
        PointsPersistence.DeleteSavedPoints();
        CardUpgradeTracker.Delete();
        GD.Print("[InfiniteUpgrade] BeforeNewGame — deleted persistence files.");
        Log.Info("InfiniteUpgrade: BeforeNewGame — deleted persistence files.");
    }

    /// <summary>
    /// 读档前：设置标志位，防止 OnRunStarted 的兜底清理误删文件。
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame.LoadRun))]
    [HarmonyPrefix]
    public static void BeforeLoadRun()
    {
        _isLoadingSave = true;
        GD.Print("[InfiniteUpgrade] BeforeLoadRun — preserving persistence files.");
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
            Log.Info($"InfiniteUpgrade: CombatWon #{_combatWonFireCount} +{points} → {UpgradePointManager.CurrentPoints}");
            GD.Print($"[InfiniteUpgrade] CombatWon +{points} → {UpgradePointManager.CurrentPoints}");
        }
    }

    private static void OnRunStarted(RunState runState)
    {
        _combatWonFireCount = 0;

        // 兜底检测：如果 BeforeNewGame 未触发（例如从"放弃并重开"路径进入），
        // 通过 TotalFloor 判断这是新局，手动清理旧持久化文件。
        if (runState.TotalFloor <= 1 && !_isLoadingSave)
        {
            PointsPersistence.DeleteSavedPoints();
            CardUpgradeTracker.Delete();
            GD.Print("[InfiniteUpgrade] OnRunStarted backup cleanup — TotalFloor <= 1.");
        }

        // 1. 恢复点数
        int points = PointsPersistence.LoadPoints();
        UpgradePointManager.SetPointsDirect(points);
        GD.Print($"[InfiniteUpgrade] RunStarted — points = {points}");

        // 2. 恢复卡牌升级
        CardUpgradeTracker.Load();
        var player = LocalContext.GetMe(runState) ?? runState.Players.FirstOrDefault();
        if (player != null)
        {
            CardUpgradeTracker.ReapplyAllUpgrades(player);
        }

        _isLoadingSave = false;
    }
}
