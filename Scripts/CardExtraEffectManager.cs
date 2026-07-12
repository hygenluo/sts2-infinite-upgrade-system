using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 卡牌额外效果管理器。
/// 追踪卡牌上附加的效果（易伤/虚弱等），在卡牌打出时通过 Harmony 注入。
/// </summary>
public static class CardExtraEffectManager
{
    /// <summary>deckIndex → (templateId, effects list)</summary>
    private static readonly Dictionary<int, EffectRecord> s_records = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public enum EffectType
    {
        Vulnerable, Weak, Poison, Heal, Forge, Vigor, Calamity, Focus, Intangible, GainEnergy
    }

    private struct EffectRecord
    {
        public string TemplateId;
        public List<string> Effects; // EffectType name list
    }

    private static string GetFilePath(string seed)
    {
        var modDir = Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".";
        return Path.Combine(modDir, "runs", $"card_effects_{seed}.json");
    }

    public static void AddEffect(CardModel card, string seed, EffectType type)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return;
        int index = FindCardIndex(player.Deck.Cards, card);
        if (index < 0) return;

        if (!s_records.ContainsKey(index))
            s_records[index] = new EffectRecord { TemplateId = card.Id.Entry, Effects = new List<string>() };
        var r = s_records[index];
        r.Effects.Add(type.ToString());
        s_records[index] = r;
        Save(seed);
    }

    public static int GetEffectCount(CardModel card)
    {
        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null) return 0;
        int index = FindCardIndex(player.Deck.Cards, card);
        return index >= 0 && s_records.TryGetValue(index, out var r) ? r.Effects.Count : 0;
    }

    /// <summary>Harmony Postfix: 卡牌打出后注入额外效果。</summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    [HarmonyPostfix]
    public static async void InjectExtraEffects(CardModel __instance)
    {
        var player = __instance.Owner;
        if (player == null) return;
        int index = FindCardIndex(player.Deck.Cards, __instance);
        if (index < 0 || !s_records.TryGetValue(index, out var record)) return;

        foreach (var effStr in record.Effects)
        {
            if (!Enum.TryParse<EffectType>(effStr, out var effect)) continue;
            try
            {
                switch (effect)
                {
                    case EffectType.Vulnerable:
                        foreach (var c in player.Creature?.CombatState?.Creatures ?? Enumerable.Empty<Creature>())
                            if (!c.IsPlayer) await ApplyPower(c, "MegaCrit.Sts2.Core.Models.Powers.VulnerablePower", 1);
                        break;
                    case EffectType.Weak:
                        foreach (var c in player.Creature?.CombatState?.Creatures ?? Enumerable.Empty<Creature>())
                            if (!c.IsPlayer) await ApplyPower(c, "MegaCrit.Sts2.Core.Models.Powers.WeakPower", 1);
                        break;
                    case EffectType.Poison:
                        foreach (var c in player.Creature?.CombatState?.Creatures ?? Enumerable.Empty<Creature>())
                            if (!c.IsPlayer) await ApplyPower(c, "MegaCrit.Sts2.Core.Models.Powers.PoisonPower", 1);
                        break;
                    case EffectType.Heal:
                        if (player.Creature != null) await CreatureCmd.Heal(player.Creature, 1);
                        break;
                    case EffectType.Vigor:
                        if (player.Creature != null) await ApplyPower(player.Creature, "MegaCrit.Sts2.Core.Models.Powers.VigorPower", 1);
                        break;
                    case EffectType.Focus:
                        if (player.Creature != null) await ApplyPower(player.Creature, "MegaCrit.Sts2.Core.Models.Powers.FocusPower", 1);
                        break;
                    case EffectType.Intangible:
                        if (player.Creature != null) await ApplyPower(player.Creature, "MegaCrit.Sts2.Core.Models.Powers.IntangiblePower", 1);
                        break;
                    case EffectType.GainEnergy:
                        await PlayerCmd.GainEnergy(1, player);
                        break;
                    case EffectType.Forge:
                        await ForgeCmd.Forge(1, player, __instance);
                        break;
                }
            }
            catch (Exception ex) { Log.Warn($"ExtraEffect {effect}: {ex.Message}"); }
        }
    }

    private static async Task ApplyPower(Creature creature, string typeName, int amount)
    {
        Type? powerType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { powerType = asm.GetType(typeName); if (powerType != null) break; }
        if (powerType == null) return;

        var modelDb = FindType("MegaCrit.Sts2.Core.Models.ModelDb");
        if (modelDb == null) return;
        var getter = modelDb.GetMethod("Power", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
        if (getter == null) return;
        var template = getter.MakeGenericMethod(powerType).Invoke(null, null);
        if (template == null) return;
        var clone = template.GetType().GetMethod("MutableClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (clone == null) return;
        var mutable = clone.Invoke(template, null);
        if (mutable == null) return;
        var apply = mutable.GetType().GetMethod("ApplyInternal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (apply == null) return;
        apply.Invoke(mutable, new object[] { creature, (decimal)amount, false });
    }

    private static Type? FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { var t = asm.GetType(name); if (t != null) return t; }
        return null;
    }

    private static int FindCardIndex(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
            if (ReferenceEquals(cards[i], target)) return i;
        return -1;
    }

    public static void Save(string seed)
    {
        try
        {
            var path = GetFilePath(seed);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var data = new Dictionary<string, EffectRecordData>();
            foreach (var kv in s_records)
                data[kv.Key.ToString()] = new EffectRecordData { T = kv.Value.TemplateId, E = kv.Value.Effects };
            File.WriteAllText(path, JsonSerializer.Serialize(data, s_jsonOptions));
        }
        catch (Exception ex) { Log.Error($"CardEffect save: {ex.Message}"); }
    }

    public static void Load(string seed)
    {
        s_records.Clear();
        try
        {
            var path = GetFilePath(seed);
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, EffectRecordData>>(File.ReadAllText(path), s_jsonOptions);
            if (data != null)
                foreach (var kv in data)
                    if (int.TryParse(kv.Key, out int i) && kv.Value != null)
                        s_records[i] = new EffectRecord { TemplateId = kv.Value.T, Effects = kv.Value.E };
        }
        catch (Exception ex) { Log.Warn($"CardEffect load: {ex.Message}"); }
    }

    private class EffectRecordData { public string T { get; set; } = ""; public List<string> E { get; set; } = new(); }
}
