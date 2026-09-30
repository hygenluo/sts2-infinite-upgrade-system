using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using InfiniteUpgradeSystem.Multiplayer;
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
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
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

    /// <summary>当前选牌流程的取消信号（Esc 触发；非本模组选牌时为 null）。</summary>
    private static TaskCompletionSource<bool>? s_activeSelectionCancel;

    public static async Task<CardModel?> SelectCardFromDeck(Player player, string builtInPromptKey = "")
    {
        // 专属提示（Phase 4）：key 在显示时由 LocStringPromptPatch（Harmony Postfix
        // on GetFormattedText）解析为本模组本地化文本 —— 无需 pck。
        var prefs = string.IsNullOrEmpty(builtInPromptKey)
            ? new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            : new CardSelectorPrefs(new LocString("cards", builtInPromptKey), 1);

        // 启动卡牌选择后延迟一帧，确保 NOverlayStack.Push 已完成并将 UI 节点加入场景树
        var selectTask = CardSelectCmd.FromDeckGeneric(player, prefs);
        await Task.Delay(33); // 2 帧 — Push() + 子节点 _Ready()
        EnsureOverlayOnTop();
        // 游戏自带返回图标在 0 张已选牌时抛异常（空集合 First()，游戏本体 bug），
        // 会把选牌流程挂起；禁用该按钮，取消统一走我们的 Esc 链路。
        TryDisableCloseButton();

        // 取消链路（DEBUG.md Phase 2）：
        // 游戏选牌屏的 CancelSelection 在 0 张已选牌时会抛 "Sequence contains no elements"
        // （空集合 First()，游戏本体同样存在），异常中断任务完成 → 屏永远关不掉。
        // 因此不调用它：Esc 由我们接管 —— 关闭选牌屏 + 触发取消信号，
        // 本任务以 null 完成 → 调用方退款并恢复面板。
        var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_activeSelectionCancel = cancelSignal;
        var completed = await Task.WhenAny(selectTask, cancelSignal.Task);
        s_activeSelectionCancel = null;

        if (completed == cancelSignal.Task)
        {
            TryCloseSelectionScreen();
            RestoreOverlayPosition();
            return null;
        }

        var selected = (await selectTask).ToList();
        RestoreOverlayPosition(); // 恢复原位，避免打乱 GlobalUi 场景树结构
        return selected.Count > 0 ? selected[0] : null;
    }

    /// <summary>
    /// 程序化取消当前选牌（Esc 快捷键用）：
    /// 1. 关闭选牌屏（强制 Remove，绕开会抛异常的游戏 CancelSelection）
    /// 2. 触发取消信号 → SelectCardFromDeck 以 null 完成 → 退款 + 恢复面板
    /// 非本模组发起的选牌（s_activeSelectionCancel 为 null）时不干预，Esc 放行给游戏。
    /// </summary>
    public static bool TryCancelActiveCardSelection()
    {
        var cancel = s_activeSelectionCancel;
        if (cancel == null) return false;
        TryCloseSelectionScreen();
        cancel.TrySetResult(true);
        return true;
    }

    /// <summary>禁用游戏选牌屏的返回按钮（其取消在 0 选牌时抛异常，会导致流程挂起）。</summary>
    private static void TryDisableCloseButton()
    {
        try
        {
            var overlays = NRun.Instance?.GlobalUi?.Overlays;
            if (overlays == null) return;
            for (int i = overlays.GetChildCount() - 1; i >= 0; i--)
            {
                if (overlays.GetChild(i) is NDeckCardSelectScreen screen)
                {
                    screen._closeButton.Visible = false;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: disable close button failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>从 Overlay 栈关闭选牌屏（先移除后释放，防残留）。</summary>
    private static void TryCloseSelectionScreen()
    {
        try
        {
            var overlays = NRun.Instance?.GlobalUi?.Overlays;
            if (overlays == null) return;
            for (int i = overlays.GetChildCount() - 1; i >= 0; i--)
            {
                if (overlays.GetChild(i) is NDeckCardSelectScreen screen)
                {
                    try { overlays.Remove(screen); } catch (Exception ex) { Log.Warn($"InfiniteUpgrade: overlay remove failed: {ex.Message}"); }
                    try { screen.QueueFree(); } catch { /* 已释放则忽略 */ }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: close selection screen failed (non-fatal): {ex.Message}");
        }
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

    // ═══════════════════════════════════════════════════════════════
    // v1.4.3：CardMod action 执行（两端一致，原子扣点+改卡+记录）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>CardMod action 执行：按身份找卡 → 校验 → 扣点 → 改卡 → 记录。两端一致。</summary>
    public static async Task<bool> TryApplyCardMod(Player player, string cardIdentity, int cost,
        CardUpgradeTracker.ModType modType, string arg, bool add)
    {
        if (player == null) return false;
        var card = FindCardByIdentity(player, cardIdentity);
        if (card == null)
        {
            // 两端牌组不一致 / 身份偏移时只有一端找得到卡 → 会 checksum 分歧，日志留证
            Log.Warn($"[IU] CardMod: card not found (player={player.NetId}, id={cardIdentity}, " +
                     $"deck={player.Deck?.Cards?.Count ?? -1})");
            return false;
        }
        if (!CanApplyCardMod(card, modType, arg, add))
        {
            Log.Warn($"[IU] CardMod: cannot apply {modType} to {card.Id.Entry} (arg={arg}, add={add})");
            return false;
        }
        if (!UpgradePointManager.TrySpendPoints(player, cost))
        {
            // 点数两端一致时不会走到这里；走到了即说明 store 已分歧（购买校验结果不同步）
            Log.Warn($"[IU] CardMod: insufficient points (player={player.NetId}, cost={cost}, " +
                     $"pts={UpgradeDataStore.GetPoints(player)}) — peers may have diverged");
            return false;
        }
        card = EnsureMutableInDeck(player, card);
        await ApplyCardMod(player, card, modType, arg, add);
        return true;
    }

    /// <summary>
    /// 向牌组添加一张牌（AddCard action 执行，两端一致）：
    /// 调用端把选中的 **ModelId 字符串**（"cards.STRIKE"）作为参数传过来，
    /// 两端各自用 `RunState.CreateCard` 从同一 canonical 模型创建新实例并加入牌组。
    /// 这样"随机选牌池"只在调用端发生一次，结果作为参数传输 —— 不再各端各摇一次（旧实现用
    /// `new System.Random()`，两端会加到不同的牌，直接导致牌组分歧）。
    /// </summary>
    public static async Task<bool> TryApplyAddCard(Player player, string modelIdString)
    {
        if (player == null || string.IsNullOrEmpty(modelIdString)) return false;
        try
        {
            var id = ModelId.Deserialize(modelIdString);
            var canonical = ModelDb.GetById<CardModel>(id);
            if (canonical == null)
            {
                Log.Warn($"[IU] AddCard: unknown model id {modelIdString}");
                return false;
            }
            var card = player.RunState.CreateCard(canonical, player);
            await CardPileCmd.Add(card, PileType.Deck);
            Log.Info($"[IU] AddCard: {id.Entry} → player {player.NetId} deck ({player.Deck.Cards.Count} cards)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[IU] AddCard failed ({modelIdString}): {ex.Message}");
            return false;
        }
    }

    /// <summary>按卡牌身份（TemplateId__实例序号）在玩家牌组找卡。</summary>
    public static CardModel? FindCardByIdentity(Player player, string identity)
    {
        if (player == null) return null;
        var counter = new Dictionary<string, int>();
        foreach (var card in player.Deck.Cards)
        {
            string t = card.Id.Entry;
            counter.TryGetValue(t, out int n);
            if ($"{t}__{n}" == identity) return card;
            counter[t] = n + 1;
        }
        return null;
    }

    /// <summary>卡牌修改前置校验（选卡后 UI 预校验 + action 两端校验，结果一致）。</summary>
    public static bool CanApplyCardMod(CardModel card, CardUpgradeTracker.ModType modType, string arg, bool add)
    {
        if (card == null) return false;
        switch (modType)
        {
            case CardUpgradeTracker.ModType.DamagePlus: return card.DynamicVars.Damage != null;
            case CardUpgradeTracker.ModType.BlockPlus: return card.DynamicVars.Block != null;
            case CardUpgradeTracker.ModType.DrawPlus: return card.DynamicVars.Cards != null;
            case CardUpgradeTracker.ModType.ReplayPlus: return true;
            case CardUpgradeTracker.ModType.EnergyReduce: return card.EnergyCost.Canonical > 0;
            case CardUpgradeTracker.ModType.KeywordAdd:
                return Enum.TryParse<CardKeyword>(arg, out var ka) && !card.Keywords.Contains(ka);
            case CardUpgradeTracker.ModType.KeywordRemove:
                return Enum.TryParse<CardKeyword>(arg, out var kr) && card.Keywords.Contains(kr);
            case CardUpgradeTracker.ModType.Enchant: return CanEnchant(card, arg);
            case CardUpgradeTracker.ModType.DeckRemove: return true;
            case CardUpgradeTracker.ModType.Upgrade: return true;
            default: return false;
        }
    }

    /// <summary>应用卡牌修改（action 两端执行）。</summary>
    private static async Task ApplyCardMod(Player player, CardModel card, CardUpgradeTracker.ModType modType, string arg, bool add)
    {
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        switch (modType)
        {
            case CardUpgradeTracker.ModType.Upgrade:
                UpgradeWithoutTracking(card);
                CardUpgradeTracker.RecordModification(player, card, seed, modType);
                break;
            case CardUpgradeTracker.ModType.DamagePlus:
                card.DynamicVars.Damage.BaseValue += 1m;
                CardUpgradeTracker.RecordModification(player, card, seed, modType);
                break;
            case CardUpgradeTracker.ModType.BlockPlus:
                card.DynamicVars.Block.BaseValue += 1m;
                CardUpgradeTracker.RecordModification(player, card, seed, modType);
                break;
            case CardUpgradeTracker.ModType.DrawPlus:
                card.DynamicVars.Cards.BaseValue += 1m;
                CardUpgradeTracker.RecordModification(player, card, seed, modType);
                break;
            case CardUpgradeTracker.ModType.ReplayPlus:
                card.BaseReplayCount += 1;
                CardUpgradeTracker.RecordModification(player, card, seed, modType);
                break;
            case CardUpgradeTracker.ModType.KeywordAdd:
                if (Enum.TryParse<CardKeyword>(arg, out var ka))
                { card.AddKeyword(ka); CardUpgradeTracker.RecordModification(player, card, seed, modType, arg); }
                break;
            case CardUpgradeTracker.ModType.KeywordRemove:
                if (Enum.TryParse<CardKeyword>(arg, out var kr))
                { card.RemoveKeyword(kr); CardUpgradeTracker.RecordModification(player, card, seed, modType, arg); }
                break;
            case CardUpgradeTracker.ModType.EnergyReduce:
                var cur = card.EnergyCost.Canonical;
                if (cur > 0) { card.EnergyCost.SetCustomBaseCost(cur - 1); CardUpgradeTracker.RecordModification(player, card, seed, modType); }
                break;
            case CardUpgradeTracker.ModType.Enchant:
                // 替换逻辑在 action 内重算（两端附魔列表一致 → replacedType 一致）
                var currentTypes = GetCardEnchantments(card);
                string? replacedType = null;
                if (!currentTypes.Any(e => e.Type == arg) && currentTypes.Count >= GetEnchantLimit(card))
                    replacedType = currentTypes[0].Type;
                if (!ApplyEnchantmentToCard(card, arg)) break;
                if (replacedType != null && card.Enchantment is CompositeEnchantment composite)
                    composite.RemoveSub(replacedType);
                if (replacedType != null)
                    CardUpgradeTracker.RemoveEnchantmentEntries(player, card, seed, replacedType);
                CardUpgradeTracker.RecordModification(player, card, seed, CardUpgradeTracker.ModType.Enchant, arg);
                break;
            case CardUpgradeTracker.ModType.DeckRemove:
                int idx = FindCardIndexInDeck(player, card);
                await MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromDeck(card);
                CardUpgradeTracker.OnCardRemoved(player, idx, seed);
                break;
        }
    }

    private static int FindCardIndexInDeck(Player player, CardModel card)
    {
        for (int i = 0; i < player.Deck.Cards.Count; i++)
            if (ReferenceEquals(player.Deck.Cards[i], card)) return i;
        return -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.1: 攻击+1 / 格挡+1
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ModifyDamage(int cost, string promptKey = "")
        => await PickAndModifyCard(cost, "攻击", CardUpgradeTracker.ModType.DamagePlus, promptKey: promptKey);

    public static async Task<bool> ModifyBlock(int cost, string promptKey = "")
        => await PickAndModifyCard(cost, "格挡", CardUpgradeTracker.ModType.BlockPlus, promptKey: promptKey);

    /// <summary>v1.4.3：选卡 → 预校验 → 入队 CardMod action（原子扣点+改卡+记录，跨端一致）。</summary>
    private static async Task<bool> PickAndModifyCard(int cost, string propName,
        CardUpgradeTracker.ModType modType, string arg = "", bool add = false, string promptKey = "")
    {
        var player = GetLocalPlayer();
        if (player == null) return false;
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player, promptKey);
            if (card == null) { UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }
            card = EnsureMutableInDeck(player, card); // 写回 deck，确保 identity 可被 action 端匹配
            if (!CanApplyCardMod(card, modType, arg, add))
            {
                GD.Print($"此卡牌无法进行{propName}操作。");
                UpgradeUIHandler.Instance?.SetUIVisible(true);
                return false;
            }
            string identity = CardUpgradeTracker.GetCardIdentity(card, player.Deck.Cards);
            var ok = await UpgradePurchaseFlow.EnqueueCardMod(cost, identity, (int)modType, arg, add);
            if (ok) { UpgradeUIHandler.Instance?.RefreshPointsLabel(); UpgradeUIHandler.Instance?.HideUI(); }
            else UpgradeUIHandler.Instance?.SetUIVisible(true);
            return ok;
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

    public static async Task<bool> ToggleKeyword(int cost, CardKeyword keyword, bool add, string promptKey = "")
    {
        var modType = add ? CardUpgradeTracker.ModType.KeywordAdd : CardUpgradeTracker.ModType.KeywordRemove;
        return await PickAndModifyCard(cost, add ? "添加词条" : "移除词条", modType, keyword.ToString(), add, promptKey);
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.3: 能量消耗-1
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ReduceEnergyCost(int cost, string promptKey = "")
        => await PickAndModifyCard(cost, "耗能", CardUpgradeTracker.ModType.EnergyReduce, promptKey: promptKey);

    // ═══════════════════════════════════════════════════════════════
    // Phase 3.4: 抽牌+1 / 重放+1 / 次数+1
    // ═══════════════════════════════════════════════════════════════

    public static async Task<bool> ModifyDrawCount(int cost, string promptKey = "")
        => await PickAndModifyCard(cost, "抽牌", CardUpgradeTracker.ModType.DrawPlus, promptKey: promptKey);

    public static async Task<bool> ModifyReplayCount(int cost, string promptKey = "")
        => await PickAndModifyCard(cost, "重放", CardUpgradeTracker.ModType.ReplayPlus, promptKey: promptKey);

    // ═══════════════════════════════════════════════════════════════
    // Phase 6: 牌组操作
    // ═══════════════════════════════════════════════════════════════

    /// <summary>从牌组选择一张牌删除（v1.4.3：走 CardMod action，跨端一致）。</summary>
    public static async Task<bool> RemoveCardFromDeck(int cost, string promptKey = "")
        => await PickAndModifyCard(cost, "删牌", CardUpgradeTracker.ModType.DeckRemove, promptKey: promptKey);

    /// <summary>
    /// 从指定卡牌池随机添加一张牌到牌组。
    ///
    /// **多人安全**：本地随机选择（`System.Random`）只在调用端发生一次，选中的 ModelId
    /// 经 `EnqueueAddCard` 同步 action 传给对端 → 两端添加同一张牌。
    /// 旧实现直接在本地创建并加牌（各端各摇一次）→ 两端牌组不一致（checksum 分歧），已废弃。
    /// </summary>
    public static async Task<bool> AddCardFromPool(string poolKey)
    {
        var player = GetLocalPlayer();
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
            if (poolType == null) { Log.Error($"[IU] AddCard: pool type not found: {poolTypeName}"); return false; }

            var modelDb = typeof(ModelDb);
            var poolGetter = modelDb.GetMethod("CardPool", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)!;
            var pool = poolGetter.MakeGenericMethod(poolType).Invoke(null, null) as CardPoolModel;
            if (pool == null) { Log.Error($"[IU] AddCard: pool null for {poolKey}"); return false; }

            var candidates = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint).ToList();
            if (candidates.Count == 0) { Log.Info($"[IU] AddCard: no candidate in pool {poolKey}"); return false; }

            // 本地选牌（结果作为 action 参数传输，因此本地随机不影响两端一致性）
            var pick = candidates[new Random().Next(candidates.Count)];
            return await UpgradePurchaseFlow.EnqueueAddCard(pick.Id.ToString());
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

    // ═══════════════════════════════════════════════════════════════
    // 卡牌附魔（update05：多种附魔共存，合成模型 CompositeEnchantment）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 给卡牌应用附魔：同种 +1 层，新种添加。读档 ReapplyAll 也走此方法（幂等重建）。
    /// 游戏存档把合成附魔记为无附魔，由 CardUpgradeTracker 持久化附魔列表。
    /// 子附魔创建失败时返回 false 且不挂空合成附魔（空合成附魔会导致伤害倍率异常）。
    /// </summary>
    public static bool ApplyEnchantmentToCard(CardModel card, string enchantType)
    {
        if (card == null) return false;
        if (!card.IsMutable)
        {
            Log.Warn($"InfiniteUpgrade: ApplyEnchantment skip on immutable card {card.Id.Entry}.");
            return false;
        }

        var sub = CreateEnchantmentSub(card, enchantType);
        if (sub == null)
        {
            Log.Warn($"InfiniteUpgrade: cannot create enchantment {enchantType} for {card.Id.Entry}.");
            return false;
        }

        var composite = card.Enchantment as CompositeEnchantment;
        if (composite == null)
        {
            composite = CreateCompositeInstance();
            card.EnchantInternal(composite, 1);
        }

        var existing = composite.Subs.FirstOrDefault(s => s.GetType() == sub.GetType());
        if (existing != null)
        {
            existing.Amount += sub.Amount; // 同种叠加：每次购买施加原版数值
            existing.RecalculateValues();
        }
        else
        {
            composite.Subs.Add(sub);
            sub.ModifyCard(); // 触发子附魔 OnEnchant（如 TezcatarasEmber 减费）
        }
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
        composite.RefreshPrimaryDisplay();

        if (card.Owner != null)
            RefreshAllVisuals(card.Owner);
        return true;
    }

    /// <summary>
    /// 获取合成附魔可变实例。CompositeEnchantment 是 AbstractModel 子类，启动时被 ModelDb
    /// 自动扫描注册（Id=ENCHANTMENT.COMPOSITE_ENCHANTMENT）——此时 `new` 必抛
    /// DuplicateModelException。因此取已注册 canonical + ToMutable（内部走 MutableClone，
    /// 不调用构造函数）。若某环境下未被注册（异常），回退到 `new` + MutableClone。
    /// </summary>
    private static CompositeEnchantment CreateCompositeInstance()
    {
        try
        {
            var canonical = ModelDb.Enchantment<CompositeEnchantment>();
            return (CompositeEnchantment)canonical.ToMutable();
        }
        catch
        {
            var bare = new CompositeEnchantment();
            return (CompositeEnchantment)bare.MutableClone();
        }
    }

    /// <summary>创建子附魔可变实例并挂到卡牌（设 Card + Amount=原版数值，不触发 OnEnchant）。</summary>
    private static EnchantmentModel? CreateEnchantmentSub(CardModel card, string enchantType)
    {
        var type = FindEnchantmentType(enchantType);
        if (type == null) return null;
        var canonical = GetCanonicalEnchantment(type);
        if (canonical == null) return null;
        var sub = (EnchantmentModel)canonical.ToMutable();
        sub.ApplyInternal(card, GetOriginalAmount(enchantType));
        return sub;
    }

    /// <summary>
    /// 附魔原版数值（与游戏施加源一致，反编译确认）：
    /// 动量 5（PunchDagger 遗物）、锋利 2 / 敏捷 2（SelfHelpBook 事件）、
    /// 迅捷 3（BeautifulBracelet 遗物）、阿德罗伊特 3（Kifuda 遗物）、
    /// 强壮 8（StoneOfAllTime 事件）、克隆 4（PaelsGrowth 遗物），其余默认 1。
    /// 数值不随层数变化的附魔（青睐/沉眠精华等）用 1 即可。
    /// </summary>
    public static int GetOriginalAmount(string enchantType)
        => s_originalAmounts.TryGetValue(enchantType, out var v) ? v : 1;

    private static readonly Dictionary<string, int> s_originalAmounts = new()
    {
        ["Momentum"] = 5,
        ["Sharp"] = 2,
        ["Nimble"] = 2,
        ["Swift"] = 3,
        ["Adroit"] = 3,
        ["Vigorous"] = 8,
        ["Clone"] = 4,
    };

    private static Type? FindEnchantmentType(string name)
    {
        string full = $"MegaCrit.Sts2.Core.Models.Enchantments.{name}";
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(full);
            if (t != null) return t;
        }
        return null;
    }

    private static EnchantmentModel? GetCanonicalEnchantment(Type type)
    {
        try
        {
            var modelDb = typeof(MegaCrit.Sts2.Core.Models.ModelDb);
            var m = modelDb.GetMethod("Enchantment", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null) return null;
            return m.MakeGenericMethod(type).Invoke(null, null) as EnchantmentModel;
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: GetCanonicalEnchantment({type.Name}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>卡牌当前附魔列表（类型名, 层数）。</summary>
    public static List<(string Type, int Amount)> GetCardEnchantments(CardModel card)
    {
        if (card?.Enchantment is CompositeEnchantment composite)
            return composite.Subs.Select(s => (s.GetType().Name, s.Amount)).ToList();
        if (card?.Enchantment != null)
            return new() { (card.Enchantment.GetType().Name, card.Enchantment.Amount) };
        return new();
    }

    /// <summary>稀有度附魔种类上限：普通2 / 罕见3 / 稀有5 / 其它3。</summary>
    public static int GetEnchantLimit(CardModel card) => card.Rarity switch
    {
        CardRarity.Common => 2,
        CardRarity.Uncommon => 3,
        CardRarity.Rare => 5,
        _ => 3
    };

    /// <summary>
    /// 附魔可加性判断（参照游戏 CanEnchant 规则，但适配合成附魔）：
    /// 只做类型门禁 + 不可玩牌排除。同种叠层 / 新种上限 / 替换由流程处理——
    /// 已达上限时新种仍可选（选后替换最早的那个），因此此处不拦截上限。
    /// </summary>
    public static bool CanEnchant(CardModel card, string enchantType)
    {
        if (card == null) return false;
        var type = FindEnchantmentType(enchantType);
        if (type == null) return false;
        var canonical = GetCanonicalEnchantment(type);
        if (canonical == null) return false;
        if (!canonical.CanEnchantCardType(card.Type)) return false;
        if (card.Pile?.Type == PileType.Deck && card.Keywords.Contains(CardKeyword.Unplayable)) return false;
        return true;
    }

    /// <summary>获取附魔的 canonical 模板（供 UI 显示名称/描述）。</summary>
    public static EnchantmentModel? GetEnchantCanonical(string typeName)
    {
        var type = FindEnchantmentType(typeName);
        return type == null ? null : GetCanonicalEnchantment(type);
    }

    /// <summary>
    /// 卡牌附魔流程（v1.4.3）：选卡 → 选附魔 → 入队 CardMod action（原子扣点+附魔+替换+记录，跨端一致）。
    /// 替换逻辑（replacedType）在 action 内重算，两端一致。
    /// </summary>
    public static async Task<bool> AddEnchantment(int cost, string promptKey = "")
    {
        var player = GetLocalPlayer();
        if (player == null) return false;
        UpgradeUIHandler.Instance?.SetUIVisible(false);
        try
        {
            var card = await SelectCardFromDeck(player, promptKey);
            if (card == null) { UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }

            var cardMutable = EnsureMutableInDeck(player, card);
            var enchantType = await UiComponents.EnchantSelectPanel.Show(cardMutable);
            if (enchantType == null) { UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }

            if (!CanApplyCardMod(cardMutable, CardUpgradeTracker.ModType.Enchant, enchantType, false))
            { GD.Print("此卡牌无法施加该附魔。"); UpgradeUIHandler.Instance?.SetUIVisible(true); return false; }

            string identity = CardUpgradeTracker.GetCardIdentity(cardMutable, player.Deck.Cards);
            var ok = await UpgradePurchaseFlow.EnqueueCardMod(cost, identity, (int)CardUpgradeTracker.ModType.Enchant, enchantType, false);
            if (ok) { UpgradeUIHandler.Instance?.RefreshPointsLabel(); UpgradeUIHandler.Instance?.HideUI(); }
            else UpgradeUIHandler.Instance?.SetUIVisible(true);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error($"AddEnchantment: {ex.Message}");
            UpgradeUIHandler.Instance?.SetUIVisible(true);
            return false;
        }
    }
}
