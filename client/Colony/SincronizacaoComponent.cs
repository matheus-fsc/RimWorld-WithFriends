using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using WithFriends.Client.Net;
using WithFriends.Client.World;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Colony;

/// <summary>
/// Liga a colônia ao coordenador: manda heartbeat na cadência acordada e
/// mostra os alertas do servidor **dentro do jogo**, não só no log.
///
/// A §7.1 regra 3 diz que o servidor nunca aceita em silêncio. Alerta que
/// chega e ninguém vê é silêncio com passos extras — foi assim que o
/// incidente da §15.1 passou cinco horas despercebido.
/// </summary>
public class SincronizacaoComponent : GameComponent
{
    /// <summary>Hash do último checkpoint enviado. Só para diagnóstico na UI.</summary>
    public string UltimoContentHash = "";

    /// <summary>
    /// Ponto de retorno da sessão (§2.3). Vive **dentro do save**: sair e
    /// voltar no meio de uma sessão não pode perder o rollback, e o valor
    /// pertence a esta colônia, não ao processo.
    /// </summary>
    public string HashPreSessao = "";

    /// <summary>Se esta partida está congelada por nós.</summary>
    public bool Congelado;

    /// <summary>Velocidade a devolver ao descongelar.</summary>
    public TimeSpeed VelocidadeAnterior = TimeSpeed.Normal;

    float proximoHeartbeat;
    bool apresentado;

    /// <summary>Quem está online agora, por <c>player_id</c>. Estado volátil.</summary>
    public static readonly Dictionary<string, string> Online = new();

    /// <summary>
    /// Espera entre tentativas automáticas. Curta o bastante para não estorvar,
    /// longa o bastante para um coordenador fora do ar não encher o log.
    /// </summary>
    static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(15);

    static float proximaTentativa;

    /// <summary>
    /// A sessão pertence à partida, não ao processo. Estado de sessão que
    /// sobrevive a um LoadGame já causou bug uma vez (ver o congelador).
    /// </summary>
    public Session.SessaoCliente Sessao { get; } = new();

    public SincronizacaoComponent(Game game) { }

    public static SincronizacaoComponent? Atual => Current.Game?.GetComponent<SincronizacaoComponent>();

