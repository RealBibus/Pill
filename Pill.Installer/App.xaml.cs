using System;
using System.Linq;
using System.Windows;

namespace Pill.Installer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--install")
        {
            Installer.Install(e.Args.Contains("--no-start") ? false : true, null);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--uninstall")
        {
            Installer.Uninstall(null);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
