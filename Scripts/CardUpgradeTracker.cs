using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌升级追踪器 — 按牌组位置（deckIndex）区分同名卡牌。
/// 每 Run 独立文件，通过 RunState.Rng.StringSeed 区分。
/// </summary>
public static class CardUpgradeTracker
{
    /// <summary>deckIndex → (templateId, count)</summary>
    private static readonly Dictionary<int, UpgradeRecord> s_records = new();

    private struct UpgradeRecord
    {
        public string TemplateId { get; set; }
        public int Count { get; set; }
    }

    private static string GetFilePath(string seed)
    {
        var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
        var dir = Path.Combine(modDir, "runs");
        return Path.Combine(dir, $"card_upgrades_{seed}.json");
    }

    public static int GetUpgradeCount(CardModel card)
    {
        // 通过引用查找 deck 中的位置
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return 0;
        int index = FindCardIndex(player.Deck.Cards, card);
        if (index < 0) return 0;
        return s_records.TryGetValue(index, out var r) ? r.Count : 0;
    }

    public static void RecordUpgrade(CardModel card, string seed)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;

        int index = FindCardIndex(player.Deck.Cards, card);
        if (index < 0)
        {
            Log.Warn("InfiniteUpgrade: upgraded card not found in deck, cannot track by index.");
            return;
        }

        if (!s_records.ContainsKey(index))
            s_records[index] = new UpgradeRecord { TemplateId = card.Id.Entry, Count = 0 };
        var record = s_records[index];
        record.Count++;
        s_records[index] = record;
        Save(seed);
    }

    public static void ReapplyAllUpgrades(Player player)
    {
        if (s_records.Count == 0) return;

        var cards = player.Deck.Cards;
        int applied = 0;

        foreach (var kv in s_records)
        {
            int index = kv.Key;
            var record = kv.Value;
            if (record.Count == 0) continue;

            // 索引越界检查
            if (index >= cards.Count) continue;

            var card = cards[index];
            // 模板 ID 校验：确保该位置仍是同一张牌（未被删除/替换）
            if (card.Id.Entry != record.TemplateId) continue;

            int gameApplied = (card.CurrentUpgradeLevel > 0) ? 1 : 0;
            int toApply = record.Count - gameApplied;
            for (int i = 0; i < toApply; i++)
                CardOperationHelper.UpgradeWithoutTracking(card);
            applied++;
        }
        Log.Info($"InfiniteUpgrade: reapplied upgrades to {applied} cards.");
    }

    public static void Save(string seed)
    {
        try
        {
            var path = GetFilePath(seed);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 转换为可序列化的格式
            var serializable = new Dictionary<string, UpgradeRecordData>();
            foreach (var kv in s_records)
                serializable[kv.Key.ToString()] = new UpgradeRecordData
                {
                    TemplateId = kv.Value.TemplateId,
                    Count = kv.Value.Count
                };

            File.WriteAllText(path, JsonSerializer.Serialize(serializable));
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: failed to save card upgrades: {ex.Message}");
        }
    }

    public static void Load(string seed)
    {
        s_records.Clear();
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path)) return;

            var data = JsonSerializer.Deserialize<Dictionary<string, UpgradeRecordData>>(File.ReadAllText(path));
            if (data != null)
            {
                foreach (var kv in data)
                {
                    if (int.TryParse(kv.Key, out int index) && kv.Value != null)
                    {
                        s_records[index] = new UpgradeRecord
                        {
                            TemplateId = kv.Value.TemplateId,
                            Count = kv.Value.Count
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to load card upgrades: {ex.Message}");
        }
    }

    public static void Delete()
    {
        s_records.Clear();
    }

    private static int FindCardIndex(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
            if (ReferenceEquals(cards[i], target))
                return i;
        return -1;
    }

    /// <summary>JSON 序列化用。</summary>
    private class UpgradeRecordData
    {
        public string TemplateId { get; set; } = "";
        public int Count { get; set; }
    }
}
