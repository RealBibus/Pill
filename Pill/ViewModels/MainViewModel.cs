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
    public StatusService Status { get; }
    public WeatherService Weather { get; } = new();
    public ObservableCollection<ShelfItem> Shelf { get; } = new();

    private readonly AppSettings _settings = SettingsService.Load();

    private bool _ambientEnabled;
    private bool _autoStart;
    private bool _autoUpdate;
    private bool _autoExpand;
    private int _autoHideMs;
    private double _equalizerGain;
    private double _waveformGain;
    private string _updateChannel = "beta";
    private string _weatherCity = "";
    private string _weatherCountry = "";
    private string _weatherUnit = "C";
    private bool _useDeviceLocation;
    private bool _hideInGames = true;
    private bool _suspended;
    private bool _expanded;
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

    public string WeatherCity
    {
        get => _weatherCity;
        set
        {
            if (!Set(ref _weatherCity, value)) return;
            _settings.WeatherCity = value;
            SettingsService.Save(_settings);
        }
    }

    public string WeatherCountry
    {
        get => _weatherCountry;
        set
        {
            if (!Set(ref _weatherCountry, value)) return;
            _settings.WeatherCountry = value;
            SettingsService.Save(_settings);
        }
    }

    public string WeatherUnit
    {
        get => _weatherUnit;
        set
        {
            if (!Set(ref _weatherUnit, value)) return;
            _settings.WeatherUnit = value;
            SettingsService.Save(_settings);
            Weather.Reload();
        }
    }

    public bool UseDeviceLocation
    {
        get => _useDeviceLocation;
        set
        {
            if (!Set(ref _useDeviceLocation, value)) return;
            _settings.UseDeviceLocation = value;
            SettingsService.Save(_settings);
            Weather.Reload();
        }
    }

    public bool HideInGames
    {
        get => _hideInGames;
        set
        {
            if (!Set(ref _hideInGames, value)) return;
            _settings.HideInGames = value;
            SettingsService.Save(_settings);
        }
    }

    public Color AmbientColor
    {
        get => _ambientColor;
        private set => Set(ref _ambientColor, value);
    }

    public MainViewModel()
    {
        Status = new StatusService(_settings.ListeningSeconds, SaveListeningSeconds);
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
        _weatherCity = _settings.WeatherCity;
        _weatherCountry = _settings.WeatherCountry;
        _weatherUnit = _settings.WeatherUnit == "F" ? "F" : "C";
        _useDeviceLocation = _settings.UseDeviceLocation;
        _hideInGames = _settings.HideInGames;

        Equalizer.Sensitivity = _equalizerGain;
        Equalizer.WaveformGain = _waveformGain;
        UpdateManager.Service.Channel = _updateChannel;
        UpdateManager.AutoEnabled = _autoUpdate;
        if (_ambientEnabled) StartAmbient();
    }

    private void StartAmbient() => AmbientColor = CoverPaletteService.FromCover(Media.Cover);

    private void StopAmbient() => AmbientColor = AmbientColorService.BaseAccent;

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemMediaService.Title) or nameof(SystemMediaService.IsPlaying))
        {
            Raise(nameof(HasMusic));
            UpdateAudio();
            Status.UpdateMedia(Media.IsPlaying, Media.Title);
        }
        else if (e.PropertyName == nameof(SystemMediaService.Cover) && _ambientEnabled)
        {
            AmbientColor = CoverPaletteService.FromCover(Media.Cover);
        }
    }

    private void OnShelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(HasShelfItems));
        Raise(nameof(ShelfCount));
    }

    private void SaveListeningSeconds(long seconds)
    {
        if (_settings.ListeningSeconds == seconds) return;
        _settings.ListeningSeconds = seconds;
        SettingsService.Save(_settings);
    }

    public void Start()
    {
        Status.Start();
        Weather.Start();
        Status.UpdateMedia(Media.IsPlaying, Media.Title);
        _ = Media.StartAsync();
    }

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        UpdateAudio();
    }

    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        Equalizer.SetPaused(suspended);
        Status.SetSuspended(suspended);
        Media.SetPaused(suspended);
        UpdateAudio();
    }

    private void UpdateAudio() => Equalizer.SetRenderingEnabled(_expanded && !_suspended && HasMusic);

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

    public void Dispose()
    {
        Shelf.CollectionChanged -= OnShelfChanged;
        Media.PropertyChanged -= OnMediaPropertyChanged;
        StopAmbient();
        Weather.Dispose();
        Status.Dispose();
        Equalizer.Dispose();
        Media.Dispose();
    }
}
