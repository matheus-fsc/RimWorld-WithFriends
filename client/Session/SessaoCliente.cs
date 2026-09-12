using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;

public enum EstadoSessaoLocal
{
    Fora,
    Convidado,
    Congelando,
    Simulando,

    /// <summary>
    /// Divergiu, e os dois lados estão refazendo o ponto de junção.
    ///
    /// <para>O laço de tick exige <see cref="Simulando"/>, então este estado já
    /// congela a simulação por si — que é o que se quer: ninguém avança
    /// enquanto o estado para o qual vão voltar está viajando.</para>
    /// </summary>
    Ressincronizando,

    Encerrando,
}

/// <summary>
/// A sessão vista de dentro do jogo — §2.3:
/// <c>convite → aceite → congelar → trocar estado → barreira → LOOP → encerrar</c>.
///
/// Decidir é do núcleo; executar é dos adaptadores. Esta classe é a costura:
/// recebe as mensagens já ordenadas pelo coordenador e aciona congelador,
/// relógio e impressão digital.
/// </summary>
public sealed class SessaoCliente
{
    /// <summary>
    /// Ticks entre relatos de barreira.
    ///
    /// Relatar em ticks **alinhados** (múltiplos deste número) é o que faz os
    /// dois lados relatarem os **mesmos** ticks — e o coordenador só compara
    /// digitais do mesmo tick. Com cadência por tempo real, cada lado reportava
    /// ticks diferentes e a comparação quase não acontecia: uma divergência
    /// nascida no tick 1400 só foi percebida no 1699.
    /// </summary>
    const int TicksEntreRelatos = 8;

    /// <summary>
    /// Tempo máximo sem relatar, para a barreira continuar andando quando o
    /// jogo está pausado e nenhum tick novo acontece.
    /// </summary>
    static readonly TimeSpan CadenciaDeBarreira = TimeSpan.FromMilliseconds(250);

    readonly CongeladorRimWorld congelador = new();
    readonly RelogioDeSessaoRimWorld relogio = new();
    readonly ImpressaoDigitalRimWorld digital = new();

    /// <summary>Comandos agendados, por tick de sessão.</summary>
    readonly Dictionary<long, List<SessaoComando>> agenda = new();

    float proximoRelato;
    long tickGameNoInicio;
    int mapaDaVisita = -1;

    /// <summary>O mapa do encontro. -1 fora de sessão.</summary>
    public int MapaDaVisita => mapaDaVisita;

    /// <summary>
    /// A visita já passou do ponto de partida?
    ///
    /// Antes disso os dois lados estão **congelados por nós**, esperando o
    /// outro entrar — e isso não é "o jogador pediu pausa". Reportar aquele
    /// congelamento como pausa travava a sessão para sempre: os dois pausados
    /// → o coordenador segura a barreira → a barreira nunca libera → ninguém
    /// despausa. Impossível sair, inclusive pela UI.
    /// </summary>
    bool visitaComecou;

    /// <summary>
    /// Última velocidade que já virou comando. Serve para detectar que o
    /// jogador mexeu no botão e propagar **uma vez**.
    /// </summary>
    TimeSpeed ultimaVelocidade = TimeSpeed.Paused;

    /// <summary>
    /// A velocidade que os jogadores **escolheram** — separada da que a
    /// barreira impõe.
    ///
    /// <para>Confundir as duas era um deadlock com uma pausa só. O relato dizia
    /// "estou pausado" lendo o relógio; mas o relógio estava pausado porque o
    /// consenso mandou, e o consenso continuava mandando porque o relato dizia
    /// que alguém queria pausa. Um jogador pausava uma vez e ninguém andava
    /// nunca mais.</para>
    ///
    /// <para>Pausa imposta não é pausa pedida. Só esta vira relato.</para>
    /// </summary>
    TimeSpeed velocidadeDesejada = TimeSpeed.Normal;

    /// <summary>
    /// O jogador mexeu no botão desde o último relato?
    ///
    /// Repetir o pedido não é mexer. Sem esta distinção, o relato periódico de
    /// um lado desfaria a mudança que o outro acabou de fazer, e o controle
    /// ficaria preso em quem falou por último sem querer.
    /// </summary>
    bool mudouVelocidade;

    /// <summary>
    /// Velocidade que este lado pediu e ainda não viu voltar.
    ///
    /// Até ela voltar, as respostas em trânsito falam de antes do clique, e
    /// aplicá-las desfaria o clique.
    /// </summary>
    TimeSpeed? pedidoPendente;

    /// <summary>Quando o pedido pendente saiu, em tempo real.</summary>
    float quandoPedi;

    /// <summary>Última versão do tempo vista do servidor.</summary>
    long versaoDoTempo;

    /// <summary>Versão que valia quando este lado pediu — só uma maior encerra o pedido.</summary>
    long versaoQuandoPedi;

    /// <summary>
    /// Este lado mexeu no tempo e ainda não sabe no que deu.
    ///
    /// <para>É a janela em que a previsão local da pausa vale (ADR 0016): o
    /// jogo para na hora aqui, sem esperar a ida e volta. Passada a janela, quem
    /// manda é a velocidade acordada — inclusive se ela disser que este lado
    /// ainda tem passos para andar.</para>
    /// </summary>
    public bool EsperandoRespostaDoTempo => pedidoPendente != null || mudouVelocidade;

    /// <summary>
    /// Depois disto, um pedido sem resposta é dado por perdido.
    ///
    /// Rede engasga, mensagem se perde, e o pedido pendente é o que impede este
    /// lado de aceitar mudanças do outro. Ele não pode ser eterno.
    /// </summary>
    const float SegundosParaDesistirDoPedido = 1.5f;

    /// <summary>Último tick já relatado, para não repetir o mesmo alinhamento.</summary>
    long ultimoTickRelatado = -1;

    /// <summary>
    /// Digital tirada no fim de um tick alinhado, esperando para ser enviada.
    ///
    /// <para>Antes a digital era tirada dentro do <c>Atualizar</c>, que roda uma
    /// vez por quadro. Mas o jogo avança **vários ticks por quadro** — em
    /// velocidade alta, mais de oito. O lado simplesmente pulava por cima dos
    /// múltiplos de <see cref="TicksEntreRelatos"/> sem nunca parar num, e a
    /// comparação só acontecia quando o acaso fazia o quadro terminar num tick
    /// alinhado.</para>
    ///
    /// <para>Medido: divergência nascida no tick 6989, primeiro tick suspeito
    /// no 7176 — <b>187 ticks depois</b>, com relatos espaçados de 20 em vez de
    /// 8. Tempo em que a sessão continua rodando errada e o rastreio, que
    /// guarda 400 ticks, já perdeu o começo.</para>
    ///
    /// <para>Agora a amostra é tirada no fim do tick, onde todo tick existe.</para>
    /// </summary>
    (long tick, string resumo)? digitalPendente;

