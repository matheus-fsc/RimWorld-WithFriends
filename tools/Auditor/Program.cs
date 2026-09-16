using Mono.Cecil;
using Mono.Cecil.Cil;
using WithFriends.Client.Auditoria;

if (args.Length == 0)
{
    Console.Error.WriteLine("uso: Auditor <caminho do Assembly-CSharp.dll> [saída.txt]");
    return 2;
}

string dll = args[0];
string saida = args.Length > 1 ? args[1] : "auditoria.txt";
// Numa consulta pontual não há fonte do Multiplayer: o terceiro argumento é o
// campo perguntado, e tentar extrair remendos dele imprimia "0 alvos extraídos
// de Pawn_PlayerSettings::medCare", que é ruído com cara de erro.
string? fonteMp = args.Length > 2 &&
                  !args.Contains("--escritores") && !args.Contains("--chamadores")
    ? args[2] : null;

// O que o Multiplayer já remenda. Cruzar com o nosso achado separa suspeita de
// prioridade: se ele remenda, houve motivo — alguém pagou com bug.
var alvosMp = fonteMp != null ? WithFriends.Auditor.Multiplayer.Extrair(fonteMp) : new HashSet<string>();
if (fonteMp != null) Console.WriteLine($"Multiplayer: {alvosMp.Count} alvo(s) extraídos de {fonteMp}");

if (!File.Exists(dll))
{
    Console.Error.WriteLine($"não encontrei {dll}");
    return 2;
}

var relogio = System.Diagnostics.Stopwatch.StartNew();

// Sem resolver: só se lê o nome do tipo declarante de cada referência, e isso
// o Cecil dá sem carregar nada.
var leitura = new ReaderParameters { ReadSymbols = false };
using var assembly = AssemblyDefinition.ReadAssembly(dll, leitura);

var tipos = TodosOsTipos(assembly.MainModule).ToList();

// **A forma de cada membro, direto do metadado.**
//
// `wf decisoes --forma` precisa separar "herdável de graça" de "trabalho de
// verdade": um `bool` cabe numa linha do registro de `Alternar`, uma propriedade
// simples numa de `AjusteDePawn`, e um método precisa de intercepção própria.
//
// A primeira versão disso lia um assembly DECOMPILADO com expressão regular, e
// portanto dependia do `ilspycmd` instalado, de 200 MB de C# gerado e de a
// formatação do decompilador não mudar. O metadado responde a mesma pergunta
// sem nada disso, e responde certo: aqui não há "achei um método com o mesmo
// nome noutra classe".
// **"Quem escreve neste campo?"**
//
// A pergunta central da família de closures. Campo público não tem setter, e o
// que escreve nele costuma ser uma lambda com nome gerado pelo compilador —
// `<DrawResponseButton_GenerateMenu>b__0`. Sem esta consulta, descobrir onde
// remendar é ler código decompilado à mão; com ela, são dois segundos:
//
//     wf auditar --escritores Pawn_PlayerSettings::selfTend
//     → RimWorld.HealthCardUtility.DrawOverviewTab
//
// E a resposta vem completa: se dois lugares escrevem, os dois aparecem, e
// remendar só um seria divergência silenciosa.
// A irmã da de cima: "quem chama este método?". Foi ela que mostrou que
// `HealthCardUtility.CreateSurgeryBill` tem sete chamadores de menu E um de
// tick — o que decidiu remendar a fonte em vez dos chamadores, e pôr um guarda
// de interface para não transformar simulação em comando.
int ondeChamadores = Array.IndexOf(args, "--chamadores");
if (ondeChamadores >= 0 && ondeChamadores + 1 < args.Length)
{
    string alvo = args[ondeChamadores + 1];
    Console.WriteLine($"quem chama {alvo}:");

    foreach (var tipo in tipos)
    foreach (var metodo in tipo.Methods.Where(m => m.HasBody))
    foreach (var ins in metodo.Body.Instructions)
        if ((ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) &&
            ins.Operand?.ToString()?.Contains(alvo) == true)
        {
            Console.WriteLine($"   {tipo.FullName}.{metodo.Name}");
            break;
        }

    return 0;
}

int ondeEscritores = Array.IndexOf(args, "--escritores");
if (ondeEscritores >= 0 && ondeEscritores + 1 < args.Length)
{
    string alvo = args[ondeEscritores + 1];
    Console.WriteLine($"quem escreve em {alvo}:");

    foreach (var tipo in tipos)
    foreach (var metodo in tipo.Methods.Where(m => m.HasBody))
    foreach (var ins in metodo.Body.Instructions)
        if (ins.OpCode.Code == Code.Stfld &&
            ins.Operand?.ToString()?.Contains(alvo) == true)
        {
            Console.WriteLine($"   {tipo.FullName}.{metodo.Name}");
            break;
        }

    return 0;
}

