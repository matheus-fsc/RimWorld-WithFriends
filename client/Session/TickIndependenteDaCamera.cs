using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Dentro da sessão, tudo tickia no mesmo ritmo — o da câmera de ninguém.
///
/// <para><b>O que o RimWorld 1.6 faz.</b> <c>Thing.DoTick</c> não chama
/// <c>TickInterval</c> todo tick: acumula um <c>tickDelta</c> e só chama quando
/// passa do ritmo do objeto. E o ritmo é este:</para>
///
/// <code>
/// public virtual int UpdateRateTicks => GenTicks.GetCameraUpdateRate(this);
///
/// public static int GetCameraUpdateRate(Thing thing) {
///     if (!WorldRendererUtility.DrawingMap || thing.MapHeld != Find.CurrentMap) return 15;
///     if (!Find.CameraDriver.InViewOf(thing)) return 15;
///     return (int)(Find.CameraDriver.CurrentZoom + 1);
/// }
/// </code>
///
/// <para>Ou seja: <b>a simulação anda em velocidades diferentes conforme a
/// câmera</b>. O que está na tela tickia até 15× mais que o que está fora. E
/// <c>TickInterval(delta)</c> não é enfeite — roda job giver, needs, saúde,
/// inspiração, tudo.</para>
///
/// <para><b>Por que isso quebrava a visita.</b> Dois jogadores nunca olham para
/// o mesmo canto. Medido no aborto do tick 152:</para>
///
/// <code>
/// tick 152  anfitrião: nada          visitante: 1x InspirationHandler.CheckStartRandomInspiration
/// tick 153  anfitrião: 3 sorteios    visitante: 33 sorteios em JobGiver_Wander
/// </code>
///
/// <para>O pawn do visitante estava na tela e pensou; o do anfitrião estava fora
/// e não pensou ainda. Dois ticks depois o anfitrião pensa também — e aí os dois
/// já decidiram coisas diferentes. Era exatamente a assinatura de "falhou ao
/// mover a câmera".</para>
///
/// <para><b>O preço.</b> Perde-se a otimização de tick do 1.6 durante a visita:
/// tudo tickia todo tick, como no 1.5. Numa visita entre amigos, num mapa só,
/// por alguns minutos, é barato — e é o único jeito de os dois lados simularem
/// a mesma coisa. Fora da sessão nada muda.</para>
/// </summary>
[HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate))]
public static class TickIndependenteDaCamera
{
    [HarmonyPrefix]
    public static bool Antes(Thing thing, ref int __result)
    {
        if (!RngDeSessao.Ativo) return true;

        // Devolver 1 para tudo funcionou, mas jogou fora a otimização inteira
        // do 1.6: na partida longa a 3× os dois lados travavam junto.
        //
        // O que o jogo quer com esse número é "o que o jogador está olhando
        // responde rápido; o resto pode ser grosso". Só a primeira metade
        // depende da câmera. A segunda a gente mantém — e 15 é exatamente o que
        // o vanilla já usa para tudo que está fora da tela, então nada aqui sai
        // da faixa que o jogo considera aceitável.
        //
        // Pawn fino, resto grosso: o movimento continua liso, plantas, filth e
        // construções voltam a tickar em lote, e os dois lados decidem igual
        // porque não há câmera na conta.
        __result = thing is Pawn ? 1 : GenTicks.MaxTickInterval;
        return false;
    }
}
