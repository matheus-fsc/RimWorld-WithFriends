using System.Text;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Colonias;
using Xunit;

namespace WithFriends.Tests;

/// <summary>
/// §7 — durabilidade e integridade. O critério de M2 está em
/// <see cref="Incidente_de_save_orfao_detectado_em_menos_de_um_minuto"/>.
/// </summary>
public class DurabilidadeTests
{
    static readonly DateTime T0 = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    readonly AlertasEmMemoria alertas = new();
    readonly ArmazenamentoEmMemoria armazenamento = new();
    readonly CheckpointStore store;

    public DurabilidadeTests() =>
        store = new CheckpointStore(armazenamento, new MonitorIntegridade(alertas), alertas);

    static byte[] Save(string conteudo) => Encoding.UTF8.GetBytes(conteudo);

    static ColoniaCheckpoint Checkpoint(long tick, string conteudo, bool preSessao = false)
    {
        var bytes = Save(conteudo);
        return new ColoniaCheckpoint
        {
            PlayerId = "jogador-1",
            ColonyId = "colonia-a",
            Conteudo = bytes,
            Metadata = new CheckpointMetadata
            {
                GameTick = tick,
                WorldCursor = 0,
                ModSetHash = "sha256:mods",
                ContentHash = Hashing.OfBytes(bytes),
                WallClock = T0,
                PreSessao = preSessao,
            },
        };
    }

    [Fact]
    public void Incidente_de_save_orfao_detectado_em_menos_de_um_minuto()
    {
        // §15.1 reproduzido: o modo Morte Permanente renomeou o save, o
        // arquivo indexado ficou órfão, e o cliente passou ~5 horas enviando
        // o mesmo estado congelado com carimbo de tempo novo. Nada avisou.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        string congelado = Hashing.OfString("estado que nunca muda");

        var inicio = T0;
        store.Aceitar(new ColoniaHeartbeat
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            GameTick = 100_000,
            StateFingerprint = congelado,
        }, inicio);

        // O jogo segue rodando: o tick avança a cada heartbeat, o conteúdo não.
        DateTime? detectadoEm = null;
        for (int i = 1; i <= 10 && detectadoEm is null; i++)
        {
            var agora = inicio + TimeSpan.FromTicks(Heartbeat.IntervaloPadrao.Ticks * i);
            var encontrados = store.Aceitar(new ColoniaHeartbeat
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                GameTick = 100_000 + i * 2_500,   // ~1 min de jogo por heartbeat
                StateFingerprint = congelado,     // ... e nada no estado muda
            }, agora);

