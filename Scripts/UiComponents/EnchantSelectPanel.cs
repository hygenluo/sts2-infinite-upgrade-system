using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 附魔选择弹窗（UI v3）：选卡后列出该卡**当前可施加**的附魔（走原版 CanEnchant 门禁：
/// 卡类型 / 牌组 Unplayable / 一卡一附魔的槽位规则），点击选中；Esc / 取消按钮返回 null。
/// 全部文本使用游戏自带 MegaLabel（UpgradeTheme 工厂），配色与主面板一致。
/// </summary>
public sealed partial class EnchantSelectPanel : Control
{
    private static CanvasLayer? s_layer;

    private readonly TaskCompletionSource<string?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task<string?> Show(CardModel card)
    {
        var panel = new EnchantSelectPanel();
        s_layer = new CanvasLayer { Name = "EnchantSelectLayer", Layer = 129 };
        s_layer.AddChild(panel);
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root?.CallDeferred(Node.MethodName.AddChild, s_layer);
        panel.Build(card);
        return panel._tcs.Task;
    }

    private void Build(CardModel card)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // 拦截背景点击

        // 遮罩（与主面板一致）
        var backdrop = new ColorRect { Color = UpgradeTheme.Backdrop, MouseFilter = MouseFilterEnum.Stop };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(500, 520) };
        panel.AddThemeStyleboxOverride("panel", UpgradeTheme.PanelStylebox());
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        margin.AddChild(root);

        var current = CardOperationHelper.GetCardEnchantments(card);

        // 先算"这张牌现在能施加哪些附魔"，再决定要不要显示「一卡一附魔」提示 ——
        // 装了 Limitless「无上限附魔」时 CanEnchant 会被它放行（可以继续加别的），
        // 那时提示就是错的，所以只在"确实没有可选项"时才说这句话。
        var applicable = new List<(string Type, EnchantmentModel Canonical)>();
        foreach (var typeName in GetEnchantTypes())
        {
            if (!CardOperationHelper.CanEnchant(card, typeName)) continue;
            var canonical = CardOperationHelper.GetEnchantCanonical(typeName);
            if (canonical == null) continue;
            applicable.Add((typeName, canonical));
        }

        // 路线 A（原版语义，一卡一附魔）：标题不再显示 "x/y" 的槽位计数
        root.AddChild(UpgradeTheme.Label(
            UpgradeLoc.Get(UpgradeLoc.UiEnchantTitle, "附魔"),
            17, UpgradeTheme.Gold, bold: true));

        root.AddChild(UpgradeTheme.Label(card.Id.Entry, 12, UpgradeTheme.TextSecondary));

        if (current.Count > 0)
        {
            var curLabel = UpgradeTheme.Label(
                UpgradeLoc.Format(UpgradeLoc.UiEnchantCurrent, "当前：{0}",
                    string.Join("、", current.Select(e => $"{e.Type}×{e.Amount}"))),
                12, UpgradeTheme.TextSecondary);
            curLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            root.AddChild(curLabel);

            if (applicable.Count == 0)
            {
                var occupied = UpgradeTheme.Label(
                    UpgradeLoc.Get(UpgradeLoc.UiEnchantOccupied,
                        "原版规则：一张牌只能有一个附魔；要换附魔请先在回退页撤销"),
                    12, UpgradeTheme.Danger);
                occupied.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                root.AddChild(occupied);
            }
        }

        root.AddChild(UpgradeTheme.Divider());

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        foreach (var (typeName, canonical) in applicable)
        {
            string name = canonical.Title.GetFormattedText();
            bool already = current.Any(e => e.Type == typeName);
            var captured = typeName;

            var btn = UpgradeTheme.TextButton(
                already
                    ? UpgradeLoc.Format(UpgradeLoc.UiEnchantOwned, "{0}（叠加）", name)
                    : name,
                14, UpgradeTheme.TextMain, () => Resolve(captured),
                minHeight: 32);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.TooltipText = FormatEnchantTooltip(canonical);
            UpgradeTheme.StyleButton(btn);
            var label = UpgradeTheme.ButtonLabel(btn);
            if (label != null)
            {
                label.HorizontalAlignment = HorizontalAlignment.Left;
                label.MinFontSize = 10 + UpgradeTheme.FontBoost;
            }
            list.AddChild(btn);
        }

        if (applicable.Count == 0)
        {
            list.AddChild(UpgradeTheme.Label(
                UpgradeLoc.Get(UpgradeLoc.UiEnchantNone, "没有可用附魔"), 13, UpgradeTheme.TextSecondary));
        }

        root.AddChild(UpgradeTheme.Divider());

        var cancel = UpgradeTheme.TextButton(
            UpgradeLoc.Get(UpgradeLoc.UiCancel, "取消"), 14, UpgradeTheme.TextMain,
            () => Resolve(null), minHeight: 32);
        UpgradeTheme.StyleButton(cancel);
        root.AddChild(cancel);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
            Resolve(null);
    }

    private void Resolve(string? typeName)
    {
        if (_tcs.Task.IsCompleted) return;
        _tcs.TrySetResult(typeName);
        if (s_layer != null)
        {
            s_layer.QueueFree();
            s_layer = null;
        }
    }

    private static List<string> GetEnchantTypes()
    {
        var result = new List<string>();
        // 只枚举游戏主程序集 sts2（附魔类型都在其中），避免对 Steamworks.NET 等
        // 程序集调用 GetTypes()（其 OptionValue 类型不可加载 → ReflectionTypeLoadException）。
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "sts2");
        if (asm == null) return result;

        IEnumerable<Type> types;
        try
        {
            types = asm.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            // 个别类型不可加载——跳过不可加载项
            types = ex.Types.Where(t => t != null)!;
        }

        foreach (var t in types)
        {
            if (t.Namespace != "MegaCrit.Sts2.Core.Models.Enchantments") continue;
            if (!typeof(EnchantmentModel).IsAssignableFrom(t)) continue;
            if (t.IsAbstract || t.Name == "DeprecatedEnchantment") continue;
            result.Add(t.Name);
        }
        return result.Distinct().OrderBy(n => n).ToList();
    }

    /// <summary>格式化附魔描述：用 DynamicDescription（提供 {Block}/{Damage}/{Amount} 等变量来源）
    /// 再剥离 [gold] 等 BBCode 标记（Godot 按钮 tooltip 不渲染自定义 BBCode）。</summary>
    private static string FormatEnchantTooltip(EnchantmentModel canonical)
    {
        string text;
        try
        {
            // 用原版数值显示描述（canonical 的 Amount 为 0，直接格式化会显示 +0）
            var mutable = canonical.ToMutable();
            mutable.Amount = CardOperationHelper.GetOriginalAmount(canonical.GetType().Name);
            text = mutable.DynamicDescription.GetFormattedText();
        }
        catch
        {
            text = canonical.Description.GetFormattedText();
        }
        return StripBbcode(text);
    }

    private static string StripBbcode(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]", "");
}
