// Adaptado de Source/Client/EarlyPatches/SettingsPatches.cs de rwmt/Multiplayer,
// MIT, Copyright (c) 2018 Zetrith. Ver THIRD_PARTY/Multiplayer-MIT.txt

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Preferência de jogador não entra na simulação.
///
/// <para><b>Como estas apareceram.</b> Saíram da interseção entre duas listas: o
/// que o nosso auditor vê a simulação de uma visita alcançar, e o que o
/// Multiplayer já remenda. Quando as duas concordam, não é suspeita — alguém já
/// pagou com bug.</para>
///
/// <para>São todas a mesma forma. Um valor que **cada jogador escolhe no menu de
/// opções** é lido de dentro do tick e muda o que acontece. Os dois lados leem
/// valores diferentes, porque são de pessoas diferentes, e a partir daí simulam
/// coisas diferentes. Não há sorteio envolvido: é divergência por configuração,
/// e nenhum rastreio de RNG jamais a mostraria.</para>
///
/// <para>A saída é a mesma do ADR 0014 em outra escala: em vez de combinar o
/// valor, **derivar** — dentro de uma visita todo mundo usa o mesmo valor fixo, e
/// não há o que combinar. A preferência do jogador continua intacta no arquivo
/// dele e volta a valer quando a visita acaba.</para>
/// </summary>
[HarmonyPatch]
public static class PreferenciasForaDaSimulacao
{
    /// <summary>
    /// Vale desde antes da troca de partida até o fim da visita — não só
    /// enquanto se simula.
    ///
    /// <para><c>PauseOnLoad</c> é lida durante o **carregamento**, e é aí que ela
    /// importa: com a ressincronização (ADR 0019) os dois lados recarregam no
    /// meio da visita, e um lado com "pausar ao carregar" ligado voltaria parado
    /// enquanto o outro voltaria andando.</para>
    /// </summary>
    static bool NaVisita => VisitaEmAndamento.Ativa;

    /// <summary>
    /// Os getters que passam a devolver o padrão do jogo durante a visita.
    ///
    /// <para>Cancelar o getter deixa <c>__result</c> no valor padrão do tipo —
    /// <c>Never</c>, <c>false</c>, <c>0</c>. É de propósito que o padrão seja o
    /// valor "nada acontece": a preferência existe para o jogador mudar o jogo, e
    /// numa visita ninguém pode mudar o jogo sozinho.</para>
    /// </summary>
    static IEnumerable<MethodBase> TargetMethods()
    {
        // Pausa automática quando chega uma carta. Cada jogador tem a sua, e
        // pausar é mexer no relógio compartilhado a partir de dentro do tick.
        yield return AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.AutomaticPauseMode));

        // Pausar ao carregar. Ver `NaVisita`: a ressincronização recarrega.
        yield return AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.PauseOnLoad));

        // Treinamento adaptativo mexe no aprendizado dos animais — simulação.
        yield return AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.AdaptiveTrainingEnabled));

        // Quantas colônias o jogador permite. Entra em decisão de incidente, e
        // incidente acontece dentro da visita.
        yield return AccessTools.PropertyGetter(
            typeof(Prefs), nameof(Prefs.MaxNumberOfPlayerSettlements));
    }

    [HarmonyPrefix]
    public static bool Antes(MethodBase __originalMethod)
    {
        if (!NaVisita) return true;

        GuardasDeDeterminismo.Disparou($"preferência neutralizada: {__originalMethod.Name}");
        return false;
    }
}

/// <summary>
/// A lista de nomes preferidos do jogador.
///
/// <para>Ela entra em <c>PawnBioAndNameGenerator.TryGetRandomUnusedSolidName</c>,
/// que batiza pawn novo. Dois jogadores com listas diferentes geram nomes
/// diferentes — e nome de pawn não é enfeite: ele vira <c>Name</c>, que é estado
/// salvo e entra na digital.</para>
///
/// <para>Postfix devolvendo uma lista vazia, e não prefix cancelando: quem chama
/// itera o resultado sem checar nulo, e um <c>NullReferenceException</c> no meio
/// da geração de um pawn seria pior que a divergência.</para>
/// </summary>
[HarmonyPatch(typeof(Prefs), nameof(Prefs.PreferredNames), MethodType.Getter)]
public static class NomesPreferidosForaDaSimulacao
{
    static readonly List<string> Vazia = new();

    [HarmonyPostfix]
    public static void Depois(ref List<string> __result)
    {
        if (!VisitaEmAndamento.Ativa) return;

        GuardasDeDeterminismo.Disparou("nomes preferidos neutralizados");
        __result = Vazia;
    }
}
