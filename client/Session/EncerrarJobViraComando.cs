using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace WithFriends.Client.Session;

/// <summary>
/// Encerrar um job também é ordem do jogador — e também vira comando.
///
/// <para><b>O buraco.</b> Toda ordem manual passava por
/// <c>TryTakeOrderedJob</c>, que já era comando desde cedo. Mas o "ir aqui" dos
/// alistados tem um atalho que não passa por lá:</para>
///
/// <code>
/// // FloatMenuOptionProvider_DraftedMove.PawnGotoAction
/// if (pawn.Position == gotoLoc) {
///     if (pawn.CurJobDef == JobDefOf.Goto)
///         pawn.jobs.EndCurrentJob(JobCondition.Succeeded);   // direto, na interface
/// }
/// </code>
///
/// <para>Arrastar o marcador de "ir aqui" até a célula onde o colono já está
/// encerra o <c>Goto</c> dele <b>na hora e só na máquina de quem clicou</b>. Do
/// outro lado nada acontece: o colono continua andando. A partir do tick
/// seguinte são duas simulações.</para>
///
/// <para><b>Como foi encontrado.</b> O rastreio de caminho passou a registrar a
/// pilha de quem encerra job, e a linha veio inteira:</para>
///
/// <code>
/// passo 580 #68 Fitz: job Goto encerrado: Succeeded
///   &lt; FloatMenuOptionProvider_DraftedMove.PawnGotoAction
///   &lt; MultiPawnGotoController.IssueGotoJobs
///   &lt; MultiPawnGotoController.FinalizeInteraction
///   &lt; Selector.HandleMapClicks &lt; Selector.SelectorOnGUI
///   &lt; MapInterface.HandleLowPriorityInput
/// </code>
///
/// <para>Antes disso a suspeita era o pathfinding desistindo — e não era: o
/// rastreio contou <b>zero</b> <c>PatherFailed</c> e <b>zero</b>
/// <c>PatherArrived</c> nos dois lados. O pather não decidiu nada; ele foi
/// parado pelo encerramento do job.</para>
///
/// <para><b>Por que remendar aqui e não lá.</b> ADR 0015: remendar a fonte
/// cobre os chamadores dela, inclusive os que ainda não existem. O caminho do
/// "ir aqui" é o que apareceu, mas qualquer outro botão que encerre job direto
/// tem o mesmo problema — e passa por este mesmo método.</para>
///
/// <para>A regra é a de sempre: dentro da interface, a ordem não acontece; ela
/// é proposta. Volta carimbada e acontece no mesmo tick dos dois lados.</para>
/// </summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
public static class EncerrarJobViraComando
{
    static readonly FieldInfo? CampoDoPawn = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

    [HarmonyPrefix]
    public static bool Antes(Pawn_JobTracker __instance, JobCondition condition)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;

        // O comando chegando de volta: é aqui que ele tem que acontecer.
        if (ComandoDeSessao.Aplicando) return true;

        // Job que acaba por conta própria — chegou, falhou, expirou — é
        // simulação, e simulação anda igual dos dois lados sozinha.
        if (!NaInterface.Agora) return true;

        if (CampoDoPawn?.GetValue(__instance) is not Pawn pawn) return true;

        var job = __instance.curJob;
        if (job?.def == null) return true;

        if (!PosseDePawns.EhMeu(pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(pawn);
            return false;
        }

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = ComandoDeSessao.EncerrarJob(pawn.thingIDNumber, condition, job.def.defName),
        });

        Log.Message(
            $"[WithFriends] encerrar {job.def.defName} de {pawn.LabelShort} " +
            $"({condition}) proposto como comando");

        return false;
    }
}
