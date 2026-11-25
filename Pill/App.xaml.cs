using System;
using System.Windows;
using Pill.Services;

namespace Pill;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            string mode = e.Args[0];
            if (mode == "--install")
            {
                InstallerService.Install();
                Shutdown();
                return;
            }

            if (mode == "--uninstall")
            {
                InstallerService.Uninstall();
                Shutdown();
                return;
            }

            if (mode == "--check-update")
            {
                _ = UpdateManager.CheckNowAsync();
                Shutdown();
                return;
            }
        }

        UpdateManager.Attach(this);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
