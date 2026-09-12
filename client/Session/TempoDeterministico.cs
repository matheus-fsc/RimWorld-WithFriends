using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// O tempo que a simulação enxerga durante a visita é o do tick, não o da
/// máquina.
///
/// <para><b>Como estes cinco métodos foram achados.</b> Não foi reproduzindo
/// divergência — foi a auditoria de IL (ADR 0015) varrendo 89.784 métodos e
/// apontando quem toca <c>UnityEngine.Time</c> a partir de código de simulação.
/// Ver <c>docs/AUDITORIA.md</c>.</para>
///
/// <para><b>Por que não dá para remendar a fonte.</b> Seria melhor neutralizar
/// <c>UnityEngine.Time</c> e cobrir todo chamador de uma vez, como fizemos com
/// o teclado. Mas:</para>
///
/// <code>
/// public static extern float deltaTime
/// </code>
///
/// <para>Método nativo não tem corpo gerenciado, e o Harmony não alcança. Para
/// esta fonte volta-se a remendar chamadores — com a diferença de que a lista
/// agora é conhecida e se refaz sozinha a cada versão do jogo.</para>
/// </summary>
public static class TempoDeterministico
{
    /// <summary>Um tick vale isto. Substitui <c>Time.deltaTime</c>.</summary>
    public static float Delta() =>
        RngDeSessao.Ativo ? 1f / GenTicks.TicksPerRealSecond : Time.deltaTime;

    /// <summary>Substitui <c>Time.realtimeSinceStartup</c>: tempo de jogo, não de processo.</summary>
    public static float TempoReal() =>
        RngDeSessao.Ativo && Find.TickManager != null
            ? (float)Find.TickManager.TicksGame / GenTicks.TicksPerRealSecond
            : Time.realtimeSinceStartup;

    /// <summary>Substitui <c>Time.frameCount</c>: o tick serve de quadro.</summary>
    public static int Quadro() =>
        RngDeSessao.Ativo && Find.TickManager != null
            ? Find.TickManager.TicksGame
            : Time.frameCount;
}

/// <summary>
/// Troca as leituras de <c>UnityEngine.Time</c> pelos equivalentes de tick, nos
/// métodos de simulação que a auditoria apontou.
///
/// <para>Cada um, e por que importa:</para>
///
/// <list type="bullet">
/// <item><c>CompSkyfallerRandomizeDirection.CompTick</c> —
/// <c>currentOffset += … * Time.deltaTime</c>: a posição do skyfaller acumula
/// por taxa de quadros.</item>
/// <item><c>GameConditionManager.MapBrightnessTracker.Tick</c> —
/// <c>lerp += lerpSeconds / 60f * Time.deltaTime</c>, e <c>lerp</c> vai no
/// <c>ExposeData</c>: estado <b>salvo</b> acumulado por taxa de quadros.</item>
/// <item><c>CompBiosculpterPod.CanReachRequiredIngredients</c> — cache válido
/// por 2 segundos de relógio real; um lado acerta o cache e o outro recalcula.</item>
/// <item><c>CompAbilityEffect_Chunkskip.FindClosestChunks</c> — cache com chave
/// no número do quadro.</item>
/// <item><c>Building_Bed.SetBedOwnerTypeByInterface</c> — trava contra dois
/// cliques no mesmo quadro; com o tick no lugar ela continua funcionando.</item>
/// </list>
/// </summary>
[HarmonyPatch]
public static class TempoDoTickNaSimulacao
{
    static readonly Dictionary<MethodInfo, MethodInfo> Trocas = Montar();

    static Dictionary<MethodInfo, MethodInfo> Montar()
    {
        var trocas = new Dictionary<MethodInfo, MethodInfo>();

        void Trocar(string doUnity, string nosso)
        {
            var de = AccessTools.PropertyGetter(typeof(Time), doUnity);
            var para = AccessTools.Method(typeof(TempoDeterministico), nosso);
            if (de != null && para != null) trocas[de] = para;
        }

        Trocar("deltaTime", nameof(TempoDeterministico.Delta));
        Trocar("realtimeSinceStartup", nameof(TempoDeterministico.TempoReal));
        Trocar("frameCount", nameof(TempoDeterministico.Quadro));

        return trocas;
    }

    static IEnumerable<MethodBase> TargetMethods()
    {
        var alvos = new List<MethodBase?>
        {
            AccessTools.Method(typeof(CompSkyfallerRandomizeDirection), "CompTick"),
            AccessTools.Method(
                AccessTools.Inner(typeof(GameConditionManager), "MapBrightnessTracker"), "Tick"),
            AccessTools.Method(typeof(CompBiosculpterPod), "CanReachRequiredIngredients"),
            AccessTools.Method(typeof(CompAbilityEffect_Chunkskip), "FindClosestChunks"),
            AccessTools.Method(typeof(Building_Bed), "SetBedOwnerTypeByInterface"),
        };

        var presentes = new List<MethodBase>();
        foreach (var alvo in alvos)
            if (alvo != null) presentes.Add(alvo);

        Log.Message(
            $"[WithFriends] tempo de tick aplicado a {presentes.Count} de {alvos.Count} " +
            "método(s) apontados pela auditoria de IL");

        return presentes;
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Trocar(IEnumerable<CodeInstruction> instrucoes)
    {
        foreach (var instrucao in instrucoes)
        {
            if (instrucao.operand is MethodInfo chamado && Trocas.TryGetValue(chamado, out var nosso))
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, nosso)
                    .WithLabels(instrucao.labels).WithBlocks(instrucao.blocks);
            else
                yield return instrucao;
        }
    }
}
