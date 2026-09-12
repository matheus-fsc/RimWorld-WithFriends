using Mono.Cecil;

namespace WithFriends.Auditor;

/// <summary>
/// O que a simulação de uma visita consegue **alcançar**.
///
/// <para><b>O problema que isto resolve.</b> O auditor achava 4.922 toques em
/// fonte local e o Multiplayer remenda 878 métodos — mas o RimWorld inteiro é
/// muito maior que uma visita. Caravana, comércio, pesquisa, o mapa do mundo:
/// nada disso acontece dentro de uma visita (ADR 0009), e cada linha dessas na
/// lista é leitura que não vira nada.</para>
///
/// <para>A separação que existia era heurística por nome — "parece simulação",
/// "parece interface". Serve para ordenar a leitura, não para cortar a lista.
/// Aqui a pergunta passa a ser respondida pelo IL: partindo do tick, quais
/// métodos o jogo consegue chamar?</para>
///
/// <para><b>Erra para mais, nunca para menos.</b> Toda dúvida vira "alcançável":
/// chamada virtual alcança todas as sobrescritas, chamada de interface alcança
/// todas as implementações, sobrecarga alcança todas as homônimas. Um método
/// marcado como inalcançável é uma afirmação forte — e é ela que autoriza tirar
/// algo da fila. O contrário seria descartar por engano justamente o caminho que
/// diverge.</para>
///
/// <para><b>O que escapa.</b> Delegate, reflexão e ponteiro de função não
/// aparecem no IL como chamada a um alvo nomeado. O RimWorld usa os três — think
/// nodes, work givers, `Action` de gizmo. Por isso os pontos de entrada abaixo
/// incluem mais que o tick: eles cobrem as portas por onde a simulação volta a
/// entrar sem que o IL mostre o caminho.</para>
/// </summary>
public sealed class Alcance
{
    /// <summary>
    /// Por onde a simulação de uma visita começa.
    ///
    /// <para>O tick é a porta principal. As outras são as portas por onde um
    /// <b>comando</b> reentra na simulação: o comando é aplicado dentro do
    /// passo, e o que ele chama é tão simulação quanto o resto.</para>
    ///
    /// <para><c>ThinkNode_Priority.TryIssueJobPackage</c> e
    /// <c>WorkGiver_Scanner.JobOnThing</c> estão aqui porque o jogo chega neles
    /// por tabela de defs, não por chamada no IL — sem citá-los, metade do
    /// comportamento de pawn ficaria de fora por um detalhe de como o RimWorld
    /// despacha, não por não acontecer numa visita.</para>
    /// </summary>
    public static readonly string[] PontosDeEntrada =
    {
        "Verse.TickManager.DoSingleTick",
        "Verse.Map.MapPreTick",
        "Verse.Map.MapPostTick",
        "Verse.World.WorldTick",
        "Verse.TickList.Tick",

        // Comando reentrando na simulação.
        "Verse.AI.Pawn_JobTracker.TryTakeOrderedJob",
        "Verse.AI.Pawn_JobTracker.TryTakeOrderedJobPrioritizedWork",
        "Verse.Designator.DesignateSingleCell",
        "Verse.Designator.DesignateThing",
        "RimWorld.Pawn_DraftController.set_Drafted",

        // Despacho por def, invisível no IL.
        "Verse.AI.ThinkNode_Priority.TryIssueJobPackage",
        "RimWorld.WorkGiver_Scanner.JobOnThing",
        "RimWorld.WorkGiver_Scanner.JobOnCell",
        "Verse.AI.JobDriver.MakeNewToils",
        "RimWorld.IncidentWorker.TryExecute",
    };

    readonly Dictionary<string, List<MethodDefinition>> porChave = new();
    readonly Dictionary<string, List<TypeDefinition>> herdeiros = new();
    readonly HashSet<string> alcancados = new();

    /// <summary>
    /// Os mesmos alcançados, em nome curto (<c>Tipo.Membro</c>, sem namespace).
    ///
    /// <para>É o formato em que os alvos do Multiplayer são extraídos — o
    /// atributo <c>[HarmonyPatch(typeof(X), "Y")]</c> não carrega namespace.
    /// Cruzar as duas listas exige falar a mesma língua, e a curta é a única que
    /// os dois lados têm.</para>
    ///
    /// <para>Nome curto colide: dois tipos homônimos em namespaces diferentes
    /// viram a mesma entrada. Erra para mais, como o resto daqui.</para>
    /// </summary>
    readonly HashSet<string> curtosAlcancados = new();

    readonly HashSet<string> tiposAlcancados = new();

    public int MetodosIndexados => porChave.Count;
    public int MetodosAlcancados => alcancados.Count;

