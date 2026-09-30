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
/// 卡牌修改追踪器 —— 按 **玩家（NetId）+ 卡牌身份** 记录本模组对卡牌做过的修改。
///
/// 身份 = `{TemplateId}__{同类实例序号}`（如 `STRIKE__0`）。游戏存档里卡牌只有
/// ModelId/升级等级/附魔/属性（见 `SerializableCard`，无实例唯一 id），
/// **牌组顺序就是身份来源**（`List&lt;SerializableCard&gt;` 按序序列化，两端一致），
/// 因此「同类卡中的第几张」是唯一可用的稳定身份。
///
/// ── v3 多人联机修复（关键） ────────────────────────────────────────
/// 旧版把所有玩家的记录塞进**一个扁平字典**（key 只有卡牌身份），并只在 RunStarted 时
/// 给**本地玩家**重放（`LocalContext.GetMe`）→ 两端各重放自己那份 →
/// 读档后 host 端只恢复 host 的卡牌修改、client 端只恢复 client 的 → **两端牌组状态分歧**
/// （checksum 报错），且玩家 A 的修改会串到玩家 B 的同名卡上。
/// v3 修复：
/// 1. 记录按玩家分桶（NetId → 卡牌身份 → 记录），互不串味；
/// 2. RunStarted 时**两端都为所有玩家**重放（谁重放都一样，结果确定性一致）；
/// 3. 旧版扁平存档在 RunStarted 时迁移给本地玩家（单人档无损）。
/// </summary>
public static class CardUpgradeTracker
{
    // ═══════════════════════════════════════════════════════════════
    // 数据结构
    // ═══════════════════════════════════════════════════════════════

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
        Enchant,
        DeckRemove, // 删牌（CardMod action 分发用，不记录修改）
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

    /// <summary>玩家 NetId → （卡牌身份 → 修改记录）。</summary>
    private static readonly Dictionary<ulong, Dictionary<string, CardModRecord>> s_byPlayer = new();

    /// <summary>旧版扁平存档（无玩家归属）：RunStarted 时迁移给本地玩家。</summary>
    private static Dictionary<string, CardModRecord>? s_legacy;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    // ═══════════════════════════════════════════════════════════════
    // 公共 API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>某张卡被本模组无限升级过的次数（按卡牌 owner 取桶，多人下不会串玩家）。</summary>
    public static int GetUpgradeCount(CardModel card)
    {
        var player = card?.Owner ?? CardOperationHelper.GetLocalPlayer();
        if (player == null || card == null) return 0;
        var bucket = Bucket(player);
        string identity = GetCardIdentity(card, player.Deck.Cards);
        if (!bucket.TryGetValue(identity, out var r)) return 0;
        return r.Entries.Count(e => e.Type == nameof(ModType.Upgrade));
    }

    /// <summary>记录修改（按被修改卡的 owner 玩家分桶；两端 action 内一致调用）。</summary>
    public static void RecordModification(Player player, CardModel card, string seed, ModType type, string? keyword = null)
    {
        if (player == null || card == null) return;

        var bucket = Bucket(player);
        string identity = GetCardIdentity(card, player.Deck.Cards);

        if (!bucket.TryGetValue(identity, out var record))
        {
            record = new CardModRecord
            {
                TemplateId = card.Id.Entry,
                Entries = new List<CardModEntry>(),
            };
        }

        var entry = new CardModEntry { Type = type.ToString() };
        if (keyword != null) entry.Keyword = keyword;
        record.Entries.Add(entry);
        bucket[identity] = record;
        SaveToDisk(seed);
    }

    /// <summary>
    /// RunStarted：迁移旧版存档 + 给**所有玩家**重放全部修改。
    /// 两端都执行同一份确定性重放 → 两端牌组状态一致（v3 多人修复的核心）。
    /// </summary>
    public static void ReapplyAllPlayers(RunState runState)
    {
        if (runState == null) return;
        MigrateLegacy(runState);
        foreach (var player in runState.Players)
        {
            if (player?.Deck?.Cards == null) continue;
            ReapplyAll(player);
        }
        Log.Info($"InfiniteUpgrade: ReapplyAllPlayers done — buckets=[" +
                 string.Join(", ", s_byPlayer.Select(kv => $"{kv.Key}:{kv.Value.Count}")) + "]");
    }

