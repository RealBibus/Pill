using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace Pill.Installer;

public static class Installer
{
    private const string AppName = "Pill";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pill";

    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string AppDirectory => Path.Combine(RootDirectory, "app");

    public static string SourceDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string Version
    {
        get
        {
            string exe = Path.Combine(SourceDirectory, "Pill.exe");
            if (File.Exists(exe))
            {
                string? fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                if (!string.IsNullOrWhiteSpace(fileVersion)) return TrimVersion(fileVersion);
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static void Install(bool startAfter, IProgress<double>? progress)
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AppDirectory);
        StopRunningApp();

        if (!PathsEqual(SourceDirectory, AppDirectory))
        {
            string[] files = Directory.GetFiles(SourceDirectory, "*", SearchOption.AllDirectories)
                .Where(IncludeFile)
                .ToArray();

            for (int i = 0; i < files.Length; i++)
            {
                string relative = Path.GetRelativePath(SourceDirectory, files[i]);
                string destination = Path.Combine(AppDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(files[i], destination, true);
                progress?.Report((double)(i + 1) / Math.Max(1, files.Length));
            }
        }
        else
        {
            progress?.Report(1);
        }

        WriteCurrent();
        CreateShortcut();
        CreateUninstallEntry();
        ApplyAutoStart(ReadAutoStart());

        if (startAfter)
        {
            string exe = Path.Combine(AppDirectory, "Pill.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppDirectory
                });
            }
        }
    }

    public static void Uninstall(IProgress<double>? progress)
    {
        StopRunningApp();
        ApplyAutoStart(false);
        RemoveShortcut();
        RemoveUninstallEntry();
        progress?.Report(1);

        string batch = Path.Combine(Path.GetTempPath(), "pill-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(batch,
            "@echo off\r\n" +
            "ping -n 4 127.0.0.1 >nul\r\n" +
            "rmdir /s /q \"" + AppDirectory + "\" >nul 2>&1\r\n" +
            "for /d %%D in (\"" + RootDirectory + "\\app-*\") do rmdir /s /q \"%%D\" >nul 2>&1\r\n" +
            "del \"" + Path.Combine(RootDirectory, "current.json") + "\" >nul 2>&1\r\n" +
            "del \"" + Path.Combine(RootDirectory, "device.json") + "\" >nul 2>&1\r\n" +
            "del \"" + Path.Combine(RootDirectory, "healthy.ok") + "\" >nul 2>&1\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"\" /min \"" + batch + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static bool IncludeFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("current.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("device.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("healthy.ok", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("staged.ok", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("manifest.sig", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".dl", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private static void StopRunningApp()
    {
        foreach (Process process in Process.GetProcessesByName("Pill"))
        {
            try
            {
                string path = process.MainModule?.FileName ?? string.Empty;
                if (path.StartsWith(AppDirectory, StringComparison.OrdinalIgnoreCase)) process.Kill();
            }
            catch { }
        }
    }

    private static bool ReadAutoStart()
    {
        try
        {
            string path = Path.Combine(RootDirectory, "settings.json");
            if (!File.Exists(path)) return true;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("AutoStart", out JsonElement value) &&
                value.ValueKind == JsonValueKind.False) return false;
            return true;
        }
        catch { return true; }
    }

    private static void WriteCurrent()
    {
        var state = new CurrentState
        {
            version = Version,
            dir = "app",
            previous = null
        };
        string path = Path.Combine(RootDirectory, "current.json");
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, true);
    }

    private sealed class CurrentState
    {
        public string version { get; set; } = "";
        public string dir { get; set; } = "";
        public string? previous { get; set; }
    }

    private static string TrimVersion(string version)
    {
        string text = version.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+' });
        return cut > 0 ? text.Substring(0, cut) : text;
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
            string target = Path.Combine(AppDirectory, "Pill.exe");

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            Type shortcutType = shortcut.GetType();

            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        catch { }
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

    private static void CreateUninstallEntry()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath)!;
            string uninstaller = Path.Combine(AppDirectory, "Pill.Installer.exe");
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "RealBibus");
            key.SetValue("InstallLocation", AppDirectory);
            key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void RemoveUninstallEntry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); }
        catch { }
    }

    private static void ApplyAutoStart(bool on)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            {
                if (key != null)
                {
                    string target = Path.Combine(AppDirectory, "Pill.exe");
                    if (on) key.SetValue(AppName, "\"" + target + "\"", RegistryValueKind.String);
                    else key.DeleteValue(AppName, false);
                }
            }
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, true);
            approved?.DeleteValue(AppName, false);
        }
        catch { }
    }
}
