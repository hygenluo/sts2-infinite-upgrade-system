using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 模组自包含本地化（Phase 1，UI设计.md §五 Phase 1）。
///
/// 背景：游戏通过 pck 挂载 mod 的本地化表，而本模组不打包 pck、部署目录也没有
/// localization 文件夹 —— 本地化 JSON 历史上从未被游戏加载（选牌提示一直是游戏内置文案）。
/// 因此这里直接读取 DLL 同目录 localization/{lang}/cards.json（随构建部署），
/// 保证显示名与搜索双语始终可用；若未来游戏 LocString 能解析（如打包 pck），
/// 显示名优先走游戏系统以跟随游戏语言切换。
///
/// 用法：显示名 → ResolveDisplayName(key, 中文回退)；搜索 → GetZhs/GetEng。
/// </summary>
public static class UpgradeLoc
{
    // ═══════════════════════════════════════════════════════════════
    // 本地化 key 常量（与 localization/{zhs,eng}/cards.json 同步维护）
    // ═══════════════════════════════════════════════════════════════

    public const string ItemBaseStrength = "INFINITEUPGRADESYSTEM-ITEM_BASE_STRENGTH";
    public const string ItemBaseDexterity = "INFINITEUPGRADESYSTEM-ITEM_BASE_DEXTERITY";
    public const string ItemBaseFocus = "INFINITEUPGRADESYSTEM-ITEM_BASE_FOCUS";
    public const string ItemBasePlating = "INFINITEUPGRADESYSTEM-ITEM_BASE_PLATING";
    public const string ItemBaseThorns = "INFINITEUPGRADESYSTEM-ITEM_BASE_THORNS";
    public const string ItemBaseArtifact = "INFINITEUPGRADESYSTEM-ITEM_BASE_ARTIFACT";
    public const string ItemHp = "INFINITEUPGRADESYSTEM-ITEM_HP";
    public const string ItemOrbSlot = "INFINITEUPGRADESYSTEM-ITEM_ORB_SLOT";
    public const string ItemEnergyPerTurn = "INFINITEUPGRADESYSTEM-ITEM_ENERGY_PER_TURN";
    public const string ItemStarsPerTurn = "INFINITEUPGRADESYSTEM-ITEM_STARS_PER_TURN";
    public const string ItemForgePerTurn = "INFINITEUPGRADESYSTEM-ITEM_FORGE_PER_TURN";
    public const string ItemBlockKeep = "INFINITEUPGRADESYSTEM-ITEM_BLOCK_KEEP";
    public const string ItemEnergyKeep = "INFINITEUPGRADESYSTEM-ITEM_ENERGY_KEEP";
    public const string ItemGoldOnKill = "INFINITEUPGRADESYSTEM-ITEM_GOLD_ON_KILL";
    public const string ItemCardUpgrade = "INFINITEUPGRADESYSTEM-ITEM_CARD_UPGRADE";
    public const string ItemAttackPlus = "INFINITEUPGRADESYSTEM-ITEM_ATTACK_PLUS";
    public const string ItemBlockPlus = "INFINITEUPGRADESYSTEM-ITEM_BLOCK_PLUS";
    public const string ItemDrawPlus = "INFINITEUPGRADESYSTEM-ITEM_DRAW_PLUS";
    public const string ItemReplayPlus = "INFINITEUPGRADESYSTEM-ITEM_REPLAY_PLUS";
    public const string ItemCostMinus = "INFINITEUPGRADESYSTEM-ITEM_COST_MINUS";
    public const string ItemAddExhaust = "INFINITEUPGRADESYSTEM-ITEM_ADD_EXHAUST";
    public const string ItemRemoveExhaust = "INFINITEUPGRADESYSTEM-ITEM_REMOVE_EXHAUST";
    public const string ItemAddSly = "INFINITEUPGRADESYSTEM-ITEM_ADD_SLY";
    public const string ItemAddRetain = "INFINITEUPGRADESYSTEM-ITEM_ADD_RETAIN";
    public const string ItemRemoveRetain = "INFINITEUPGRADESYSTEM-ITEM_REMOVE_RETAIN";
    public const string ItemAddInnate = "INFINITEUPGRADESYSTEM-ITEM_ADD_INNATE";
    public const string ItemRemoveInnate = "INFINITEUPGRADESYSTEM-ITEM_REMOVE_INNATE";
    public const string ItemAddEthereal = "INFINITEUPGRADESYSTEM-ITEM_ADD_ETHEREAL";
    public const string ItemRemoveEthereal = "INFINITEUPGRADESYSTEM-ITEM_REMOVE_ETHEREAL";
    public const string ItemAddEternal = "INFINITEUPGRADESYSTEM-ITEM_ADD_ETERNAL";
    public const string ItemRemoveEternal = "INFINITEUPGRADESYSTEM-ITEM_REMOVE_ETERNAL";
    public const string ItemDeckRemove = "INFINITEUPGRADESYSTEM-ITEM_DECK_REMOVE";
    public const string ItemEnchant = "INFINITEUPGRADESYSTEM-ITEM_ENCHANT";
    public const string ItemTestP1 = "INFINITEUPGRADESYSTEM-ITEM_TEST_P1";
    public const string ItemTestP5 = "INFINITEUPGRADESYSTEM-ITEM_TEST_P5";
    public const string ItemTestP10 = "INFINITEUPGRADESYSTEM-ITEM_TEST_P10";

