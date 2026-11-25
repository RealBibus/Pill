using System;
using System.Windows;
using System.Windows.Interop;
using Pill.Native;

namespace Pill.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PathText.Text = Installer.AppDirectory;
        VersionText.Text = "Version " + Installer.Version;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.UseDarkMode(hwnd);
        WindowEffects.UseRoundCorners(hwnd);
        WindowEffects.EnableStrongBlur(hwnd);
        WindowEffects.ApplyAcrylic(hwnd);
        WindowEffects.TrySystemBackdrop(hwnd);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "Installing…";
        var progress = new Progress<double>(value => Progress.Value = value);
        try
        {
            await System.Threading.Tasks.Task.Run(() => Installer.Install(true, progress));
            Progress.Value = 1;
            StatusText.Text = "Installed. Pill is starting.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            "Uninstall Pill? Settings will be kept.", "Pill Installer",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        SetBusy(true);
        StatusText.Text = "Uninstalling…";
        var progress = new Progress<double>(value => Progress.Value = value);
        try
        {
            await System.Threading.Tasks.Task.Run(() => Installer.Uninstall(progress));
            Progress.Value = 1;
            StatusText.Text = "Uninstalled. Files will be removed after exit.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
    }
}
