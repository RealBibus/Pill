using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace Pill.Services;

public static class InstallerService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pill";
    private const string AppName = "Pill";

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pill", "app");

    public static string InstalledExecutable => Path.Combine(InstallDirectory, "Pill.exe");

    public static bool IsInstalled => File.Exists(InstalledExecutable);

    public static string CurrentExecutable => Environment.ProcessPath ?? InstalledExecutable;

    public static void Install()
    {
        string source = Path.GetDirectoryName(CurrentExecutable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return;

        Directory.CreateDirectory(InstallDirectory);
        string[] patterns = { "*.exe", "*.dll", "*.json", "*.config", "*.pri" };
        foreach (string pattern in patterns)
        {
            foreach (string file in Directory.GetFiles(source, pattern, SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, Path.GetFileName(SettingsService.FilePath), StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(InstallDirectory, name), true);
            }
        }

        CreateShortcut();
        WriteUninstallEntry();

        AppSettings settings = SettingsService.Load();
        SetAutoStart(settings.AutoStart);
        if (settings.AutoStart && IsInstalled)
            Process.Start(new ProcessStartInfo(InstalledExecutable) { UseShellExecute = true, WorkingDirectory = InstallDirectory });
    }

    public static void Uninstall()
    {
        SetAutoStart(false);
        RemoveShortcut();
        RemoveUninstallEntry();

        string directory = InstallDirectory;
        if (!Directory.Exists(directory)) return;

        string batch = Path.Combine(Path.GetTempPath(), "pill-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(batch,
            "@echo off\r\n" +
            "ping -n 4 127.0.0.1 >nul\r\n" +
            "rmdir /s /q \"" + directory + "\"\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"\" /min \"" + batch + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static void SetAutoStart(bool enabled)
    {
        string executable = IsInstalled ? InstalledExecutable : CurrentExecutable;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            if (enabled)
                key.SetValue(AppName, "\"" + executable + "\"");
            else
                key.DeleteValue(AppName, false);
        }
        catch { }
    }

    public static bool GetAutoStart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateShortcut()
    {
        try
        {
            string programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            Directory.CreateDirectory(programs);
            string shortcutPath = Path.Combine(programs, AppName + ".lnk");

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            Type shortcutType = shortcut.GetType();

            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                new object[] { InstalledExecutable });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { InstallDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                new object[] { InstalledExecutable + ",0" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        catch { }
    }

    private static void RemoveShortcut()
    {
        try
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", AppName + ".lnk");
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }
        catch { }
    }

    private static void WriteUninstallEntry()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey)!;
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
            key.SetValue("Publisher", "RealBibus");
            key.SetValue("InstallLocation", InstallDirectory);
            key.SetValue("UninstallString", "\"" + InstalledExecutable + "\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void RemoveUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }
        catch { }
    }
}
