using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Listas que o jogo embaralha <b>no lugar</b> começam cada sorteio na mesma
/// ordem dos dois lados.
///
/// <para><b>Por que zerar no começo da visita não basta.</b> Uma lista
/// embaralhada no lugar carrega a ordem do sorteio anterior. Se qualquer coisa
/// fora da simulação embaralhar — e embaralha: o jogo chama estes caminhos ao
/// desenhar, ao passar o mouse, ao abrir menu — os dois lados se separam de novo
/// no quadro seguinte, e a interface não é sincronizada por desenho (ADR 0004).
/// Zerar na carga trata o começo; isto trata o resto da visita.</para>
///
/// <para><b>Por que não enviesa.</b> Fisher–Yates sobre qualquer ordem inicial
/// dá permutação uniforme. Fixar a ordem de entrada não muda a distribuição —
/// muda só o fato de a permutação passar a ser função exclusiva do gerador, que
/// é a propriedade que a sessão precisa. O jogo continua sorteando; ele só
/// deixa de sortear a partir de um estado que ninguém combinou.</para>
///
/// <para><b>Como esta família foi encontrada.</b> Pelo sangue.
/// <c>FilthMaker.TryMakeFilth</c>, quando a célula já tem sangue que não
/// engrossa, caminha os oito vizinhos na ordem de
/// <c>GenAdj.AdjacentCells8WayRandomized()</c>. Os dois lados sorteavam as
/// mesmas sete trocas, com o contador do gerador em dia — e visitavam vizinhos
/// diferentes, porque a ordem de <b>entrada</b> era diferente. Ver
/// <see cref="EstaticosDaSessao"/> para a caçada inteira.</para>
///
/// <para>Fora de visita, nada disto roda: mod não muda jogo de quem está
/// jogando sozinho (§1).</para>
/// </summary>
static class OrdemCanonica
{
    /// <summary>Devolve a lista à ordem declarada, sem trocar a instância.</summary>
    public static void Repor(IList<IntVec3> lista, IntVec3[] canonica)
    {
        if (lista.Count != canonica.Length)
        {
            lista.Clear();
            foreach (var c in canonica) lista.Add(c);
            return;
        }

        for (int i = 0; i < canonica.Length; i++) lista[i] = canonica[i];
    }
}

/// <summary>
/// Os oito vizinhos. A lista de <c>GenAdj</c> vive o processo inteiro.
/// </summary>
[HarmonyPatch(typeof(GenAdj), nameof(GenAdj.AdjacentCells8WayRandomized))]
public static class VizinhosEmOrdemCanonica
{
    static readonly FieldInfo? Campo = AccessTools.Field(typeof(GenAdj), "adjRandomOrderList");

    [HarmonyPrefix]
    public static void Antes()
    {
        if (!VisitaEmAndamento.Ativa || Campo == null) return;

        // Lista ainda não criada: o próprio jogo a monta na ordem canônica.
        if (Campo.GetValue(null) is not List<IntVec3> lista) return;

        OrdemCanonica.Repor(lista, GenAdj.AdjacentCells);
        GuardasDeDeterminismo.Disparou("ordem canônica dos 8 vizinhos antes do sorteio");
    }
}

/// <summary>
/// Onde sentar para comer: cardeais e diagonais, dois estáticos embaralhados
/// em sequência dentro do mesmo método.
/// </summary>
[HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.TryFindAdjacentIngestionPlaceSpot))]
public static class LugarDeComerEmOrdemCanonica
{
    static readonly FieldInfo? Cardeais = AccessTools.Field(typeof(Toils_Ingest), "cardinals");
    static readonly FieldInfo? Diagonais = AccessTools.Field(typeof(Toils_Ingest), "diagonals");

