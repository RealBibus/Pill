using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pill.Services;

public static class CoverPaletteService
{
    private const int SampleSize = 64;
    private const int HueBins = 36;
    private const double TwoPi = Math.PI * 2;

    public static Color FromCover(ImageSource? cover)
    {
        if (cover is not BitmapSource source || source.PixelWidth < 8 || source.PixelHeight < 8)
            return AmbientColorService.BaseAccent;

        try
        {
            BitmapSource sample = Prepare(source);
            byte[] pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4];
            sample.CopyPixels(pixels, sample.PixelWidth * 4, 0);

            var weight = new double[HueBins];
            var sinSum = new double[HueBins];
            var cosSum = new double[HueBins];
            var saturationSum = new double[HueBins];
            var count = new int[HueBins];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;

                byte blue = pixels[i];
                byte green = pixels[i + 1];
                byte red = pixels[i + 2];

                ToHsl(red, green, blue, out double hue, out double saturation, out double lightness);
                if (lightness < 0.08 || lightness > 0.94 || saturation < 0.10) continue;

                int bin = (int)(hue * HueBins) % HueBins;
                double value = Math.Pow(saturation, 1.4) * (0.55 + lightness * 0.45);
                double angle = hue * TwoPi;

                weight[bin] += value;
                sinSum[bin] += Math.Sin(angle) * value;
                cosSum[bin] += Math.Cos(angle) * value;
                saturationSum[bin] += saturation;
                count[bin]++;
            }

            int total = 0;
            for (int i = 0; i < HueBins; i++) total += count[i];
            if (total == 0) return AmbientColorService.BaseAccent;

            var smoothed = new double[HueBins];
            for (int i = 0; i < HueBins; i++)
            {
                smoothed[i] =
                    weight[(i - 2 + HueBins) % HueBins] * 0.10 +
                    weight[(i - 1 + HueBins) % HueBins] * 0.22 +
                    weight[i] * 0.36 +
                    weight[(i + 1) % HueBins] * 0.22 +
                    weight[(i + 2) % HueBins] * 0.10;
            }

            int dominant = 0;
            for (int i = 1; i < HueBins; i++)
                if (smoothed[i] > smoothed[dominant]) dominant = i;

            double sin = 0;
            double cos = 0;
            double saturationTotal = 0;
            int mixedCount = 0;

            for (int offset = -12; offset <= 12; offset++)
            {
                int bin = (dominant + offset + HueBins) % HueBins;
                sin += sinSum[bin];
                cos += cosSum[bin];
                saturationTotal += saturationSum[bin];
                mixedCount += count[bin];
            }

            if (mixedCount == 0 || Math.Sqrt(sin * sin + cos * cos) < 0.0001)
                return AmbientColorService.BaseAccent;

            double mixedHue = Math.Atan2(sin, cos) / TwoPi;
            if (mixedHue < 0) mixedHue += 1;

            double averageSaturation = saturationTotal / mixedCount;
            double finalSaturation = Math.Clamp(Math.Max(averageSaturation * 1.08, 0.62), 0.62, 0.88);
            return AmbientColorService.FromHsl(mixedHue, finalSaturation, 0.52);
        }
        catch
        {
            return AmbientColorService.BaseAccent;
        }
    }

    private static BitmapSource Prepare(BitmapSource source)
    {
        double scale = Math.Min(SampleSize / (double)source.PixelWidth, SampleSize / (double)source.PixelHeight);
        BitmapSource resized = scale < 1
            ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
            : source;

        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
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
}
