using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace Pill.Services;

public sealed class ActiveWindowColorService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    private int _exLeft = int.MinValue;
    private int _exTop;
    private int _exRight;
    private int _exBottom;

    private MediaColor? _currentColor;
    private MediaColor? _pendingColor;
    private int _pendingCount;
    private IntPtr _lastExternalWindow;

    public event EventHandler<MediaColor>? ColorChanged;

    public ActiveWindowColorService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += (_, _) => Sample();
    }

    public void SetExclusion(int left, int top, int width, int height)
    {
        _exLeft = left - 40;
        _exTop = top - 40;
        _exRight = left + width + 40;
        _exBottom = top + height + 40;
    }

    public void Start()
    {
        if (_disposed) return;
        _currentColor = null;
        _pendingColor = null;
        _pendingCount = 0;
        Sample();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _pendingColor = null;
        _pendingCount = 0;
    }

    private void Sample()
    {
        if (_disposed) return;
        try
        {
            (MediaColor? Color, bool Confident, bool WindowChanged) sampled = SampleScreen();
            if (!sampled.Color.HasValue) return;

            MediaColor color = sampled.Color.Value;
            if (_currentColor is null || sampled.WindowChanged)
            {
                Accept(color);
                return;
            }

            double change = ColorChange(color, _currentColor.Value);

            if (sampled.Confident && change >= 0.10)
            {
                Accept(color);
                return;
            }

            if (change < 0.08) return;

            if (_pendingColor.HasValue && ColorChange(color, _pendingColor.Value) < 0.06)
                _pendingCount++;
            else
            {
                _pendingColor = color;
                _pendingCount = 1;
            }

            if (_pendingCount < 3) return;
            Accept(color);
        }
        catch { }
    }

    private void Accept(MediaColor color)
    {
        _currentColor = color;
        _pendingColor = null;
        _pendingCount = 0;
        ColorChanged?.Invoke(this, color);
    }

    private static double ColorChange(MediaColor a, MediaColor b)
    {
        ToHsl(a.R, a.G, a.B, out double hueA, out double saturationA, out _);
        ToHsl(b.R, b.G, b.B, out double hueB, out double saturationB, out _);

        double hueDelta = Math.Abs(hueA - hueB);
        hueDelta = Math.Min(hueDelta, 1 - hueDelta);
        double saturationDelta = Math.Abs(saturationA - saturationB);
        return Math.Max(hueDelta * 2, saturationDelta);
    }

    private static void ToHsl(byte r8, byte g8, byte b8,
        out double hue, out double saturation, out double lightness)
    {
        double r = r8 / 255.0;
        double g = g8 / 255.0;
        double b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        lightness = (max + min) / 2;

        if (delta <= 0)
        {
            hue = 0;
            saturation = 0;
            return;
        }

        saturation = lightness <= 0.5
            ? delta / (max + min)
            : delta / (2 - max - min);

        if (max == r)
            hue = ((g - b) / delta + (g < b ? 6 : 0)) / 6;
        else if (max == g)
            hue = ((b - r) / delta + 2) / 6;
        else
            hue = ((r - g) / delta + 4) / 6;
    }

    private (MediaColor? Color, bool Confident, bool WindowChanged) SampleScreen()
    {
        int sx = GetSystemMetrics(76);
        int sy = GetSystemMetrics(77);
        int screenWidth = GetSystemMetrics(78);
        int screenHeight = GetSystemMetrics(79);
        if (screenWidth < 16 || screenHeight < 16) return (null, false, false);

        GetTargetRect(sx, sy, screenWidth, screenHeight,
            out int x, out int y, out int width, out int height, out IntPtr foreground);
        if (width < 16 || height < 16) return (null, false, false);

        bool windowChanged = foreground != IntPtr.Zero && foreground != _lastExternalWindow;
        if (foreground != IntPtr.Zero) _lastExternalWindow = foreground;

        using var window = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(window))
            graphics.CopyFromScreen(x, y, 0, 0,
                new Size(width, height), CopyPixelOperation.SourceCopy);

        var hueCounts = new int[36];
        var redSums = new double[36];
        var greenSums = new double[36];
        var blueSums = new double[36];

        double sumR = 0, sumG = 0, sumB = 0;
        int count = 0;

        BitmapData data = window.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int stride = data.Stride;
            byte[] buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (int py = 0; py < height; py++)
            {
                int screenY = y + py;
                int row = py * stride;
                for (int px = 0; px < width; px++)
                {
                    int screenX = x + px;
                    if (screenX >= _exLeft && screenX <= _exRight &&
                        screenY >= _exTop && screenY <= _exBottom) continue;

                    int index = row + px * 4;
                    byte blue = buffer[index];
                    byte green = buffer[index + 1];
                    byte red = buffer[index + 2];

                    sumR += red;
                    sumG += green;
                    sumB += blue;
                    count++;

                    ToHsl(red, green, blue, out double hue, out double saturation, out double lightness);
                    if (saturation < 0.25 || lightness < 0.10 || lightness > 0.82) continue;

                    int bin = (int)(hue * 36) % 36;
                    hueCounts[bin]++;
                    redSums[bin] += red;
                    greenSums[bin] += green;
                    blueSums[bin] += blue;
                }
            }
        }
        finally
        {
            window.UnlockBits(data);
        }

        MediaColor? Average()
        {
            if (count == 0) return null;
            return MediaColor.FromArgb(255,
                (byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
        }

        int bestBin = 0;
        for (int i = 1; i < hueCounts.Length; i++)
            if (hueCounts[i] > hueCounts[bestBin]) bestBin = i;

        int totalQualified = 0;
        for (int i = 0; i < hueCounts.Length; i++) totalQualified += hueCounts[i];
        if (totalQualified == 0) return (Average(), false, windowChanged);

        int clusterCount = 0;
        for (int offset = -4; offset <= 4; offset++)
            clusterCount += hueCounts[(bestBin + offset + 36) % 36];

        int minCount = Math.Max(500, (int)(width * height * 0.015));
        if (clusterCount < minCount ||
            (double)clusterCount / totalQualified < 0.30)
            return (Average(), false, windowChanged);

        double clusterRed = 0, clusterGreen = 0, clusterBlue = 0;
        for (int offset = -4; offset <= 4; offset++)
        {
            int index = (bestBin + offset + 36) % 36;
            clusterRed += redSums[index];
            clusterGreen += greenSums[index];
            clusterBlue += blueSums[index];
        }

        byte averageRed = (byte)(clusterRed / clusterCount);
        byte averageGreen = (byte)(clusterGreen / clusterCount);
        byte averageBlue = (byte)(clusterBlue / clusterCount);

        ToHsl(averageRed, averageGreen, averageBlue,
            out double mixedHue, out double mixedSaturation, out _);

        MediaColor accent = AmbientColorService.FromHsl(
            mixedHue, Math.Clamp(mixedSaturation, 0.55, 1.0), 0.5);
        return (accent, true, windowChanged);
    }

    private static void GetTargetRect(
        int sx, int sy, int screenWidth, int screenHeight,
        out int x, out int y, out int width, out int height, out IntPtr foreground)
    {
        x = sx;
        y = sy;
        width = screenWidth;
        height = screenHeight;
        foreground = IntPtr.Zero;

        IntPtr active = GetForegroundWindow();
        if (active == IntPtr.Zero) return;

        GetWindowThreadProcessId(active, out uint processId);
        if (processId == (uint)Environment.ProcessId) return;
        if (!GetWindowRect(active, out Rect rect)) return;

        foreground = active;

        int left = Math.Max(sx, rect.Left);
        int top = Math.Max(sy, rect.Top);
        int right = Math.Min(sx + screenWidth, rect.Right);
        int bottom = Math.Min(sy + screenHeight, rect.Bottom);
        if (right - left < 32 || bottom - top < 32) return;

        x = left;
        y = top;
        width = right - left;
        height = bottom - top;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
