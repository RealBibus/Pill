using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Pill.Services;

public sealed record UpdateInfo(Version Version, string Url, string Name);

public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/RealBibus/Pill/releases?per_page=10";

    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "Pill-Updater");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            string json = await Client.GetStringAsync(ApiUrl);
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                try
                {
                    bool beta = release.GetProperty("prerelease").GetBoolean();
                    string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
                    if (!beta && tag.IndexOf("beta", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Version? version = ParseVersion(tag);
                    if (version is null || version.CompareTo(CurrentVersion) <= 0) continue;

                    foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? string.Empty;
                        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        string url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        return new UpdateInfo(version, url, name);
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static Version? ParseVersion(string tag)
    {
        Match match = Regex.Match(tag, @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out Version? version) ? version : null;
    }

    public static async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        string root = Path.Combine(Path.GetTempPath(), "PillUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        byte[] archive = await Client.GetByteArrayAsync(info.Url);
        string zipPath = Path.Combine(root, "update.zip");
        File.WriteAllBytes(zipPath, archive);

        string extracted = Path.Combine(root, "files");
        ZipFile.ExtractToDirectory(zipPath, extracted, true);

        string? executable = Directory.GetFiles(extracted, "Pill.exe", SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
        if (executable is null) throw new FileNotFoundException("Pill.exe not found in update archive", zipPath);

        string source = Path.GetDirectoryName(executable)!;
        string target = InstallerService.IsInstalled
            ? InstallerService.InstallDirectory
            : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = source
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start);
    }

    public static void ApplyUpdate(string source, string target, int oldPid)
    {
        try
        {
            Process old = Process.GetProcessById(oldPid);
            if (!old.HasExited) old.WaitForExit(30000);
        }
        catch { }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (string.Equals(relative, "settings.json", StringComparison.OrdinalIgnoreCase)) continue;

            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }

        Process.Start(new ProcessStartInfo(Path.Combine(target, "Pill.exe"))
        {
            UseShellExecute = true,
            WorkingDirectory = target
        });
    }
}
