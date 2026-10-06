using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem.UiComponents;

/// <summary>
/// 可移动悬浮窗（HUD 小圆钮）—— 无限升级系统的常驻入口。
///
/// 行为契约（UI设计.md §0.7 / DEBUG.md「悬浮窗」）：
/// 1. **只在本局内出现**：`RunManager.DebugOnlyGetState() == null`（主菜单 / 读档界面）时
///    不绘制、不参与输入（节点常驻，用 <see cref="_active"/> 开关，避免依赖「隐藏节点的
///    _Process 是否继续跑」这一不确定行为）。
/// 2. **左键点击 → 打开 / 收起加点面板**（走 `UpgradeUIHandler.ToggleFromFloatingButton`，
///    选牌进行中该方法会直接忽略，不会打断选牌取消链路）。
/// 3. **按住拖拽 → 移动**：位移超过 <see cref="DragThreshold"/> 才算拖拽，否则视为点击
///    （避免手抖把「点击」判成「拖拽」导致点不开面板）。松手后位置被记忆并 Clamp 在屏幕内。
/// 4. **显示当前点数**，跟随 `UpgradePointManager.CurrentPoints`。
///
/// 渲染全部走 `_Draw` 矢量绘制（圆角胶囊 + 金环 + 菱形 + 数字），**不新建任何文本节点**：
/// 文本节点必须经 `UpgradeTheme` 工厂创建（`MegaLabel._Ready` 会断言 font 主题覆盖存在），
/// 而这里用 `DrawString` + 同一套游戏字体（`UpgradeTheme.FontBold`，含语言替换）画数字，
/// 绕开该约束且不引入节点开销。图形语言与 `UiIcons.DiamondIcon` / 顶栏点数徽标保持一致。
/// </summary>
public sealed partial class UpgradeFloatingButton : Control
{
    // ═══════════════════════════════════════════════════════════════
    // 常量
    // ═══════════════════════════════════════════════════════════════

    /// <summary>胶囊宽（竖排：上菱形图标、下点数）。</summary>
    private const float PillWidth = 56f;

    /// <summary>胶囊高。</summary>
    private const float PillHeight = 84f;

    /// <summary>距屏幕边缘的默认留白。</summary>
    private const float EdgeMargin = 10f;

    /// <summary>按下后位移超过该像素值才判定为拖拽（否则算点击）。</summary>
    private const float DragThreshold = 5f;

    /// <summary>非悬停时的整体透明度（降低对游戏画面的干扰）。</summary>
    private const float IdleAlpha = 0.62f;

    /// <summary>CanvasLayer 层级：低于主面板（128），高于游戏常规 UI。</summary>
    private const int LayerIndex = 127;

    // ═══════════════════════════════════════════════════════════════
    // 静态实例
    // ═══════════════════════════════════════════════════════════════

    private static UpgradeFloatingButton? s_instance;
    private static CanvasLayer? s_canvasLayer;

    /// <summary>悬浮窗实例（未创建时为 null）。</summary>
    public static UpgradeFloatingButton? Instance => s_instance;

    /// <summary>悬浮窗所在 CanvasLayer（外部需要挂同级节点时可取）。</summary>
    public static CanvasLayer? Layer => s_canvasLayer;

    /// <summary>位置记忆（运行期间跨局保持；NaN = 尚未拖拽 → 用右侧中部默认位）。</summary>
    private static Vector2 s_position = new(float.NaN, float.NaN);

    /// <summary>
    /// 创建悬浮窗并挂到场景树（在 `Entry.Init` 中调用，早于游戏本地化就绪 ——
    /// 因此所有文本都在首次进入局内时再解析，见 <see cref="RefreshTooltip"/>）。
    /// </summary>
    public static void CreateInstance()
    {
        if (s_instance != null) return;

        s_instance = new UpgradeFloatingButton
        {
            Name = "InfiniteUpgradeFloatingButton",
            CustomMinimumSize = new Vector2(PillWidth, PillHeight),
            Size = new Vector2(PillWidth, PillHeight),
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            FocusMode = FocusModeEnum.None,
            Modulate = new Color(1f, 1f, 1f, IdleAlpha),
            Active = false,
        };
        s_canvasLayer = new CanvasLayer { Name = "InfiniteUpgradeFloatingLayer", Layer = LayerIndex };
        s_canvasLayer.AddChild(s_instance);

        var tree = (SceneTree?)Engine.GetMainLoop();
        tree?.Root?.CallDeferred(Node.MethodName.AddChild, s_canvasLayer);

        Log.Info("InfiniteUpgradeFloatingButton: created.");
    }

    // ═══════════════════════════════════════════════════════════════
    // 运行时状态
    // ═══════════════════════════════════════════════════════════════

    /// <summary>是否处于「应该出现」的状态（局内）。false 时既不绘制也不吃输入。</summary>
    private bool _active;

