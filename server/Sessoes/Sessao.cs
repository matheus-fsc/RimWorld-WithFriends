using System.Collections.Generic;
using System.Linq;
using System;
using WithFriends.Protocol.Messages;

namespace WithFriends.Server.Sessoes;

public enum EstadoSessao
{
    Convidada,
    Ativa,

    /// <summary>
    /// Divergiu, e os dois lados estão refazendo o ponto de junção.
    ///
    /// <para>A barreira não libera passo e comando nenhum é carimbado: o estado
    /// para o qual eles vão voltar ainda está viajando. Quando os dois relatarem
    /// o tick do novo ponto, a sessão volta a ser <see cref="Ativa"/>.</para>
    /// </summary>
    Ressincronizando,

    Encerrada,
}

/// <summary>
/// Uma sessão viva, do ponto de vista do coordenador.
///
/// O servidor **não simula** (§10): ele sequencia comandos, sustenta a
/// barreira e compara impressões digitais. Toda a simulação acontece nos dois
/// clientes.
/// </summary>
public sealed class Sessao
{
    /// <summary>
    /// Atraso entre propor um comando e ele valer. Lockstep clássico: o
    /// comando entra num tick que os dois lados ainda não simularam, senão um
    /// deles teria que voltar no tempo.
    ///
    /// <para>Aqui ele é só a margem de trânsito entre o servidor carimbar e a
    /// mensagem chegar. O que garante que ninguém já simulou o tick alvo é a
    /// barreira, não este número — ver <see cref="TickDeComando"/>.</para>
    /// </summary>
    public const int TicksDeAtraso = 5;

    /// <summary>
    /// Quantos ticks cada lado pode simular além do que o outro já confirmou.
    ///
    /// <para>Isto é o buffer do lockstep, e estava embutido no
    /// <see cref="TicksDeAtraso"/> — o mesmo número servindo de atraso de
    /// comando e de pista de simulação. São coisas diferentes: o atraso existe
    /// para o comando cair num tick que ninguém simulou ainda; a pista existe
    /// para o jogo não parar a cada ida e volta da rede.</para>
    ///
    /// <para><b>Este número é a latência de comando.</b> Não dá para separar os
    /// dois: o comando tem de cair depois de tudo que alguém pode ter simulado,
    /// e a pista é exatamente a licença para simular à frente. Pista grande =
    /// rede some da conta e o comando demora; pista pequena = comando responde
    /// e a rede aparece. É o preço do lockstep, não um ajuste que escapa dele.</para>
    ///
    /// <para>Estava em 60 para mascarar o stall de 250 ms do relato. Com o
    /// stall resolvido na origem — quem trava na barreira relata na hora — 20
    /// basta: um terço de segundo de pista a 1×, e a latência do comando cai
    /// junto, porque as duas são a mesma coisa.</para>
    /// </summary>
    public const int FolgaDaBarreira = 20;

    public string Id { get; init; } = "";
    public TipoSessao Tipo { get; init; }
    public string Anfitriao { get; init; } = "";
    public string Visitante { get; init; } = "";
    public string ColoniaAnfitria { get; init; } = "";
    public int Semente { get; init; }
    public long TickInicial { get; init; }

    /// <summary>
    /// Quantas vezes esta sessão já refez o ponto de junção.
    ///
    /// <para>Existe porque ressincronizar não pode virar laço: uma causa que
    /// diverge de novo em dez segundos vai divergir na décima vez também, e aí a
    /// visita é só uma sequência de recarregamentos. Passado o orçamento, o
    /// aborto volta a ser a resposta — e aí é desistir com diagnóstico, não por
    /// falta de tentativa.</para>
    /// </summary>
    public int Ressincronizacoes { get; internal set; }

    /// <summary>Divergências já registradas nesta sessão, para o relatório do fim.</summary>
    public List<string> RelatoriosDeDivergencia { get; } = new();

    /// <summary>
    /// Passo em que o último ponto de junção foi refeito, ou -1.
    ///
    /// <para>Serve para uma pergunta que separa duas doenças muito diferentes:
    /// <b>quantos passos a divergência levou para voltar</b> depois de os dois
    /// lados partirem de um estado idêntico.</para>
    ///
    /// <para>Voltou depois de muito tempo: era divergência de <b>estado</b>, e
    /// ressincronizar resolveu — o que veio depois é outra causa. Voltou em
    /// alguns passos: é divergência de <b>comportamento</b>, e nenhum
    /// recarregamento vai resolver, porque os dois lados partem iguais e se
    /// afastam de novo. Só a segunda justifica gastar o orçamento.</para>
    /// </summary>
    public long PassoDoUltimoPonto { get; internal set; } = -1;

