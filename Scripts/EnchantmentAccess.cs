using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem;

/// <summary>
/// 附魔槽位访问层：本模组只施加"真实附魔"（原版语义，一卡一附魔），但**别的模组**会把附魔包进
/// 包装附魔 —— 最典型的是 Limitless 的「无上限附魔」：
/// <c>Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment</c> 占住唯一槽位，
/// 真正的附魔挂在一张隐藏「存储卡」(<c>EnchantmentStorageCard</c>) 上，随包装附魔的
/// <c>[SavedProperty] List&lt;SerializableCard&gt;</c> 一起进存档/过网络。
///
/// 于是所有 <c>card.Enchantment is X</c> 的判断在装了 Limitless 的机器上全部失明 ——
/// 实机日志：<c>[IU] Refund enchant: STRIKE_IRONCLAD carries MultiEnchantment,
/// not the recorded RoyallyApproved</c>，回退直接失效。
///
/// 本类只做三件事：把槽位里的**真实附魔**拆出来（优先走上游公开契约
/// <c>static InnerEnchantments(EnchantmentModel)</c>，没有就按结构搜索）、列出槽位里到底有什么、
/// 以及从包装里精确摘掉一个附魔。不引用任何第三方程序集，缺哪个模组都自动退化成"直挂附魔"。
/// </summary>
public static class EnchantmentAccess
{
    /// <summary>上游公开契约：Limitless 的 <c>static IReadOnlyList&lt;EnchantmentModel&gt; InnerEnchantments(EnchantmentModel?)</c>。</summary>
    private const string InnerEnchantmentsApiName = "InnerEnchantments";

    /// <summary>Limitless 包装附魔的公开成员：内层附魔所在的"存储卡"列表。</summary>
    private const string EnchantmentsOnCardsMember = "EnchantmentsOnCards";

    /// <summary>包装附魔用来把内层附魔同步进 <c>[SavedProperty]</c> 镜像的公开方法。</summary>
    private const string SetSerializableCardsMethod = "SetSerializableCards";

    private const int MaxDepth = 3;
    private const int MaxNodes = 256;

    /// <summary>槽位里的真实附魔（不含包装本身）。直挂附魔 → 只返回它自己。</summary>
    public static IReadOnlyList<EnchantmentModel> Inner(EnchantmentModel? root)
    {
        if (root == null) return Array.Empty<EnchantmentModel>();

        var fromApi = TryInnerEnchantmentsApi(root);
        if (fromApi != null) return fromApi;

        var fromKnownMember = TryKnownWrapperMember(root);
        if (fromKnownMember != null) return fromKnownMember;

        var found = new List<EnchantmentModel>();
        int budget = MaxNodes;
        Walk(root, 1, new HashSet<object>(ReferenceEqualityComparer.Instance), root, found, ref budget);
        return found;
    }

    /// <summary>在槽位里找指定类型的附魔实例（包装会展开）。直挂同类型 → 返回它自己。</summary>
    public static EnchantmentModel? Find(EnchantmentModel? root, string typeName)
    {
        if (root == null || string.IsNullOrEmpty(typeName)) return null;
        if (root.GetType().Name == typeName) return root;
        foreach (var inner in Inner(root))
            if (inner.GetType().Name == typeName) return inner;
        return null;
    }

    /// <summary>槽位里到底有哪些附魔（类型名, 层数）。包装会展开成多条；空槽位返回空表。</summary>
    public static List<(string Type, int Amount)> Describe(EnchantmentModel? root)
    {
        var result = new List<(string, int)>();
        if (root == null) return result;

        var inner = Inner(root);
        if (inner.Count == 0)
        {
            result.Add((root.GetType().Name, root.Amount));
            return result;
        }
        foreach (var e in inner)
            result.Add((e.GetType().Name, e.Amount));
        return result;
    }

    /// <summary>卡上是否挂着指定类型的附魔（含包装内）。</summary>
    public static bool Has(EnchantmentModel? root, string typeName) => Find(root, typeName) != null;

