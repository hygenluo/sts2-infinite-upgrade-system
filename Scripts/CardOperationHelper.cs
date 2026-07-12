using System.Linq;
using System.Reflection;
using HarmonyLib;
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
/// 持久化策略（v16）：
/// - 升级后 CurrentUpgradeLevel 始终重置为 0，由 CardUpgradeTracker 管理一切。
/// - Harmony patch get_IsUpgraded：有追踪升级的卡牌强制返回 true，确保外观正确。
/// - 不修改 CurrentUpgradeLevel → 游戏存档记录 0 → 读档不会重复应用升级。
/// </summary>
public static class CardOperationHelper
{
    public const int UpgradeCost = 5;

    public static Player? GetLocalPlayer()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
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

        // 已达最大等级 → 临时重置以通过 setter 校验
        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        // 重置为 0 — 游戏存档不参与我们的升级持久化
        WriteUpgradeLevelField(card, 0);

        // CardUpgradeTracker 是唯一升级记录来源
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.RecordUpgrade(card, seed);

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();
        int originalLevel = card.CurrentUpgradeLevel;
        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();
        WriteUpgradeLevelField(card, 0);
    }

    /// <summary>
    /// Harmony patch：有 CardUpgradeTracker 追踪的卡牌强制 IsUpgraded = true。
    /// 不修改 CurrentUpgradeLevel → 存档安全 → 读档不会重复应用升级。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), "get_IsUpgraded")]
    [HarmonyPostfix]
    public static void PatchIsUpgraded(CardModel __instance, ref bool __result)
    {
        if (!__result && CardUpgradeTracker.GetUpgradeCount(__instance) > 0)
            __result = true;
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

    public static void WriteUpgradeLevelField(CardModel card, int value)
    {
        typeof(CardModel)
            .GetField("_currentUpgradeLevel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(card, value);
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
