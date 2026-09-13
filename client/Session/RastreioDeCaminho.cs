using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace WithFriends.Client.Session;

/// <summary>
/// As decisões do pathfinding, passo a passo, dos dois lados.
///
/// <para><b>Por que um rastreio só para isto.</b> Uma divergência real deixou
/// esta assinatura: três colonos alistados, indo para células vizinhas, com
/// <b>estado idêntico</b> até o passo 5063 —</para>
///
/// <code>
/// 5063  A: Rynyk pos 105,123  mov 1  custo 19.098/19.692  job Goto
/// 5063  B: Rynyk pos 105,123  mov 1  custo 19.098/19.692  job Goto
/// 5064  A: Rynyk             mov 1  custo 18.098/19.692  job Goto
/// 5064  B: Rynyk             mov 0  custo  0.000/ 1.000  job Wait_Combat
/// </code>
///
/// <para><c>custo 0.000/1.000</c> é a assinatura exata de
/// <c>Pawn_PathFollower.StopDead()</c>, e o toil de <c>Goto</c> é
/// <c>ToilCompleteMode.PatherArrival</c>: pather parado encerra o toil, o toil
/// encerra o job, e a árvore do alistado dá <c>Wait_Combat</c>. Os três
/// pararam no mesmo tick, sem chegar ao destino.</para>
///
/// <para>E o gerador <b>não</b> diverge ali: os contadores batem até o passo
/// 5070 e só separam no 5071 — sete ticks <b>depois</b> de o estado já ter
/// divergido. É decisão tomada sem sortear, a mesma assinatura do caso do
/// sangue (ADR 0020): leitura de estado que não é igual dos dois lados.</para>
///
/// <para><b>O que o jogador contou, e que fecha o quadro.</b> A ordem era para
/// um lugar impossível — e o jogo anda até onde dá e desiste. Ou seja: o caso
/// exercitado é justamente o caro, o da busca que varre o mapa inteiro e volta
/// sem caminho. O que os dois lados fizeram diferente foi <b>quando</b>
/// desistir.</para>
///
/// <para><b>Onde a decisão mora.</b> No 1.6 o pathfinding é assíncrono:</para>
///
/// <code>
/// PatherTick → curPathRequest.TryGetPath(out path)
///                ├ Found == false            → PatherFailed() → StopDead()
///                └ !path.Found               → PatherFailed()
///
/// PathRequest.Validate() → pawn.CanReach(...)  // reachability, não pathfinder
///                            └ false → found = false, pedido descartado
/// </code>
///
/// <para>Note o <c>Validate</c>: quem responde ali é a <b>alcançabilidade</b>,
/// que tem cache próprio e é consultada também pela interface — menu flutuante,
/// dica, validação de designador. Cache consultado pela interface é exatamente
/// a família de bug da ADR 0020, e é a primeira hipótese a testar.</para>
///
/// <para>Então este rastreio registra o caminho inteiro de uma decisão de
/// caminhar: quem pediu, o que o pedido respondeu, e quem desistiu. E conta,
/// por tick, quantas consultas de alcançabilidade vieram de <b>dentro</b> da
/// simulação e quantas de <b>fora</b> — se esse número diferir entre os dois
/// lados, a hipótese está confirmada sem precisar de mais nada.</para>
///
/// <para>Ligado por <c>-rastrearcaminho</c>: é instrumento de caça, não guarda.</para>
/// </summary>
public static class RastreioDeCaminho
{
    public static readonly bool Ligado =
        GenCommandLine.CommandLineArgPassed("rastrearcaminho");

    /// <summary>Consultas de alcançabilidade neste tick, por origem.</summary>
    static int naSimulacao, naInterface;

    static long Passo => Colony.SincronizacaoComponent.Atual?.Sessao.TickDeSessao ?? -1;

    public static void ContarAlcancabilidade()
    {
        if (NaInterface.Agora) naInterface++;
        else naSimulacao++;
    }

    /// <summary>
    /// Fecha a conta do tick. Só imprime quando houve consulta: numa visita
    /// parada isto seria uma linha por tick sem dizer nada.
    /// </summary>
    public static void FecharTick()
    {
        if (naSimulacao == 0 && naInterface == 0) return;

        Log.Message(
            $"[WithFriends/caminho] passo {Passo} alcançabilidade: " +
            $"{naSimulacao} na simulação, {naInterface} fora dela");

        naSimulacao = 0;
        naInterface = 0;
    }

    public static void Anotar(Pawn? pawn, string o_que)
    {
        if (pawn == null) return;
        Log.Message($"[WithFriends/caminho] passo {Passo} #{pawn.thingIDNumber} {pawn.LabelShort}: {o_que}");
    }
}

