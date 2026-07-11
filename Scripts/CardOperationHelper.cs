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
/// 负责选择卡牌、执行修改、刷新视觉效果。不含 UI 状态管理。
///
/// 持久化策略：
/// 每次升级后立即重置 CurrentUpgradeLevel 为 0（通过反射设置 _currentUpgradeLevel），
/// 同时将升级次数记录到 CardUpgradeTracker。读档时由 RunStateHook 调用
/// CardUpgradeTracker.ReapplyAllUpgrades 重新应用所有升级。
/// 这样避免了游戏存档系统中 CurrentUpgradeLevel > MaxUpgradeLevel 导致的异常。
/// </summary>
public static class CardOperationHelper
{
    public const int UpgradeCost = 5;

    /// <summary>从当前 RunState 获取本地玩家。</summary>
    public static Player? GetLocalPlayer()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null) return null;
        return LocalContext.GetMe(state) ?? state.Players.FirstOrDefault();
    }

    /// <summary>弹出牌组选择界面。调用前须隐藏自定义 UI 遮罩（陷阱1）。</summary>
    public static async Task<CardModel?> SelectCardFromDeck(Player player, string builtInPromptKey = "")
    {
        var prefs = string.IsNullOrEmpty(builtInPromptKey)
            ? new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            : new CardSelectorPrefs(new LocString("cards", builtInPromptKey), 1);

        var selected = (await CardSelectCmd.FromDeckGeneric(player, prefs)).ToList();
        return selected.Count > 0 ? selected[0] : null;
    }

    /// <summary>
    /// 执行无限升级（玩家触发）。
    /// 升级后重置 CurrentUpgradeLevel 为 0，由 CardUpgradeTracker 记录次数。
    /// </summary>
    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;

        // 如果已达 MaxUpgradeLevel，先反射重置（绕过属性 setter 的校验）
        if (card.CurrentUpgradeLevel >= card.MaxUpgradeLevel)
            ResetUpgradeLevel(card);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        // 重置为 0 — 游戏存档不记录额外升级次数，由 CardUpgradeTracker 管理
        ResetUpgradeLevel(card);

        // 追踪升级次数（按 Run seed 持久化）
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        CardUpgradeTracker.RecordUpgrade(card, seed);

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    /// <summary>
    /// 仅执行升级逻辑，不追踪（供读档时 CardUpgradeTracker.ReapplyAllUpgrades 使用）。
    /// </summary>
    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();

        if (card.CurrentUpgradeLevel >= card.MaxUpgradeLevel)
            ResetUpgradeLevel(card);

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();
        ResetUpgradeLevel(card);

        // 不调 RecordUpgrade — 这是从存档恢复已有的升级
    }

    /// <summary>通过反射将 _currentUpgradeLevel 设为 0。</summary>
    public static void ResetUpgradeLevel(CardModel card)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var field = typeof(CardModel).GetField("_currentUpgradeLevel", flags)
                 ?? typeof(CardModel).GetField("CurrentUpgradeLevel", flags)
                 ?? typeof(CardModel).GetField("upgradeLevel", flags);

        if (field != null)
        {
            field.SetValue(card, 0);
        }
        else
        {
            Log.Warn("InfiniteUpgrade: could not find upgrade level field via reflection.");
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
