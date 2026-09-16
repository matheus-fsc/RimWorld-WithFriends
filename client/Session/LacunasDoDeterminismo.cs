using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// O que faltava da lista de determinismo do Multiplayer, portado de uma vez.
///
/// <para><b>Por que de uma vez.</b> Cada causa que perseguimos até aqui chegou
/// pelo seu próprio unsync: uma sessão perdida, um diário de 80 MB, meia hora de
/// comparação. O Multiplayer já pagou esse preço por dezenas delas e deixou a
/// lista escrita em <c>Patches/Determinism.cs</c>. Cruzar os 54 alvos dele com
/// os nossos custou uma tarde de leitura e evita que cada um chegue pela porta
/// cara.</para>
///
/// <para><b>O que NÃO foi portado, e por quê.</b> Caravana e busca de tile são
/// do mapa-mundo, e a visita é num mapa só (§4). <c>UndercaveMapComponent</c>,
/// os coletores de bioferrita e <c>EmergeFromWater</c> são geração de mapa de
/// DLC, fora do alcance de uma visita. As duas janelas principais
/// (<c>MainTabWindow</c>) são tamanho de janela, que não é simulação. E os motes
/// já estão isolados inteiros em <see cref="EfeitosNaoDeterministicos"/>.</para>
///
/// <para><b>O arquivo de cartas ficou de fora por mudança de forma.</b> O
/// Multiplayer desempata a ordenação de <c>Archive.Add</c> remendando a
/// <b>comparação</b> — dois itens, devolve int —, o que custa nada porque a
/// ordenação já está acontecendo. No 1.6 aquilo virou um <b>seletor de
/// chave</b>: um item, devolve int, e não há onde enfiar um desempate. O
/// equivalente aqui seria reordenar a lista inteira a cada carta, numa lista que
/// só cresce durante a partida — caro, e para um efeito que é de save e de tela,
/// não de simulação. Anotado como não portado, de propósito.</para>
///
/// <para><b>Sem transpilador.</b> O Multiplayer usa transpilador em três destes.
/// Aqui a mesma coisa sai com prefixo e postfixo: é mais fácil de ler, quebra de
/// forma legível quando o jogo muda, e o catálogo de remendos consegue
/// verificar. Transpilador que erra o alvo falha em silêncio.</para>
/// </summary>
public static class LacunasDoDeterminismo
{
    /// <summary>Estamos no tick da sessão (ou aplicando comando), e não na interface?</summary>
    public static bool NaSimulacao =>
        !RngDeSessao.Ativo || NaInterface.Tickando || ComandoDeSessao.Aplicando;
}

/// <summary>
/// "Largar o trabalho prioritário" não acontece porque alguém abriu uma tela.
///
/// <para><c>PriorityWork.Clear</c> apaga o alvo do trabalho priorizado do pawn —
/// estado de simulação — e é chamada de dentro de código de interface. Do lado
/// que abriu a tela o colono esquece o que ia fazer; do outro, não.</para>
/// </summary>
[HarmonyPatch(typeof(PriorityWork), nameof(PriorityWork.Clear))]
public static class TrabalhoPrioritarioSoNoTick
{
    [HarmonyPrefix]
    public static bool Antes()
    {
        if (LacunasDoDeterminismo.NaSimulacao) return true;

        GuardasDeDeterminismo.Disparou("PriorityWork.Clear (fora do tick)");
        return false;
    }
}

/// <summary>
/// Marcar os pensamentos como sujos na interface não apaga o que já foi
/// calculado.
///
/// <para><c>Notify_SituationalThoughtsDirty</c> faz duas coisas: levanta a
/// bandeira de "sujo" e <b>esvazia o cache</b>. A bandeira é inofensiva; o
/// esvaziamento não: quem desenha uma tela força o recálculo num momento que só
/// existe de um lado, e pensamento vira humor, que vira surto.</para>
///
/// <para>Na interface, só a bandeira. O recálculo acontece no tick seguinte, nos
/// dois lados, como sempre aconteceu.</para>
/// </summary>
[HarmonyPatch(typeof(SituationalThoughtHandler),
    nameof(SituationalThoughtHandler.Notify_SituationalThoughtsDirty))]
public static class SujeiraDePensamentoSoNoTick
{
    static readonly FieldInfo? Bandeira =
        AccessTools.Field(typeof(SituationalThoughtHandler), "thoughtsDirty");

    [HarmonyPrefix]
    public static bool Antes(SituationalThoughtHandler __instance)
    {
        if (LacunasDoDeterminismo.NaSimulacao || Bandeira == null) return true;

        GuardasDeDeterminismo.Disparou("Notify_SituationalThoughtsDirty (fora do tick)");
        Bandeira.SetValue(__instance, true);
        return false;
    }
}

/// <summary>
/// Ler os pensamentos sociais numa tela não conta como tê-los consultado.
///
/// <para><c>AppendSocialThoughts</c> carimba <c>lastQueryTick</c> no cache de
/// cada par de pawns. Esse carimbo decide quando o cache expira — então uma tela
/// aberta num lado adia a expiração só nele, e o recálculo seguinte cai em ticks
/// diferentes nos dois.</para>
///
/// <para>O Multiplayer resolve com transpilador, trocando o valor gravado. Aqui:
/// guarda-se o carimbo antes e devolve-se depois, quando quem chamou foi a
/// interface. Mesmo efeito, sem reescrever IL.</para>
/// </summary>
[HarmonyPatch(typeof(SituationalThoughtHandler),
    nameof(SituationalThoughtHandler.AppendSocialThoughts))]
public static class ConsultaDePensamentoNaoCarimba
{
    static readonly FieldInfo? Cache =
        AccessTools.Field(typeof(SituationalThoughtHandler), "cachedSocialThoughts");

    static readonly FieldInfo? Carimbo =
        AccessTools.Inner(typeof(SituationalThoughtHandler), "CachedSocialThoughts") is { } tipo
            ? AccessTools.Field(tipo, "lastQueryTick")
            : null;

    static object? Entrada(SituationalThoughtHandler handler, Pawn outro)
    {
        if (Cache?.GetValue(handler) is not IDictionary mapa) return null;
        return mapa.Contains(outro) ? mapa[outro] : null;
    }

    [HarmonyPrefix]
    public static void Antes(SituationalThoughtHandler __instance, Pawn otherPawn, out int __state)
    {
        __state = int.MinValue;
        if (LacunasDoDeterminismo.NaSimulacao || Carimbo == null || otherPawn == null) return;

        if (Entrada(__instance, otherPawn) is { } entrada)
            __state = (int)Carimbo.GetValue(entrada);
    }

    [HarmonyPostfix]
    public static void Depois(SituationalThoughtHandler __instance, Pawn otherPawn, int __state)
    {
        if (__state == int.MinValue || Carimbo == null) return;

        GuardasDeDeterminismo.Disparou("AppendSocialThoughts (carimbo preservado)");

        if (Entrada(__instance, otherPawn) is { } entrada)
            Carimbo.SetValue(entrada, __state);
    }
}
