using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Quem consumiu o RNG, e não só quanto.
///
/// <para>O histórico de <see cref="RngDeSessao"/> responde <b>em que tick</b> os
/// dois lados se separaram. Não responde <b>por causa de quê</b> — e sem isso a
/// caça vira adivinhação. Ideia vinda do Multiplayer (MIT, Zetrith), que guarda
/// pilhas de chamada por sorteio em <c>Desyncs/StackTraceLogItem</c>.</para>
///
/// <para>No aborto, os dois lados despejam a mesma janela de ticks agregada por
/// local de chamada. A linha com contagem diferente é a causa.</para>
///
/// <para><b>Custo.</b> Da primeira vez isto montava uma string por sorteio, o
/// que era caro o bastante para aparecer no jogo. Agora o caminho quente só
/// guarda ponteiros de método; nome e texto só existem no despejo. E o próprio
/// rastreio se mede — ver <see cref="Custo"/> — para a decisão de deixá-lo
/// ligado sair de número, não de impressão.</para>
/// </summary>
public static class RastreioDeRng
{
    /// <summary>
    /// Ticks de rastreio guardados.
    ///
    /// <para>Eram 400, e não bastavam. O histórico de RNG (que guarda 4.000)
    /// apontou a primeira divergência de uma sessão no tick <b>3604</b>; quando
    /// a detecção veio e o despejo aconteceu, o anel de locais já só tinha de
    /// 3.720 em diante. O instrumento que diz <b>onde</b> tinha esquecido
    /// justamente o tick que o instrumento que diz <b>quando</b> apontava.</para>
    ///
    /// <para>A detecção vem até algumas centenas de passos depois da causa: são
    /// três amostras de digital sem bater, de oito em oito, mais o trânsito. O
    /// anel precisa cobrir essa distância com folga, e agora cobre a mesma que o
    /// histórico.</para>
    ///
    /// <para><b>Por que não 4.000, como o histórico.</b> Tentei, e o anfitrião
    /// passou a estourar no meio do recarregamento da ressincronização — o
    /// momento de maior pressão de memória do mod, com duas partidas vivas ao
    /// mesmo tempo. O histórico guarda dois números por tick; este guarda um
    /// dicionário de locais por tick, e dez vezes mais ticks é outra ordem de
    /// grandeza.</para>
    ///
    /// <para>1.200 cobre com folga a distância entre a causa e a detecção — três
    /// amostras de digital sem bater, de oito em oito, mais o trânsito — que é o
    /// que faltava quando eram 400.</para>
    /// </summary>
    const int TicksGuardados = 1200;

    /// <summary>
    /// Quadros de pilha que identificam um local de chamada.
    ///
    /// Seis era pouco: a cadeia da ideologia
    /// (<c>MemberWillingToDo &lt; Ideo &lt; IdeoUtility &lt; IsTeetotaler &lt; CanTakeDrug</c>)
    /// gasta cinco sozinha, e **quem chama** — a parte que interessa — ficava
    /// de fora. Como a chave virou um hash, profundidade é de graça.
    /// </summary>
    const int Profundidade = 12;

    static readonly Queue<long> ordem = new();

    /// <summary>
    /// Contagem por tick, por local de chamada.
    ///
    /// O local é um hash FNV-1a dos ponteiros dos métodos da pilha: comparar
    /// ponteiros é barato, resolver nome é caro, e o hash deixa a profundidade
    /// sair de graça. O nome fica para o despejo, uma vez por aborto.
    /// </summary>
    static readonly Dictionary<long, Dictionary<long, int>> porTick = new();