    public const string ItemSkillBlockOnPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_BLOCK_ON_PLAY";
    public const string ItemSkillAgilityEvery4Plays = "INFINITEUPGRADESYSTEM-ITEM_SKILL_AGILITY_EVERY_4_PLAYS";
    public const string ItemSkillStrengthEvery4Plays = "INFINITEUPGRADESYSTEM-ITEM_SKILL_STRENGTH_EVERY_4_PLAYS";
    public const string ItemSkillDrawWhenNoHand = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_WHEN_NO_HAND";
    public const string ItemSkillRestAllOptions = "INFINITEUPGRADESYSTEM-ITEM_SKILL_REST_ALL_OPTIONS";
    public const string ItemSkillBlockAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_BLOCK_AT_TURN_START";
    public const string ItemSkillStrAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_STR_AT_TURN_START";
    public const string ItemSkillDrawOnExhaust = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_ON_EXHAUST";
    public const string ItemSkillVulnerableOnExhaust = "INFINITEUPGRADESYSTEM-ITEM_SKILL_VULNERABLE_ON_EXHAUST";
    public const string ItemSkillStrFromVulnerableAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_STR_FROM_VULNERABLE_AT_TURN_START";
    public const string ItemSkillDrawOnHpLoss = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_ON_HP_LOSS";
    public const string ItemSkillDoubleBlockAtTurnEnd = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DOUBLE_BLOCK_AT_TURN_END";
    public const string ItemSkillHealAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_HEAL_AT_TURN_START";
    public const string ItemSkillWeakOnDiscard = "INFINITEUPGRADESYSTEM-ITEM_SKILL_WEAK_ON_DISCARD";
    public const string ItemSkillDexterityOnDiscard = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DEXTERITY_ON_DISCARD";
    public const string ItemSkillShivAllEnemies = "INFINITEUPGRADESYSTEM-ITEM_SKILL_SHIV_ALL_ENEMIES";
    public const string ItemSkillBlockOnPoison = "INFINITEUPGRADESYSTEM-ITEM_SKILL_BLOCK_ON_POISON";
    public const string ItemSkillPoisonAllOnCardPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_POISON_ALL_ON_CARD_PLAY";
    public const string ItemSkillDrawAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_AT_TURN_START";
    public const string ItemSkillForgeOnPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_FORGE_ON_PLAY";
    public const string ItemSkillVigorOnSkillPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_VIGOR_ON_SKILL_PLAY";
    public const string ItemSkillSovereignBladeBlockOnForge = "INFINITEUPGRADESYSTEM-ITEM_SKILL_SOVEREIGN_BLADE_BLOCK_ON_FORGE";
    public const string ItemSkillFreeFirstCard = "INFINITEUPGRADESYSTEM-ITEM_SKILL_FREE_FIRST_CARD";
    public const string ItemSkillEnemyLoseStrOnStar = "INFINITEUPGRADESYSTEM-ITEM_SKILL_ENEMY_LOSE_STR_ON_STAR";
    public const string ItemSkillAddColorlessCardAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_ADD_COLORLESS_CARD_AT_TURN_START";
    public const string ItemSkillSummonOnPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_SUMMON_ON_PLAY";
    public const string ItemSkillDmgOnEtherealPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DMG_ON_ETHEREAL_PLAY";
    public const string ItemSkillOstyExtraAttack = "INFINITEUPGRADESYSTEM-ITEM_SKILL_OSTY_EXTRA_ATTACK";
    public const string ItemSkillSummonAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_SUMMON_AT_TURN_START";
    public const string ItemSkillEnemyLoseStrOnDoom = "INFINITEUPGRADESYSTEM-ITEM_SKILL_ENEMY_LOSE_STR_ON_DOOM";
    public const string ItemSkillPlayEtherealFromExhaust = "INFINITEUPGRADESYSTEM-ITEM_SKILL_PLAY_ETHEREAL_FROM_EXHAUST";
    public const string ItemSkillDrawOnPowerPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_ON_POWER_PLAY";
    public const string ItemSkillOrbDmgAtTurnEnd = "INFINITEUPGRADESYSTEM-ITEM_SKILL_ORB_DMG_AT_TURN_END";
    public const string ItemSkillExhaustStatusGainBlockAtTurnEnd = "INFINITEUPGRADESYSTEM-ITEM_SKILL_EXHAUST_STATUS_GAIN_BLOCK_AT_TURN_END";
    public const string ItemSkillAutoPlayPowerOnDraw = "INFINITEUPGRADESYSTEM-ITEM_SKILL_AUTO_PLAY_POWER_ON_DRAW";
    public const string ItemSkillOrbAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_ORB_AT_TURN_START";
    public const string ItemSkillAutoPlayRandomPowerAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_AUTO_PLAY_RANDOM_POWER_AT_TURN_START";
    public const string ItemSkillDmgXTimesAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DMG_X_TIMES_AT_TURN_START";
    public const string ItemSkillFreeNextCardOnPower = "INFINITEUPGRADESYSTEM-ITEM_SKILL_FREE_NEXT_CARD_ON_POWER";

