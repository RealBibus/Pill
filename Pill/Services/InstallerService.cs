using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace Pill.Services;

public static class InstallerService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pill";
    private const string AppName = "Pill";

    public static string RootDirectory => UpdateConfig.RootDir;

    public static string InstalledExecutable
    {
        get
        {
            CurrentState? state = UpdateManager.Service.ReadCurrent();
            string dir = string.IsNullOrWhiteSpace(state?.Dir) ? "app" : state!.Dir;
            return Path.Combine(RootDirectory, dir, UpdateConfig.AppExeName);
        }
    }

    public static bool IsInstalled => File.Exists(Path.Combine(RootDirectory, "current.json"));

    public static string CurrentExecutable => Environment.ProcessPath ?? InstalledExecutable;

    public static void Install()
    {
        string installer = FindInstaller();
        if (File.Exists(installer))
        {
            Launch(installer, "--install");
            return;
        }

        InstallFallback();
    }

    public static void Uninstall()
    {
        string installer = File.Exists(Path.Combine(InstalledExecutable.Replace(UpdateConfig.AppExeName, ""), "Pill.Installer.exe"))
            ? Path.Combine(InstalledExecutable.Replace(UpdateConfig.AppExeName, ""), "Pill.Installer.exe")
            : FindInstaller();
        if (File.Exists(installer))
        {
            Launch(installer, "--uninstall");
            return;
        }

        UninstallFallback();
    }

    public static void LaunchInstaller(bool uninstall)
    {
        string installer = FindInstaller();
        if (!File.Exists(installer)) return;
        Launch(installer, uninstall ? "--uninstall" : "--install");
    }

    private static string FindInstaller()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Pill.Installer.exe");
        if (File.Exists(local)) return local;

        CurrentState? state = UpdateManager.Service.ReadCurrent();
        string dir = string.IsNullOrWhiteSpace(state?.Dir) ? "app" : state!.Dir;
        return Path.Combine(RootDirectory, dir, "Pill.Installer.exe");
    }

    private static void Launch(string installer, string arguments)
    {
        Process.Start(new ProcessStartInfo(installer)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installer) ?? AppContext.BaseDirectory
        });
        Application.Current?.Shutdown();
    }

    private static void InstallFallback()
    {
        string source = Path.GetDirectoryName(CurrentExecutable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return;

        string appDir = Path.Combine(RootDirectory, "app");
        Directory.CreateDirectory(appDir);
        string[] patterns = { "*.exe", "*.dll", "*.json", "*.config", "*.pri" };
        foreach (string pattern in patterns)
        {
            foreach (string file in Directory.GetFiles(source, pattern, SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("current.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("device.json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(appDir, name), true);
            }
        }

        UpdateManager.Service.WriteCurrent(new CurrentState
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            Dir = "app"
        });
        UpdateManager.ApplyAutoStart(true);
    }

    private static void UninstallFallback()
    {
        UpdateManager.ApplyAutoStart(false);
        RemoveShortcut();
        RemoveUninstallEntry();

        string batch = Path.Combine(Path.GetTempPath(), "pill-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(batch,
            "@echo off\r\n" +
            "ping -n 4 127.0.0.1 >nul\r\n" +
            "rmdir /s /q \"" + RootDirectory + "\\app\" >nul 2>&1\r\n" +
            "del \"" + Path.Combine(RootDirectory, "current.json") + "\" >nul 2>&1\r\n" +
            "del \"" + Path.Combine(RootDirectory, "device.json") + "\" >nul 2>&1\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"\" /min \"" + batch + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static void SetAutoStart(bool enabled) => UpdateManager.ApplyAutoStart(enabled);

    public static bool GetAutoStart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    private static void RemoveShortcut()
    {
        try
        {
            string shortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", AppName + ".lnk");
            if (File.Exists(shortcut)) File.Delete(shortcut);
        }
        catch { }
    }

    private static void RemoveUninstallEntry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
        catch { }
    }
}