/// <summary>Quem pediu para andar, e para onde.</summary>
[HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
public static class CaminhoPedido
{
    [HarmonyPostfix]
    public static void Depois(Pawn_PathFollower __instance, LocalTargetInfo dest, PathEndMode peMode)
    {
        if (!RastreioDeCaminho.Ligado) return;

        RastreioDeCaminho.Anotar(
            PatherEspiao.PawnDe(__instance),
            $"StartPath para {dest.Cell.x},{dest.Cell.z} ({peMode}) — " +
            $"movendo {__instance.Moving}, pedido {(__instance.curPathRequest != null ? "sim" : "não")}");
    }
}

/// <summary>Quem desistiu — o fim da linha desta caçada.</summary>
[HarmonyPatch(typeof(Pawn_PathFollower), "PatherFailed")]
public static class CaminhoDesistiu
{
    [HarmonyPrefix]
    public static void Antes(Pawn_PathFollower __instance)
    {
        if (!RastreioDeCaminho.Ligado) return;

        var pedido = __instance.curPathRequest;
        RastreioDeCaminho.Anotar(
            PatherEspiao.PawnDe(__instance),
            $"PatherFailed — destino {__instance.Destination.Cell.x},{__instance.Destination.Cell.z}, " +
            $"caminho {(__instance.curPath != null ? "sim" : "não")}, " +
            $"pedido {(pedido == null ? "nenhum" : $"achou={pedido.Found?.ToString() ?? "pendente"}")}");
    }
}

/// <summary>Quem chegou. A outra saída do mesmo toil.</summary>
[HarmonyPatch(typeof(Pawn_PathFollower), "PatherArrived")]
public static class CaminhoChegou
{
    [HarmonyPrefix]
    public static void Antes(Pawn_PathFollower __instance)
    {
        if (!RastreioDeCaminho.Ligado) return;
        RastreioDeCaminho.Anotar(PatherEspiao.PawnDe(__instance), "PatherArrived");
    }
}

/// <summary>
/// O veredito do pedido: achou caminho, não achou, ou foi recusado ainda na
/// validação — que é onde a alcançabilidade responde.
/// </summary>
[HarmonyPatch(typeof(PathRequest), nameof(PathRequest.Validate))]
public static class PedidoValidado
{
    [HarmonyPostfix]
    public static void Depois(PathRequest __instance, bool __result)
    {
        if (!RastreioDeCaminho.Ligado || __result) return;

        RastreioDeCaminho.Anotar(
            __instance.pawn,
            $"pedido RECUSADO na validação — destino " +
            $"{__instance.Target.Cell.x},{__instance.Target.Cell.z} " +
            $"(a alcançabilidade disse que não dá)");
    }
}

[HarmonyPatch(typeof(PathRequest), nameof(PathRequest.Resolve))]
public static class PedidoResolvido
{
    [HarmonyPostfix]
    public static void Depois(PathRequest __instance, PawnPath p)
    {
        if (!RastreioDeCaminho.Ligado) return;

        RastreioDeCaminho.Anotar(
            __instance.pawn,
            $"pedido resolvido: {(p != null && p.Found ? $"caminho de {p.NodesLeftCount} nó(s)" : "SEM CAMINHO")} " +
            $"— destino {__instance.Target.Cell.x},{__instance.Target.Cell.z}");
    }
}

/// <summary>
/// Quantas consultas de alcançabilidade este tick teve, e quantas vieram de
/// fora da simulação.
/// </summary>
[HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
public static class AlcancabilidadeContada
{
    [HarmonyPrefix]
    public static void Antes()
    {
        if (!RastreioDeCaminho.Ligado) return;
        RastreioDeCaminho.ContarAlcancabilidade();
    }
}

/// <summary>Fecha a conta do tick junto com o relógio da sessão.</summary>
[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PathFinderTick))]
public static class ContaDoTickFechada
{
    [HarmonyPostfix]
    public static void Depois()
    {
        if (!RastreioDeCaminho.Ligado) return;
        RastreioDeCaminho.FecharTick();
    }
}

/// <summary>
/// O <c>pawn</c> do seguidor de caminho é protegido. Alcançado por reflexão,
/// uma vez, e catalogado como todo acoplamento interno.
/// </summary>
static class PatherEspiao
{
    static readonly FieldInfo? Campo = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");

    public static Pawn? PawnDe(Pawn_PathFollower seguidor) => Campo?.GetValue(seguidor) as Pawn;
}
