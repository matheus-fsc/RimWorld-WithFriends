using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Uma visita inteira sem ninguém clicando.
///
/// <para><b>Por que existe.</b> Reproduzir uma divergência custava dois humanos,
/// duas janelas e vários minutos de jogo — e cada hipótese testada exigia repetir
/// tudo. O gargalo do dia inteiro não foi escrever guarda: foi <b>jogar</b>.</para>
///
/// <para>Com o árbitro pronto, os dois lados podem ser instâncias sem interface.
/// O anfitrião abre uma colônia, sobe, chama o árbitro, convida, despausa e
/// deixa rodar; no fim, os dois diários estão no disco para comparar. É o mesmo
/// caminho do jogo de verdade — mesma sessão, mesma barreira, mesmos comandos —
/// só que ninguém precisa estar presente.</para>
///
/// <para><b>O que isto não é.</b> Não é simulação de brinquedo nem mock: é o
/// RimWorld rodando. A diferença em relação a uma visita normal é só a ausência
/// de mouse — que, como o dia mostrou, é justamente a variável que se quer
/// isolar.</para>
///
/// <para><b>Como o Multiplayer faz.</b> Lá o árbitro é lançado no momento de
/// <b>hospedar</b>, não de convidar: <c>CreateLocalClient</c> chama
/// <c>StartArbiter</c>, que abre uma porta só para ele e passa
/// <c>-connect=127.0.0.1:porta</c>. O árbitro entra como cliente e <b>recebe o
/// mundo do anfitrião</b> — não tem colônia própria.</para>
///
/// <para>Aqui ele precisa de uma: a nossa visita é entre duas colônias, e o
/// visitante congela a dele e publica o assentamento antes de atravessar
/// (ADR 0010). Por isso <c>-arbitrosave</c> existe e o dele não precisa de
/// equivalente. É a assimetria que faz a introdução parecer estranha, e ela vem
/// do desenho da visita, não do árbitro.</para>
/// </summary>
public static class ModoEmulacao
{
    public static readonly bool Ativo =
        GenCommandLine.CommandLineArgPassed("emulacao");

    /// <summary>Segundos de visita antes de despejar e sair. Padrão: dois minutos.</summary>
    static float Duracao =>
        GenCommandLine.TryGetCommandLineArg("emulacaosegundos", out string s) &&
        float.TryParse(s, out float v) ? v : 120f;

    /// <summary>
    /// Em que velocidade a emulação corre. Padrão <c>Fast</c>.
    ///
    /// <para>Existe porque há coisa que só acontece <b>depois de muito tempo de
    /// jogo</b>. A troca de clima é a primeira: <c>curWeatherDuration</c> é
    /// sorteado na faixa do def e chega fácil a dezenas de milhares de ticks,
    /// e a janela que interessa — a transição, com
    /// <c>curWeatherAge &lt; 4000</c> — só existe logo depois dela.</para>
    ///
    /// <para><c>Ultrafast</c> é a velocidade que o jogo só oferece em modo de
    /// desenvolvedor. Aqui ela não é trapaça: o relógio da sessão é negociado
    /// (§3) e a barreira continua mandando, então os dois lados andam juntos
    /// como em qualquer outra velocidade — só andam mais.</para>
    /// </summary>
    static TimeSpeed Velocidade =>
        GenCommandLine.TryGetCommandLineArg("emulacaovelocidade", out string v)
        && System.Enum.TryParse<TimeSpeed>(v, ignoreCase: true, out var escolhida)
            ? escolhida
            : TimeSpeed.Fast;

