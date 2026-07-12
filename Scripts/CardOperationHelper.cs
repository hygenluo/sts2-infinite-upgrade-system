using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌操作执行引擎。
///
/// v14 核心逻辑：
/// - 首次升级（originalLevel==0）：CurrentUpgradeLevel 自然变为 1，卡牌外观升级。
///   游戏存档记录 level=1。始终追踪到 CardUpgradeTracker。
/// - 非首次升级（originalLevel>0）：升级后恢复 originalLevel。
///   CardUpgradeTracker 追踪额外次数。
///
/// 读档去重：ReapplyAllUpgrades 中通过 skipCount 处理游戏已自动应用的升级次数。
/// </summary>
public static class CardOperationHelper
{
    public const int UpgradeCost = 5;

    public static Player? GetLocalPlayer()
    {
        var state = RunManager.Instance?.State;
        if (state == null) return null;
        return LocalContext.GetMe(state) ?? state.Players.FirstOrDefault();
    }

    public static async Task<CardModel?> SelectCardFromDeck(Player player, string builtInPromptKey = "")
    {
        var prefs = string.IsNullOrEmpty(builtInPromptKey)
            ? new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            : new CardSelectorPrefs(new LocString("cards", builtInPromptKey), 1);
        var selected = (await CardSelectCmd.FromDeckGeneric(player, prefs)).ToList();
        return selected.Count > 0 ? selected[0] : null;
    }

    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();
        var pileType = card.Pile?.Type ?? PileType.Deck;
        int originalLevel = card.CurrentUpgradeLevel;
        bool wasAlreadyUpgraded = originalLevel > 0;

        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";

        if (wasAlreadyUpgraded)
        {
            // 非首次：恢复到原始等级，追踪额外升级
            WriteUpgradeLevelField(card, originalLevel);
        }
        // else: 首次升级 → CurrentUpgradeLevel 保持为 1，卡牌外观自动变为"已升级"

        // 始终追踪：CardUpgradeTracker 是唯一权威修改记录
        CardUpgradeTracker.RecordModification(card, seed, CardUpgradeTracker.ModType.Upgrade);

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.1: 攻击+1 / 格挡+1
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ModifyDamage(int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var result = await PickAndModifyCard("攻击", c => c.DynamicVars.Damage?.BaseValue,
            c => c.DynamicVars.Damage.BaseValue += 1m,
            CardUpgradeTracker.ModType.DamagePlus);
        if (!result) UpgradePointManager.AddPoints(cost);
        return result;
    }

    public static async Task<bool> ModifyBlock(int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var result = await PickAndModifyCard("格挡", c => c.DynamicVars.Block?.BaseValue,
            c => c.DynamicVars.Block.BaseValue += 1m,
            CardUpgradeTracker.ModType.BlockPlus);
        if (!result) UpgradePointManager.AddPoints(cost);
        return result;
    }