    /// <summary>
    /// A <b>sequência</b> de locais de chamada de cada tick, não só a contagem.
    ///
    /// <para><b>Por que a contagem não bastou.</b> Numa divergência real o tick
    /// 857 tinha só 3 sorteios de diferença no total — mas a composição era
    /// outra:</para>
    ///
    /// <code>
    /// A=20  B=12   CellRect.RandomCell &lt; Region.RandomCell
    /// A=10  B=6    RCellFinder.RandomWanderDestFor &lt; JobGiver_Wander
    /// A=0   B=2    PreceptComp_UnwillingToDo_Chance &lt; IdeoUtility.Notify_PawnDid…
    /// </code>
    ///
    /// <para>Quatro escolhas de destino a mais de um lado, duas notificações de
    /// ideologia a mais do outro, quase se cancelando. Isso não é "um lado fez
    /// mais": é o mesmo tick fazendo trabalho <b>diferente</b>, e agregado por
    /// local não dá para saber em que sorteio as duas histórias se separam.</para>
    ///
    /// <para>Com a sequência, dá: o comparador acha o primeiro índice em que os
    /// dois discordam e diz quem consumiu aquele número de cada lado. É a
    /// diferença entre "divergiram neste tick" e "divergiram no 47º sorteio
    /// deste tick, e foi aqui".</para>
    ///
    /// <para>Custo: um <c>long</c> por sorteio, num anel do mesmo tamanho do
    /// outro. Medido em ~47 sorteios por tick, são uns 56 mil longs — meio
    /// megabyte, e nenhuma captura de pilha a mais, porque o hash do local já
    /// é calculado para a contagem.</para>
    /// </summary>
    static readonly Dictionary<long, List<long>> sequenciaPorTick = new();

    /// <summary>Nomes já resolvidos, montados na primeira vez que cada local aparece.</summary>
    static readonly Dictionary<long, string> nomes = new();
    static readonly Stopwatch cronometro = new();

    static long tickAtual = -1;
    static bool dentro;      // montar a pilha não pode se rastrear
    static long sorteios;

    public static bool Ligado { get; set; } = true;

    static bool medindo;

    public static bool Ativo => medindo || (Ligado && RngDeSessao.Ativo && NaInterface.Tickando);

    /// <summary>
    /// Mede quanto custa capturar um local de chamada, agora.
    ///
    /// <para><b>Por que existe.</b> A decisão de deixar o rastreio ligado por
    /// padrão precisa de número. Ele já mediu 88 µs por sorteio — mas naquela
    /// versão o caminho quente resolvia metadado de método, que era também o que
    /// derrubava o jogo. Depois de tirar a reflexão dali, o custo é outro, e
    /// medir é mais barato que supor.</para>
    ///
    /// <para><b>O viés, dito na cara.</b> A pilha dentro de uma debug action é
    /// mais rasa que a pilha dentro de um tick — e o custo cresce com a
    /// profundidade. Este número é <b>piso</b>, não média. O valor de verdade
    /// continua sendo o <see cref="Custo"/> depois de uma visita, que mede o que
    /// aconteceu de fato.</para>
    /// </summary>
    public static string Medir(int amostras = 20000)
    {
        bool ligadoAntes = Ligado;
        long tickAntes = tickAtual;

        medindo = true;
        AbrirTick(-999);

        var relogio = Stopwatch.StartNew();
        for (var i = 0; i < amostras; i++) Registrar();
        relogio.Stop();

        medindo = false;
        Ligado = ligadoAntes;
        tickAtual = tickAntes;

        // A medição não pode sujar o anel de diagnóstico.
        if (porTick.Remove(-999L)) { }
        var restantes = new Queue<long>(ordem.Where(t => t != -999L));
        ordem.Clear();
        foreach (var t in restantes) ordem.Enqueue(t);

        double micros = relogio.Elapsed.TotalMilliseconds * 1000 / amostras;
        double porSegundoA1x = micros * 20 * 60 / 1000;   // ~20 sorteios por tick, 60 ticks/s

        return
            $"{amostras} captura(s) em {relogio.Elapsed.TotalMilliseconds:F0} ms " +
            $"= {micros:F1} µs cada.\n" +
            $"  A ~20 sorteios por tick, isso é ~{porSegundoA1x:F0} ms por segundo de jogo a 1× " +
            $"({porSegundoA1x / 10:F1}% de um núcleo).\n" +
            "  Piso, não média: a pilha dentro de um tick é mais funda que aqui, e o custo cresce " +
            "com a profundidade. O número real sai no Custo depois de uma visita.";
    }

    /// <summary>Quanto o diagnóstico custou até agora, para decidir com número.</summary>
    public static string Custo =>
        sorteios == 0
            ? "nenhum sorteio rastreado"
            : $"{sorteios} sorteio(s) em {cronometro.Elapsed.TotalMilliseconds:F0} ms " +
              $"({cronometro.Elapsed.TotalMilliseconds * 1000 / sorteios:F1} µs por sorteio)";

    /// <summary>
    /// A sequência de um tick, em nomes. Vazia se aquele tick saiu do anel.
    /// </summary>
    public static IReadOnlyList<string> SequenciaDe(long tick)
    {
        if (!sequenciaPorTick.TryGetValue(tick, out var sequencia)) return Array.Empty<string>();

        var fora = new List<string>(sequencia.Count);
        foreach (long local in sequencia)
            fora.Add(nomes.TryGetValue(local, out var nome) ? nome : $"local {local:x}");
        return fora;
    }

