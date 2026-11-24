using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Pill.Models;
using Pill.Services;

namespace Pill.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public SystemMediaService Media { get; } = new();
    public AudioEqualizer Equalizer { get; } = new();
    public StatusService Status { get; } = new();
    public ObservableCollection<ShelfItem> Shelf { get; } = new();

    private readonly ActiveWindowColorService _activeWindowColors = new();
    private readonly AppSettings _settings = SettingsService.Load();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private bool _ambientEnabled;
    private bool _autoStart;
    private bool _autoUpdate;
    private Color _ambientColor = AmbientColorService.BaseAccent;

    public bool HasShelfItems => Shelf.Count > 0;
    public int ShelfCount => Shelf.Count;
    public bool HasMusic => Media.Title != "Nothing playing";

    public bool AmbientColorEnabled
    {
        get => _ambientEnabled;
        set
        {
            if (!Set(ref _ambientEnabled, value)) return;
            _settings.AmbientColorEnabled = value;
            SettingsService.Save(_settings);

            if (value)
                StartAmbient();
            else
            {
                StopAmbient();
                AmbientColor = AmbientColorService.BaseAccent;
            }
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;
            _settings.AutoStart = value;
            SettingsService.Save(_settings);
            InstallerService.SetAutoStart(value);
        }
    }

    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (!Set(ref _autoUpdate, value)) return;
            _settings.AutoUpdate = value;
            SettingsService.Save(_settings);
            if (value)
            {
                _updateTimer.Start();
                _ = ApplyBestUpdateAsync();
            }
            else
            {
                _updateTimer.Stop();
            }
        }
    }

    public Color AmbientColor
    {
        get => _ambientColor;
        private set => Set(ref _ambientColor, value);
    }

    public MainViewModel()
    {
        Shelf.CollectionChanged += OnShelfChanged;
        Media.PropertyChanged += OnMediaPropertyChanged;
        _updateTimer.Tick += (_, _) => _ = ApplyBestUpdateAsync();

        _ambientEnabled = _settings.AmbientColorEnabled;
        _autoStart = _settings.AutoStart;
        _autoUpdate = _settings.AutoUpdate;
        if (_ambientEnabled) StartAmbient();
    }

    private void StartAmbient()
    {
        _activeWindowColors.ColorChanged -= OnWindowColorChanged;
        _activeWindowColors.ColorChanged += OnWindowColorChanged;
        _activeWindowColors.Start();
    }

    private void StopAmbient()
    {
        _activeWindowColors.Stop();
        _activeWindowColors.ColorChanged -= OnWindowColorChanged;
    }

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemMediaService.Title) or nameof(SystemMediaService.IsPlaying))
        {
            Raise(nameof(HasMusic));
            Status.UpdateMedia(Media.IsPlaying, Media.Title);
        }
    }

    private void OnWindowColorChanged(object? sender, Color color) => AmbientColor = color;

    private void OnShelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(HasShelfItems));
        Raise(nameof(ShelfCount));
    }

    public void Start()
    {
        Equalizer.Start();
        Status.Start();
        Status.UpdateMedia(Media.IsPlaying, Media.Title);
        _ = Media.StartAsync();
        if (_autoUpdate)
        {
            _updateTimer.Start();
            _ = ApplyBestUpdateAsync();
        }
    }

    public void Install()
    {
        if (InstallerService.IsInstalled)
        {
            System.Windows.MessageBox.Show("Pill is already installed.", "Pill");
            return;
        }

        InstallerService.Install();
        System.Windows.Application.Current?.Shutdown();
    }

    public void Uninstall()
    {
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            "Uninstall Pill?", "Pill",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        InstallerService.Uninstall();
        System.Windows.Application.Current?.Shutdown();
    }

    public async Task CheckForUpdatesAsync()
    {
        UpdateInfo? update = await UpdateService.CheckAsync();
        if (update is null)
        {
            System.Windows.MessageBox.Show("No beta update available.", "Pill");
            return;
        }

        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            "Beta update " + update.Version + " is ready. Install and restart now?",
            "Pill", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (result == System.Windows.MessageBoxResult.Yes)
            await ApplyUpdateAsync(update);
    }

    private async Task ApplyBestUpdateAsync()
    {
        UpdateInfo? update = await UpdateService.CheckAsync();
        if (update is not null) await ApplyUpdateAsync(update);
    }

    private async Task ApplyUpdateAsync(UpdateInfo update)
    {
        try
        {
            await UpdateService.DownloadAndApplyAsync(update);
        }
        catch
        {
            return;
        }

        System.Windows.Application.Current?.Shutdown();
    }

    public string[] AddFiles(IEnumerable<string> paths)
    {
        var added = new List<string>();
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = Path.GetFullPath(raw.Trim('"')); }
            catch { continue; }

            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (Shelf.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            Shelf.Add(new ShelfItem(path));
            added.Add(path);
        }
        return added.ToArray();
    }

    public void Remove(ShelfItem item) => Shelf.Remove(item);

    public void SetColorExclusion(int left, int top, int width, int height)
        => _activeWindowColors.SetExclusion(left, top, width, height);

    public void Dispose()
    {
        Shelf.CollectionChanged -= OnShelfChanged;
        Media.PropertyChanged -= OnMediaPropertyChanged;
        _updateTimer.Stop();
        StopAmbient();
        _activeWindowColors.Dispose();
        Status.Dispose();
        Equalizer.Dispose();
        Media.Dispose();
    }
}