    /// <summary>
    /// 从槽位移除指定类型的附魔：
    /// - 直挂：同种可叠加的多层每次只扣一份原版数值，剩一份时清槽位；
    /// - 包装内：先把那一个内层附魔摘掉并同步包装的序列化镜像，摘空之后整槽清掉。
    /// 幂等、确定性（两端看到的是同一份包装列表，按类型名定位 → 索引一致）。
    /// </summary>
    public static bool Remove(CardModel card, string typeName, out string? failureReason)
    {
        failureReason = null;
        var root = card?.Enchantment;
        if (root == null)
        {
            failureReason = "card has no enchantment";
            return false;
        }
        if (!card!.IsMutable)
        {
            failureReason = "card is immutable";
            return false;
        }

        // ① 直挂
        if (root.GetType().Name == typeName)
        {
            int original = CardOperationHelper.GetOriginalAmount(typeName);
            if (root.Amount > original)
            {
                root.Amount -= original;
                root.RecalculateValues();
            }
            else
            {
                card.ClearEnchantmentInternal();
            }
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
            return true;
        }

        // ② 包装内
        var inner = Inner(root);
        EnchantmentModel? target = null;
        foreach (var e in inner)
        {
            if (e.GetType().Name == typeName) { target = e; break; }
        }
        if (target == null)
        {
            failureReason = $"carries {root.GetType().Name} without {typeName}";
            return false;
        }

        // 只剩这一个 → 整槽清掉（留个空包装只会占住槽位）
        if (inner.Count <= 1)
        {
            card.ClearEnchantmentInternal();
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
            return true;
        }

        if (!TryRemoveInner(root, typeName))
        {
            failureReason = $"cannot detach {typeName} from wrapper {root.GetType().Name}";
            return false;
        }
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
        return true;
    }

    // ── 上游契约 ───────────────────────────────────────────────

