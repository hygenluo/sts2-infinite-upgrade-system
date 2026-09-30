using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 折叠分区（UI v3 排版）。
///
/// 结构 = 分区头（Flat 按钮 + 子 HBox：三角指示器 + 标题 + 右侧摘要）+ 内容 VBox。
/// 展开/收起 = 内容可见性切换（容器自动重排，渲染绝对可靠，无需裁剪容器/min-size Tween）
/// + 淡入动画 + 指示器方向切换。
///
/// 三角指示器为程序化 `_Draw` 绘制的实心三角（不依赖字体是否有 ▸/▾ 字形——
/// 英文语言用游戏 Kreon 字体，几何符号字形不保证存在，用文字符号会渲染成缺字方框）。
///
/// 历史教训（DEBUG.md）：
/// - Button 无文本时最小尺寸只按样式框算（≈12px）→ 必须显式给高度，否则点击命中不到。
/// - 旋转默认绕左上角 → 已改为直接切换三角方向，不依赖 PivotOffset。
/// </summary>
public sealed partial class CollapsibleSection : VBoxContainer
{
    private readonly Button _header;
    private readonly UiIcons.Chevron _chevron;
    private readonly MegaLabel _titleLabel;
    private readonly MegaLabel _summaryLabel;
    private readonly VBoxContainer _content;
    private bool _collapsed;
    private Tween? _tween;

    /// <summary>分区内容容器（向其中添加条目行）。</summary>
    public VBoxContainer Content => _content;

    /// <summary>当前是否收起。</summary>
    public bool Collapsed => _collapsed;

    /// <summary>标题控件（供外部调整，如测试分区弱化色）。</summary>
    public MegaLabel TitleLabel => _titleLabel;

    /// <summary>收起状态变化事件（true=收起）。</summary>
    public event Action<bool>? Toggled;

    public CollapsibleSection(string title, bool collapsed = true, bool weakStyle = false)
    {
        _collapsed = collapsed;
        AddThemeConstantOverride("separation", 4);

        // ── 分区头 ──
        _header = new Button
        {
            Flat = true,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 34),
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        _header.AddThemeStyleboxOverride("normal", UpgradeTheme.ButtonStylebox(flat: true));
        _header.AddThemeStyleboxOverride("hover", UpgradeTheme.RowStylebox(false, true));
        _header.AddThemeStyleboxOverride("pressed", UpgradeTheme.RowStylebox(false, true));
        _header.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        UpgradeTheme.ApplyGameFont(_header);
        _header.Pressed += Toggle;

        var headerRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        headerRow.AddThemeConstantOverride("separation", 8);

        _chevron = new UiIcons.Chevron
        {
            Expanded = !collapsed,
            IconColor = weakStyle ? UpgradeTheme.TextSecondary : UpgradeTheme.Gold,
        };
        headerRow.AddChild(_chevron);

        _titleLabel = UpgradeTheme.Label(title, weakStyle ? 14 : 15,
            weakStyle ? UpgradeTheme.TextSecondary : UpgradeTheme.Gold, bold: true);
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _titleLabel.MouseFilter = MouseFilterEnum.Ignore;
        headerRow.AddChild(_titleLabel);

        _summaryLabel = UpgradeTheme.Label("", 12, UpgradeTheme.TextSecondary,
            align: HorizontalAlignment.Right);
        _summaryLabel.MouseFilter = MouseFilterEnum.Ignore;
        headerRow.AddChild(_summaryLabel);

        _header.AddChild(headerRow);
        headerRow.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_header);

        // ── 内容区（直接子节点；可见性切换驱动容器重排）──
        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 3);
        AddChild(_content);

        if (collapsed) _content.Visible = false;
    }

    /// <summary>设置右侧摘要文本（如「8 项 · 已投入 24 点」）。</summary>
    public void SetSummary(string text) => _summaryLabel.Text = text;

    /// <summary>展开/收起：内容可见性切换 + 透明度淡入 + 三角方向切换。</summary>
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        Toggled?.Invoke(collapsed);
        _tween?.Kill();
        _tween = null;
        _chevron.Expanded = !collapsed;

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
