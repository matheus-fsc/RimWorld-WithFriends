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
string? fonteMp = args.Length > 2 ? args[2] : null;

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

var achados = new List<(string metodo, FonteLocal fonte, string tocado, string balde, bool mp)>();
int metodos = 0;

foreach (var tipo in TodosOsTipos(assembly.MainModule))
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

        achados.Add((
            $"{tipo.FullName}.{metodo.Name}",
            fonte,
            $"{Curto(tipoDeclarante)}.{membro}",
            FontesLocais.Balde(tipo.FullName, metodo.Name),
            WithFriends.Auditor.Multiplayer.Remenda(alvosMp, $"{tipo.FullName}.{metodo.Name}")));
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
        $"interface {grupo.Count(a => a.balde == "interface"),5}" +
        (fonteMp == null ? "" : $"   MP {grupo.Count(a => a.mp),4}"));
}

if (fonteMp != null)
{
    var sim = achados.Where(a => a.balde == "simulacao").ToList();
    Console.WriteLine();
    Console.WriteLine(
        $"cruzamento: {sim.Count(a => a.mp)} de {sim.Count} achados de simulação " +
        "também são remendados pelo Multiplayer");
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
    texto.AppendLine("A separação abaixo é heurística por nome: serve para ordenar a leitura.");
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
                .Select(a => $"    {(a.mp ? "[MP]" : "    ")} {a.metodo}   →  {a.tocado}")
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
