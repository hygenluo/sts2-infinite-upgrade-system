using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 使用 Harmony 补丁挂钩游戏生命周期事件：
/// - 战斗胜利时根据房间类型发放点数
/// - 新对局开始时重置点数
/// </summary>
public static class RunStateHook
{
    /// <summary>
    /// 战斗胜利后发放点数：普通怪 +1，精英 +5，BOSS +20
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal))]
    [HarmonyPostfix]
    public static void AfterCombatWon()
    {
        var room = RunManager.Instance?.State?.CurrentRoom as CombatRoom;
        if (room == null) return;

        var roomType = room.RoomType;
        int points = roomType switch
        {
            RoomType.Monster => 1,
            RoomType.Elite => 5,
            RoomType.Boss => 20,
            _ => 0
        };

        if (points > 0)
        {
            UpgradePointManager.AddPoints(points);
            Log.Info($"InfiniteUpgrade: +{points} points from {roomType} combat. Total: {UpgradePointManager.CurrentPoints}");
        }
    }

    /// <summary>
    /// 新对局开始时重置点数为 5
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    [HarmonyPostfix]
    public static void AfterRunLaunch()
    {
        UpgradePointManager.Initialize();
        Log.Info("InfiniteUpgrade: points reset to 5 for new run.");
    }
}
