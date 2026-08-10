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
    public const string ItemTestP1 = "INFINITEUPGRADESYSTEM-ITEM_TEST_P1";
    public const string ItemTestP5 = "INFINITEUPGRADESYSTEM-ITEM_TEST_P5";
    public const string ItemTestP10 = "INFINITEUPGRADESYSTEM-ITEM_TEST_P10";

    public const string ItemSkillBlockOnPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_BLOCK_ON_PLAY";
    public const string ItemSkillAgilityEvery4Plays = "INFINITEUPGRADESYSTEM-ITEM_SKILL_AGILITY_EVERY_4_PLAYS";
    public const string ItemSkillStrengthEvery4Plays = "INFINITEUPGRADESYSTEM-ITEM_SKILL_STRENGTH_EVERY_4_PLAYS";
    public const string ItemSkillDrawWhenNoHand = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_WHEN_NO_HAND";
    public const string ItemSkillRestAllOptions = "INFINITEUPGRADESYSTEM-ITEM_SKILL_REST_ALL_OPTIONS";
    public const string ItemSkillBlockAtTurnStart = "INFINITEUPGRADESYSTEM-ITEM_SKILL_BLOCK_AT_TURN_START";
    public const string ItemSkillDrawOnExhaust = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_ON_EXHAUST";
    public const string ItemSkillDrawOnHpLoss = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_ON_HP_LOSS";
    public const string ItemSkillDoubleBlockAtTurnEnd = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DOUBLE_BLOCK_AT_TURN_END";
    public const string ItemSkillWeakOnDiscard = "INFINITEUPGRADESYSTEM-ITEM_SKILL_WEAK_ON_DISCARD";
    public const string ItemSkillBlockOnPoison = "INFINITEUPGRADESYSTEM-ITEM_SKILL_BLOCK_ON_POISON";
    public const string ItemSkillPoisonAllOnCardPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_POISON_ALL_ON_CARD_PLAY";
    public const string ItemSkillForgeOnPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_FORGE_ON_PLAY";
    public const string ItemSkillVigorOnSkillPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_VIGOR_ON_SKILL_PLAY";
    public const string ItemSkillSovereignBladeBlockOnForge = "INFINITEUPGRADESYSTEM-ITEM_SKILL_SOVEREIGN_BLADE_BLOCK_ON_FORGE";
    public const string ItemSkillSummonOnPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_SUMMON_ON_PLAY";
    public const string ItemSkillDmgOnEtherealPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DMG_ON_ETHEREAL_PLAY";
    public const string ItemSkillDrawOnPowerPlay = "INFINITEUPGRADESYSTEM-ITEM_SKILL_DRAW_ON_POWER_PLAY";
    public const string ItemSkillOrbDmgAtTurnEnd = "INFINITEUPGRADESYSTEM-ITEM_SKILL_ORB_DMG_AT_TURN_END";

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
