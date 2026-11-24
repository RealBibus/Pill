using System;
using System.Windows.Media;

namespace Pill.Services;

public static class AmbientColorService
{
    public static readonly Color BaseAccent = Color.FromArgb(255, 91, 140, 255);

    public static Color Lift(Color color, double part = 0.12)
        => Blend(color, Colors.White, part);

    public static Color Deepen(Color color, double part = 0.18)
        => Blend(color, Color.FromArgb(255, 12, 14, 20), part);

    public static Color Blend(Color a, Color b, double bPart = 0.85)
    {
        static byte Mix(byte x, byte y, double t) => (byte)Math.Clamp(x + (y - x) * t, 0, 255);
        return Color.FromArgb(255, Mix(a.R, b.R, bPart), Mix(a.G, b.G, bPart), Mix(a.B, b.B, bPart));
    }

    public static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        double Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return Color.FromArgb(255,
            (byte)(Channel(h + 1.0 / 3) * 255),
            (byte)(Channel(h) * 255),
            (byte)(Channel(h - 1.0 / 3) * 255));
    }
}
