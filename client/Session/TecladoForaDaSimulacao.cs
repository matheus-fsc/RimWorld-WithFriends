using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Dentro do tick, o teclado de ninguém existe.
///
/// <para><b>O achado.</b> <c>Pawn_JobTracker.TryTakeOrderedJob</c> não confia só
/// no parâmetro — ele lê o teclado <b>na hora</b>:</para>
///
/// <code>
/// public bool TryTakeOrderedJob(Job job, JobTag? tag = JobTag.Misc, bool requestQueueing = false) {
///     ...
///     bool isDownEvent = KeyBindingDefOf.QueueOrder.IsDownEvent;   // o teclado DESTA máquina
///     isDownEvent = isDownEvent || requestQueueing;
///     if (isDownEvent) { jobQueue.EnqueueLast(job, tag); return true; }   // empilha
///     ClearQueuedJobs();                                                  // ou substitui
/// </code>
///
/// <para>Empilhar ou substituir depende de <b>quem está com Shift pressionado</b>.
/// Quando o comando é aplicado, quem clicou ainda está com Shift; o outro
/// jogador não está com nada. Mesmo comando, dois efeitos.</para>
///
/// <para>Medido pelo rastreio de estado dos pawns, que foi feito exatamente
/// para isto:</para>
///
/// <code>
/// J1  #510 Sappy  pos 126,137  dest 124,136  fila 4  draft 1
/// J2  #510 Sappy  pos 126,137  dest 127,144  fila 0  draft 1
/// </code>
///
/// <para>Fila crescendo de um lado, sempre zero do outro, e o destino do outro
/// pulando para cada clique novo. O rastreio de RNG nunca acharia isto: empilhar
/// não sorteia nada, e o que ele mostrava era só o pawn entrando numa célula um
/// tick antes.</para>
///
/// <para><b>A regra.</b> Teclado e mouse são de um jogador. Simulação é dos
/// dois. Durante o tick, toda consulta de tecla responde "não pressionada", e a
/// intenção real viaja no comando — onde ela é lida uma vez, na máquina de quem
/// clicou (ver <c>OrdemViraComando</c>).</para>
/// </summary>
[HarmonyPatch]
public static class TecladoForaDaSimulacao
{
    /// <summary>As quatro formas de perguntar "esta tecla está apertada?".</summary>
    static readonly string[] Consultas = { "KeyDownEvent", "IsDownEvent", "JustPressed", "IsDown" };

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var nome in Consultas)
        {
            var getter = AccessTools.PropertyGetter(typeof(KeyBindingDef), nome);
            if (getter != null) yield return getter;
        }
    }

    [HarmonyPrefix]
    public static bool Antes(ref bool __result)
    {
        if (!RngDeSessao.Ativo || !NaInterface.Tickando) return true;

        __result = false;
        return false;
    }
}