    public EstadoSessaoLocal Estado { get; private set; } = EstadoSessaoLocal.Fora;
    public SessaoInicio? Atual { get; private set; }
    public SessaoConvite? ConvitePendente { get; private set; }
    /// <summary>
    /// O passo da sessão — o relógio dos <b>comandos</b>.
    ///
    /// <para>Era <c>TicksGame - tickGameNoInicio</c>, ou seja, o tick simulado.
    /// Com um relógio só, a única forma de um comando acontecer com o jogo
    /// pausado era simular um tick — e então pausar deixava de ser pausar.</para>
    ///
    /// <para>Agora são dois relógios, como no Multiplayer: o passo anda mesmo
    /// parado e é contra ele que os comandos são carimbados; a simulação só
    /// acontece nos passos em que o jogo não está pausado.</para>
    ///
    /// <code>
    /// para cada passo liberado pela barreira:
    ///     aplicar os comandos daquele passo      ← sempre
    ///     DoSingleTick()                          ← só se não estiver pausado
    /// </code>
    /// </summary>
    public long TickDeSessao { get; private set; }

    /// <summary>Quantos passos simularam e quantos só aplicaram comando.</summary>
    public long passosSimulados;
    public long passosSemSimular;

    /// <summary>
    /// Desde que passo cada velocidade vale — o suficiente para decidir por
    /// número de passo em vez de por "o que eu sei agora".
    ///
    /// <para>Curto de propósito: os dois lados nunca se afastam mais que a
    /// folga da barreira, então guardar as últimas mudanças basta. Uma lista que
    /// cresce sem fim seria vazamento disfarçado de histórico.</para>
    /// </summary>
    readonly List<(long desde, VelocidadeDeSessao velocidade)> velocidadePorPasso = new();

    /// <summary>
    /// Este passo deve simular?
    ///
    /// A pergunta certa é sobre o **passo**, não sobre o instante: os dois
    /// clientes recebem a mudança em momentos diferentes, e se cada um decidir
    /// pelo que sabe agora, o mesmo passo simula de um lado e não do outro.
    /// </summary>
    public bool PassoSimula(long passo)
    {
        var valendo = VelocidadeDeSessao.Normal;

        foreach (var (desde, velocidade) in velocidadePorPasso)
            if (desde <= passo) valendo = velocidade;

        return valendo != VelocidadeDeSessao.Pausado;
    }

    void AnotarVelocidade(long desde, VelocidadeDeSessao velocidade)
    {
        if (velocidadePorPasso.Count > 0)
        {
            var ultima = velocidadePorPasso[velocidadePorPasso.Count - 1];
            if (ultima.desde == desde && ultima.velocidade == velocidade) return;
        }

        velocidadePorPasso.Add((desde, velocidade));

        // Mudanças mais velhas que a folga já não podem ser consultadas por
        // passo nenhum que ainda esteja por vir.
        while (velocidadePorPasso.Count > 8) velocidadePorPasso.RemoveAt(0);
    }
    public long TickLiberado { get; private set; }

    /// <summary>
    /// Se já faz sentido comparar impressões digitais.
    ///
    /// O anfitrião está pronto de saída; o visitante só depois que o mapa da
    /// visita chegar e entrar. Comparar antes disso acusaria desync no
    /// primeiro relato — o mecanismo certo disparando pelo motivo errado,
    /// exatamente como no ensaio (ADR 0007).
    /// </summary>
    public bool ProntoParaComparar { get; private set; }

    // --- convite ---

    public void Receber(SessaoConvite convite)
    {
        string eu = WithFriendsMod.Settings.PlayerIdOuNovo();

        if (convite.De == eu)
        {
            Log.Message($"[WithFriends] convite {convite.ConviteId} enviado para {convite.Para}.");
            return;
        }

        ConvitePendente = convite;
        Find.LetterStack.ReceiveLetter(
            "With Friends: convite de sessão",
            $"{convite.De} convidou você para: {convite.Tipo}.\n\n" +
            "Aceitar congela sua colônia e cria um ponto de retorno. Se der divergência, " +
            "a sessão aborta e sua colônia volta a esse ponto — perde-se o encontro, " +
            "nunca a colônia.\n\n" +
            "Use a debug action \"Aceitar convite de sessão\".",
            LetterDefOf.PositiveEvent);
        Log.Message($"[WithFriends] convite {convite.ConviteId} de {convite.De} ({convite.Tipo})");
    }

    public void Aceitar()
    {
        if (ConvitePendente == null)
        {
            Log.Warning("[WithFriends] não há convite pendente.");
            return;
        }

        WithFriendsMod.Cliente.Enviar(new SessaoAceite
        {
            ConviteId = ConvitePendente.ConviteId,
            SessionModSetHash = ModSetHash.Calcular(),
        });
        Estado = EstadoSessaoLocal.Convidado;
        Log.Message($"[WithFriends] aceite enviado para {ConvitePendente.ConviteId}");
        ConvitePendente = null;
    }

    // --- ciclo de vida ---

    public void Iniciar(SessaoInicio inicio)
    {
        Atual = inicio;
        Estado = EstadoSessaoLocal.Congelando;

        // Congelar ANTES de qualquer tick compartilhado: é o ponto de retorno
        // que torna a sessão segura de tentar (§2.3).
        congelador.CongelarECheckpoint();

        tickGameNoInicio = Find.TickManager.TicksGame - inicio.TickInicial;
        TickDeSessao = inicio.TickInicial;
        TickLiberado = inicio.TickInicial;
        agenda.Clear();
        visitaComecou = inicio.Tipo == TipoSessao.Ensaio;   // no ensaio ninguém espera ninguém entrar
        RelogioDeSessaoRimWorld.ZerarDiagnostico();

        relogio.LimitarAte(TickLiberado);
        digital.IniciarIntervalo(inicio.TickInicial);

        // Os dois lados partem do mesmo estado de RNG (ADR 0009). Só vale
        // enquanto apenas os mapas da sessão tickarem — ver MapaInserido.
        if (inicio.Tipo != TipoSessao.Ensaio)
            RngDeSessao.Entrar(inicio.Semente);

        // O anfitrião manda o mapa do encontro: é o bootstrap da visita (§4).
        // No ensaio não há mapa compartilhado — só a máquina de tempo.
        bool ensaio = inicio.Tipo == TipoSessao.Ensaio;
        if (!ensaio && SouAnfitriao(inicio))
        {
            // A partida inteira, não o mapa (ADR 0010). O tick que viaja é o
            // do **jogo**, não o da sessão: é ele que o visitante usa para
            // frear a simulação assim que a partida trocar.
            //
            // E o anfitrião recarrega junto: os dois têm de partir do mesmo
            // estado **restaurado**, não um vivo e um restaurado.
            BootstrapDaPartida.Enviar(
                inicio.SessaoId, Find.TickManager.TicksGame, inicio, congelador.HashPreSessao);
            mapaDaVisita = Find.CurrentMap.uniqueID;
        }

        // O anfitrião já está no estado de referência; o visitante espera o
        // mapa. No ensaio ninguém compara (o servidor nem pediria).
        ProntoParaComparar = !ensaio && SouAnfitriao(inicio);

        Estado = EstadoSessaoLocal.Simulando;

        // No ensaio cada um segue no próprio ritmo sob a barreira. Numa visita
        // o anfitrião fica parado até o visitante entrar: um tick a mais aqui
        // é divergência real do outro lado, que ainda está carregando.
        if (ensaio) congelador.Descongelar();
        else if (SouAnfitriao(inicio)) Log.Message("[WithFriends] aguardando o visitante entrar na partida…");
        else Log.Message("[WithFriends] aguardando a partida do anfitrião chegar…");

        Messages.Message(
            $"Sessão iniciada com {Parceiro(inicio)} ({inicio.Tipo}).",
            MessageTypeDefOf.PositiveEvent, historical: true);
        Log.Message(
            $"[WithFriends] sessão {inicio.SessaoId} iniciada — tick de jogo {Find.TickManager.TicksGame}, " +
            $"tick de sessão {inicio.TickInicial}, digitais comparadas: {inicio.CompararDigitais}");
    }

