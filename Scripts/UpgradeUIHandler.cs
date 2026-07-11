using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

public sealed partial class UpgradeUIHandler : Control
{
    private const Key ToggleHotkey = Key.P;
    private const float PanelWidth = 520f;
    private const float PanelHeight = 440f;

    private static UpgradeUIHandler? s_instance;

    private ColorRect? _background;
    private Panel? _mainPanel;
    private Label? _pointsLabel;
    private Button? _upgradeButton;

    private bool _isOpen;

    public static void CreateInstance()
    {
        if (s_instance != null)
            return;

        s_instance = new UpgradeUIHandler
        {
            Name = "InfiniteUpgradeUI"
        };

        var tree = (SceneTree?)Engine.GetMainLoop();
        if (tree?.Root != null)
        {
            tree.Root.CallDeferred(Node.MethodName.AddChild, s_instance);
        }
        else
        {
            Log.Warn("InfiniteUpgradeUI: SceneTree root not available during init.");
        }
    }

    public static UpgradeUIHandler? Instance => s_instance;

    public override void _Ready()
    {
        BuildUI();
        SetUIVisible(false);
        Log.Info("InfiniteUpgradeUI: ready.");
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            ResizeBackground();
            CenterMainPanel();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: ToggleHotkey, Echo: false })
        {
            GetViewport().SetInputAsHandled();
            ToggleUI();
        }
    }

    private void BuildUI()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;

        _background = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.65f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_background);

        _mainPanel = new Panel
        {
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        AddChild(_mainPanel);

        var margin = 9;
        var ownSize = new Vector2(PanelWidth - margin * 2, PanelHeight - margin * 2);
        var vbox = new VBoxContainer
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
            Size = ownSize,
            Position = new Vector2(margin, margin),
        };
        _mainPanel.AddChild(vbox);

        var title = new Label
        {
            Text = "无限升级系统",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        _pointsLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _pointsLabel.AddThemeFontSizeOverride("font_size", 22);
        RefreshPointsLabel();
        vbox.AddChild(_pointsLabel);

        vbox.AddChild(new Label
        {
            Text = "--- 卡牌操作 ---",
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        _upgradeButton = new Button
        {
            Text = "选择一张牌，令其升级 [" + CardOperationHelper.UpgradeCost + "点]",
            TooltipText = "从牌组中选择一张卡牌，消耗点数令其升级（可无限次升级同一张牌）",
        };
        _upgradeButton.Pressed += OnUpgradeClicked;
        vbox.AddChild(_upgradeButton);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.Expand });

        vbox.AddChild(new HSeparator());

        var closeButton = new Button
        {
            Text = "关闭",
        };
        closeButton.Pressed += OnCloseClicked;
        vbox.AddChild(closeButton);
    }

    private void ToggleUI()
    {
        if (_isOpen) HideUI();
        else ShowUI();
    }

    private void ShowUI()
    {
        if (CombatManager.Instance is { IsOverOrEnding: false })
        {
            GD.Print("战斗中无法打开无限升级系统。");
            return;
        }

        if (RunManager.Instance?.DebugOnlyGetState() == null)
        {
            GD.Print("没有正在进行的游戏，无法打开升级系统。");
            return;
        }

        _isOpen = true;
        RefreshPointsLabel();
        SetUIVisible(true);
        ResizeBackground();
        CenterMainPanel();
    }

    private void HideUI()
    {
        _isOpen = false;
        SetUIVisible(false);
    }

    private void SetUIVisible(bool visible)
    {
        Visible = visible;
        MouseFilter = visible ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
    }

    private void OnCloseClicked()
    {
        HideUI();
    }

    private void ResizeBackground()
    {
        if (_background != null)
            _background.Size = GetViewportRect().Size;
    }

    private void CenterMainPanel()
    {
        if (_mainPanel == null) return;
        var vpSize = GetViewportRect().Size;
        _mainPanel.Position = new Vector2(
            (vpSize.X - PanelWidth) / 2,
            (vpSize.Y - PanelHeight) / 2);
    }

    private void RefreshPointsLabel()
    {
        if (_pointsLabel != null)
            _pointsLabel.Text = "当前点数: " + UpgradePointManager.CurrentPoints;
    }

    private async void OnUpgradeClicked()
    {
        if (!UpgradePointManager.TrySpendPoints(CardOperationHelper.UpgradeCost))
        {
            GD.Print("点数不足！需要 " + CardOperationHelper.UpgradeCost + " 点，当前只有 " + UpgradePointManager.CurrentPoints + " 点。");
            return;
        }

        var player = CardOperationHelper.GetLocalPlayer();
        if (player == null)
        {
            Log.Warn("InfiniteUpgradeUI: no local player found, refunding.");
            UpgradePointManager.AddPoints(CardOperationHelper.UpgradeCost);
            RefreshPointsLabel();
            return;
        }

        try
        {
            // 陷阱1：在唤起游戏原生卡牌选择界面之前，先隐藏自定义UI遮罩层
            SetUIVisible(false);

            var card = await CardOperationHelper.SelectCardFromDeck(player);

            if (card == null)
            {
                UpgradePointManager.AddPoints(CardOperationHelper.UpgradeCost);
                RefreshPointsLabel();
                GD.Print("未选择卡牌，点数已退回。");
                SetUIVisible(true);
                return;
            }

            CardOperationHelper.PerformInfiniteUpgrade(card);

            RefreshPointsLabel();
            GD.Print("卡牌 [" + card.Id.Entry + "] 升级成功！剩余点数: " + UpgradePointManager.CurrentPoints);
            Log.Info("InfiniteUpgrade: upgraded card '" + card.Id.Entry + "', points remaining: " + UpgradePointManager.CurrentPoints);

            // 升级成功后关闭UI回到游戏
            HideUI();
        }
        catch (Exception ex)
        {
            Log.Error("InfiniteUpgrade: upgrade error: " + ex.Message + "\n" + ex.StackTrace);
            UpgradePointManager.AddPoints(CardOperationHelper.UpgradeCost);
            RefreshPointsLabel();
            GD.PrintErr("升级过程出错：" + ex.Message);
            SetUIVisible(true);
        }
    }
}
