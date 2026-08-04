using System;
using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 条目行（UI设计.md §4.2）：名称 + 当前值 + 成本小字 + 加号按钮。
/// 状态机（Refresh 按当前点数刷新）：
/// - Stat 无上限：等级数字，加号可重复购买
/// - Stat 限等级（MaxLevel&gt;0）：等级达上限 → MAX 徽标替代加号
/// - Action：无当前值（显示 —），加号可重复购买
/// - 点数不足：成本变红 + 加号置灰禁用
/// 所有文本统一应用主题字体（Phase 0.5 教训：防漏网节点）。
/// </summary>
public sealed partial class UpgradeItemRow : HBoxContainer
{
    private readonly UpgradeItemDef _def;
    private readonly RichTextLabel _nameLabel;
    private readonly Label _valueLabel;
    private readonly Label _costLabel;
    private readonly Button _plusButton;
    private readonly PanelContainer _maxBadge;
    private Tween? _shakeTween;

    public UpgradeItemDef Def => _def;

    /// <summary>购买成功事件（Phase 5：+1 飘字与点数跳动）。</summary>
    public event Action? Purchased;

    public UpgradeItemRow(UpgradeItemDef def)
    {
        _def = def;
        AddThemeConstantOverride("separation", 8);

        // 名称（占满剩余宽度）。Label 不支持 BBCode（Godot 4.5 官方文档确认），
        // 搜索命中高亮需要 RichTextLabel —— BbcodeEnabled + FitContent 与 Label 等宽等高。
        _nameLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = def.DisplayName,
        };
        _nameLabel.AddThemeFontOverride("normal_font", UpgradeTheme.Regular);
        _nameLabel.AddThemeFontSizeOverride("normal_font_size", 15);
        _nameLabel.AddThemeColorOverride("default_color", UpgradeTheme.TextMain);
        AddChild(_nameLabel);

        // 当前值
        _valueLabel = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(42, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _valueLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
        _valueLabel.AddThemeFontSizeOverride("font_size", 15);
        _valueLabel.AddThemeColorOverride("font_color", UpgradeTheme.TitleGold);
        AddChild(_valueLabel);

        // 成本小字
        _costLabel = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(24, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _costLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
        _costLabel.AddThemeFontSizeOverride("font_size", 13);
        _costLabel.AddThemeColorOverride("font_color", UpgradeTheme.CostColor);
        AddChild(_costLabel);

        // 加号按钮（Phase 5：点数不足时不用 Disabled —— 禁用态收不到点击，
        // 无法触发抖动提示；改为灰色半透明视觉 + 点击失败抖动）
        _plusButton = new Button
        {
            Text = "+",
            CustomMinimumSize = new Vector2(28, 26),
            TooltipText = def.Cost > 0 ? $"需要 {def.Cost} 点" : "",
        };
        _plusButton.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        _plusButton.AddThemeFontSizeOverride("font_size", 16);
        _plusButton.Pressed += async () =>
        {
            var ok = await def.OnClick();
            if (ok) Purchased?.Invoke();
            else Shake(); // 点数不足/操作失败 → 抖动提示
        };
        AddChild(_plusButton);

        // MAX 徽标（满级时替代加号）
        // 用 PanelContainer（panel 样式框是真实主题项）包 Label —— 之前把 StyleBoxFlat
        // 挂到 Label 的 "normal" 项上（Label 无此主题项），背景/文字颜色均不可靠，
        // 落成游戏默认近白文字 + 浅底 = 白底白字。
        _maxBadge = new PanelContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(30, 26),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var badgeStyle = new StyleBoxFlat
        {
            BgColor = new Color("b8b8b8"), // 中浅灰底（区别于面板与强调色）
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        _maxBadge.AddThemeStyleboxOverride("panel", badgeStyle);
        var badgeLabel = new Label
        {
            Text = "MAX",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeLabel.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        badgeLabel.AddThemeFontSizeOverride("font_size", 13);
        badgeLabel.AddThemeColorOverride("font_color", new Color("262019")); // 深字
        _maxBadge.AddChild(badgeLabel);
        badgeLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_maxBadge);

        // Phase 5：悬停提亮
        MouseEntered += () => Modulate = new Color(1.12f, 1.12f, 1.12f);
        MouseExited += () => Modulate = Colors.White;
    }

    /// <summary>
    /// 搜索命中高亮（Phase 3）：命中片段用亮金色 BBCode 标出；query 为空恢复原文。
    /// 仅在显示名包含查询词时生效（中英搜索词都可能命中 SearchText，但显示名只有一种语言）。
    /// </summary>
    public void SetSearchHighlight(string? query)
    {
        var text = _def.DisplayName;
        if (string.IsNullOrEmpty(query))
        {
            _nameLabel.Text = text; // Godot 4.3+ BBCode 恒开，无标签即原文
            return;
        }
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            _nameLabel.Text = text;
            return;
        }
        var matched = text.Substring(idx, query.Length);
        // 高亮用纯白：浅灰主题下比正文（#e8e2d4）更亮，保证可见
        _nameLabel.Text = $"{text[..idx]}[color=#ffffff]{matched}[/color]{text[(idx + query.Length)..]}";
    }

    /// <summary>按当前点数刷新状态：值文本 / 满级徽标 / 成本颜色 / 加号可用性。</summary>
    public void Refresh(int currentPoints)
    {
        var level = _def.LevelProvider?.Invoke() ?? 0;
        var isMaxed = _def.MaxLevel > 0 && level >= _def.MaxLevel;
        var affordable = currentPoints >= _def.Cost;

        if (_def.Kind == UpgradeItemKind.Action)
            _valueLabel.Text = "—";
        else if (_def.ValueText != null)
            _valueLabel.Text = _def.ValueText();
        else
            _valueLabel.Text = level.ToString();

        _maxBadge.Visible = isMaxed;
        _plusButton.Visible = !isMaxed;
        // 点数不足：加号半透明灰 + 成本变红（保持可点击以触发抖动反馈）
        _plusButton.Modulate = affordable ? Colors.White : new Color(1, 1, 1, 0.45f);
        _costLabel.Modulate = affordable ? Colors.White : UpgradeTheme.Danger;
        _costLabel.Text = _def.Cost > 0 ? _def.Cost.ToString() : "";
    }

    /// <summary>操作失败/点数不足的抖动提示（水平 ±4px 三次，Phase 5）。</summary>
    public void Shake()
    {
        _shakeTween?.Kill();
        var baseX = Position.X;
        _shakeTween = CreateTween();
        for (int i = 0; i < 3; i++)
        {
            _shakeTween.TweenProperty(this, "position:x", baseX + 4, 0.04f);
            _shakeTween.TweenProperty(this, "position:x", baseX - 4, 0.04f);
        }
        _shakeTween.TweenProperty(this, "position:x", baseX, 0.04f);
    }
}