    /// <summary>
    /// 重放某玩家的全部卡牌修改。
    ///
    /// 算法：
    /// 1. 遍历其牌组（按顺序），计算每张卡的身份 = "{TemplateId}__{实例序号}"
    /// 2. 精确匹配该身份的记录 → 应用
    /// 3. 未匹配记录（牌组变化导致身份偏移）→ 按 TemplateId 回退匹配未被占用的同名卡
    /// 4. 回退匹配成功后把记录迁移到新身份（下次读档即精确匹配）
    /// </summary>
    public static void ReapplyAll(Player player)
    {
        if (player?.Deck?.Cards == null) return;
        var bucket = Bucket(player);
        if (bucket.Count == 0) return;

        var cards = player.Deck.Cards;

        // 阶段 1：为当前牌组每张卡计算身份
        var deckIdentities = new List<(string Identity, int DeckIndex, CardModel Card)>();
        var templateCounter = new Dictionary<string, int>();
        for (int i = 0; i < cards.Count; i++)
        {
            string t = cards[i].Id.Entry;
            templateCounter.TryGetValue(t, out int n);
            deckIdentities.Add(($"{t}__{n}", i, cards[i]));
            templateCounter[t] = n + 1;
        }

        var identityToCard = new Dictionary<string, CardModel>();
        foreach (var (id, _, card) in deckIdentities)
            identityToCard[id] = card;

        // 阶段 2：精确匹配
        var matchedRecords = new HashSet<string>();
        var unmatchedRecords = new List<(string Identity, CardModRecord Record)>();
        foreach (var kv in bucket)
        {
            if (identityToCard.ContainsKey(kv.Key)) matchedRecords.Add(kv.Key);
            else unmatchedRecords.Add((kv.Key, kv.Value));
        }

        // 阶段 3：回退匹配（同名卡 + 身份未被占用）
        var fallbackAssignments = new List<(string OldIdentity, string NewIdentity, CardModel Card)>();
        var usedFallbackIdentities = new HashSet<string>();
        foreach (var (oldId, record) in unmatchedRecords)
        {
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
                Log.Warn($"InfiniteUpgrade: ReapplyAll[{player.NetId}] cannot match record {oldId} " +
                         $"({record.TemplateId}, {record.Entries.Count} mods); modifications lost.");
        }
        foreach (var (oldId, newId, _) in fallbackAssignments)
            Log.Info($"InfiniteUpgrade: ReapplyAll[{player.NetId}] fallback match {oldId} → {newId}");

        // 重建桶：把回退匹配的记录迁到新身份
        if (fallbackAssignments.Count > 0)
        {
            var rebuilt = new Dictionary<string, CardModRecord>();
            foreach (var oldId in matchedRecords)
                rebuilt[oldId] = bucket[oldId];
            foreach (var (oldId, newId, _) in fallbackAssignments)
            {
                if (rebuilt.TryGetValue(newId, out var existing))
                    existing.Entries.AddRange(bucket[oldId].Entries);
                else
                    rebuilt[newId] = bucket[oldId];
            }
            bucket.Clear();
            foreach (var kv in rebuilt)
                bucket[kv.Key] = kv.Value;
        }

        // 阶段 4：应用修改
        int applied = 0;
        var allMatchedIdentities = new HashSet<string>(matchedRecords);
        foreach (var (_, id, _) in fallbackAssignments)
            allMatchedIdentities.Add(id);

        foreach (var identity in allMatchedIdentities)
        {
            if (!identityToCard.TryGetValue(identity, out var card)) continue;
            if (!bucket.TryGetValue(identity, out var record)) continue;
            if (record.Entries.Count == 0) continue;

            GD.Print($"[InfiniteUpgrade] Reapplying {record.Entries.Count} mods to {card.Id.Entry} " +
                     $"(player={player.NetId}, identity={identity})");

            var mutable = EnsureMutable(card, player);
            foreach (var entry in record.Entries)
                ApplyModification(mutable, entry);
            applied++;
        }

        if (fallbackAssignments.Count > 0)
        {
            try
            {
                SaveToDisk(RunManager.Instance?.State?.Rng?.StringSeed ?? "unknown");
            }
            catch (Exception ex)
            {
                Log.Warn($"InfiniteUpgrade: ReapplyAll failed to save rewritten records: {ex.Message}");
            }
        }

        Log.Info($"InfiniteUpgrade: reapplied modifications to {applied} cards (player={player.NetId}).");
    }

    public static void SaveCheckpoint(string seed) => SaveToDisk(seed);