    // ═══════════════════════════════════════════════════════════════
    // UI 文字（UI v3：面板标题/分区名/占位符/提示，全部本地化，不再硬编码中文）
    // 格式串用 {0} {1} 占位，配合 Format(key, fallback, args)。
    // ═══════════════════════════════════════════════════════════════

    public const string UiTitle = "INFINITEUPGRADESYSTEM-UI_TITLE";
    public const string UiSearchPlaceholder = "INFINITEUPGRADESYSTEM-UI_SEARCH_PLACEHOLDER";
    public const string UiEmpty = "INFINITEUPGRADESYSTEM-UI_EMPTY";
    public const string UiCostTooltip = "INFINITEUPGRADESYSTEM-UI_COST_TOOLTIP";
    public const string UiBack = "INFINITEUPGRADESYSTEM-UI_BACK";
    public const string UiDeck = "INFINITEUPGRADESYSTEM-UI_DECK";
    public const string UiDeckCount = "INFINITEUPGRADESYSTEM-UI_DECK_COUNT";
    public const string UiHint = "INFINITEUPGRADESYSTEM-UI_HINT";
    public const string UiHintReadOnly = "INFINITEUPGRADESYSTEM-UI_HINT_READONLY";
    public const string UiReadOnlyBadge = "INFINITEUPGRADESYSTEM-UI_READONLY_BADGE";
    public const string UiPoints = "INFINITEUPGRADESYSTEM-UI_POINTS";
    public const string UiSectionBase = "INFINITEUPGRADESYSTEM-UI_SECTION_BASE";
    public const string UiSectionAbility = "INFINITEUPGRADESYSTEM-UI_SECTION_ABILITY";
    public const string UiSectionSkill = "INFINITEUPGRADESYSTEM-UI_SECTION_SKILL";
    public const string UiSectionDeck = "INFINITEUPGRADESYSTEM-UI_SECTION_DECK";
    public const string UiSectionTest = "INFINITEUPGRADESYSTEM-UI_SECTION_TEST";
    public const string UiSectionCount = "INFINITEUPGRADESYSTEM-UI_SECTION_COUNT";
    public const string UiSectionSpent = "INFINITEUPGRADESYSTEM-UI_SECTION_SPENT";
    public const string UiClassGeneric = "INFINITEUPGRADESYSTEM-UI_CLASS_GENERIC";
    public const string UiClassIronclad = "INFINITEUPGRADESYSTEM-UI_CLASS_IRONCLAD";
    public const string UiClassSilent = "INFINITEUPGRADESYSTEM-UI_CLASS_SILENT";
    public const string UiClassRegent = "INFINITEUPGRADESYSTEM-UI_CLASS_REGENT";
    public const string UiClassNecrobinder = "INFINITEUPGRADESYSTEM-UI_CLASS_NECROBINDER";
    public const string UiClassDefect = "INFINITEUPGRADESYSTEM-UI_CLASS_DEFECT";
    public const string UiEnchantTitle = "INFINITEUPGRADESYSTEM-UI_ENCHANT_TITLE";
    public const string UiEnchantCurrent = "INFINITEUPGRADESYSTEM-UI_ENCHANT_CURRENT";
    public const string UiEnchantFull = "INFINITEUPGRADESYSTEM-UI_ENCHANT_FULL";
    public const string UiEnchantNone = "INFINITEUPGRADESYSTEM-UI_ENCHANT_NONE";
    public const string UiEnchantOwned = "INFINITEUPGRADESYSTEM-UI_ENCHANT_OWNED";
    public const string UiCancel = "INFINITEUPGRADESYSTEM-UI_CANCEL";
    public const string UiOwned = "INFINITEUPGRADESYSTEM-UI_OWNED";
    public const string UiNotOwned = "INFINITEUPGRADESYSTEM-UI_NOT_OWNED";

