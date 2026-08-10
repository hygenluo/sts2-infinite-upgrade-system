using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 合成附魔（update05 多种附魔共存）：挂到卡牌的单个 Enchantment 槽位，
/// 内部持有多个子附魔（各带 Amount/层数），转发所有效果方法与战斗 Hook。
///
/// 序列化：走基类 ToSerializable（Id=CompositeEnchantment，游戏反序列化回退为
/// DeprecatedEnchantment，不崩溃），由 CardUpgradeTracker 持久化附魔列表并在读档
/// ReapplyAll 用合成模型覆盖重建。
/// 显示：图标取首个子附魔（_iconPath），标题/描述经 LocStringPromptPatch 统一解析为
/// "多重附魔"，悬停提示聚合所有子附魔。
/// </summary>
public sealed class CompositeEnchantment : EnchantmentModel
{
    public List<EnchantmentModel> Subs { get; } = new();

    /// <summary>用首个子附魔刷新显示身份（图标）；无子附魔时清空。</summary>
    public void RefreshPrimaryDisplay()
    {
        _iconPath = Subs.FirstOrDefault()?.IconPath;
    }

    /// <summary>移除指定类型的子附魔（多种附魔替换用）。一次性 OnEnchant 副作用（如减费）不回滚。</summary>
    public void RemoveSub(string typeName)
    {
        Subs.RemoveAll(s => s.GetType().Name == typeName);
        RefreshPrimaryDisplay();
    }

    // ── 数值修饰符转发 ─────────────────────────────────────────────

    public override decimal EnchantDamageAdditive(decimal originalDamage, ValueProp props) =>
        Subs.Sum(s => s.EnchantDamageAdditive(originalDamage, props));

    public override decimal EnchantDamageMultiplicative(decimal originalDamage, ValueProp props)
    {
        var result = originalDamage;
        foreach (var s in Subs) result = s.EnchantDamageMultiplicative(result, props);
        return result;
    }

    public override decimal EnchantBlockAdditive(decimal originalBlock) =>
        Subs.Sum(s => s.EnchantBlockAdditive(originalBlock));

    public override decimal EnchantBlockMultiplicative(decimal originalBlock)
    {
        var result = originalBlock;
        foreach (var s in Subs) result = s.EnchantBlockMultiplicative(result);
        return result;
    }

    public override int EnchantPlayCount(int originalPlayCount)
    {
        var result = originalPlayCount;
        foreach (var s in Subs) result = s.EnchantPlayCount(result);
        return result;
    }

    // ── 事件/生命周期转发 ──────────────────────────────────────────

    public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        foreach (var s in Subs)
            await s.OnPlay(choiceContext, cardPlay);
    }

    public override void RecalculateValues()
    {
        foreach (var s in Subs) s.RecalculateValues();
    }

    public override void OnEnchant()
    {
        foreach (var s in Subs) s.OnEnchant();
    }

    public override bool ShouldStartAtBottomOfDrawPile => Subs.Any(s => s.ShouldStartAtBottomOfDrawPile);

    // ── 战斗 Hook 转发（子附魔不在战斗监听列表，由合成模型代理） ──

    public override Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay) =>
        ForwardAll(s => s.AfterCardPlayed(context, cardPlay));

    public override Task AfterCardDrawn(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw) =>
        ForwardAll(s => s.AfterCardDrawn(choiceContext, card, fromHandDraw));

    public override Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player) =>
        ForwardAll(s => s.AfterPlayerTurnStart(choiceContext, player));

    public override Task BeforeFlush(PlayerChoiceContext choiceContext, Player player) =>
        ForwardAll(s => s.BeforeFlush(choiceContext, player));

    public override void ModifyShuffleOrder(Player player, List<CardModel> cards, bool isInitialShuffle)
    {
        foreach (var s in Subs) s.ModifyShuffleOrder(player, cards, isInitialShuffle);
    }

    private async Task ForwardAll(System.Func<EnchantmentModel, Task> call)
    {
        foreach (var s in Subs)
            await call(s);
    }

    // ── 显示转发 ───────────────────────────────────────────────────

    public override IEnumerable<IHoverTip> ExtraHoverTips
    {
        get
        {
            var tips = new List<IHoverTip>();
            foreach (var s in Subs) tips.AddRange(s.HoverTips);
            return tips;
        }
    }

    public override bool HasExtraCardText => Subs.Any(s => s.HasExtraCardText);

    public override bool ShowAmount => Subs.Any(s => s.ShowAmount);

    public override IEnumerable<DynamicVar> CanonicalVars =>
        Subs.SelectMany(s => s.DynamicVars.Values);
}