    /// <summary>
    /// Ticks que o anfitrião joga <b>sozinho</b> antes de chamar o árbitro.
    ///
    /// <para><b>Por que isto existe.</b> Três divergências seguidas de partidas
    /// de verdade tinham a mesma causa e nenhuma delas a bancada conseguia
    /// reproduzir: <b>estado de processo do anfitrião</b> (ADR 0020) — a fase da
    /// rotação das torres, os caches de stat, listas embaralhadas no lugar.
    /// Nada disso vai para o save.</para>
    ///
    /// <para>Na emulação os dois lados sobem, carregam o save e entram na visita
    /// em segundos. Nascem com o estado de processo igual, e por isso a bancada
    /// dava "nenhuma divergência" para bugs que derrubavam a sessão de gente no
    /// primeiro minuto. O arnês media um jogo que ninguém joga.</para>
    ///
    /// <para>Com aquecimento, o anfitrião joga sozinho por uns milhares de ticks
    /// antes de convidar — exatamente o que uma pessoa faz. Os caches enchem, as
    /// torres param em qualquer ângulo, os contadores andam. Aí o árbitro chega
    /// do zero, como o visitante de verdade, e a assimetria que a partida real
    /// tem passa a existir também aqui.</para>
    ///
    /// <para>Zero por padrão: uma corrida sem aquecimento continua sendo a mais
    /// rápida, e é a que se quer quando a pergunta é outra.</para>
    /// </summary>
    static int Aquecimento =>
        GenCommandLine.TryGetCommandLineArg("emulacaoaquecimento", out string a) &&
        int.TryParse(a, out int v) ? v : 0;

    static float acabaEm = -1f;
    static bool comecou;
    static int aquecerAte = -1;
    static bool aquecido;

    /// <summary>
    /// Chamado a cada quadro. Toca a emulação do começo ao fim.
    ///
    /// <para>Cada passo só acontece quando o anterior deu certo — não há
    /// temporizador chutado no meio. O único prazo é o da visita em si.</para>
    /// </summary>
    public static void Acompanhar()
    {
        if (!Ativo || Current.Game == null) return;

        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;

        // 0. Aquecer: jogar sozinho antes de convidar, para acumular o estado de
        //    processo que uma pessoa acumula. Só o anfitrião faz isto — o
        //    árbitro roda sem `-emulacao` e nem chega aqui, que é justamente a
        //    assimetria que se quer.
        if (!aquecido && Aquecimento > 0 && sessao is { Estado: EstadoSessaoLocal.Fora })
        {
            if (aquecerAte < 0)
            {
                aquecerAte = Find.TickManager.TicksGame + Aquecimento;
                Log.Message(
                    $"[WithFriends/emulação] aquecendo: {Aquecimento} tick(s) sozinho antes de convidar " +
                    $"(até o tick {aquecerAte}).");
            }

            if (Find.TickManager.TicksGame < aquecerAte)
            {
                // Sem isto o jogo fica parado no tick em que carregou e o
                // aquecimento nunca termina.
                if (Find.TickManager.Paused || Find.TickManager.CurTimeSpeed != Velocidade)
                    ControleDeVelocidade.ComoSistema(
                        () => Find.TickManager.CurTimeSpeed = Velocidade);
                return;
            }

            aquecido = true;
            Log.Message(
                $"[WithFriends/emulação] aquecido no tick {Find.TickManager.TicksGame} — chamando o árbitro.");
        }

        // 1. Chamar o árbitro. `Lancar` já espera ele aparecer e convida.
        if (!comecou && WithFriendsMod.Cliente.Conectado && sessao is { Estado: EstadoSessaoLocal.Fora })
        {
            comecou = true;
            Log.Message("[WithFriends/emulação] conectado — chamando o árbitro.");
            ModoArbitro.Lancar();
            return;
        }

        if (sessao is not { Estado: EstadoSessaoLocal.Simulando }) return;

        // 2. Visita de pé: despausar e marcar o prazo.
        //
        // O prazo conta da **primeira liberação da barreira**, não de o estado
        // virar Simulando. Entre uma coisa e outra o anfitrião ainda transfere e
        // recarrega a partida, o que num headless leva dezenas de segundos: na
        // primeira corrida o árbitro gastou quase todo o tempo esperando, e a
        // visita durou dezesseis passos.
        if (!VisitaEmAndamento.JaComecou) return;

        if (acabaEm < 0f)
        {
            acabaEm = Time.realtimeSinceStartup + Duracao;
            Log.Message(
                $"[WithFriends/emulação] visita de pé — rodando por {Duracao:F0}s em {Velocidade}.");

            // O roteiro só começa a contar agora: os segundos dele são de
            // visita, não de processo. Entre subir o jogo e a barreira andar
            // passam dezenas de segundos que não interessam a ninguém.
            if (RoteiroDeEmulacao.Algum) RoteiroDeEmulacao.Comecar();
        }

        RoteiroDeEmulacao.Acompanhar();

        // Despausar sempre: a ressincronização volta pausada de propósito
        // (pauseOnDesync), e numa emulação não há ninguém para retomar. Sem
        // isto, a primeira divergência congelaria o resto do experimento.
        if (Find.TickManager is { Paused: true })
            ControleDeVelocidade.ComoSistema(
                () => Find.TickManager.CurTimeSpeed = Velocidade);

        if (Time.realtimeSinceStartup < acabaEm) return;

        Encerrar(sessao, "emulação");
    }