    // ── 悬浮窗（v2.4）──
    public const string UiFloatingTooltip = "INFINITEUPGRADESYSTEM-UI_FLOATING_TOOLTIP";

    // ── 模组设置页（v2.4）──
    public const string SettingsSectionGeneral = "INFINITEUPGRADESYSTEM-SETTINGS_SECTION_GENERAL";
    public const string SettingsShowFloating = "INFINITEUPGRADESYSTEM-SETTINGS_SHOW_FLOATING";
    public const string SettingsShowFloatingDesc = "INFINITEUPGRADESYSTEM-SETTINGS_SHOW_FLOATING_DESC";

    // ── 回退系统（v2.3）──
    public const string UiSectionRefund = "INFINITEUPGRADESYSTEM-UI_SECTION_REFUND";
    public const string UiRefundSectionSummary = "INFINITEUPGRADESYSTEM-UI_REFUND_SUMMARY";
    public const string UiRefundEmpty = "INFINITEUPGRADESYSTEM-UI_REFUND_EMPTY";
    public const string UiRefundTooltip = "INFINITEUPGRADESYSTEM-UI_REFUND_TOOLTIP";
    public const string UiRefundTooltipZero = "INFINITEUPGRADESYSTEM-UI_REFUND_TOOLTIP_ZERO";
    public const string UiRefundConfirm = "INFINITEUPGRADESYSTEM-UI_REFUND_CONFIRM";
    public const string UiRefundPaid = "INFINITEUPGRADESYSTEM-UI_REFUND_PAID";
    public const string UiRefundPaidShort = "INFINITEUPGRADESYSTEM-UI_REFUND_PAID_SHORT";
    public const string UiRefundLocked = "INFINITEUPGRADESYSTEM-UI_REFUND_LOCKED";
    public const string UiRefundHint = "INFINITEUPGRADESYSTEM-UI_REFUND_HINT";
    public const string UiRefundOlderMods = "INFINITEUPGRADESYSTEM-UI_REFUND_OLDER_MODS";
    public const string UiRefundNoteUpgrade = "INFINITEUPGRADESYSTEM-UI_REFUND_NOTE_UPGRADE";
    public const string UiRefundNoteGone = "INFINITEUPGRADESYSTEM-UI_REFUND_NOTE_GONE";
    public const string UiRefundToast = "INFINITEUPGRADESYSTEM-UI_REFUND_TOAST";
    public const string UiRefundLevel = "INFINITEUPGRADESYSTEM-UI_REFUND_LEVEL";

