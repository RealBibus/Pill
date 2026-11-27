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

public sealed class SystemMediaService : ObservableObject, IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _version;
    private bool _disposed;
    private bool _paused;
    private readonly DispatcherTimer _timelineTimer;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _reportedPosition;
    private DateTime _positionStamp;
    private bool _positionPrimed;

    private string _title = "Nothing playing";
    private string _artist = "Hover or click the island";
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
        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timelineTimer.Tick += (_, _) => UpdateTimeline();
        _timelineTimer.Start();
    }

    public void SetPaused(bool paused)
    {
        if (_disposed || _paused == paused) return;
        _paused = paused;
        if (paused) _timelineTimer.Stop();
        else
        {
            _timelineTimer.Start();
            _ = RefreshAsync();
        }
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    private void UpdateTimeline()
    {
        if (_paused) return;
        var session = _session;
        if (session is null || _disposed)
        {
            if (_position != TimeSpan.Zero || _duration != TimeSpan.Zero)
            {
                _position = TimeSpan.Zero;
                _duration = TimeSpan.Zero;
                _positionPrimed = false;
                RaiseProgress();
            }
            return;
        }

        try
        {
            var timeline = session.GetTimelineProperties();
            TimeSpan reported = timeline.Position;
            TimeSpan length = timeline.EndTime - timeline.StartTime;
            TimeSpan previousDuration = _duration;
            if (length > TimeSpan.Zero) _duration = length;
            DateTime now = DateTime.UtcNow;

            if (!_positionPrimed)
            {
                _position = reported;
                _reportedPosition = reported;
                _positionStamp = now;
                _positionPrimed = true;
            }
            else
            {
                TimeSpan expected = _isPlaying ? _reportedPosition + (now - _positionStamp) : _reportedPosition;
                if (_duration > TimeSpan.Zero && expected > _duration) expected = _duration;

                bool restarted = reported < TimeSpan.FromSeconds(1) && _duration > TimeSpan.FromSeconds(1) &&
                                 (expected >= _duration || Math.Abs((_duration - previousDuration).TotalSeconds) > 1d);
                bool seeked = reported > TimeSpan.Zero &&
                              Math.Abs((reported - expected).TotalSeconds) > 1.5d;

                if (!_isPlaying || restarted || seeked)
                {
                    _position = reported;
                    _reportedPosition = reported;
                    _positionStamp = now;
                }
                else
                {
                    _position = expected;
                }
            }
            RaiseProgress();
        }
        catch { }
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
            if (_disposed || _paused) return;

            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            Attach(_manager.GetCurrentSession());
        }
        catch { }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        if (_disposed || _paused || _manager is null) return;
        Attach(_manager.GetCurrentSession());
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        if (_disposed || _paused || _manager is null) return;
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
        _positionPrimed = false;

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
        MediaPropertiesChangedEventArgs args)
    {
        if (!_paused) _ = RefreshAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        if (!_paused) _ = RefreshAsync();
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        if (!_paused) UpdateTimeline();
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _paused) return;
        int version = ++_version;
        var session = _session;

        if (session is null)
        {
            Title = "Nothing playing";
            Artist = "Start Spotify, a browser or Yandex Music";
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

            string title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown track" : props.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props.Artist)
                ? (string.IsNullOrWhiteSpace(props.Subtitle) ? "Unknown artist" : props.Subtitle.Trim())
                : props.Artist.Trim();

            if (!string.Equals(Title, title, StringComparison.Ordinal))
                _positionPrimed = false;

            Title = title;
            Artist = artist;
            Album = props.AlbumTitle ?? "";
            Source = FriendlySource(session.SourceAppUserModelId);

            ImageSource? art = await LoadCoverAsync(props.Thumbnail);
            if (_disposed || version != _version) return;
            Cover = art;
        }
        catch { }

        try
        {
            var info = session.GetPlaybackInfo();
            if (_disposed || version != _version) return;

            bool wasPlaying = _isPlaying;
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (wasPlaying != _isPlaying)
            {
                _reportedPosition = _position;
                _positionStamp = DateTime.UtcNow;
                _positionPrimed = true;
            }
            CanPlay = info.Controls.IsPlayEnabled;
            CanPause = info.Controls.IsPauseEnabled;
            CanNext = info.Controls.IsNextEnabled;
            CanPrevious = info.Controls.IsPreviousEnabled;
            UpdateTimeline();
        }
        catch { }
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
            catch { }
        }
        SendMediaKey(0xB3);
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
        SendMediaKey(0xB0);
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
        SendMediaKey(0xB1);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void SendMediaKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero);
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
