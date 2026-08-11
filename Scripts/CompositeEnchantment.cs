using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
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
    /// <summary>子附魔列表（MutableClone 时经 DeepCloneFields 深拷贝为独立列表）。</summary>
    public List<EnchantmentModel> Subs = new();

    public override void DeepCloneFields()
    {
        base.DeepCloneFields();
        // MutableClone 的 MemberwiseClone 会共享 Subs 引用，且游戏克隆卡牌时也会克隆附魔
        // —— 必须深拷贝子附魔（克隆每个子附魔），否则克隆出的合成附魔丢失全部子附魔。
        var old = Subs;
        Subs = new List<EnchantmentModel>(old.Count);
        foreach (var s in old)
            Subs.Add((EnchantmentModel)s.ClonePreservingMutability());
    }
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

    public override decimal EnchantDamageAdditive(decimal originalDamage, ValueProp props)
    {
        var result = Subs.Sum(s => s.EnchantDamageAdditive(originalDamage, props));
        LogDamageOnce($"additive result={result} subs=[{string.Join(",", Subs.Select(s => $"{s.GetType().Name}(Amount={s.Amount})"))}]");
        return result;
    }

    public override decimal EnchantDamageMultiplicative(decimal originalDamage, ValueProp props)
    {
        if (Subs.Count == 0) return 1m;
        var result = 1m;
        foreach (var s in Subs) result *= s.EnchantDamageMultiplicative(originalDamage, props);
        LogDamageOnce($"multiplicative result={result} subs=[{string.Join(",", Subs.Select(s => $"{s.GetType().Name}(Amount={s.Amount})"))}]");
        return result;
    }

    private static bool s_damageDiagLogged;
    private static void LogDamageOnce(string msg)
    {
        if (s_damageDiagLogged) return;
        s_damageDiagLogged = true;
        Godot.GD.Print($"[IU-Ench] {msg}");
    }

    public override decimal EnchantBlockAdditive(decimal originalBlock) =>
        Subs.Sum(s => s.EnchantBlockAdditive(originalBlock));

    public override decimal EnchantBlockMultiplicative(decimal originalBlock)
    {
        if (Subs.Count == 0) return 1m;
        var result = 1m;
        foreach (var s in Subs) result *= s.EnchantBlockMultiplicative(originalBlock);
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

    /// <summary>最近一次生成的卡面附加文本摘要（子附魔名称，供 LocStringPromptPatch 解析 extraCardText）。</summary>
    public static string? LastSummary;

    // 子附魔的描述/数值由 ExtraHoverTips 聚合展示，合成附魔自身不聚合子附魔变量
    // （避免子附魔 DynamicVar 名称冲突/渲染异常）。
}

/// <summary>
/// 卡面附加文本（DynamicExtraCardText 非虚不能重写）：用 Harmony Postfix 在基类 getter 后
/// 生成子附魔名称摘要（LastSummary），供 LocStringPromptPatch 解析合成附魔的 extraCardText key。
/// </summary>
[HarmonyPatch(typeof(EnchantmentModel), "get_DynamicExtraCardText")]
public static class CompositeEnchantmentExtraCardTextPatch
{
    public static void Postfix(EnchantmentModel __instance)
    {
        if (__instance is CompositeEnchantment composite && composite.HasExtraCardText)
        {
            CompositeEnchantment.LastSummary = string.Join(
                "、", composite.Subs.Select(s => s.Title.GetFormattedText()));
        }
    }
}

/// <summary>
/// 卡牌克隆后（CardModel.DeepCloneFields 内部 EnchantInternal 只设合成附魔的 Card），
/// 子附魔克隆的 Card 被清空——重新挂到克隆卡上，避免依赖 base.Card 的子附魔 Hook 空引用。
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.DeepCloneFields))]
public static class CompositeEnchantmentCardClonePatch
{
    public static void Postfix(CardModel __instance)
    {
        if (__instance?.Enchantment is not CompositeEnchantment composite) return;
        foreach (var sub in composite.Subs)
        {
            try
            {
                if (sub.Card == null)
                    sub.ApplyInternal(__instance, sub.Amount);
            }
            catch { /* 卡不可变/子附魔异常：忽略，不影响克隆 */ }
        }
    }
}