    /// <summary>Ensaio não compara digitais: ver <see cref="TipoSessao.Ensaio"/>.</summary>
    public bool CompararDigitais => Tipo != TipoSessao.Ensaio;

    public EstadoSessao Estado { get; internal set; } = EstadoSessao.Convidada;

    /// <summary>Até onde cada participante afirma ter simulado.</summary>
    public Dictionary<string, long> TickPorParticipante { get; } = new();

    /// <summary>Quem está pedindo pausa — §3, consenso de pausa.</summary>
    public HashSet<string> Pausados { get; } = new();

    /// <summary>
    /// A velocidade que está valendo — uma só, para os dois.
    ///
    /// <para>Qualquer um dos dois muda, e vale para todos. É o §3 lido como
    /// <b>controle comum</b>, e não como veto mútuo.</para>
    ///
    /// <para>Houve aqui um "o mais lento manda", e o defeito dele aparece
    /// depressa: pausado, só quem pausou conseguia despausar, porque o pedido
    /// do outro era sempre o mais rápido dos dois e perdia. Controle que
    /// responde a uma pessoa por vez não é compartilhado.</para>
    /// </summary>
    public VelocidadeDeSessao Velocidade { get; private set; } = VelocidadeDeSessao.Normal;

    /// <summary>
    /// Janela em que a pausa não pode ser desfeita.
    ///
    /// <para><b>Por que existe.</b> Pausar é pedido de atenção: alguém viu uma
    /// ameaça que o outro não viu. Mas a tecla de pausa é um <b>alternador</b> —
    /// ela quer dizer "o contrário do que estou vendo", e o que cada um está
    /// vendo pode estar velho por uma ida e volta.</para>
    ///
    /// <para>O caso ruim: jogo andando, um aperta espaço para parar, e no mesmo
    /// instante o outro aperta espaço — só que a pausa do primeiro já chegou na
    /// tela dele, então o espaço dele quer dizer <b>despausar</b>. A pausa
    /// dura um piscar e o combate segue. Isso mata pawn.</para>
    ///
    /// <para>Meio segundo resolve: cobre a ida e volta e o dedo do outro,
    /// e é curto demais para atrapalhar quem quer mesmo continuar — basta
    /// apertar de novo.</para>
    /// </summary>
    public static readonly TimeSpan ProtecaoDaPausa = TimeSpan.FromMilliseconds(500);

    /// <summary>Quem mexeu no tempo por último.</summary>
    public string QuemMudouOTempo { get; private set; } = "";

    /// <summary>
    /// A partir de qual **passo** a velocidade atual vale.
    ///
    /// <para>Sem isto, "se este passo simula" virava "o que eu sei agora" — e os
    /// dois clientes sabem em momentos diferentes. Um executava o passo 367
    /// antes de a pausa chegar e o outro depois, com o mesmo número de passo e
    /// resultados diferentes. Desync por defasagem de aviso, não por
    /// simulação.</para>
    ///
    /// <para>O valor é o primeiro passo que ainda não podia ser executado por
    /// ninguém no instante da mudança. Assim quem já passou por ele usou a
    /// velocidade antiga dos dois lados, e quem ainda não passou usará a nova
    /// dos dois lados.</para>
    /// </summary>
    public long VelocidadeDesdePasso { get; private set; }

    /// <summary>
    /// Quantas vezes o tempo mudou nesta sessão.
    ///
    /// <para>Serve ao cliente para saber se a resposta que chegou é **posterior**
    /// ao pedido dele. Sem isso ele só consegue perguntar "quem mudou por
    /// último?", e essa pergunta continua respondendo "o outro" muito depois de
    /// o outro ter mexido — então todo pedido novo era descartado como se
    /// tivesse sido superado.</para>
    /// </summary>
    public long VersaoDoTempo { get; private set; }

    DateTime pausadoEm;