    private static async Task<bool> PickAndModifyCard(string propName,
        Func<CardModel, decimal?> getter, Action<CardModel> modifier,
        CardUpgradeTracker.ModType? modType = null)
    {
        var player = GetLocalPlayer();
        if (player == null) return false;
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player);
            if (card == null) { UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
            if (!card.IsMutable) card = card.ToMutable();
            if (getter(card) == null)
            {
                GD.Print($"此卡牌没有{propName}属性。");
                UpgradeUIHandler.Instance?.SetUIVisible(true);
                return false;
            }
            modifier(card);
            if (modType != null)
            {
                var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
                CardUpgradeTracker.RecordModification(card, seed, modType.Value);
            }
            UpgradeUIHandler.Instance?.RefreshPointsLabel();
            UpgradeUIHandler.Instance?.HideUI();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Card {propName} error: {ex.Message}");
            UpgradeUIHandler.Instance?.SetUIVisible(true);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.2: Keyword 操作
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ToggleKeyword(int cost, CardKeyword keyword, bool add)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var player = GetLocalPlayer();
        if (player == null) return false;
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player);
            if (card == null) { UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
            if (!card.IsMutable) card = card.ToMutable();

            if (add)
            {
                if (card.Keywords.Contains(keyword))
                { GD.Print("此卡牌已有该词条。"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
                card.AddKeyword(keyword);
            }
            else
            {
                if (!card.Keywords.Contains(keyword))
                { GD.Print("此卡牌没有该词条。"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
                card.RemoveKeyword(keyword);
            }
            var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
            var modType = add ? CardUpgradeTracker.ModType.KeywordAdd : CardUpgradeTracker.ModType.KeywordRemove;
            CardUpgradeTracker.RecordModification(card, seed, modType, keyword.ToString());
            UpgradeUIHandler.Instance?.RefreshPointsLabel();
            UpgradeUIHandler.Instance?.HideUI();
            return true;
        }
        catch (Exception ex) { Log.Error($"Keyword error: {ex.Message}"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.3: 能量消耗-1
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ReduceEnergyCost(int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var player = GetLocalPlayer();
        if (player == null) return false;
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player);
            if (card == null) { UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
            if (!card.IsMutable) card = card.ToMutable();
            var cur = card.EnergyCost.Canonical;
            if (cur <= 0) { GD.Print("此卡牌已是0费。"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
            card.EnergyCost.SetCustomBaseCost(cur - 1);
            var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
            CardUpgradeTracker.RecordModification(card, seed, CardUpgradeTracker.ModType.EnergyReduce);
            UpgradeUIHandler.Instance?.RefreshPointsLabel();
            UpgradeUIHandler.Instance?.HideUI();
            return true;
        }
        catch (Exception ex) { Log.Error($"Energy cost error: {ex.Message}"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.4: 抽牌+1 / 重放+1 / 次数+1
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ModifyDrawCount(int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var result = await PickAndModifyCard("抽牌", c => c.DynamicVars.Cards?.BaseValue,
            c => c.DynamicVars.Cards.BaseValue += 1m,
            CardUpgradeTracker.ModType.DrawPlus);
        if (!result) UpgradePointManager.AddPoints(cost);
        return result;
    }

    public static async Task<bool> ModifyReplayCount(int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var player = GetLocalPlayer();
        if (player == null) return false;
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player);
            if (card == null) { UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
            if (!card.IsMutable) card = card.ToMutable();
            card.BaseReplayCount += 1;
            var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
            CardUpgradeTracker.RecordModification(card, seed, CardUpgradeTracker.ModType.ReplayPlus);
            UpgradeUIHandler.Instance?.RefreshPointsLabel();
            UpgradeUIHandler.Instance?.HideUI();
            return true;
        }
        catch (Exception ex) { Log.Error($"Replay error: {ex.Message}"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
    }

    /// <summary>
    /// 与 PerformInfiniteUpgrade 逻辑一致：升级后恢复 originalLevel。
    /// 不追踪 — 调用方负责管理计数。
    /// </summary>
    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();
        int originalLevel = card.CurrentUpgradeLevel;

        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        if (originalLevel > 0)
            WriteUpgradeLevelField(card, originalLevel);
        // else: 首次升级 → 保持 level=1
    }

    public static void WriteUpgradeLevelField(CardModel card, int value)
    {
        typeof(CardModel)
            .GetField("_currentUpgradeLevel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(card, value);
    }

    /// <summary>读档后刷新牌组所有卡牌的外观。</summary>
    public static void RefreshAllVisuals(Player player)
    {
        foreach (var card in player.Deck.Cards)
        {
            var ncard = NCard.FindOnTable(card);
            if (ncard != null)
                ncard.UpdateVisuals(card.Pile?.Type ?? PileType.Deck, CardPreviewMode.Normal);
        }
    }

    public static void ShowUpgradeVfx(CardModel card)
    {
        try
        {
            var container = NRun.Instance?.GlobalUi?.CardPreviewContainer;
            if (container != null)
            {
                var vfx = NCardUpgradeVfx.Create(card);
                container.AddChildSafely(vfx);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("InfiniteUpgrade: upgrade VFX failed (non-fatal): " + ex.Message);
        }
    }
}