if (args.Contains("--formas"))
{
    foreach (var tipo in tipos)
    {
        // **Campo e propriedade não são a mesma notícia.**
        //
        // Os dois guardam um `bool`, mas só a propriedade tem setter — e setter
        // é onde se remenda. Campo público é escrito direto, quase sempre de
        // dentro da lambda de um botão, e aí não há fonte: é o caso caro.
        //
        // A ADR 0022 contou "31 herdáveis de graça (11 bool + 20 valor)" porque
        // a classificação antiga não fazia esta distinção. Dos 29 que sobraram
        // depois da família zona/área, cinco são propriedade. O resto é campo.
        foreach (var campo in tipo.Fields.Where(f => f.IsPublic && !f.IsStatic))
            Console.WriteLine($"{tipo.Name}.{campo.Name}\tcampo");

        foreach (var prop in tipo.Properties.Where(p => p.SetMethod?.IsPublic == true))
            Console.WriteLine($"{tipo.Name}.{prop.Name}\t{Forma(prop.PropertyType)}");

        foreach (var m in tipo.Methods.Where(m => m.IsPublic && !m.IsConstructor &&
                                                  !m.IsGetter && !m.IsSetter))
            Console.WriteLine($"{tipo.Name}.{m.Name}\t{(ProduzBotao(m.Name) ? "closure" : "método")}");
    }

    return 0;
}

// Um membro que o jogador altera: `bool` vira chave, o resto vira valor.
static string Forma(TypeReference t) => t.FullName == "System.Boolean" ? "bool" : "valor";

// Métodos que DEVOLVEM botões: a decisão não está neles, está na lambda que o
// botão executa. É o caso caro — e a razão de o Multiplayer ter 227 registros
// de lambda.
static bool ProduzBotao(string nome) =>
    nome is "GetGizmos" or "GetMultiSelectFloatMenuOptions" or "CompFloatMenuOptions"
        or "ExtraFloatMenuOptions" or "CompGetGizmosExtra" or "GetFloatMenuOptionsForPawn"
        or "Inspect";

// Quem a simulação de uma visita alcança, pelo IL. A separação por nome ordena
// a leitura; esta corta a lista.
var alcance = new WithFriends.Auditor.Alcance(assembly.MainModule, tipos);
Console.WriteLine(
    $"alcance: {alcance.MetodosAlcancados} de {alcance.MetodosIndexados} método(s) " +
    $"alcançáveis a partir do tick e das portas de comando");

var achados = new List<(string metodo, FonteLocal fonte, string tocado, string balde, bool mp, bool alcanca)>();
int metodos = 0;

foreach (var tipo in tipos)
foreach (var metodo in tipo.Methods)
{
    if (!metodo.HasBody) continue;
    metodos++;

    foreach (var instrucao in metodo.Body.Instructions)
    {
        string? tipoDeclarante = null, membro = null;

        if (instrucao.Operand is MethodReference chamado)
        {
            tipoDeclarante = chamado.DeclaringType?.FullName;
            membro = chamado.Name;
        }
        else if (instrucao.Operand is FieldReference campo)
        {
            // DebugSettings.godMode e afins são CAMPO, não propriedade.
            tipoDeclarante = campo.DeclaringType?.FullName;
            membro = campo.Name;
        }
        else continue;

        var fonte = FontesLocais.Casar(tipoDeclarante, membro);
        if (fonte == null) continue;

        string chave = $"{tipo.FullName}.{metodo.Name}";
        achados.Add((
            chave,
            fonte,
            $"{Curto(tipoDeclarante)}.{membro}",
            FontesLocais.Balde(tipo.FullName, metodo.Name),
            WithFriends.Auditor.Multiplayer.Remenda(alvosMp, chave),
            alcance.Alcanca(chave)));
    }
}

relogio.Stop();
File.WriteAllText(saida, Relatorio());

Console.WriteLine(
    $"{metodos} método(s) com corpo, {achados.Count} toque(s) em fonte local, " +
    $"{relogio.Elapsed.TotalSeconds:F1}s → {saida}");

foreach (var grupo in achados.GroupBy(a => a.fonte)
             .OrderByDescending(g => g.Count(a => a.balde == "simulacao")))
{
    Console.WriteLine(
        $"  {Marca(grupo.Key.Estado)} {grupo.Key.Tipo,-28} " +
        $"simulação {grupo.Count(a => a.balde == "simulacao"),5}   " +
        $"indefinido {grupo.Count(a => a.balde == "indefinido"),5}   " +
        $"interface {grupo.Count(a => a.balde == "interface"),5}   " +
        $"NA VISITA {grupo.Count(a => a.alcanca),5}" +
        (fonteMp == null ? "" : $"   MP {grupo.Count(a => a.mp),4}"));
}

