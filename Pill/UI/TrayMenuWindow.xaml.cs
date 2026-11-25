using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pill.Native;
using Pill.Services;
using Pill.ViewModels;

namespace Pill.UI;

public partial class TrayMenuWindow : Window
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x201;
    private const int WmRButtonDown = 0x204;
    private const int WmMButtonDown = 0x207;

    private static readonly Color DefaultAccent = Color.FromRgb(0x5B, 0x8C, 0xFF);

    private readonly MainViewModel _viewModel;
    private readonly Action _toggleVisibility;
    private readonly Action _onExit;
    private readonly SolidColorBrush _accentBrush;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook;
    private DispatcherTimer? _outsideTimer;
    private bool _closing;
    private bool _checking;

    public TrayMenuWindow(MainViewModel viewModel, Action toggleVisibility, Action onExit)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _toggleVisibility = toggleVisibility;
        _onExit = onExit;
        _accentBrush = (SolidColorBrush)Resources["MenuAccentBrush"];
        _mouseProc = OnMouseHook;

        RefreshAccent(_viewModel.AmbientColor);
        AmbientValue.Text = _viewModel.AmbientColorEnabled ? "On" : "Off";

        AutoStartToggle.IsChecked = _viewModel.AutoStart;
        AutoStartToggle.Checked += (_, _) => _viewModel.AutoStart = true;
        AutoStartToggle.Unchecked += (_, _) => _viewModel.AutoStart = false;

        AutoUpdateToggle.IsChecked = _viewModel.AutoUpdate;
        AutoUpdateToggle.Checked += (_, _) => _viewModel.AutoUpdate = true;
        AutoUpdateToggle.Unchecked += (_, _) => _viewModel.AutoUpdate = false;

        BetaToggle.IsChecked = _viewModel.UpdateChannel == "beta";
        BetaToggle.Checked += (_, _) => _viewModel.UpdateChannel = "beta";
        BetaToggle.Unchecked += (_, _) => _viewModel.UpdateChannel = "stable";

        UpdateNowButton.Click += async (_, _) => await UpdateNowAsync();
        DeferButton.Click += async (_, _) => await DeferAsync();

        UpdateManager.UpdateAvailable += OnUpdateAvailable;
        UpdateManager.StageProgress += OnStageProgress;
        UpdateManager.StageCompleted += OnStageCompleted;
        if (UpdateManager.Pending != null) ShowUpdateCard(UpdateManager.Pending);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            UpdateManager.UpdateAvailable -= OnUpdateAvailable;
            UpdateManager.StageProgress -= OnStageProgress;
            UpdateManager.StageCompleted -= OnStageCompleted;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _outsideTimer?.Stop();
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        };
    }

    private void RefreshAccent(Color color)
    {
        Color accent = SafeAccent(color);
        _accentBrush.Color = accent;
        AccentValue.Text = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
    }

    private Color SafeAccent(Color color)
    {
        if (!_viewModel.AmbientColorEnabled) return DefaultAccent;

        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2d;
        double saturation = max - min < 0.0001
            ? 0d
            : (max - min) / (1d - Math.Abs(2d * lightness - 1d));

        if (saturation < 0.35 || lightness < 0.22 || lightness > 0.78) return DefaultAccent;
        return color;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AmbientColor) or nameof(MainViewModel.AmbientColorEnabled))
        {
            RefreshAccent(_viewModel.AmbientColor);
            AmbientValue.Text = _viewModel.AmbientColorEnabled ? "On" : "Off";
        }
        else if (e.PropertyName == nameof(MainViewModel.AutoStart))
        {
            AutoStartToggle.IsChecked = _viewModel.AutoStart;
        }
        else if (e.PropertyName == nameof(MainViewModel.AutoUpdate))
        {
            AutoUpdateToggle.IsChecked = _viewModel.AutoUpdate;
        }
        else if (e.PropertyName == nameof(MainViewModel.UpdateChannel))
        {
            BetaToggle.IsChecked = _viewModel.UpdateChannel == "beta";
        }
    }

    private void AmbientHit_MouseUp(object sender, MouseButtonEventArgs e)
        => _viewModel.AmbientColorEnabled = !_viewModel.AmbientColorEnabled;

    private void ResetAccentButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AmbientColorEnabled = false;

    private void ShowHideButton_Click(object sender, RoutedEventArgs e)
    {
        _toggleVisibility();
        Close();
    }

    private void ClearShelfButton_Click(object sender, RoutedEventArgs e) => _viewModel.ClearShelf();

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        _onExit();
    }

    private void OnUpdateAvailable(UpdateInfo info) => Dispatcher.Invoke(() => ShowUpdateCard(info));

    private void OnStageProgress(double value) => Dispatcher.Invoke(() => UpdateProgress.Value = value);

    private void OnStageCompleted() => Dispatcher.Invoke(() =>
    {
        UpdateProgress.Value = 1;
        UpdateStatusValue.Text = "Ready · installs on shutdown";
        UpdateNotesText.Text = "The update is downloaded. It will be installed automatically when you exit Pill.";
    });

    private void ShowUpdateCard(UpdateInfo info)
    {
        UpdateVersionText.Text = "Version " + info.Manifest.Version;
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(info.Manifest.ReleaseNotes)
            ? "No release notes."
            : info.Manifest.ReleaseNotes;
        UpdateStatusValue.Text = UpdateManager.IsStaged
            ? "Ready · installs on shutdown"
            : "Downloading in background…";
        UpdateCard.Visibility = Visibility.Visible;
    }

    private async void CheckUpdateHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_checking) return;
        if (!UpdateManager.UpdatesEnabled)
        {
            UpdateStatusValue.Text = "Only in installed builds";
            return;
        }
        _checking = true;
        UpdateStatusValue.Text = "Checking…";
        bool found = await _viewModel.CheckForUpdatesAsync();
        if (!found) UpdateStatusValue.Text = "Up to date";
        else if (UpdateManager.IsStaged) UpdateStatusValue.Text = "Ready · installs on shutdown";
        _checking = false;
    }

    private async System.Threading.Tasks.Task UpdateNowAsync()
    {
        UpdateNowButton.IsEnabled = false;
        DeferButton.IsEnabled = false;
        UpdateStatusValue.Text = "Downloading…";
        try
        {
            await UpdateManager.StagePendingAsync();
            await UpdateManager.RestartWithVeilAsync();
        }
        catch
        {
            UpdateStatusValue.Text = "Failed";
            UpdateNowButton.IsEnabled = true;
            DeferButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task DeferAsync()
    {
        UpdateNowButton.IsEnabled = false;
        DeferButton.IsEnabled = false;
        UpdateStatusValue.Text = "Downloading…";
        try
        {
            await UpdateManager.StagePendingAsync();
            UpdateManager.MarkDeferred();
            UpdateStatusValue.Text = "On shutdown";
        }
        catch
        {
            UpdateStatusValue.Text = "Failed";
            UpdateNowButton.IsEnabled = true;
            DeferButton.IsEnabled = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.UseDarkMode(hwnd);
        WindowEffects.UseRoundCorners(hwnd);
        WindowEffects.EnableStrongBlur(hwnd);
        WindowEffects.ApplyAcrylic(hwnd);
        WindowEffects.TrySystemBackdrop(hwnd);

        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateLayout();
            PlaceAtCursor();
            var border = RootBorder;
            var translate = new TranslateTransform(0, 8);
            border.RenderTransform = translate;
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            border.BeginAnimation(UIElement.OpacityProperty, fade);
            var move = new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            translate.BeginAnimation(TranslateTransform.YProperty, move);
        }));
    }

    private void PlaceAtCursor()
    {
        System.Drawing.Point mousePos = System.Windows.Forms.Cursor.Position;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double mouseDipX = mousePos.X / dpi.DpiScaleX;
        double mouseDipY = mousePos.Y / dpi.DpiScaleY;
        double x = mouseDipX - ActualWidth;
        double y = mouseDipY - ActualHeight;
        System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(mousePos);
        double workLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
        double workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
        double workRight = screen.WorkingArea.Right / dpi.DpiScaleX;
        double workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;
        if (x < workLeft) x = workLeft;
        if (y < workTop) y = workTop;
        if (x + ActualWidth > workRight) x = workRight - ActualWidth;
        if (y + ActualHeight > workBottom) y = workBottom - ActualHeight;
        Left = x;
        Top = y;
    }

    private void CloseMenu()
    {
        if (!_closing) Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseMenu();
        }
    }

    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsOutsideClick(wParam, lParam))
            Dispatcher.BeginInvoke(new Action(ScheduleOutsideClose));
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsOutsideClick(IntPtr wParam, IntPtr lParam)
    {
        if (wParam != (IntPtr)WmLButtonDown &&
            wParam != (IntPtr)WmRButtonDown &&
            wParam != (IntPtr)WmMButtonDown) return false;

        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double x = info.pt.X / dpi.DpiScaleX;
        double y = info.pt.Y / dpi.DpiScaleY;
        Point origin = RootBorder.TranslatePoint(new Point(0, 0), this);
        double left = Left + origin.X;
        double top = Top + origin.Y;
        double right = left + RootBorder.ActualWidth;
        double bottom = top + RootBorder.ActualHeight;
        return x < left || x > right || y < top || y > bottom;
    }

    private void ScheduleOutsideClose()
    {
        if (_closing) return;
        _outsideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _outsideTimer.Tick -= OnOutsideTimerTick;
        _outsideTimer.Tick += OnOutsideTimerTick;
        _outsideTimer.Start();
    }

    private void OnOutsideTimerTick(object? sender, EventArgs e)
    {
        _outsideTimer?.Stop();
        if (!_closing && IsVisible) Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => ScheduleOutsideClose();

    private void Window_Closing(object? sender, CancelEventArgs e) => _closing = true;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public NativePoint pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
