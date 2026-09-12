using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Verse;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Transport;

namespace WithFriends.Client.Net;

public enum EstadoConexao
{
    Desconectado,
    Conectando,
    Conectado,
    Falhou,
}

/// <summary>
/// Ligação do mod com o coordenador. Todo o tráfego acontece numa thread
/// própria: um checkpoint tem megabytes, e escrever isso na thread do jogo
/// travaria o frame.
///
/// A thread do jogo só faz duas coisas: enfileirar mensagem de saída e
/// drenar a fila de entrada.
/// </summary>
public sealed class ClienteCoordenador : IDisposable
{
    /// <summary>
    /// Teto para a fase de conexão. Sem isto, um endereço que resolve mas não
    /// responde deixa o estado em "Conectando" até o TCP desistir sozinho —
    /// minutos de silêncio, que é exatamente o que este projeto não aceita.
    /// </summary>
    public static readonly TimeSpan LimiteDeConexao = TimeSpan.FromSeconds(15);

    readonly BlockingCollection<IMessage> saida = new(new ConcurrentQueue<IMessage>());
    readonly ConcurrentQueue<Envelope> entrada = new();

    CancellationTokenSource? cts;
    Thread? thread;
    IConexao? conexao;

    public EstadoConexao Estado { get; private set; } = EstadoConexao.Desconectado;
    public string UltimoErro { get; private set; } = "";
    public string Descricao { get; private set; } = "";
    public Capabilities CapacidadesNegociadas { get; private set; }

    public bool Conectado => Estado == EstadoConexao.Conectado;

    public void Conectar(EnderecoServidor endereco, string playerId, string displayName)
    {
        Desconectar();

        Estado = EstadoConexao.Conectando;
        UltimoErro = "";
        Descricao = endereco.ToString();
        cts = new CancellationTokenSource();

        thread = new Thread(() => Rodar(endereco, playerId, displayName, cts.Token))
        {
            IsBackground = true,
            Name = "WithFriends-rede",
        };
        thread.Start();
    }

    public void Desconectar()
    {
        cts?.Cancel();
        conexao?.Dispose();
        conexao = null;
        thread = null;
        cts = null;
        if (Estado != EstadoConexao.Falhou) Estado = EstadoConexao.Desconectado;
    }

    /// <summary>
    /// Enfileira para envio. Nunca bloqueia a thread do jogo.
    /// Devolve <c>false</c> quando não há conexão viva — quem chamou precisa
    /// saber que a mensagem não vai a lugar nenhum.
    /// </summary>
    public bool Enviar(IMessage mensagem)
    {
        // **Árbitro não dá ordem.**
        //
        // Ele existe para simular e comparar digitais; um comando saindo dali
        // seria uma terceira vontade numa visita de dois. Em tese ele nem
        // geraria — não há interface para clicar — mas "em tese" é frágil demais
        // para o papel de referência: se ele mandar comando, ele deixa de ser
        // referência e vira participante.
        if (Session.ModoArbitro.Ativo && mensagem.Id == MessageId.SessaoComando)
        {
            Log.Warning("[WithFriends] árbitro tentou propor um comando — recusado.");
            return false;
        }

        if (Estado is not (EstadoConexao.Conectado or EstadoConexao.Conectando))
        {
            Log.Warning($"[WithFriends] {mensagem.Id} não enviada: sem conexão (estado: {Estado}).");
            return false;
        }

        saida.Add(mensagem);
        return true;
    }

    /// <summary>Drena uma mensagem recebida, se houver. Chamar da thread do jogo.</summary>
    public bool TentarReceber(out Envelope envelope) => entrada.TryDequeue(out envelope);

    void Rodar(EnderecoServidor endereco, string playerId, string displayName, CancellationToken token)
    {
        try
        {
            using (var limite = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limite.CancelAfter(LimiteDeConexao);
                try
                {
                    conexao = ConnectorRegistry.Padrao
                        .ConectarAsync(endereco, limite.Token)
                        .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Falhar(
                        $"{endereco} não respondeu em {LimiteDeConexao.TotalSeconds:N0}s. " +
                        "Confira o endereço, se o coordenador está no ar e a regra de " +
                        "entrada no firewall do anfitrião.");
                    return;
                }
            }

            Descricao = conexao.Descricao;

            conexao.Enviar(new Handshake
            {
                PlayerId = playerId,
                DisplayName = displayName,
                Capabilities = Capabilities.Checkpoints | Capabilities.WorldEvents,
                WorldModSetHash = Colony.ModSetHash.Calcular(),
            });

            var resposta = EsperarResposta(token);
            if (resposta.Id == MessageId.HandshakeRecusado)
            {
                var recusa = resposta.Decode(HandshakeRecusado.Read);
                // §9.1: a recusa sempre traz motivo legível — mostrar, não engolir.
                Falhar($"{recusa.Motivo}: {recusa.Explicacao}");
                return;
            }

            var aceito = resposta.Decode(HandshakeAceito.Read);
            CapacidadesNegociadas = aceito.Capabilities;
            Estado = EstadoConexao.Conectado;
            Log.Message($"[WithFriends] conectado a {aceito.ServerName} ({Descricao}), caps={aceito.Capabilities}");

            Laco(token);

            // Sair do laço sem cancelamento significa que o outro lado fechou.
            // Ficar em "Conectado" depois disso faria todo envio sumir em
            // silêncio — inclusive o checkpoint pré-sessão.
            if (!token.IsCancellationRequested)
                Falhar($"A conexão com {Descricao} caiu. Reconecte antes de continuar.");
        }
        catch (OperationCanceledException)
        {
            // Desconexão pedida pelo jogador.
        }
        catch (Exception e)
        {
            Falhar(e.GetBaseException().Message);
        }
    }

    Envelope EsperarResposta(CancellationToken token)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            token.ThrowIfCancellationRequested();
            if (conexao!.TentarReceber(out var envelope)) return envelope;
            Thread.Sleep(20);
        }
        throw new TimeoutException("O coordenador não respondeu ao handshake em 15s.");
    }

    void Laco(CancellationToken token)
    {
        while (!token.IsCancellationRequested && conexao!.Conectado)
        {
            if (saida.TryTake(out var mensagem, millisecondsTimeout: 50))
            {
                try
                {
                    conexao.Enviar(mensagem);
                }
                catch (Exception e)
                {
                    Falhar($"Falha ao enviar {mensagem.Id}: {e.Message}");
                    return;
                }
            }

            try
            {
                while (conexao.TentarReceber(out var envelope))
                    entrada.Enqueue(envelope);
            }
            catch (Exception e)
            {
                Falhar($"Falha ao receber do coordenador: {e.Message}");
                return;
            }
        }
    }

    void Falhar(string motivo)
    {
        UltimoErro = motivo;
        Estado = EstadoConexao.Falhou;
        Log.Warning($"[WithFriends] conexão falhou: {motivo}");
    }

    public void Dispose()
    {
        Desconectar();
        saida.Dispose();
    }
}
