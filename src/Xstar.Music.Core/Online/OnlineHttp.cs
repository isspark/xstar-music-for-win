using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Xstar.Music.Core;

/// <summary>
/// Builds <see cref="HttpClient"/> instances for online providers. On networks where the
/// IPv6 route is broken (common with dual-stack CDNs), the default handler stalls until the
/// whole request times out; connecting over IPv4 directly avoids that.
/// </summary>
internal static class OnlineHttp
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                              ?? throw new HttpRequestException($"无法解析主机：{context.DnsEndPoint.Host}");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = timeout };
    }
}