    /// <summary>
    /// Enquanto a troca de partida não terminou, a partida **antiga** não tem
    /// nada a dizer sobre a sessão.
    ///
    /// Ela continua atualizando por vários frames depois do `LoadGame` (que é
    /// enfileirado), e qualquer barreira processada ali recalcula o freio de
    /// tick com o tick da colônia do visitante — desfazendo o freio armado
    /// para o tick do anfitrião. Foi o que deixou o visitante 2 ticks à frente.
    /// </summary>
    internal static bool TrocandoDePartida =>
        VisitaEmAndamento.Ativa && !VisitaEmAndamento.JaTrocouDePartida;

    public void Atualizar()
    {
        if (TrocandoDePartida) return;
        if (Estado != EstadoSessaoLocal.Simulando || Atual == null) return;

        // Ver o clique do jogador é trabalho de TODO quadro, não de quando dá
        // vontade de relatar.
        //
        // Estava lá embaixo, junto do envio, que acontece a cada 250 ms. As
        // respostas da barreira chegam muito mais rápido que isso — e cada uma
        // reflete a velocidade acordada no relógio local. O clique era apagado
        // pelo reflexo antes de alguém reparar que ele existiu, e o botão ficava
        // preso na velocidade antiga para sempre.
        PropagarVelocidade();
        AvisarSeAVisitaParou();

        long tick = TickDeSessao;
        bool tickAlinhado = digitalPendente != null;
        bool porTempo = Time.realtimeSinceStartup >= proximoRelato;

        // Parado na barreira é o sinal de "preciso de mais pista", e ele não
        // pode esperar cadência nenhuma.
        //
        // Era daqui que vinha a sensação de rede ruim em rede local: o limite
        // cai em `mínimo + folga`, que quase nunca é múltiplo de
        // TicksEntreRelatos. O jogo travava no limite, o relato alinhado nunca
        // chegava — porque o tick alinhado seguinte estava do outro lado do
        // freio — e sobrava o fallback de 250 ms. Dez ticks a cada 250 ms são
        // 40 ticks por segundo: menos que 1×, e a 3× o engasgo dos dois lados.
        bool travado = RelogioDeSessaoRimWorld.NaBarreira && tick != ultimoTickRelatado;

        // Mexeu no tempo, o relato sai AGORA.
        //
        // A cadência de 250 ms existe para o relato periódico, e o clique não é
        // periódico: enquanto ele esperava a vez, o outro lado seguia correndo.
        // Numa pausa a 3× isso são dezenas de ticks de combate acontecendo
        // depois de alguém mandar parar — e a pessoa aperta de novo achando que
        // falhou, o que no alternador vira um pedido de despausar.
        //
        // Pausa é pedido de atenção (Regra 1). Pedido de atenção não entra em
        // fila.
        if (!tickAlinhado && !porTempo && !travado && !mudouVelocidade) return;

        proximoRelato = Time.realtimeSinceStartup + (float)CadenciaDeBarreira.TotalSeconds;
        ultimoTickRelatado = tick;

        // A digital é uma função **do estado no tick**, não de quantas vezes
        // este lado reportou.
        //
        // Antes o intervalo acumulava entre relatos: quem esperou mais tempo
        // no mesmo tick mandava uma amostra a menos que quem acabou de chegar,
        // e os resumos diferiam sem que nada no jogo estivesse diferente. Foi
        // o que abortou a visita mesmo com os dois no mesmo tick e com a mesma
        // semente.
        //
        // Só os mapas da sessão entram: o mundo e a colônia privada de cada um
        // não são compartilhados (ADR 0009).
        // A digital pendente foi tirada no fim de um tick alinhado; sem ela,
        // este relato é só "estou vivo e aqui", sem comparação.
        string resumo = "";
        if (digitalPendente is { } pendente)
        {
            tick = pendente.tick;
            resumo = pendente.resumo;
            digitalPendente = null;
        }

        bool mudou = mudouVelocidade;
        mudouVelocidade = false;
        if (mudou)
        {
            pedidoPendente = velocidadeDesejada;
            quandoPedi = Time.realtimeSinceStartup;
            versaoQuandoPedi = versaoDoTempo;
            Log.Message($"[WithFriends/tempo] relatando mudança: {velocidadeDesejada} (tick {tick})");
        }

        WithFriendsMod.Cliente.Enviar(new SessaoBarreira
        {
            SessaoId = Atual.SessaoId,
            Tick = tick,
            // Digital vazia = "ainda não me compare". O coordenador só
            // compara quando os dois lados mandam alguma.
            Fingerprint = Atual.CompararDigitais && ProntoParaComparar ? resumo : "",
            // Só depois de a visita começar a pausa é intenção do jogador
            // (§3, consenso de pausa). Antes, é a espera do outro entrar.
            Pausado = visitaComecou && velocidadeDesejada == TimeSpeed.Paused,

            // Voto, não ordem: o coordenador junta os pedidos e o mais lento
            // manda. O relógio de cada jogador continua sendo dele.
            //
            // **Sem o `visitaComecou` aqui**, de propósito. Antes da visita
            // começar o relógio local está congelado por nós, não pelo jogador —
            // e relatar isso como "peço pausa" travava tudo antes de começar: o
            // relógio do coordenador não andava porque ninguém pedia tempo, e a
            // barreira não liberava o primeiro tick porque o relógio não andava.
            //
            // É o mesmo erro que `visitaComecou` conserta logo abaixo, na
            // bandeira de pausa: congelamento mecânico não é intenção do
            // jogador. O pedido continua sendo Normal enquanto esperamos.
            Velocidade = (VelocidadeDeSessao)(byte)velocidadeDesejada,
            MudouVelocidade = mudou,
        });
    }

