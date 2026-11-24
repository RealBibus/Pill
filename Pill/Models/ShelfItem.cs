using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pill.Services;

namespace Pill.Models;

public sealed class ShelfItem
{
    public ShelfItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path) ?? path;
        Icon = IconHelper.GetFileIcon(path);
        SizeText = GetSizeText(path);
    }

    public string Path { get; }
    public string Name { get; }
    public string SizeText { get; }
    public ImageSource? Icon { get; }

    private static string GetSizeText(string path)
    {
        try
        {
            if (Directory.Exists(path)) return "folder";
            var info = new FileInfo(path);
            return info.Exists ? FormatSize(info.Length) : "file";
        }
        catch
        {
            return "file";
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} {units[i]}" : $"{value:0.#} {units[i]}";
    }
}
