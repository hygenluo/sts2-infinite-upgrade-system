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
    /// 保存并恢复原始 CurrentUpgradeLevel——让游戏存档只记录卡牌原本的升级状态，
    /// 我们的额外升级由 CardUpgradeTracker 管理并在读档时重新应用。
    /// </summary>
    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;

        // 1. 保存原始升级等级
        int originalLevel = card.CurrentUpgradeLevel;

        // 2. 如果已达最大等级，临时重置以通过 UpgradeInternal 的 setter 校验
        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        // 3. 执行升级（OnUpgrade 效果生效，CurrentUpgradeLevel 临时变化）
        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        // 4. 恢复原始升级等级——游戏存档不记录我们的额外升级
        WriteUpgradeLevelField(card, originalLevel);

        // 5. 追踪额外升级次数（持久化到 card_upgrades_{seed}.json）
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.RecordUpgrade(card, seed);

        // 6. 刷新视觉
        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    /// <summary>
    /// 仅执行升级逻辑（供读档时 CardUpgradeTracker.ReapplyAllUpgrades 使用）。
    /// 不追踪、不恢复——调用方负责管理升级计数。
    /// </summary>
    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();

        int originalLevel = card.CurrentUpgradeLevel;
        if (originalLevel >= card.MaxUpgradeLevel)
            WriteUpgradeLevelField(card, 0);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

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