    /// <summary>
    /// Quem mexeu manda — com uma exceção: <b>pausa tem prioridade</b>.
    ///
    /// <para>Sair da pausa dentro da janela de proteção é recusado, venha por
    /// espaço ou por número. O jogador que insistir consegue no clique
    /// seguinte; quem pausou ganha o instante de que precisava para falar.</para>
    /// </summary>
    public bool PedirVelocidade(string autor, VelocidadeDeSessao velocidade, DateTime agora)
    {
        bool saindoDaPausa =
            Velocidade == VelocidadeDeSessao.Pausado && velocidade != VelocidadeDeSessao.Pausado;

        if (saindoDaPausa && agora - pausadoEm < ProtecaoDaPausa) return false;

        if (velocidade == VelocidadeDeSessao.Pausado && Velocidade != VelocidadeDeSessao.Pausado)
            pausadoEm = agora;

        Velocidade = velocidade;
        QuemMudouOTempo = autor;

        // `relogio` é o primeiro passo ainda não liberado: ninguém pode ter
        // executado dali para frente.
        VelocidadeDesdePasso = (long)relogio;
        VersaoDoTempo++;
        return true;
    }

    /// <summary>
    /// O relógio compartilhado, em ticks, com a parte fracionária.
    ///
    /// <para>Guardar a fração importa: a 1× são 60 ticks por segundo, e um
    /// quadro de 16 ms vale 0,96 tick. Arredondar a cada quadro perderia quase
    /// um tick por quadro e o jogo andaria devagar.</para>
    /// </summary>
    double relogio;

    DateTime ultimoAvanco;

    /// <summary>
    /// Avança o relógio compartilhado até <paramref name="agora"/>.
    ///
    /// <para><b>Esta é a virada.</b> Antes a barreira só respondia "o mais lento
    /// chegou até aqui, podem ir até ali" — e cada cliente andava no ritmo do
    /// próprio relógio, o que os deixava em ticks diferentes. Pausados,
    /// congelavam separados: medido, um no tick 972 e o outro no 952.</para>
    ///
    /// <para>Agora quem anda é este relógio, no ritmo acordado, e os clientes
    /// simulam até ele o mais rápido que conseguem. Todos no mesmo tick, sempre.
    /// A velocidade local vira pedido; a pausa vira multiplicador zero.</para>
    /// </summary>
    public void AvancarRelogio(DateTime agora)
    {
        Andar(agora);

        // A liberação do passo parado vem **depois** de andar, e por último: o
        // teto do fim de `Andar` puxaria o passo de volta, e aí o comando ficava
        // pendurado num passo que nunca chegava.
        LiberarPassoParado();
    }

    /// <summary>
    /// O passo que um comando dado com o jogo parado precisa para acontecer —
    /// ainda <b>não liberado</b>.
    ///
    /// <para>Ele é carimbado no comando e só liberado no relato seguinte da
    /// barreira. Esse intervalo é de propósito: enquanto o passo não é liberado,
    /// ninguém pode tê-lo executado, e todos os comandos do mesmo clique cabem
    /// nele — as oito paredes aparecem juntas, num tick só.</para>
    ///
    /// <para>Liberar na hora de carimbar, como era antes, dava os dois piores
    /// resultados ao mesmo tempo: ou cada comando pegava um passo novo (a
    /// construção aparecia aos pedaços), ou dois comandos dividiam um passo que
    /// o outro lado já tinha executado — comando atrasado, sessão encerrada.</para>
    /// </summary>
    long? passoParado;

    void LiberarPassoParado()
    {
        if (passoParado is not { } passo) return;

        // Liberar **um além** do passo do comando: o cliente anda enquanto
        // `passo < liberado`.
        if (relogio < passo + 1) relogio = passo + 1;
        passoParado = null;
    }

    void Andar(DateTime agora)
    {
        if (TickPorParticipante.Count == 0)
        {
            ultimoAvanco = agora;
            return;
        }

        long minimo = TickPorParticipante.Values.Min();
        if (relogio < minimo) relogio = minimo;

        double segundos = (agora - ultimoAvanco).TotalSeconds;
        ultimoAvanco = agora;

        // Um salto grande é relógio de parede pulando (suspensão, depuração),
        // não jogo que precisa correr. Simular meio minuto de uma vez seria
        // pior que perder o meio minuto.
        if (segundos <= 0 || segundos > 0.5) return;

        float ritmo = RitmoDaSessao.Multiplicador(Velocidade);
        relogio += segundos * RitmoDaSessao.TicksPorSegundo * ritmo;

        // O mais lento continua mandando: o relógio nunca se afasta dele mais
        // que a pista. Sem isto, uma máquina lenta ficaria para trás sem fim.
        double teto = minimo + FolgaDaBarreira;
        if (relogio > teto) relogio = teto;
    }

