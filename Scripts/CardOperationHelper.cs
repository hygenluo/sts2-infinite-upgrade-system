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
/// 关键 Harmony patch：全局移除 CurrentUpgradeLevel setter 中的 MaxUpgradeLevel 校验。
/// 不这样做会导致读档时 FromSerializable 的 UpgradeInternal 循环在 level > MaxUpgradeLevel 时抛异常。
///
/// 基础游戏行为不受影响：营火/事件升级 UI 使用 IsUpgradable 过滤
/// （IsUpgradable = CurrentUpgradeLevel < MaxUpgradeLevel），
/// getter 未被 patch，过滤逻辑不变。
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
    /// 不重置 CurrentUpgradeLevel——让游戏存档系统自然记录升级次数。
    /// </summary>
    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    /// <summary>
    /// 仅执行升级逻辑（供读档时 CardUpgradeTracker.ReapplyAllUpgrades 使用）。
    /// </summary>
    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();
        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();
    }

    /// <summary>
    /// Harmony patch：全局移除 CurrentUpgradeLevel setter 的 MaxUpgradeLevel 校验。
    /// 直接将值写入 _currentUpgradeLevel 字段，跳过 value > MaxUpgradeLevel 检查。
    ///
    /// 为什么必须这样做：
    /// CardModel.FromSerializable 中 for 循环调用 UpgradeInternal()，
    /// 每次 CurrentUpgradeLevel++。如果卡牌已被多次升级（level > MaxUpgradeLevel=1），
    /// 后续的 ++ 操作会通过 setter 抛出 InvalidOperationException。
    ///
    /// 对基础游戏的影响：
    /// - IsUpgradable 仍使用原始 MaxUpgradeLevel getter（未被 patch）
    /// - 营火/事件升级 UI 过滤逻辑不受影响
    /// </summary>
    [HarmonyPatch(typeof(CardModel), "set_CurrentUpgradeLevel")]
    [HarmonyPrefix]
    public static bool PatchCurrentUpgradeLevelSetter(CardModel __instance, int value)
    {
        // 通过反射直接写入 _currentUpgradeLevel，绕过 setter 的校验
        typeof(CardModel)
            .GetField("_currentUpgradeLevel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(__instance, value);
        return false; // 跳过原始 setter
    }

    /// <summary>通过反射将 _currentUpgradeLevel 设为 0。</summary>
    public static void ResetUpgradeLevel(CardModel card)
    {
        typeof(CardModel)
            .GetField("_currentUpgradeLevel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(card, 0);
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