    public static void Limpar()
    {
        ordem.Clear();
        porTick.Clear();
        nomes.Clear();
        cronometro.Reset();
        sorteios = 0;
        tickAtual = -1;
    }

    public static void AbrirTick(long tickDeSessao) => tickAtual = tickDeSessao;

    public static void Registrar()
    {
        if (!Ativo || dentro || tickAtual < 0) return;

        dentro = true;
        cronometro.Start();
        try
        {
            if (!porTick.TryGetValue(tickAtual, out var contagens))
            {
                porTick[tickAtual] = contagens = new Dictionary<long, int>();
                sequenciaPorTick[tickAtual] = new List<long>(64);
                ordem.Enqueue(tickAtual);
                while (ordem.Count > TicksGuardados)
                {
                    long velho = ordem.Dequeue();
                    porTick.Remove(velho);
                    sequenciaPorTick.Remove(velho);
                }
            }

            var local = Capturar();
            contagens.TryGetValue(local, out var n);
            contagens[local] = n + 1;

            if (sequenciaPorTick.TryGetValue(tickAtual, out var sequencia)) sequencia.Add(local);

            sorteios++;
        }
        catch (Exception) { /* rastreio nunca pode derrubar o tick */ }
        finally
        {
            cronometro.Stop();
            dentro = false;
        }
    }

    /// <summary>
    /// Quantos sorteios aconteceram fora do tick.
    ///
    /// <para><b>Só a contagem, não o local.</b> Capturar a pilha custa ~116 µs.
    /// Dentro do tick isso cabe: são dezenas de sorteios por tick. Fora dele é
    /// desenho, e desenho sorteia milhares de vezes por quadro — quando tentei
    /// capturar todos, o jogo travou a ponto de precisar matar o processo.</para>
    ///
    /// <para>A contagem sozinha já responde a pergunta que importa: se os dois
    /// lados tiverem números diferentes, a interface está consumindo o gerador
    /// compartilhado, e o conserto não é mais um guarda por método — é isolar o
    /// gerador da interface.</para>
    /// </summary>
    public static void DespejarForaDoTick() =>
        Log.Message(
            $"[WithFriends] sorteios fora do tick: {ContadorDeSorteios.ForaDoTick}\n" +
            "  (número diferente entre os dois lados = a interface está consumindo o Rand)");

    /// <summary>
    /// Identifica o local de chamada <b>sem tocar em metadado</b>.
    ///
    /// <para>Isto derrubou o jogo, e a pilha do crash nomeou o culpado:</para>
    ///
    /// <code>
    /// #3  (wrapper managed-to-native) System.RuntimeType:get_Namespace
    /// #4  RastreioDeRng:Registrar ()
    /// #7  (wrapper dynamic-method) Verse.Rand.get_Int_Patch1 ()
    /// </code>
    ///
    /// <para>A pilha de um sorteio passa pelos métodos dinâmicos que o Harmony
    /// gera. Perguntar <c>Namespace</c>, <c>MethodHandle</c> ou
    /// <c>MetadataToken</c> a um método dinâmico é pedir metadado a algo que não
    /// tem — e no Mono isso não estoura exceção que dê para pegar: mata o
    /// processo com sinal.</para>
    ///
    /// <para>A identidade agora vem de <c>RuntimeHelpers.GetHashCode</c>, que é
    /// o endereço do objeto e não pergunta nada. Nome só no despejo, uma vez por
    /// aborto, dentro de try/catch. De quebra o caminho quente ficou bem mais
    /// barato — os 88 µs por sorteio eram, em boa parte, reflexão.</para>
    /// </summary>
    static long Capturar()
    {
        var pilha = new StackTrace(2, false);
        int guardados = 0;
        long hash = unchecked((long)14695981039346656037UL);   // FNV-1a, base
        MethodBase[]? metodos = null;

        for (var i = 0; i < pilha.FrameCount && guardados < Profundidade; i++)
        {
            var metodo = pilha.GetFrame(i)?.GetMethod();
            if (metodo == null) continue;

            (metodos ??= new MethodBase[Profundidade])[guardados++] = metodo;
            hash = unchecked((hash ^ RuntimeHelpers.GetHashCode(metodo)) * 1099511628211L);
        }

        if (metodos != null && !nomes.ContainsKey(hash)) nomes[hash] = Nomear(metodos);
        return hash;
    }