    /// <summary>
    /// O arquivo do mapa chegou e está verificado no disco.
    ///
    /// **Não** liga a comparação de digitais: o mapa ainda não está na
    /// partida, então os dois lados continuam em estados legitimamente
    /// diferentes. Comparar aqui aborta toda visita no tick 0 — foi o que
    /// aconteceu no primeiro teste com dois jogos.
    /// </summary>
    /// <summary>
    /// Chegou a partida do anfitrião. A partir daqui o visitante **troca de
    /// jogo**: o que precisa sobreviver vai para <see cref="VisitaEmAndamento"/>.
    /// </summary>
    int ultimaRessincronizacao;

    /// <summary>
    /// Refaz o ponto de junção no meio da visita, em vez de perder o encontro.
    ///
    /// <para>É o mesmo caminho do bootstrap (ADR 0010), disparado por
    /// divergência em vez de por alguém entrando: o anfitrião manda a partida e
    /// <b>os dois</b> recarregam. Recarregar dos dois lados não é desperdício —
    /// um jogo vivo e o mesmo jogo recém-carregado não são idênticos (medimos
    /// 891 coisas de diferença), e sair de estados diferentes é justamente o que
    /// produziria a próxima divergência.</para>
    ///
    /// <para>O pedido é <b>idempotente</b>: o coordenador o repete enquanto os
    /// dois não chegarem, para que um lado que o perdeu no meio de um
    /// recarregamento não deixe a visita pendurada. Quem já tratou aquele número
    /// ignora.</para>
    /// </summary>
    public void Ressincronizar(SessaoRessincronizar pedido)
    {
        if (Atual == null || pedido.SessaoId != Atual.SessaoId) return;
        if (pedido.Numero <= ultimaRessincronizacao) return;
        ultimaRessincronizacao = pedido.Numero;

        Estado = EstadoSessaoLocal.Ressincronizando;
        agenda.Clear();

        Log.Warning(
            $"[WithFriends] ressincronizando (#{pedido.Numero}): voltando ao tick " +
            $"{pedido.TickAlvo}. {pedido.Explicacao}");

        Messages.Message(
            $"A visita divergiu e está voltando ao último ponto em que os dois batiam " +
            $"(passo {pedido.TickAlvo}). O encontro continua.",
            MessageTypeDefOf.NeutralEvent, historical: true);

        Atual = Atual.APartirDoTick(pedido.TickAlvo);

        // O visitante não faz nada aqui: ele espera a partida chegar, e
        // `PartidaRecebida` cuida do resto — a mesma porta do bootstrap.
        if (VisitaEmAndamento.SouVisitante) return;

        // **Passo de sessão e tick de jogo são dois números diferentes** (ADR
        // 0017), e trocar um pelo outro aqui já custou uma sessão inteira.
        //
        // `pedido.TickAlvo` é **passo de sessão** — é o que o coordenador conta,
        // é contra ele que a barreira libera, e é ele que `Retomar` põe em
        // `TickDeSessao`. O tick de jogo vem junto no save, igual para os dois,
        // e não precisa de ninguém para combiná-lo.
        //
        // Uma versão anterior recomeçava do tick de jogo do instantâneo. O lado
        // que recarregava passava a contar passos a partir de 66055 enquanto o
        // coordenador liberava 20, 266, 932 — e ficava parado para sempre, com o
        // outro jogando normalmente. O sintoma era "uma instância em play e a
        // outra parada", e a causa era esta troca.
        Log.Message(
            $"[WithFriends] ponto de junção novo no passo {pedido.TickAlvo} " +
            $"(tick de jogo {Find.TickManager.TicksGame})");

        BootstrapDaPartida.Enviar(
            Atual.SessaoId, pedido.TickAlvo, Atual, congelador.HashPreSessao);
    }

    public void PartidaRecebida(SessaoPartida mensagem)
    {
        if (Atual == null || mensagem.SessaoId != Atual.SessaoId) return;

        BootstrapDaPartida.Receber(mensagem, Atual, congelador.HashPreSessao);
    }

    /// <summary>
    /// Retoma a sessão depois da troca de partida — o visitante já está dentro
    /// da partida do anfitrião.
    ///
    /// Não congela nem tira checkpoint: isso aconteceu antes de sair, e o
    /// ponto de retorno está guardado fora da partida.
    /// </summary>
    public void Retomar(SessaoInicio inicio)
    {
        Atual = inicio;
        tickGameNoInicio = Find.TickManager.TicksGame - inicio.TickInicial;
        TickDeSessao = inicio.TickInicial;
        TickLiberado = inicio.TickInicial;
        agenda.Clear();
        visitaComecou = false;
        mapaDaVisita = Find.CurrentMap?.uniqueID ?? -1;

        relogio.LimitarAte(TickLiberado);
        digital.IniciarIntervalo(inicio.TickInicial);
        RngDeSessao.Entrar(inicio.Semente);

        // Os dois lados estão agora na **mesma partida**: comparar faz sentido.
        ProntoParaComparar = true;
        Estado = EstadoSessaoLocal.Simulando;
        relogio.Pausar();

        Messages.Message(
            $"Você está na colônia de {inicio.Anfitriao}.",
            MessageTypeDefOf.PositiveEvent, historical: true);
        Log.Message(
            $"[WithFriends] visita retomada dentro da partida do anfitrião — " +
            $"tick de jogo {Find.TickManager.TicksGame}, mapa {mapaDaVisita}");
    }

    public void MapaRecebido(int mapaId, string caminho)
    {
        Log.Message($"[WithFriends] mapa {mapaId} verificado no disco — inserindo na partida…");

        var resultado = InsercaoDeMapa.Inserir(caminho, mapaId);
        Log.Message($"[WithFriends] {resultado}");

        if (!resultado.Ok)
        {
            // Sem mapa não há visita. Encerrar é melhor do que ficar numa
            // sessão que nunca vai comparar nada.
            Messages.Message(
                "Não foi possível abrir o mapa da visita. Encerrando a sessão.",
                MessageTypeDefOf.NegativeEvent, historical: true);
            PedirEncerramento($"o mapa da visita não abriu: {resultado}");
            return;
        }

        MapaInserido(resultado.MapaId);
    }

    /// <summary>
    /// O mapa da visita está **dentro** da partida e os dois lados simulam o
    /// mesmo estado. Só agora a digital deste lado vale alguma coisa.
    /// </summary>
    public void MapaInserido(int mapaId)
    {
        mapaDaVisita = mapaId;

        // A partir daqui só a sessão simula: é o que torna válido o push/pop
        // único de RNG (ADR 0009).
        CongeladorDeMapas.Congelar(mapaId);

        ProntoParaComparar = true;
        Log.Message(
            $"[WithFriends] mapa {mapaId} inserido — impressões digitais passam a ser " +
            $"comparadas a partir do tick {TickDeSessao}");
    }

