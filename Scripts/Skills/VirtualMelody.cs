using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using STS2RitsuLib.RunRngs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 「虚拟旋律」：本 mod 的愚者技能在回合开始时白给一个旋律（哇 / 啾 / 叽），**不发牌**。
///
/// ── 为什么必须这么做 ────────────────────────────────────────────────
/// 游戏里旋律**不是 buff、也不是资源**：`CardTag.Wah/Cho/Ki` 只是卡牌标签，
/// 「拥有旋律」的全部判定都走 `MelodyCmd.PlayedThisTurn(player, tag)`
/// （内部查 CombatHistory：本回合有没有打出过带该标签的牌）。
/// 官方文本「每当你获得旋律时…」= 本回合**首次**打出带该标签的牌
/// （见 `LostLutePower` / `MotifPower` / `ViolentChordPower` 的实现：数 CardPlaysStarted 里该标签的次数 == 1）。
///
/// 所以要让技能「获得一个旋律」而不塞一张牌，就在 `MelodyCmd.PlayedThisTurn` 上挂 Postfix，
/// 把本回合白给的标签也算成「拥有」。一处补丁覆盖所有消费者：
/// `MelodiesThisTurn` / `PlayedWah|Cho|KiThisTurn` / 旋律射击与节奏打击的命中数 /
/// 尾声（Coda）减费 / 谐音盾 / 稳定节拍 / 哇爆 / 叽息 / 曲调（Tune）/ 摇篮曲 /
/// 歌径（SongPath），以及愚者战斗 UI 的旋律图标（`NFoolCombatUi.SetMelodyAmounts`）。
///
/// ── 生命周期与多人 ────────────────────────────────────────────────
/// - 授予：`SkillEffectsPower.AfterPlayerTurnStart`（拥有该技能的玩家自己的回合开始）；
/// - 清理：`SkillEffectsPower.BeforeSideTurnStart`（下一个自己的回合开始前），
///   与游戏 `HappenedThisTurn` 的「回合」口径一致（按 RoundNumber，敌方回合仍算本回合）；
/// - 随机源：RitsuLib 每玩家确定性 RNG 流（`ModRunRngRegistry`，本 mod 自有的流，
///   随 run save 保存、两端派生一致），**不用** `System.Random`/`GD.Rand`，也不去消耗原版战斗随机流；
/// - 状态只按玩家实例存，两端在同一个 hook 里同样地授予/清理 → 判定一致。
/// </summary>
public static class VirtualMelody
{
    /// <summary>本回合白给的旋律（player → tag）。只存一个（每回合一个）。</summary>
    private static readonly Dictionary<Player, CardTag> s_granted = new();

    /// <summary>三个合法旋律标签。</summary>
    public static readonly CardTag[] Tags = [CardTag.Wah, CardTag.Cho, CardTag.Ki];

    /// <summary>授予一个旋律标签（覆盖上一回合的）。</summary>
    public static void Grant(Player player, CardTag tag)
    {
        if (player == null) return;
        s_granted[player] = tag;
    }

    /// <summary>清除该玩家的虚拟旋律（新回合开始 / 战斗结束）。</summary>
    public static void Clear(Player player)
    {
        if (player == null) return;
        s_granted.Remove(player);
    }

    /// <summary>清空（换局/开局时防止旧 Player 实例残留）。</summary>
    public static void ClearAll() => s_granted.Clear();

    /// <summary>该玩家本回合是否「拥有」这个旋律。</summary>
    public static bool Has(Player player, CardTag tag) =>
        player != null && s_granted.TryGetValue(player, out var granted) && granted == tag;

    /// <summary>
    /// 随机取一个旋律标签。随机源是 RitsuLib 的每玩家 run RNG 流（"melody"），
    /// 两端按同序消耗 → 抽到同一个标签。
    /// 若玩家不属于具体 RunState（脱离对局），两端会同样失败 → 返回 None（不发旋律），不会单边分歧。
    /// </summary>
    public static CardTag PickRandom(Player player)
    {
        if (player == null) return CardTag.None;
        try
        {
            var rng = ModRunRngRegistry.Get(player, Entry.ModId, "melody");
            return Tags[rng.NextInt(0, Tags.Length)];
        }
        catch (System.Exception ex)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"IU melody rng unavailable: {ex.Message}");
            return CardTag.None;
        }
    }
}

/// <summary>
/// `MelodyCmd.PlayedThisTurn` 的 Postfix：原版判定为 false 时，再问一次「虚拟旋律」。
/// 只在结果为 false 时多一次字典查询，不影响原版语义与性能。
///
/// `HarmonyPriority(Priority.Last)`：实测有别的 mod（HextechRunes）也在这个方法上挂 Postfix，
/// 排在最后才保证「原版 false + 别人 false + 我们的虚拟旋律」这个 OR 组合不会被后续补丁覆盖。
/// </summary>
[HarmonyPatch(typeof(MelodyCmd), nameof(MelodyCmd.PlayedThisTurn))]
public static class VirtualMelodyPatch
{
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Player player, CardTag cardTag, ref bool __result)
    {
        if (__result) return;
        __result = VirtualMelody.Has(player, cardTag);
    }
}
