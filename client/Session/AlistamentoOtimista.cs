using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// O botão de alistar responde na hora; a simulação continua esperando o tick.
///
/// <para><b>O problema.</b> O gizmo de alistar é um alternador que lê
/// <c>drafter.Drafted</c>. Numa visita, clicar não muda nada de imediato — vira
/// comando, e o valor só muda quando ele volta carimbado. O botão continua
/// mostrando o estado antigo, o jogador acha que falhou e clica de novo:</para>
///
/// <code>
/// Engie: desalistar proposto como comando      ← clique 1
/// Engie: desalistar proposto como comando      ← clique 2, mesmo pedido
/// comando 19 … Engie desalistado
/// </code>
///
/// <para>Os dois cliques pedem a mesma coisa (o alternador recalcula sobre o
/// mesmo estado), então nada quebra — mas a sensação é de botão que precisa de
/// dois cliques.</para>
///
/// <para><b>A correção, e o seu limite.</b> Dentro da interface,
/// <c>Drafted</c> passa a responder o valor pedido. Dentro do tick, responde o
/// valor real. É <see cref="NaInterface"/> usado ao contrário do habitual: até
/// agora ele servia para impedir a interface de mexer na simulação; aqui serve
/// para deixar a interface mentir <b>só para si mesma</b>.</para>
///
/// <para>A simulação nunca vê o palpite. Se o comando não voltar, o palpite
/// expira e o botão volta a contar a verdade.</para>
/// </summary>
public static class AlistamentoOtimista
{
    /// <summary>
    /// Depois disto, o palpite é esquecido.
    ///
    /// Comando que não voltou em dois segundos não vai voltar, e botão que
    /// mente para sempre é pior que botão lento.
    /// </summary>
    const float SegundosDeValidade = 2f;

    static readonly Dictionary<int, (bool alistado, float quando)> pedidos = new();

    public static void Pedir(int pawnId, bool alistado)
    {
        pedidos[pawnId] = (alistado, Time.realtimeSinceStartup);
        Log.Message($"[WithFriends/ui] palpite: pawn {pawnId} → {(alistado ? "alistado" : "livre")}");
    }

    /// <summary>
    /// O comando chegou. Só encerra o palpite se ele for o palpite **deste**
    /// pedido.
    ///
    /// <para>Antes qualquer comando daquele pawn encerrava o palpite, e com
    /// dois cliques rápidos isso invertia o botão na cara do jogador: o
    /// comando do primeiro clique chegava, apagava o palpite do segundo, e o
    /// botão saltava para o valor antigo até o segundo comando chegar.</para>
    ///
    /// <para>Era o "às vezes ativa e desativa" — e não era instabilidade, era
    /// ordem: comando antigo respondendo a uma intenção que já tinha sido
    /// substituída.</para>
    /// </summary>
    public static void Confirmar(int pawnId, bool valorAplicado)
    {
        if (!pedidos.TryGetValue(pawnId, out var pedido)) return;

        if (pedido.alistado != valorAplicado)
        {
            Log.Message(
                $"[WithFriends/ui] comando antigo ({(valorAplicado ? "alistado" : "livre")}) " +
                $"não encerra o palpite de pawn {pawnId} " +
                $"(esperando {(pedido.alistado ? "alistado" : "livre")})");
            return;
        }

        pedidos.Remove(pawnId);
        Log.Message($"[WithFriends/ui] palpite confirmado: pawn {pawnId}");
    }

    public static void Limpar() => pedidos.Clear();

    public static bool TemPedido(int pawnId, out bool alistado)
    {
        alistado = false;
        if (!pedidos.TryGetValue(pawnId, out var pedido)) return false;

        if (Time.realtimeSinceStartup - pedido.quando > SegundosDeValidade)
        {
            pedidos.Remove(pawnId);
            Log.Message(
                $"[WithFriends/ui] palpite EXPIROU sem o comando voltar: pawn {pawnId} " +
                $"(queria {(pedido.alistado ? "alistado" : "livre")}) — o botão volta ao valor real");
            return false;
        }

        alistado = pedido.alistado;
        return true;
    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Getter)]
public static class AlistamentoOtimistaNaInterface
{
    [HarmonyPostfix]
    public static void Depois(Pawn_DraftController __instance, ref bool __result)
    {
        // Só na interface. Dentro do tick a simulação enxerga o valor de
        // verdade, que é o que os dois lados compartilham.
        if (!NaInterface.Agora) return;
        if (__instance.pawn == null) return;

        if (AlistamentoOtimista.TemPedido(__instance.pawn.thingIDNumber, out bool alistado))
            __result = alistado;
    }
}
