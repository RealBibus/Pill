using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
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

    private bool _ambientEnabled;
    private bool _autoStart;
    private bool _autoUpdate;
    private bool _autoExpand;
    private int _autoHideMs;
    private double _equalizerGain;
    private double _waveformGain;
    private string _updateChannel = "beta";
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
            if (value) StartAmbient();
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
            UpdateManager.AutoEnabled = value;
        }
    }

    public bool AutoExpand
    {
        get => _autoExpand;
        set
        {
            if (!Set(ref _autoExpand, value)) return;
            _settings.AutoExpand = value;
            SettingsService.Save(_settings);
        }
    }

    public int AutoHideMs
    {
        get => _autoHideMs;
        set
        {
            if (!Set(ref _autoHideMs, value)) return;
            _settings.AutoHideMs = value;
            SettingsService.Save(_settings);
        }
    }

    public double EqualizerGain
    {
        get => _equalizerGain;
        set
        {
            if (!Set(ref _equalizerGain, value)) return;
            Equalizer.Sensitivity = value;
            _settings.EqualizerGain = value;
            SettingsService.Save(_settings);
        }
    }

    public double WaveformGain
    {
        get => _waveformGain;
        set
        {
            if (!Set(ref _waveformGain, value)) return;
            Equalizer.WaveformGain = value;
            _settings.WaveformGain = value;
            SettingsService.Save(_settings);
        }
    }

    public string UpdateChannel
    {
        get => _updateChannel;
        set
        {
            if (!Set(ref _updateChannel, value)) return;
            _settings.UpdateChannel = value;
            SettingsService.Save(_settings);
            UpdateManager.Service.Channel = value;
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

        _ambientEnabled = _settings.AmbientColorEnabled;
        _autoStart = _settings.AutoStart;
        _autoUpdate = _settings.AutoUpdate;
        _autoExpand = _settings.AutoExpand;
        _autoHideMs = _settings.AutoHideMs;
        _equalizerGain = Math.Clamp(_settings.EqualizerGain, 0.4, 2.5);
        _waveformGain = Math.Clamp(_settings.WaveformGain, 0.4, 2.5);
        _updateChannel = string.IsNullOrWhiteSpace(_settings.UpdateChannel) ? "beta" : _settings.UpdateChannel;

        Equalizer.Sensitivity = _equalizerGain;
        Equalizer.WaveformGain = _waveformGain;
        UpdateManager.Service.Channel = _updateChannel;
        UpdateManager.AutoEnabled = _autoUpdate;
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
    }

    public void Install() => InstallerService.LaunchInstaller(false);

    public void Uninstall() => InstallerService.LaunchInstaller(true);

    public async Task<bool> CheckForUpdatesAsync()
    {
        await UpdateManager.CheckNowAsync();
        return UpdateManager.Pending != null;
    }

    public void ClearShelf()
    {
        var items = new List<ShelfItem>(Shelf);
        foreach (ShelfItem item in items) Shelf.Remove(item);
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
            if (Shelf.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

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
        StopAmbient();
        _activeWindowColors.Dispose();
        Status.Dispose();
        Equalizer.Dispose();
        Media.Dispose();
    }
}
