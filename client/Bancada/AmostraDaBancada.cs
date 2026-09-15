using HarmonyLib;
using Verse;
using WithFriends.Client.Session;

namespace WithFriends.Client.Bancada;

/// <summary>
/// A bancada também amostra estado de pawn, tick a tick.
///
/// <para><b>Para que.</b> Para medir <b>deriva</b>: quanto duas simulações do
/// mesmo save se afastam quando rodam livres. A ADR 0022 depende desse número —
/// ele decide se corrigir posição a 1 Hz basta ou se precisa ser mais denso —
/// e a medição que existia partia de uma divergência pequena.</para>
///
/// <para><b>Por que na bancada e não numa visita.</b> Dentro de uma visita a
/// divergência é detectada e <b>desfeita</b>: a ressincronização volta os dois
/// ao mesmo ponto em poucos ticks, que é exatamente o que se quer impedir aqui.
/// Duas bancadas do mesmo save não têm barreira, não têm digital e não têm
/// ressincronização — elas rodam livres por construção, que é a condição da
/// medida.</para>
///
/// <para>E têm um controle de graça: <b>sem</b> injetar nada, as duas deveriam
/// ficar idênticas o tempo todo. Se não ficarem, o problema é anterior à
/// pergunta, e a medição avisa em vez de mentir.</para>
///
/// <para>O gancho é <c>TickManager.DoSingleTick</c> porque numa velocidade alta
/// acontecem vários ticks por quadro: amostrar por quadro perderia a maioria
/// deles, e deriva se mede tick a tick.</para>
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
public static class AmostraDaBancada
{
    [HarmonyPrefix]
    public static void Antes()
    {
        if (!ModoAvulso.Ativo) return;
        RastreioDePawns.ComecarTick();
    }

    [HarmonyPostfix]
    public static void Depois()
    {
        if (!ModoAvulso.Ativo) return;

        var mapa = Find.CurrentMap;
        if (mapa == null) return;

        RastreioDePawns.Amostrar(Find.TickManager.TicksGame, mapa.uniqueID);
    }
}