    [HarmonyPrefix]
    public static void Antes()
    {
        if (!VisitaEmAndamento.Ativa) return;

        if (Cardeais?.GetValue(null) is List<IntVec3> c)
            OrdemCanonica.Repor(c, GenAdj.CardinalDirections);

        if (Diagonais?.GetValue(null) is List<IntVec3> d)
            OrdemCanonica.Repor(d, GenAdj.DiagonalDirections);

        GuardasDeDeterminismo.Disparou("ordem canônica do lugar de comer antes do sorteio");
    }
}

/// <summary>
/// As quatro rotações de uma coisa na borda de um cômodo gerado.
///
/// <para>O campo é de instância, mas a instância mora no def: vive o processo
/// inteiro como qualquer estático, e é embaralhada no lugar a cada geração.</para>
/// </summary>
[HarmonyPatch(typeof(SymbolResolver_EdgeThing), nameof(SymbolResolver_EdgeThing.Resolve))]
public static class RotacoesDeBordaEmOrdemCanonica
{
    static readonly FieldInfo? Campo =
        AccessTools.Field(typeof(SymbolResolver_EdgeThing), "randomRotations");

    [HarmonyPrefix]
    public static void Antes(SymbolResolver_EdgeThing __instance)
    {
        if (!VisitaEmAndamento.Ativa || Campo == null) return;
        if (Campo.GetValue(__instance) is not List<int> lista) return;

        if (lista.Count != 4) { lista.Clear(); lista.AddRange(new[] { 0, 1, 2, 3 }); }
        else for (int i = 0; i < 4; i++) lista[i] = i;

        GuardasDeDeterminismo.Disparou("ordem canônica das rotações de borda antes do sorteio");
    }
}

/// <summary>
/// As células de uma zona são embaralhadas <b>uma vez</b>, na primeira vez que
/// alguém pede — e quem pede primeiro pode ser a interface.
///
/// <code>
/// public List&lt;IntVec3&gt; Cells {
///     get {
///         if (!cellsShuffled) { cells.Shuffle(); cellsShuffled = true; }
///         return cells;
///     }
/// }
/// </code>
///
/// <para>O jogo desenha zonas, mostra contagem em tooltip, monta menu de
/// prioridade — tudo isso lê <c>Cells</c>. Quem passar o mouse primeiro
/// embaralha primeiro, e a ordem vale para o resto da partida: é ela que decide
/// onde o colono colhe, onde deposita, que célula da pilha é escolhida.</para>
///
/// <para>O remédio é o do Multiplayer (<c>CellsShufflePatchShared</c>):
/// <b>a interface não embaralha</b>. Ela recebe as células na ordem em que
/// estão e vai embora sem marcar nada — o embaralhamento fica para o primeiro
/// pedido de dentro da simulação, que acontece no mesmo tick dos dois lados.</para>
///
/// <para>Note que a interface receber ordem não-embaralhada não atrapalha nada:
/// desenhar e contar não dependem de ordem.</para>
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.Cells), MethodType.Getter)]
public static class CelulasDeZonaNaoEmbaralhamNaInterface
{
    [HarmonyPrefix]
    public static bool Antes(Zone __instance, ref List<IntVec3> __result)
    {
        if (!VisitaEmAndamento.Ativa || !NaInterface.Agora) return true;

        GuardasDeDeterminismo.Disparou("interface não embaralha células de zona");
        __result = __instance.cells;
        return false;
    }
}

/// <summary>O mesmo, para os planos de construção.</summary>
[HarmonyPatch(typeof(Plan), nameof(Plan.Cells), MethodType.Getter)]
public static class CelulasDePlanoNaoEmbaralhamNaInterface
{
    static readonly FieldInfo? Campo = AccessTools.Field(typeof(Plan), "cells");

    [HarmonyPrefix]
    public static bool Antes(Plan __instance, ref List<IntVec3> __result)
    {
        if (!VisitaEmAndamento.Ativa || !NaInterface.Agora) return true;
        if (Campo?.GetValue(__instance) is not List<IntVec3> celulas) return true;

        GuardasDeDeterminismo.Disparou("interface não embaralha células de plano");
        __result = celulas;
        return false;
    }
}