    /// <summary>Impressões digitais por (tick, participante), para comparar.</summary>
    public Dictionary<(long tick, string autor), string> Fingerprints { get; } = new();

    /// <summary>
    /// Comparações seguidas em que as digitais não bateram.
    ///
    /// Zerado assim que uma comparação bate: divergência que se corrige
    /// sozinha não é desync, é ruído.
    /// </summary>
    public int DivergenciasSeguidas { get; internal set; }

    /// <summary>Ticks onde houve divergência, para o relatório de aborto.</summary>
    public List<long> TicksSuspeitos { get; } = new();

    /// <summary>
    /// Último tick em que os dois lados concordaram. É o ponto de rollback
    /// natural do aborto (§2.3, §14.3 decisão 5).
    /// </summary>
    public long UltimoTickValido { get; internal set; }

    public IEnumerable<string> Participantes
    {
        get
        {
            yield return Anfitriao;
            yield return Visitante;
        }
    }

    public bool Tem(string playerId) => playerId == Anfitriao || playerId == Visitante;

    public string Outro(string playerId) => playerId == Anfitriao ? Visitante : Anfitriao;

    /// <summary>
    /// Barreira comum: ninguém passa do tick que o mais lento já alcançou.
    /// Com alguém pedindo pausa, a barreira congela onde está (§3).
    /// </summary>
    /// <summary>
    /// Em que tick um comando proposto agora deve valer.
    ///
    /// <para>O que importa é ninguém ter simulado esse tick ainda — e quem sabe
    /// isso é a <b>barreira</b>, não o último relato. Carimbar a partir do
    /// maior tick relatado parecia mais apertado e barato, e estava errado: a
    /// cada quadro o jogo avança vários ticks e relata uma vez só, então o
    /// relatado fica para trás do simulado por quanto couber num quadro. Em
    /// Superfast isso deu 31 ticks, o comando chegou para um tick já passado e
    /// a sessão encerrou — que é o comportamento certo diante de um comando
    /// atrasado, pelo motivo errado.</para>
    ///
    /// <para><c>TickLiberado()</c> é o teto que o próprio servidor concedeu:
    /// ninguém pode ter passado dele. <see cref="TicksDeAtraso"/> cobre só o
    /// trânsito da mensagem de volta.</para>
    /// </summary>
    public long TickDeComando()
    {
        // O teto **já concedido**, não o de agora: com alguém pausado a
        // barreira recua para o mínimo, e carimbar a partir dela produzia
        // comando para um tick já simulado. Foi assim que o comando 8 nasceu
        // para o tick 458 depois de o comando 7 ter nascido para o 473.
        long alvo = Math.Max(TetoConcedido, TickLiberado()) + TicksDeAtraso;

        // Monotonicidade, cinto e suspensório: dois comandos nunca saem fora de
        // ordem, mesmo que o teto pare de crescer.
        if (alvo <= ultimoCarimbo) alvo = ultimoCarimbo + 1;

        // Com o jogo parado, o relógio não anda — e comando só acontece dentro
        // de um tick. Sem isto, alistar um pawn com o jogo pausado não fazia
        // nada até alguém despausar, que é metade do jeito de jogar RimWorld.
        //
        // A saída é o relógio dar **um passo** por ordem: um tick, o comando
        // acontece nele, e para de novo. Um tick a 1× é 16 ms de simulação —
        // não se vê, e a regra que importa continua de pé: os dois aplicam no
        // mesmo tick.
        if (Velocidade == VelocidadeDeSessao.Pausado)
        {
            // **Um passo, quantos comandos couberem.**
            //
            // Cada comando pedindo o próprio tick fazia oito designações virarem
            // oito ticks de simulação com o jogo "parado" — e, na tela, a coisa
            // aparecendo aos pedaços em vez de de uma vez.
            //
            // Comandos do mesmo tick são aplicados em ordem de carimbo, então
            // dividir o passo é seguro: os dois lados aplicam os mesmos
            // comandos, na mesma ordem, no mesmo tick.
            // **Carimbar a partir do teto, nunca do mais lento.**
            //
            // Aqui ficava `mínimo dos relatados + 1`, para reaproveitar o passo
            // pendente enquanto alguém ainda não tivesse chegado nele. Estava
            // errado por dois motivos, e os dois dão o mesmo resultado: comando
            // nascido para um passo que o **outro** lado já executou, recusado
            // na chegada, sessão encerrada. Era o "unsync instantâneo ao
            // construir pausado": a primeira construção parado matava a visita.
            //
            // 1. O mínimo é do mais atrasado, e o carimbo precisa valer para o
            //    mais adiantado. Entre pausar e o primeiro comando, um lado pode
            //    estar até `FolgaDaBarreira` passos à frente do outro.
            //
            // 2. O relato nem sempre é o passo atual: quando há digital
            //    pendente, o cliente relata o passo **da digital**, que é mais
            //    velho. O relatado é piso frouxo; o teto não é.
            //
            // `TetoConcedido` é o primeiro passo que o servidor ainda não
            // liberou: ninguém pode ter executado ele. É o único número aqui que
            // vale como "passo que com certeza está no futuro de todo mundo".
            //
            // O preço é perder o compartilhamento de passo entre comandos
            // seguidos: cada ordem parado custa o seu tick. A 1× são 16 ms, não
            // se vê — e o cliente já junta designações do mesmo clique num
            // comando só, que era o caso que doía.
            long maisRapido = TickPorParticipante.Count > 0
                ? TickPorParticipante.Values.Max()
                : TickInicial;

            // `ultimoCarimbo` entra sem `+1`: comandos do mesmo clique dividem o
            // passo enquanto ele não for liberado. Assim que a barreira o
            // libera, `TetoConcedido` passa dele e o próximo comando ganha um
            // passo novo — sem precisar adivinhar quando o lote fechou.
            alvo = Math.Max(Math.Max(TetoConcedido, maisRapido), ultimoCarimbo);

            // Marcado para liberar, não liberado. Ver `passoParado`: é esse
            // atraso de um relato que deixa o lote inteiro cair no mesmo tick
            // sem correr o risco de alguém já ter passado por ele.
            //
            // O cliente pede a barreira assim que recebe comando para um passo
            // que ainda não pode executar, então a espera é uma ida e volta.
            passoParado = alvo;

            ultimoCarimbo = alvo;
            return alvo;
        }

        ultimoCarimbo = alvo;
        return alvo;
    }

