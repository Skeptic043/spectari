using System.Net;
using System.Net.NetworkInformation;
using Spectari.Util;

namespace Spectari.Server;

internal static class LocalViewerAuthority
{
    internal static ViewerAccessPolicy Create(int port)
    {
        var names = new List<string> { "localhost", Dns.GetHostName() };
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            names.Add(properties.HostName);
            if (!string.IsNullOrEmpty(properties.DomainName))
                names.Add(properties.HostName + "." + properties.DomainName);
            string? selfName = StreamDiscovery.GetSelfDnsName();
            if (!string.IsNullOrEmpty(selfName)) names.Add(selfName);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("[http] local name discovery failed; address links remain available");
        }
        return new ViewerAccessPolicy(port, names, ReadAddresses);
    }

    private static IEnumerable<IPAddress> ReadAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }
}
