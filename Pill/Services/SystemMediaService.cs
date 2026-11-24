using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Pill.Models;
using Windows.Media.Control;

namespace Pill.Services;

/// <summary>
/// Подключение к системному медиа-контролу Windows (SMTC).
/// Работает со Spotify, браузерами, Яндекс Музыкой и любым плеером,
/// который публикует состояние через Windows Media Controls.
/// </summary>
public sealed class SystemMediaService : ObservableObject, IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _version;
    private bool _disposed;
    private readonly DispatcherTimer _timelineTimer;
    private TimeSpan _position;
    private TimeSpan _duration;

    private string _title = "Ничего не играет";
    private string _artist = "Наведите курсор или кликните по островку";
    private string _album = "";
    private string _source = "SYSTEM PLAYER";
    private ImageSource? _cover;
    private bool _isPlaying;
    private bool _canPlay = true;
    private bool _canPause = true;
    private bool _canNext = true;
    private bool _canPrevious = true;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string Album { get => _album; private set => Set(ref _album, value); }
    public string Source { get => _source; private set => Set(ref _source, value); }
    public ImageSource? Cover { get => _cover; private set => Set(ref _cover, value); }
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public bool CanPlay { get => _canPlay; private set => Set(ref _canPlay, value); }
    public bool CanPause { get => _canPause; private set => Set(ref _canPause, value); }
    public bool CanNext { get => _canNext; private set => Set(ref _canNext, value); }
    public bool CanPrevious { get => _canPrevious; private set => Set(ref _canPrevious, value); }

    public double Progress => _duration > TimeSpan.Zero
        ? Math.Clamp(_position.TotalSeconds / _duration.TotalSeconds, 0d, 1d)
        : 0d;
    public string PositionText => FormatTime(_position);
    public string DurationText => FormatTime(_duration);

    public SystemMediaService()
    {
        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _timelineTimer.Tick += (_, _) => UpdateTimeline();
        _timelineTimer.Start();
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    private void UpdateTimeline()
    {
        var session = _session;
        if (session is null || _disposed)
        {
            if (_position != TimeSpan.Zero || _duration != TimeSpan.Zero)
            {
                _position = TimeSpan.Zero;
                _duration = TimeSpan.Zero;
                RaiseProgress();
            }
            return;
        }

        try
        {
            var timeline = session.GetTimelineProperties();
            _position = timeline.Position;
            _duration = timeline.EndTime - timeline.StartTime;
            RaiseProgress();
        }
        catch
        {
            // сессия могла исчезнуть
        }
    }

    private void RaiseProgress()
    {
        Raise(nameof(Progress));
        Raise(nameof(PositionText));
        Raise(nameof(DurationText));
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;

            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            Attach(_manager.GetCurrentSession());
        }
        catch
        {
            // SMTC недоступен — оставляем дефолтные значения и медиа-клавиши как фолбэк.
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        if (_disposed || _manager is null) return;
        Attach(_manager.GetCurrentSession());
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        if (_disposed || _manager is null) return;
        Attach(_manager.GetCurrentSession());
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            _ = RefreshAsync();
            return;
        }

        DetachSession();
        _session = session;

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync();
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => UpdateTimeline();

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        int version = ++_version;
        var session = _session;

        if (session is null)
        {
            Title = "Ничего не играет";
            Artist = "Запустите Spotify, браузер или Яндекс Музыку";
            Album = "";
            Source = "SYSTEM PLAYER";
            Cover = null;
            IsPlaying = false;
            CanNext = CanPrevious = true;
            CanPlay = CanPause = true;
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed || version != _version) return;

            string title = string.IsNullOrWhiteSpace(props.Title) ? "Неизвестный трек" : props.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props.Artist)
                ? (string.IsNullOrWhiteSpace(props.Subtitle) ? "Исполнитель не указан" : props.Subtitle.Trim())
                : props.Artist.Trim();

            Title = title;
            Artist = artist;
            Album = props.AlbumTitle ?? "";
            Source = FriendlySource(session.SourceAppUserModelId);

            ImageSource? art = await LoadCoverAsync(props.Thumbnail);
            if (_disposed || version != _version) return;
            Cover = art;
        }
        catch
        {
            // сессия могла закрыться между вызовами
        }

        try
        {
            var info = session.GetPlaybackInfo();
            if (_disposed || version != _version) return;

            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            CanPlay = info.Controls.IsPlayEnabled;
            CanPause = info.Controls.IsPauseEnabled;
            CanNext = info.Controls.IsNextEnabled;
            CanPrevious = info.Controls.IsPreviousEnabled;
            UpdateTimeline();
        }
        catch
        {
            // ignored
        }
    }

    private static async Task<ImageSource?> LoadCoverAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.AsStream().CopyToAsync(memory);
            if (memory.Length == 0) return null;
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string FriendlySource(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return "SYSTEM PLAYER";
        string id = appUserModelId.ToLowerInvariant();
        if (id.Contains("spotify")) return "SPOTIFY";
        if (id.Contains("yandex")) return "YANDEX MUSIC";
        if (id.Contains("chrome")) return "CHROME";
        if (id.Contains("msedge") || id.Contains("edge")) return "EDGE";
        if (id.Contains("firefox")) return "FIREFOX";
        if (id.Contains("opera")) return "OPERA";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("itunes") || id.Contains("apple")) return "iTUNES";
        if (id.Contains("foo_bar") || id.Contains("foobar")) return "FOOBAR2000";
        return "SYSTEM PLAYER";
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is not null)
        {
            try
            {
                if (await session.TryTogglePlayPauseAsync())
                {
                    await RefreshAsync();
                    return;
                }
            }
            catch { /* fall through to media keys */ }
        }
        SendMediaKey(0xB3); // VK_MEDIA_PLAY_PAUSE
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is not null)
        {
            try
            {
                if (await session.TrySkipNextAsync())
                {
                    await RefreshAsync();
                    return;
                }
            }
            catch { }
        }
        SendMediaKey(0xB0); // VK_MEDIA_NEXT_TRACK
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is not null)
        {
            try
            {
                if (await session.TrySkipPreviousAsync())
                {
                    await RefreshAsync();
                    return;
                }
            }
            catch { }
        }
        SendMediaKey(0xB1); // VK_MEDIA_PREV_TRACK
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void SendMediaKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _version++;
        _timelineTimer.Stop();
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        DetachSession();
    }
}
