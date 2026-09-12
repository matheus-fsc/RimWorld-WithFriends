using System.Net;
using System.Net.Sockets;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server;
using WithFriends.Transport;
using Xunit;

namespace WithFriends.Tests;

/// <summary>§17.2–17.4 — IPv6 primeiro, IPv4 como degradação, nunca serializado.</summary>
public class DirectTransportTests
{
    static async Task<(TcpClient aceito, IConexao conexao)> Parear(Listener listener, string host)
    {
        listener.Start();
        int porta = listener.LocalEndPoint.Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var aceitando = listener.AcceptAsync(cts.Token);

        var conexao = await new DirectTransport()
            .ConectarAsync(new EnderecoServidor(host, porta), cts.Token);

        return (await aceitando, conexao);
    }

    [Fact]
    public async Task Prefere_IPv6_quando_os_dois_caminhos_funcionam()
    {
        // 'localhost' resolve para ::1 e 127.0.0.1; o listener é dual-stack.
        using var listener = new Listener(0);
        var (aceito, conexao) = await Parear(listener, "localhost");
        using (aceito)
        using (conexao)
        {
            var remoto = (IPEndPoint)aceito.Client.RemoteEndPoint!;
            Assert.Equal(AddressFamily.InterNetworkV6, remoto.AddressFamily);
            Assert.True(IPAddress.IPv6Loopback.Equals(remoto.Address),
                $"esperava chegar por ::1, chegou por {remoto.Address}");
        }
    }

    [Fact]
    public async Task Cai_para_IPv4_quando_o_IPv6_nao_atende()
    {
        // Listener só em IPv4: a tentativa IPv6 falha e a IPv4 vence, sem
        // esperar timeout longo (§17.3 regra 3).
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int porta = ((IPEndPoint)tcp.LocalEndpoint).Port;

        var inicio = DateTime.UtcNow;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var aceitando = tcp.AcceptTcpClientAsync();

        using var conexao = await new DirectTransport()
            .ConectarAsync(new EnderecoServidor("localhost", porta), cts.Token);
        using var aceito = await aceitando;

        var decorrido = DateTime.UtcNow - inicio;
        Assert.True(conexao.Conectado);
        Assert.True(decorrido < TimeSpan.FromSeconds(3),
            $"a queda para IPv4 levou {decorrido}; tentativas não podem ser serializadas com timeout longo.");
        tcp.Stop();
    }

    [Fact]
    public async Task Handshake_completo_pelo_transporte()
    {
        using var listener = new Listener(0);
        var (aceito, conexao) = await Parear(listener, "::1");
        using (aceito)
        using (conexao)
        {
            conexao.Enviar(new Handshake { PlayerId = "p1", DisplayName = "math" });

            // Lado servidor.
            var stream = aceito.GetStream();
            var recebido = Envelope.ReadFrom(stream).Decode(Handshake.Read);
            Assert.Equal("math", recebido.DisplayName);

            new HandshakeHandler("teste", Capabilities.Checkpoints)
                .Handle(recebido)
                .ToEnvelope()
                .WriteTo(stream);

            // Lado cliente: TentarReceber não bloqueia, então esperamos ativamente.
            Envelope resposta = default;
            var limite = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limite && !conexao.TentarReceber(out resposta))
                await Task.Delay(10);

            Assert.Equal(MessageId.HandshakeAceito, resposta.Id);
            Assert.Equal("teste", resposta.Decode(HandshakeAceito.Read).ServerName);
        }
    }

    [Fact]
    public async Task Mensagem_partida_em_dois_pacotes_e_remontada()
    {
        using var listener = new Listener(0);
        var (aceito, conexao) = await Parear(listener, "::1");
        using (aceito)
        using (conexao)
        {
            var alerta = new ColoniaAlerta
            {
                PlayerId = "p1",
                ColonyId = "c1",
                Tipo = TipoAlerta.ConteudoCongelado,
                Explicacao = new string('x', 5000),
            };

            using var ms = new MemoryStream();
            alerta.ToEnvelope().WriteTo(ms);
            var bytes = ms.ToArray();

            var stream = aceito.GetStream();
            stream.Write(bytes, 0, 10);            // cabeçalho + começo do payload
            stream.Flush();
            await Task.Delay(100);
            Assert.False(conexao.TentarReceber(out _));   // ainda incompleta

            stream.Write(bytes, 10, bytes.Length - 10);
            stream.Flush();

            Envelope envelope = default;
            var limite = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limite && !conexao.TentarReceber(out envelope))
                await Task.Delay(10);

            Assert.Equal(MessageId.ColoniaAlerta, envelope.Id);
            Assert.Equal(5000, envelope.Decode(ColoniaAlerta.Read).Explicacao.Length);
        }
    }

    [Fact]
    public async Task Endereco_que_nao_responde_falha_no_limite_e_explica()
    {
        // 203.0.113.0/24 é TEST-NET-3 (RFC 5737): roteável no papel, nunca
        // atendido na prática — o caso do endereço que resolve e não responde.
        var transporte = new DirectTransport { Limite = TimeSpan.FromSeconds(1) };
        var inicio = DateTime.UtcNow;

        var erro = await Assert.ThrowsAnyAsync<Exception>(() =>
            transporte.ConectarAsync(new EnderecoServidor("203.0.113.1", 25555), CancellationToken.None));

        var decorrido = DateTime.UtcNow - inicio;
        Assert.True(decorrido < TimeSpan.FromSeconds(5),
            $"deveria desistir perto de 1s, levou {decorrido}");
        Assert.Contains("não respondeu", erro.Message);
        Assert.Contains("firewall", erro.Message);
    }

    [Fact]
    public async Task Conexao_fechada_pelo_outro_lado_deixa_de_ser_conectada()
    {
        // TcpClient.Connected mente: continua true depois que o outro lado
        // fecha, até uma operação de I/O falhar. Sem detectar isso, tudo o que
        // for enviado some em silêncio — inclusive o checkpoint pré-sessão.
        using var listener = new Listener(0);
        var (aceito, conexao) = await Parear(listener, "::1");
        using (conexao)
        {
            Assert.True(conexao.Conectado);

            aceito.Close();   // o coordenador caiu
            await Task.Delay(200);

            // Sem precisar escrever nada: o FIN do outro lado já é detectável.
            Assert.False(conexao.Conectado);
        }
    }

    [Fact]
    public async Task Enviar_em_conexao_morta_falha_em_vez_de_sumir()
    {
        using var listener = new Listener(0);
        var (aceito, conexao) = await Parear(listener, "::1");
        using (conexao)
        {
            aceito.Close();
            await Task.Delay(200);

            var erro = Assert.Throws<IOException>(() => conexao.Enviar(new Handshake { PlayerId = "p1" }));
            Assert.Contains("não está mais viva", erro.Message);
        }
    }

    [Fact]
    public async Task Host_inexistente_falha_rapido_e_explica()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var erro = await Assert.ThrowsAnyAsync<Exception>(() =>
            new DirectTransport().ConectarAsync(new EnderecoServidor("::1", 1), cts.Token));

        Assert.Contains("firewall", erro.Message);
    }
}
