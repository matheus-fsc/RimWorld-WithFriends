using System.Reflection;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Durante a visita, o pathfinding do 1.6 não atravessa o tick.
///
/// <para><b>O que o jogo faz.</b> <c>PathFinder.PathFinderTick</c> agenda o
/// trabalho de busca de caminho como Unity Jobs em threads de trabalho e
/// <b>não espera</b>: quem completa é o <c>ForceCompleteScheduledJobs()</c> do
/// tick <b>seguinte</b>.</para>
///
/// <code>
/// public void PathFinderTick() {
///     ForceCompleteScheduledJobs();     // colhe o que foi agendado no tick anterior
///     ...
///     ScheduleBatchedPathJobs(lastGridHandle);   // agenda e segue a vida
/// }
/// </code>
///
/// <para><b>Por que isso quebra lockstep.</b> Entre agendar e colher, os jobs
/// rodam <b>ao mesmo tempo</b> que a thread principal tickando os pawns — e
/// leem <c>pawn.Position</c> e estado de porta enquanto
/// <c>Pawn_PathFollower.PatherTick</c> escreve <c>pawn.Position = nextCell</c>.
/// O que cada job enxerga depende do escalonamento de threads, que não é igual
/// em duas máquinas nem em duas execuções. Caminho diferente → célula seguinte
/// diferente → o pawn entra na célula num tick diferente.</para>
///
/// <para>A leitura é do Multiplayer (<c>Patches/PathFinderPatch.cs</c>, MIT), que
/// descreve a corrida em detalhe. Lá a correção proposta é tirar uma foto das
/// posições na thread principal e fazer os jobs lerem a foto — mais fiel ao
/// desenho do jogo, e mais código; está desligada no fonte deles.</para>
///
/// <para><b>O que fazemos.</b> Completar os jobs ainda dentro do
/// <c>PathFinderTick</c>, antes de qualquer pawn tickar. Nada muta enquanto
/// eles rodam, então o resultado volta a ser função do estado — igual nos dois
/// lados. O preço é perder a sobreposição: a busca de caminho deixa de correr
/// junto com o tick e passa a custar dentro dele.</para>
///
/// <para>Foi medido assim: <c>Notify_EnteredNewCell</c> (3 sorteios, jogar
/// sujeira ao entrar numa célula) aparecendo no tick 8030 de um lado e no 8031
/// do outro, repetidamente, sempre com pawns em movimento. Três abortos
/// anteriores tinham a mesma assinatura de ~4 sorteios e ficaram sem
/// explicação.</para>
/// </summary>
[HarmonyPatch]
public static class PathfindingSemCorrida
{
    static readonly MethodInfo? Completar =
        AccessTools.Method(typeof(PathFinder), "ForceCompleteScheduledJobs");

    public static bool Disponivel => Completar != null;

    static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(PathFinder), nameof(PathFinder.PathFinderTick));

    [HarmonyPostfix]
    public static void Depois(PathFinder __instance)
    {
        if (!RngDeSessao.Ativo || Completar == null) return;

        Completar.Invoke(__instance, null);
    }
}
