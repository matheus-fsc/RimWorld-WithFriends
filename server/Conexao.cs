using System.Net.Sockets;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Colonias;
using WithFriends.Server.Mundo;
using WithFriends.Server.Sessoes;

namespace WithFriends.Server;

/// <summary>
/// Atende um cliente: handshake e depois o laço de mensagens.
/// Mensagem desconhecida é registrada e pulada — o tamanho explícito no
/// envelope garante que o fluxo não perde o sincronismo (§9.1).
/// </summary>
public sealed class Conexao : IDestinatario
{
    readonly TcpClient cliente;
    readonly HandshakeHandler handshake;
    readonly CheckpointStore checkpoints;
    readonly LogDeEventos mundo;
    readonly Presenca presenca;
    readonly BrokerDeSessoes sessoes;
    readonly object travaDeEnvio = new();

    NetworkStream? stream;

    public Conexao(
        TcpClient cliente,
        HandshakeHandler handshake,
        CheckpointStore checkpoints,
        LogDeEventos mundo,
        Presenca presenca,
        BrokerDeSessoes sessoes)
    {
        this.cliente = cliente;
        this.handshake = handshake;
        this.checkpoints = checkpoints;
        this.mundo = mundo;
        this.presenca = presenca;
        this.sessoes = sessoes;
    }

