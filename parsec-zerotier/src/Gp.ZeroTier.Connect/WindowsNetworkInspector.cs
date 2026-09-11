using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed record NetworkSnapshot(IReadOnlyList<NetworkObservation> Observations, IReadOnlyDictionary<string, uint> BestInterfaces);

public sealed class WindowsNetworkInspector
{
    private static readonly string[] PublicProbeAddresses = ["1.1.1.1", "8.8.8.8"];

    public NetworkSnapshot Capture()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var namesByIndex = interfaces
                .Where(item => item.OperationalStatus == OperationalStatus.Up)
                .Select(item => (Item: item, Props: TryGetIpv4Properties(item)))
                .Where(item => item.Props is not null)
                .ToDictionary(item => (uint)item.Props!.Index, item => DisplayName(item.Item));

            var observations = new List<NetworkObservation>();
            foreach (var adapter in interfaces.Where(item => item.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses
                             .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    if (IPAddress.IsLoopback(address.Address) || IsMulticast(address.Address)) continue;
                    observations.Add(new NetworkObservation(
                        Ipv4Prefix.Parse($"{address.Address}/{address.PrefixLength}"),
                        "local_address", DisplayName(adapter), true));
                }
            }

            observations.AddRange(ReadIpv4Routes(namesByIndex));
            var best = PublicProbeAddresses.ToDictionary(value => value, GetBestInterface);
            return new(observations, best);
        }
        catch (Exception ex) when (ex is not LauncherException)
        {
            throw new LauncherException("ZT_NETWORK_PREFLIGHT_UNAVAILABLE", "Nie można wiarygodnie odczytać aktywnych adresów i tras IPv4.");
        }
    }

    public static bool PublicRouteUnchanged(NetworkSnapshot before, NetworkSnapshot after, uint zeroTierInterfaceIndex)
    {
        return before.BestInterfaces.All(pair =>
            after.BestInterfaces.TryGetValue(pair.Key, out var current) &&
            current == pair.Value && current != zeroTierInterfaceIndex);
    }

    public uint BestInterfaceFor(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new LauncherException("ZT_VM_IP_INVALID", "Serwer zwrócił nieprawidłowy adres VM.");
        try { return GetBestInterface(address); }
        catch (Exception ex) when (ex is not LauncherException)
        {
            throw new LauncherException("ZT_NETWORK_PREFLIGHT_UNAVAILABLE", "Nie można potwierdzić trasy do przypisanej maszyny.");
        }
    }

    private static IPv4InterfaceProperties? TryGetIpv4Properties(NetworkInterface item)
    {
        try { return item.GetIPProperties().GetIPv4Properties(); }
        catch (NetworkInformationException) { return null; }
    }

    private static bool IsMulticast(IPAddress address) => address.GetAddressBytes()[0] is >= 224 and <= 239;

    private static string DisplayName(NetworkInterface item) => $"{item.Name} ({item.NetworkInterfaceType})";

    private static IReadOnlyList<NetworkObservation> ReadIpv4Routes(IReadOnlyDictionary<uint, string> namesByIndex)
    {
        var error = GetIpForwardTable2((ushort)AddressFamily.InterNetwork, out var table);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibIpForwardRow2>();
            var offset = IntPtr.Size == 8 ? 8 : 4;
            var result = new List<NetworkObservation>(count);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow2>(IntPtr.Add(table, offset + index * rowSize));
                if (row.DestinationPrefix.Prefix.Family != (ushort)AddressFamily.InterNetwork ||
                    !namesByIndex.ContainsKey(row.InterfaceIndex)) continue;
                var ip = new IPAddress(row.DestinationPrefix.Prefix.Address);
                var prefix = Ipv4Prefix.Parse($"{ip}/{row.DestinationPrefix.PrefixLength}");
                var name = namesByIndex.GetValueOrDefault(row.InterfaceIndex, $"ifIndex:{row.InterfaceIndex}");
                result.Add(new NetworkObservation(prefix, "route", name, true));
            }
            return result;
        }
        finally { FreeMibTable(table); }
    }

    private static uint GetBestInterface(string address)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        var socket = new SockaddrInet
        {
            Family = (ushort)AddressFamily.InterNetwork,
            Address = BitConverter.ToUInt32(bytes, 0),
            Padding = new byte[20]
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<SockaddrInet>());
        try
        {
            Marshal.StructureToPtr(socket, pointer, false);
            var error = GetBestInterfaceEx(pointer, out var interfaceIndex);
            if (error != 0) throw new Win32Exception((int)error);
            return interfaceIndex;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrInet
    {
        public ushort Family;
        public ushort Port;
        public uint Address;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        [MarshalAs(UnmanagedType.U1)] public bool Loopback;
        [MarshalAs(UnmanagedType.U1)] public bool AutoconfigureAddress;
        [MarshalAs(UnmanagedType.U1)] public bool Publish;
        [MarshalAs(UnmanagedType.U1)] public bool Immortal;
        public uint Age;
        public uint Origin;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(IntPtr destinationAddress, out uint bestInterfaceIndex);
}
