using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Cache não se recalcula na interface.
///
/// <para><b>A família que faltava no nosso vocabulário.</b> Depois de nomear
/// cinco tipos de fonte de divergência — cosmético, câmera, interface, thread,
/// dispositivo de entrada — ficou faltando a maior delas no Multiplayer:
/// <b>cache</b>.</para>
///
/// <para>Cache é computado <b>quando alguém olha</b>. E quem olha é a interface,
/// em momentos diferentes em cada máquina: um jogador abre a aba social e o
/// outro não, um passa o mouse num pawn e o outro não. O cálculo grava
/// resultado e carimbo de tempo — e a simulação passa a ler valores diferentes
/// nos dois lados sem ninguém ter feito nada errado.</para>
///
/// <para>O remédio é sempre o mesmo, e é o que o Multiplayer faz em toda essa
/// família: <b>dentro da interface, não recalcule</b>. Quem recalcula é o tick,
/// que acontece igual nos dois lados.</para>
/// </summary>
public static class CachesForaDaInterface
{
    /// <summary>
    /// Atalho das guardas simples: "na interface, não faça".
    ///
    /// Devolve <c>false</c> (cancela o método original) quando estamos na
    /// interface, contando o disparo.
    /// </summary>
    internal static bool DeixarPassar(string guarda)
    {
        if (!NaInterface.Agora) return true;

        GuardasDeDeterminismo.Disparou(guarda);
        return false;
    }
}

/// <summary>Riqueza não se recalcula porque alguém abriu uma aba.</summary>
[HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
public static class RiquezaSoNoTick
{
    [HarmonyPrefix]
    public static bool Antes() => CachesForaDaInterface.DeixarPassar("WealthWatcher.ForceRecount");
}

/// <summary>
/// Nível de perigo não se recalcula na interface — devolve o último calculado.
///
/// O cálculo varre coisas hostis do mapa e grava o resultado. Feito porque um
/// jogador abriu a aba errada, ele muda o estado de um lado só.
/// </summary>
[HarmonyPatch(typeof(DangerWatcher), nameof(DangerWatcher.DangerRating), MethodType.Getter)]
public static class PerigoSoNoTick
{
    static readonly System.Reflection.FieldInfo? Cache =
        AccessTools.Field(typeof(DangerWatcher), "dangerRatingInt");

    [HarmonyPrefix]
    public static bool Antes() => CachesForaDaInterface.DeixarPassar("DangerWatcher.DangerRating");

    [HarmonyPostfix]
    public static void Depois(DangerWatcher __instance, ref StoryDanger __result)
    {
        if (!NaInterface.Agora || Cache == null) return;

        // O prefixo cancelou: devolver o valor em cache, e não o default.
        if (Cache.GetValue(__instance) is StoryDanger cacheado) __result = cacheado;
    }
}

/// <summary>Lista de habilidades não se reconstrói na interface.</summary>
[HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.AllAbilitiesForReading), MethodType.Getter)]
public static class HabilidadesSoNoTick
{
    static readonly System.Reflection.FieldInfo? Cache =
        AccessTools.Field(typeof(Pawn_AbilityTracker), "allAbilitiesCached");

    [HarmonyPrefix]
    public static bool Antes() =>
        CachesForaDaInterface.DeixarPassar("Pawn_AbilityTracker.AllAbilitiesForReading");

    [HarmonyPostfix]
    public static void Depois(Pawn_AbilityTracker __instance, ref List<Ability> __result)
    {
        // Só fica nulo se o prefixo tiver cancelado.
        if (__result == null && Cache != null)
            __result = Cache.GetValue(__instance) as List<Ability>;
    }
}

/// <summary>
/// Pensamentos de humor não se recalculam na interface.
///
/// Mesmo motivo dos sociais: recalcular consulta ideologia, traços e estado, e
/// guarda o resultado.
/// </summary>
[HarmonyPatch(typeof(SituationalThoughtHandler), "UpdateAllMoodThoughts")]
public static class HumorSoNoTick
{
    [HarmonyPrefix]
    public static bool Antes() =>
        CachesForaDaInterface.DeixarPassar("SituationalThoughtHandler.UpdateAllMoodThoughts");
}

/// <summary>
/// O narrador não conta eventos que a interface provocou.
///
/// <c>Notify_PawnEvent</c> alimenta a adaptação de população — estado do
/// narrador. Disparar isso ao desenhar uma tela move o narrador de um lado só.
/// </summary>
[HarmonyPatch(typeof(StoryWatcher_PopAdaptation), nameof(StoryWatcher_PopAdaptation.Notify_PawnEvent))]
public static class NarradorSoNoTick
{
    [HarmonyPrefix]
    public static bool Antes() =>
        CachesForaDaInterface.DeixarPassar("StoryWatcher_PopAdaptation.Notify_PawnEvent");
}

