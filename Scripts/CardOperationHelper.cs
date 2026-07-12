using System.Linq;
using System.Reflection;
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
/// 持久化策略（v14）：
/// - 升级前保存原始 CurrentUpgradeLevel，升级后恢复。
///   游戏存档只记录卡牌"原本"的升级次数（营火/事件等），
///   我们的无限升级次数由 CardUpgradeTracker 单独管理。
/// - 读档时游戏恢复原始升级，CardUpgradeTracker.ReapplyAllUpgrades 恢复额外升级。
/// - 不再需要任何 Harmony patch 修改 CardModel 行为。
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

    /// <summary>
    /// 执行无限升级。
    ///
    /// 策略：
    /// - 卡牌之前未被升级过（originalLevel == 0）：让 CurrentUpgradeLevel 自然变为 1，
    ///   游戏存档记录。不追踪到 CardUpgradeTracker（避免读档时重复应用）。
    ///   卡牌外观变为"已升级"（IsUpgraded = true）。
    /// - 卡牌之前已被升级过（originalLevel > 0）：升级后恢复到 originalLevel，
    ///   追踪到 CardUpgradeTracker。读档时游戏恢复 originalLevel 次升级，
    ///   CardUpgradeTracker 恢复额外次数。
    /// </summary>
    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;
        int originalLevel = card.CurrentUpgradeLevel;
        bool wasAlreadyUpgraded = originalLevel > 0;

        // 如果已达最大等级，临时重置以通过 UpgradeInternal 的 setter 校验
        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        // 执行升级（OnUpgrade 效果生效）
        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";

        if (wasAlreadyUpgraded)
        {
            // 恢复原始等级——额外的升级由 CardUpgradeTracker 管理
            WriteUpgradeLevelField(card, originalLevel);
            CardUpgradeTracker.RecordUpgrade(card, seed);
        }
        // else: 卡牌之前未被升级 → CurrentUpgradeLevel 自然为 1
        // 游戏存档会记录 level=1，读档时自动恢复，无需 CardUpgradeTracker

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    /// <summary>
    /// 仅执行升级逻辑（供读档时 CardUpgradeTracker.ReapplyAllUpgrades 使用）。
    /// 与 PerformInfiniteUpgrade 逻辑一致：已在追踪中的升级都是"额外"升级，
    /// 需要恢复原始等级。
    /// </summary>
    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();

        int originalLevel = card.CurrentUpgradeLevel;
        bool wasAlreadyUpgraded = originalLevel > 0;

        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        if (wasAlreadyUpgraded)
            WriteUpgradeLevelField(card, originalLevel);
    }

    /// <summary>通过反射直接写入 _currentUpgradeLevel。</summary>
    private static void WriteUpgradeLevelField(CardModel card, int value)
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