    /// <summary>鼠标是否悬停在胶囊上（悬停时提高不透明度并高亮描边）。</summary>
    private bool _hover;

    /// <summary>左键是否按住（用于绘制按下态）。</summary>
    private bool _pressing;

    /// <summary>本次按下是否已越过拖拽阈值（决定松手时是「移动」还是「点击」）。</summary>
    private bool _draggedOverThreshold;

    /// <summary>按下瞬间的鼠标全局坐标（阈值判定基准）。</summary>
    private Vector2 _pressGlobalPos;

    /// <summary>拖拽时鼠标与胶囊左上角的偏移（保证拖拽不跳位）。</summary>
    private Vector2 _dragOffset;

    /// <summary>已绘制的点数（变化时重绘）。</summary>
    private int _drawnPoints = int.MinValue;

    /// <summary>上一次布局用的视口尺寸（分辨率变化时重新定位）。</summary>
    private Vector2 _lastViewportSize = Vector2.Zero;

    /// <summary>工具提示已解析时所用的语言（语言切换后重新解析）。</summary>
    private string _tooltipLocale = "";

    /// <summary>公开给同程序集的可见性开关（步骤 2 的配置项由此驱动）。</summary>
    internal bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            // 关键：不可见时必须改成 Ignore，否则这 56×84 的矩形会在主菜单里
            // 继续吃掉 GUI 鼠标事件（看不见但点不到下面东西）。
            MouseFilter = value ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
            if (!value)
            {
                // 退出局内时把交互状态一并复位，避免残留拖拽把节点拽到屏幕外
                _hover = false;
                _pressing = false;
                _draggedOverThreshold = false;
                TooltipText = "";
            }
            QueueRedraw();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 生命周期
    // ═══════════════════════════════════════════════════════════════

    public override void _Process(double delta)
    {
        // 局内判定：RunState 消失（回主菜单）→ 收起来
        Active = RunManager.Instance?.DebugOnlyGetState() != null;

        var vpSize = GetViewportRect().Size;
        if (vpSize != _lastViewportSize)
        {
            _lastViewportSize = vpSize;
            ApplyPosition(vpSize);
        }

        if (!_active) return;

        // 点数变化 → 重绘
        int points = UpgradePointManager.CurrentPoints;
        if (points != _drawnPoints)
        {
            _drawnPoints = points;
            QueueRedraw();
        }

        // 语言可能在建节点之后才就绪（Entry.Init 早于 LocManager.Initialize）
        RefreshTooltip();
    }

    /// <summary>首次进入局内（此时游戏语言与字体一定已就绪）时解析工具提示。</summary>
    private void RefreshTooltip()
    {
        var locale = OS.GetLocale();
        if (locale == _tooltipLocale) return;
        _tooltipLocale = locale;
        TooltipText = UpgradeLoc.Get(UpgradeLoc.UiFloatingTooltip,
            "左键打开无限升级系统 · 拖拽移动");
    }

    // ═══════════════════════════════════════════════════════════════
    // 输入（手动命中测试；不实现 _GuiInput，见类注释）
    // ═══════════════════════════════════════════════════════════════