    /// <summary>
    /// O jogador mexeu na velocidade: vira comando, carimbado pelo servidor e
    /// aplicado no mesmo tick nos dois lados (§3).
    ///
    /// Sem isto, um lado em Superfast e outro em Normal atravessam os mesmos
    /// ticks em momentos diferentes — e foi assim que um evento de ~123
    /// sorteios aconteceu com um tick de diferença entre os dois.
    /// </summary>
    /// <summary>
    /// Põe no relógio local a velocidade que está valendo para os dois.
    ///
    /// Atualiza <c>ultimaVelocidade</c> junto, senão <see cref="PropagarVelocidade"/>
    /// veria isto como clique do jogador e mandaria de volta — o eco que já
    /// custou caro uma vez.
    /// </summary>
    /// <summary>A velocidade que está valendo para os dois — para a interface mostrar.</summary>
    public VelocidadeDeSessao VelocidadeAcordada { get; private set; } = VelocidadeDeSessao.Normal;

    /// <summary>Quem mexeu no tempo por último — a etiqueta mostra.</summary>
    public string QuemMudouOTempo { get; private set; } = "";

    /// <summary>Quando a autoria mudou, em tempo real — para a etiqueta chamar atenção e depois assentar.</summary>
    public float QuandoMudouOTempo { get; private set; }

    void RefletirVelocidadeAcordada(SessaoBarreira barreira)
    {
        // Comparar ANTES de atribuir: estava ao contrário, e a condição
        // `barreira.VelocidadeAcordada != VelocidadeAcordada` era sempre falsa
        // porque o valor acabara de ser copiado. O destaque da etiqueta só
        // acendia quando trocava o autor, nunca quando o mesmo autor mudava
        // de velocidade.
        versaoDoTempo = barreira.VersaoDoTempo;
        AnotarVelocidade(barreira.VelocidadeDesdePasso, barreira.VelocidadeAcordada);

        bool novidade = barreira.QuemMudou.Length > 0
                        && (barreira.QuemMudou != QuemMudouOTempo
                            || barreira.VelocidadeAcordada != VelocidadeAcordada);

        VelocidadeAcordada = barreira.VelocidadeAcordada;
        if (novidade)
        {
            QuemMudouOTempo = barreira.QuemMudou;
            QuandoMudouOTempo = Time.realtimeSinceStartup;
        }

        if (barreira.DespausarRecusado)
        {
            // Recusa muda: o jogador aperta, nada acontece, e ele acha que a
            // tecla falhou. Pausa recente é decisão do outro, e ela precisa ser
            // dita — é o pedido de atenção funcionando.
            pedidoPendente = null;
            Messages.Message(
                "With Friends: alguém acabou de pausar. Aperte de novo se quiser mesmo continuar.",
                MessageTypeDefOf.RejectInput, historical: false);
        }

        var acordada = barreira.VelocidadeAcordada;

        if (!visitaComecou || Find.TickManager == null) return;

        var alvo = (TimeSpeed)(byte)acordada;

        // Enquanto o meu pedido não voltar, respostas em trânsito falam do
        // passado. Aplicá-las desfaz o clique que acabou de sair.
        //
        // Mas o pedido precisa **poder morrer**, e era isto que faltava: se o
        // outro jogador mexeu no tempo depois de eu pedir, a minha resposta
        // nunca vem — o servidor já está falando de outra coisa. O pedido ficava
        // pendurado para sempre e este lado parava de refletir qualquer
        // mudança. Era o "despausar num lado não despausa os dois": o outro não
        // estava ignorando o comando, estava preso esperando uma resposta que
        // não existia mais.
        if (pedidoPendente is { } pedido)
        {
            bool respondeuAMim = alvo == pedido;

            // **Versão, não autoria.** "O outro mudou por último" continua
            // verdade muito depois de ele ter mexido, então essa pergunta
            // descartava todo pedido novo como se já tivesse sido superado —
            // e era preciso clicar várias vezes até um pedido chegar antes da
            // próxima resposta.
            //
            // A pergunta certa é se o servidor mudou o tempo **depois** de eu
            // pedir, e para isso serve um contador que só cresce.
            bool aconteceuDepois = barreira.VersaoDoTempo > versaoQuandoPedi;
            bool esperouDemais =
                Time.realtimeSinceStartup - quandoPedi > SegundosParaDesistirDoPedido;

            if (!respondeuAMim && !aconteceuDepois && !esperouDemais)
            {
                Log.Message(
                    $"[WithFriends/tempo] resposta {acordada} ignorada: esperando a minha ({pedido})");
                return;
            }

            Log.Message(
                $"[WithFriends/tempo] pedido {pedido} encerrado — " +
                (respondeuAMim ? "respondeu a mim"
                 : aconteceuDepois ? $"superado pela versão {barreira.VersaoDoTempo} ({acordada})"
                 : "esperei demais"));
            pedidoPendente = null;
        }

        if (mudouVelocidade)
        {
            Log.Message($"[WithFriends/tempo] resposta {acordada} ignorada: clique novo esperando relato");
            return;
        }

        if (Find.TickManager.CurTimeSpeed == alvo) return;

        ControleDeVelocidade.ComoSistema(() => Find.TickManager.CurTimeSpeed = alvo);

        // Ler de volta, e não supor. O setter do jogo recusa em silêncio:
        //
        //     set { if (!PlayerCanControl) { …mensagem… } else curTimeSpeed = value; }
        //
        // Supondo que pegou, `ultimaVelocidade` ficava com o valor que eu
        // queria, `PropagarVelocidade` via diferença no quadro seguinte e
        // reportava a velocidade antiga **como clique novo**. O servidor
        // despausava, e a pausa voltava sozinha sem ninguém ter apertado nada.
        var efetiva = Find.TickManager.CurTimeSpeed;
        Log.Message(
            $"[WithFriends/tempo] relógio local → {efetiva}" +
            (efetiva != alvo ? $" (pedi {alvo}; o jogo recusou)" : $", por {barreira.QuemMudou}"));

        ultimaVelocidade = efetiva;
        velocidadeDesejada = efetiva;
    }

    /// <summary>
    /// Anota o que o jogador está pedindo. Não propõe nada.
    ///
    /// <para>Velocidade era comando: um lado mudava e o outro obedecia. Isso
    /// gerou três bugs seguidos — eco, pausa desfeita, dois cliques — porque a
    /// resposta sempre chega depois da intenção mais nova.</para>
    ///
    /// <para>Com o relógio no coordenador ela vira <b>pedido</b>, que viaja no
    /// relato da barreira. Ninguém mexe no botão de ninguém, e "o mais lento
    /// manda" (§3) sai de graça: pausa é multiplicador zero, e zero é o menor.</para>
    /// </summary>
    void PropagarVelocidade()
    {
        if (!visitaComecou || Find.TickManager == null) return;

        // Intenção, não diferença de estado. Apertar 2 com o relógio local já
        // em 2 continua sendo um pedido — e era justamente esse caso que se
        // perdia, fazendo a tecla "funcionar só às vezes".
        if (ControleDeVelocidade.Consumir() is not { } intencao) return;

        ultimaVelocidade = intencao;
        velocidadeDesejada = intencao;
        mudouVelocidade = true;
    }

