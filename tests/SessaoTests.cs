using WithFriends.Protocol;
using WithFriends.Protocol.Determinismo;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Mundo;
using WithFriends.Server.Sessoes;
using Xunit;

namespace WithFriends.Tests;

/// <summary>§2.3 e §3 — sessão delimitada, barreira comum, aborto com rollback.</summary>
public class SessaoTests
{
    static readonly DateTime T0 = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
    const string Anfitriao = "jogador-1";
    const string Visitante = "jogador-2";
    const string Mods = "sha256:mods-de-sessao";

    sealed class DestinatarioFalso : IDestinatario
    {
        public string PlayerId { get; init; } = "";
        public string DisplayName => PlayerId;
        public List<IMessage> Recebidas { get; } = new();
        public void Entregar(IMessage mensagem) => Recebidas.Add(mensagem);
    }

    readonly Presenca presenca = new();
    readonly BrokerDeSessoes broker;

    public SessaoTests()
    {
        broker = new BrokerDeSessoes(presenca);
        presenca.Entrou(new DestinatarioFalso { PlayerId = Anfitriao });
        presenca.Entrou(new DestinatarioFalso { PlayerId = Visitante });
    }

    SessaoInicio AbrirSessao(string modsDoVisitante = Mods)
    {
        var convite = (SessaoConvite)broker.Convidar(Anfitriao, new SessaoConvite
        {
            Para = Visitante,
            Tipo = TipoSessao.Visitar,
            ColoniaAnfitria = "colonia-a",
            SessionModSetHash = Mods,
        }, T0);

        return (SessaoInicio)broker.Aceitar(Visitante, new SessaoAceite
        {
            ConviteId = convite.ConviteId,
            SessionModSetHash = modsDoVisitante,
        }, T0);
    }

    [Fact]
    public void Convite_aceito_abre_sessao_com_os_dois_participantes()
    {
        var inicio = AbrirSessao();

        Assert.Equal(Anfitriao, inicio.Anfitriao);
        Assert.Equal(Visitante, inicio.Visitante);
        Assert.Equal(TipoSessao.Visitar, inicio.Tipo);
        Assert.Single(broker.Ativas);
    }

    [Fact]
    public void Convidar_quem_esta_offline_e_recusado_com_motivo()
    {
        // §11: visitar, ajudar e atacar exigem sessão — e sessão exige os dois.
        var recusa = Assert.IsType<SessaoRecusa>(broker.Convidar(Anfitriao, new SessaoConvite
        {
            Para = "jogador-que-nao-existe",
            Tipo = TipoSessao.Visitar,
            SessionModSetHash = Mods,
        }, T0));

        Assert.Contains("offline", recusa.Explicacao);
    }

    [Fact]
    public void Mods_de_sessao_divergentes_recusam_a_sessao_e_nao_o_login()
    {
        // §8/§15.5: a verificação acontece AQUI, na entrada da sessão. O login
        // dos dois continuou válido o tempo todo.
        var recusa = Assert.IsType<SessaoRecusa>(AbrirSessaoComoMensagem("sha256:outros-mods"));

        Assert.Contains("mods", recusa.Explicacao);
        Assert.Empty(broker.Ativas);
    }

    IMessage AbrirSessaoComoMensagem(string modsDoVisitante)
    {
        var convite = (SessaoConvite)broker.Convidar(Anfitriao, new SessaoConvite
        {
            Para = Visitante,
            Tipo = TipoSessao.Visitar,
            SessionModSetHash = Mods,
        }, T0);

        return broker.Aceitar(Visitante, new SessaoAceite
        {
            ConviteId = convite.ConviteId,
            SessionModSetHash = modsDoVisitante,
        }, T0);
    }

    [Fact]
    public void Convite_expirado_e_recusado()
    {
        var convite = (SessaoConvite)broker.Convidar(Anfitriao, new SessaoConvite
        {
            Para = Visitante,
            Tipo = TipoSessao.Visitar,
            SessionModSetHash = Mods,
        }, T0);

        var resposta = broker.Aceitar(Visitante, new SessaoAceite
        {
            ConviteId = convite.ConviteId,
            SessionModSetHash = Mods,
        }, T0 + BrokerDeSessoes.ValidadeDoConvite + TimeSpan.FromSeconds(1));

        Assert.Contains("expirou", Assert.IsType<SessaoRecusa>(resposta).Explicacao);
    }

