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
    private readonly Label _valueLabel;
    private readonly Label _costLabel;
    private readonly Button _plusButton;
    private readonly Label _maxBadge;

    public UpgradeItemDef Def => _def;

    public UpgradeItemRow(UpgradeItemDef def)
    {
        _def = def;
        AddThemeConstantOverride("separation", 8);

        // 名称（占满剩余宽度；不用 TrimEllipsis —— CJK + 0 宽分配时会整段消失，
        // 宁可溢出也不隐藏，待 Phase 3 宽度探针确认后决定是否恢复）
        var nameLabel = new Label
        {
            Text = def.DisplayName,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
        nameLabel.AddThemeFontSizeOverride("font_size", 15);
        nameLabel.AddThemeColorOverride("font_color", UpgradeTheme.TextMain);
        AddChild(nameLabel);

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

        // 加号按钮
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
            await def.OnClick();
            // 行状态与点数由调用方统一刷新（OnClick 内部已刷新点数）
        };
        AddChild(_plusButton);

        // MAX 徽标（满级时替代加号）
        _maxBadge = new Label
        {
            Text = "MAX",
            Visible = false,
            CustomMinimumSize = new Vector2(28, 26),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _maxBadge.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        _maxBadge.AddThemeFontSizeOverride("font_size", 13);
        _maxBadge.AddThemeColorOverride("font_color", new Color(0x26, 0x20, 0x19)); // 深字
        var badgeStyle = new StyleBoxFlat { BgColor = UpgradeTheme.TitleGold, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
        _maxBadge.AddThemeStyleboxOverride("normal", badgeStyle);
        AddChild(_maxBadge);
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
        _plusButton.Disabled = !affordable;
        _costLabel.Modulate = affordable ? Colors.White : UpgradeTheme.Danger;
        _costLabel.Text = _def.Cost > 0 ? _def.Cost.ToString() : "";
    }
}
