using System.Net;
using System.Net.NetworkInformation;

namespace DomainDesk.Services;

public static class RemoteValidation
{
    public static void ValidateServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Any(char.IsWhiteSpace) || server.Contains(':') && !IPAddress.TryParse(server, out _))
            throw new ArgumentException("请输入远端服务器的完整 DNS 主机名或 IP 地址，不含协议、端口或路径。");
        if (!IPAddress.TryParse(server, out var ip) &&
            (Uri.CheckHostName(server) != UriHostNameType.Dns || !server.Contains('.') || server.EndsWith('.')))
            throw new ArgumentException("请输入远端服务器的完整 DNS 主机名或 IP 地址，不含协议、端口或路径。");
        var local = Environment.MachineName;
        if (ip is not null && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            NetworkInterface.GetAllNetworkInterfaces().SelectMany(x => x.GetIPProperties().UnicastAddresses).Any(x => x.Address.Equals(ip))))
            throw new ArgumentException("此客户端仅管理远端服务器，请勿连接本机地址。");
        if (server.Split('.')[0].Equals(local, StringComparison.OrdinalIgnoreCase) || server.StartsWith("localhost.", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("此客户端仅管理远端服务器，请勿连接本机。");
    }
}
