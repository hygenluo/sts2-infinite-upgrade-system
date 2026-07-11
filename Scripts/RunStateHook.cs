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
/// 持久化策略（简化版）：
/// 1. 每次点数变化 → SavePoints 写入 points.json（存于 mod DLL 目录）
/// 2. 新局 → Harmony Prefix 拦截 NGame.StartRun，先删除 points.json 再让流程继续
/// 3. 读档 → NGame.LoadRun 不会触发 StartRun，points.json 保留 → RunStarted 时恢复
/// 4. RunStarted 总是从 points.json 加载（新局=5，读档=上次保存的值）
///
/// 不再使用 _isLoadingSave 标志位或 TotalFloor 检测。
/// </summary>
public static class RunStateHook
{
    private static int _combatWonFireCount;

    public static void Subscribe()
    {
        CombatManager.Instance.CombatWon += OnCombatWon;
        RunManager.Instance.RunStarted += OnRunStarted;
        Log.Info("InfiniteUpgrade: subscribed to CombatWon + RunStarted.");
        GD.Print("[InfiniteUpgrade] Subscribed.");
    }

    /// <summary>
    /// 新局开始前删除旧的点数文件，确保 RunStarted 时从 5 开始。
    /// Prefix 在 async 状态机启动前同步执行。
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame.StartRun))]
    [HarmonyPrefix]
    public static void BeforeStartRun()
    {
        PointsPersistence.DeleteSavedPoints();
        GD.Print("[InfiniteUpgrade] BeforeStartRun — deleted points file for fresh run.");
    }

    /// <summary>
    /// 读档时不会调用 StartRun（不会删文件），因此 points.json 保留。
    /// Harmony Prefix 在 async 状态机启动前同步执行。
    /// </summary>
    [HarmonyPatch(typeof(NGame), nameof(NGame.LoadRun))]
    [HarmonyPrefix]
    public static void BeforeLoadRun()
    {
        GD.Print("[InfiniteUpgrade] BeforeLoadRun — points file preserved for restore.");
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
            Log.Info($"InfiniteUpgrade: CombatWon #{_combatWonFireCount} — +{points} pts → total {UpgradePointManager.CurrentPoints}");
            GD.Print($"[InfiniteUpgrade] CombatWon +{points} → total {UpgradePointManager.CurrentPoints}");
        }
    }

    private static void OnRunStarted(RunState runState)
    {
        _combatWonFireCount = 0;

        // 总是从文件加载：新局时文件被 BeforeStartRun 删除 → LoadPoints 返回 5
        //              读档时文件保留 → LoadPoints 返回上次保存的值
        int points = PointsPersistence.LoadPoints();
        UpgradePointManager.SetPointsDirect(points);
        Log.Info($"InfiniteUpgrade: RunStarted — points = {points}");
        GD.Print($"[InfiniteUpgrade] RunStarted — points = {points}");
    }
}
