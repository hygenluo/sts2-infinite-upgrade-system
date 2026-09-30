using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 顶部栏（UI v3）：拖拽手柄 + 标题 + 点数徽标 + 关闭按钮，固定不随内容滚动。
///
/// 结构 = PanelContainer（顶栏底色 + 底部 1px 分隔线）
///   └ HBoxContainer [ DragGrip | Title(MegaLabel 自适应) | 点数徽标 | CloseButton ]
///
/// 顶栏内的图形（拖拽点、菱形、叉号）全部为 `UiIcons` 矢量绘制 —— 不依赖字体字形。
/// </summary>
public sealed partial class UpgradeTopBar : PanelContainer
{
    private readonly MegaLabel _pointsLabel;

    /// <summary>点数计数器文本控件（购买飘字/跳动动画的锚点）。</summary>
    public MegaLabel PointsLabel => _pointsLabel;

    /// <summary>标题栏区域（拖拽命中测试用）。</summary>
    public Control TitleArea { get; }

    /// <summary>关闭按钮（拖拽命中测试需排除其区域，否则点击被拖拽逻辑吞掉）。</summary>
    public Control CloseButton { get; }

    /// <summary>只读徽标（战斗中显示）。</summary>
    public Control ReadOnlyBadge { get; }

    private readonly MegaLabel _readOnlyLabel;

    public UpgradeTopBar(string title, Action onClose)
    {
        MouseFilter = MouseFilterEnum.Pass;

        // 顶栏底：比分面底色亮一档 + 底部 1px 分隔线 + 顶部圆角（贴合主面板 8px 圆角）
        var style = new StyleBoxFlat
        {
            BgColor = UpgradeTheme.PanelBgAlt,
            BorderColor = new Color(1f, 1f, 1f, 0.08f),
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            ContentMarginLeft = 12,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
        AddThemeStyleboxOverride("panel", style);

        var bar = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        bar.AddThemeConstantOverride("separation", 10);
        AddChild(bar);

        // 拖拽手柄
        var grip = new UiIcons.DragGrip { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        bar.AddChild(grip);

        // 标题（自适应字号：中英/长短标题都自动贴合）
        var titleLabel = UpgradeTheme.AutoLabel(title, 15, 20, UpgradeTheme.Gold, bold: true,
            align: HorizontalAlignment.Center);
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleLabel.MouseFilter = MouseFilterEnum.Ignore;
        bar.AddChild(titleLabel);

        // 只读徽标（局内查看）
        _readOnlyLabel = UpgradeTheme.Label(
            UpgradeLoc.Get(UpgradeLoc.UiReadOnlyBadge, "只读"), 11, UpgradeTheme.PanelBg, bold: true,
            align: HorizontalAlignment.Center);
        _readOnlyLabel.MouseFilter = MouseFilterEnum.Ignore;
        var badge = new PanelContainer
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        var badgeStyle = new StyleBoxFlat { BgColor = UpgradeTheme.TextSecondary };
        badgeStyle.SetCornerRadiusAll(4);
        badgeStyle.ContentMarginLeft = 7;
        badgeStyle.ContentMarginRight = 7;
        badgeStyle.ContentMarginTop = 1;
        badgeStyle.ContentMarginBottom = 1;
        badge.AddThemeStyleboxOverride("panel", badgeStyle);
        badge.AddChild(_readOnlyLabel);
        bar.AddChild(badge);
        ReadOnlyBadge = badge;

        // 点数徽标（金边 + 菱形 + 数字）
        var chip = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        var chipStyle = new StyleBoxFlat
        {
            BgColor = new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.12f),
            BorderColor = new Color(UpgradeTheme.Gold.R, UpgradeTheme.Gold.G, UpgradeTheme.Gold.B, 0.55f),
        };
        chipStyle.SetBorderWidthAll(1);
        chipStyle.SetCornerRadiusAll(5);
        chipStyle.ContentMarginLeft = 9;
        chipStyle.ContentMarginRight = 9;
        chipStyle.ContentMarginTop = 3;
        chipStyle.ContentMarginBottom = 3;
        chip.AddThemeStyleboxOverride("panel", chipStyle);
        var chipRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        chipRow.AddThemeConstantOverride("separation", 6);
        chipRow.AddChild(new UiIcons.Diamond { IconColor = UpgradeTheme.Gold, SizeFlagsVertical = SizeFlags.ShrinkCenter });
        _pointsLabel = UpgradeTheme.Label("0", 16, UpgradeTheme.Gold, bold: true);
        _pointsLabel.MouseFilter = MouseFilterEnum.Ignore;
        chipRow.AddChild(_pointsLabel);
        chip.AddChild(chipRow);
        bar.AddChild(chip);

        // 关闭按钮（叉号矢量绘制）
        var closeButton = new Button
        {
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(28, 28),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = "Esc",
        };
        closeButton.AddThemeStyleboxOverride("normal", UpgradeTheme.ButtonStylebox(flat: true));
        closeButton.AddThemeStyleboxOverride("hover", UpgradeTheme.RowStylebox(false, true));
        closeButton.AddThemeStyleboxOverride("pressed", UpgradeTheme.RowStylebox(false, true));
        closeButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        UpgradeTheme.ApplyGameFont(closeButton);
        closeButton.Pressed += onClose;
        var closeIcon = new UiIcons.Close();
        closeButton.AddChild(closeIcon);
        closeIcon.SetAnchorsPreset(LayoutPreset.FullRect);
        bar.AddChild(closeButton);

        TitleArea = bar;
        CloseButton = closeButton;
    }

    /// <summary>更新点数显示。</summary>
    public void SetPoints(int points) => _pointsLabel.Text = points.ToString();

    /// <summary>只读模式徽标显示/隐藏。</summary>
    public void SetReadOnly(bool readOnly) => ReadOnlyBadge.Visible = readOnly;
}
