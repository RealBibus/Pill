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
    private const double CollapsedWidth = 354;
    private const double CollapsedHeight = 84;
    private const double ExpandedWidth = 500;
    private const double ExpandedHeight = 350;

    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _hoverWatchTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _dragLeaveTimer;

    private Storyboard? _stateStoryboard;
    private bool _sizeAnimating;
    private long _sizeStartMs;
    private int _sizeDurationMs;
    private double _sizeFromWidth, _sizeFromHeight, _sizeToWidth, _sizeToHeight;
    private EasingFunctionBase _sizeEase = new CubicEase();
    private WinFormsNotifyIcon? _tray;
    private WinFormsContextMenuStrip? _trayMenu;
    private TrayIcon? _trayIcon;

    private bool _expanded;
    private bool _pinned;
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

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Expand(); };

        // MouseLeave при смене hit-test слоёв приходит один раз и бывает ложным.
        // Опрос реальной позиции курсора гарантирует сворачивание при уходе.
        _hoverWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverWatchTimer.Tick += OnHoverWatchTick;
        _hoverWatchTimer.Start();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _collapseTimer.Tick += OnCollapseTimerTick;

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            _dragActive = false;
            AnimateOpacity(DropOutline, 0, 180);
            ScheduleCollapse(_viewModel.HasShelfItems ? 6000 : 250);
        };

        SourceInitialized += (_, _) => PositionAtTopCenter();
        SizeChanged += (_, _) =>
        {
            if (IsLoaded)
                Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
        };
        Loaded += (_, _) =>
        {
            PositionAtTopCenter();
            _viewModel.Start();
            SetupTray();
        };
        Closing += MainWindow_Closing;
    }

    // ------------------------------------------------------------------
    // Позиционирование и анимация состояния
    // ------------------------------------------------------------------

    private void PositionAtTopCenter()
    {
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
        Top = -8;
    }

    private void Expand()
    {
        if (_expanded || _suppressAutoExpand && !_pinned) return;
        _expanded = true;
        ExpandedRoot.IsHitTestVisible = true;
        CompactRoot.IsHitTestVisible = false;

        AnimateState(ExpandedWidth, ExpandedHeight, compactOpacity: 0, expandedOpacity: 1, expand: true);
    }

    private void Collapse(bool force = false)
    {
        if (!force && (_pinned || _dragActive || _viewModel.HasShelfItems)) return;
        if (!_expanded) return;

        _expanded = false;
        ExpandedRoot.IsHitTestVisible = false;
        CompactRoot.IsHitTestVisible = true;

        AnimateState(CollapsedWidth, CollapsedHeight, compactOpacity: 1, expandedOpacity: 0, expand: false);
    }

    private void AnimateState(double width, double height, double compactOpacity, double expandedOpacity, bool expand)
    {
        // WPF не применяет анимированную Height нативного окна до конца анимации,
        // поэтому размер ведём вручную покадрово — Width и Height идут синхронно.
        double fromWidth = ActualWidth > 0 ? ActualWidth : Width;
        double fromHeight = ActualHeight > 0 ? ActualHeight : Height;
        double fromCompactOpacity = CompactRoot.Opacity;
        double fromExpandedOpacity = ExpandedRoot.Opacity;

        _stateStoryboard?.Stop(this);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        Width = fromWidth;
        Height = fromHeight;
        CompactRoot.Opacity = fromCompactOpacity;
        ExpandedRoot.Opacity = fromExpandedOpacity;

        _sizeFromWidth = fromWidth;
        _sizeFromHeight = fromHeight;
        _sizeToWidth = width;
        _sizeToHeight = height;
        _sizeDurationMs = expand ? 520 : 420;
        _sizeEase = expand
            ? new CircleEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
        _sizeStartMs = Environment.TickCount64;

        if (!_sizeAnimating)
        {
            _sizeAnimating = true;
            CompositionTarget.Rendering += OnSizeFrame;
        }

        var storyboard = new Storyboard();
        EasingFunctionBase fadeEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

        AddDouble(storyboard, CompactRoot, UIElement.OpacityProperty, fromCompactOpacity, compactOpacity,
            expand ? 180 : 280, fadeEase);
        AddDouble(storyboard, ExpandedRoot, UIElement.OpacityProperty, fromExpandedOpacity, expandedOpacity,
            expand ? 340 : 150, fadeEase,
            begin: TimeSpan.FromMilliseconds(expand ? 120 : 0));

        _stateStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    private void OnSizeFrame(object? sender, EventArgs e)
    {
        double progress = Math.Min(1.0, (Environment.TickCount64 - _sizeStartMs) / (double)_sizeDurationMs);
        double eased = _sizeEase.Ease(progress);

        Width = _sizeFromWidth + (_sizeToWidth - _sizeFromWidth) * eased;
        Height = _sizeFromHeight + (_sizeToHeight - _sizeFromHeight) * eased;

        if (progress >= 1.0)
        {
            Width = _sizeToWidth;
            Height = _sizeToHeight;
            _sizeAnimating = false;
            CompositionTarget.Rendering -= OnSizeFrame;
        }
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
        if (_pinned || _dragActive || _draggingOut) return;
        Collapse(force: true);
    }

    private void OnHoverWatchTick(object? sender, EventArgs e)
    {
        if (!IsVisible || !_expanded || _pinned || _dragActive || _draggingOut) return;

        Point position = Mouse.GetPosition(this);
        bool inside = position.X >= 0 && position.X <= ActualWidth &&
                      position.Y >= 0 && position.Y <= ActualHeight;

        if (inside)
        {
            if (_collapseTimer.IsEnabled) _collapseTimer.Stop();
        }
        else if (!_collapseTimer.IsEnabled)
        {
            ScheduleCollapse(_viewModel.HasShelfItems ? 6000 : 420);
        }
    }

    // ------------------------------------------------------------------
    // Мышь
    // ------------------------------------------------------------------

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

        // При смене hit-test у дочерних слоёв WPF может прислать ложный MouseLeave.
        Point position = e.GetPosition(this);
        if (position.X >= 0 && position.X <= ActualWidth &&
            position.Y >= 0 && position.Y <= ActualHeight)
            return;

        ScheduleCollapse(_viewModel.HasShelfItems ? 6000 : 420);
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<Button>(source) is not null || FindAncestor<ItemsControl>(source) is not null))
            return;

        SetPinned(!_pinned);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        if (_pinned)
        {
            _suppressAutoExpand = false;
            _collapseTimer.Stop();
            Expand();
        }
        else
        {
            ScheduleCollapse(600);
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = false;
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

    // ------------------------------------------------------------------
    // Dropshelf: файл прилетает сверху и ждёт на полке
    // ------------------------------------------------------------------

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
        ScheduleCollapse(_viewModel.HasShelfItems ? 7000 : 300);
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
            ScheduleCollapse(_viewModel.HasShelfItems ? 6000 : 500);
        }
    }

    private void Chip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

    private void RemoveShelf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShelfItem item)
            _viewModel.Remove(item);
    }

    // ------------------------------------------------------------------
    // Плеер
    // ------------------------------------------------------------------

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _viewModel.Media.PreviousAsync();

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _viewModel.Media.TogglePlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _viewModel.Media.NextAsync();

    // ------------------------------------------------------------------
    // Трей и завершение
    // ------------------------------------------------------------------

    private void SetupTray()
    {
        try
        {
            _trayIcon = CreateTrayIcon();
            _trayMenu = new WinFormsContextMenuStrip
            {
                BackColor = Color.FromArgb(28, 29, 39),
                ForeColor = Color.FromArgb(244, 246, 255),
                ShowImageMargin = false
            };
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Показать / скрыть", null, (_, _) => ToggleVisibility()));
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Закрепить островок", null,
                (_, _) => SetPinned(!_pinned)));
            _trayMenu.Items.Add(new WinFormsToolStripSeparator());
            _trayMenu.Items.Add(new WinFormsToolStripMenuItem("Выход", null, (_, _) => Exit()));

            _tray = new WinFormsNotifyIcon
            {
                Icon = _trayIcon,
                Text = "Pill — Dynamic Island",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _tray.DoubleClick += (_, _) => ToggleVisibility();
        }
        catch
        {
            // трей недоступен — контекстное меню самого островка всё равно работает
        }
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

    private void MenuPin_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Exit();

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
        if (_sizeAnimating)
        {
            _sizeAnimating = false;
            CompositionTarget.Rendering -= OnSizeFrame;
        }

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
