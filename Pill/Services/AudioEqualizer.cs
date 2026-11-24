using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Pill.Models;

namespace Pill.Services;

public sealed class AudioEqualizer : ObservableObject, IDisposable
{
    public const int BandCount = 7;
    public const int WaveformLength = 256;

    private const int FftPower = 10;
    private const int FftSize = 1 << FftPower;
    private static readonly double[] BandEdges = { 30, 90, 220, 500, 1200, 3000, 7000, 16000 };

    private readonly object _sync = new();
    private readonly DispatcherTimer _retryTimer;
    private readonly float[] _raw = new float[BandCount];
    private readonly float[] _smooth = new float[BandCount];
    private readonly float[] _waveform = new float[WaveformLength];
    private readonly float[] _samples = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];

    private int _sampleCount;
    private int _waveIndex;
    private int _frameCounter;
    private double _phase;
    private double _frameMs = 16.7;
    private TimeSpan _lastFrameTime = TimeSpan.FromMilliseconds(-1);
    private float _peak = 0.05f;
    private float _signalPeak;
    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _enumerator;
    private bool _disposed;
    private bool _isActive;
    private string _status = "WAITING FOR AUDIO";
    private string _deviceName = "";

    public ObservableCollection<double> Levels { get; } = new(Enumerable.Repeat(0.04d, BandCount));

    public bool IsActive
    {
        get => _isActive;
        private set => Set(ref _isActive, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => Set(ref _deviceName, value);
    }

    public AudioEqualizer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FftSize - 1)));

        CompositionTarget.Rendering += OnRendering;

        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += (_, _) => { if (!IsActive) Start(); };
        _retryTimer.Start();
    }

    public void Start()
    {
        if (_disposed || IsActive) return;

        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            var candidates = new List<MMDevice>();

            try
            {
                MMDevice defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                candidates.Add(defaultDevice);
            }
            catch { }

            foreach (MMDevice device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (!candidates.Any(existing => string.Equals(existing.ID, device.ID, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(device);
            }

            foreach (MMDevice device in candidates)
            {
                try
                {
                    var capture = new WasapiLoopbackCapture(device);
                    capture.DataAvailable += OnDataAvailable;
                    capture.StartRecording();

                    _capture = capture;
                    DeviceName = device.FriendlyName ?? "Output device";
                    Status = $"LIVE · {capture.WaveFormat.SampleRate / 1000} kHz";
                    IsActive = true;
                    return;
                }
                catch { }
            }

            Status = "NO OUTPUT DEVICE";
            IsActive = false;
        }
        catch
        {
            Status = "CAPTURE UNAVAILABLE";
            IsActive = false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null || e.BytesRecorded <= 0) return;

        WaveFormat format = capture.WaveFormat;
        int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        int frameSize = bytesPerSample * Math.Max(1, format.Channels);
        bool isFloat = format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat;
        int downsample = Math.Max(1, format.SampleRate / 20000);
        int limit = Math.Min(e.BytesRecorded, e.Buffer.Length);
        float framePeak = 0;

        for (int offset = 0; offset + frameSize <= limit; offset += frameSize)
        {
            double sum = 0;
            for (int channel = 0; channel < format.Channels; channel++)
                sum += ReadSample(e.Buffer, offset + channel * bytesPerSample, bytesPerSample, isFloat);

            float sample = (float)(sum / Math.Max(1, format.Channels));
            framePeak = Math.Max(framePeak, Math.Abs(sample));

            _samples[_sampleCount++] = sample;
            if (_sampleCount == FftSize)
            {
                Analyze(format.SampleRate);
                _sampleCount = 0;
            }

            if (_frameCounter++ % downsample == 0)
            {
                lock (_sync)
                {
                    _waveIndex = (_waveIndex + 1) % WaveformLength;
                    _waveform[_waveIndex] = sample;
                }
            }
        }

        _signalPeak = Math.Max(_signalPeak * 0.97f, framePeak);
    }

    private static double ReadSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat && offset + 4 <= buffer.Length)
            return BitConverter.ToSingle(buffer, offset);

        return bytesPerSample switch
        {
            1 => offset < buffer.Length ? (buffer[offset] - 128) / 128d : 0d,
            2 => offset + 2 <= buffer.Length ? BitConverter.ToInt16(buffer, offset) / 32768d : 0d,
            3 => offset + 3 <= buffer.Length ? Read24(buffer, offset) : 0d,
            4 => offset + 4 <= buffer.Length ? BitConverter.ToInt32(buffer, offset) / 2147483648d : 0d,
            _ => 0d
        };
    }

    private static double Read24(byte[] buffer, int offset)
    {
        int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
        return value / 8388608d;
    }

    private void Analyze(int sampleRate)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _samples[i] * _window[i];
            _fft[i].Y = 0;
        }

        FastFourierTransform.FFT(true, FftPower, _fft);

        double hzPerBin = (double)sampleRate / FftSize;
        int maxBin = FftSize / 2;

        lock (_sync)
        {
            for (int band = 0; band < BandCount; band++)
            {
                int from = Math.Max(1, (int)(BandEdges[band] / hzPerBin));
                int to = Math.Min(maxBin, Math.Max(from + 1, (int)(BandEdges[band + 1] / hzPerBin)));
                double peakInBand = 0;

                for (int bin = from; bin < to; bin++)
                {
                    double magnitude = Math.Sqrt(_fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y);
                    if (magnitude > peakInBand) peakInBand = magnitude;
                }

                _peak = Math.Max(_peak * 0.997f, (float)peakInBand);
                float level = (float)Math.Clamp(peakInBand / Math.Max(_peak, 0.008d), 0d, 1d);
                _raw[band] = Math.Max(level, _raw[band] * 0.9f);
            }
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed) return;

        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastFrameTime) return;
            if (_lastFrameTime >= TimeSpan.Zero)
                _frameMs = Math.Clamp((args.RenderingTime - _lastFrameTime).TotalMilliseconds, 4, 50);
            _lastFrameTime = args.RenderingTime;
        }

        _phase += _frameMs / 16.7;
        double decay = Math.Pow(0.975, _frameMs / 24d);
        double attack = 1d - Math.Pow(1d - 0.45, _frameMs / 24d);
        double release = 1d - Math.Pow(1d - 0.14, _frameMs / 24d);

        lock (_sync)
        {
            bool quiet = !IsActive || _signalPeak < 0.003f;
            if (quiet)
            {
                float idle = !IsActive
                    ? (float)(0.035 + 0.02 * (0.5 + 0.5 * Math.Sin(_phase * 0.08)))
                    : 0.014f;
                for (int i = 0; i < WaveformLength; i++)
                    _waveform[i] = (float)(idle * Math.Sin(i * 0.16 + _phase * 0.09));
            }

            for (int i = 0; i < BandCount; i++)
            {
                double target;
                if (!IsActive)
                    target = 0.05 + 0.03 * Math.Sin(_phase * 0.1 + i * 0.8);
                else if (quiet)
                    target = Math.Max(_raw[i], 0.04 + 0.02 * (0.5 + 0.5 * Math.Sin(_phase * 0.07 + i * 0.7)));
                else
                    target = _raw[i];

                if (IsActive) _raw[i] *= (float)decay;

                float k = (float)(target > _smooth[i] ? attack : release);
                _smooth[i] += (float)(target - _smooth[i]) * k;
                Levels[i] = Math.Clamp(_smooth[i], 0.035d, 1d);
            }
        }
    }

    public void CopyWaveform(float[] destination)
    {
        if (destination.Length < WaveformLength) return;
        lock (_sync)
        {
            for (int i = 0; i < WaveformLength; i++)
                destination[i] = _waveform[(_waveIndex + 1 + i) % WaveformLength];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRendering;
        _retryTimer.Stop();

        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.StopRecording();
                capture.Dispose();
            }
            catch { }
        }
        _enumerator?.Dispose();
        _enumerator = null;
        IsActive = false;
    }
}
