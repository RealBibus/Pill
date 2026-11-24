using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pill.Services;

namespace Pill.Controls;

/// <summary>
/// Минималистичный oscilloscope: одна линия по буферу AudioEqualizer.
/// Обновляется вместе с кадрами WPF и не создаёт объектов на каждый тик.
/// </summary>
public sealed class WaveformControl : Control
{
    public static readonly DependencyProperty AudioSourceProperty = DependencyProperty.Register(
        nameof(AudioSource), typeof(AudioEqualizer), typeof(WaveformControl),
        new PropertyMetadata(null));

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

        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public AudioEqualizer? AudioSource
    {
        get => (AudioEqualizer?)GetValue(AudioSourceProperty);
        set => SetValue(AudioSourceProperty, value);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastFrameTime) return;
            if (_lastFrameTime >= TimeSpan.Zero)
                _frameMs = Math.Clamp((args.RenderingTime - _lastFrameTime).TotalMilliseconds, 4, 50);
            _lastFrameTime = args.RenderingTime;
        }
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
