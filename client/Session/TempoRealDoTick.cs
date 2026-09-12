using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Durante o tick, <c>Verse.RealTime</c> conta ticks, não quadros.
///
/// <para><b>O que é.</b> Um cache que o jogo mantém de
/// <c>UnityEngine.Time</c>:</para>
///
/// <code>
/// public static void Update() {
///     frameCount = Time.frameCount;
///     deltaTime  = Time.deltaTime;
///     …
/// }
/// </code>
///
/// <para>Mesmo relógio, mesmo contador de quadros — um nome a menos. A nossa
/// regra de auditoria vigiava <c>UnityEngine.Time</c> e não o embrulho, então
/// 57 leituras passavam despercebidas. Foi assim que
/// <c>Pawn_JobTracker.StartJob</c> lendo <c>RealTime.frameCount</c> escapou
/// (o Multiplayer remenda esse mesmo método).</para>
///
/// <para><b>Por que aqui dá para tratar a fonte.</b> São <b>campos estáticos
/// públicos</b>, não propriedades nativas — dá para escrever neles. Em vez de
/// remendar leitores, trocamos os valores em volta do tick e devolvemos depois,
/// exatamente como já é feito com o estado do RNG (ADR 0009). Um lugar, e todos
/// os 57 leitores ficam determinísticos, inclusive os que uma versão futura do
/// jogo trouxer.</para>
///
/// <para>Fora do tick nada muda: motes, animação e interface continuam vendo o
/// tempo real da máquina, que é o que eles precisam ver.</para>
/// </summary>
public static class TempoRealDoTick
{
    static readonly FieldInfo? UltimoTempoReal = AccessTools.Field(typeof(RealTime), "lastRealTime");
    static readonly FieldInfo? TempoSemPausa = AccessTools.Field(typeof(RealTime), "unpausedTime");

    static float deltaGuardado;
    static float deltaRealGuardado;
    static int quadroGuardado;
    static float ultimoGuardado;
    static float semPausaGuardado;
    static bool trocado;

    public static void AntesDoTick(long tickDeJogo)
    {
        if (trocado) return;

        deltaGuardado = RealTime.deltaTime;
        deltaRealGuardado = RealTime.realDeltaTime;
        quadroGuardado = RealTime.frameCount;
        ultimoGuardado = Ler(UltimoTempoReal);
        semPausaGuardado = Ler(TempoSemPausa);

        float segundos = (float)tickDeJogo / GenTicks.TicksPerRealSecond;
        RealTime.deltaTime = 1f / GenTicks.TicksPerRealSecond;
        RealTime.realDeltaTime = 1f / GenTicks.TicksPerRealSecond;
        RealTime.frameCount = (int)tickDeJogo;
        Escrever(UltimoTempoReal, segundos);
        Escrever(TempoSemPausa, segundos);

        trocado = true;
    }

    public static void DepoisDoTick()
    {
        if (!trocado) return;

        RealTime.deltaTime = deltaGuardado;
        RealTime.realDeltaTime = deltaRealGuardado;
        RealTime.frameCount = quadroGuardado;
        Escrever(UltimoTempoReal, ultimoGuardado);
        Escrever(TempoSemPausa, semPausaGuardado);

        trocado = false;
    }

    static float Ler(FieldInfo? campo)
    {
        try { return campo == null ? 0f : (float)campo.GetValue(null)!; }
        catch (Exception) { return 0f; }
    }

    static void Escrever(FieldInfo? campo, float valor)
    {
        try { campo?.SetValue(null, valor); }
        catch (Exception) { /* o catálogo avisa na subida */ }
    }
}