    public void Barreira(SessaoBarreira barreira)
    {
        if (TrocandoDePartida) return;
        if (Atual == null || barreira.SessaoId != Atual.SessaoId) return;

        bool primeiraLiberacao = TickLiberado <= Atual.TickInicial && barreira.TickLiberado > Atual.TickInicial;

        TickLiberado = barreira.TickLiberado;
        relogio.LimitarAte(TickLiberado);

        // A barreira andou pela primeira vez: os dois estão dentro e no mesmo
        // tick. Só agora a visita começa de verdade.
        if (primeiraLiberacao)
        {
            visitaComecou = true;
            congelador.Descongelar();
            relogio.Retomar();
            ultimaVelocidade = Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Normal;
            velocidadeDesejada = ultimaVelocidade;
            Log.Message($"[WithFriends] visita começou — barreira liberou o tick {barreira.TickLiberado}");

            // Os dois lados devem estar simulando a mesma coisa. Se não
            // estiverem, é aqui que dá para ver — sem precisar do RNG.
            RetratoDaPartida.Registrar("início da visita");
        }

        // Consenso de pausa (§3 v1): qualquer um pausado para o tempo de todos.
        //
        // **A barreira já é o consenso.** Com alguém pausado o coordenador para
        // de liberar tick, e nada anda dos dois lados — sem precisar mexer no
        // botão de ninguém.
        //
        // Mexer no botão era a fonte de três bugs seguidos, e o último não tinha
        // conserto limpo: a resposta da barreira chega defasada, e aplicar um
        // consenso de antes por cima de uma intenção de agora desfaz o clique
        // que o jogador acabou de dar. Pausar, despausar e trocar velocidade
        // precisavam de dois cliques, porque o primeiro era revertido pela
        // resposta em trânsito.
        //
        // O relógio mostra o tempo que está valendo para os dois.
        //
        // Isto **não** é o consenso mexendo no botão do jogador, que foi o erro
        // de antes. Agora só existe um relógio: qualquer um dos dois o muda, e
        // os dois veem o resultado. Botão que mostra outra coisa é pior que
        // botão que não obedece — e aqui ele obedece, só que a quem mexeu por
        // último, que pode ser o outro.
        RefletirVelocidadeAcordada(barreira);
    }

    /// <summary>
    /// Chamado no fim de cada tick compartilhado, de dentro do próprio tick.
    ///
    /// É o único lugar onde **todo** tick é visto. O quadro do jogo não serve:
    /// ele pula ticks em lote.
    /// </summary>
    /// <summary>
    /// Executa um passo da sessão.
    ///
    /// <para>Aplicar comando é obrigação do passo; simular é opcional. É essa
    /// separação que faz uma ordem dada com o jogo parado acontecer na hora,
    /// sem o tempo do jogo andar.</para>
    ///
    /// <para>O contexto determinístico é montado aqui, em volta dos dois — e
    /// nessa ordem. A ordem inversa já custou um desync: um raid aplicado antes
    /// de o RNG da sessão estar instalado sorteou do fluxo do processo, que não
    /// tem relação nenhuma entre duas máquinas.</para>
    /// </summary>
    public void ExecutarPasso(bool simular)
    {
        if (Estado != EstadoSessaoLocal.Simulando) return;

        // Cada passo registra se simulou. Os dois lados têm de decidir igual —
        // e quando não decidem, é isto que mostra: mesmo número de passo, um
        // gastando sorteios e o outro não.
        if (simular) passosSimulados++;
        else passosSemSimular++;

        RngDeSessao.AntesDoTick();
        TempoRealDoTick.AntesDoTick(Find.TickManager?.TicksGame ?? 0);
        NaInterface.Tickando = true;
        RastreioDeRng.AbrirTick(TickDeSessao);

        try
        {
            // Lockstep clássico: os comandos do passo valem **antes** de ele
            // acontecer, na ordem que o servidor carimbou.
            AplicarComandosDoTick(TickDeSessao);

            if (simular) RelogioDeSessaoRimWorld.TickarUmaVez();
        }
        finally
        {
            NaInterface.Tickando = false;
            TempoRealDoTick.DepoisDoTick();
            RngDeSessao.DepoisDoTick(TickDeSessao);
        }

        TickCompleto(TickDeSessao);
        TickDeSessao++;
    }

    long passoQuandoOlhei = -1;
    float quandoOPassoMudou;
    float ultimoAvisoDeParada;

    /// <summary>
    /// A visita parada não pode ser um mistério.
    ///
    /// <para>Numa sessão o relógio local é um <b>voto</b>: quem libera passo é o
    /// coordenador. Se ele some, nada anda — e a tela fica exatamente igual a um
    /// jogo travado. Aconteceu: a conexão caiu no meio de um combate, o jogador
    /// apertou espaço dezenas de vezes, e o único sinal era um aviso de
    /// reconexão perdido no log. Do lado de dentro do jogo, "congelou".</para>
    ///
    /// <para>Botão de velocidade que não responde numa visita quase sempre quer
    /// dizer isto, então a mensagem diz o estado da conexão junto: é a diferença
    /// entre "o mod travou" e "o coordenador não está respondendo".</para>
    /// </summary>
    void AvisarSeAVisitaParou()
    {
        float agora = Time.realtimeSinceStartup;

        if (TickDeSessao != passoQuandoOlhei)
        {
            passoQuandoOlhei = TickDeSessao;
            quandoOPassoMudou = agora;
            return;
        }

        // Parado é normal: com o jogo pausado o passo também anda, mas devagar.
        // Cinco segundos sem nenhum passo é outra coisa.
        if (agora - quandoOPassoMudou < SegundosParadoParaAvisar) return;
        if (agora - ultimoAvisoDeParada < SegundosEntreAvisosDeParada) return;

        ultimoAvisoDeParada = agora;

        var conexao = WithFriendsMod.Cliente.Estado;
        string diagnostico = conexao == Net.EstadoConexao.Conectado
            ? "o coordenador está conectado mas não liberou o próximo passo — " +
              "o outro jogador pode estar carregando ou travado"
            : $"a conexão com o coordenador está {conexao}";

        string recado =
            $"A visita está parada há {agora - quandoOPassoMudou:F0}s: {diagnostico}. " +
            "O relógio local não manda sozinho numa visita — por isso os botões de " +
            "velocidade não respondem.";

        Messages.Message(recado, MessageTypeDefOf.NegativeEvent, historical: false);
        Log.Warning(
            $"[WithFriends] visita parada no passo {TickDeSessao} há " +
            $"{agora - quandoOPassoMudou:F0}s (liberado até " +
            $"{RelogioDeSessaoRimWorld.LimiteDeTick}, conexão {conexao}).");
    }

    const float SegundosParadoParaAvisar = 5f;
    const float SegundosEntreAvisosDeParada = 10f;

