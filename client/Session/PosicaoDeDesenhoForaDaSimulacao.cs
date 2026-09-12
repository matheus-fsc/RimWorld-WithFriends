// Adaptado de Source/Client/Patches/Determinism.cs (DrawPosPatch) de
// rwmt/Multiplayer, MIT, Copyright (c) 2018 Zetrith.
// Ver THIRD_PARTY/Multiplayer-MIT.txt

using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Dentro do tick, a posição de um pawn é a da <b>simulação</b>, não a do
/// desenho.
///
/// <para><b>Como este apareceu.</b> Não por leitura: pelo rastreio. Comparando
/// os dois diários de uma visita, o primeiro tick em que o contador de sorteios
/// da sessão diferiu foi o 6130 — um sorteio de diferença — e o rastreio por
/// local de chamada mostrou, só de um lado:</para>
///
/// <code>
/// Pawn.TickInterval
///   &lt; Pawn_HealthTracker.HealthTickInterval
///     &lt; Pawn_HealthTracker.DropBloodSmear
///       &lt; FilthMaker.TryMakeFilth
///         &lt; GenSpawn.Spawn
///           &lt; Filth.SpawnSetup
///             &lt; FloatRange.RandomInRange  →  Rand.Range
/// </code>
///
/// <para>E no jogo, a decisão de largar o rastro de sangue:</para>
///
/// <code>
/// if (pawn.Crawling &amp;&amp; pawn.Spawned)
///     if (!lastSmearDropPos.HasValue ||
///         Vector3.Distance(pawn.DrawPos, lastSmearDropPos.Value) > …)
///         DropBloodSmear();
/// </code>
///
/// <para><b><c>DrawPos</c> é posição de quadro, não de tick.</b> Ela vem de
/// <c>PawnTweener.TweenedPos</c>, interpolada com <c>RealTime.deltaTime</c> e
/// recalculada a cada quadro desenhado. Duas máquinas desenham em ritmos
/// diferentes — e uma janela em foco e outra não desenham ainda mais diferente.
/// O pawn ferrado que rasteja larga sangue em lugares diferentes nos dois lados,
/// o sangue é <c>Filth</c> (estado salvo, não enfeite), e o sorteio da espessura
/// dele desloca o gerador da sessão para sempre.</para>
///
/// <para>Isso explica o padrão inteiro que vínhamos perseguindo: divergia em
/// combate — búfalos, mecanoides, insetos, tiro em construção — e nunca
/// construindo ou movendo. Combate é o que derruba pawn, e pawn derrubado
/// rasteja e sangra. E explica por que ressincronizar não resolvia: os dois
/// lados voltavam iguais e continuavam desenhando em ritmos diferentes.</para>
///
/// <para><b>O guarda.</b> Durante o tick, <c>TweenedPos</c> devolve a posição
/// raiz — a mesma dos dois lados. Fora do tick ela continua interpolada, porque
/// aí é desenho, e desenho pode ser diferente: é justamente o que ele é.</para>
///
/// <para>Estava na lista de guardas do Multiplayer por portar desde o começo, e
/// aparecia na interseção entre "o que uma visita alcança" e "o que eles
/// remendam". Faltava a evidência de que era ele — e dessa vez ela veio inteira,
/// do rastreio dos dois lados.</para>
/// </summary>
[HarmonyPatch(typeof(PawnTweener), nameof(PawnTweener.TweenedPos), MethodType.Getter)]
public static class PosicaoDeDesenhoForaDaSimulacao
{
    /// <summary>
    /// <c>TweenedPosRoot</c> é privado. Delegate em vez de <c>Invoke</c>: isto
    /// roda dentro do tick, e reflexão no caminho quente é o que já derrubou o
    /// jogo uma vez.
    /// </summary>
    static readonly Func<PawnTweener, Vector3>? Raiz =
        AccessTools.Method(typeof(PawnTweener), "TweenedPosRoot") != null
            ? AccessTools.MethodDelegate<Func<PawnTweener, Vector3>>(
                AccessTools.Method(typeof(PawnTweener), "TweenedPosRoot"))
            : null;

    [HarmonyPostfix]
    public static void Depois(PawnTweener __instance, ref Vector3 __result)
    {
        // Só dentro do tick da sessão. Fora dele quem pergunta é o desenho, e o
        // desenho tem todo direito de ver a posição interpolada.
        if (!NaInterface.Tickando || Raiz == null) return;

        GuardasDeDeterminismo.Disparou("PawnTweener.TweenedPos (dentro do tick)");
        __result = Raiz(__instance);
    }
}
