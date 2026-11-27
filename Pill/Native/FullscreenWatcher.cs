using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pill.Native;

public static class FullscreenWatcher
{
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsSysMenu = 0x00080000;
    private const uint MonitorDefaultToNearest = 2;

    public static bool IsFullscreenGame(IntPtr ownWindow)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == ownWindow) return false;

        var rect = new NativeRect();
        if (!GetWindowRect(window, out rect)) return false;

        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

        NativeRect screen = monitorInfo.Monitor;
        int tolerance = 4;
        bool coversScreen =
            Math.Abs(rect.Left - screen.Left) <= tolerance &&
            Math.Abs(rect.Top - screen.Top) <= tolerance &&
            Math.Abs(rect.Right - screen.Right) <= tolerance &&
            Math.Abs(rect.Bottom - screen.Bottom) <= tolerance;
        if (!coversScreen) return false;

        IntPtr stylePtr = GetWindowLongPtr(window, GwlStyle);
        long style = stylePtr.ToInt64();
        bool hasWindowControls = (style & (WsCaption | WsSysMenu)) != 0;
        if (hasWindowControls) return false;

        var className = new StringBuilder(128);
        GetClassName(window, className, className.Capacity);
        string cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;

        int processId = 0;
        GetWindowThreadProcessId(window, out processId);
        if (processId == 0) return false;

        try
        {
            using Process process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            if (name is "explorer" or "ShellExperienceHost" or "dwm" or "SearchHost") return false;
        }
        catch
        {
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLong64(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index)
        => IntPtr.Size == 8 ? GetWindowLong64(hWnd, index) : GetWindowLong32(hWnd, index);
}
