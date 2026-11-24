using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Pill.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
    {
        if (_dispatcher.CheckAccess())
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        else
            _dispatcher.Invoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }
}