/// <summary>Abate automático não marca configuração suja por causa da interface.</summary>
[HarmonyPatch(typeof(AutoSlaughterManager), nameof(AutoSlaughterManager.Notify_ConfigChanged))]
public static class AbateSoNoTick
{
    [HarmonyPrefix]
    public static bool Antes() =>
        CachesForaDaInterface.DeixarPassar("AutoSlaughterManager.Notify_ConfigChanged");
}

/// <summary>
/// "Está visível para a câmera agora" é sempre não, fora da interface.
///
/// A pergunta só faz sentido para desenhar. Respondida dentro da simulação, ela
/// é a câmera decidindo o jogo — a família que já nos custou a corrida do
/// <c>GetCameraUpdateRate</c>.
/// </summary>
[HarmonyPatch(typeof(RimWorld.Planet.WorldObjectSelectionUtility),
    nameof(RimWorld.Planet.WorldObjectSelectionUtility.VisibleToCameraNow))]
public static class VisivelParaCameraSoNaInterface
{
    [HarmonyPostfix]
    public static void Depois(ref bool __result)
    {
        if (NaInterface.Agora || !RngDeSessao.Ativo) return;
        if (!__result) return;

        GuardasDeDeterminismo.Disparou("WorldObjectSelectionUtility.VisibleToCameraNow");
        __result = false;
    }
}

/// <summary>
/// Pensamentos sociais não se recalculam por alguém estar olhando.
///
/// <para>Apareceu no nosso próprio rastreio de RNG, numa divergência de 96
/// sorteios, e eu não reparei na hora:</para>
///
/// <code>
/// 4x  Rand.Value
///   &lt; PreceptComp_UnwillingToDo_Chance.MemberWillingToDo
///   &lt; … &lt; PawnUtility.IsTeetotaler
///   &lt; ThoughtWorker_Drunk.CurrentSocialStateInternal
///   &lt; SituationalThoughtHandler.CheckRecalculateSocialThoughts
/// </code>
///
/// <para>Recalcular sorteia (a ideologia tira um número por consulta) e grava
/// <c>lastRecalculationTick</c>. Deixar isso acontecer porque um jogador abriu
/// uma aba é divergência garantida.</para>
///
/// <para>O inicializador do dicionário precisa continuar rodando — o método do
/// jogo começa por ele, e quem chama conta com a entrada existindo.</para>
/// </summary>
[HarmonyPatch(typeof(SituationalThoughtHandler), "CheckRecalculateSocialThoughts")]
public static class PensamentosSociaisSoNoTick
{
    // O dicionário e a classe do cache são privados: alcançados por reflexão,
    // como todo acoplamento interno, e catalogados.
    static readonly System.Reflection.FieldInfo? Cache =
        AccessTools.Field(typeof(SituationalThoughtHandler), "cachedSocialThoughts");

    static readonly System.Type? TipoDoCache =
        AccessTools.Inner(typeof(SituationalThoughtHandler), "CachedSocialThoughts");

    [HarmonyPrefix]
    public static bool Antes(SituationalThoughtHandler __instance, Pawn otherPawn)
    {
        if (!NaInterface.Agora) return true;
        if (otherPawn == null || Cache == null || TipoDoCache == null) return true;

        GuardasDeDeterminismo.Disparou("SituationalThoughtHandler.CheckRecalculateSocialThoughts");

        // Só a criação da entrada, sem recalcular nada — o método do jogo
        // começa por ela, e quem chama conta com a entrada existindo.
        if (Cache.GetValue(__instance) is not System.Collections.IDictionary mapa) return true;
        if (!mapa.Contains(otherPawn))
            mapa[otherPawn] = System.Activator.CreateInstance(TipoDoCache);

        return false;
    }
}

/// <summary>
/// Zona e plano só embaralham as células durante o tick.
///
/// <code>
/// public List&lt;IntVec3&gt; Cells {
///     get {
///         if (!cellsShuffled) { cells.Shuffle(); cellsShuffled = true; }
///         return cells;
///     }
/// }
/// </code>
///
/// <para>O embaralhamento é preguiçoso e <b>permanente</b>: acontece na primeira
/// leitura e fica. Se a interface de um jogador ler a zona antes do tick — para
/// desenhar, para um tooltip — ela embaralha ali, com o RNG daquele momento, e a
/// ordem das células passa a ser outra naquele lado para sempre.</para>
///
/// <para>Isto é exatamente a assinatura que medimos e não soubemos explicar:
/// <b>mesma contagem de sorteios, resultado diferente</b>. Não é o sorteio que
/// diverge — é a lista sobre a qual se sorteia.</para>
///
/// <para>Dentro da interface a leitura devolve as células como estão. Fora de
/// sessão, nada muda.</para>
/// </summary>
[HarmonyPatch]
public static class CelulasEmbaralhamSoNoTick
{
    [HarmonyPatch(typeof(Zone), nameof(Zone.Cells), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool AntesDaZona(Zone __instance, ref System.Collections.Generic.List<IntVec3> __result)
    {
        if (!NaInterface.Agora) return true;

        GuardasDeDeterminismo.Disparou("Zone.Cells (embaralhar)");
        __result = __instance.cells;
        return false;
    }

}
