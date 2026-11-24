using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Pill.Models;

namespace Pill.Services;

public sealed class StatusService : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _deviceTimer;
    private bool _disposed;

    private string _clockText = DateTime.Now.ToString("HH:mm");
    private string _mouseBattery = "—";
    private string _headphonesBattery = "—";
    private string _listeningText = "00:00:00";
    private int _networkBars;

    private bool _isPlaying;
    private long _listeningSeconds;

    private static readonly Guid BatteryProperty = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private static readonly Guid DeviceProperties = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid MouseClass = new("4D36E96F-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");
    private static readonly Guid MediaClass = new("4D36E96C-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid AudioEndpointClass = new("C6CB1C63-822B-4A05-80DC-9A2CDBA9E5C7");
    private static readonly Guid BluetoothClass = new("E0CBF06C-CD8B-4647-BB8A-263B43F0F974");
    private const uint BatteryPropertyId = 2;
    private const uint DevPropTypeByte = 3;
    private const uint DevPropTypeUint32 = 7;
    private const uint DevPropTypeString = 18;
    private const int SpdrpFriendlyName = 0x0C;
    private const uint DigcfAllClasses = 0x4;
    private const uint CmLocatePhantom = 0x1;
    private const uint CrSuccess = 0;
    private const uint DnHasProblem = 0x400;
    private const uint DnDeviceDisconnected = 0x02000000;

    public string ClockText { get => _clockText; private set => Set(ref _clockText, value); }
    public string MouseBattery { get => _mouseBattery; private set => Set(ref _mouseBattery, value); }
    public string HeadphonesBattery { get => _headphonesBattery; private set => Set(ref _headphonesBattery, value); }
    public string ListeningText { get => _listeningText; private set => Set(ref _listeningText, value); }
    public int NetworkBars { get => _networkBars; private set => Set(ref _networkBars, value); }

    public StatusService()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += OnClockTick;

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _deviceTimer.Tick += (_, _) => RefreshDevices();
    }

    public void Start()
    {
        if (_disposed) return;
        ClockText = DateTime.Now.ToString("HH:mm");
        RefreshDevices();
        _clockTimer.Start();
        _deviceTimer.Start();
    }

    public void UpdateMedia(bool isPlaying, string title)
    {
        if (_disposed) return;
        _isPlaying = isPlaying
            && !string.IsNullOrWhiteSpace(title)
            && !string.Equals(title, "Nothing playing", StringComparison.Ordinal);
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        ClockText = DateTime.Now.ToString("HH:mm");
        if (!_isPlaying) return;
        _listeningSeconds++;
        UpdateListeningText();
    }

    private void RefreshDevices()
    {
        List<BatteryDevice> devices = ReadBatteries();
        string[] customMouse = CustomNames("Mouse");
        string[] customHeadphones = CustomNames("Headphones");

        BatteryDevice? mouse = Pick(devices, device => IsMouse(device, customMouse), null, d => !IsPhoneLike(d));
        BatteryDevice? headphones = Pick(devices, device => IsHeadphones(device, customHeadphones), mouse, d => !IsPhoneLike(d));

        MouseBattery = mouse is null ? "—" : mouse.Level + "%";
        HeadphonesBattery = headphones is null ? "—" : headphones.Level + "%";
        NetworkBars = ReadNetworkBars();
    }

    private static string[] CustomNames(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Pill\BatteryNames");
            if (key?.GetValue(valueName) is not string text) return Array.Empty<string>();
            return text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static BatteryDevice? Pick(List<BatteryDevice> devices,
        Func<BatteryDevice, bool> preferred, BatteryDevice? exclude, Func<BatteryDevice, bool> allowed)
    {
        foreach (BatteryDevice device in devices)
        {
            if (ReferenceEquals(device, exclude) || !allowed(device)) continue;
            if (preferred(device)) return device;
        }
        foreach (BatteryDevice device in devices)
        {
            if (ReferenceEquals(device, exclude) || !allowed(device)) continue;
            return device;
        }
        return null;
    }

    private static bool IsPhoneLike(BatteryDevice device)
    {
        return Matches(device.Name, new[]
        {
            "phone", "redmi", "iphone", "samsung", "pixel", "huawei", "xiaomi", "телефон"
        });
    }

    private static bool IsMouse(BatteryDevice device, string[] custom)
    {
        if (device.ClassGuid == MouseClass || device.ClassGuid == HidClass) return true;
        return Matches(device.Name, custom)
            || Matches(device.Name, new[]
            {
                "mouse", "мыш", "trackpad", "touchpad", "mchose", "a7 ultra",
                "logitech", "mx master", "razer", "steelseries", "deathadder"
            });
    }

    private static bool IsHeadphones(BatteryDevice device, string[] custom)
    {
        if (IsPhoneLike(device) && !Matches(device.Name, custom)) return false;
        if (device.ClassGuid == MediaClass || device.ClassGuid == AudioEndpointClass) return true;
        return Matches(device.Name, custom)
            || Matches(device.Name, new[]
            {
                "headphone", "headset", "наушник", "гарнитур", "earbud", "buds",
                "airpods", "audio", "hands-free", "handsfree", "mic", "микрофон"
            });
    }

    private static bool Matches(string name, IEnumerable<string> keywords)
    {
        foreach (string keyword in keywords)
        {
            if (keyword.Length > 0 && name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static int ReadNetworkBars()
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return 0;

        List<NetworkInterface> active = new();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            active.Add(nic);
        }
        if (active.Count == 0) return 0;

        uint routeIndex = 0;
        uint destination = BitConverter.ToUInt32(new byte[] { 8, 8, 8, 8 }, 0);
        if (GetBestInterface(destination, out routeIndex) == CrSuccess && routeIndex != 0)
        {
            string? adapterName = FindAdapterName(routeIndex);
            if (adapterName is not null)
            {
                foreach (NetworkInterface nic in active)
                {
                    string id = nic.Id.Trim('{', '}');
                    if (string.Equals(id, adapterName.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(nic.Description, adapterName, StringComparison.OrdinalIgnoreCase))
                        return BarsFor(nic);
                }
            }
        }

        foreach (NetworkInterface nic in active)
        {
            if (nic.GetIPProperties().GatewayAddresses.Count > 0 &&
                nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                return BarsFor(nic);
        }
        foreach (NetworkInterface nic in active)
        {
            if (nic.GetIPProperties().GatewayAddresses.Count > 0) return BarsFor(nic);
        }
        return 0;
    }

    private static int BarsFor(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return 4;
        double mbps = nic.Speed / 1_000_000d;
        if (mbps >= 100) return 4;
        if (mbps >= 20) return 3;
        if (mbps >= 5) return 2;
        return 1;
    }

    private static string? FindAdapterName(uint index)
    {
        int size = 16384;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint result = GetAdaptersInfo(buffer, ref size);
            if (result != CrSuccess) return null;

            IntPtr cursor = buffer;
            while (cursor != IntPtr.Zero)
            {
                IpAdapterInfo info = Marshal.PtrToStructure<IpAdapterInfo>(cursor);
                if (info.Index == index) return info.AdapterName;
                cursor = info.Next;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void UpdateListeningText()
    {
        TimeSpan value = TimeSpan.FromSeconds(_listeningSeconds);
        ListeningText = $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private static List<BatteryDevice> ReadBatteries()
    {
        var result = new List<BatteryDevice>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfAllClasses);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var key = new DevPropKey { FormatId = BatteryProperty, PropertyId = BatteryPropertyId };
            for (uint index = 0; ; index++)
            {
                var device = new DevInfoData { Size = (uint)Marshal.SizeOf(typeof(DevInfoData)) };
                if (!SetupDiEnumDeviceInfo(set, index, ref device)) break;

                string instanceId = InstanceId(set, ref device);
                if (instanceId.Length == 0) continue;
                if (CmLocateDevNode(out uint node, instanceId, CmLocatePhantom) != CrSuccess) continue;

                if (CmGetDevNodeStatus(out uint status, out _, node, 0) != CrSuccess) continue;
                if ((status & (DnDeviceDisconnected | DnHasProblem)) != 0) continue;

                var buffer = new byte[8];
                uint size = (uint)buffer.Length;
                if (CmGetDevNodeProperty(node, ref key, out uint type, buffer, ref size, 0) != CrSuccess) continue;

                int level;
                if (type == DevPropTypeByte)
                    level = buffer[0];
                else if (type == DevPropTypeUint32 && size >= 4)
                    level = (int)BitConverter.ToUInt32(buffer, 0);
                else
                    continue;

                if (level < 0 || level > 100) continue;

                string name = FriendlyName(node, set, ref device);
                if (name.Length > 0) result.Add(new BatteryDevice(name, instanceId, device.ClassGuid, level));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static string InstanceId(IntPtr set, ref DevInfoData device)
    {
        SetupDiGetDeviceInstanceId(set, ref device, null, 0, out uint required);
        if (required == 0 || required > 4096) return string.Empty;

        var buffer = new StringBuilder((int)required + 1);
        if (!SetupDiGetDeviceInstanceId(set, ref device, buffer, required + 1, out _))
            return string.Empty;
        return buffer.ToString();
    }

    private static string FriendlyName(uint node, IntPtr set, ref DevInfoData device)
    {
        string name = ReadStringProperty(node, DeviceProperties, 14);
        if (name.Length > 0) return name;

        SetupDiGetDeviceRegistryProperty(set, ref device, SpdrpFriendlyName,
            out _, null, 0, out uint required);
        if (required == 0 || required > 2048) return string.Empty;

        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref device, SpdrpFriendlyName,
                out _, buffer, required, out _))
            return string.Empty;
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string ReadStringProperty(uint node, Guid formatId, uint propertyId)
    {
        var key = new DevPropKey { FormatId = formatId, PropertyId = propertyId };
        uint size = 0;
        CmGetDevNodeProperty(node, ref key, out _, null, ref size, 0);
        if (size == 0 || size > 2048) return string.Empty;

        var buffer = new byte[size];
        if (CmGetDevNodeProperty(node, ref key, out uint type,
                buffer, ref size, 0) != CrSuccess || type != DevPropTypeString)
            return string.Empty;

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clockTimer.Stop();
        _deviceTimer.Stop();
    }

    private sealed record BatteryDevice(string Name, string InstanceId, Guid ClassGuid, int Level);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IpAddrString
    {
        public IntPtr Next;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string IpAddress;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Mask;
        public uint Context;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IpAdapterInfo
    {
        public IntPtr Next;
        public uint ComboIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 132)] public string Description;
        public uint AddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Address;
        public uint Index;
        public uint Type;
        public uint DhcpEnabled;
        public IntPtr CurrentIpAddress;
        public IpAddrString IpAddressList;
        public IpAddrString GatewayList;
        public IpAddrString DhcpServer;
        [MarshalAs(UnmanagedType.Bool)] public bool HaveWins;
        public IpAddrString PrimaryWinsServer;
        public IpAddrString SecondaryWinsServer;
        public uint LeaseObtained;
        public uint LeaseExpires;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator,
        IntPtr parent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfoData device);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfoData device,
        StringBuilder? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DevInfoData device,
        uint property, out uint regType, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CmLocateDevNode(out uint devNode, string deviceInstanceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static extern uint CmGetDevNodeStatus(out uint status, out uint problemNumber,
        uint devNode, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
    private static extern uint CmGetDevNodeProperty(uint devNode, ref DevPropKey key,
        out uint propertyType, byte[]? buffer, ref uint size, uint flags);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destination, out uint bestInterfaceIndex);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Ansi)]
    private static extern uint GetAdaptersInfo(IntPtr adapterInfo, ref int size);
}