    private static IReadOnlyList<EnchantmentModel>? TryInnerEnchantmentsApi(EnchantmentModel root)
    {
        for (Type? current = root.GetType(); current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (var method in SafeGetMethods(current))
            {
                if (!method.IsStatic || method.Name != InnerEnchantmentsApiName) continue;
                var ps = method.GetParameters();
                if (ps.Length != 1 || !ps[0].ParameterType.IsAssignableFrom(typeof(EnchantmentModel))) continue;
                try
                {
                    if (method.Invoke(null, new object?[] { root }) is not IEnumerable enumerable) return null;
                    var list = new List<EnchantmentModel>();
                    foreach (var item in enumerable)
                    {
                        if (item is EnchantmentModel e && !ReferenceEquals(e, root)) list.Add(e);
                    }
                    return list;
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>Limitless 的包装附魔公开 <c>EnchantmentsOnCards</c>（List&lt;CardModel&gt; 存储卡）。</summary>
    private static IReadOnlyList<EnchantmentModel>? TryKnownWrapperMember(EnchantmentModel root)
    {
        var value = TryGetMemberValue(root, EnchantmentsOnCardsMember);
        if (value is not IEnumerable enumerable) return null;

        var list = new List<EnchantmentModel>();
        try
        {
            foreach (var item in enumerable)
            {
                if (item is EnchantmentModel e && !ReferenceEquals(e, root)) list.Add(e);
                else if (item is CardModel card && card.Enchantment != null) list.Add(card.Enchantment);
            }
        }
        catch
        {
            return null;
        }
        return list;
    }

    // ── 结构式兜底（其它多附魔模组） ───────────────────────────

    private static void Walk(object? node, int depth, HashSet<object> visited, EnchantmentModel root,
        List<EnchantmentModel> found, ref int budget)
    {
        if (node == null || depth > MaxDepth || budget <= 0) return;
        if (node is string) return;
        if (!visited.Add(node)) return;
        budget--;

        if (node is CardModel card)
        {
            Walk(card.Enchantment, depth + 1, visited, root, found, ref budget);
            return;
        }

        if (node is EnchantmentModel enchantment && !ReferenceEquals(enchantment, root))
            found.Add(enchantment);

        if (node is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is EnchantmentModel || item is CardModel || item is IEnumerable)
                    Walk(item, depth + 1, visited, root, found, ref budget);
            }
            return;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo[] fields;
        PropertyInfo[] properties;
        try
        {
            var type = node.GetType();
            fields = type.GetFields(flags);
            properties = type.GetProperties(flags);
        }
        catch
        {
            return;
        }

        foreach (var field in fields)
        {
            if (!IsInteresting(field.FieldType)) continue;
            try { Walk(field.GetValue(node), depth + 1, visited, root, found, ref budget); } catch { }
        }
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod == null) continue;
            if (!IsInteresting(property.PropertyType)) continue;
            try { Walk(property.GetValue(node), depth + 1, visited, root, found, ref budget); } catch { }
        }
    }

    private static bool IsInteresting(Type type)
        => typeof(EnchantmentModel).IsAssignableFrom(type)
           || typeof(CardModel).IsAssignableFrom(type)
           || typeof(IEnumerable).IsAssignableFrom(type);

    // ── 从包装里摘掉一个 ───────────────────────────────────────

    private static bool TryRemoveInner(EnchantmentModel root, string typeName)
    {
        // ① Limitless：EnchantmentsOnCards（List<CardModel> 存储卡）+ SetSerializableCards 同步镜像
        if (TryGetMemberValue(root, EnchantmentsOnCardsMember) is IList<CardModel> cards)
        {
            int index = IndexOfStorageCard(cards, typeName);
            if (index >= 0)
            {
                var removed = cards[index];
                cards.RemoveAt(index);
                removed?.ClearEnchantmentInternal();
                if (cards.Count == 0)
                {
                    // 调用方会负责整槽清掉；这里先把镜像同步成空表，避免残留
                    TrySetSerializableCards(root, cards);
                }
                else
                {
                    TrySetSerializableCards(root, cards);
                }
                return true;
            }
        }

        // ② 其它多附魔模组：直接持有 List<EnchantmentModel>
        foreach (var value in EnumerateMemberValues(root))
        {
            if (value is IList<EnchantmentModel> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i]?.GetType().Name != typeName) continue;
                    list.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }

    private static int IndexOfStorageCard(IList<CardModel> cards, string typeName)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var enchantment = cards[i]?.Enchantment;
            if (enchantment != null && enchantment.GetType().Name == typeName) return i;
        }
        return -1;
    }

    private static void TrySetSerializableCards(EnchantmentModel root, IList<CardModel> cards)
    {
        try
        {
            // 包装附魔内部持有的是 List<CardModel>（活引用），尽量原样传回去以保持镜像一致
            var list = cards as List<CardModel> ?? new List<CardModel>(cards);
            var method = root.GetType().GetMethod(SetSerializableCardsMethod,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var parameters = method?.GetParameters();
            if (method == null || parameters == null || parameters.Length != 1) return;
            if (!parameters[0].ParameterType.IsInstanceOfType(list)) return;
            method.Invoke(root, new object?[] { list });
        }
        catch
        {
            // 镜像没同步上也不致命：下一次 RecalculateValues/OnEnchant 会再同步一次
        }
    }

    // ── 反射小工具 ─────────────────────────────────────────────

    private static object? TryGetMemberValue(object instance, string memberName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var type = instance.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0 && property.GetMethod != null)
                return property.GetValue(instance);
            var field = type.GetField(memberName, flags);
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object?> EnumerateMemberValues(object instance)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type;
        try { type = instance.GetType(); } catch { yield break; }

        FieldInfo[] fields;
        PropertyInfo[] properties;
        try
        {
            fields = type.GetFields(flags);
            properties = type.GetProperties(flags);
        }
        catch { yield break; }

        foreach (var field in fields)
        {
            if (!IsInteresting(field.FieldType)) continue;
            object? value = null;
            try { value = field.GetValue(instance); } catch { }
            if (value != null) yield return value;
        }
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod == null) continue;
            if (!IsInteresting(property.PropertyType)) continue;
            object? value = null;
            try { value = property.GetValue(instance); } catch { }
            if (value != null) yield return value;
        }
    }

    private static IEnumerable<MethodInfo> SafeGetMethods(Type type)
    {
        try
        {
            return type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public
                                   | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch
        {
            return Array.Empty<MethodInfo>();
        }
    }
}
