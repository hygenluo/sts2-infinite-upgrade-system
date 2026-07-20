using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
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

    /// <summary>
    /// 确保卡牌可变，且可变副本被写回牌组。
    /// 直接 ToMutable() 而不写回会导致修改作用于孤儿副本：
    /// 牌组里的原卡不变，且 RecordModification 按引用找卡会失败。
    /// </summary>
    public static CardModel EnsureMutableInDeck(Player player, CardModel card)
    {
        if (card.IsMutable) return card;

        var cards = player.Deck.Cards;
        int index = -1;
        for (int i = 0; i < cards.Count; i++)
            if (ReferenceEquals(cards[i], card)) { index = i; break; }

        var mutable = card.ToMutable();
        if (index >= 0 && player.Deck.Cards is IList<CardModel> deckList && !deckList.IsReadOnly)
            deckList[index] = mutable;
        else
            Log.Warn($"InfiniteUpgrade: cannot write mutable copy of {card.Id.Entry} back to deck (index={index}); modification may not persist.");
        return mutable;
    }

    public static async Task<CardModel?> SelectCardFromDeck(Player player, string builtInPromptKey = "")
    {
        var prefs = string.IsNullOrEmpty(builtInPromptKey)
            ? new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            : new CardSelectorPrefs(new LocString("cards", builtInPromptKey), 1);

        // 启动卡牌选择后延迟一帧，确保 NOverlayStack.Push 已完成并将 UI 节点加入场景树
        var selectTask = CardSelectCmd.FromDeckGeneric(player, prefs);
        await Task.Delay(33); // 2 帧 — Push() + 子节点 _Ready()
        EnsureOverlayOnTop();

        var selected = (await selectTask).ToList();
        RestoreOverlayPosition(); // 恢复原位，避免打乱 GlobalUi 场景树结构
        return selected.Count > 0 ? selected[0] : null;
    }

    private static int s_overlayOriginalIndex = -1;

    /// <summary>临时将 NOverlayStack 提升到其父节点子列表末尾，确保卡牌选择界面渲染在最顶层。</summary>
    private static void EnsureOverlayOnTop()
    {
        try
        {
            var overlays = NRun.Instance?.GlobalUi?.Overlays;
            if (overlays == null) return;
            var parent = overlays.GetParent();
            if (parent != null)
            {
                s_overlayOriginalIndex = overlays.GetIndex();
                if (s_overlayOriginalIndex < parent.GetChildCount() - 1)
                    parent.MoveChild(overlays, parent.GetChildCount() - 1);
            }
        }
        catch (Exception)
        {
            // 非致命：图层提升失败不影响核心功能
        }
    }

    /// <summary>将 NOverlayStack 恢复到提升前的位置，避免破坏游戏 UI 层级结构。</summary>
    private static void RestoreOverlayPosition()
    {
        try
        {
            if (s_overlayOriginalIndex < 0) return;
            var overlays = NRun.Instance?.GlobalUi?.Overlays;
            if (overlays == null) return;
            var parent = overlays.GetParent();
            if (parent != null)
                parent.MoveChild(overlays, s_overlayOriginalIndex);
            s_overlayOriginalIndex = -1;
        }
        catch (Exception)
        {
            // 非致命
        }
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
            card = EnsureMutableInDeck(player, card);
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
            card = EnsureMutableInDeck(player, card);

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
            card = EnsureMutableInDeck(player, card);
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
            card = EnsureMutableInDeck(player, card);
            card.BaseReplayCount += 1;
            var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
            CardUpgradeTracker.RecordModification(card, seed, CardUpgradeTracker.ModType.ReplayPlus);
            UpgradeUIHandler.Instance?.RefreshPointsLabel();
            UpgradeUIHandler.Instance?.HideUI();
            return true;
        }
        catch (Exception ex) { Log.Error($"Replay error: {ex.Message}"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 6: 牌组操作
    // ═══════════════════════════════════════════════════════════════

    /// <summary>从牌组选择一张牌删除。</summary>
    public static async Task<bool> RemoveCardFromDeck(int cost)
    {
        if (!UpgradePointManager.TrySpendPoints(cost)) return false;
        var player = GetLocalPlayer();
        if (player == null) { UpgradePointManager.AddPoints(cost); return false; }
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player);
            if (card == null) { UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }

            // 删除前记下标，删除后修正 CardUpgradeTracker 的按下标记录
            var deckCards = player.Deck.Cards;
            int removedIndex = -1;
            for (int i = 0; i < deckCards.Count; i++)
                if (ReferenceEquals(deckCards[i], card)) { removedIndex = i; break; }

            await MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromDeck(card);

            var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
            CardUpgradeTracker.OnCardRemoved(removedIndex, seed);

            UpgradeUIHandler.Instance?.RefreshPointsLabel();
            UpgradeUIHandler.Instance?.HideUI();
            return true;
        }
        catch (Exception ex) { Log.Error($"RemoveCard: {ex.Message}"); UpgradePointManager.AddPoints(cost); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
    }

    /// <summary>从指定卡牌池随机添加一张牌到牌组（免费）。</summary>
    public static async Task<bool> AddCardFromPool(string poolKey)
    {
        GD.Print($"[IU] AddCardFromPool START: key={poolKey}");
        var player = GetLocalPlayer();
        GD.Print($"[IU] AddCardFromPool player={player != null}");
        if (player == null) return false;
        try
        {
            var poolTypeName = poolKey switch
            {
                "red" => "MegaCrit.Sts2.Core.Models.CardPools.IroncladCardPool",
                "green" => "MegaCrit.Sts2.Core.Models.CardPools.SilentCardPool",
                "blue" => "MegaCrit.Sts2.Core.Models.CardPools.DefectCardPool",
                "purple" => "MegaCrit.Sts2.Core.Models.CardPools.NecrobinderCardPool",
                "orange" => "MegaCrit.Sts2.Core.Models.CardPools.RegentCardPool",
                "colorless" => "MegaCrit.Sts2.Core.Models.CardPools.ColorlessCardPool",
                "curse" => "MegaCrit.Sts2.Core.Models.CardPools.CurseCardPool",
                _ => null
            };
            if (poolTypeName == null) return false;

            Type? poolType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { poolType = asm.GetType(poolTypeName); if (poolType != null) break; }
            if (poolType == null) { GD.PrintErr($"Pool type not found: {poolTypeName}"); return false; }

            var modelDb = typeof(MegaCrit.Sts2.Core.Models.ModelDb);
            var poolGetter = modelDb.GetMethod("CardPool", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)!;
            var pool = poolGetter.MakeGenericMethod(poolType).Invoke(null, null) as MegaCrit.Sts2.Core.Models.CardPoolModel;
            if (pool == null) { GD.PrintErr($"Pool null for {poolKey}"); return false; }

            var candidates = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint).ToList();
            if (candidates.Count == 0) { GD.Print($"没有{poolKey}卡牌。"); return false; }

            var pick = candidates[new System.Random().Next(candidates.Count)];
            var card = pick.ToMutable();
            card.Owner = player;  // 必须设置 Owner，否则 CardPileCmd.Add 抛异常
            card.FloorAddedToDeck = player.RunState.TotalFloor;
            await MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck);
            GD.Print($"已添加{poolKey}牌: {card.Id.Entry}");
            return true;
        }
        catch (Exception ex) { Log.Error($"AddCard: {ex.Message}"); return false; }
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
