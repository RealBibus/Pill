using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace Pill.Services;

public static class UpdateConfig
{
    public const string GitHubOwner = "RealBibus";
    public const string GitHubRepo = "Pill";
    public const string PublicKeySpki =
    "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEArJVKkF0ORm77x3LQiseMrzTEyuVQygh4N9f0O84uqm2YuYgSyHdoaifZqFKu9xHNZB2K83mz2YrCF/KOJZUdCwrkhakT1zuK+iSUBVIJGkHXkLbNj7ksJg0HSJ3o/6hd17rX6dK90cTGlPx0FRxKGbNuJ/wBDnN5ooWA8fxANv32rkrOLEc4/O7Y/13XK1py6SEuxN9CrRd47gSyFXQelKcgnBi58q/9HhsQCqRZdnYqrUhlEr4Dmm8pTBKgac6c7zioPp1wle7hFL5pIDcQBgCEo7IWaq/Q3YOSSP1k+zRneeTtp/RrQUpzTNt0XisrOIF2mqGEhdkWdbgQMoOL5BFlnItWZ0WyhxkAnn2uVh8tfreYRgS1TmmNyk/MsUnMMUqxN4DZ88V6XYofbnSwe6/P2u1YsbFMbeAWpny/T9uO+5tfCrjAjoZhF9H7a2yfzDMcbWeuWxilJsjLBvtRNmS+PDUpREBR4UFw+wlxdyBFCCJUa+2HIoXl50AMpoBlAgMBAAE=";
    public const string AppExeName = "Pill.exe";

    public static string RootDir
    {
        get
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string parent = Directory.GetParent(baseDir)?.FullName ?? baseDir;
            return File.Exists(Path.Combine(parent, "current.json")) ? parent : baseDir;
        }
    }
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";
}

public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("rollout_percent")] public int RolloutPercent { get; set; } = 100;
    [JsonPropertyName("release_notes")] public string ReleaseNotes { get; set; } = "";
    [JsonPropertyName("entry")] public string Entry { get; set; } = "Pill.exe";
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
}

public sealed class CurrentState
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("dir")] public string Dir { get; set; } = "";
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

public sealed class UpdateInfo
{
    public UpdateManifest Manifest { get; init; } = new();
    public IReadOnlyDictionary<string, string> AssetUrls { get; init; } = new Dictionary<string, string>();
    public string ReleaseUrl { get; init; } = "";
}

public static class UpdateCrypto
{
    public static bool VerifyManifest(byte[] data, byte[] sig)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateConfig.PublicKeySpki), out _);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static int DeviceBucket(string deviceId)
    {
        using var sha = SHA256.Create();
        byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
        return (int)(BitConverter.ToUInt32(h, 0) % 100);
    }
}