    /// <summary>
    /// Nome legível, no despejo. Cada quadro sozinho no try/catch: um método
    /// dinâmico no meio da pilha não pode apagar os outros onze.
    /// </summary>
    static string Nomear(MethodBase[] metodos)
    {
        var partes = new List<string>(Profundidade);

        foreach (var metodo in metodos)
        {
            if (metodo == null) continue;

            try
            {
                partes.Add(metodo.DeclaringType is { } tipo
                    ? $"{tipo.Name}.{metodo.Name}"
                    : $"(dinâmico).{metodo.Name}");
            }
            catch (Exception)
            {
                partes.Add("(sem metadado)");
            }
        }

        return string.Join(" < ", partes);
    }

    /// <summary>
    /// Despeja a janela em volta do tick do aborto, agregada por local.
    ///
    /// Formato feito para <c>diff</c>: uma linha por local, ordenada, contagem
    /// na frente. A linha que só existe de um lado — ou que tem número
    /// diferente — é a causa.
    /// </summary>
    /// <summary>
    /// <paramref name="ticksDepois"/> precisa passar da **detecção**, não do
    /// rollback.
    ///
    /// <para>Eram 8, e isso cortava justamente a região que interessa. O tick do
    /// aborto é o último ponto <b>consistente</b>; a divergência nasce depois
    /// dele e só é detectada mais tarde ainda — três amostras seguidas sem
    /// bater, de oito em oito ticks, mais o tempo de a mensagem chegar. Numa
    /// divergência real medida, o último válido foi 5408, a causa apareceu no
    /// 5470 e a detecção no 5536: a janela terminava no 5416 e não mostrava
    /// nada. O anel guarda esses ticks — só não os imprimia.</para>
    /// </summary>
    public static void Despejar(long tickDoAborto, int ticksAntes = TicksGuardados, int ticksDepois = 200)
    {
        if (porTick.Count == 0)
        {
            Log.Message(
                "[WithFriends] rastreio de RNG desligado — sem locais de chamada para mostrar.\n" +
                "  Ligue com a debug action \"Rastrear RNG da sessão\" nos DOIS jogos e refaça a visita.");
            return;
        }

        long inicio = tickDoAborto - ticksAntes;
        long fim = tickDoAborto + ticksDepois;
        var texto = new StringBuilder();

        texto.AppendLine(
            $"[WithFriends] rastreio de RNG por local de chamada — " +
            $"geração {VisitaEmAndamento.UltimaRessincronizacao}, ticks {inicio} a {fim}\n" +
            $"  (compare este bloco com o do outro jogador; a linha com contagem diferente é a causa)\n" +
            $"  janela: o anel inteiro — a divergência costuma nascer bem antes de ser detectada\n" +
            $"  custo do diagnóstico: {Custo}");

        foreach (var tick in ordem.Where(t => t >= inicio && t <= fim).OrderBy(t => t))
        {
            var contagens = porTick[tick];
            texto.AppendLine($"  tick {tick,6}  total {contagens.Values.Sum(),4}");

            foreach (var par in contagens
                         .Select(p => (nome: nomes.TryGetValue(p.Key, out var n) ? n : "(desconhecido)", p.Value))
                         .OrderByDescending(p => p.Value)
                         .ThenBy(p => p.nome, StringComparer.Ordinal))
                texto.AppendLine($"      {par.Value,4}x  {par.nome}");
        }

        // Em pedaços: o anel inteiro é grande, e o despejo acontece logo antes
        // do rollback, que já é o pico de memória do mod.
        foreach (var pedaco in EmPedacos(texto.ToString(), 400))
            Log.Message(pedaco);

        DespejarSequencia(tickDoAborto);
    }

