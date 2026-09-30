using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace InfiniteUpgradeSystem;

/// <summary>可回退项的种类。</summary>
public enum RefundKind
{
    Boost,   // 基础属性 / 能力（store.Boosts[key] 层数）
    Skill,   // 技能（store.Skills[id] 等级）
    CardMod, // 卡牌操作（CardUpgradeTracker 记录）
}

/// <summary>
/// 回退项（UI 流水行 + 回退请求参数）。
/// </summary>
public sealed class RefundEntry
{
    public RefundKind Kind { get; init; }

    /// <summary>回退目标：Boost 的 key / Skill 的 id / CardMod 的卡牌身份（`STRIKE__0`）。</summary>
    public required string Target { get; init; }

    /// <summary>当初**实际支付**的点数（返还 = floor(0.8 × 该值)）。</summary>
    public int PaidCost { get; init; }

    /// <summary>CardMod 专用：<see cref="CardUpgradeTracker.ModType"/> 的整数值。</summary>
    public int ModType { get; init; }

    /// <summary>显示名（已本地化）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>附加说明（卡牌名 / 词条名 / 旧记录说明）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>是否可回退（false → 按钮置灰并显示 <see cref="Note"/>）。</summary>
    public bool Refundable { get; init; } = true;

    /// <summary>不可回退原因（本地化文本）。</summary>
    public string? Note { get; init; }

    /// <summary>该卡还有多少次更早的修改（后进先出提示）。</summary>
    public int OlderMods { get; init; }

    /// <summary>搜索语料（小写）。</summary>
    public string SearchText { get; init; } = "";

    /// <summary>本次回退返还的点数。</summary>
    public int Refund => UpgradeRefundService.RefundOf(PaidCost);

    public bool IsSkill => Kind == RefundKind.Skill;
    public bool IsCardMod => Kind == RefundKind.CardMod;
}

/// <summary>
/// 回退服务（v2.3）：把「加错了的加点」撤销并把点数按 **80% 下取整** 返还。
///
/// ── 规则 ──────────────────────────────────────────────────────────
/// - 返还 = floor(已付点数 × 0.8)：10 → 8，7 → 5（5.6 下取整），1 → 0。
/// - 可回退：基础属性 / 能力（每级一项）· 技能（每项一级）· 卡牌操作中的
///   数值类 / 词条类 / 耗能 / 附魔。
/// - 不可回退：**升级卡牌**（游戏 `DowngradeInternal` 会把整卡重置为原型，无法只撤销一次升级）、
///   **从牌组删牌**（牌已不在牌组，记录也已被丢弃）、**测试加点**（白给的点数）。
/// - 卡牌操作按 **后进先出** 回退（每张卡只能先回退最近一次修改）——
///   这样才能保证「回退后的状态」与「读档重放剩余记录」的结果永远一致。
///
/// ── 多人一致性 ────────────────────────────────────────────────────
/// 回退是状态变更，走 <see cref="Multiplayer.UpgradeDataOp.Refund"/> 同步 action：
/// action 参数只携带「目标 + 当初付了多少点」，两端各自按自己那份**已一致的 store/记录**
/// 做同样的校验与撤销，因此结论必然一致（找不到目标/类型不匹配 → 两端都 no-op）。
/// </summary>
public static class UpgradeRefundService
{
    /// <summary>返还比例（80%）。</summary>
    public const float RefundRatio = 0.8f;

    /// <summary>返还点数 = floor(已付 × 0.8)。cost ≤ 0 → 0。</summary>
    public static int RefundOf(int paidCost) =>
        paidCost <= 0 ? 0 : (int)Math.Floor(paidCost * RefundRatio);

    /// <summary>UI 触发回退（跨端同步）。</summary>
    public static Task<bool> Enqueue(RefundEntry entry) =>
        Multiplayer.UpgradePurchaseFlow.EnqueueRefund(
            entry.Target, entry.PaidCost, entry.ModType, entry.IsSkill, entry.IsCardMod);

    /// <summary>
    /// 执行回退（由 Refund action 两端调用）。
    /// 返回 false = 校验失败（目标不存在/类型不匹配/不可逆），两端都不产生任何变更。
    /// </summary>
    public static async Task<bool> Execute(Player player, string target, int paidCost, int modType,
        bool isSkill, bool isCardMod)
    {
        if (player == null || string.IsNullOrEmpty(target)) return false;
        int refund = RefundOf(paidCost);
        bool ok;

        if (isCardMod)
        {
            ok = CardOperationHelper.TryRefundCardMod(
                player, target, (CardUpgradeTracker.ModType)modType, refund);
        }
        else
        {
            ok = RemoveOneLevel(player, target, isSkill);
            if (ok)
            {
                if (refund > 0) UpgradePointManager.AddPoints(player, refund); // 内部写盘
                if (!isSkill) await AbilityOperationHelper.ApplyImmediateRefund(player, target);
                UpgradePointManager.SaveCheckpoint();
            }
        }

        var kind = isCardMod ? "card" : (isSkill ? "skill" : "boost");
        if (ok)
            Log.Info($"IU Refund OK kind={kind} target={target} paid={paidCost} refund={refund} " +
                     $"pts_after={UpgradeDataStore.GetPoints(player)} player={player.NetId}");
        else
            Log.Warn($"IU Refund FAILED kind={kind} target={target} paid={paidCost} " +
                     $"pts={UpgradeDataStore.GetPoints(player)} player={player.NetId}");

        await Task.CompletedTask;
        return ok;
    }

    /// <summary>回退一级 boost / 技能（写 store；等级归零时移除该 key）。</summary>
    private static bool RemoveOneLevel(Player player, string key, bool isSkill)
    {
        var data = UpgradeDataStore.For(player);
        var dict = isSkill ? data.Skills : data.Boosts;
        if (!dict.TryGetValue(key, out int level) || level <= 0) return false;

        UpgradeDataStore.Mutate(player, d =>
        {
            var target = isSkill ? d.Skills : d.Boosts;
            if (!target.TryGetValue(key, out int cur) || cur <= 0) return;
            if (cur - 1 <= 0) target.Remove(key);
            else target[key] = cur - 1;
        });
        return true;
    }
}
