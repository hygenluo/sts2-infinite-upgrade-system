using System;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace InfiniteUpgradeSystem;

public sealed partial class UpgradeUIHandler : Control
{
    private const Key ToggleHotkey = Key.P;
    private const int UpgradeCost = 5;
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
            Text = "选择一张牌，令其升级 [" + UpgradeCost + "点]",
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

    /// <summary>
    /// 升级按钮点击处理。
    /// 注意：为了避免自定义UI遮罩层阻塞游戏原生卡牌选择界面（陷阱1），
    /// 必须在唤起 CardSelectCmd 前先隐藏遮罩层，选择完成后再关闭UI。
    /// </summary>
    private async void OnUpgradeClicked()
    {
        if (!UpgradePointManager.TrySpendPoints(UpgradeCost))
        {
            GD.Print("点数不足！需要 " + UpgradeCost + " 点，当前只有 " + UpgradePointManager.CurrentPoints + " 点。");
            return;
        }

        var player = GetLocalPlayer();
        if (player == null)
        {
            Log.Warn("InfiniteUpgradeUI: no local player found, refunding.");
            UpgradePointManager.AddPoints(UpgradeCost);
            RefreshPointsLabel();
            return;
        }

        try
        {
            // 陷阱1：在唤起游戏原生卡牌选择界面之前，先隐藏自定义UI遮罩层
            SetUIVisible(false);

            var prefs = new CardSelectorPrefs(
                new LocString("cards", "INFINITEUPGRADESYSTEM-UPGRADE_SELECT_PROMPT"), 1);

            var selected = (await CardSelectCmd.FromDeckGeneric(player, prefs)).ToList();

            if (selected.Count == 0)
            {
                UpgradePointManager.AddPoints(UpgradeCost);
                RefreshPointsLabel();
                GD.Print("未选择卡牌，点数已退回。");
                // 用户取消了选择，恢复UI显示以便继续操作
                SetUIVisible(true);
                return;
            }

            var card = selected[0];
            PerformInfiniteUpgrade(card);

            RefreshPointsLabel();
            GD.Print("卡牌 [" + card.Id.Entry + "] 升级成功！剩余点数: " + UpgradePointManager.CurrentPoints);
            Log.Info("InfiniteUpgrade: upgraded card '" + card.Id.Entry + "', points remaining: " + UpgradePointManager.CurrentPoints);

            // 升级成功后关闭UI回到游戏
            HideUI();
        }
        catch (Exception ex)
        {
            Log.Error("InfiniteUpgrade: upgrade error: " + ex.Message + "\n" + ex.StackTrace);
            UpgradePointManager.AddPoints(UpgradeCost);
            RefreshPointsLabel();
            GD.PrintErr("升级过程出错：" + ex.Message);
            // 出错后恢复UI显示以便查看状态
            SetUIVisible(true);
        }
    }

    private static void PerformInfiniteUpgrade(CardModel card)
    {
        card.AssertMutable();

        var pileType = card.Pile?.Type ?? PileType.Deck;

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();

        ResetUpgradeLevel(card);

        var ncard = NCard.FindOnTable(card);
        if (ncard != null)
        {
            ncard.UpdateVisuals(pileType, CardPreviewMode.Normal);
        }

        ShowUpgradeVfx(card);
    }

    private static void ResetUpgradeLevel(CardModel card)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var field = typeof(CardModel).GetField("_currentUpgradeLevel", flags)
                 ?? typeof(CardModel).GetField("CurrentUpgradeLevel", flags)
                 ?? typeof(CardModel).GetField("upgradeLevel", flags);

        if (field != null)
        {
            field.SetValue(card, 0);
        }
        else
        {
            Log.Warn("InfiniteUpgrade: could not find upgrade level field via reflection.");
        }
    }

    private static void ShowUpgradeVfx(CardModel card)
    {
        try
        {
            var container = NRun.Instance?.GlobalUi?.CardPreviewContainer;
            if (container != null)
            {
                var vfx = NCardUpgradeVfx.Create(card);
                container.AddChildSafely(vfx);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("InfiniteUpgrade: upgrade VFX failed (non-fatal): " + ex.Message);
        }
    }

    private static Player? GetLocalPlayer()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null) return null;

        return LocalContext.GetMe(state) ?? state.Players.FirstOrDefault();
    }
}
