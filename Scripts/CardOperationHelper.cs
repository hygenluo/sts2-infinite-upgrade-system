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
/// 升级持久化策略：
/// 不再重置 CurrentUpgradeLevel。通过 Harmony patch 临时使 MaxUpgradeLevel 返回
/// int.MaxValue 绕过属性 setter 的校验。升级后 CurrentUpgradeLevel 自然累加，
/// 游戏存档系统自动记录并在读档时重新执行对应次数的 UpgradeInternal()。
///
/// CardUpgradeTracker 仅用于游戏存档无法覆盖的修改（如攻击+1、格挡+1、Keyword 等），
/// 升级操作不使用 CardUpgradeTracker。
/// </summary>
public static class CardOperationHelper
{
    public const int UpgradeCost = 5;

    /// <summary>
    /// 线程本地标志：在 PerformInfiniteUpgrade 执行 UpgradeInternal 时为 true，
    /// 使 Harmony patch get_MaxUpgradeLevel 返回 int.MaxValue。
    /// </summary>
    [ThreadStatic]
    private static bool s_isOurUpgrade;

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
    /// 升级后不重置 CurrentUpgradeLevel——让游戏存档系统自然记录升级次数。
    /// 卡牌保持"已升级"外观（IsUpgraded = true）。
    /// </summary>
    public static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;

        // 设置标志位：让 MaxUpgradeLevel 临时返回 int.MaxValue
        s_isOurUpgrade = true;
        try
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        finally
        {
            s_isOurUpgrade = false;
        }

        // 不再重置 CurrentUpgradeLevel → 卡牌保持"已升级"外观
        // 游戏存档自然记录升级次数 → 读档时自动恢复

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);

        ShowUpgradeVfx(card);
    }

    /// <summary>
    /// 仅执行升级逻辑（供读档时 CardUpgradeTracker.ReapplyAllUpgrades 使用）。
    /// 同样使用 MaxUpgradeLevel 绕过，不重置 level。
    /// </summary>
    public static void UpgradeWithoutTracking(CardModel card)
    {
        card.AssertMutable();

        s_isOurUpgrade = true;
        try
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        finally
        {
            s_isOurUpgrade = false;
        }
        // 不重置 level，不调 RecordUpgrade
    }

    /// <summary>
    /// Harmony patch：当 s_isOurUpgrade = true 时，MaxUpgradeLevel 返回 int.MaxValue。
    /// 绕过 CurrentUpgradeLevel setter 的 value > MaxUpgradeLevel 校验。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), "get_MaxUpgradeLevel")]
    [HarmonyPrefix]
    public static bool PatchMaxUpgradeLevel(CardModel __instance, ref int __result)
    {
        if (s_isOurUpgrade)
        {
            __result = int.MaxValue;
            return false; // 跳过原 getter
        }
        return true;
    }

    /// <summary>通过反射将 _currentUpgradeLevel 设为 0（供 CardUpgradeTracker 后续使用）。</summary>
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
