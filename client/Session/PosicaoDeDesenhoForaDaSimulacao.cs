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


/// <summary>
/// A inclinação para fora da cobertura também é desenho — e ela entra na
/// <b>origem de cada tiro</b>.
///
/// <para><b>Como apareceu.</b> Uma visita divergiu com a mesma bala saindo de
/// lugares diferentes, 77 ticks antes de a digital acusar:</para>
///
/// <code>
/// =96439 Bullet_Shotgun  A: origem 112.50, 93.50   ← centro exato da célula
///                        B: origem 112.59, 93.45
/// </code>
///
/// <para>Mesmo atirador, mesmo alvo, mesmos ticks até o impacto. O que difere é
/// de onde a bala nasceu — e <c>Verb_LaunchProjectile.TryCastShot</c> passa
/// <c>caster.DrawPos</c> como origem do projétil. Posição de desenho, dentro da
/// simulação.</para>
///
/// <para><b>Por que o guarda de cima não bastou.</b> Ele devolve
/// <c>TweenedPosRoot()</c> dentro do tick, e no 1.6 esse "root" <b>não é
/// puro</b>: ele soma <c>PawnCollisionTweenerUtility.PawnCollisionPosOffsetFor</c>,
/// que por sua vez soma <c>Drawer.leaner.LeanOffset</c> — a inclinação de quem
/// atira de trás de uma parede, interpolada quadro a quadro por
/// <c>ProcessPostTickVisuals</c>. Tirou-se o tween e deixou-se a inclinação.</para>
///
/// <para>O deslocamento medido — 0,087 e 0,049 de célula — é exatamente a escala
/// de uma inclinação, não a de um passo.</para>
///
/// <para><b>O guarda.</b> Dentro do tick, inclinação é zero: a bala sai de onde
/// o pawn está, não de onde ele parece estar. Fora do tick nada muda, e o pawn
/// continua se inclinando na tela dos dois jogadores como sempre — cada um no
/// ritmo do seu quadro, que é o que desenho é.</para>
/// </summary>
[HarmonyPatch(typeof(PawnLeaner), nameof(PawnLeaner.LeanOffset), MethodType.Getter)]
public static class InclinacaoForaDaSimulacao
{
    [HarmonyPostfix]
    public static void Depois(ref Vector3 __result)
    {
        if (!NaInterface.Tickando) return;

        GuardasDeDeterminismo.Disparou("PawnLeaner.LeanOffset (dentro do tick)");
        __result = Vector3.zero;
    }
}
