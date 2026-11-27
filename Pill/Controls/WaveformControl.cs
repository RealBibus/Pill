using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pill.Services;

namespace Pill.Controls;

public sealed class WaveformControl : Control
{
    public static readonly DependencyProperty AudioSourceProperty = DependencyProperty.Register(
        nameof(AudioSource), typeof(AudioEqualizer), typeof(WaveformControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty RenderEnabledProperty = DependencyProperty.Register(
        nameof(RenderEnabled), typeof(bool), typeof(WaveformControl),
        new PropertyMetadata(false, OnRenderEnabledChanged));

    private readonly float[] _buffer = new float[AudioEqualizer.WaveformLength];
    private readonly Point[] _points = new Point[AudioEqualizer.WaveformLength];
    private readonly StreamGeometry _geometry = new();
    private readonly Pen _linePen;
    private double _visiblePeak = 0.06;
    private double _frameMs = 16.7;
    private TimeSpan _lastFrameTime = TimeSpan.FromMilliseconds(-1);

    static WaveformControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(WaveformControl),
            new FrameworkPropertyMetadata(typeof(WaveformControl)));
    }

    public WaveformControl()
    {
        var brush = new SolidColorBrush(Color.FromArgb(235, 232, 238, 245));
        brush.Freeze();
        _linePen = new Pen(brush, 1.4)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _linePen.Freeze();

        IsVisibleChanged += (_, _) => UpdateHook();
        Unloaded += (_, _) => Detach();
    }

    public AudioEqualizer? AudioSource
    {
        get => (AudioEqualizer?)GetValue(AudioSourceProperty);
        set => SetValue(AudioSourceProperty, value);
    }

    public bool RenderEnabled
    {
        get => (bool)GetValue(RenderEnabledProperty);
        set => SetValue(RenderEnabledProperty, value);
    }

    private static void OnRenderEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WaveformControl)d).UpdateHook();

    private bool _hooked;

    private void UpdateHook()
    {
        bool shouldHook = IsVisible && RenderEnabled;
        if (shouldHook == _hooked) return;
        _hooked = shouldHook;
        if (shouldHook)
        {
            _lastFrameTime = TimeSpan.FromMilliseconds(-1);
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void Detach()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_hooked || !RenderEnabled || e is not RenderingEventArgs args) return;
        if (args.RenderingTime == _lastFrameTime) return;
        if (_lastFrameTime >= TimeSpan.Zero)
        {
            double elapsed = (args.RenderingTime - _lastFrameTime).TotalMilliseconds;
            if (elapsed < 50d) return;
            _frameMs = Math.Clamp(elapsed, 4, 50);
        }
        _lastFrameTime = args.RenderingTime;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        AudioEqualizer? audio = AudioSource;
        double width = ActualWidth;
        double height = ActualHeight;
        if (audio is null || width <= 0 || height <= 0) return;

        audio.CopyWaveform(_buffer);

        float max = 0;
        for (int i = 0; i < _buffer.Length; i++)
            max = Math.Max(max, Math.Abs(_buffer[i]));

        _visiblePeak = Math.Max(max, _visiblePeak * Math.Pow(0.965, _frameMs / 16.7));
        double scale = height * 0.42 / Math.Max(_visiblePeak, 0.02);
        double center = height / 2;

        for (int i = 0; i < _buffer.Length; i++)
        {
            double x = (double)i / (_buffer.Length - 1) * width;
            _points[i] = new Point(x, center - _buffer[i] * scale);
        }

        using (StreamGeometryContext context = _geometry.Open())
        {
            context.BeginFigure(_points[0], false, false);
            for (int i = 1; i < _points.Length; i++)
                context.LineTo(_points[i], true, true);
        }

        drawingContext.DrawGeometry(null, _linePen, _geometry);
    }
}