    public const string PromptCardUpgrade = "INFINITEUPGRADESYSTEM-PROMPT_CARD_UPGRADE";
    public const string PromptAttackPlus = "INFINITEUPGRADESYSTEM-PROMPT_ATTACK_PLUS";
    public const string PromptBlockPlus = "INFINITEUPGRADESYSTEM-PROMPT_BLOCK_PLUS";
    public const string PromptDrawPlus = "INFINITEUPGRADESYSTEM-PROMPT_DRAW_PLUS";
    public const string PromptReplayPlus = "INFINITEUPGRADESYSTEM-PROMPT_REPLAY_PLUS";
    public const string PromptCostMinus = "INFINITEUPGRADESYSTEM-PROMPT_COST_MINUS";
    public const string PromptAddExhaust = "INFINITEUPGRADESYSTEM-PROMPT_ADD_EXHAUST";
    public const string PromptRemoveExhaust = "INFINITEUPGRADESYSTEM-PROMPT_REMOVE_EXHAUST";
    public const string PromptAddSly = "INFINITEUPGRADESYSTEM-PROMPT_ADD_SLY";
    public const string PromptAddRetain = "INFINITEUPGRADESYSTEM-PROMPT_ADD_RETAIN";
    public const string PromptRemoveRetain = "INFINITEUPGRADESYSTEM-PROMPT_REMOVE_RETAIN";
    public const string PromptAddInnate = "INFINITEUPGRADESYSTEM-PROMPT_ADD_INNATE";
    public const string PromptRemoveInnate = "INFINITEUPGRADESYSTEM-PROMPT_REMOVE_INNATE";
    public const string PromptAddEthereal = "INFINITEUPGRADESYSTEM-PROMPT_ADD_ETHEREAL";
    public const string PromptRemoveEthereal = "INFINITEUPGRADESYSTEM-PROMPT_REMOVE_ETHEREAL";
    public const string PromptAddEternal = "INFINITEUPGRADESYSTEM-PROMPT_ADD_ETERNAL";
    public const string PromptRemoveEternal = "INFINITEUPGRADESYSTEM-PROMPT_REMOVE_ETERNAL";
    public const string PromptDeckRemove = "INFINITEUPGRADESYSTEM-PROMPT_DECK_REMOVE";
    public const string PromptEnchant = "INFINITEUPGRADESYSTEM-PROMPT_ENCHANT";

    // ═══════════════════════════════════════════════════════════════
    // 加载与查询
    // ═══════════════════════════════════════════════════════════════

    private static Dictionary<string, string>? s_zhs;
    private static Dictionary<string, string>? s_eng;
    private static bool s_loaded;