public sealed class UpdateService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Channel { get; set; } = "stable";
    public string Root => UpdateConfig.RootDir;
    public string CurrentAppDir => Path.Combine(Root, ReadCurrent()?.Dir ?? "");
    public string CurrentVersion => ReadCurrent()?.Version ?? AssemblyVersion();

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Pill-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private static string AssemblyVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public string DeviceId()
    {
        string p = Path.Combine(Root, "device.json");
        try
        {
            if (File.Exists(p))
            {
                string? id = JsonSerializer.Deserialize<DeviceDoc>(File.ReadAllText(p))?.id;
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
            string fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(p, JsonSerializer.Serialize(new DeviceDoc { id = fresh }));
            return fresh;
        }
        catch
        {
            return Environment.MachineName + Environment.UserName;
        }
    }

    private sealed class DeviceDoc { public string id { get; set; } = ""; }

    public CurrentState? ReadCurrent()
    {
        string p = Path.Combine(Root, "current.json");
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<CurrentState>(File.ReadAllText(p)); }
        catch { return null; }
    }

    public void WriteCurrent(CurrentState s)
    {
        string p = Path.Combine(Root, "current.json");
        string tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, p, true);
    }

    public async Task<UpdateInfo?> CheckAsync()
    {
        List<ReleaseDoc>? releases = await GetReleasesAsync();
        if (releases == null || releases.Count == 0) return null;

        ReleaseDoc? release = releases
            .Where(r => !r.draft && (Channel == "beta" ? r.prerelease || TagHasBeta(r.tag_name) : !r.prerelease))
            .OrderByDescending(r => r.tag_name, new VersionOrder())
            .FirstOrDefault();
        if (release == null) return null;

        Dictionary<string, string> assets = release.assets.ToDictionary(a => a.name, a => a.browser_download_url);
        if (!assets.TryGetValue("manifest.json", out string? manifestUrl) ||
            !assets.TryGetValue("manifest.sig", out string? signatureUrl)) return null;

        byte[] manifestBytes = await _http.GetByteArrayAsync(manifestUrl);
        byte[] signatureBytes = await _http.GetByteArrayAsync(signatureUrl);
        if (!UpdateCrypto.VerifyManifest(manifestBytes, signatureBytes)) return null;

        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version)) return null;
        if (!string.IsNullOrWhiteSpace(manifest.Channel) &&
            !string.Equals(manifest.Channel, Channel, StringComparison.OrdinalIgnoreCase)) return null;
        if (CompareVersion(manifest.Version, CurrentVersion) <= 0) return null;

        int bucket = UpdateCrypto.DeviceBucket(DeviceId());
        if (Channel != "beta" && bucket >= manifest.RolloutPercent) return null;

        string notes = PlainNotes(release.body);
        manifest.ReleaseNotes = string.IsNullOrWhiteSpace(notes) ? PlainNotes(manifest.ReleaseNotes) : notes;

        return new UpdateInfo
        {
            Manifest = manifest,
            AssetUrls = assets,
            ReleaseUrl = release.html_url
        };
    }

    private static bool TagHasBeta(string tag) =>
        tag.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;

    private async Task<List<ReleaseDoc>?> GetReleasesAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{UpdateConfig.GitHubOwner}/{UpdateConfig.GitHubRepo}/releases?per_page=20";
            using HttpResponseMessage resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<List<ReleaseDoc>>(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    public static string PlainNotes(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        string text = markdown.Replace("\r", "");
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"^[ \t]*#{1,6}[ \t]*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[ \t]*[-*+][ \t]+", "• ", RegexOptions.Multiline);
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private sealed class ReleaseDoc
    {
        public bool prerelease { get; set; }
        public bool draft { get; set; }
        public string tag_name { get; set; } = "";
        public string body { get; set; } = "";
        public string html_url { get; set; } = "";
        public List<AssetDoc> assets { get; set; } = new();
    }

    private sealed class AssetDoc
    {
        public string name { get; set; } = "";
        public string browser_download_url { get; set; } = "";
    }

    private sealed class VersionOrder : IComparer<string>
    {
        public int Compare(string? a, string? b) => CompareVersion(a ?? "", b ?? "");
    }

    public static int CompareVersion(string a, string b)
    {
        int[] pa = Parts(a);
        int[] pb = Parts(b);
        for (int i = 0; i < pa.Length; i++)
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        return 0;
    }

    private static int[] Parts(string version)
    {
        var numbers = new int[4];
        if (string.IsNullOrWhiteSpace(version)) return numbers;
        string text = version.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut > 0) text = text.Substring(0, cut);
        string[] parts = text.Split('.');
        for (int i = 0; i < numbers.Length && i < parts.Length; i++) int.TryParse(parts[i], out numbers[i]);
        return numbers;
    }

    public async Task<string> StageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        string target = Path.Combine(Root, "app-" + info.Manifest.Version);
        string marker = Path.Combine(target, "staged.ok");
        if (File.Exists(marker) && File.Exists(Path.Combine(target, info.Manifest.Entry))) return target;

        if (Directory.Exists(target))
        {
            try { Directory.Delete(target, true); } catch { }
        }
        Directory.CreateDirectory(target);

        string source = CurrentAppDir;
        int done = 0;
        foreach (ManifestFile file in info.Manifest.Files)
        {
            string relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            string dst = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            string src = Path.Combine(source, relative);

            if (File.Exists(src) && UpdateCrypto.Sha256File(src) == file.Sha256)
            {
                File.Copy(src, dst, true);
            }
            else
            {
                if (!info.AssetUrls.TryGetValue(file.Asset, out string? url))
                    throw new InvalidOperationException("asset missing: " + file.Asset);
                string tmp = dst + ".dl";
                await using (var fs = File.Create(tmp))
                await using (Stream stream = await _http.GetStreamAsync(url))
                    await stream.CopyToAsync(fs);
                if (UpdateCrypto.Sha256File(tmp) != file.Sha256)
                {
                    File.Delete(tmp);
                    throw new InvalidOperationException("hash mismatch: " + file.Path);
                }
                File.Move(tmp, dst, true);
            }

            done++;
            progress?.Report((double)done / Math.Max(1, info.Manifest.Files.Count));
        }

        if (!File.Exists(Path.Combine(target, info.Manifest.Entry)))
            throw new InvalidOperationException("entry missing after staging: " + info.Manifest.Entry);

        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        return target;
    }

    public void ApplyStaged(string stagedDir)
    {
        CurrentState? cur = ReadCurrent();
        WriteCurrent(new CurrentState
        {
            Version = Path.GetFileName(stagedDir).Replace("app-", ""),
            Dir = Path.GetFileName(stagedDir),
            Previous = cur?.Dir
        });
    }

    public void MarkHealthy()
    {
        try
        {
            string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            File.WriteAllText(Path.Combine(dir, "healthy.ok"), DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }
}

public static class UpdateManager
{
    public static UpdateService Service { get; } = new();

    public static event Action<UpdateInfo>? UpdateAvailable;
    public static event Action<double>? StageProgress;
    public static event Action? StageCompleted;
    public static event Action? FadeOutRequested;

    public static UpdateInfo? Pending { get; private set; }
    public static string? StagedDir { get; private set; }
    public static bool Deferred { get; private set; }
    public static bool IsStaged => StagedDir != null;
    public static bool AutoEnabled { get; set; } = true;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DispatcherTimer? _timer;

    public static bool UpdatesEnabled => File.Exists(Path.Combine(UpdateConfig.RootDir, "current.json"));

    public static void Attach(Application app)
    {
        AppSettings settings = SettingsService.Load();
        Service.Channel = settings.UpdateChannel;
        AutoEnabled = settings.AutoUpdate;
        Service.MarkHealthy();
        ApplyAutoStart(settings.AutoStart);

        SystemEvents.SessionEnding += (_, _) => ApplyStagedIfAny();
        app.Exit += (_, _) => ApplyStagedIfAny();

        if (!UpdatesEnabled) return;

        AdoptStagedOnStartup();
        CleanupOldVersions();

        if (!AutoEnabled) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(2) };
        _timer.Tick += async (_, _) => await CheckNowAsync();
        _timer.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await CheckNowAsync();
            }
            catch { }
        });
    }

    public static async Task CheckNowAsync()
    {
        if (!UpdatesEnabled) return;
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            UpdateInfo? info = await Service.CheckAsync();
            if (info == null)
            {
                if (StagedDir == null) Pending = null;
                return;
            }
            Pending = info;
            UpdateAvailable?.Invoke(info);
            await StageCoreAsync(info, null);
        }
        catch { }
        finally { Gate.Release(); }
    }

    public static async Task StagePendingAsync(IProgress<double>? progress = null)
    {
        if (Pending == null) return;
        await Gate.WaitAsync();
        try { await StageCoreAsync(Pending, progress); }
        finally { Gate.Release(); }
    }

    private static async Task<string> StageCoreAsync(UpdateInfo info, IProgress<double>? progress)
    {
        var bridge = new Progress<double>(value =>
        {
            progress?.Report(value);
            StageProgress?.Invoke(value);
        });
        StagedDir = await Service.StageAsync(info, bridge);
        StageCompleted?.Invoke();
        return StagedDir;
    }

    public static void MarkDeferred() => Deferred = true;

    public static void ApplyStagedIfAny()
    {
        if (StagedDir == null) return;
        try { Service.ApplyStaged(StagedDir); } catch { }
        StagedDir = null;
    }

    private static void AdoptStagedOnStartup()
    {
        try
        {
            CurrentState? cur = Service.ReadCurrent();
            if (cur == null || string.IsNullOrWhiteSpace(cur.Dir)) return;

            string? best = null;
            string bestVersion = cur.Version;
            foreach (string dir in Directory.GetDirectories(UpdateConfig.RootDir, "app-*"))
            {
                if (!File.Exists(Path.Combine(dir, "staged.ok"))) continue;
                string version = Path.GetFileName(dir).Replace("app-", "");
                if (UpdateService.CompareVersion(version, bestVersion) <= 0) continue;
                if (!File.Exists(Path.Combine(dir, UpdateConfig.AppExeName))) continue;
                best = dir;
                bestVersion = version;
            }
            if (best != null) Service.ApplyStaged(best);
        }
        catch { }
    }

    private static void CleanupOldVersions()
    {
        try
        {
            CurrentState? cur = Service.ReadCurrent();
            if (cur == null || string.IsNullOrWhiteSpace(cur.Dir)) return;

            string? keepStaged = null;
            string keepVersion = cur.Version;
            foreach (string dir in Directory.GetDirectories(UpdateConfig.RootDir, "app-*"))
            {
                if (!File.Exists(Path.Combine(dir, "staged.ok"))) continue;
                string version = Path.GetFileName(dir).Replace("app-", "");
                if (UpdateService.CompareVersion(version, keepVersion) <= 0) continue;
                keepStaged = Path.GetFileName(dir);
                keepVersion = version;
            }

            foreach (string dir in Directory.GetDirectories(UpdateConfig.RootDir, "app-*"))
            {
                string name = Path.GetFileName(dir);
                if (name == cur.Dir || name == cur.Previous || name == keepStaged) continue;
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    public static void RestartWithUpdate()
    {
        ApplyStagedIfAny();
        RestartApp();
    }

    public static async Task RestartWithVeilAsync()
    {
        FadeOutRequested?.Invoke();
        await Task.Delay(1500);
        RestartWithUpdate();
    }

    public static void RestartApp()
    {
        string launcher = Path.Combine(UpdateConfig.RootDir, "Launcher.exe");
        CurrentState? cur = Service.ReadCurrent();
        string? target = null;

        if (File.Exists(launcher)) target = launcher;
        else if (cur != null && !string.IsNullOrWhiteSpace(cur.Dir))
            target = Path.Combine(UpdateConfig.RootDir, cur.Dir, UpdateConfig.AppExeName);

        if (target == null || !File.Exists(target)) target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
            target = Path.Combine(AppContext.BaseDirectory, UpdateConfig.AppExeName);

        if (File.Exists(target))
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? AppContext.BaseDirectory
            });
        }
        Application.Current.Shutdown();
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AutoStartValueName = "Pill";

    public static string AutoStartTarget()
    {
        string root = UpdateConfig.RootDir;
        string launcher = Path.Combine(root, "Launcher.exe");
        if (File.Exists(launcher) && File.Exists(Path.Combine(root, "current.json"))) return launcher;

        CurrentState? cur = Service.ReadCurrent();
        if (cur != null && !string.IsNullOrWhiteSpace(cur.Dir))
        {
            string installed = Path.Combine(root, cur.Dir, UpdateConfig.AppExeName);
            if (File.Exists(installed)) return installed;
        }

        string? process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process) && File.Exists(process)) return process;

        return Path.Combine(AppContext.BaseDirectory, UpdateConfig.AppExeName);
    }

    public static void ApplyAutoStart(bool on)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            {
                if (key != null)
                {
                    if (on) key.SetValue(AutoStartValueName, "\"" + AutoStartTarget() + "\"", RegistryValueKind.String);
                    else key.DeleteValue(AutoStartValueName, false);
                }
            }
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, true);
            approved?.DeleteValue(AutoStartValueName, false);
        }
        catch { }
    }
}