    public void TickCompleto(long tickDeSessao)
    {
        if (Estado != EstadoSessaoLocal.Simulando || Atual == null) return;

        // Estado de pawn em TODO tick: ele não consome sorteio, então o rastreio
        // de RNG não o enxerga, e amostrar de oito em oito deixava causa e
        // efeito dentro da mesma amostra.
        RastreioDePawns.Amostrar(tickDeSessao, mapaDaVisita);

        if (tickDeSessao % TicksEntreRelatos != 0 || tickDeSessao == ultimoTickRelatado) return;

        ultimoTickRelatado = tickDeSessao;

        // A digital é função **do estado no tick**, não de quantas vezes este
        // lado reportou — por isso o intervalo abre e fecha no mesmo tick.
        digital.IniciarIntervalo(tickDeSessao);
        if (mapaDaVisita >= 0) digital.AmostrarMapa(mapaDaVisita);
        else digital.AmostrarMundo();

        digitalPendente = (tickDeSessao, digital.FecharIntervalo(tickDeSessao).Resumo());

        // **Os dois relógios, lado a lado, em toda amostra.**
        //
        // Comparando os diários de uma divergência, os dois lados tinham o mesmo
        // trabalho de simulação com o rótulo de passo deslocado em um: o que um
        // registrou no passo N, o outro registrou no N+1. Com os dois relógios
        // só no retrato do fim, não dava para dizer QUANDO o deslocamento
        // nasceu — e é o quando que aponta a causa.
        //
        // Se os passos batem e os ticks de jogo não, os dois lados discordaram
        // sobre algum passo simular, e a digital passou a comparar estados de
        // momentos diferentes. Isso é desync de relógio, não de simulação, e
        // procurar sorteio nesse caso é procurar no lugar errado.
        Log.Message(
            $"[WithFriends/digital] passo {tickDeSessao}: tick de jogo " +
            $"{Find.TickManager.TicksGame}, {passosSimulados} simulados, " +
            $"{passosSemSimular} só com comando");
    }

    public void Agendar(SessaoComando comando)
    {
        if (TrocandoDePartida) return;
        if (Atual == null || comando.SessaoId != Atual.SessaoId) return;

        // Velocidade não espera tick — e não pode esperar.
        //
        // Pausar é o único comando que impede o próprio tick que o aplicaria.
        // Com alguém pausado a barreira volta para o mínimo (§3, consenso de
        // pausa), o comando de despausar fica agendado num tick à frente da
        // barreira, e ninguém chega lá: para despausar era preciso já estar
        // andando. Os dois jogos congelavam com os comandos 1 a 4 pendurados.
        //
        // E não precisa esperar: velocidade muda **quando** cada lado chega a
        // um tick, nunca o que acontece dentro dele. Quem mantém os dois no
        // mesmo tick é a barreira, não a velocidade. Aplicar na chegada, em
        // ticks possivelmente diferentes, não muda simulação nenhuma.
        if (comando.Payload.Length > 0 && (TipoDeComando)comando.Payload[0] == TipoDeComando.Velocidade)
        {
            Log.Message(
                $"[WithFriends] comando {comando.Ordem} de {comando.Autor} aplicado na chegada: " +
                ComandoDeSessao.Aplicar(comando.Payload));

            ultimaVelocidade = Find.TickManager?.CurTimeSpeed ?? ultimaVelocidade;
            velocidadeDesejada = ultimaVelocidade;
            return;
        }

        // Comando para um tick que este lado já passou **nunca** seria
        // aplicado: ficaria na agenda para sempre, e o outro lado aplicaria.
        // Desync garantido, e silencioso — o pior tipo.
        //
        // Não deveria acontecer: o servidor agenda para barreira + atraso, e
        // ninguém passa da barreira. Se acontecer, é falha de protocolo e
        // precisa aparecer, não ser engolida.
        // `TickDeSessao` é o **próximo** passo a executar, não o último
        // executado: passos 0..N-1 já rodaram e N está por vir. Então um comando
        // para o passo N chegou na hora, não tarde.
        //
        // A guarda era `<=` e vinha da época em que o tick de sessão era o tick
        // de jogo, onde "estou em N" queria dizer "já simulei N". Com o passo
        // separado (ADR 0017), a mesma comparação passou a recusar comandos
        // perfeitamente válidos — e recusar aqui encerra a sessão.
        //
        // É a segunda vez que a fronteira "dentro ou fora?" morde: a primeira
        // foi o coordenador liberando até o passo do comando em vez de um além.
        long agora = TickDeSessao;
        if (comando.TickAlvo < agora)
        {
            Log.Error(
                $"[WithFriends] comando de {comando.Autor} chegou tarde: agendado para o tick " +
                $"{comando.TickAlvo}, e este lado já está em {agora}. " +
                "Aplicá-lo agora divergiria do outro lado; encerrando a sessão.");
            PedirEncerramento(
                $"comando de {comando.Autor} carimbado para o passo {comando.TickAlvo}, " +
                $"mas este lado já está em {agora}");
            return;
        }

        if (!agenda.TryGetValue(comando.TickAlvo, out var lista))
            agenda[comando.TickAlvo] = lista = new List<SessaoComando>();
        lista.Add(comando);

        // Chegou comando para um tick que ainda não foi liberado: pedir a
        // barreira **agora**.
        //
        // Parado, o cliente só descobre que ganhou o passo na próxima resposta
        // — e, parado na barreira, o relato daquele tick já saiu, então ele caía
        // no fallback de 250 ms. Cada cliente no seu ciclo, cada um aplicando
        // num momento diferente: era o "a construção aparece em tempo diferente
        // para cada player", e às vezes parecia não aparecer.
        if (comando.TickAlvo > TickLiberado) proximoRelato = 0f;

        Log.Message(
            $"[WithFriends] comando {comando.Ordem} de {comando.Autor} agendado para o tick " +
            $"{comando.TickAlvo} (estou em {agora})");
    }

    /// <summary>
    /// Aplica o que estiver marcado para este tick, em ordem idêntica dos dois
    /// lados — a ordem vem do servidor, não do relógio de ninguém.
    /// </summary>
    public void AplicarComandosDoTick(long tick)
    {
        if (!agenda.TryGetValue(tick, out var comandos)) return;
        agenda.Remove(tick);

        foreach (var comando in comandos.OrderBy(c => c.Ordem))
        {
            // Quanto o comando custou em aleatoriedade: se os dois lados
            // gastarem valores diferentes aqui, o comando é a causa.
            uint antes = RngDeSessao.Iteracoes();
            var cronometro = System.Diagnostics.Stopwatch.StartNew();
            string efeito = ComandoDeSessao.Aplicar(comando.Payload);
            cronometro.Stop();
            uint depois = RngDeSessao.Iteracoes();

            // A velocidade aplicada por comando não pode ser reproposta.
            if (Find.TickManager != null) ultimaVelocidade = Find.TickManager.CurTimeSpeed;

            // O tempo entra no log porque "trava o jogo com piso grande" precisa
            // de número: um comando que custa 300 ms acontece dentro de um
            // único DoSingleTick, e nada interrompe isso pela metade.
            string custo = cronometro.Elapsed.TotalMilliseconds >= 5
                ? $", {cronometro.Elapsed.TotalMilliseconds:F0} ms"
                : "";

            Log.Message(
                $"[WithFriends] comando {comando.Ordem} de {comando.Autor} no tick {tick}: " +
                $"{efeito} (rng +{depois - antes}{custo})");
            digital.AmostrarComando();
        }
    }

