using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 顶部栏（UI设计.md 契约：固定不随内容滚动）：
/// 拖拽手柄 + 标题 + 金色点数计数器 + 关闭按钮。
/// </summary>
public sealed partial class UpgradeTopBar : HBoxContainer
{
    private readonly Label _pointsLabel;

    /// <summary>点数计数器文本控件。</summary>
    public Label PointsLabel => _pointsLabel;

    /// <summary>标题栏区域（拖拽命中测试用）。</summary>
    public Control TitleArea { get; }

    /// <summary>关闭按钮（拖拽命中测试需排除其区域，否则点击被拖拽逻辑吞掉）。</summary>
    public Control CloseButton { get; }

    public UpgradeTopBar(string title, Action onClose)
    {
        AddThemeConstantOverride("separation", 10);

        // 拖拽手柄
        var handle = new Label { Text = "⋮⋮" };
        handle.AddThemeFontOverride("font", UpgradeTheme.Regular);
        handle.AddThemeFontSizeOverride("font_size", 18);
        handle.AddThemeColorOverride("font_color", UpgradeTheme.PanelBorder);
        AddChild(handle);

        // 标题
        var titleLabel = new Label
        {
            Text = title,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        titleLabel.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        titleLabel.AddThemeFontSizeOverride("font_size", 20);
        titleLabel.AddThemeColorOverride("font_color", UpgradeTheme.TitleGold);
        AddChild(titleLabel);

        // 点数计数器（宝石图标 + 金色数字）
        var gem = new Label { Text = "◆", VerticalAlignment = VerticalAlignment.Center };
        gem.AddThemeFontOverride("font", UpgradeTheme.Regular);
        gem.AddThemeFontSizeOverride("font_size", 14);
        gem.AddThemeColorOverride("font_color", UpgradeTheme.TitleGold);
        AddChild(gem);

        _pointsLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        _pointsLabel.AddThemeFontOverride("font", UpgradeTheme.SemiBold);
        _pointsLabel.AddThemeFontSizeOverride("font_size", 18);
        _pointsLabel.AddThemeColorOverride("font_color", UpgradeTheme.TitleGold);
        AddChild(_pointsLabel);

        // 关闭按钮
        var closeButton = new Button { Text = "✕", CustomMinimumSize = new Vector2(30, 0) };
        closeButton.AddThemeFontOverride("font", UpgradeTheme.Regular);
        closeButton.AddThemeFontSizeOverride("font_size", 15);
        closeButton.Pressed += () => onClose();
        AddChild(closeButton);

        TitleArea = this;
        CloseButton = closeButton;
    }

    /// <summary>更新点数显示。</summary>
    public void SetPoints(int points) => _pointsLabel.Text = points.ToString();
}