    /// <summary>
    /// Despeja os rastreios e fecha a instância.
    ///
    /// <para><b>Os dois lados precisam chamar isto.</b> Na primeira corrida só o
    /// anfitrião despejava; o árbitro era morto junto com o processo e o diário
    /// dele terminava sem histórico nenhum. A comparação — que é a única razão
    /// de tudo isto existir — não tinha o que comparar.</para>
    /// </summary>
    public static void Encerrar(SessaoCliente sessao, string quem)
    {
        if (encerrando) return;
        encerrando = true;

        Log.Message($"[WithFriends/{quem}] prazo cumprido — despejando os rastreios.");
        RngDeSessao.Despejar($"fim da {quem}");
        RastreioDeRng.Despejar(sessao.TickDeSessao, ticksAntes: 400, ticksDepois: 0);
        RastreioDePawns.Despejar();
        RetratoDaPartida.Registrar($"fim da {quem}");

        Log.Message($"[WithFriends/{quem}] diário: {DiarioDaInstancia.Caminho}");
        DiarioDaInstancia.Fechar();

        Root.Shutdown();
    }

    static bool encerrando;

    /// <summary>
    /// Quanto o árbitro deve rodar. Vem do mesmo argumento do anfitrião, que o
    /// repassa ao lançá-lo — assim os dois param juntos.
    /// </summary>
    public static float DuracaoConfigurada => Duracao;
}

/// <summary>
/// Instância automática — árbitro ou emulação — abre a colônia dela sozinha.
///
/// <para>Aceitar ou propor uma visita exige estar <b>dentro de uma partida</b>:
/// o visitante congela a própria colônia e publica o assentamento dele. Uma
/// instância em <c>-batchmode</c> sobe no menu principal e ficaria ali.</para>
/// </summary>
/// <remarks>
/// <para><b>O gancho é <c>Root_Entry.Update</c>, não o menu principal.</b> A
/// primeira versão engatava em <c>MainMenuDrawer.MainMenuOnGUI</c> — e em
/// <c>-batchmode -nographics</c> <b>não há GUI</b>: o método nunca é chamado, a
/// instância subia, carregava os defs e ficava parada para sempre. Só apareceu
/// rodando de verdade; compilava perfeitamente.</para>
///
/// <para><c>Update</c> da cena de entrada roda com ou sem tela, que é a
/// propriedade que importa aqui.</para>
/// </remarks>
[HarmonyPatch(typeof(Root_Entry), nameof(Root_Entry.Update))]
public static class InstanciaAutomaticaCarregaSozinha
{
    static bool jaTentou;

    [HarmonyPostfix]
    public static void Depois()
    {
        // Esperar os defs: carregar um save antes disso não tem o que resolver.
        if (LongEventHandler.AnyEventNowOrWaiting) return;

        if (jaTentou) return;
        if (!ModoArbitro.Ativo && !ModoEmulacao.Ativo) return;
        jaTentou = true;

        string chave = ModoArbitro.Ativo ? "arbitrosave" : "emulacaosave";

        if (!GenCommandLine.TryGetCommandLineArg(chave, out string nome) ||
            string.IsNullOrWhiteSpace(nome))
        {
            Log.Warning(
                $"[WithFriends] instância automática sem -{chave}=NOME: não há colônia " +
                "para entrar na visita, e ela não vai fazer nada.");
            return;
        }

        Log.Message($"[WithFriends] abrindo a colônia \"{nome}\"…");
        LongEventHandler.QueueLongEvent(
            () => GameDataSaveLoader.LoadGame(nome), "LoadingLongEvent", true, null);
    }
}
