using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using InfiniteUpgradeSystem.UiComponents;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 附魔选择弹窗（update05 卡牌附魔）：选卡后列出该卡兼容的附魔（类型门禁 + 上限），
/// 点击选中；Esc / 取消按钮返回 null。
/// 复用 UpgradeTheme 主题与模组 CanvasLayer 层级。
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

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Stop };
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(480, 520) };
        panel.AddThemeStyleboxOverride("panel", UpgradeTheme.BuildPanelStylebox());
        center.AddChild(panel);

        var root = new VBoxContainer();
        panel.AddChild(root);

        var current = CardOperationHelper.GetCardEnchantments(card);
        var limit = CardOperationHelper.GetEnchantLimit(card);
        var title = new Label
        {
            Text = $"{card.Id.Entry} · 附魔 {current.Count}/{limit}",
            Modulate = UpgradeTheme.TitleGold,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        title.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        title.AddThemeFontSizeOverride("font_size", 18);
        root.AddChild(title);

        if (current.Count > 0)
        {
            var curLabel = new Label
            {
                Text = "当前：" + string.Join("、", current.Select(e => $"{e.Type}×{e.Amount}")),
                Modulate = UpgradeTheme.TextSecondary,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            curLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
            curLabel.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(curLabel);
        }
        if (current.Count >= limit)
        {
            var fullLabel = new Label
            {
                Text = "已达上限，选新附魔将替换最早的那个",
                Modulate = UpgradeTheme.Danger
            };
            fullLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
            fullLabel.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(fullLabel);
        }

        root.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(list);

        foreach (var typeName in GetEnchantTypes())
        {
            if (!CardOperationHelper.CanEnchant(card, typeName)) continue;
            var canonical = CardOperationHelper.GetEnchantCanonical(typeName);
            if (canonical == null) continue;
            string name = canonical.Title.GetFormattedText();
            bool already = current.Any(e => e.Type == typeName);
            var btn = new Button
            {
                Text = already ? $"{name}（已有，+1层）" : name,
                Alignment = HorizontalAlignment.Left,
                TooltipText = canonical.Description.GetFormattedText()
            };
            btn.AddThemeFontOverride("font", UpgradeTheme.Regular);
            btn.AddThemeFontSizeOverride("font_size", 14);
            btn.Pressed += () => Resolve(typeName);
            list.AddChild(btn);
        }

        if (list.GetChildCount() == 0)
        {
            var empty = new Label { Text = "没有可用附魔", Modulate = UpgradeTheme.TextSecondary };
            empty.AddThemeFontOverride("font", UpgradeTheme.Regular);
            empty.AddThemeFontSizeOverride("font_size", 14);
            list.AddChild(empty);
        }

        root.AddChild(new HSeparator());

        var cancel = new Button { Text = "取消" };
        cancel.AddThemeFontOverride("font", UpgradeTheme.Regular);
        cancel.AddThemeFontSizeOverride("font_size", 14);
        cancel.Pressed += () => Resolve(null);
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
}
