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
    /// <summary>Ticks de rastreio guardados. Cobre a detecção, que vem depois.</summary>
    const int TicksGuardados = 400;

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
                ordem.Enqueue(tickAtual);
                while (ordem.Count > TicksGuardados) porTick.Remove(ordem.Dequeue());
            }

            var local = Capturar();
            contagens.TryGetValue(local, out var n);
            contagens[local] = n + 1;
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
    public static void Despejar(long tickDoAborto, int ticksAntes = TicksGuardados, int ticksDepois = 8)
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
            $"[WithFriends] rastreio de RNG por local de chamada — ticks {inicio} a {fim}\n" +
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
    [HarmonyPatch(typeof(Rand), nameof(Rand.Value), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DepoisDeValue() => RastreioDeRng.Registrar();

    [HarmonyPatch(typeof(Rand), nameof(Rand.Int), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DepoisDeInt() => RastreioDeRng.Registrar();
}
