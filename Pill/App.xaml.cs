using System;
using System.Diagnostics;
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
            if (mode == "--apply-update" && e.Args.Length >= 4)
            {
                if (int.TryParse(e.Args[3], out int oldPid))
                    UpdateService.ApplyUpdate(e.Args[1], e.Args[2], oldPid);
                Shutdown();
                return;
            }

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
                _ = UpdateService.CheckAsync();
                Shutdown();
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
