using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Consultas que sorteiam: semeadas por (quem, quando), em vez de tiradas do
/// fluxo compartilhado.
///
/// <para><b>O caso que abriu esta porta.</b> A divergência mais antiga em
/// aberto, vista quatro vezes, e sempre com o mesmo rastro:</para>
///
/// <code>
/// PreceptComp_UnwillingToDo_Chance.MemberWillingToDo
///   &lt; IdeoUtility.DoerWillingToDo
///     &lt; PawnUtility.IsTeetotaler
///       &lt; JobGiver_TakeCombatEnhancingDrug.TryGiveJob
/// </code>
///
/// <para>O método sorteia <c>Rand.Value</c> <b>toda vez que é chamado</b> — é
/// assim que um preceito de ideologia com "chance de recusa" funciona. O
/// problema não é ele sortear; é <b>quem pergunta</b>:</para>
///
/// <code>
/// wf auditar --chamadores IdeoUtility::DoerWillingToDo
///   FloatMenuUtility.GetRangedAttackAction        ← menu do botão direito
///   FloatMenuUtility.GetMeleeAttackAction         ← menu do botão direito
///   ITab_Pawn_Visitor.ColonyHasAnyWardenCapable…  ← aba
///   TradeUI.DrawTradeableRow                      ← tela de comércio
///   RelationsUtility.GetRelationshipWarning       ← aviso de relação
/// </code>
///
/// <para><b>Todo clique com o botão direito num pawn alistado tirava um número
/// do fluxo da sessão</b> — só na máquina de quem clicou. Daí o "resync quase
/// imediato" numa partida de gente, e o silêncio na bancada, que não clica.</para>
///
/// <para><b>Por que semear e não isolar.</b> Isolar — desfazer o sorteio depois
/// — resolveria o fluxo e deixaria a resposta diferente a cada pergunta: o menu
/// diria "pode atacar" num quadro e "não pode" no seguinte. Semear com
/// <c>(id do pawn, tick)</c> resolve as duas coisas: o fluxo não anda, e a
/// resposta é <b>a mesma</b> para o mesmo pawn no mesmo tick, em qualquer
/// chamada e dos dois lados. É o que o Multiplayer faz (<c>Patches/Seeds.cs</c>),
/// e o raciocínio dele está no comentário original.</para>
///
/// <para><b>Sem a pilha do RNG.</b> O Multiplayer usa
/// <c>Rand.PushState</c>/<c>PopState</c>; aqui não, pelo mesmo motivo de
/// <see cref="EfeitosNaoDeterministicos"/> — aquela pilha é global, o jogo a
/// esvazia por conta própria e o desequilíbrio derrubou o jogo uma vez. Salvar e
/// escrever o estado bruto não depende de pilha nenhuma.</para>
/// </summary>
[HarmonyPatch(typeof(PreceptComp_UnwillingToDo_Chance),
    nameof(PreceptComp_UnwillingToDo_Chance.MemberWillingToDo))]
public static class VontadeDeIdeologiaSemeada
{
    [HarmonyPrefix]
    public static void Antes(HistoryEvent ev, out EfeitosNaoDeterministicos.EstadoSalvo __state)
    {
        __state = default;
        if (!RngDeSessao.Ativo) return;

        var estado = RngDeSessao.LerEstadoBruto();
        if (estado == null) return;

        __state.Guardou = true;
        __state.Valor = estado.Value;

        // Quem pergunta é o "doer" do evento. Sem ele — que não deveria
        // acontecer neste preceito — vale a semente constante do mundo, que é
        // igual nos dois lados porque vem do save.
        int quem = ev.args.TryGetArg<Pawn>(HistoryEventArgsNames.Doer, out var pawn)
            ? pawn.thingIDNumber
            : Find.World?.ConstantRandSeed ?? 0;

        Rand.Seed = Gen.HashCombineInt(quem, Find.TickManager?.TicksGame ?? 0);
    }

    /// <summary>
    /// Finalizador e não postfixo: se o método do jogo lançar, o estado do RNG
    /// tem de voltar mesmo assim — senão um erro isolado vira divergência.
    /// </summary>
    [HarmonyFinalizer]
    public static void Depois(EfeitosNaoDeterministicos.EstadoSalvo __state)
    {
        if (__state.Guardou) RngDeSessao.EscreverEstadoBruto(__state.Valor);
    }
}