    public void Encerrar(SessaoFim fim)
    {
        if (Atual == null && !VisitaEmAndamento.Ativa) return;

        Log.Message($"[WithFriends] sessão {fim.SessaoId} encerrada ({fim.Motivo}): {fim.Explicacao}");
        Messages.Message($"Sessão encerrada: {fim.Motivo}.", MessageTypeDefOf.NeutralEvent, historical: true);

        Limpar();

        if (VoltarParaCasa()) return;   // o visitante recarrega; não há o que commitar aqui

        // Commit do anfitrião: o estado pós-sessão vira checkpoint próprio (§2.3).
        try { CheckpointAutomaticoDoFim(); }
        catch (Exception e) { Log.Warning($"[WithFriends] falha ao guardar o commit da sessão: {e.Message}"); }
    }

    /// <summary>
    /// O visitante volta para a própria colônia, restaurando o
    /// <c>checkpoint_pre_sessao</c> — que ficou guardado **fora** da partida,
    /// justamente porque a partida atual é a do anfitrião (ADR 0010).
    ///
    /// Devolve <c>true</c> se houve retorno (e portanto uma carga em curso).
    /// </summary>
    static bool VoltarParaCasa()
    {
        if (VisitaEmAndamento.Ativa && !VisitaEmAndamento.SouVisitante)
        {
            // O anfitrião já está em casa: a partida da visita **é** a dele.
            // Só limpa o estado e o save temporário.
            string? save = VisitaEmAndamento.SaveDaVisita;
            VisitaEmAndamento.Limpar();
            BootstrapDaPartida.Descartar(save);
            return false;
        }

        if (!VisitaEmAndamento.SouVisitante) return false;

        string? pontoDeRetorno = VisitaEmAndamento.HashPreSessao;
        string? saveDaVisita = VisitaEmAndamento.SaveDaVisita;
        VisitaEmAndamento.Limpar();
        BootstrapDaPartida.Descartar(saveDaVisita);

        if (pontoDeRetorno == null)
        {
            Log.Error(
                "[WithFriends] fim de visita sem ponto de retorno. Sua colônia está " +
                "intacta no disco — carregue-a pelo menu.");
            return false;
        }

        Messages.Message("Voltando para casa…", MessageTypeDefOf.NeutralEvent, historical: false);
        try
        {
            // guardarEstadoAtual: false — o jogo carregado agora é o do
            // anfitrião, e ele não é checkpoint de ninguém aqui.
            new CongeladorRimWorld().Restaurar(pontoDeRetorno, guardarEstadoAtual: false);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] não foi possível voltar para casa: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Aborto — §2.3: volta ao <c>checkpoint_pre_sessao</c>.
    /// **Perde-se o encontro, nunca a colônia.**
    /// </summary>
    public void Abortar(SessaoAborto aborto)
    {
        Log.Error($"[WithFriends] sessão {aborto.SessaoId} ABORTADA: {aborto.Explicacao}");

        // Só depois do desync o detalhe trafega/aparece (§14.3 decisão 4).
        RngDeSessao.Despejar($"aborto no tick {aborto.UltimoTickValido}");
        RastreioDeRng.Despejar(aborto.UltimoTickValido);
        RastreioDePawns.Despejar();
        RetratoDaPartida.Registrar("no aborto");

        bool souVisitante = VisitaEmAndamento.SouVisitante;
        string? pontoDeRetorno = souVisitante
            ? VisitaEmAndamento.HashPreSessao
            : congelador.HashPreSessao;
        string? saveDaVisita = VisitaEmAndamento.SaveDaVisita;
        Limpar();

        Find.LetterStack.ReceiveLetter(
            "With Friends: sessão abortada",
            aborto.Explicacao + "\n\n" +
            (pontoDeRetorno != null
                ? $"Voltando ao ponto de retorno {pontoDeRetorno.Substring(7, 12)}…"
                : "Não há ponto de retorno nesta máquina — sua colônia continua como está."),
            LetterDefOf.NegativeEvent);

        if (pontoDeRetorno == null) return;

        // Os dois lados largam o save temporário; só o visitante precisa
        // voltar para a própria colônia (o anfitrião já está na dele).
        VisitaEmAndamento.Limpar();
        BootstrapDaPartida.Descartar(saveDaVisita);

        try
        {
            congelador.Restaurar(pontoDeRetorno, guardarEstadoAtual: !souVisitante);
        }
        catch (Exception e)
        {
            // Nunca deixar o jogador num limbo silencioso: se não dá para
            // voltar, ele precisa saber para pedir o checkpoint ao servidor.
            Log.Error($"[WithFriends] não foi possível voltar ao ponto de retorno: {e.Message}");
            Messages.Message(
                "Não foi possível voltar ao ponto de retorno. Peça o checkpoint ao coordenador.",
                MessageTypeDefOf.NegativeEvent, historical: true);
        }
    }

    /// <summary>
    /// Encerra a visita, dizendo ao coordenador **por quê**.
    ///
    /// <para>A explicação vai junto porque o log do coordenador é o único que
    /// sobra: as duas instâncias do jogo escrevem no mesmo <c>Player.log</c>, e
    /// a segunda a abrir apaga o da primeira. Quem encerra costuma ser
    /// justamente o lado cujo log se perdeu.</para>
    /// </summary>
    public void PedirEncerramento(string porque = "")
    {
        if (Atual == null) return;
        WithFriendsMod.Cliente.Enviar(new SessaoFim { SessaoId = Atual.SessaoId, Explicacao = porque });
    }

    void Limpar()
    {
        visitaComecou = false;
        mapaDaVisita = -1;

        CongeladorDeMapas.Liberar();
        ProntoParaComparar = false;
        RngDeSessao.Sair();
        relogio.Liberar();
        congelador.Descongelar();
        agenda.Clear();
        Atual = null;
        Estado = EstadoSessaoLocal.Fora;
    }

    static void CheckpointAutomaticoDoFim()
    {
        var checkpoint = CheckpointWriter.Criar();
        WithFriendsMod.Cliente.Enviar(checkpoint.ParaMensagem());
        Log.Message($"[WithFriends] commit da sessão: {checkpoint.Metadata.ContentHash}");
    }

    static bool SouAnfitriao(SessaoInicio inicio) =>
        inicio.Anfitriao == WithFriendsMod.Settings.PlayerIdOuNovo();

    static string Parceiro(SessaoInicio inicio) =>
        inicio.Anfitriao == WithFriendsMod.Settings.PlayerIdOuNovo() ? inicio.Visitante : inicio.Anfitriao;
}