    /// <summary>
    /// 删牌后按身份修正该玩家的修改记录：被删卡的记录丢弃，剩余卡按当前牌组顺序重新分配身份。
    /// 两端都执行同一份确定性重排 → 结果一致。
    /// </summary>
    public static void OnCardRemoved(Player player, int removedIndex, string seed)
    {
        if (player == null || removedIndex < 0) return;
        var bucket = Bucket(player);
        var cards = player.Deck.Cards;

        // 收集旧记录（按 TemplateId 分组，保留 Entry 列表顺序）
        var oldEntriesByTemplate = new Dictionary<string, Queue<List<CardModEntry>>>();
        foreach (var kv in bucket.OrderBy(kv => kv.Key))
        {
            if (kv.Value.Entries.Count == 0) continue;
            string t = kv.Value.TemplateId;
            if (!oldEntriesByTemplate.ContainsKey(t))
                oldEntriesByTemplate[t] = new Queue<List<CardModEntry>>();
            oldEntriesByTemplate[t].Enqueue(kv.Value.Entries);
        }

        bucket.Clear();
        var templateCounter = new Dictionary<string, int>();
        foreach (var card in cards)
        {
            string t = card.Id.Entry;
            templateCounter.TryGetValue(t, out int n);
            string newId = $"{t}__{n}";
            templateCounter[t] = n + 1;

            if (oldEntriesByTemplate.TryGetValue(t, out var queue) && queue.Count > 0)
            {
                bucket[newId] = new CardModRecord
                {
                    TemplateId = t,
                    Entries = queue.Dequeue(),
                };
            }
        }

        SaveToDisk(seed);
    }

    // ═══════════════════════════════════════════════════════════════
    // 持久化
    // ═══════════════════════════════════════════════════════════════

    public static void Load(string seed)
    {
        s_byPlayer.Clear();
        s_legacy = null;
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);