    [Fact]
    public void Comando_e_agendado_para_um_tick_que_ninguem_simulou_ainda()
    {
        // Lockstep: o comando entra no futuro, senão um dos lados teria que
        // voltar no tempo para aplicá-lo.
        var inicio = AbrirSessao();
        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100 });
        broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100 });

        var agendado = broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new byte[] { 1, 2, 3 },
        })!;

        Assert.True(agendado.TickAlvo > 100);
        Assert.Equal(Anfitriao, agendado.Autor);
        Assert.Equal(new byte[] { 1, 2, 3 }, agendado.Payload);
    }

    [Fact]
    public void Comandos_recebem_ordem_estavel_de_desempate()
    {
        var inicio = AbrirSessao();

        var primeiro = broker.Agendar(Anfitriao, new SessaoComando { SessaoId = inicio.SessaoId })!;
        var segundo = broker.Agendar(Visitante, new SessaoComando { SessaoId = inicio.SessaoId })!;

        Assert.True(segundo.Ordem > primeiro.Ordem);
    }

    [Fact]
    public void Ninguem_passa_do_participante_mais_lento()
    {
        var inicio = AbrirSessao();

        // O relógio nunca se afasta do mais lento mais que a pista — senão uma
        // máquina lenta ficaria para trás sem fim.
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 500, Velocidade = VelocidadeDeSessao.Ultra,
        }, t);

        // Tempo de sobra para o relógio querer disparar; a pista o segura.
        var barreira = (SessaoBarreira)broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 300, Velocidade = VelocidadeDeSessao.Ultra,
        }, t.AddSeconds(0.4))!;

        Assert.Equal(300 + Sessao.FolgaDaBarreira, barreira.TickLiberado);
    }

    [Fact]
    public void Pausa_de_um_para_o_tempo_dos_dois()
    {
        // §3, v1: consenso de pausa. Sem timer, sem contagem regressiva.
        //
        // Com o relógio no coordenador, "congelou onde estava" deixa de ser uma
        // regra escrita e vira o comportamento natural: pausado é multiplicador
        // zero, e relógio com ritmo zero não anda por mais tempo que passe.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        SessaoBarreira Relatar(string quem, VelocidadeDeSessao v, bool mudou, DateTime quando) =>
            (SessaoBarreira)broker.Barreira(quem, new SessaoBarreira
            {
                SessaoId = inicio.SessaoId, Tick = 200, Velocidade = v, MudouVelocidade = mudou,
            }, quando)!;

        Relatar(Anfitriao, VelocidadeDeSessao.Normal, false, t);
        Relatar(Visitante, VelocidadeDeSessao.Normal, false, t);

        var comPausa = Relatar(Visitante, VelocidadeDeSessao.Pausado, true, t.AddSeconds(0.5));
        Assert.Equal(200, comPausa.TickLiberado);   // congelou onde estava

        // Meio segundo pausado não anda um tick: pausa é multiplicador zero.
        var aindaPausado = Relatar(Anfitriao, VelocidadeDeSessao.Pausado, false, t.AddSeconds(1.0));
        Assert.Equal(200, aindaPausado.TickLiberado);

        Relatar(Anfitriao, VelocidadeDeSessao.Normal, true, t.AddSeconds(1.1));
        var andando = Relatar(Visitante, VelocidadeDeSessao.Normal, false, t.AddSeconds(1.3));
        Assert.True(andando.TickLiberado > 200, "retomado, o relógio volta a andar");
    }

    [Fact]
    public void Dois_lados_parados_esperando_nao_travam_a_sessao_para_sempre()
    {
        // Achado em jogo: os dois congelam esperando o outro entrar, reportam
        // "pausado", o coordenador segura a barreira, e como a barreira nunca
        // libera ninguém despausa. Sessão travada, sem saída nem pela UI.
        //
        // A barreira só é segurada por pausa **pedida pelo jogador**; o
        // congelamento de entrada não conta — quem decide isso é o cliente, que
        // reporta velocidade Normal enquanto espera.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0, Velocidade = VelocidadeDeSessao.Normal,
        }, t);
        broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0, Velocidade = VelocidadeDeSessao.Normal,
        }, t);

        var barreira = (SessaoBarreira)broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0, Velocidade = VelocidadeDeSessao.Normal,
        }, t.AddSeconds(0.2))!;

        Assert.True(barreira.TickLiberado > inicio.TickInicial,
            "com os dois presentes e ninguém pedindo pausa, o relógio tem de andar");
        Assert.False(barreira.Pausado);
    }

    [Fact]
    public void Fingerprints_iguais_avancam_o_ultimo_tick_valido()
    {
        var inicio = AbrirSessao();

        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100, Fingerprint = "rng:abc" });
        broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100, Fingerprint = "rng:abc" });

        Assert.Equal(100, broker.PorId(inicio.SessaoId)!.UltimoTickValido);
    }

    [Fact]
    public void Divergencia_isolada_que_se_corrige_nao_aborta()
    {
        // Medido em jogo: os dois lados fizeram o **mesmo trabalho total** com
        // um tick de diferença, e o traço reconvergeu. Abortar ali mataria a
        // visita por ruído — desync de verdade nunca volta a bater.
        var inicio = AbrirSessao();

        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100, Fingerprint = "rng:a" });
        var resposta = broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId,
            Tick = 100,
            Fingerprint = "rng:b",     // diverge
        });

        Assert.IsType<SessaoBarreira>(resposta);   // não abortou
        Assert.Single(broker.Ativas);

        // Volta a bater: o contador zera.
        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 108, Fingerprint = "rng:c" });
        broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 108, Fingerprint = "rng:c" });

        Assert.Equal(0, broker.PorId(inicio.SessaoId)!.DivergenciasSeguidas);
        Assert.Single(broker.Ativas);
    }

    [Fact]
    public void Divergencia_que_persiste_pede_ponto_de_juncao_novo()
    {
        // Era aborto. Virou ressincronização: o aborto agora só vem depois de
        // o orçamento de pontos de junção acabar — ver
        // `Ressincronizar_tem_orcamento_e_depois_aborta`.
        var inicio = AbrirSessao();

        IMessage? resposta = null;
        for (int i = 1; i <= BrokerDeSessoes.DivergenciasParaAbortar; i++)
        {
            long tick = 100 + i * 8;
            broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = tick, Fingerprint = $"rng:a{i}" });
            resposta = broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = tick, Fingerprint = $"rng:b{i}" });
        }

        var pedido = Assert.IsType<SessaoRessincronizar>(resposta);
        Assert.Contains("Ticks suspeitos", pedido.Explicacao);

        // A sessão **continua viva**. É o ponto inteiro da mudança: perder o
        // encontro era o que tornava cada causa desconhecida cara.
        Assert.Single(broker.Ativas);
    }

    [Fact]
    public void Divergencia_de_RNG_recomeca_do_ultimo_tick_consistente()
    {
        // §14.3: a impressão digital é o estado do RNG, e diverge de imediato.
        // O ponto de junção novo sai do último tick em que os dois bateram —
        // não do tick da detecção, que já está do lado errado da divergência.
        var inicio = AbrirSessao();

        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100, Fingerprint = "rng:igual" });
        broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100, Fingerprint = "rng:igual" });

        // Diverge e **continua** divergindo: aí sim é desync.
        IMessage? resposta = null;
        for (int i = 1; i <= BrokerDeSessoes.DivergenciasParaAbortar; i++)
        {
            long tick = 200 + i * 8;
            broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = tick, Fingerprint = $"rng:um{i}" });
            resposta = broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = tick, Fingerprint = $"rng:outro{i}" });
        }

        var pedido = Assert.IsType<SessaoRessincronizar>(resposta);
        Assert.Equal(100, pedido.TickAlvo);          // último ponto consistente

        // A barreira congela no lugar: ninguém avança enquanto o estado novo
        // está viajando. A sessão segue viva — é o conserto, não o fim.
        Assert.Equal(EstadoSessao.Ressincronizando, broker.PorId(inicio.SessaoId)!.Estado);
    }

    [Fact]
    public void Desconexao_encerra_a_sessao_sem_perder_a_colonia()
    {
        var inicio = AbrirSessao();
        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 50, Fingerprint = "rng:a" });
        broker.Barreira(Visitante, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 50, Fingerprint = "rng:a" });

        var fim = broker.ParticipanteSaiu(Visitante)!;

        Assert.Equal(MotivoFimDeSessao.ParticipanteDesconectou, fim.Motivo);
        Assert.Contains("tick 50", fim.Explicacao);
        Assert.Empty(broker.Ativas);
    }

    [Fact]
    public void Nao_da_para_entrar_em_duas_sessoes_ao_mesmo_tempo()
    {
        AbrirSessao();

        var recusa = Assert.IsType<SessaoRecusa>(broker.Convidar(Anfitriao, new SessaoConvite
        {
            Para = Visitante,
            Tipo = TipoSessao.AjudarNaDefesa,
            SessionModSetHash = Mods,
        }, T0));

        Assert.Contains("já está em sessão", recusa.Explicacao);
    }

    [Fact]
    public void Ensaio_nao_compara_digitais()
    {
        // Ensaio existe para validar tempo, barreira, pausa e aborto antes de
        // haver mapa compartilhado. Os dois lados estão em colônias
        // diferentes: divergir é o esperado, não é desync.
        var convite = (SessaoConvite)broker.Convidar(Anfitriao, new SessaoConvite
        {
            Para = Visitante,
            Tipo = TipoSessao.Ensaio,
            SessionModSetHash = Mods,
        }, T0);

        var inicio = (SessaoInicio)broker.Aceitar(Visitante, new SessaoAceite
        {
            ConviteId = convite.ConviteId,
            SessionModSetHash = Mods,
        }, T0);

        Assert.False(inicio.CompararDigitais);

        broker.Barreira(Anfitriao, new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 100, Fingerprint = "rng:um" });
        var resposta = broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId,
            Tick = 100,
            Fingerprint = "rng:completamente-diferente",
        });

        Assert.IsType<SessaoBarreira>(resposta);   // seguiu, não abortou
        Assert.Single(broker.Ativas);
    }

    [Fact]
    public void Sessao_de_verdade_continua_comparando_digitais()
    {
        var inicio = AbrirSessao();

        Assert.True(inicio.CompararDigitais);
    }

    [Fact]
    public void Comando_de_quem_nao_esta_na_sessao_e_ignorado()
    {
        var inicio = AbrirSessao();

        Assert.Null(broker.Agendar("estranho", new SessaoComando { SessaoId = inicio.SessaoId }));
        Assert.Null(broker.Barreira("estranho", new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = 10 }));
    }

    [Fact]
    public void Comando_cai_depois_do_lado_mais_adiantado()
    {
        // Um comando que cai num tick já simulado encerra a sessão — e com
        // razão, ninguém volta no tempo. O teto seguro é o que a barreira já
        // concedeu, não o último tick relatado: entre dois relatos o jogo
        // avança um quadro inteiro de ticks, e em Superfast isso passou de 30.
        var sessao = new Sessao { Id = "s", Anfitriao = "a", Visitante = "v", TickInicial = 0 };
        sessao.TickPorParticipante["a"] = 100;
        sessao.TickPorParticipante["v"] = 160;

        Assert.Equal(sessao.TickLiberado() + Sessao.TicksDeAtraso, sessao.TickDeComando());
        Assert.True(sessao.TickDeComando() > sessao.TickLiberado());
    }

    [Fact]
    public void Pausa_nao_faz_o_carimbo_de_comando_andar_para_tras()
    {
        // A pausa recua a barreira para o mínimo (§3). Carimbar comando a
        // partir dela produzia um tick ANTERIOR ao de um comando já carimbado —
        // e anterior ao que o outro lado já simulou, que encerra a sessão.
        var sessao = new Sessao { Id = "s", Anfitriao = "a", Visitante = "v", TickInicial = 0 };
        sessao.TickPorParticipante["a"] = 448;
        sessao.TickPorParticipante["v"] = 448;

        long antesDaPausa = sessao.TickDeComando();

        sessao.Pausados.Add("a");
        sessao.TickPorParticipante["a"] = 453;
        sessao.TickPorParticipante["v"] = 453;

        long durantePausa = sessao.TickDeComando();

        Assert.True(sessao.TickLiberado() < antesDaPausa, "a pausa precisa mesmo recuar a barreira");
        Assert.True(durantePausa > antesDaPausa, $"carimbo andou para trás: {antesDaPausa} → {durantePausa}");
    }

    [Fact]
    public void Comando_parado_nao_cai_atras_do_lado_mais_adiantado()
    {
        // O "unsync instantâneo ao construir pausado".
        //
        // Parado, o carimbo saía de `mínimo dos relatados + 1`. O mínimo é do
        // lado mais ATRASADO: o outro já podia ter executado esse passo, e
        // comando para passo já executado encerra a sessão na chegada. Bastava
        // pausar depois de uma corrida normal — onde os dois ficam a passos
        // diferentes — e a primeira construção matava a visita.
        var sessao = new Sessao { Id = "s", Anfitriao = "a", Visitante = "v", TickInicial = 0 };
        sessao.TickPorParticipante["a"] = 2929;
        sessao.TickPorParticipante["v"] = 2929;

        var t = new DateTime(2025, 1, 1);
        sessao.AvancarRelogio(t.AddSeconds(1));
        long liberadoAntes = sessao.TickLiberado();

        // Um lado corre até onde a barreira deixou; o outro fica para trás.
        sessao.TickPorParticipante["a"] = liberadoAntes;
        sessao.TickPorParticipante["v"] = 2929;

        sessao.PedirVelocidade("v", VelocidadeDeSessao.Pausado, t.AddSeconds(2));
        Assert.Equal(VelocidadeDeSessao.Pausado, sessao.Velocidade);

        long carimbo = sessao.TickDeComando();

        Assert.True(carimbo >= liberadoAntes,
            $"carimbo {carimbo} cai antes do teto já concedido {liberadoAntes}");
        foreach (var (quem, passo) in sessao.TickPorParticipante)
            Assert.True(carimbo >= passo, $"carimbo {carimbo} já foi executado por {quem} ({passo})");
    }

    [Fact]
    public void Comandos_parados_seguidos_saem_em_ordem_e_no_futuro()
    {
        // Cada ordem parado custa o seu passo, e nenhuma delas pode cair num
        // passo que a barreira já liberou — senão a segunda ordem do mesmo
        // clique encerra a sessão.
        var sessao = new Sessao { Id = "s", Anfitriao = "a", Visitante = "v", TickInicial = 0 };
        sessao.TickPorParticipante["a"] = 500;
        sessao.TickPorParticipante["v"] = 500;
        sessao.PedirVelocidade("a", VelocidadeDeSessao.Pausado, new DateTime(2025, 1, 1));

        var quando = new DateTime(2025, 1, 1);
        long anterior = long.MinValue;
        for (int i = 0; i < 5; i++)
        {
            long carimbo = sessao.TickDeComando();

            // Enquanto o passo não é liberado, o comando seguinte divide ele:
            // é assim que as oito paredes do mesmo clique caem num tick só.
            Assert.Equal(carimbo, sessao.TickDeComando());

            Assert.True(carimbo > anterior, $"carimbo {carimbo} não passou do anterior {anterior}");
            Assert.True(sessao.TickLiberado() <= carimbo,
                $"passo {carimbo} foi liberado ANTES de ser carimbado — o outro lado pode já ter passado");

            anterior = carimbo;

            // Relato seguinte da barreira: o passo é liberado e os dois lados o
            // executam.
            quando = quando.AddSeconds(0.1);
            sessao.AvancarRelogio(quando);
            Assert.True(sessao.TickLiberado() > carimbo,
                $"passo {carimbo} nunca foi liberado (barreira em {sessao.TickLiberado()})");

            sessao.TickPorParticipante["a"] = sessao.TickLiberado();
            sessao.TickPorParticipante["v"] = sessao.TickLiberado();
        }
    }

    [Fact]
    public void Visitante_nao_decide_pela_colonia_do_anfitriao()
    {
        // §4: a visita acontece NA colônia do anfitrião. O visitante comanda os
        // próprios pawns — ajuda humanitária, tropas (§5) — e não as escolhas
        // do lugar. A recusa é no coordenador, antes de carimbar: comando
        // recusado nunca existe para lado nenhum, então não há metade aplicada.
        var inicio = AbrirSessao();
        var incidente = new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Incidente, (byte)0 },
        };

        Assert.Null(broker.Agendar(Visitante, incidente, out string? recusa));
        Assert.NotNull(recusa);

        Assert.NotNull(broker.Agendar(Anfitriao, incidente, out string? semRecusa));
        Assert.Null(semRecusa);
    }

    [Fact]
    public void Ordem_de_pawn_o_visitante_pode()
    {
        // O corte é entre "meus pawns" e "esta colônia". Alistar e mandar
        // trabalhar continuam valendo para os dois — senão a visita não serve
        // para mandar tropas, que é o motivo dela existir.
        //
        // `Designar` está aqui por decisão explícita: construir e minerar são
        // ordens à colônia e caberiam no balde do anfitrião, mas o visitante
        // precisa poder erguer uma barricada durante um raid. Ver
        // docs/COMANDOS-DE-SESSAO.md — se alguém mover Designar para
        // só-anfitrião por parecer mais coerente, este teste avisa.
        var inicio = AbrirSessao();

        foreach (var tipo in new[] { TipoDeComando.Alistar, TipoDeComando.OrdemDeTrabalho,
                                     TipoDeComando.Designar, TipoDeComando.Velocidade,
                                     TipoDeComando.OrdemPriorizada, TipoDeComando.Alternar })
        {
            var comando = new SessaoComando
            {
                SessaoId = inicio.SessaoId,
                Payload = new[] { (byte)tipo },
            };
            Assert.NotNull(broker.Agendar(Visitante, comando, out string? recusa));
            Assert.Null(recusa);
        }
    }

    [Fact]
    public void Divergir_refaz_o_ponto_de_juncao_em_vez_de_abortar()
    {
        // A alavanca que muda o **modo** de falha em vez da taxa. Antes,
        // qualquer causa desconhecida custava o encontro inteiro — e causa
        // desconhecida só aparece jogando. Agora custa um recarregamento, e a
        // visita rende vários relatórios em vez de um.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        IMessage? resposta = null;
        for (int i = 1; i <= BrokerDeSessoes.DivergenciasParaAbortar; i++)
        {
            long tick = 100 + i * 8;
            broker.Barreira(Anfitriao, Digital(inicio.SessaoId, tick, "aaa"), t);
            resposta = broker.Barreira(Visitante, Digital(inicio.SessaoId, tick, "bbb"), t);
        }

        var pedido = Assert.IsType<SessaoRessincronizar>(resposta);
        Assert.Equal(1, pedido.Numero);
        Assert.Contains("divergiu em", pedido.Explicacao);

        // Enquanto o ponto novo não fica de pé, comando nenhum é carimbado — o
        // passo contra o qual carimbar ainda está viajando.
        Assert.Null(broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Alistar },
        }, out string? recusa));
        Assert.Contains("ressincronizando", recusa!, StringComparison.OrdinalIgnoreCase);

        // Um lado chegou: ele **não** recebe o pedido de novo. A resposta é
        // difundida para os dois, e repetir "ressincronize" para quem acabou de
        // voltar é pedir que ele recomece — foi assim que duas ressincronizações
        // viraram jogo congelado.
        Assert.Null(broker.Barreira(Anfitriao,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t));

        // Quem ainda está atrás continua recebendo — é para ele que o reenvio
        // existe, caso a mensagem tenha se perdido no meio do recarregamento.
        Assert.IsType<SessaoRessincronizar>(broker.Barreira(Visitante,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo - 50 }, t));

        // Os dois chegaram: a visita continua.
        var voltou = broker.Barreira(Visitante,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t);
        Assert.IsType<SessaoBarreira>(voltou);

        Assert.NotNull(broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Alistar },
        }, out _));
    }

    [Fact]
    public void Ressincronizar_tem_orcamento_e_depois_aborta()
    {
        // Ressincronizar não pode virar laço: uma causa que volta em dez
        // segundos vai voltar na décima vez, e aí a visita é só uma sequência de
        // recarregamentos. Passado o orçamento, desistir com diagnóstico.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long tick = 100;

        IMessage? resposta = null;
        for (int rodada = 0; rodada <= BrokerDeSessoes.RessincronizacoesPorSessao; rodada++)
        {
            for (int i = 0; i < BrokerDeSessoes.DivergenciasParaAbortar; i++)
            {
                tick += 8;
                broker.Barreira(Anfitriao, Digital(inicio.SessaoId, tick, "aaa"), t);
                resposta = broker.Barreira(Visitante, Digital(inicio.SessaoId, tick, "bbb"), t);
            }

            if (resposta is SessaoRessincronizar pedido)
            {
                broker.Barreira(Anfitriao,
                    new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t);
                broker.Barreira(Visitante,
                    new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t);
            }
        }

        var aborto = Assert.IsType<SessaoAborto>(resposta);

        // O aborto carrega o histórico inteiro: se chegamos até aqui, cada
        // divergência é uma pista, e jogá-las fora seria perder o que a visita
        // custou para produzir.
        Assert.Contains("Divergências desta sessão", aborto.Explicacao);
    }

    [Fact]
    public void O_resumo_diz_qual_parte_divergiu()
    {
        // Era um sha256 de tudo junto, e o que chegava era "diferente". Mundo,
        // mapa, comandos e modo de arredondamento apontam para lugares
        // diferentes — saber qual foi poupa a rodada de comparar rastreio à mão.
        Assert.Null(OpiniaoDeSincronia.DiferencaEntreResumos(
            "fp:0|passo:100-108|mapa0:aaaaaaaa|mundo:bbbbbbbb|cmd:cccccccc",
            "fp:0|passo:100-108|mapa0:aaaaaaaa|mundo:bbbbbbbb|cmd:cccccccc"));

        Assert.Contains("mapa0", OpiniaoDeSincronia.DiferencaEntreResumos(
            "fp:0|passo:100-108|mapa0:aaaaaaaa|mundo:bbbbbbbb|cmd:cccccccc",
            "fp:0|passo:100-108|mapa0:99999999|mundo:bbbbbbbb|cmd:cccccccc")!);

        // Arredondamento diferente é a primeira coisa a olhar: nenhum estado de
        // RNG vai bater, e caçar sorteio nesse caso é caçar no lugar errado.
        var fp = OpiniaoDeSincronia.DiferencaEntreResumos(
            "fp:0|passo:100-108|mundo:bbbbbbbb",
            "fp:1|passo:100-108|mundo:bbbbbbbb")!;
        Assert.Contains("fp:", fp);

        // Um mapa que só existe de um lado não pode passar como "igual".
        Assert.Contains("só existe", OpiniaoDeSincronia.DiferencaEntreResumos(
            "fp:0|passo:100-108|mapa0:aaaaaaaa|mapa1:dddddddd",
            "fp:0|passo:100-108|mapa0:aaaaaaaa")!);
    }

    [Fact]
    public void O_ponto_de_juncao_novo_e_um_passo_de_sessao()
    {
        // Passo de sessão e tick de jogo são dois números diferentes (ADR 0017),
        // e trocar um pelo outro custou uma sessão: o lado que recarregava
        // passava a contar passos a partir do tick de jogo (66055) enquanto o
        // coordenador liberava 20, 266, 932 — parado para sempre, com o outro
        // jogando normalmente.
        //
        // O alvo tem de continuar na escala do que o coordenador conta.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, Digital(inicio.SessaoId, 100, "igual"), t);
        broker.Barreira(Visitante, Digital(inicio.SessaoId, 100, "igual"), t);

        IMessage? resposta = null;
        for (int i = 1; i <= BrokerDeSessoes.DivergenciasParaAbortar; i++)
        {
            long tick = 100 + i * 8;
            broker.Barreira(Anfitriao, Digital(inicio.SessaoId, tick, $"a{i}"), t);
            resposta = broker.Barreira(Visitante, Digital(inicio.SessaoId, tick, $"b{i}"), t);
        }

        var pedido = Assert.IsType<SessaoRessincronizar>(resposta);
        Assert.Equal(100, pedido.TickAlvo);

        // E a barreira volta a liberar a partir dele — não de um número de outra
        // escala, que o cliente nunca alcançaria.
        broker.Barreira(Anfitriao,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t);
        var voltou = (SessaoBarreira)broker.Barreira(Visitante,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t)!;

        Assert.InRange(voltou.TickLiberado, pedido.TickAlvo, pedido.TickAlvo + Sessao.FolgaDaBarreira);
    }

    [Fact]
    public void Ponto_de_juncao_que_nao_dura_nao_e_refeito_de_novo()
    {
        // O "laço de ressincronização" que o jogador vê. Se a divergência volta
        // poucos passos depois do ponto, os dois lados partiram de um estado
        // idêntico e se afastaram de novo: é a simulação que difere, e refazer
        // o ponto vai dar no mesmo. Insistir é meio minuto de recarregamentos
        // para terminar no mesmo lugar.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, Digital(inicio.SessaoId, 100, "igual"), t);
        broker.Barreira(Visitante, Digital(inicio.SessaoId, 100, "igual"), t);

        IMessage? resposta = null;
        void Divergir(long de)
        {
            for (int i = 1; i <= BrokerDeSessoes.DivergenciasParaAbortar; i++)
            {
                broker.Barreira(Anfitriao, Digital(inicio.SessaoId, de + i * 8, $"a{i}"), t);
                resposta = broker.Barreira(Visitante, Digital(inicio.SessaoId, de + i * 8, $"b{i}"), t);
            }
        }

        // Primeira divergência: ainda vale tentar, não há ponto anterior.
        Divergir(100);
        var pedido = Assert.IsType<SessaoRessincronizar>(resposta);

        broker.Barreira(Anfitriao,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t);
        broker.Barreira(Visitante,
            new SessaoBarreira { SessaoId = inicio.SessaoId, Tick = pedido.TickAlvo }, t);

        // Volta a divergir logo em seguida, dentro da janela em que o ponto
        // ainda nem se provou: desiste em vez de gastar o orçamento.
        Divergir(pedido.TickAlvo);

        var aborto = Assert.IsType<SessaoAborto>(resposta);
        Assert.Contains("voltou logo depois do ponto de junção", aborto.Explicacao);

        // E desistiu com o orçamento quase intacto: o limite que valeu foi o de
        // utilidade, não o de tentativas.
        Assert.True(broker.PorId(inicio.SessaoId)!.Ressincronizacoes < BrokerDeSessoes.RessincronizacoesPorSessao);
    }

    static SessaoBarreira Digital(string sessaoId, long tick, string digital) => new()
    {
        SessaoId = sessaoId,
        Tick = tick,
        Fingerprint = digital,
    };

    [Fact]
    public void So_o_incidente_e_decisao_da_colonia()
    {
        // A lista de só-anfitrião é curta de propósito, e cresce só por decisão
        // (§4). Este teste existe para que **acrescentar** alguém a ela seja um
        // ato consciente: um tipo novo nasce valendo para os dois, e quem quiser
        // restringi-lo tem de vir mudar isto aqui.
        foreach (TipoDeComando tipo in Enum.GetValues(typeof(TipoDeComando)))
            Assert.Equal(tipo == TipoDeComando.Incidente,
                AutoridadeDeComando.SoDoAnfitriao((byte)tipo));
    }

    [Fact]
    public void Parado_passa_a_ser_uma_coisa_so()
    {
        // Com relógio único, "parado" é uma coisa só: a velocidade acordada.
        // Não existe mais um lado parado e o outro andando — era essa
        // possibilidade que tornava perigoso aplicar comando com o jogo parado.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 10, Velocidade = VelocidadeDeSessao.Normal,
        }, t);

        var andando = (SessaoBarreira)broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 10, Velocidade = VelocidadeDeSessao.Normal,
        }, t)!;
        Assert.False(andando.TodosPausados);

        var parado = (SessaoBarreira)broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 10,
            Velocidade = VelocidadeDeSessao.Pausado, MudouVelocidade = true,
        }, t.AddSeconds(0.1))!;
        Assert.True(parado.TodosPausados);
    }

    [Fact]
    public void Qualquer_um_dos_dois_muda_o_relogio_de_todos()
    {
        // Controle comum, não veto mútuo. O "mais lento manda" foi descartado
        // porque, pausado, só quem pausou conseguia despausar — o pedido do
        // outro era sempre o mais rápido e perdia sempre.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        SessaoBarreira Relatar(string quem, VelocidadeDeSessao v, bool mudou, DateTime quando) =>
            (SessaoBarreira)broker.Barreira(quem, new SessaoBarreira
            {
                SessaoId = inicio.SessaoId, Tick = 0, Velocidade = v, MudouVelocidade = mudou,
            }, quando)!;

        Relatar(Anfitriao, VelocidadeDeSessao.Normal, false, t);
        Relatar(Visitante, VelocidadeDeSessao.Normal, false, t);

        // O visitante pausa: vale para os dois.
        var pausado = Relatar(Visitante, VelocidadeDeSessao.Pausado, true, t.AddSeconds(0.1));
        Assert.Equal(VelocidadeDeSessao.Pausado, pausado.VelocidadeAcordada);

        // E o ANFITRIÃO consegue despausar — era exatamente isto que o
        // "mais lento manda" impedia. Depois da janela de proteção, que existe
        // para outra coisa (ver o teste da prioridade da pausa).
        var retomado = Relatar(Anfitriao, VelocidadeDeSessao.Normal, true, t.AddSeconds(1.0));
        Assert.Equal(VelocidadeDeSessao.Normal, retomado.VelocidadeAcordada);

        var andando = Relatar(Visitante, VelocidadeDeSessao.Normal, false, t.AddSeconds(1.2));
        Assert.True(andando.TickLiberado > 0, "despausado, o relógio volta a andar");
    }

    [Fact]
    public void Repetir_o_pedido_nao_desfaz_a_mudanca_do_outro()
    {
        // O relato é periódico: os dois mandam a velocidade toda vez. Se
        // repetir contasse como mexer, o próximo relato de um lado desfaria a
        // mudança recém-feita do outro, e o controle ficaria oscilando.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Normal, MudouVelocidade = false,
        }, t);

        broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Rapido, MudouVelocidade = true,
        }, t);

        var depois = (SessaoBarreira)broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Normal, MudouVelocidade = false,
        }, t.AddSeconds(0.05))!;

        Assert.Equal(VelocidadeDeSessao.Rapido, depois.VelocidadeAcordada);
    }

    [Fact]
    public void A_visita_comeca_mesmo_com_os_dois_congelados_esperando()
    {
        // Antes de a visita começar, os dois lados estão congelados por NÓS,
        // esperando o outro entrar. Se esse congelamento virar "peço pausa", o
        // relógio do coordenador não anda; se ele não anda, a barreira não
        // libera o primeiro tick; se não libera, a visita nunca começa.
        //
        // Congelamento mecânico não é intenção do jogador — nem na bandeira de
        // pausa, nem no pedido de velocidade.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Normal, Pausado = false,
        }, t);
        broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Normal, Pausado = false,
        }, t);

        var liberada = (SessaoBarreira)broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Normal, Pausado = false,
        }, t.AddSeconds(0.2))!;

        Assert.True(liberada.TickLiberado > inicio.TickInicial,
            "sem o primeiro tick liberado, a visita não sai do lugar");
    }

    [Fact]
    public void Pausa_tem_prioridade_sobre_despausar_simultaneo()
    {
        // O caso que mata pawn: jogo andando, um aperta espaço para parar, e no
        // mesmo instante o outro aperta espaço — só que a pausa do primeiro já
        // chegou na tela dele, então o espaço dele quer dizer DESPAUSAR. A pausa
        // dura um piscar e o combate segue.
        //
        // A tecla de pausa é um alternador: ela quer dizer "o contrário do que
        // estou vendo", e o que cada um vê pode estar velho por uma ida e volta.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        SessaoBarreira Pedir(string quem, VelocidadeDeSessao v, DateTime quando) =>
            (SessaoBarreira)broker.Barreira(quem, new SessaoBarreira
            {
                SessaoId = inicio.SessaoId, Tick = 0, Velocidade = v, MudouVelocidade = true,
            }, quando)!;

        Pedir(Anfitriao, VelocidadeDeSessao.Normal, t);
        Pedir(Visitante, VelocidadeDeSessao.Normal, t);

        Pedir(Anfitriao, VelocidadeDeSessao.Pausado, t.AddSeconds(1));

        // 50 ms depois: o dedo do outro. Recusado, e explicado.
        var logoDepois = Pedir(Visitante, VelocidadeDeSessao.Normal, t.AddSeconds(1.05));
        Assert.Equal(VelocidadeDeSessao.Pausado, logoDepois.VelocidadeAcordada);
        Assert.True(logoDepois.DespausarRecusado);

        // Passada a janela, quem quiser mesmo continuar aperta de novo e vai.
        var depoisDaJanela = Pedir(Visitante, VelocidadeDeSessao.Normal, t.AddSeconds(2));
        Assert.Equal(VelocidadeDeSessao.Normal, depoisDaJanela.VelocidadeAcordada);
        Assert.False(depoisDaJanela.DespausarRecusado);
    }

    [Fact]
    public void Numero_de_velocidade_nao_sofre_com_o_alternador()
    {
        // Número é absoluto: "2" sempre quer dizer 2×. Dois jogadores pedindo
        // velocidades diferentes é só o último ganhar — o jogo segue fluido e
        // ninguém morre por isso. A proteção existe para a pausa, não para a
        // troca de ritmo.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        SessaoBarreira Pedir(string quem, VelocidadeDeSessao v, DateTime quando) =>
            (SessaoBarreira)broker.Barreira(quem, new SessaoBarreira
            {
                SessaoId = inicio.SessaoId, Tick = 0, Velocidade = v, MudouVelocidade = true,
            }, quando)!;

        Pedir(Anfitriao, VelocidadeDeSessao.Normal, t);
        Pedir(Visitante, VelocidadeDeSessao.Normal, t);

        Pedir(Anfitriao, VelocidadeDeSessao.Rapido, t.AddSeconds(1));
        var ultimo = Pedir(Visitante, VelocidadeDeSessao.MuitoRapido, t.AddSeconds(1.02));

        Assert.Equal(VelocidadeDeSessao.MuitoRapido, ultimo.VelocidadeAcordada);
        Assert.Equal(Visitante, ultimo.QuemMudou);
        Assert.False(ultimo.DespausarRecusado);
    }

    [Fact]
    public void Ordem_dada_com_o_jogo_parado_ganha_um_tick_para_acontecer()
    {
        // Comando só existe dentro de um tick, e parado não há tick — então
        // alistar um pawn com o jogo pausado não fazia nada até alguém
        // despausar. Metade do jeito de jogar RimWorld é com o jogo parado.
        //
        // O relógio dá um passo de UM tick por ordem: o comando acontece nele e
        // o jogo para de novo. Os dois aplicam no mesmo tick, que é a regra que
        // não pode cair.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0, Velocidade = VelocidadeDeSessao.Normal,
        }, t);
        broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0, Velocidade = VelocidadeDeSessao.Normal,
        }, t);

        var parado = (SessaoBarreira)broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0,
            Velocidade = VelocidadeDeSessao.Pausado, MudouVelocidade = true,
        }, t.AddSeconds(0.1))!;

        long antes = parado.TickLiberado;

        var comando = broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Alistar },
        }, out _);

        Assert.NotNull(comando);
        Assert.Equal(antes, comando!.TickAlvo);
        Assert.True(comando.TickAlvo - antes <= 1,
            "parado, o passo é de um tick só — mais que isso vira solavanco visível");

        // Vários comandos parados dividem o MESMO passo. Um tick por comando
        // fazia oito designações virarem oito ticks de simulação com o jogo
        // "parado", e a construção aparecia aos pedaços.
        var segundo = broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Alistar },
        }, out _);

        var terceiro = broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Alistar },
        }, out _);

        Assert.Equal(comando.TickAlvo, segundo!.TickAlvo);
        Assert.Equal(comando.TickAlvo, terceiro!.TickAlvo);

        // E o passo é liberado, senão o comando ficaria pendurado para sempre.
        var depois = (SessaoBarreira)broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 0, Velocidade = VelocidadeDeSessao.Pausado,
        }, t.AddSeconds(0.2))!;

        Assert.True(depois.TickLiberado > comando.TickAlvo,
            "o passo do comando tem de ficar ESTRITAMENTE dentro do liberado: o cliente " +
            "anda enquanto passo < liberado, então liberar até o próprio passo o deixa de fora — " +
            "e a ordem só acontece quando a próxima empurra o limite");
    }

    [Fact]
    public void A_versao_do_tempo_cresce_a_cada_mudanca_adotada()
    {
        // O cliente usa isto para saber se a resposta que chegou é POSTERIOR ao
        // pedido dele. Com "quem mudou por último" ele não conseguia: essa
        // pergunta continua respondendo "o outro" muito depois, e todo pedido
        // novo era descartado como se já tivesse sido superado — daí ter de
        // clicar várias vezes no botão de velocidade depois de o outro mexer.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        SessaoBarreira Pedir(string quem, VelocidadeDeSessao v, bool mudou, DateTime quando) =>
            (SessaoBarreira)broker.Barreira(quem, new SessaoBarreira
            {
                SessaoId = inicio.SessaoId, Tick = 0, Velocidade = v, MudouVelocidade = mudou,
            }, quando)!;

        Pedir(Anfitriao, VelocidadeDeSessao.Normal, false, t);
        var inicial = Pedir(Visitante, VelocidadeDeSessao.Normal, false, t);
        Assert.Equal(0, inicial.VersaoDoTempo);

        var depoisDeUma = Pedir(Visitante, VelocidadeDeSessao.Rapido, true, t.AddSeconds(1));
        Assert.Equal(1, depoisDeUma.VersaoDoTempo);

        // Relato periódico não é mudança: a versão não pode andar sozinha,
        // senão ela deixaria de distinguir "aconteceu algo" de "o tempo passou".
        var semMudanca = Pedir(Anfitriao, VelocidadeDeSessao.Rapido, false, t.AddSeconds(1.1));
        Assert.Equal(1, semMudanca.VersaoDoTempo);

        var depoisDeOutra = Pedir(Anfitriao, VelocidadeDeSessao.MuitoRapido, true, t.AddSeconds(1.2));
        Assert.Equal(2, depoisDeOutra.VersaoDoTempo);
    }

    [Fact]
    public void A_velocidade_vale_a_partir_de_um_passo_e_nao_de_um_instante()
    {
        // "Este passo simula?" não pode ser respondido com "o que eu sei
        // agora": os dois clientes sabem em momentos diferentes. Um executava o
        // passo 367 antes de a pausa chegar e o outro depois — mesmo passo,
        // resultados diferentes.
        //
        // O passo de corte é o primeiro que ninguém podia ter executado ainda,
        // então quem já passou por ele usou a velocidade antiga dos DOIS lados.
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        SessaoBarreira Relatar(string quem, long tick, VelocidadeDeSessao v, bool mudou, DateTime quando) =>
            (SessaoBarreira)broker.Barreira(quem, new SessaoBarreira
            {
                SessaoId = inicio.SessaoId, Tick = tick, Velocidade = v, MudouVelocidade = mudou,
            }, quando)!;

        Relatar(Anfitriao, 0, VelocidadeDeSessao.Normal, false, t);
        Relatar(Visitante, 0, VelocidadeDeSessao.Normal, false, t);

        // Deixa o relógio andar um pouco.
        var andando = Relatar(Anfitriao, 10, VelocidadeDeSessao.Normal, false, t.AddSeconds(0.3));
        Assert.True(andando.TickLiberado > 0);

        var pausado = Relatar(Visitante, 10, VelocidadeDeSessao.Pausado, true, t.AddSeconds(0.35));

        Assert.Equal(VelocidadeDeSessao.Pausado, pausado.VelocidadeAcordada);
        Assert.True(pausado.VelocidadeDesdePasso >= andando.TickLiberado,
            "a pausa não pode valer para um passo que alguém já podia ter executado");
    }

    [Fact]
    public void Comando_para_o_proximo_passo_chegou_na_hora()
    {
        // O coordenador carimba o comando para o primeiro passo que ninguém
        // executou — que é exatamente o passo em que o cliente "está". Se o
        // cliente tratar isso como atraso, ele encerra a sessão por um comando
        // válido:
        //
        //   comando chegou tarde: agendado para o tick 1156, e este lado já
        //   está em 1156
        //
        // "Estou em N" quer dizer "N é o próximo", não "já executei N".
        var inicio = AbrirSessao();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        broker.Barreira(Anfitriao, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 100, Velocidade = VelocidadeDeSessao.Normal,
        }, t);
        broker.Barreira(Visitante, new SessaoBarreira
        {
            SessaoId = inicio.SessaoId, Tick = 100,
            Velocidade = VelocidadeDeSessao.Pausado, MudouVelocidade = true,
        }, t);

        var comando = broker.Agendar(Anfitriao, new SessaoComando
        {
            SessaoId = inicio.SessaoId,
            Payload = new[] { (byte)TipoDeComando.Alistar },
        }, out _);

        // O carimbo é o passo em que os clientes ESTÃO: "estou em 100" quer
        // dizer que 100 ainda vai acontecer. Era 101 enquanto o carimbo parado
        // saía de `mínimo + 1`, o que gastava um tick à toa — e, quando os dois
        // lados não estavam no mesmo passo, carimbava no passado do mais
        // adiantado e encerrava a sessão.
        Assert.Equal(100, comando!.TickAlvo);
    }
}
