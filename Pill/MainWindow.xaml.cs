using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pill.Models;
using Pill.Services;
using Pill.ViewModels;
using WinFormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsToolStripSeparator = System.Windows.Forms.ToolStripSeparator;
using Color = System.Drawing.Color;
using DrawingLinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using DrawingPen = System.Drawing.Pen;
using TrayIcon = System.Drawing.Icon;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace Pill;

public partial class MainWindow : Window
{
    private const double CollapsedWidth = 380;
    private const double CollapsedHeight = 66;
    private const double ExpandedWidth = 480;
    private const double ExpandedHeight = 330;

    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _hoverWatchTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _dragLeaveTimer;

    private Storyboard? _stateStoryboard;
    private readonly SolidColorBrush _accentBrush =
        new(System.Windows.Media.Color.FromArgb(255, 91, 140, 255));
    private readonly SolidColorBrush _accentBackgroundBrush =
        new(AmbientColorService.BaseAccent);
    private readonly SolidColorBrush _accentSoftBrush =
        new(System.Windows.Media.Color.FromArgb(255, 159, 188, 255));
    private readonly SolidColorBrush _accentDeepBrush =
        new(System.Windows.Media.Color.FromArgb(255, 46, 90, 168));

    public SolidColorBrush AccentBrush => _accentBrush;
    public SolidColorBrush AccentBackgroundBrush => _accentBackgroundBrush;
    public SolidColorBrush AccentSoftBrush => _accentSoftBrush;
    public SolidColorBrush AccentDeepBrush => _accentDeepBrush;

    private WinFormsNotifyIcon? _tray;
    private WinFormsContextMenuStrip? _trayMenu;
    private WinFormsToolStripMenuItem? _ambientMenuItem;
    private WinFormsToolStripMenuItem? _autoStartMenuItem;
    private WinFormsToolStripMenuItem? _autoUpdateMenuItem;
    private TrayIcon? _trayIcon;

    private bool _expanded;
    private bool _dragActive;
    private bool _draggingOut;
    private bool _suppressAutoExpand;
    private bool _exitRequested;

