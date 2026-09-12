using System.Net;
using System.Net.Sockets;
using WithFriends.Server;
using Xunit;

namespace WithFriends.Tests;

/// <summary>Critérios de §17.6, na parte que dá para testar sem duas máquinas.</summary>
public class TransportTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Listener_aceita_IPv4_e_IPv6_no_mesmo_socket(string loopback)
    {
        using var listener = new Listener(0);
        listener.Start();
        int porta = listener.LocalEndPoint.Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var aceitando = listener.AcceptAsync(cts.Token);

        using var cliente = new TcpClient(IPAddress.Parse(loopback).AddressFamily);
        await cliente.ConnectAsync(IPAddress.Parse(loopback), porta);

        using var aceito = await aceitando;
        Assert.True(aceito.Connected);
    }
}
