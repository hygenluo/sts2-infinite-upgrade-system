using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌修改追踪器 — 按牌组位置跟踪所有修改（升级 + 属性修改 + Keyword 等）。
/// 在检查点时写盘，读档时按顺序重放。
/// </summary>
public static class CardUpgradeTracker
{
    /// <summary>deckIndex → 修改记录</summary>
    private static readonly Dictionary<int, CardModRecord> s_records = new();

    public enum ModType
    {
        Upgrade,
        DamagePlus,
        BlockPlus,
        DrawPlus,
        ReplayPlus,
        RepeatPlus,
        KeywordAdd,
        KeywordRemove,
        EnergyReduce,
    }

    [Serializable]
    public struct CardModEntry
    {
        public string Type { get; set; }     // ModType 名称
        public string? Keyword { get; set; } // Keyword 操作时的 CardKeyword 名称
    }

    private struct CardModRecord
    {
        public string TemplateId;
        public List<CardModEntry> Entries;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    // ═══════════════════════════════════════════════════════════════
    // 公共 API
    // ═══════════════════════════════════════════════════════════════

    public static int GetUpgradeCount(CardModel card)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return 0;
        int index = FindCardIndex(player.Deck.Cards, card);
        if (index < 0 || !s_records.TryGetValue(index, out var r)) return 0;
        return r.Entries.Count(e => e.Type == nameof(ModType.Upgrade));
    }

    public static void RecordModification(CardModel card, string seed, ModType type, string? keyword = null)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;

        int index = FindCardIndex(player.Deck.Cards, card);
        if (index < 0)
        {
            Log.Warn("InfiniteUpgrade: card not found in deck, cannot track.");
            return;
        }

        if (!s_records.ContainsKey(index))
            s_records[index] = new CardModRecord
            {
                TemplateId = card.Id.Entry,
                Entries = new List<CardModEntry>()
            };

        var entry = new CardModEntry { Type = type.ToString() };
        if (keyword != null) entry.Keyword = keyword;
        s_records[index].Entries.Add(entry);
        // 不立即写盘 — SaveCheckpoint 在检查点时保存
    }

    public static void ReapplyAll(Player player)
    {
        if (s_records.Count == 0) return;

        var cards = player.Deck.Cards;
        int applied = 0;

        foreach (var kv in s_records)
        {
            int index = kv.Key;
            var record = kv.Value;
            if (record.Entries.Count == 0) continue;
            if (index >= cards.Count) continue;

            var card = cards[index];
            if (card.Id.Entry != record.TemplateId) continue;

            // 重放所有修改
            foreach (var entry in record.Entries)
            {
                ApplyModification(card, entry);
            }
            applied++;
        }
        Log.Info($"InfiniteUpgrade: reapplied modifications to {applied} cards.");
    }

    public static void SaveCheckpoint(string seed) => SaveToDisk(seed);

    public static void Load(string seed)
    {
        s_records.Clear();
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<CardModEntry>>>(json, s_jsonOptions);
            if (data != null)
            {
                foreach (var kv in data)
                {
                    if (int.TryParse(kv.Key, out int index) && kv.Value != null)
                    {
                        s_records[index] = new CardModRecord
                        {
                            TemplateId = "", // 旧格式可能没有，由后续升级更新
                            Entries = kv.Value
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to load: {ex.Message}");
        }
    }

    public static void Delete() => s_records.Clear();

    // ═══════════════════════════════════════════════════════════════
    // 内部
    // ═══════════════════════════════════════════════════════════════

    private static void ApplyModification(CardModel card, CardModEntry entry)
    {
        if (!Enum.TryParse<ModType>(entry.Type, out var type)) return;

        switch (type)
        {
            case ModType.Upgrade:
                CardOperationHelper.UpgradeWithoutTracking(card);
                break;
            case ModType.DamagePlus:
                if (!card.IsMutable) card = card.ToMutable();
                card.DynamicVars.Damage.BaseValue += 1m;
                break;
            case ModType.BlockPlus:
                if (!card.IsMutable) card = card.ToMutable();
                card.DynamicVars.Block.BaseValue += 1m;
                break;
            case ModType.DrawPlus:
                if (!card.IsMutable) card = card.ToMutable();
                card.DynamicVars.Cards.BaseValue += 1m;
                break;
            case ModType.ReplayPlus:
                if (!card.IsMutable) card = card.ToMutable();
                card.BaseReplayCount += 1;
                break;
            case ModType.RepeatPlus:
                if (!card.IsMutable) card = card.ToMutable();
                card.DynamicVars.Repeat.BaseValue += 1m;
                break;
            case ModType.KeywordAdd:
                if (entry.Keyword != null && Enum.TryParse<CardKeyword>(entry.Keyword, out var kwAdd))
                {
                    if (!card.IsMutable) card = card.ToMutable();
                    card.AddKeyword(kwAdd);
                }
                break;
            case ModType.KeywordRemove:
                if (entry.Keyword != null && Enum.TryParse<CardKeyword>(entry.Keyword, out var kwRem))
                {
                    if (!card.IsMutable) card = card.ToMutable();
                    card.RemoveKeyword(kwRem);
                }
                break;
            case ModType.EnergyReduce:
                if (!card.IsMutable) card = card.ToMutable();
                var cur = card.EnergyCost.Canonical;
                if (cur > 0) card.EnergyCost.SetCustomBaseCost(cur - 1);
                break;
        }
    }

    private static void SaveToDisk(string seed)
    {
        try
        {
            var path = GetFilePath(seed);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var serializable = new Dictionary<string, List<CardModEntry>>();
            foreach (var kv in s_records)
                serializable[kv.Key.ToString()] = kv.Value.Entries;

            File.WriteAllText(path, JsonSerializer.Serialize(serializable, s_jsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: failed to save: {ex.Message}");
        }
    }

    private static string GetFilePath(string seed)
    {
        var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
        var dir = Path.Combine(modDir, "runs");
        return Path.Combine(dir, $"card_upgrades_{seed}.json");
    }

    private static int FindCardIndex(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
            if (ReferenceEquals(cards[i], target))
                return i;
        return -1;
    }
}
