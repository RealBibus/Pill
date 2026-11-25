using System;
using System.Runtime.InteropServices;

namespace Pill.Native;

public static class WindowEffects
{
    public static void UseDarkMode(IntPtr hwnd) => SetAttribute(hwnd, 19, 1);

    public static void UseRoundCorners(IntPtr hwnd) => SetAttribute(hwnd, 33, 2);

    public static void EnableStrongBlur(IntPtr hwnd) => SetComposition(hwnd, 3, unchecked((int)0x99181414));

    public static void ApplyAcrylic(IntPtr hwnd) => SetComposition(hwnd, 4, unchecked((int)0x99181414));

    public static void TrySystemBackdrop(IntPtr hwnd) => SetAttribute(hwnd, 38, 3);

    private static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            int local = value;
            DwmSetWindowAttribute(hwnd, attribute, ref local, sizeof(int));
        }
        catch { }
    }

    private static void SetComposition(IntPtr hwnd, int state, int color)
    {
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,
                GradientColor = color,
                AnimationId = 0
            };
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf(policy));
            try
            {
                Marshal.StructureToPtr(policy, data, false);
                var attribute = new WindowCompositionAttributeData
                {
                    Attribute = 19,
                    Data = data,
                    SizeOfData = Marshal.SizeOf(policy)
                };
                SetWindowCompositionAttribute(hwnd, ref attribute);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
