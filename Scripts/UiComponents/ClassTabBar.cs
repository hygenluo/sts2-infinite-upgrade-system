using System;
using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 技能分区二级标签页（UI设计.md 契约：通用/铁甲战士/静默猎手/储君/亡灵契约师/故障机器人）。
/// Phase 2 先落地结构与空态占位；技能条目按 SubCategory 落入对应标签（Phase 2.5/技能实现时）。
/// </summary>
public sealed partial class ClassTabBar : HBoxContainer
{
    public const string Generic = "通用";
    public const string Ironclad = "铁甲战士";
    public const string Silent = "静默猎手";
    public const string Regent = "储君";
    public const string Necrobinder = "亡灵契约师";
    public const string Defect = "故障机器人";

    public static readonly string[] Classes = { Generic, Ironclad, Silent, Regent, Necrobinder, Defect };

    private readonly ButtonGroup _group = new();
    private string? _selected;

    /// <summary>当前选中的职业（null 表示未选中）。</summary>
    public string? SelectedClass => _selected;

    /// <summary>标签切换事件。</summary>
    public event Action<string>? ClassSelected;

    public ClassTabBar()
    {
        AddThemeConstantOverride("separation", 4);
        foreach (var cls in Classes)
        {
            var btn = new Button
            {
                Text = cls,
                ToggleMode = true,
                ButtonGroup = _group,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(0, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            btn.AddThemeFontOverride("font", UpgradeTheme.Regular);
            btn.AddThemeFontSizeOverride("font_size", 13);
            btn.AddThemeColorOverride("font_color", UpgradeTheme.TextSecondary);
            btn.AddThemeColorOverride("font_pressed_color", UpgradeTheme.TitleGold);
            var pressedStyle = new StyleBoxFlat { BgColor = UpgradeTheme.PanelBgAlt, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
            btn.AddThemeStyleboxOverride("pressed", pressedStyle);
            btn.Pressed += () => Select(cls);
            AddChild(btn);
        }
    }

    /// <summary>选中指定职业标签。</summary>
    public void Select(string cls)
    {
        _selected = cls;
        foreach (var child in GetChildren())
        {
            if (child is Button b)
                b.ButtonPressed = b.Text == cls;
        }
        ClassSelected?.Invoke(cls);
    }
}