Console.WriteLine();
var naVisita = achados.Where(a => a.alcanca).Select(a => a.metodo).Distinct().Count();
Console.WriteLine(
    $"fila real: {naVisita} método(s) distintos tocam fonte local **e** rodam numa visita " +
    $"(de {achados.Select(a => a.metodo).Distinct().Count()} no jogo inteiro)");

if (fonteMp != null)
{
    var sim = achados.Where(a => a.balde == "simulacao").ToList();
    Console.WriteLine(
        $"cruzamento: {sim.Count(a => a.mp)} de {sim.Count} achados de simulação " +
        "também são remendados pelo Multiplayer");

    // A lista dele é sete anos de bug real. Cortada pelo nosso escopo, ela
    // deixa de ser "878 alvos, leia todos" e vira uma fila que acaba.
    int mpNaVisita = alvosMp.Count(alvo => alcance.AlcancaCurto(alvo));
    Console.WriteLine(
        $"Multiplayer × visita: {mpNaVisita} dos {alvosMp.Count} alvos dele " +
        "rodam dentro de uma visita nossa — é a fila herdada, já filtrada");

    File.WriteAllLines(
        Path.ChangeExtension(saida, ".fila-mp.txt"),
        alvosMp.Where(alvo => alcance.AlcancaCurto(alvo)).OrderBy(x => x, StringComparer.Ordinal));
    Console.WriteLine($"  → {Path.ChangeExtension(saida, ".fila-mp.txt")}");
}

return 0;

static IEnumerable<TypeDefinition> TodosOsTipos(ModuleDefinition modulo)
{
    foreach (var tipo in modulo.Types)
    foreach (var achatado in Achatar(tipo))
        yield return achatado;

    static IEnumerable<TypeDefinition> Achatar(TypeDefinition tipo)
    {
        yield return tipo;
        foreach (var aninhado in tipo.NestedTypes)
        foreach (var achatado in Achatar(aninhado))
            yield return achatado;
    }
}

static string Marca(Tratamento estado) => estado switch
{
    Tratamento.Fonte => "[fonte]",
    Tratamento.Chamadores => "[chama]",
    _ => "[     ]",
};

static string Curto(string? nomeCompleto) =>
    nomeCompleto?.Split('.').LastOrDefault() ?? "?";

string Relatorio()
{
    var texto = new System.Text.StringBuilder();
    texto.AppendLine("WithFriends — auditoria de fontes locais (ADR 0015)");
    texto.AppendLine();
    texto.AppendLine($"assembly   {Path.GetFileName(dll)}");
    texto.AppendLine($"métodos    {metodos} com corpo");
    texto.AppendLine($"toques     {achados.Count}");
    texto.AppendLine();
    texto.AppendLine("A pergunta aqui não é \"o que divergiu\" — é \"o que PODE divergir\".");
    texto.AppendLine("Interface pode ler câmera e teclado; é o trabalho dela. Simulação não.");
    texto.AppendLine("A separação por balde é heurística por nome: serve para ordenar a leitura.");
    texto.AppendLine("[visita] não é heurística: é alcançabilidade no IL a partir do tick e das");
    texto.AppendLine("portas de comando, errando sempre para mais. Sem a marca, o método não roda");
    texto.AppendLine("numa visita — e é isso que autoriza tirá-lo da fila.");
    texto.AppendLine();

    foreach (var grupo in achados.GroupBy(a => a.fonte)
                 .OrderByDescending(g => g.Count(a => a.balde == "simulacao")))
    {
        texto.AppendLine(new string('=', 78));
        texto.AppendLine($"FONTE  {grupo.Key}");
        texto.AppendLine($"       {grupo.Key.Motivo}");
        texto.AppendLine(grupo.Key.Estado switch
        {
            Tratamento.Fonte => "       FONTE NEUTRALIZADA no tick — cobre todo chamador, inclusive os futuros",
            Tratamento.Chamadores => "       fonte é nativa e não se remenda; CHAMADORES tratados um a um\n" +
                                     "       *** reveja esta lista a cada versão do jogo ***",
            _ => "       *** ainda não tratada ***",
        });
        texto.AppendLine();

        foreach (var balde in new[] { "simulacao", "indefinido", "interface" })
        {
            var linhas = grupo.Where(a => a.balde == balde)
                .Select(a => $"    {(a.alcanca ? "[visita]" : "        ")}" +
                             $"{(a.mp ? "[MP]" : "    ")} {a.metodo}   →  {a.tocado}")
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (linhas.Count == 0) continue;

            texto.AppendLine($"  -- {balde} ({linhas.Count})");
            foreach (var linha in linhas.Take(300)) texto.AppendLine(linha);
            if (linhas.Count > 300) texto.AppendLine($"    … e mais {linhas.Count - 300}");
            texto.AppendLine();
        }
    }

    return texto.ToString();
}
