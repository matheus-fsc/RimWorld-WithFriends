using System.Net;
using System.Net.Sockets;

namespace WithFriends.Server;

/// <summary>
/// Listener dual-stack — §17.3 regra 1: bind em <c>::</c> com DualMode,
/// atendendo IPv6 e IPv4 no mesmo socket. Nunca fixar AddressFamily.
/// </summary>
public sealed class Listener : IDisposable
{
    readonly TcpListener listener;

    public Listener(int port)
    {
        listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)listener.LocalEndpoint;

    public void Start() => listener.Start();

    public Task<TcpClient> AcceptAsync(CancellationToken token) =>
        listener.AcceptTcpClientAsync(token).AsTask();

    public void Dispose() => listener.Stop();
}