            if (encontrados.Any(a => a.Tipo == TipoAlerta.SimulacaoCongelada))
                detectadoEm = agora;
        }

        Assert.NotNull(detectadoEm);

        var latencia = detectadoEm!.Value - inicio;
        Assert.True(latencia < TimeSpan.FromMinutes(1),
            $"critério de M2: detectar em menos de 1 minuto. Levou {latencia}.");

        var alerta = Assert.Single(alertas.Todos, a => a.Tipo == TipoAlerta.SimulacaoCongelada);
        Assert.Contains("não muda", alerta.Explicacao);
    }

    [Fact]
    public void Tick_regredido_e_aceito_marcado_suspeito_e_alertado()
    {
        // §7.1 regra 3: nunca aceita em silêncio — mas também nunca recusa,
        // porque perder progresso é pior do que guardar algo suspeito.
        store.Aceitar(Checkpoint(tick: 500_000, "estado novo"), T0);

        var entrada = store.Aceitar(Checkpoint(tick: 400_000, "estado antigo"), T0.AddMinutes(1));

        Assert.True(entrada.Suspeito);
        Assert.Contains(alertas.Todos, a => a.Tipo == TipoAlerta.TickRegrediu);
        // Aceito: está no histórico e o conteúdo é recuperável.
        Assert.Equal(2, store.Historico(new ColonyIdentity("jogador-1", "colonia-a")).Count);
        Assert.Equal(Save("estado antigo"), store.Recuperar(entrada.ContentHash));
    }

    [Fact]
    public void Checkpoint_nunca_sobrescreve()
    {
        // §7.1 regra 1: append-only, endereçado por hash.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        var primeiro = store.Aceitar(Checkpoint(100, "estado 1"), T0);
        var segundo = store.Aceitar(Checkpoint(200, "estado 2"), T0.AddHours(1));

        Assert.NotEqual(primeiro.ContentHash, segundo.ContentHash);
        Assert.Equal(2, store.Historico(identity).Count);
        Assert.Equal(Save("estado 1"), store.Recuperar(primeiro.ContentHash));
        Assert.Equal(Save("estado 2"), store.Recuperar(segundo.ContentHash));
    }

    [Fact]
    public void Hash_declarado_errado_vale_o_conteudo_e_alerta()
    {
        var bytes = Save("o conteúdo de verdade");
        var entrada = store.Aceitar(new ColoniaCheckpoint
        {
            PlayerId = "jogador-1",
            ColonyId = "colonia-a",
            Conteudo = bytes,
            Metadata = new CheckpointMetadata
            {
                GameTick = 1,
                ContentHash = "sha256:mentira",
                ModSetHash = "sha256:mods",
                WallClock = T0,
            },
        }, T0);

        Assert.True(entrada.Suspeito);
        Assert.Equal(Hashing.OfBytes(bytes), entrada.ContentHash);
        Assert.Contains(alertas.Todos, a => a.Tipo == TipoAlerta.HashNaoConfere);
        Assert.Equal(bytes, store.Recuperar(entrada.ContentHash));
    }

    [Fact]
    public void Identidade_e_player_e_colony_nunca_o_nome_do_arquivo()
    {
        // §7.1 regra 5 / §15.1: o save pode ser renomeado (Morte Permanente
        // renomeia) sem que a colônia mude de identidade; e duas colônias do
        // mesmo jogador não se misturam.
        var colonia = new ColonyIdentity("jogador-1", "colonia-a");
        var outra = new ColonyIdentity("jogador-1", "colonia-b");

        store.Aceitar(Checkpoint(100, "antes do rename"), T0);
        store.Aceitar(Checkpoint(200, "depois do rename"), T0.AddHours(1));
        store.Aceitar(new ColoniaCheckpoint
        {
            PlayerId = "jogador-1",
            ColonyId = "colonia-b",
            Conteudo = Save("outra colônia"),
            Metadata = new CheckpointMetadata
            {
                GameTick = 50,
                ContentHash = Hashing.OfBytes(Save("outra colônia")),
                ModSetHash = "sha256:mods",
                WallClock = T0,
            },
        }, T0.AddHours(2));

        Assert.Equal(2, store.Historico(colonia).Count);
        Assert.Single(store.Historico(outra));
        // O tick 50 da colônia B não dispara alerta de regressão da colônia A.
        Assert.DoesNotContain(alertas.Todos, a => a.Tipo == TipoAlerta.TickRegrediu);
    }

    [Fact]
    public void Jogo_pausado_nao_e_regressao_de_tick()
    {
        // Falso positivo encontrado em jogo: com a partida pausada, dois
        // heartbeats seguidos carregam o mesmo tick. Isso é estado normal em
        // RimWorld — só tick MENOR denuncia save antigo carregado por cima.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");

        store.Aceitar(new ColoniaHeartbeat
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            GameTick = 21_864,
            StateFingerprint = "sha256:estado-1",
        }, T0);

        var encontrados = store.Aceitar(new ColoniaHeartbeat
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            GameTick = 21_864,                 // pausado: mesmo tick
            StateFingerprint = "sha256:estado-1", // e mesmo estado
        }, T0.AddSeconds(30));

        Assert.Empty(encontrados);
        Assert.Empty(alertas.Todos);
    }

    [Fact]
    public void Checkpoints_espacados_nao_alertam_entre_si()
    {
        // Outro falso positivo encontrado em jogo: entre dois checkpoints o
        // hash do save fica igual por construção. Só é sintoma quando um
        // checkpoint NOVO repete o conteúdo do anterior com o tick adiantado.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");

        store.Aceitar(Checkpoint(tick: 2_265, "estado no tick 2265"), T0);

        // Jogo roda 15 minutos; heartbeats mostram estado sempre diferente.
        for (int i = 1; i <= 30; i++)
        {
            store.Aceitar(new ColoniaHeartbeat
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                GameTick = 2_265 + i * 500,
                StateFingerprint = $"sha256:estado-{i}",
            }, T0.AddSeconds(30 * i));
        }

        // Só então vem o próximo checkpoint, com conteúdo novo.
        store.Aceitar(Checkpoint(tick: 17_265, "estado no tick 17265"), T0.AddMinutes(15));

        Assert.Empty(alertas.Todos);
    }

    [Fact]
    public void Checkpoint_novo_repetindo_conteudo_antigo_alerta()
    {
        // O sintoma real da §15.1: o cliente reenvia o MESMO arquivo com o
        // tick já adiantado, porque o save que ele acompanha ficou órfão.
        store.Aceitar(Checkpoint(tick: 1_000, "estado congelado"), T0);

        var entrada = store.Aceitar(Checkpoint(tick: 50_000, "estado congelado"), T0.AddHours(1));

        Assert.True(entrada.Suspeito);
        Assert.Contains(alertas.Todos, a => a.Tipo == TipoAlerta.ConteudoCongelado);
    }

    [Fact]
    public void Alerta_nao_se_repete_a_cada_heartbeat()
    {
        // Alerta que se repete sozinho vira ruído, e ruído treina o jogador a
        // ignorar — pior do que não alertar. Uma vez por episódio.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        string congelada = "sha256:estado-travado";

        for (int i = 0; i <= 10; i++)
        {
            store.Aceitar(new ColoniaHeartbeat
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                GameTick = 1_000 + i * 2_500,
                StateFingerprint = congelada,
            }, T0.AddSeconds(30 * i));
        }

        Assert.Single(alertas.Todos, a => a.Tipo == TipoAlerta.SimulacaoCongelada);
    }

    [Fact]
    public void Alerta_e_rearmado_depois_que_a_colonia_normaliza()
    {
        var identity = new ColonyIdentity("jogador-1", "colonia-a");

        void Bater(long tick, string estado, int segundos) =>
            store.Aceitar(new ColoniaHeartbeat
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                GameTick = tick,
                StateFingerprint = estado,
            }, T0.AddSeconds(segundos));

        Bater(1_000, "sha256:a", 0);
        Bater(3_500, "sha256:a", 30);   // congelou → alerta 1
        Bater(6_000, "sha256:a", 60);   // continua congelado → silêncio
        Bater(8_500, "sha256:b", 90);   // normalizou → rearma
        Bater(11_000, "sha256:b", 120); // congelou de novo → alerta 2

        Assert.Equal(2, alertas.Todos.Count(a => a.Tipo == TipoAlerta.SimulacaoCongelada));
    }

    [Fact]
    public void Depois_de_alertar_regressao_o_monitoramento_volta_ao_normal()
    {
        // Encontrado em jogo: ticks futuros elevaram a referência do servidor,
        // e cada heartbeat verdadeiro seguinte — com tick menor — alertava de
        // novo, para sempre. O cliente é autoridade sobre a própria colônia
        // (§7.3): o servidor nota, avisa uma vez e readota a referência.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");

        void Bater(long tick, string estado, int segundos) =>
            store.Aceitar(new ColoniaHeartbeat
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                GameTick = tick,
                StateFingerprint = estado,
            }, T0.AddSeconds(30 * segundos));

        Bater(9_163, "sha256:a", 0);    // referência alta
        Bater(1_871, "sha256:b", 1);    // regressão → alerta
        Bater(1_890, "sha256:c", 2);    // tick real seguindo daí
        Bater(1_920, "sha256:d", 3);

        Assert.Single(alertas.Todos, a => a.Tipo == TipoAlerta.TickRegrediu);
    }

    [Fact]
    public void Restauracao_devolve_o_conteudo_pedido()
    {
        // §7.3: o servidor entrega quando pedido. Quem decide aplicar é o
        // jogador — o servidor nunca impõe uma versão do save.
        var entrada = store.Aceitar(Checkpoint(tick: 1_000, "estado que vou querer de volta"), T0);

        var resposta = store.Atender(new ColoniaRestauracao
        {
            PlayerId = "jogador-1",
            ColonyId = "colonia-a",
            ContentHash = entrada.ContentHash,
        });

        Assert.Empty(resposta.Erro);
        Assert.Equal(Save("estado que vou querer de volta"), resposta.Conteudo);
        Assert.Equal(entrada.ContentHash, Hashing.OfBytes(resposta.Conteudo));
    }

    [Fact]
    public void Restauracao_de_checkpoint_de_outra_colonia_e_negada()
    {
        // Identidade é (player_id, colony_id): o checkpoint de uma colônia
        // não é entregue para outra, mesmo do mesmo jogador.
        var entrada = store.Aceitar(Checkpoint(tick: 1_000, "estado da colônia A"), T0);

        var resposta = store.Atender(new ColoniaRestauracao
        {
            PlayerId = "jogador-1",
            ColonyId = "colonia-b",
            ContentHash = entrada.ContentHash,
        });

        Assert.NotEmpty(resposta.Erro);
        Assert.Empty(resposta.Conteudo);
    }

    [Fact]
    public void Restauracao_de_hash_desconhecido_responde_com_erro_legivel()
    {
        var resposta = store.Atender(new ColoniaRestauracao
        {
            PlayerId = "jogador-1",
            ColonyId = "colonia-a",
            ContentHash = "sha256:nunca-existiu",
        });

        Assert.Contains("nunca chegou aqui", resposta.Erro);
    }

    [Fact]
    public void Checkpoint_pre_sessao_e_marcado_e_sobrevive_a_retencao()
    {
        // §2.3 + §7.2: o ponto de retorno da sessão é dos três mantidos para
        // sempre.
        var entrada = store.Aceitar(Checkpoint(tick: 1_000, "antes da sessão", preSessao: true), T0);

        Assert.True(entrada.PreSessao);

        var historico = store.Historico(new ColonyIdentity("jogador-1", "colonia-a"));
        var mantidos = Server.Colonias.Retencao.Manter(historico, T0.AddYears(1));

        Assert.Contains(mantidos, e => e.PreSessao && e.ContentHash == entrada.ContentHash);
    }

    [Fact]
    public void Colonia_saudavel_nao_gera_alerta_nenhum()
    {
        for (int i = 1; i <= 20; i++)
            store.Aceitar(Checkpoint(i * 10_000, $"estado {i}"), T0.AddMinutes(i));

        Assert.Empty(alertas.Todos);
    }
}
