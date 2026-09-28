using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MultiProxy
{
    public class NetworkInterfaceInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public IPAddress IPv4 { get; set; }
        public int Index { get; set; }
        public NetworkInterfaceType Type { get; set; }
        public int Weight { get; set; } = 1;
        public bool Enabled { get; set; } = true;

        public string DisplayName
        {
            get
            {
                string typeStr = Type == NetworkInterfaceType.Wireless80211 ? "无线" :
                                 Type == NetworkInterfaceType.Ethernet ? "有线" : "其他";
                return $"[{typeStr}] {Name} - {IPv4}";
            }
        }
    }

    public static class NetworkHelper
    {
        public static List<NetworkInterfaceInfo> GetActiveInterfaces()
        {
            var list = new List<NetworkInterfaceInfo>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = ni.GetIPProperties();
                var ipv4Info = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4Info == null) continue;
                if (IPAddress.IsLoopback(ipv4Info.Address)) continue;

                int index = 0;
                try { index = props.GetIPv4Properties().Index; } catch { }

                list.Add(new NetworkInterfaceInfo
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    IPv4 = ipv4Info.Address,
                    Index = index,
                    Type = ni.NetworkInterfaceType
                });
            }
            return list;
        }
    }
}