    long ultimoCarimbo;

    /// <summary>
    /// Maior tick que a barreira já liberou nesta sessão.
    ///
    /// <para>A barreira pode recuar — é assim que a pausa funciona, §3 — mas o
    /// que já foi concedido já foi simulado por alguém. Para carimbar comando,
    /// o que importa é este teto, que só cresce.</para>
    ///
    /// <para>Com ele, a margem de <see cref="TicksDeAtraso"/> não precisa
    /// cobrir a velocidade do jogo: o cliente só avança quando recebe uma
    /// liberação nova, e a liberação nova sai **depois** do comando na mesma
    /// conexão. A ordem do TCP faz o resto.</para>
    /// </summary>
    public long TetoConcedido { get; private set; }

    /// <summary>
    /// Todo mundo pediu pausa? Ver <c>SessaoBarreira.TodosPausados</c>.
    ///
    /// Exige que todos os participantes já tenham relatado alguma vez — senão
    /// um lado ainda carregando contaria como "não pausado" e ninguém poderia
    /// dar ordem parado.
    /// </summary>
    public bool TodosPausados() =>
        TickPorParticipante.Count >= 2 && Velocidade == VelocidadeDeSessao.Pausado;

    /// <summary>
    /// Recomeça a contagem a partir do ponto de junção novo.
    ///
    /// <para>Tudo o que descrevia o passo antigo tem de sumir: relatos, teto,
    /// carimbo e digitais. Deixar qualquer um deles para trás faria o primeiro
    /// comando depois da ressincronização nascer para um passo que, do outro
    /// lado, já passou — o mesmo erro que matava a construção com o jogo
    /// parado.</para>
    /// </summary>
    public void RecomecarEm(long tick)
    {
        TickPorParticipante.Clear();
        Fingerprints.Clear();
        TicksSuspeitos.Clear();
        DivergenciasSeguidas = 0;

        relogio = tick;
        TetoConcedido = tick;
        ultimoCarimbo = tick - 1;
        passoParado = null;
        UltimoTickValido = tick;
        PassoDoUltimoPonto = tick;
    }

    public long TickLiberado()
    {
        if (TickPorParticipante.Count < 2) return TickInicial;

        long liberado = (long)relogio;
        if (liberado > TetoConcedido) TetoConcedido = liberado;
        return liberado;
    }
}
