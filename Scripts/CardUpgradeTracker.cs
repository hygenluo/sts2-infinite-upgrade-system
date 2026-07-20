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
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌修改追踪器 — 使用卡牌身份（TemplateId + 同类序号）而不是牌组下标追踪修改。
///
/// v25 重大修复：
/// - 之前用牌组下标（deck index）作为存储 key，但下标在快速重启后会变化（
///   游戏从检查点重建牌组时卡牌顺序可能与上次保存不一致），导致 ReapplyAll
///   找不到卡牌，修改记录静默丢失。
/// - 现在使用卡牌身份 = "{TemplateId}__{实例序号}"，基于牌组中同类卡的
///   相对顺序，在游戏存档序列化/反序列化后保持稳定。
/// - 同一张卡的多条修改记录合并存储，彻底消除"一张卡多条记录只匹配一条"的 bug。
/// </summary>
public static class CardUpgradeTracker
{
    /// <summary>cardIdentity → 修改记录（同一个 identity 下所有修改合并）</summary>
    private static readonly Dictionary<string, CardModRecord> s_records = new();

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
        public string Type { get; set; }
        public string? Keyword { get; set; }
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
        string identity = GetCardIdentity(card, player.Deck.Cards);
        if (!s_records.TryGetValue(identity, out var r)) return 0;
        return r.Entries.Count(e => e.Type == nameof(ModType.Upgrade));
    }

    public static void RecordModification(CardModel card, string seed, ModType type, string? keyword = null)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;

        string identity = GetCardIdentity(card, player.Deck.Cards);

        if (!s_records.ContainsKey(identity))
            s_records[identity] = new CardModRecord
            {
                TemplateId = card.Id.Entry,
                Entries = new List<CardModEntry>()
            };

        var entry = new CardModEntry { Type = type.ToString() };
        if (keyword != null) entry.Keyword = keyword;
        s_records[identity].Entries.Add(entry);
        SaveToDisk(seed);
    }

    /// <summary>
    /// 读档或新局开始时重放所有修改。
    ///
    /// 算法：
    /// 1. 遍历当前牌组（按顺序），计算每张卡的身份 = "{TemplateId}__{实例序号}"
    /// 2. 在 s_records 中查找对应身份的修改记录
    /// 3. 找到则应用全部修改
    /// 4. 找不到且 s_records 有对应 TemplateId 的未匹配记录时，尝试回退匹配
    ///    （处理牌组变化导致身份偏移的边界情况）
    /// </summary>
    public static void ReapplyAll(Player player)
    {
        if (s_records.Count == 0) return;

        var cards = player.Deck.Cards;

        // 阶段 1：为当前牌组每张卡计算身份
        var deckIdentities = new List<(string Identity, int DeckIndex, CardModel Card)>();
        var templateCounter = new Dictionary<string, int>();
        for (int i = 0; i < cards.Count; i++)
        {
            string t = cards[i].Id.Entry;
            templateCounter.TryGetValue(t, out int n);
            string identity = $"{t}__{n}";
            templateCounter[t] = n + 1;
            deckIdentities.Add((identity, i, cards[i]));
        }

        // 构建 identity → card 快速查找
        var identityToCard = new Dictionary<string, CardModel>();
        foreach (var (id, _, card) in deckIdentities)
            identityToCard[id] = card;

        // 阶段 2：精确匹配 — identity 完全相同
        var matchedRecords = new HashSet<string>();
        var unmatchedRecords = new List<(string Identity, CardModRecord Record)>();

        foreach (var kv in s_records)
        {
            if (identityToCard.ContainsKey(kv.Key))
                matchedRecords.Add(kv.Key);
            else
                unmatchedRecords.Add((kv.Key, kv.Value));
        }

        // 阶段 3：回退匹配 — 处理身份偏移的边界情况
        // 对于未匹配的记录，尝试在同名卡中寻找未被精确匹配使用的卡
        var fallbackAssignments = new List<(string OldIdentity, string NewIdentity, CardModel Card)>();
        var usedFallbackIdentities = new HashSet<string>();

        foreach (var (oldId, record) in unmatchedRecords)
        {
            // 先看有没有同 TemplateId 且未被精确匹配和回退匹配占用的卡
            foreach (var (identity, _, card) in deckIdentities)
            {
                if (card.Id.Entry == record.TemplateId
                    && !matchedRecords.Contains(identity)
                    && !usedFallbackIdentities.Contains(identity))
                {
                    fallbackAssignments.Add((oldId, identity, card));
                    usedFallbackIdentities.Add(identity);
                    break;
                }
            }
        }

        if (unmatchedRecords.Count > 0 && fallbackAssignments.Count == 0)
        {
            foreach (var (oldId, record) in unmatchedRecords)
            {
                Log.Warn($"InfiniteUpgrade: ReapplyAll cannot match record {oldId} ({record.TemplateId}, {record.Entries.Count} mods); modifications lost.");
            }
        }

        foreach (var (oldId, newId, _) in fallbackAssignments)
        {
            Log.Info($"InfiniteUpgrade: ReapplyAll fallback match {oldId} → {newId}");
        }

        // 阶段 4：应用修改
        int applied = 0;
        var allMatchedIdentities = new HashSet<string>(matchedRecords);
        foreach (var (_, id, _) in fallbackAssignments)
            allMatchedIdentities.Add(id);

        // 重建 s_records：将回退匹配的记录迁移到新 identity
        bool needsRewrite = fallbackAssignments.Count > 0;
        if (needsRewrite)
        {
            var newRecords = new Dictionary<string, CardModRecord>();
            foreach (var oldId in matchedRecords)
                newRecords[oldId] = s_records[oldId];
            foreach (var (oldId, newId, _) in fallbackAssignments)
            {
                // 如果新身份已存在记录（极罕见边界：同名卡在同位置），合并
                if (newRecords.TryGetValue(newId, out var existing))
                {
                    existing.Entries.AddRange(s_records[oldId].Entries);
                }
                else
                {
                    newRecords[newId] = s_records[oldId];
                }
            }
            s_records.Clear();
            foreach (var kv in newRecords)
                s_records[kv.Key] = kv.Value;
        }

        foreach (var identity in allMatchedIdentities)
        {
            if (!identityToCard.TryGetValue(identity, out var card)) continue;
            if (!s_records.TryGetValue(identity, out var record)) continue;
            if (record.Entries.Count == 0) continue;

            GD.Print($"[InfiniteUpgrade] Reapplying {record.Entries.Count} mods to {card.Id.Entry} (identity={identity})");

            var mutable = card;
            if (!mutable.IsMutable)
            {
                mutable = mutable.ToMutable();
                var deckList = player.Deck.Cards as IList<CardModel>;
                // 找到该卡在牌组中的实际下标
                for (int i = 0; i < (deckList?.Count ?? 0); i++)
                {
                    if (ReferenceEquals(deckList![i], card))
                    {
                        deckList[i] = mutable;
                        break;
                    }
                }
            }

            foreach (var entry in record.Entries)
            {
                ApplyModification(mutable, entry);
            }
            applied++;
        }

        // 阶段 5：保存（如果发生了回退匹配导致记录迁移）
        if (needsRewrite)
        {
            try
            {
                var seed = RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown";
                SaveToDisk(seed);
            }
            catch (Exception ex)
            {
                Log.Warn($"InfiniteUpgrade: ReapplyAll failed to save rewritten records: {ex.Message}");
            }
        }

        Log.Info($"InfiniteUpgrade: reapplied modifications to {applied} cards.");
        GD.Print($"[InfiniteUpgrade] ReapplyAll done: {applied} cards.");
    }

    public static void SaveCheckpoint(string seed) => SaveToDisk(seed);

    /// <summary>
    /// 删牌后修正按身份记录的修改。
    /// 被删卡的记录丢弃，剩余卡按当前牌组顺序重新分配身份。
    /// </summary>
    public static void OnCardRemoved(int removedIndex, string seed)
    {
        if (removedIndex < 0) return;

        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;

        var cards = player.Deck.Cards;

        // 收集旧记录中的所有修改（按 TemplateId 分组，保留 Entry 列表）
        // key: TemplateId, value: 按旧身份排序的 Entry 列表队列
        var oldEntriesByTemplate = new Dictionary<string, Queue<List<CardModEntry>>>();
        foreach (var kv in s_records.OrderBy(kv => kv.Key))
        {
            if (kv.Value.Entries.Count == 0) continue;
            string t = kv.Value.TemplateId;
            if (!oldEntriesByTemplate.ContainsKey(t))
                oldEntriesByTemplate[t] = new Queue<List<CardModEntry>>();
            oldEntriesByTemplate[t].Enqueue(kv.Value.Entries);
        }

        // 按当前牌组顺序重建身份 → 分配旧修改
        s_records.Clear();
        var templateCounter = new Dictionary<string, int>();
        foreach (var card in cards)
        {
            string t = card.Id.Entry;
            templateCounter.TryGetValue(t, out int n);
            string newId = $"{t}__{n}";
            templateCounter[t] = n + 1;

            if (oldEntriesByTemplate.TryGetValue(t, out var queue) && queue.Count > 0)
            {
                s_records[newId] = new CardModRecord
                {
                    TemplateId = t,
                    Entries = queue.Dequeue()
                };
            }
        }

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

            // 先尝试新格式（"Id" 字段为 string）
            var newData = JsonSerializer.Deserialize<List<SerializableRecordV2>>(json, s_jsonOptions);
            if (newData != null && newData.Count > 0 && !string.IsNullOrEmpty(newData[0].Id))
            {
                foreach (var r in newData)
                {
                    if (!string.IsNullOrEmpty(r.Id) && !string.IsNullOrEmpty(r.T) && r.E != null)
                    {
                        s_records[r.Id] = new CardModRecord
                        {
                            TemplateId = r.T,
                            Entries = r.E
                        };
                    }
                }
                Log.Info($"InfiniteUpgrade: loaded {s_records.Count} card records (v2 format).");
                return;
            }

            // 回退：旧格式（"I" 字段为 int），按 TemplateId 合并多条记录
            var oldData = JsonSerializer.Deserialize<List<SerializableRecordV1>>(json, s_jsonOptions);
            if (oldData != null && oldData.Count > 0)
            {
                // 按 TemplateId 分组 → 按原始下标排序 → 合并 Entries → 分配临时身份
                var groups = new Dictionary<string, List<SerializableRecordV1>>();
                foreach (var r in oldData)
                {
                    if (string.IsNullOrEmpty(r.T) || r.E == null || r.E.Count == 0) continue;
                    if (!groups.ContainsKey(r.T))
                        groups[r.T] = new List<SerializableRecordV1>();
                    groups[r.T].Add(r);
                }

                int syntheticId = 0;
                foreach (var (templateId, recs) in groups)
                {
                    var merged = new CardModRecord
                    {
                        TemplateId = templateId,
                        Entries = new List<CardModEntry>()
                    };
                    // 按原始下标排序 → 保证时间顺序
                    foreach (var r in recs.OrderBy(r => r.I))
                        merged.Entries.AddRange(r.E);

                    // 使用合成身份：ReapplyAll 的精确匹配找不到时会走回退匹配
                    string tempKey = $"__LEGACY_SYNTH_{syntheticId}__{templateId}";
                    s_records[tempKey] = merged;
                    syntheticId++;
                }

                Log.Info($"InfiniteUpgrade: loaded {oldData.Count} v1 records → merged into {s_records.Count} groups by TemplateId.");
                return;
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

    /// <summary>新格式：身份为字符串</summary>
    [Serializable]
    private class SerializableRecordV2
    {
        public string Id { get; set; } = "";      // card identity: "BASH__0"
        public string T { get; set; } = "";        // templateId
        public List<CardModEntry> E { get; set; } = new();
    }

    /// <summary>旧格式 v1：下标为整数（向后兼容）</summary>
    [Serializable]
    private class SerializableRecordV1
    {
        public int I { get; set; }                 // deck index (legacy)
        public string T { get; set; } = "";
        public List<CardModEntry> E { get; set; } = new();
    }

    /// <summary>
    /// 计算卡牌身份 = "{TemplateId}__{实例序号}"。
    /// 实例序号 = 在牌组中从头扫描，遇到同 TemplateId 的卡牌时递增，到目标卡为止。
    /// 使用 ReferenceEquals 精确匹配目标卡牌。
    /// </summary>
    private static string GetCardIdentity(CardModel target, IReadOnlyList<CardModel> deck)
    {
        int counter = 0;
        for (int i = 0; i < deck.Count; i++)
        {
            if (deck[i].Id.Entry == target.Id.Entry)
            {
                if (ReferenceEquals(deck[i], target))
                    return $"{target.Id.Entry}__{counter}";
                counter++;
            }
        }
        // 找不到（不应该发生）：返回最大序号
        return $"{target.Id.Entry}__{counter}";
    }

    private static void SaveToDisk(string seed)
    {
        try
        {
            var path = GetFilePath(seed);

            var list = new List<SerializableRecordV2>();
            foreach (var kv in s_records)
                list.Add(new SerializableRecordV2
                {
                    Id = kv.Key,
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
