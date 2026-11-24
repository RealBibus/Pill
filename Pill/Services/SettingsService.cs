using System;
using System.IO;
using System.Text.Json;

namespace Pill.Services;

public sealed class AppSettings
{
    public bool AmbientColorEnabled { get; set; }
    public bool AutoStart { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public string UpdateChannel { get; set; } = "beta";
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pill");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch { }
    }
}