    private ShelfItem? _dragCandidate;
    private Point _dragOrigin;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.AmbientColor) or nameof(MainViewModel.AmbientColorEnabled))
            {
                System.Windows.Media.Color main = _viewModel.AmbientColor;
                AnimateAccent(_accentBackgroundBrush, main);
                AnimateAccent(_accentBrush, main);
                AnimateAccent(_accentSoftBrush, AmbientColorService.Lift(main));
                AnimateAccent(_accentDeepBrush, AmbientColorService.Deepen(main));
                if (_ambientMenuItem is not null) _ambientMenuItem.Checked = _viewModel.AmbientColorEnabled;
            }
            else if (e.PropertyName == nameof(MainViewModel.AutoStart) && _autoStartMenuItem is not null)
            {
                _autoStartMenuItem.Checked = _viewModel.AutoStart;
            }
            else if (e.PropertyName == nameof(MainViewModel.AutoUpdate) && _autoUpdateMenuItem is not null)
            {
                _autoUpdateMenuItem.Checked = _viewModel.AutoUpdate;
            }
        };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Expand(); };

        _hoverWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _hoverWatchTimer.Tick += OnHoverWatchTick;
        _hoverWatchTimer.Start();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _collapseTimer.Tick += OnCollapseTimerTick;

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            _dragActive = false;
            AnimateOpacity(DropOutline, 0, 180);
            ScheduleCollapse(_viewModel.HasShelfItems ? 1800 : 160);
        };

        SourceInitialized += (_, _) => PositionAtTopCenter();
        Loaded += (_, _) =>
        {
            PositionAtTopCenter();
            _viewModel.SetColorExclusion((int)Left, (int)Top, (int)Width, (int)Height);
            _viewModel.Start();
            SetupTray();
        };
        Closing += MainWindow_Closing;
    }

    private void PositionAtTopCenter()
    {
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ExpandedWidth) / 2;
        Top = 0;
    }

    private void Expand()
    {
        if (_expanded || _suppressAutoExpand) return;
        _expanded = true;
        ExpandedRoot.IsHitTestVisible = true;
        CompactRoot.IsHitTestVisible = false;

        AnimateState(ExpandedWidth, ExpandedHeight, compactOpacity: 0, expandedOpacity: 1, expand: true);
    }

    private void Collapse(bool force = false)
    {
        if (!force && (_dragActive || _viewModel.HasShelfItems)) return;
        if (!_expanded) return;

        _expanded = false;
        ExpandedRoot.IsHitTestVisible = false;
        CompactRoot.IsHitTestVisible = true;

        AnimateState(CollapsedWidth, CollapsedHeight, compactOpacity: 1, expandedOpacity: 0, expand: false);
    }

    private void AnimateState(double width, double height, double compactOpacity, double expandedOpacity, bool expand)
    {
        double fromWidth = Pill.ActualWidth > 0 ? Pill.ActualWidth : Pill.Width;
        double fromHeight = Pill.ActualHeight > 0 ? Pill.ActualHeight : Pill.Height;
        double fromCompactOpacity = CompactRoot.Opacity;
        double fromExpandedOpacity = ExpandedRoot.Opacity;
        double fromShift = ExpandedShift.Y;

        _stateStoryboard?.Stop(this);
        Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
        Pill.BeginAnimation(FrameworkElement.HeightProperty, null);
        ExpandedShift.BeginAnimation(TranslateTransform.YProperty, null);

        Pill.Width = fromWidth;
        Pill.Height = fromHeight;
        CompactRoot.Opacity = fromCompactOpacity;
        ExpandedRoot.Opacity = fromExpandedOpacity;

        var storyboard = new Storyboard();
        EasingFunctionBase sizeEase = expand
            ? new CircleEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
        EasingFunctionBase fadeEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

        AddDouble(storyboard, Pill, FrameworkElement.WidthProperty, fromWidth, width, expand ? 560 : 440, sizeEase);
        AddDouble(storyboard, Pill, FrameworkElement.HeightProperty, fromHeight, height, expand ? 520 : 420, sizeEase);

        AddDouble(storyboard, CompactRoot, UIElement.OpacityProperty, fromCompactOpacity, compactOpacity,
            expand ? 180 : 260, fadeEase);
        AddDouble(storyboard, ExpandedRoot, UIElement.OpacityProperty, fromExpandedOpacity, expandedOpacity,
            expand ? 320 : 160, fadeEase,
            begin: TimeSpan.FromMilliseconds(expand ? 90 : 0));
        AddDouble(storyboard, ExpandedShift, TranslateTransform.YProperty, fromShift, expand ? 0 : 12,
            expand ? 420 : 220, fadeEase,
            begin: TimeSpan.FromMilliseconds(expand ? 70 : 0));

        _stateStoryboard = storyboard;
        storyboard.Begin(this, true);

        if (expand) AnimateSheen();
    }

    private void AnimateSheen()
    {
        SheenShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-180, 420, TimeSpan.FromMilliseconds(760))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromPercent(0.25)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Sheen.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void AddDouble(Storyboard storyboard, DependencyObject target, DependencyProperty property,
        double from, double to, int milliseconds, IEasingFunction easing, TimeSpan? begin = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        if (begin.HasValue) animation.BeginTime = begin;
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    private static void AnimateAccent(SolidColorBrush brush, System.Windows.Media.Color color)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private void AnimateOpacity(UIElement element, double to, int milliseconds)
    {
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ScheduleCollapse(int milliseconds)
    {
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        if (milliseconds > 0) _collapseTimer.Start();
    }

    private void OnCollapseTimerTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_dragActive || _draggingOut) return;
        Collapse(force: true);
    }

    private Rect PillBounds()
    {
        GeneralTransform transform = Pill.TransformToVisual(this);
        return transform.TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
    }

    private void OnHoverWatchTick(object? sender, EventArgs e)
    {
        if (!IsVisible || !_expanded || _dragActive || _draggingOut) return;

        Point position = Mouse.GetPosition(this);
        bool inside = PillBounds().Contains(position);

        if (inside)
        {
            if (_collapseTimer.IsEnabled) _collapseTimer.Stop();
        }
        else if (!_collapseTimer.IsEnabled)
        {
            ScheduleCollapse(_viewModel.HasShelfItems ? 1800 : 180);
        }
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        _dragLeaveTimer.Stop();
        _collapseTimer.Stop();
        _suppressAutoExpand = false;
        if (!_expanded) _hoverTimer.Start();
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_draggingOut || _dragActive) return;

        if (PillBounds().Contains(e.GetPosition(this))) return;

        ScheduleCollapse(_viewModel.HasShelfItems ? 1800 : 180);
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<Button>(source) is not null || FindAncestor<ItemsControl>(source) is not null))
            return;

        _collapseTimer.Stop();
        if (_expanded)
        {
            if (!_viewModel.HasShelfItems && !_dragActive) Collapse(force: true);
        }
        else Expand();
    }

    private void AmbientButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AmbientColorEnabled = !_viewModel.AmbientColorEnabled;

    private void ClearShelf_Click(object sender, RoutedEventArgs e)
    {
        var items = new System.Collections.Generic.List<ShelfItem>(_viewModel.Shelf);
        foreach (ShelfItem item in items) _viewModel.Remove(item);
        ScheduleCollapse(220);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressAutoExpand = true;
        _hoverTimer.Stop();
        Collapse(force: true);
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool HasFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        _dragActive = true;
        _suppressAutoExpand = false;
        _dragLeaveTimer.Stop();
        _collapseTimer.Stop();
        AnimateOpacity(DropOutline, 0.85, 180);
        Expand();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _dragLeaveTimer.Stop();
        _dragActive = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        if (!_dragActive) return;
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _viewModel.AddFiles(paths);

        _dragActive = false;
        _dragLeaveTimer.Stop();
        AnimateOpacity(DropOutline, 0, 220);
        ScheduleCollapse(_viewModel.HasShelfItems ? 2500 : 220);
    }

    private void Chip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
            return;

        _dragCandidate = (sender as FrameworkElement)?.DataContext as ShelfItem;
        _dragOrigin = e.GetPosition(this);
    }

    private void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || _draggingOut || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.X) < 6 && Math.Abs(current.Y - _dragOrigin.Y) < 6)
            return;

        ShelfItem item = _dragCandidate;
        _dragCandidate = null;
        _draggingOut = true;
        _collapseTimer.Stop();

        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { item.Path });
            data.SetText(item.Path);

            DragDropEffects effect = DragDrop.DoDragDrop((DependencyObject)sender, data,
                DragDropEffects.Copy | DragDropEffects.Move);

            if (effect != DragDropEffects.None)
                _viewModel.Remove(item);
        }
        finally
        {
            _draggingOut = false;
            ScheduleCollapse(_viewModel.HasShelfItems ? 1800 : 220);
        }
    }

    private void Chip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

    private void RemoveShelf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShelfItem item)
            _viewModel.Remove(item);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _viewModel.Media.PreviousAsync();

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _viewModel.Media.TogglePlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _viewModel.Media.NextAsync();

    private void SetupTray()
    {
        try
        {
            _trayIcon = CreateTrayIcon();
            _trayMenu = new WinFormsContextMenuStrip
            {
                BackColor = Color.FromArgb(10, 14, 24),
                ForeColor = Color.FromArgb(244, 246, 255),
                ShowImageMargin = false
            };
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Show / hide", null, (_, _) => ToggleVisibility()));

            _ambientMenuItem = new WinFormsToolStripMenuItem("Ambient color")
            {
                CheckOnClick = true,
                Checked = _viewModel.AmbientColorEnabled
            };
            _ambientMenuItem.Click += (_, _) => _viewModel.AmbientColorEnabled = _ambientMenuItem.Checked;
            _trayMenu.Items.Add(_ambientMenuItem);

            _autoStartMenuItem = new WinFormsToolStripMenuItem("Autostart")
            {
                CheckOnClick = true,
                Checked = _viewModel.AutoStart
            };
            _autoStartMenuItem.Click += (_, _) => _viewModel.AutoStart = _autoStartMenuItem.Checked;
            _trayMenu.Items.Add(_autoStartMenuItem);

            _autoUpdateMenuItem = new WinFormsToolStripMenuItem("Auto update (beta)")
            {
                CheckOnClick = true,
                Checked = _viewModel.AutoUpdate
            };
            _autoUpdateMenuItem.Click += (_, _) => _viewModel.AutoUpdate = _autoUpdateMenuItem.Checked;
            _trayMenu.Items.Add(_autoUpdateMenuItem);

            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Check for updates", null,
                (_, _) => _ = _viewModel.CheckForUpdatesAsync()));
            _trayMenu.Items.Add(new WinFormsToolStripSeparator());
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Install", null, (_, _) => _viewModel.Install()));
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Uninstall", null, (_, _) => _viewModel.Uninstall()));
            _trayMenu.Items.Add(new WinFormsToolStripSeparator());
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Exit", null, (_, _) => Exit()));

            _tray = new WinFormsNotifyIcon
            {
                Icon = _trayIcon,
                Text = "Pill — Dynamic Island",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _tray.DoubleClick += (_, _) => ToggleVisibility();
        }
        catch { }
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        PositionAtTopCenter();
        _suppressAutoExpand = false;
        Expand();
    }

    private void MenuToggle_Click(object sender, RoutedEventArgs e) => ToggleVisibility();

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Exit();

    private void MenuUpdate_Click(object sender, RoutedEventArgs e)
        => _ = _viewModel.CheckForUpdatesAsync();

    private void MenuInstall_Click(object sender, RoutedEventArgs e) => _viewModel.Install();

    private void MenuUninstall_Click(object sender, RoutedEventArgs e) => _viewModel.Uninstall();

    private void Exit()
    {
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        Cleanup();
    }

    private void Cleanup()
    {
        _hoverTimer.Stop();
        _hoverWatchTimer.Stop();
        _collapseTimer.Stop();
        _dragLeaveTimer.Stop();
        _stateStoryboard?.Stop(this);

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        _viewModel.Dispose();
    }

    private static TrayIcon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(4, 12, 56, 40);
        using var path = RoundedRectangle(rect, 18);
        using var gradient = new DrawingLinearGradientBrush(
            new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Bottom),
            Color.FromArgb(255, 116, 228, 255), Color.FromArgb(255, 185, 140, 255));
        graphics.FillPath(gradient, path);

        using var pen = new DrawingPen(Color.FromArgb(80, 255, 255, 255), 1.4f);
        graphics.DrawPath(pen, path);

        float[] heights = { 12, 22, 15 };
        for (int i = 0; i < heights.Length; i++)
        {
            float x = 22 + i * 9;
            float h = heights[i];
            using var bar = RoundedRectangle(new RectangleF(x, 32 - h / 2, 4.5f, h), 2);
            using var barBrush = new SolidBrush(Color.FromArgb(245, 14, 16, 26));
            graphics.FillPath(barBrush, bar);
        }

        return TrayIcon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRectangle(RectangleF rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
