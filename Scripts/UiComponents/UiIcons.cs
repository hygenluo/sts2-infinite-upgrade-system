using Godot;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 程序化绘制的小图标（UI v3）。
///
/// 为什么不用字符/符号（▸ ▾ ◆ ✕ → ⋮⋮）：
/// 非 CJK 语言下 UI 使用游戏主字体 Kreon —— 它只有拉丁字形，U+25xx（几何符号）、
/// U+2715（✕）、U+22EE（⋮）等一律缺失，Godot 动态字体无自动回退时会渲染成缺字方框
/// （中文语言替换成 Noto Sans Mono CJK SC 才有这些字形 —— 于是又变成"中英表现不一致"）。
/// 因此所有装饰性符号统一改为 `_Draw` 矢量绘制：任意语言、任意分辨率都一致且清晰。
/// </summary>
public static partial class UiIcons
{
    /// <summary>实心三角（分区折叠指示器）：收起指向右，展开指向下。</summary>
    public sealed partial class Chevron : Control
    {
        private bool _expanded;

        public Color IconColor { get; set; } = UpgradeTheme.Gold;

        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                QueueRedraw();
            }
        }

        public Chevron()
        {
            CustomMinimumSize = new Vector2(12, 12);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var s = Size;
            if (s.X <= 0 || s.Y <= 0) return;
            Vector2[] points = _expanded
                ? [
                    new Vector2(s.X * 0.16f, s.Y * 0.30f),
                    new Vector2(s.X * 0.84f, s.Y * 0.30f),
                    new Vector2(s.X * 0.50f, s.Y * 0.74f),
                  ]
                : [
                    new Vector2(s.X * 0.30f, s.Y * 0.16f),
                    new Vector2(s.X * 0.74f, s.Y * 0.50f),
                    new Vector2(s.X * 0.30f, s.Y * 0.84f),
                  ];
            DrawColoredPolygon(points, IconColor);
        }
    }

    /// <summary>拖拽手柄（两列三点的凸点纹理）。</summary>
    public sealed partial class DragGrip : Control
    {
        public Color IconColor { get; set; } = UpgradeTheme.PanelBorder;

        public DragGrip()
        {
            CustomMinimumSize = new Vector2(12, 18);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var s = Size;
            if (s.X <= 0 || s.Y <= 0) return;
            float r = Mathf.Max(1f, s.X * 0.10f);
            for (int col = 0; col < 2; col++)
            {
                for (int row = 0; row < 3; row++)
                {
                    var p = new Vector2(
                        s.X * (col == 0 ? 0.30f : 0.70f),
                        s.Y * (0.22f + row * 0.28f));
                    DrawCircle(p, r, IconColor);
                }
            }
        }
    }

    /// <summary>菱形（点数货币图标）。</summary>
    public sealed partial class Diamond : Control
    {
        public Color IconColor { get; set; } = UpgradeTheme.Gold;

        public Diamond()
        {
            CustomMinimumSize = new Vector2(10, 10);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var s = Size;
            if (s.X <= 0 || s.Y <= 0) return;
            DrawColoredPolygon(
            [
                new Vector2(s.X * 0.5f, 0f),
                new Vector2(s.X, s.Y * 0.5f),
                new Vector2(s.X * 0.5f, s.Y),
                new Vector2(0f, s.Y * 0.5f),
            ], IconColor);
        }
    }

    /// <summary>关闭叉号（两条抗锯齿线）。</summary>
    public sealed partial class Close : Control
    {
        public Color IconColor { get; set; } = UpgradeTheme.TextMain;

        public Close()
        {
            CustomMinimumSize = new Vector2(12, 12);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var s = Size;
            if (s.X <= 0 || s.Y <= 0) return;
            float m = Mathf.Min(s.X, s.Y) * 0.24f;
            float w = Mathf.Max(1.5f, Mathf.Min(s.X, s.Y) * 0.11f);
            DrawLine(new Vector2(m, m), new Vector2(s.X - m, s.Y - m), IconColor, w, true);
            DrawLine(new Vector2(s.X - m, m), new Vector2(m, s.Y - m), IconColor, w, true);
        }
    }

    /// <summary>右向箭头（进入子面板）。</summary>
    public sealed partial class ArrowRight : Control
    {
        public Color IconColor { get; set; } = UpgradeTheme.Gold;

        public ArrowRight()
        {
            CustomMinimumSize = new Vector2(14, 14);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var s = Size;
            if (s.X <= 0 || s.Y <= 0) return;
            float w = Mathf.Max(1.5f, Mathf.Min(s.X, s.Y) * 0.12f);
            DrawLine(new Vector2(s.X * 0.18f, s.Y * 0.18f), new Vector2(s.X * 0.70f, s.Y * 0.5f), IconColor, w, true);
            DrawLine(new Vector2(s.X * 0.70f, s.Y * 0.5f), new Vector2(s.X * 0.18f, s.Y * 0.82f), IconColor, w, true);
        }
    }
}
