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
/// </summary>
public static class CardOperationHelper
{
    public const int UpgradeCost = 5;

    /// <summary>
    /// 从当前 RunState 获取本地玩家。
    /// </summary>
    public static Player? GetLocalPlayer()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null) return null;
        return LocalContext.GetMe(state) ?? state.Players.FirstOrDefault();
    }

    /// <summary>
    /// 弹出牌组选择界面，让玩家选择一张卡牌。
    /// 调用者必须在调用前隐藏自定义 UI 遮罩层（陷阱1）。
    /// </summary>
    public static async Task<CardModel?> SelectCardFromDeck(Player player, string builtInPromptKey = "")
    {
        var prefs = string.IsNullOrEmpty(builtInPromptKey)
            ? new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            : new CardSelectorPrefs(new LocString("cards", builtInPromptKey), 1);

        var selected = (await CardSelectCmd.FromDeckGeneric(player, prefs)).ToList();
        return selected.Count > 0 ? selected[0] : null;
    }

    /// <summary>
    /// 执行无限升级：升级 + 重置升级等级 + 刷新视觉 + VFX。
    /// 卡牌必须已转为 mutable。
    /// </summary>
    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        ResetUpgradeLevel(card);

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
        {
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);
        }

        ShowUpgradeVfx(card);
    }

    /// <summary>
    /// 通过反射重置升级等级，使卡牌可被无限次升级。
    /// 注意：依赖内部字段名 _currentUpgradeLevel，游戏更新时可能变化。
    /// 技术债务：后续可考虑使用 Publicizer 暴露该字段。
    /// </summary>
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

    /// <summary>
    /// 显示卡牌升级 VFX。
    /// </summary>
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
