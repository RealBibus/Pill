using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pill.Models;
using Pill.Services;

namespace Pill.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public SystemMediaService Media { get; } = new();
    public AudioEqualizer Equalizer { get; } = new();
    public ObservableCollection<ShelfItem> Shelf { get; } = new();

    public bool HasShelfItems => Shelf.Count > 0;
    public int ShelfCount => Shelf.Count;

    public MainViewModel()
    {
        Shelf.CollectionChanged += OnShelfChanged;
    }

    private void OnShelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(HasShelfItems));
        Raise(nameof(ShelfCount));
    }

    public void Start()
    {
        Equalizer.Start();
        _ = Media.StartAsync();
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
            if (Shelf.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            Shelf.Add(new ShelfItem(path));
            added.Add(path);
        }
        return added.ToArray();
    }

    public void Remove(ShelfItem item) => Shelf.Remove(item);

    public void Dispose()
    {
        Shelf.CollectionChanged -= OnShelfChanged;
        Equalizer.Dispose();
        Media.Dispose();
    }
}