    public override void _Input(InputEvent @event)
    {
        if (!_active)
        {
            // 退出局内时若仍处于拖拽，复位状态
            _pressing = false;
            _draggedOverThreshold = false;
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                HandleMouseButton(mb);
                break;

            case InputEventMouseMotion:
                HandleMouseMotion();
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        var mouse = GetGlobalMousePosition();

        if (mb.Pressed)
        {
            if (!HitTest(mouse)) return; // 点在胶囊外 → 完全不干预，交给游戏
            _pressing = true;
            _draggedOverThreshold = false;
            _pressGlobalPos = mouse;
            _dragOffset = Position - mouse;
            QueueRedraw();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!_pressing) return;

        bool wasDrag = _draggedOverThreshold;
        _pressing = false;
        _draggedOverThreshold = false;

        if (wasDrag)
        {
            s_position = Position;              // 拖拽结束 → 记忆新位置
            ClampIntoViewport(GetViewportRect().Size);
        }
        else if (HitTest(mouse))
        {
            OnClicked();                        // 未越过阈值 → 视为点击
        }

        QueueRedraw();
        GetViewport().SetInputAsHandled();
    }

    private void HandleMouseMotion()
    {
        var mouse = GetGlobalMousePosition();

        if (_pressing)
        {
            if (!_draggedOverThreshold && mouse.DistanceTo(_pressGlobalPos) > DragThreshold)
                _draggedOverThreshold = true;

            if (_draggedOverThreshold)
            {
                Position = mouse + _dragOffset;
                ClampIntoViewport(GetViewportRect().Size);
                QueueRedraw();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // 悬停高亮
        bool hover = HitTest(mouse);
        if (hover == _hover) return;
        _hover = hover;
        QueueRedraw();
    }

    /// <summary>命中测试：鼠标是否在胶囊矩形内。</summary>
    private bool HitTest(Vector2 globalMousePos) =>
        new Rect2(GlobalPosition, Size).HasPoint(globalMousePos);

    /// <summary>
    /// 点击：打开 / 收起加点面板。
    /// `ToggleFromFloatingButton` 内部会忽略「选牌进行中」的调用（面板隐藏但 _isOpen 保持），
    /// 避免打断游戏的选牌取消链路（DEBUG.md 陷阱 1）。
    /// </summary>
    private void OnClicked()
    {
        var ui = UpgradeUIHandler.Instance;
        if (ui == null)
        {
            Log.Warn("InfiniteUpgradeFloatingButton: UpgradeUIHandler 实例不存在，点击被忽略。");
            return;
        }
        ui.ToggleFromFloatingButton();
    }

    // ═══════════════════════════════════════════════════════════════
    // 位置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>应用记忆位置；没有记忆时落在「右侧中部」默认位。</summary>
    private void ApplyPosition(Vector2 vpSize)
    {
        if (float.IsNaN(s_position.X) || s_position == Vector2.Zero)
            s_position = DefaultPosition(vpSize);
        Position = s_position;
        ClampIntoViewport(vpSize);
        s_position = Position;
    }

    /// <summary>默认位：右侧边缘垂直居中（不挡顶栏、不挡右下角回合结束按钮）。</summary>
    private static Vector2 DefaultPosition(Vector2 vpSize) => new(
        Mathf.Max(EdgeMargin, vpSize.X - PillWidth - EdgeMargin),
        Mathf.Max(EdgeMargin, (vpSize.Y - PillHeight) / 2f));

    /// <summary>把胶囊限制在视口内（分辨率变化 / 拖拽越界时）。</summary>
    private void ClampIntoViewport(Vector2 vpSize)
    {
        Position = new Vector2(
            Mathf.Clamp(Position.X, 0f, Mathf.Max(0f, vpSize.X - Size.X)),
            Mathf.Clamp(Position.Y, 0f, Mathf.Max(0f, vpSize.Y - Size.Y)));
    }

    // ═══════════════════════════════════════════════════════════════
    // 绘制
    // ═══════════════════════════════════════════════════════════════

    public override void _Draw()
    {
        if (!_active) return;

        bool lit = _hover || _pressing;
        Modulate = new Color(1f, 1f, 1f, lit ? 1f : IdleAlpha);

        var rect = new Rect2(Vector2.Zero, Size);
        float radius = Size.X / 2f;

        // 胶囊底：冷灰蓝 + 金环 + 投影（悬停/按下时提亮）
        var box = new StyleBoxFlat
        {
            BgColor = lit ? UpgradeTheme.RowHover : UpgradeTheme.PanelBg,
            BorderColor = lit ? UpgradeTheme.HoverGlow : UpgradeTheme.Gold,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 8,
            ShadowOffset = new Vector2(0, 2),
        };
        box.SetBorderWidthAll(2);
        box.SetCornerRadiusAll((int)radius);
        DrawStyleBox(box, rect);

        // 菱形图标（与顶栏点数徽标同款符号）
        var iconCenter = new Vector2(Size.X / 2f, Size.Y * 0.33f);
        float iconRadius = Size.X * 0.21f;
        DrawDiamond(iconCenter, iconRadius, lit ? UpgradeTheme.HoverGlow : UpgradeTheme.Gold);

        // 点数
        var font = UpgradeTheme.FontBold;
        string text = _drawnPoints == int.MinValue ? "0" : _drawnPoints.ToString();
        const int fontSize = 18;
        var textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
        float textAreaTop = Size.Y * 0.60f;
        float baseline = textAreaTop + (Size.Y - textAreaTop - 10f - textSize.Y) / 2f
                         + font.GetAscent(fontSize);
        DrawString(font, new Vector2(0f, baseline), text, HorizontalAlignment.Center,
            Size.X, fontSize, lit ? UpgradeTheme.TextMain : UpgradeTheme.Gold);
    }

    /// <summary>画一个描边菱形（填充半透明金 + 2px 金边）。</summary>
    private void DrawDiamond(Vector2 center, float radius, Color color)
    {
        var points = new[]
        {
            center + new Vector2(0f, -radius),
            center + new Vector2(radius, 0f),
            center + new Vector2(0f, radius),
            center + new Vector2(-radius, 0f),
        };
        DrawColoredPolygon(points, new Color(color.R, color.G, color.B, 0.20f));

        // DrawPolyline 不自动闭合 → 首点再压一次
        var outline = new Vector2[points.Length + 1];
        Array.Copy(points, outline, points.Length);
        outline[^1] = points[0];
        DrawPolyline(outline, color, 2f, true);
    }
}
