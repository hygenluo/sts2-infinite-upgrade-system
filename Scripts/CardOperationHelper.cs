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
    /// <summary>升级卡牌成本（唯一定义在 CardModCosts，购买/回退共用）。</summary>
    public const int UpgradeCost = CardModCosts.Upgrade;

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

    /// <summary>
    /// 【多人关键】只在本机弹出牌组选牌屏，**不走游戏的「同步选择」通道**。
    ///
    /// 为什么不能用 <c>CardSelectCmd.FromDeckGeneric</c>：那条路径在正常（无 selector）情况下会
    /// 调 <c>PlayerChoiceSynchronizer.ReserveChoiceId(player)</c> 并在选完后
    /// <c>SyncLocalChoice</c> 广播结果 —— 这是给「所有端都会跑到的确定性流程」（战斗、事件、火堆）
    /// 设计的协议：**每个端都必须按相同顺序为同一个玩家预留相同的 choice id**，
    /// 而且 <c>PlayerChoiceSynchronizer.ChoiceIds</c> 会作为 <c>nextChoiceIds</c> 进入
    /// <c>NetFullCombatState</c> 的 checksum。
    ///
    /// 本模组的选牌由玩家在任意时刻点面板触发，只有点的那一端会执行 → 该玩家的 choice id
    /// 计数器单边 +1，于是：
    ///   1) 下一次 checksum 的 nextChoiceIds 两端不同 → 分歧 → host 踢人 → 黑屏；
    ///   2) 对端那条「过期」的选择结果会滞留在 _receivedChoices 里，被后续某个**真正的**游戏选择
    ///      消费掉（类型不符会抛 InvalidOperationException），或让对端永久等一个不会来的 id
    ///      （WaitForRemoteChoice 永不完成 → 流程卡死、无异常日志）。
    ///
    /// 因此这里直接复刻 FromDeckGeneric 的「本地分支」：Push 选牌屏 + await 结果，
    /// 不预留 choice id、不同步。选中的卡最终通过 CardMod action 的 cardIdentity 传给对端，
    /// 两端再各自按身份校验/应用，确定性完全由 action 保证。
    /// </summary>
    private static Task<IEnumerable<CardModel>> ShowLocalDeckSelect(Player player, CardSelectorPrefs prefs)
    {
        var cards = PileType.Deck.GetPile(player).Cards.ToList();
        if (cards.Count == 0)
            return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
        // 与游戏一致：不需要手动确认且可选数已达下限 → 直接全选，不弹屏
        if (!prefs.RequireManualConfirmation && cards.Count <= prefs.MinSelect)
            return Task.FromResult<IEnumerable<CardModel>>(cards);

        var screen = NDeckCardSelectScreen.Create(cards, prefs);
        var overlayStack = NOverlayStack.Instance;
        if (overlayStack == null)
        {
            Log.Warn("InfiniteUpgrade: NOverlayStack.Instance is null; local deck selection aborted.");
            return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
        }
        overlayStack.Push(screen);
        return screen.CardsSelected();
    }

    public static async Task<CardModel?> SelectCardFromDeck(Player player, string builtInPromptKey = "")
    {
        // 专属提示（Phase 4）：key 在显示时由 LocStringPromptPatch（Harmony Postfix
        // on GetFormattedText）解析为本模组本地化文本 —— 无需 pck。
        var prefs = string.IsNullOrEmpty(builtInPromptKey)
            ? new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1)
            : new CardSelectorPrefs(new LocString("cards", builtInPromptKey), 1);

        // 启动卡牌选择后延迟一帧，确保 NOverlayStack.Push 已完成并将 UI 节点加入场景树
        var selectTask = ShowLocalDeckSelect(player, prefs);
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
        await ApplyCardMod(player, card, modType, arg, add, cost);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // 回退（v2.3）：撤销该卡**最近一次**修改并返还点数
    // ═══════════════════════════════════════════════════════════════

    /// <summary>该修改类型能否安全逆操作。</summary>
    public static bool IsRevertible(CardUpgradeTracker.ModType modType) => modType switch
    {
        // 游戏 DowngradeInternal() 会把整张卡重置为原型（DynamicVars/Keywords/EnergyCost 全部
        // 从 canonical 重新克隆，反编译确认）——它无法只撤销「一次无限升级」，
        // 会连带清空同一张卡上其它所有修改，因此升级卡牌不参与回退。
        CardUpgradeTracker.ModType.Upgrade => false,
        // 牌已经从牌组移除，记录也已被 OnCardRemoved 丢弃。
        CardUpgradeTracker.ModType.DeckRemove => false,
        _ => true,
    };

    /// <summary>
    /// 回退该卡最近一次修改（由 Refund action 两端调用）：
    /// 逆操作改卡 → 删除记录 → 返还点数。任一步失败则整体不生效（不改点数）。
    /// </summary>
    public static bool TryRefundCardMod(Player player, string identity,
        CardUpgradeTracker.ModType modType, int refund)
    {
        if (player == null || string.IsNullOrEmpty(identity)) return false;
        if (!IsRevertible(modType)) return false;

        var entry = CardUpgradeTracker.NewestEntry(player, identity);
        if (entry == null)
        {
            Log.Warn($"[IU] Refund: no record for {identity} (player={player.NetId})");
            return false;
        }
        if (!Enum.TryParse<CardUpgradeTracker.ModType>(entry.Value.Type, out var newest) || newest != modType)
        {
            Log.Warn($"[IU] Refund: newest record type mismatch for {identity} — " +
                     $"expected {modType}, newest {entry.Value.Type}");
            return false;
        }

        var card = FindCardByIdentity(player, identity);
        if (card == null)
        {
            Log.Warn($"[IU] Refund: card not found for {identity} (player={player.NetId})");
            return false;
        }
        card = EnsureMutableInDeck(player, card);
        if (!RevertCardMod(card, modType, entry.Value.Keyword))
        {
            Log.Warn($"[IU] Refund: revert {modType} failed on {card.Id.Entry} (arg={entry.Value.Keyword})");
            return false;
        }

        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        if (CardUpgradeTracker.RemoveNewestEntry(player, identity, modType, seed) == null)
        {
            // 记录在改卡之后消失（理论上不会发生）→ 不回滚卡片，但绝不能给点数
            Log.Error($"[IU] Refund: record vanished for {identity}; card reverted but no points refunded.");
            RefreshAllVisuals(player);
            return false;
        }

        if (refund > 0) UpgradePointManager.AddPoints(player, refund);
        RefreshAllVisuals(player);
        Log.Info($"[IU] Refund: {modType} on {card.Id.Entry} ({identity}) → +{refund} pts " +
                 $"(player={player.NetId}, pts={UpgradeDataStore.GetPoints(player)})");
        return true;
    }

    /// <summary>逆操作改卡（与 ApplyCardMod 一一对应，数值/词条/附魔全为精确逆）。</summary>
    private static bool RevertCardMod(CardModel card, CardUpgradeTracker.ModType modType, string? arg)
    {
        if (card == null) return false;
        switch (modType)
        {
            case CardUpgradeTracker.ModType.DamagePlus:
                if (card.DynamicVars.Damage == null) return false;
                card.DynamicVars.Damage.BaseValue -= 1m;
                return true;
            case CardUpgradeTracker.ModType.BlockPlus:
                if (card.DynamicVars.Block == null) return false;
                card.DynamicVars.Block.BaseValue -= 1m;
                return true;
            case CardUpgradeTracker.ModType.DrawPlus:
                if (card.DynamicVars.Cards == null) return false;
                card.DynamicVars.Cards.BaseValue -= 1m;
                return true;
            case CardUpgradeTracker.ModType.ReplayPlus:
                if (card.BaseReplayCount <= 0) return false;
                card.BaseReplayCount -= 1;
                return true;
            case CardUpgradeTracker.ModType.EnergyReduce:
                card.EnergyCost.SetCustomBaseCost(card.EnergyCost.Canonical + 1);
                return true;
            case CardUpgradeTracker.ModType.KeywordAdd:
                if (!Enum.TryParse<CardKeyword>(arg, out var ka)) return false;
                card.RemoveKeyword(ka);
                return true;
            case CardUpgradeTracker.ModType.KeywordRemove:
                if (!Enum.TryParse<CardKeyword>(arg, out var kr)) return false;
                card.AddKeyword(kr);
                return true;
            case CardUpgradeTracker.ModType.Enchant:
                return TryRevertEnchant(card, arg);
            default:
                return false;
        }
    }

    /// <summary>
    /// 撤销卡上的附魔（路线 A：一卡一附魔）。只有卡上现有附魔与记录的类型一致才撤销，
    /// 否则说明这张牌后来被换过附魔，不能拿旧记录去回退。
    /// 注意：附魔的一次性 OnEnchant 副作用（如余烬把费用改写成 0、部分附魔加关键词）不回滚 ——
    /// 原版没有「卸下附魔」的 API，这里只能清槽位；数值类附魔走 hook 实时计算，清掉即失效。
    /// </summary>
    private static bool TryRevertEnchant(CardModel card, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        var current = card.Enchantment;
        if (current == null)
        {
            Log.Warn($"[IU] Refund enchant: {card.Id.Entry} has no enchantment to remove.");
            return false;
        }
        if (current.GetType().Name != typeName)
        {
            Log.Warn($"[IU] Refund enchant: {card.Id.Entry} carries {current.GetType().Name}, " +
                     $"not the recorded {typeName} (可能已被新附魔替换，无法回退)。");
            return false;
        }
        if (!card.IsMutable)
        {
            Log.Warn($"[IU] Refund enchant: {card.Id.Entry} is immutable.");
            return false;
        }
        // 同种可叠加的附魔可能是"叠了几层"：每次回退只扣回一份原版数值，扣到只剩一份才清槽位。
        int original = GetOriginalAmount(typeName);
        if (current.Amount > original)
        {
            current.Amount -= original;
            current.RecalculateValues();
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
        }
        else
        {
            card.ClearEnchantmentInternal();
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
        }
        if (card.Owner != null) RefreshAllVisuals(card.Owner);
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

    /// <summary>应用卡牌修改（action 两端执行）。cost = 本次实际支付点数（记入记录，回退返还用）。</summary>
    private static async Task ApplyCardMod(Player player, CardModel card, CardUpgradeTracker.ModType modType,
        string arg, bool add, int cost)
    {
        var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
        switch (modType)
        {
            case CardUpgradeTracker.ModType.Upgrade:
                UpgradeWithoutTracking(card);
                CardUpgradeTracker.RecordModification(player, card, seed, modType, cost: cost);
                break;
            case CardUpgradeTracker.ModType.DamagePlus:
                card.DynamicVars.Damage.BaseValue += 1m;
                CardUpgradeTracker.RecordModification(player, card, seed, modType, cost: cost);
                break;
            case CardUpgradeTracker.ModType.BlockPlus:
                card.DynamicVars.Block.BaseValue += 1m;
                CardUpgradeTracker.RecordModification(player, card, seed, modType, cost: cost);
                break;
            case CardUpgradeTracker.ModType.DrawPlus:
                card.DynamicVars.Cards.BaseValue += 1m;
                CardUpgradeTracker.RecordModification(player, card, seed, modType, cost: cost);
                break;
            case CardUpgradeTracker.ModType.ReplayPlus:
                card.BaseReplayCount += 1;
                CardUpgradeTracker.RecordModification(player, card, seed, modType, cost: cost);
                break;
            case CardUpgradeTracker.ModType.KeywordAdd:
                if (Enum.TryParse<CardKeyword>(arg, out var ka))
                { card.AddKeyword(ka); CardUpgradeTracker.RecordModification(player, card, seed, modType, arg, cost); }
                break;
            case CardUpgradeTracker.ModType.KeywordRemove:
                if (Enum.TryParse<CardKeyword>(arg, out var kr))
                { card.RemoveKeyword(kr); CardUpgradeTracker.RecordModification(player, card, seed, modType, arg, cost); }
                break;
            case CardUpgradeTracker.ModType.EnergyReduce:
                var cur = card.EnergyCost.Canonical;
                if (cur > 0) { card.EnergyCost.SetCustomBaseCost(cur - 1); CardUpgradeTracker.RecordModification(player, card, seed, modType, cost: cost); }
                break;
            case CardUpgradeTracker.ModType.Enchant:
                // 路线 A（原版语义，一卡一附魔）：能否施加只取决于「卡上现有附魔 + arg」，
                // 两端看到的是同一张卡（action 在两端执行，卡状态已一致）→ 结论一致。
                // 不再有"已达上限就替换最早附魔"的逻辑。
                if (ApplyEnchantmentToCard(card, arg))
                    CardUpgradeTracker.RecordModification(player, card, seed, CardUpgradeTracker.ModType.Enchant, arg, cost);
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
    // 卡牌附魔（路线 A：原版语义，一卡一附魔）
    //
    // 参照海克斯符文（HextechRunes 0.10.1 / 拓展包）的做法：不自造包装附魔，
    // 附魔就是真实的 EnchantmentModel（真实 ModelId），施加走原版 CardCmd.Enchant：
    //   - 牌上没有附魔 → 挂上；
    //   - 已有同种且 IsStackable → Amount 叠加；
    //   - 其余情况 CanEnchant 返回 false（原版一卡一附魔）。
    // 持久化由引擎负责：SerializableCard.Enchantment 带 Id+Amount+Props，
    // 反序列化时 EnchantmentModel.FromSerializable → card.Enchantment.ModifyCard() → OnEnchant()
    // 会把一次性副作用（如余烬减费）与带 [SavedProperty] 的计数重放出来。
    // 因此附魔本身**不再需要本模组的记录重放**跨端/跨存档；记录只保留给回退算账。
    // 代价（相对旧的 CompositeEnchantment）：一卡只能一个附魔，没有"多附魔/替换"。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 给卡牌施加附魔（原版语义）。读档/对端同步的修复路径也会调它，因此必须幂等：
    /// 卡上已有附魔时只有「同种且可叠加」才继续叠加，其余情况返回 false 且不动这张卡。
    /// </summary>
    public static bool ApplyEnchantmentToCard(CardModel card, string enchantType)
    {
        if (card == null) return false;
        if (!card.IsMutable)
        {
            Log.Warn($"InfiniteUpgrade: ApplyEnchantment skip on immutable card {card.Id.Entry}.");
            return false;
        }
        var canonical = GetEnchantCanonical(enchantType);
        if (canonical == null)
        {
            Log.Warn($"InfiniteUpgrade: unknown enchantment type {enchantType}.");
            return false;
        }
        // 原版门禁：卡类型 / 牌组里的 Unplayable / 已有附魔的槽位规则都在这里
        if (!canonical.CanEnchant(card))
        {
            Log.Warn($"InfiniteUpgrade: {enchantType} cannot enchant {card.Id.Entry} " +
                     $"(current={(card.Enchantment?.GetType().Name ?? "none")}).");
            return false;
        }
        // 原版施加入口：内部做 EnchantInternal + ModifyCard（触发 OnEnchant）+ FinalizeUpgradeInternal，
        // 并写入本层 run history 的 CardsEnchanted。
        CardCmd.Enchant((EnchantmentModel)canonical.ToMutable(), card, GetOriginalAmount(enchantType));
        if (card.Owner != null)
            RefreshAllVisuals(card.Owner);
        return true;
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

    /// <summary>卡牌当前附魔（0 或 1 条：类型名, 层数）。</summary>
    public static List<(string Type, int Amount)> GetCardEnchantments(CardModel card)
    {
        if (card?.Enchantment == null) return new();
        return new() { (card.Enchantment.GetType().Name, card.Enchantment.Amount) };
    }

    /// <summary>
    /// 原版可附魔判断（卡类型门禁 + 牌组 Unplayable 排除 + 已有附魔的槽位规则）。
    /// 路线 A 之后不再有"种类上限/替换最早附魔"，一切按原版 CanEnchant 走。
    /// </summary>
    public static bool CanEnchant(CardModel card, string enchantType)
    {
        if (card == null) return false;
        var canonical = GetEnchantCanonical(enchantType);
        return canonical != null && canonical.CanEnchant(card);
    }

    /// <summary>获取附魔的 canonical 模板（供 UI 显示名称/描述）。</summary>
    public static EnchantmentModel? GetEnchantCanonical(string typeName)
    {
        var type = FindEnchantmentType(typeName);
        return type == null ? null : GetCanonicalEnchantment(type);
    }

    /// <summary>
    /// 卡牌附魔流程：选卡 → 选附魔 → 入队 CardMod action（原子扣点+附魔+记录，跨端一致）。
    /// 路线 A：施加减法由原版 CanEnchant/CardCmd.Enchant 决定（一卡一附魔），
    /// 两端在 action 内各算一次，结论一致。
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
