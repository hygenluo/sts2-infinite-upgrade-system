using System;
using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 可折叠分区（UI设计.md 契约：分区默认收起）。
///
/// 结构 = 分区头按钮 + 内容 VBox（直接子节点，无裁剪容器）。
/// 展开/收起 = 内容可见性切换（容器自动重排），附透明度淡入动画。
///
/// 历史教训（Phase 2 调试记录）：曾用「裁剪容器 + custom_minimum_size Tween +
/// 锚定内容」方案，日志证明逻辑层全部正确（行数/目标高度/节点在树）但视觉层
/// 无任何变化 —— min-size/Tween/锚点组合在模组运行环境下渲染不可靠，弃用。
/// 可见性切换 + modulate 淡入不依赖布局机制，绝对可靠。
/// </summary>
public sealed partial class CollapsibleSection : VBoxContainer
{
    private readonly Label _arrowLabel;
    private readonly Label _summaryLabel;
    private readonly VBoxContainer _content;
    private bool _collapsed;
    private Tween? _tween;

    /// <summary>分区内容容器（向其中添加条目行）。</summary>
    public VBoxContainer Content => _content;

    /// <summary>当前是否收起。</summary>
    public bool Collapsed => _collapsed;

    /// <summary>收起状态变化事件（true=收起）。</summary>
    public event Action<bool>? Toggled;

    public CollapsibleSection(string title, bool collapsed = true, bool weakStyle = false)
    {
        _collapsed = collapsed;

        // ── 分区头（扁平按钮，子节点提供视觉）──
        // 注意：Button 无文本时最小尺寸只按样式框计算（≈12px），子节点不参与。
        // 必须显式给高度，否则标题文字溢出按钮矩形、点击落在按钮外 → Pressed 不触发。
        var header = new Button
        {
            Flat = true,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 34),
        };
        header.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
        header.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
        header.Pressed += Toggle;

        var headerRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        headerRow.AddThemeConstantOverride("separation", 6);

        // 箭头固定 ▸ 字形，展开/收起用旋转 Tween（0° = 收起，90° = 展开）。
        // 注意：默认绕左上角旋转，90° 后字形被甩出可视区域（实测箭头消失）——
        // 必须把旋转轴心设为标签中心（尺寸确定后设置）。
        _arrowLabel = new Label { Text = "▸", VerticalAlignment = VerticalAlignment.Center };
        _arrowLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
        _arrowLabel.AddThemeFontSizeOverride("font_size", 14);
        _arrowLabel.AddThemeColorOverride("font_color", UpgradeTheme.PanelBorder);
        _arrowLabel.Resized += () => _arrowLabel.PivotOffset = _arrowLabel.Size * 0.5f;
        if (collapsed) _arrowLabel.RotationDegrees = 0;
        headerRow.AddChild(_arrowLabel);

        var titleLabel = new Label
        {
            Text = title,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLabel.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        titleLabel.AddThemeFontSizeOverride("font_size", weakStyle ? 14 : 17);
        titleLabel.AddThemeColorOverride("font_color", weakStyle ? UpgradeTheme.TextSecondary : UpgradeTheme.TitleGold);
        headerRow.AddChild(titleLabel);

        _summaryLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        _summaryLabel.AddThemeFontOverride("font", UpgradeTheme.Regular);
        _summaryLabel.AddThemeFontSizeOverride("font_size", 13);
        _summaryLabel.AddThemeColorOverride("font_color", UpgradeTheme.TextSecondary);
        headerRow.AddChild(_summaryLabel);

        header.AddChild(headerRow);
        headerRow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(header);

        // ── 内容区（直接子节点，可见性切换驱动容器重排）──
        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 4);
        AddChild(_content);

        if (collapsed) _content.Visible = false;
    }

    /// <summary>设置右侧摘要文本（如分类说明）。</summary>
    public void SetSummary(string text) => _summaryLabel.Text = text;

    /// <summary>展开/收起：内容可见性切换 + 透明度淡入（收起为瞬时）。</summary>
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        Toggled?.Invoke(collapsed);
        _tween?.Kill();
        _tween = null;
        // 箭头旋转动画（▸ 顺时针 90° = ▾）——独立 tween，避免与内容淡入互相覆盖
        var arrowTween = CreateTween();
        arrowTween.TweenProperty(_arrowLabel, "rotation_degrees", collapsed ? 0f : 90f, 0.15f);

        if (collapsed)
        {
            _content.Visible = false;
        }
        else
        {
            _content.Visible = true;
            _content.Modulate = new Color(1, 1, 1, 0);
            _tween = CreateTween();
            _tween.TweenProperty(_content, "modulate:a", 1f, 0.15f);
        }
    }

    private void Toggle() => SetCollapsed(!_collapsed);
}