    /// <summary>Este método (chave <c>Tipo.Nome</c>) roda dentro de uma visita?</summary>
    public bool Alcanca(string chave) => alcancados.Contains(chave);

    /// <summary>
    /// A versão curta, para cruzar com a lista do Multiplayer.
    /// <c>Tipo.*</c> quer dizer "o tipo inteiro" e vale se qualquer método dele
    /// for alcançável.
    /// </summary>
    public bool AlcancaCurto(string curto)
    {
        if (curto.EndsWith(".*"))
            return tiposAlcancados.Contains(curto.Substring(0, curto.Length - 2));

        return curtosAlcancados.Contains(curto);
    }

    public Alcance(ModuleDefinition modulo, IEnumerable<TypeDefinition> tipos)
    {
        var todos = tipos.ToList();

        foreach (var tipo in todos)
        {
            foreach (var metodo in tipo.Methods)
            {
                string chave = $"{tipo.FullName}.{metodo.Name}";
                if (!porChave.TryGetValue(chave, out var lista))
                    porChave[chave] = lista = new List<MethodDefinition>();
                lista.Add(metodo);
            }

            // Base e interfaces apontam para baixo: chegando na declaração,
            // chega-se em quem a implementa.
            if (tipo.BaseType != null) Ligar(tipo.BaseType.FullName, tipo);
            if (tipo.HasInterfaces)
                foreach (var contrato in tipo.Interfaces)
                    Ligar(contrato.InterfaceType.FullName, tipo);
        }

        foreach (string entrada in PontosDeEntrada) Visitar(entrada);

        foreach (string chave in alcancados)
        {
            int ponto = chave.LastIndexOf('.');
            if (ponto < 0) continue;

            string tipoCompleto = chave.Substring(0, ponto);
            string membro = chave.Substring(ponto + 1);

            string tipoCurto = tipoCompleto.Substring(tipoCompleto.LastIndexOf('.') + 1);

            // Tipo aninhado e closure de compilador: o que vale é o tipo de fora.
            int barra = tipoCurto.IndexOf('/');
            if (barra >= 0) tipoCurto = tipoCurto.Substring(0, barra);

            curtosAlcancados.Add($"{tipoCurto}.{membro}");
            tiposAlcancados.Add(tipoCurto);
        }
    }

    void Ligar(string baseOuContrato, TypeDefinition derivado)
    {
        if (!herdeiros.TryGetValue(baseOuContrato, out var lista))
            herdeiros[baseOuContrato] = lista = new List<TypeDefinition>();
        lista.Add(derivado);
    }

    void Visitar(string chaveRaiz)
    {
        var fila = new Queue<string>();
        if (alcancados.Add(chaveRaiz)) fila.Enqueue(chaveRaiz);

        while (fila.Count > 0)
        {
            string chave = fila.Dequeue();
            if (!porChave.TryGetValue(chave, out var definicoes)) continue;

            foreach (var metodo in definicoes)
            {
                // Uma chamada virtual pode cair em qualquer sobrescrita, e o IL
                // não diz em qual. Todas entram.
                foreach (string herdada in SobrescritasDe(metodo))
                    if (alcancados.Add(herdada)) fila.Enqueue(herdada);

                if (!metodo.HasBody) continue;

                foreach (var instrucao in metodo.Body.Instructions)
                {
                    if (instrucao.Operand is not MethodReference chamado) continue;
                    if (chamado.DeclaringType == null) continue;

                    string alvo = $"{chamado.DeclaringType.FullName}.{chamado.Name}";
                    if (!porChave.ContainsKey(alvo)) continue;   // fora do assembly
                    if (alcancados.Add(alvo)) fila.Enqueue(alvo);
                }
            }
        }
    }

    /// <summary>
    /// As chaves que uma chamada a este método pode acabar executando: ele
    /// mesmo, nas classes que herdam ou implementam o tipo dele.
    /// </summary>
    IEnumerable<string> SobrescritasDe(MethodDefinition metodo)
    {
        var tipo = metodo.DeclaringType;
        if (tipo == null) yield break;

        var pilha = new Stack<string>();
        pilha.Push(tipo.FullName);
        var vistos = new HashSet<string>();

        while (pilha.Count > 0)
        {
            string atual = pilha.Pop();
            if (!vistos.Add(atual)) continue;
            if (!herdeiros.TryGetValue(atual, out var derivados)) continue;

            foreach (var derivado in derivados)
            {
                pilha.Push(derivado.FullName);

                string chave = $"{derivado.FullName}.{metodo.Name}";
                if (porChave.ContainsKey(chave)) yield return chave;
            }
        }
    }
}
