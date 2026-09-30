using System;
using System.Collections.Generic;
using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 技能分区二级标签页（UI v3）：通用 / 铁甲战士 / 静默猎手 / 储君 / 亡灵契约师 / 故障机器人。
///
/// 标签按钮 = `UpgradeTheme.TextButton`（Flat 按钮 + 子 MegaLabel），
/// 选中态用金色描边 + 金字，未选中为次级色，悬停提亮。
/// 标签文字可显示该职业已拥有 / 总数（`SetCount`），便于快速定位已购技能。
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

    private readonly Dictionary<string, Button> _tabOf = new();
    private readonly Dictionary<string, string> _baseTextOf = new();
    private readonly Dictionary<string, (int Owned, int Total)> _countOf = new();
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
            var captured = cls;
            var baseText = UpgradeLoc.ClassTitle(cls);
            _baseTextOf[cls] = baseText;
            var btn = UpgradeTheme.TextButton(baseText, 13, UpgradeTheme.TextSecondary,
                () => Select(captured), minHeight: 28, minWidth: 0);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _tabOf[cls] = btn;
            AddChild(btn);
        }
        RefreshStyles();
    }

    /// <summary>选中指定职业标签。</summary>
    public void Select(string cls)
    {
        _selected = cls;
        RefreshStyles();
        ClassSelected?.Invoke(cls);
    }

    /// <summary>设置某职业的「已拥有 / 总数」计数（显示在标签文字后）。</summary>
    public void SetCount(string cls, int owned, int total)
    {
        _countOf[cls] = (owned, total);
        if (!_tabOf.TryGetValue(cls, out var btn)) return;
        var label = UpgradeTheme.ButtonLabel(btn);
        if (label == null) return;
        var text = total > 0 ? $"{_baseTextOf[cls]} {owned}/{total}" : _baseTextOf[cls];
        label.Text = text;
    }

    private void RefreshStyles()
    {
        foreach (var (cls, btn) in _tabOf)
        {
            var selected = cls == _selected;
            var style = selected
                ? TabStyle(selected: true, hovered: false)
                : TabStyle(selected: false, hovered: false);
            btn.AddThemeStyleboxOverride("normal", style);
            btn.AddThemeStyleboxOverride("hover", TabStyle(selected, hovered: true));
            btn.AddThemeStyleboxOverride("pressed", style);
            var label = UpgradeTheme.ButtonLabel(btn);
            if (label != null)
            {
                label.AddThemeColorOverride("font_color",
                    selected ? UpgradeTheme.HoverGlow : UpgradeTheme.TextSecondary);
                label.MinFontSize = 9; // 6 个标签挤一行：允许缩到 9px 保证中文不裁切
            }
        }
    }

    private static StyleBoxFlat TabStyle(bool selected, bool hovered)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = selected
                ? new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.18f)
                : (hovered ? UpgradeTheme.RowHover : new Color(0f, 0f, 0f, 0f)),
            BorderColor = selected
                ? new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.85f)
                : new Color(1f, 1f, 1f, hovered ? 0.12f : 0.06f),
        };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(4);
        sb.ContentMarginLeft = 6;
        sb.ContentMarginRight = 6;
        sb.ContentMarginTop = 2;
        sb.ContentMarginBottom = 2;
        return sb;
    }
}
