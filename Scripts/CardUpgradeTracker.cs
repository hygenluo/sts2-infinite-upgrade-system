using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
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
        SaveToDisk(seed); // 每次修改立即写盘，避免中途退出丢失
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
            if (index >= cards.Count)
            {
                Log.Warn($"InfiniteUpgrade: ReapplyAll skip index={index} >= count={cards.Count}");
                continue;
            }

            var card = cards[index];
            if (card.Id.Entry != record.TemplateId)
            {
                Log.Warn($"InfiniteUpgrade: ReapplyAll skip index={index} template mismatch: card={card.Id.Entry} vs record={record.TemplateId}");
                continue;
            }

            GD.Print($"[InfiniteUpgrade] Reapplying {record.Entries.Count} mods to {card.Id.Entry} at index {index}");

            // 确保卡牌可变
            if (!card.IsMutable)
            {
                card = card.ToMutable();
                // 替换牌组中的引用
                var deckList = player.Deck.Cards as IList<CardModel>;
                if (deckList != null && !deckList.IsReadOnly)
                    deckList[index] = card;
            }

            foreach (var entry in record.Entries)
            {
                ApplyModification(card, entry);
            }
            applied++;
        }
        Log.Info($"InfiniteUpgrade: reapplied modifications to {applied} cards.");
        GD.Print($"[InfiniteUpgrade] ReapplyAll done: {applied} cards.");
    }

    public static void SaveCheckpoint(string seed) => SaveToDisk(seed);

    /// <summary>
    /// 删牌后修正按下标记录的修改：删除该卡的记录，其后的记录下标前移 1。
    /// 不修正的话读档重放会错位（template mismatch 跳过，或应用到错的卡）。
    /// </summary>
    public static void OnCardRemoved(int removedIndex, string seed)
    {
        if (removedIndex < 0) return;

        var shifted = new Dictionary<int, CardModRecord>();
        foreach (var kv in s_records)
        {
            if (kv.Key == removedIndex) continue;          // 被删卡的记录丢弃
            int newKey = kv.Key > removedIndex ? kv.Key - 1 : kv.Key;
            shifted[newKey] = kv.Value;
        }
        s_records.Clear();
        foreach (var kv in shifted) s_records[kv.Key] = kv.Value;

        SaveToDisk(seed);
    }

    public static void Load(string seed)
    {
        s_records.Clear();
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            // 新格式：List<{I, T, E}> (index, templateId, entries)
            var data = JsonSerializer.Deserialize<List<SerializableRecord>>(json, s_jsonOptions);
            if (data != null)
            {
                foreach (var r in data)
                {
                    if (r.I >= 0 && !string.IsNullOrEmpty(r.T) && r.E != null)
                    {
                        s_records[r.I] = new CardModRecord
                        {
                            TemplateId = r.T,
                            Entries = r.E
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

    [Serializable]
    private class SerializableRecord
    {
        public int I { get; set; }            // deck index
        public string T { get; set; } = "";   // templateId
        public List<CardModEntry> E { get; set; } = new(); // entries
    }

    private static void SaveToDisk(string seed)
    {
        try
        {
            var path = GetFilePath(seed);

            var list = new List<SerializableRecord>();
            foreach (var kv in s_records)
                list.Add(new SerializableRecord
                {
                    I = kv.Key,
                    T = kv.Value.TemplateId,
                    E = kv.Value.Entries
                });

            File.WriteAllText(path, JsonSerializer.Serialize(list, s_jsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: failed to save: {ex.Message}");
        }
    }

    private static void ApplyModification(CardModel card, CardModEntry entry)
    {
        if (!Enum.TryParse<ModType>(entry.Type, out var type)) return;

        // ReapplyAll 已保证卡牌可变且写回牌组；此处 ToMutable 只会产生孤儿副本，直接跳过
        if (!card.IsMutable)
        {
            Log.Warn($"InfiniteUpgrade: ApplyModification skip {entry.Type} on immutable card {card.Id.Entry}.");
            return;
        }

        switch (type)
        {
            case ModType.Upgrade:
                CardOperationHelper.UpgradeWithoutTracking(card);
                break;
            case ModType.DamagePlus:
                card.DynamicVars.Damage.BaseValue += 1m;
                break;
            case ModType.BlockPlus:
                card.DynamicVars.Block.BaseValue += 1m;
                break;
            case ModType.DrawPlus:
                card.DynamicVars.Cards.BaseValue += 1m;
                break;
            case ModType.ReplayPlus:
                card.BaseReplayCount += 1;
                break;
            case ModType.KeywordAdd:
                if (entry.Keyword != null && Enum.TryParse<CardKeyword>(entry.Keyword, out var kwAdd))
                    card.AddKeyword(kwAdd);
                break;
            case ModType.KeywordRemove:
                if (entry.Keyword != null && Enum.TryParse<CardKeyword>(entry.Keyword, out var kwRem))
                    card.RemoveKeyword(kwRem);
                break;
            case ModType.EnergyReduce:
                var cur = card.EnergyCost.Canonical;
                if (cur > 0) card.EnergyCost.SetCustomBaseCost(cur - 1);
                break;
        }
    }

    private static string GetFilePath(string seed) => SavePaths.GetFilePath("card_upgrades", seed);

    private static int FindCardIndex(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
            if (ReferenceEquals(cards[i], target))
                return i;
        return -1;
    }
}