    public static void LoadOnce()
    {
        if (s_loaded) return;
        s_loaded = true;
        try
        {
            var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dllDir == null) return;
            s_zhs = LoadFile(Path.Combine(dllDir, "localization", "zhs", "cards.json"));
            s_eng = LoadFile(Path.Combine(dllDir, "localization", "eng", "cards.json"));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[InfiniteUpgrade] 本地化加载异常: {ex.Message}");
        }
    }

    private static Dictionary<string, string>? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dict = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? "";
        return dict;
    }

    /// <summary>按系统语言取文本（zh → zhs，否则 eng），两级回退到 fallback。</summary>
    public static string Get(string key, string fallback)
    {
        LoadOnce();
        var preferZhs = OS.GetLocale().ToLower().StartsWith("zh");
        var primary = preferZhs ? s_zhs : s_eng;
        var secondary = preferZhs ? s_eng : s_zhs;
        if (primary?.TryGetValue(key, out var p) == true) return p;
        if (secondary?.TryGetValue(key, out var s) == true) return s;
        return fallback;
    }

    public static string? GetZhs(string key) { LoadOnce(); return s_zhs?.TryGetValue(key, out var z) == true ? z : null; }
    public static string? GetEng(string key) { LoadOnce(); return s_eng?.TryGetValue(key, out var e) == true ? e : null; }

    /// <summary>本地化文本 + 格式化（{0}/{1} 占位）。缺失 key 时用 fallback 文本格式化。</summary>
    public static string Format(string key, string fallback, params object[] args)
    {
        var template = Get(key, fallback);
        if (args.Length == 0) return template;
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // 本地化文本里的 { } 与占位符冲突（如描述含花括号）→ 退回英文模板
            try { return string.Format(fallback, args); }
            catch (FormatException) { return template; }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 内部逻辑 key → 本地化显示名映射
    // 分区/职业在代码里用中文常量作分组 key（UpgradeItemDef.Category / SubCategory），
    // 显示时统一经此映射，保证 UI 文本可跟随语言切换。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>分区标题显示名。</summary>
    public static string SectionTitle(string category) => category switch
    {
        "基础属性" => Get(UiSectionBase, "基础属性"),
        "能力" => Get(UiSectionAbility, "能力"),
        "技能" => Get(UiSectionSkill, "技能"),
        "牌组" => Get(UiSectionDeck, "牌组"),
        "测试操作" => Get(UiSectionTest, "测试"),
        "回退" => Get(UiSectionRefund, "回退"),
        _ => category,
    };

    /// <summary>职业标签页显示名。</summary>
    public static string ClassTitle(string className) => className switch
    {
        "通用" => Get(UiClassGeneric, "通用"),
        "铁甲战士" => Get(UiClassIronclad, "铁甲战士"),
        "静默猎手" => Get(UiClassSilent, "静默猎手"),
        "储君" => Get(UiClassRegent, "储君"),
        "亡灵契约师" => Get(UiClassNecrobinder, "亡灵契约师"),
        "故障机器人" => Get(UiClassDefect, "故障机器人"),
        _ => className,
    };

    /// <summary>牌组操作的分类显示名（子面板中与卡牌操作同列）。</summary>
    public static string DeckOperationTitle(string category) => category == "牌组操作"
        ? Get(UiSectionDeck, "牌组")
        : category;

    private static bool? s_gameLocAvailable;

    /// <summary>
    /// 一次性探测游戏 LocString 表是否挂载了本模组的 key（未打包 pck 时必失败）。
    /// 探测通过后才走游戏 LocString（跟随游戏语言）；否则只用自加载字典，
    /// 避免每次解析都触发 RitsuLib 的 missing-key 日志噪音。
    /// </summary>
    private static bool ProbeGameLoc(string key)
    {
        if (s_gameLocAvailable != null) return s_gameLocAvailable.Value;
        try
        {
            var resolved = new LocString("cards", key).GetFormattedText();
            s_gameLocAvailable = !string.IsNullOrEmpty(resolved) && resolved != key && !resolved.Contains(key);
        }
        catch
        {
            s_gameLocAvailable = false;
        }
        return s_gameLocAvailable.Value;
    }

    /// <summary>
    /// 显示名解析：优先游戏 LocString（若本地化表已挂载则跟随游戏语言），
    /// 解析失败（返回 key 本身/空）时回退自加载字典（zhs→eng→fallback）。
    /// </summary>
    public static string ResolveDisplayName(string key, string fallback)
    {
        if (!string.IsNullOrEmpty(key) && ProbeGameLoc(key))
        {
            try
            {
                var resolved = new LocString("cards", key).GetFormattedText();
                if (!string.IsNullOrEmpty(resolved) && resolved != key && !resolved.Contains(key))
                    return resolved;
            }
            catch
            {
                // LocManager 不可用（如初始化早期）→ 走自加载
            }
        }
        return Get(key, fallback);
    }
}