    /// <summary>
    /// A sequência de sorteios de uma janela <b>estreita</b> em volta do tick.
    ///
    /// <para><b>A janela precisa vir ANTES, e larga.</b> A primeira versão
    /// pegava seis ticks de cada lado do ponto de rollback, e não serviu para
    /// nada: numa divergência real o rollback foi para o tick 1016 e o contador
    /// de sorteios já diferia desde o <b>865</b> — 151 ticks antes, fora da
    /// janela inteira.</para>
    ///
    /// <para>O motivo é que "último ponto consistente" é o que a <b>digital</b>
    /// enxerga, e ela é mais grossa que o contador por tick: ela amostra em
    /// intervalos, e composição diferente com total parecido passa por ela. O
    /// ponto de rollback é onde se pode voltar com segurança, não onde a coisa
    /// começou.</para>
    ///
    /// <para>Então a janela vai 200 ticks para trás. São umas dez mil linhas —
    /// caro, mas o despejo agregado já é dessa ordem, e uma sequência que não
    /// alcança a divergência custa a corrida inteira.</para>
    ///
    /// <para>Nasceu de um tick em que o total diferia por 3 e a composição
    /// diferia por dezenas: quatro escolhas de destino a mais de um lado, duas
    /// notificações de ideologia a mais do outro, quase se cancelando. Agregado
    /// por local, isso é indistinguível de ruído; em sequência, é o índice
    /// exato.</para>
    /// </summary>
    const int TicksDeSequenciaAntes = 200;
    const int TicksDeSequenciaDepois = 8;

    static void DespejarSequencia(long tickDoAborto)
    {
        long de = tickDoAborto - TicksDeSequenciaAntes;
        long ate = tickDoAborto + TicksDeSequenciaDepois;

        var texto = new StringBuilder();
        texto.AppendLine(
            $"[WithFriends] sequência de sorteios — ticks {de} a {ate}\n" +
            "  (um sorteio por linha, na ordem em que aconteceram; o primeiro índice\n" +
            "   que diferir entre os dois lados é onde as histórias se separam)");

        for (long tick = de; tick <= ate; tick++)
        {
            var sequencia = SequenciaDe(tick);
            if (sequencia.Count == 0) continue;

            texto.AppendLine($"  tick {tick,6}  {sequencia.Count} sorteio(s)");
            for (int i = 0; i < sequencia.Count; i++)
                texto.AppendLine($"      #{i,4}  {sequencia[i]}");
        }

        foreach (var pedaco in EmPedacos(texto.ToString(), 400))
            Log.Message(pedaco);
    }

    static IEnumerable<string> EmPedacos(string texto, int linhasPorPedaco)
    {
        var linhas = texto.Split('\n');
        for (var i = 0; i < linhas.Length; i += linhasPorPedaco)
            yield return string.Join("\n", linhas.Skip(i).Take(linhasPorPedaco));
    }
}

/// <summary>
/// Os dois primitivos do <c>Rand</c>: todo o resto (<c>Range</c>, <c>Chance</c>,
/// <c>Bool</c>, <c>Element</c>…) acaba passando por um deles.
/// </summary>
[HarmonyPatch]
public static class ContadorDeSorteios
{
    /// <summary>
    /// Sorteios feitos **fora** do tick da sessão, desde o começo da visita.
    ///
    /// <para><b>A hipótese que isto testa.</b> A digital é o estado do
    /// <c>Rand</c>, e o <c>Rand</c> do RimWorld é um gerador global e único. Se
    /// alguma coisa sortear entre dois ticks — um alerta, um mote, uma janela,
    /// qualquer desenho —, o estado anda sem que a simulação tenha andado. Os
    /// dois lados então divergem sem que nada no jogo esteja diferente.</para>
    ///
    /// <para>É a suspeita mais forte para a divergência que reaparece 8 passos
    /// depois de os dois lados carregarem o mesmo save: nenhuma simulação erra
    /// tão rápido, mas duas interfaces diferentes — uma janela em foco, outra
    /// não — erram na hora.</para>
    ///
    /// <para>Custa duas leituras estáticas e uma soma, então fica sempre ligado.
    /// O rastreio por local de chamada responde <i>qual</i> sorteio; este
    /// responde <i>se é dentro ou fora do tick</i>, que é a pergunta anterior e
    /// muito mais barata.</para>
    /// </summary>
    public static long ForaDoTick { get; private set; }

    public static void Zerar() => ForaDoTick = 0;

    static void Contar()
    {
        if (!NaInterface.Tickando && RelogioDeSessaoRimWorld.EmSessao) ForaDoTick++;
    }

    [HarmonyPatch(typeof(Rand), nameof(Rand.Value), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DepoisDeValue()
    {
        Contar();
        RastreioDeRng.Registrar();
    }

    [HarmonyPatch(typeof(Rand), nameof(Rand.Int), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DepoisDeInt()
    {
        Contar();
        RastreioDeRng.Registrar();
    }
}