    public string PlayerId { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    /// <summary>
    /// Enquanto o planeta não for conferido, nada de mundo trafega. Índice de
    /// tile de outro planeta não significa nada — e aplicar um faz o jogo do
    /// outro lado quebrar longe da causa.
    /// </summary>
    bool planetaCompativel;

    /// <summary>
    /// Envio é serializado: a difusão de eventos vem de outra thread, e duas
    /// mensagens intercaladas no mesmo socket corromperiam o enquadramento.
    /// </summary>
    public void Entregar(IMessage mensagem)
    {
        lock (travaDeEnvio)
            mensagem.ToEnvelope().WriteTo(stream ?? throw new InvalidOperationException("sem stream"));
    }

    public async Task AtenderAsync(CancellationToken token)
    {
        var origem = cliente.Client.RemoteEndPoint;
        bool registrado = false;

        try
        {
            using (cliente)
            {
                stream = cliente.GetStream();

                var primeiro = Envelope.ReadFrom(stream);
                if (primeiro.Id != MessageId.Handshake)
                {
                    Console.WriteLine($"[{origem}] esperava Handshake, veio {primeiro.Id} — encerrando");
                    return;
                }

                var hello = primeiro.Decode(Handshake.Read);
                var resposta = handshake.Handle(hello);
                Console.WriteLine($"[{origem}] {hello.DisplayName} ({hello.PlayerId}) → {resposta.Id}");
                Entregar(resposta);
                if (resposta is HandshakeRecusado) return;

                PlayerId = hello.PlayerId;
                DisplayName = hello.DisplayName;

                var deslocada = presenca.Entrou(this);
                deslocada?.Entregar(new SistemaErro
                {
                    Codigo = CodigoErro.IdentidadeAssumidaPorOutraConexao,
                    Explicacao =
                        $"Outra conexão entrou como {PlayerId}. Se são duas instâncias do jogo " +
                        "na mesma máquina, elas estão compartilhando a mesma pasta de dados — " +
                        "inicie a segunda com -savedatafolder=<outra pasta> para ter identidade própria.",
                });
                registrado = true;

                while (!token.IsCancellationRequested)
                {
                    Envelope envelope;
                    try
                    {
                        envelope = Envelope.ReadFrom(stream);
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }
                    catch (InvalidDataException e)
                    {
                        // Quadro inválido corrompe o sincronismo do fluxo: não
                        // dá para continuar lendo. Mas o jogador merece saber
                        // por quê (§9.1) — antes isto derrubava a conexão em
                        // silêncio e o outro lado só via "socket shut down".
                        Console.WriteLine($"[{origem}] quadro inválido: {e.Message}");
                        Entregar(new SistemaErro
                        {
                            Codigo = CodigoErro.FalhaAoProcessar,
                            Explicacao = $"O coordenador não conseguiu ler uma mensagem: {e.Message}",
                        });
                        break;
                    }

                    try
                    {
                        Processar(envelope, origem);
                    }
                    catch (Exception e) when (e is not IOException)
                    {
                        // Uma mensagem que falha ao ser processada não derruba
                        // a conexão: o fluxo continua sincronizado.
                        Console.WriteLine($"[{origem}] falha ao processar {envelope.Id}: {e.Message}");
                        Entregar(new SistemaErro
                        {
                            Codigo = CodigoErro.FalhaAoProcessar,
                            Explicacao = $"Falha ao processar {envelope.Id}: {e.Message}",
                        });
                    }
                }
            }
        }
        catch (IOException e)
        {
            Console.WriteLine($"[{origem}] conexão encerrada: {e.Message}");
        }
        finally
        {
            if (registrado)
            {
                // Sessão só existe com os dois presentes: cair encerra (§2.3).
                var fim = sessoes.ParticipanteSaiu(PlayerId);
                if (fim != null) DifundirNaSessao(fim.SessaoId, fim);
                presenca.Saiu(this);
            }
            Console.WriteLine($"[{origem}] desconectado");
        }
    }

    /// <summary>Entrega aos dois participantes: dentro da sessão, tudo é simétrico.</summary>
    void DifundirNaSessao(string sessaoId, IMessage mensagem)
    {
        var sessao = sessoes.PorId(sessaoId);
        if (sessao == null) return;

        foreach (string participante in sessao.Participantes)
            presenca.Para(participante)?.Entregar(mensagem);
    }

    void Processar(Envelope envelope, System.Net.EndPoint? origem)
    {
        var agora = DateTime.UtcNow;

        switch (envelope.Id)
        {
            case MessageId.ColoniaCheckpoint:
            {
                var checkpoint = envelope.Decode(ColoniaCheckpoint.Read);
                var entrada = checkpoints.Aceitar(checkpoint, agora);
                Console.WriteLine(
                    $"[{origem}] checkpoint {checkpoint.Identity} tick={entrada.GameTick} " +
                    $"{entrada.ContentHash}{(entrada.Suspeito ? " (SUSPEITO)" : "")}");
                foreach (var alerta in entrada.Alertas) Entregar(alerta);
                break;
            }

            case MessageId.ColoniaHeartbeat:
            {
                var heartbeat = envelope.Decode(ColoniaHeartbeat.Read);
                foreach (var alerta in checkpoints.Aceitar(heartbeat, agora)) Entregar(alerta);
                break;
            }

            case MessageId.ColoniaRestauracao:
            {
                var pedido = envelope.Decode(ColoniaRestauracao.Read);
                if (!pedido.EhPedido) break;   // resposta é coisa do cliente

                var resposta = checkpoints.Atender(pedido);
                Console.WriteLine(
                    $"[{origem}] restauração {pedido.Identity} {pedido.ContentHash}: " +
                    (resposta.Erro.Length > 0 ? $"negada ({resposta.Erro})" : $"{resposta.Conteudo.Length:N0} bytes"));
                Entregar(resposta);
                break;
            }

            case MessageId.MundoSincronizacaoCursor:
            {
                var pedido = envelope.Decode(MundoSincronizacaoCursor.Read);

                string? divergencia = mundo.ConferirPlaneta(pedido.Planeta);
                if (divergencia != null)
                {
                    // Não derruba ninguém: só recusa participar do mundo
                    // compartilhado, com motivo legível (§8, §9.1, §11).
                    Console.WriteLine($"[{origem}] planeta divergente: {divergencia}");
                    planetaCompativel = false;
                    Entregar(new SistemaErro
                    {
                        Codigo = CodigoErro.PlanetaDivergente,
                        Explicacao = divergencia,
                    });
                    break;
                }

                planetaCompativel = true;
                long cursor = pedido.Cursor;
                var pendentes = mundo.Desde(cursor);
                Console.WriteLine($"[{origem}] cursor={cursor} → {pendentes.Count} evento(s) de mundo");
                foreach (var evento in pendentes)
                    Entregar(new MundoEvento { Evento = evento });
                break;
            }

            case MessageId.MundoPublicar:
            {
                if (!planetaCompativel)
                {
                    Console.WriteLine($"[{origem}] evento de mundo descartado: planeta divergente");
                    break;
                }

                var proposta = envelope.Decode(MundoPublicar.Read);
                // O servidor numera; o autor é sempre quem está na conexão,
                // nunca o que o cliente diz ser.
                var evento = mundo.Publicar(PlayerId, proposta.Tipo, proposta.Payload);
                Console.WriteLine($"[{origem}] evento seq={evento.Seq} {evento.Tipo} por {evento.Autor}");
                presenca.Difundir(new MundoEvento { Evento = evento });
                break;
            }

            case MessageId.SessaoConvite:
            {
                var pedido = envelope.Decode(SessaoConvite.Read);
                var resposta = sessoes.Convidar(PlayerId, pedido, agora);
                if (resposta is SessaoConvite convite)
                {
                    Console.WriteLine($"[{origem}] convite {convite.ConviteId}: {PlayerId} → {convite.Para} ({convite.Tipo})");
                    presenca.Para(convite.Para)?.Entregar(convite);
                    Entregar(convite);   // o autor também precisa do id
                }
                else Entregar(resposta);
                break;
            }

            case MessageId.SessaoAceite:
            {
                var aceite = envelope.Decode(SessaoAceite.Read);
                var resposta = sessoes.Aceitar(PlayerId, aceite, agora);
                if (resposta is SessaoInicio inicio)
                {
                    Console.WriteLine($"[{origem}] sessão {inicio.SessaoId} iniciada: {inicio.Anfitriao} + {inicio.Visitante}");
                    presenca.Para(inicio.Anfitriao)?.Entregar(inicio);
                    presenca.Para(inicio.Visitante)?.Entregar(inicio);
                }
                else Entregar(resposta);
                break;
            }

            case MessageId.SessaoRecusa:
            {
                var recusa = envelope.Decode(SessaoRecusa.Read);
                sessoes.Recusar(recusa.ConviteId);
                Console.WriteLine($"[{origem}] convite {recusa.ConviteId} recusado");
                break;
            }

            case MessageId.SessaoComando:
            {
                var agendado = sessoes.Agendar(
                    PlayerId, envelope.Decode(SessaoComando.Read), out string? recusa);

                if (agendado != null) DifundirNaSessao(agendado.SessaoId, agendado);
                else if (recusa != null)
                    Entregar(new SistemaErro
                    {
                        Codigo = CodigoErro.SemAutoridade,
                        Explicacao = recusa,
                    });
                break;
            }

            case MessageId.SessaoBarreira:
            {
                var relato = envelope.Decode(SessaoBarreira.Read);
                var resposta = sessoes.Barreira(PlayerId, relato);
                if (resposta is SessaoAborto aborto)
                {
                    Console.WriteLine($"!! sessão {aborto.SessaoId} ABORTADA: {aborto.Explicacao}");
                    DifundirNaSessao(aborto.SessaoId, aborto);
                }
                else if (resposta != null)
                {
                    DifundirNaSessao(relato.SessaoId, resposta);
                }
                break;
            }

            case MessageId.SessaoMapa:
            {
                // O coordenador repassa sem interpretar (§10): ele não sabe o
                // que é um mapa de RimWorld, e não precisa saber.
                var mapa = envelope.Decode(SessaoMapa.Read);
                var sessao = sessoes.PorId(mapa.SessaoId);
                if (sessao == null || !sessao.Tem(PlayerId)) break;

                Console.WriteLine(
                    $"[{origem}] mapa da sessão {mapa.SessaoId}: {mapa.Comprimido.Length:N0} bytes " +
                    $"comprimidos (de {mapa.TamanhoCru:N0}) → {sessao.Outro(PlayerId)}");
                presenca.Para(sessao.Outro(PlayerId))?.Entregar(mapa);
                break;
            }

            case MessageId.SessaoPartida:
            {
                // Repassa sem interpretar (§10): o coordenador não sabe o que
                // é um save de RimWorld, e não precisa saber.
                var partida = envelope.Decode(SessaoPartida.Read);
                var sessaoDaPartida = sessoes.PorId(partida.SessaoId);
                if (sessaoDaPartida == null || !sessaoDaPartida.Tem(PlayerId)) break;

                Console.WriteLine(
                    $"[{origem}] partida da sessão {partida.SessaoId}: " +
                    $"{partida.Comprimido.Length:N0} bytes comprimidos (de {partida.TamanhoCru:N0}) " +
                    $"→ {sessaoDaPartida.Outro(PlayerId)}");
                presenca.Para(sessaoDaPartida.Outro(PlayerId))?.Entregar(partida);
                break;
            }

            case MessageId.SessaoFim:
            {
                var pedido = envelope.Decode(SessaoFim.Read);
                string porque = pedido.Explicacao.Length > 0 ? $" — {pedido.Explicacao}" : "";
                var fim = sessoes.Encerrar(pedido.SessaoId, MotivoFimDeSessao.EncerradaPelosParticipantes,
                    $"{PlayerId} encerrou a sessão.{porque}");
                // O motivo fica aqui de propósito: é o único log que sobrevive
                // às duas instâncias do jogo dividirem o mesmo Player.log.
                Console.WriteLine($"[{origem}] sessão {fim.SessaoId} encerrada por {PlayerId}{porque}");
                DifundirNaSessao(fim.SessaoId, fim);
                break;
            }

            default:
                // §9.1: ignorada com log, nunca derruba a conexão.
                Console.WriteLine($"[{origem}] mensagem não tratada: {envelope.Id} ({envelope.Payload.Length} bytes) — ignorada");
                break;
        }
    }
}