            // 格式 v3（含玩家 NetId）
            var v3 = JsonSerializer.Deserialize<List<SerializableRecordV3>>(json, s_jsonOptions);
            if (v3 != null && v3.Count > 0 && v3.Any(r => r.P != 0))
            {
                foreach (var r in v3)
                {
                    if (string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.T) || r.E == null) continue;
                    Bucket(r.P)[r.Id] = new CardModRecord { TemplateId = r.T, Entries = r.E };
                }
                Log.Info($"InfiniteUpgrade: loaded card records for {s_byPlayer.Count} player(s) (v3 format).");
                return;
            }

            // 格式 v2（扁平，无玩家归属）→ 记为 legacy，RunStarted 迁移给本地玩家
            var v2 = JsonSerializer.Deserialize<List<SerializableRecordV2>>(json, s_jsonOptions);
            if (v2 != null && v2.Count > 0 && !string.IsNullOrEmpty(v2[0].Id))
            {
                s_legacy = new Dictionary<string, CardModRecord>();
                foreach (var r in v2)
                {
                    if (string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.T) || r.E == null) continue;
                    s_legacy[r.Id] = new CardModRecord { TemplateId = r.T, Entries = r.E };
                }
                Log.Info($"InfiniteUpgrade: loaded {s_legacy.Count} legacy (v2) card records.");
                return;
            }

            // 格式 v1（按牌组下标）→ 合并成 legacy 合成身份
            var v1 = JsonSerializer.Deserialize<List<SerializableRecordV1>>(json, s_jsonOptions);
            if (v1 != null && v1.Count > 0)
            {
                var groups = new Dictionary<string, List<SerializableRecordV1>>();
                foreach (var r in v1)
                {
                    if (string.IsNullOrEmpty(r.T) || r.E == null || r.E.Count == 0) continue;
                    if (!groups.ContainsKey(r.T)) groups[r.T] = new List<SerializableRecordV1>();
                    groups[r.T].Add(r);
                }

                s_legacy = new Dictionary<string, CardModRecord>();
                int syntheticId = 0;
                foreach (var (templateId, recs) in groups)
                {
                    var merged = new CardModRecord
                    {
                        TemplateId = templateId,
                        Entries = new List<CardModEntry>(),
                    };
                    foreach (var r in recs.OrderBy(r => r.I))
                        merged.Entries.AddRange(r.E);
                    s_legacy[$"__LEGACY_SYNTH_{syntheticId}__{templateId}"] = merged;
                    syntheticId++;
                }
                Log.Info($"InfiniteUpgrade: loaded {v1.Count} v1 records → {s_legacy.Count} legacy groups.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"InfiniteUpgrade: failed to load card records: {ex.Message}");
        }
    }

    public static void Delete()
    {
        s_byPlayer.Clear();
        s_legacy = null;
    }

    /// <summary>本地玩家是否已有记录（诊断用）。</summary>
    public static bool HasAnyRecords(Player player) => player != null && Bucket(player).Count > 0;

    /// <summary>旧版扁平存档迁移给本地玩家（两端各自迁移自己那份；store 权威数据以 NetId 分桶后不再串味）。</summary>
    private static void MigrateLegacy(RunState runState)
    {
        if (s_legacy == null || s_legacy.Count == 0) { s_legacy = null; return; }
        var me = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(runState);
        if (me == null) return;

        var bucket = Bucket(me.NetId);
        foreach (var kv in s_legacy)
        {
            if (bucket.TryGetValue(kv.Key, out var existing))
                existing.Entries.AddRange(kv.Value.Entries);
            else
                bucket[kv.Key] = kv.Value;
        }
        Log.Info($"InfiniteUpgrade: migrated {s_legacy.Count} legacy card records to player {me.NetId}.");
        s_legacy = null;
        SaveToDisk(runState.Rng?.StringSeed ?? "unknown");
    }

    private static void SaveToDisk(string seed)
    {
        try
        {
            var path = GetFilePath(seed);
            var list = new List<SerializableRecordV3>();
            foreach (var (netId, bucket) in s_byPlayer)
            {
                foreach (var kv in bucket)
                {
                    list.Add(new SerializableRecordV3
                    {
                        P = netId,
                        Id = kv.Key,
                        T = kv.Value.TemplateId,
                        E = kv.Value.Entries,
                    });
                }
            }
            File.WriteAllText(path, JsonSerializer.Serialize(list, s_jsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"InfiniteUpgrade: failed to save card records: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 内部
    // ═══════════════════════════════════════════════════════════════

    /// <summary>格式 v3：带玩家 NetId。</summary>
    [Serializable]
    private class SerializableRecordV3
    {
        public ulong P { get; set; }              // player NetId
        public string Id { get; set; } = "";      // card identity: "STRIKE__0"
        public string T { get; set; } = "";       // templateId
        public List<CardModEntry> E { get; set; } = new();
    }

    /// <summary>格式 v2（旧）：身份为字符串、无玩家归属。</summary>
    [Serializable]
    private class SerializableRecordV2
    {
        public string Id { get; set; } = "";
        public string T { get; set; } = "";
        public List<CardModEntry> E { get; set; } = new();
    }

    /// <summary>格式 v1（旧）：下标为整数。</summary>
    [Serializable]
    private class SerializableRecordV1
    {
        public int I { get; set; }
        public string T { get; set; } = "";
        public List<CardModEntry> E { get; set; } = new();
    }

    private static Dictionary<string, CardModRecord> Bucket(Player player) => Bucket(player?.NetId ?? 0);

    private static Dictionary<string, CardModRecord> Bucket(ulong netId)
    {
        if (!s_byPlayer.TryGetValue(netId, out var bucket))
        {
            bucket = new Dictionary<string, CardModRecord>();
            s_byPlayer[netId] = bucket;
        }
        return bucket;
    }

    /// <summary>
    /// 计算卡牌身份 = "{TemplateId}__{实例序号}"（CardMod action 选卡后编码 / 两端找卡用）。
    /// 实例序号 = 在牌组中从头扫描，遇到同 TemplateId 的卡牌时递增，到目标卡为止。
    /// </summary>
    public static string GetCardIdentity(CardModel target, IReadOnlyList<CardModel> deck)
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
        return $"{target.Id.Entry}__{counter}";
    }

    /// <summary>确保卡牌可变并写回牌组（直接改用可变副本会变成孤儿副本，修改丢失）。</summary>
    private static CardModel EnsureMutable(CardModel card, Player player)
    {
        if (card.IsMutable) return card;
        var mutable = card.ToMutable();
        var deckList = player.Deck.Cards as IList<CardModel>;
        for (int i = 0; i < (deckList?.Count ?? 0); i++)
        {
            if (ReferenceEquals(deckList![i], card))
            {
                deckList[i] = mutable;
                break;
            }
        }
        return mutable;
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
            case ModType.Enchant:
                if (entry.Keyword != null)
                    CardOperationHelper.ApplyEnchantmentToCard(card, entry.Keyword);
                break;
        }
    }

    /// <summary>移除某张卡指定附魔类型的全部记录（多种附魔替换用）。</summary>
    public static void RemoveEnchantmentEntries(Player player, CardModel card, string seed, string enchantType)
    {
        if (player == null || card == null) return;
        var bucket = Bucket(player);
        string identity = GetCardIdentity(card, player.Deck.Cards);
        if (!bucket.TryGetValue(identity, out var rec)) return;
        rec.Entries.RemoveAll(e => e.Type == nameof(ModType.Enchant) && e.Keyword == enchantType);
        SaveToDisk(seed);
    }

    private static string GetFilePath(string seed) => SavePaths.GetFilePath("card_upgrades", seed);
}
