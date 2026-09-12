using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WithFriends.Transport;

/// <summary>
/// Conexão direta, IPv6 e IPv4, qualquer loja — o caminho 1 da escada da
/// §17.4 e o baseline do projeto.
///
/// Implementa Happy Eyeballs (RFC 8305): tenta os endereços em paralelo,
/// escalonados, com vantagem inicial para o IPv6, e fica com o primeiro que
/// conectar. Nunca serializa as tentativas com timeout longo — era assim que
/// um host dual-stack com IPv6 quebrado ficava 30s travado antes de tentar
/// IPv4.
/// </summary>
public sealed class DirectTransport : ITransport
{
    /// <summary>
    /// Vantagem do IPv6 antes de a primeira tentativa IPv4 começar.
    /// A RFC 8305 recomenda 50ms; usamos um pouco mais porque queremos que o
    /// IPv6 ganhe quando ambos funcionam (§17.2).
    /// </summary>
    public static readonly TimeSpan AtrasoEntreTentativas = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Teto para a tentativa inteira. Um endereço que resolve mas não responde
    /// só falharia quando o TCP desistisse — minutos. Silêncio longo é a
    /// falha que este projeto não aceita, então o transporte impõe o próprio
    /// limite mesmo que o chamador esqueça.
    /// </summary>
    public TimeSpan Limite { get; set; } = TimeSpan.FromSeconds(15);

    public string Nome => "Direto (IPv6/IPv4)";

    public bool Disponivel => true;

    public async Task<IConexao> ConectarAsync(EnderecoServidor endereco, CancellationToken token)
    {
        var enderecos = await ResolverAsync(endereco.Host).ConfigureAwait(false);
        if (enderecos.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(Limite);
        var tentativas = new List<Task<TcpClient>>();
        var falhas = new List<Exception>();

        foreach (var ip in enderecos)
        {
            tentativas.Add(TentarAsync(ip, endereco.Porta, cts.Token));

            var espera = Task.WhenAny(tentativas);
            var concluida = await Task.WhenAny(espera, Task.Delay(AtrasoEntreTentativas, cts.Token))
                .ConfigureAwait(false);

            if (concluida == espera)
            {
                var vencedora = await espera.ConfigureAwait(false);
                if (vencedora.Status == TaskStatus.RanToCompletion)
                {
                    cts.Cancel();
                    return Envolver(vencedora.Result, endereco);
                }
                falhas.Add(vencedora.Exception!.GetBaseException());
                tentativas.Remove(vencedora);
            }
        }

        // Acabaram os endereços: espera o que ainda estiver em andamento.
        while (tentativas.Count > 0)
        {
            var concluida = await Task.WhenAny(tentativas).ConfigureAwait(false);
            tentativas.Remove(concluida);

            if (concluida.Status == TaskStatus.RanToCompletion)
            {
                cts.Cancel();
                return Envolver(concluida.Result, endereco);
            }
            falhas.Add(concluida.Exception!.GetBaseException());
        }

        if (cts.IsCancellationRequested && !token.IsCancellationRequested)
            throw new TimeoutException(
                $"{endereco} não respondeu em {Limite.TotalSeconds:N0}s. " +
                "Confira o endereço, se o coordenador está no ar e a regra de entrada " +
                "no firewall do anfitrião — IPv6 não tem NAT, mas tem firewall.");

        throw new AggregateException(
            $"Não foi possível conectar em {endereco}. " +
            "Se o servidor é IPv6, confira a regra de entrada no firewall do anfitrião — " +
            "IPv6 não tem NAT, mas tem firewall (§17.3 regra 7).",
            falhas);
    }

    static IConexao Envolver(TcpClient cliente, EnderecoServidor endereco) =>
        new ConexaoTcp(cliente, endereco);

    static async Task<TcpClient> TentarAsync(IPAddress ip, int porta, CancellationToken token)
    {
        var cliente = new TcpClient(ip.AddressFamily) { NoDelay = true };
        try
        {
            using (token.Register(() => cliente.Close()))
                await cliente.ConnectAsync(ip, porta).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            return cliente;
        }
        catch
        {
            cliente.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolve e ordena: IPv6 primeiro, depois IPv4, intercalando famílias
    /// para não gastar toda a escada numa família quebrada. Teredo e 6to4
    /// vão para o fim — presentes em muitas máquinas e instáveis na prática
    /// (§17.3 regra 8).
    /// </summary>
    static async Task<List<IPAddress>> ResolverAsync(string host)
    {
        IPAddress[] resolvidos = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);

        var v6 = resolvidos.Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 && !EhTunel(ip)).ToList();
        var v4 = resolvidos.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToList();
        var tuneis = resolvidos.Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 && EhTunel(ip)).ToList();

        var ordem = new List<IPAddress>();
        for (int i = 0; i < Math.Max(v6.Count, v4.Count); i++)
        {
            if (i < v6.Count) ordem.Add(v6[i]);
            if (i < v4.Count) ordem.Add(v4[i]);
        }
        ordem.AddRange(tuneis);
        return ordem;
    }

    static bool EhTunel(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return false;
        var bytes = ip.GetAddressBytes();
        // 2001:0000::/32 Teredo, 2002::/16 6to4
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) return true;
        return bytes[0] == 0x20 && bytes[1] == 0x02;
    }
}