    /// <summary>
    /// De quem é cada pawn durante a visita — ver <see cref="Session.PosseDePawns"/>.
    ///
    /// Vive aqui, e não na sessão, porque daqui ele vai para o save; e o save é
    /// o que viaja para o outro lado (ADR 0010). A posse chega junto, com os
    /// mesmos ids, sem mensagem nova.
    /// </summary>
    public Dictionary<int, string> DonoPorPawn = new();

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref DonoPorPawn, "donoPorPawn", LookMode.Value, LookMode.Value);
        DonoPorPawn ??= new Dictionary<int, string>();
        Scribe_Values.Look(ref HashPreSessao, "hashPreSessao", "");
        Scribe_Values.Look(ref Congelado, "congelado", false);
        Scribe_Values.Look(ref VelocidadeAnterior, "velocidadeAnterior", TimeSpeed.Normal);
    }

    public override void GameComponentUpdate()
    {
        var cliente = WithFriendsMod.Cliente;

        TentarConectarSozinho(cliente);

        while (cliente.TentarReceber(out var envelope))
            Processar(envelope);

        if (!cliente.Conectado || Current.Game == null)
        {
            apresentado = false;
            return;
        }

        if (!apresentado)
        {
            apresentado = true;
            Apresentar(cliente);
        }

        // Acabamos de entrar na partida do anfitrião: a sessão vive fora da
        // partida justamente para poder ser retomada aqui.
        //
        // JaTrocouDePartida é essencial: LoadGame enfileira a carga, e a
        // partida antiga ainda atualiza por vários frames. Sem isso a sessão
        // era retomada dentro da colônia do próprio visitante.
        if (Session.VisitaEmAndamento.JaTrocouDePartida
            && !Session.VisitaEmAndamento.Retomada
            && Sessao.Estado == Session.EstadoSessaoLocal.Fora)
        {
            Session.VisitaEmAndamento.Retomada = true;
            Sessao.Retomar(Session.VisitaEmAndamento.Inicio!);
        }

        Sessao.Atualizar();

        if (Time.realtimeSinceStartup < proximoHeartbeat) return;
        proximoHeartbeat = Time.realtimeSinceStartup + (float)Heartbeat.IntervaloPadrao.TotalSeconds;

        cliente.Enviar(CheckpointWriter.Heartbeat());
    }

    /// <summary>
    /// Conecta sozinho ao entrar numa partida, se o jogador quiser.
    ///
    /// Não insiste de forma agressiva: se o coordenador estiver fora do ar,
    /// tenta de novo a cada 15s em vez de a cada frame. Jogar sozinho nunca
    /// depende disso (§11), então falhar é só uma linha de log.
    /// </summary>
    static void TentarConectarSozinho(Net.ClienteCoordenador cliente)
    {
        if (!WithFriendsMod.Settings.conectarAoIniciar) return;
        if (cliente.Estado is EstadoConexao.Conectado or EstadoConexao.Conectando) return;
        if (Time.realtimeSinceStartup < proximaTentativa) return;

        proximaTentativa = Time.realtimeSinceStartup + (float)EsperaEntreTentativas.TotalSeconds;

        if (!Net.Conexao.Conectar(out string erro))
            Log.Message($"[WithFriends] conexão automática adiada: {erro}");
    }

    /// <summary>
    /// Ao conectar: pede o que passou desde o próprio cursor e publica a
    /// própria colônia. Nesta ordem — assim o log já chega com contexto.
    /// </summary>
    void Apresentar(Net.ClienteCoordenador cliente)
    {
        long cursor = Current.Game.GetComponent<WorldCursorComponent>()?.WorldCursor ?? 0;
        Log.Message($"[WithFriends] sincronizando mundo a partir do cursor {cursor}");
        cliente.Enviar(new MundoSincronizacaoCursor
        {
            Cursor = cursor,
            Planeta = World.IdentidadeDoPlaneta.Calcular(),
        });
        MundoPublicador.PublicarProprioAssentamento();
    }

    static void Processar(Envelope envelope)
    {
        switch (envelope.Id)
        {
            case MessageId.ColoniaAlerta:
                Alertar(envelope.Decode(ColoniaAlerta.Read));
                break;

            case MessageId.MundoEvento:
                MundoAplicador.Aplicar(envelope.Decode(MundoEvento.Read).Evento);
                break;

            case MessageId.ColoniaRestauracao:
                Session.CongeladorRimWorld.Receber(envelope.Decode(ColoniaRestauracao.Read));
                break;

            case MessageId.Erro:
                Falhar(envelope.Decode(SistemaErro.Read));
                break;

            case MessageId.MundoPresenca:
            {
                var presenca = envelope.Decode(MundoPresenca.Read);
                if (presenca.Online) Online[presenca.PlayerId] = presenca.DisplayName;
                else Online.Remove(presenca.PlayerId);
                MundoAplicador.AtualizarPresenca(presenca);
                break;
            }

            case MessageId.SessaoConvite:
                Atual?.Sessao.Receber(envelope.Decode(SessaoConvite.Read));
                break;

            case MessageId.SessaoRecusa:
            {
                var recusa = envelope.Decode(SessaoRecusa.Read);
                Log.Warning($"[WithFriends] sessão recusada: {recusa.Explicacao}");
                Messages.Message($"Sessão recusada: {recusa.Explicacao}",
                    MessageTypeDefOf.RejectInput, historical: false);
                break;
            }

            case MessageId.SessaoInicio:
                Atual?.Sessao.Iniciar(envelope.Decode(SessaoInicio.Read));
                break;

            case MessageId.SessaoBarreira:
                Atual?.Sessao.Barreira(envelope.Decode(SessaoBarreira.Read));
                break;

            case MessageId.SessaoComando:
                Atual?.Sessao.Agendar(envelope.Decode(SessaoComando.Read));
                break;

            case MessageId.SessaoFim:
                Atual?.Sessao.Encerrar(envelope.Decode(SessaoFim.Read));
                break;

            case MessageId.SessaoMapa:
                Session.BootstrapDeMapa.Receber(envelope.Decode(SessaoMapa.Read));
                break;

            case MessageId.SessaoPartida:
                Atual?.Sessao.PartidaRecebida(envelope.Decode(SessaoPartida.Read));
                break;

            case MessageId.SessaoAborto:
                Atual?.Sessao.Abortar(envelope.Decode(SessaoAborto.Read));
                break;

            default:
                // §9.1: desconhecida é registrada e ignorada, nunca fatal.
                Log.Message($"[WithFriends] mensagem não tratada: {envelope.Id} ({envelope.Payload.Length} bytes)");
                break;
        }
    }

    static void Alertar(ColoniaAlerta alerta)
    {
        Log.Warning($"[WithFriends] ALERTA {alerta.Tipo}: {alerta.Explicacao}");
        Messages.Message(
            $"With Friends — {Titulo(alerta.Tipo)}",
            MessageTypeDefOf.NegativeEvent,
            historical: true);
        // Uma carta força o jogador a ler: alerta de integridade não é
        // notificação passageira.
        Find.LetterStack.ReceiveLetter(
            $"With Friends: {Titulo(alerta.Tipo)}",
            alerta.Explicacao,
            LetterDefOf.NegativeEvent);
    }

    /// <summary>
    /// Erro do servidor: nunca some no log. O jogador precisa saber que
    /// parou de estar conectado e por quê (§9.1).
    /// </summary>
    static void Falhar(SistemaErro erro)
    {
        Log.Warning($"[WithFriends] erro do coordenador ({erro.Codigo}): {erro.Explicacao}");

        // Planeta divergente não derruba: o jogador continua conectado,
        // guardando checkpoint e jogando. Só o mundo compartilhado fica fora.
        bool fatal = erro.Codigo != CodigoErro.PlanetaDivergente;

        Find.LetterStack.ReceiveLetter(
            fatal ? "With Friends: conexão encerrada" : "With Friends: planeta diferente",
            erro.Explicacao +
            (fatal ? "" : "\n\nSeu planeta: " + World.IdentidadeDoPlaneta.Descrever()),
            LetterDefOf.NegativeEvent);

        if (fatal) WithFriendsMod.Cliente.Desconectar();
    }

    static string Titulo(TipoAlerta tipo) => tipo switch
    {
        TipoAlerta.TickRegrediu => "o tick do jogo regrediu",
        TipoAlerta.ConteudoCongelado => "o save enviado não está mudando",
        TipoAlerta.SimulacaoCongelada => "a simulação parece congelada",
        TipoAlerta.HashNaoConfere => "o hash do checkpoint não confere",
        TipoAlerta.DivergenciaDeAutoridade => "cliente e servidor divergem",
        _ => tipo.ToString(),
    };
}
