using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Ferramentas de desenvolvimento ficam de fora enquanto a visita dura.
///
/// <para><b>Por quê.</b> Uma ação de debug acontece **fora do fluxo de tick**,
/// no lado de quem clicou, e não passa por comando nenhum. Spawnar uma coisa na
/// colônia do anfitrião cria um objeto que só existe de um lado. A partir daí a
/// simulação é outra: medido num teste proposital, a divergência apareceu em
/// <c>WildPlantSpawner</c> — a densidade de plantas do mapa mudou porque o mapa
/// mudou.</para>
///
/// <para><b>O aborto estava certo.</b> Foram 4472 ticks de lockstep limpo antes
/// disso, e a integridade fez o trabalho dela: detectou, abortou, devolveu as
/// duas colônias. O que faltava era a mensagem dizer **o que** o jogador fez —
/// em vez de dois hashes diferentes.</para>
///
/// <para><b>Por que bloquear em vez de sincronizar.</b> O Multiplayer
/// sincroniza ferramentas de debug, e gasta ~640 linhas (<c>Debug/DebugSync.cs</c>
/// + <c>Debug/DebugPatches.cs</c>) para replicar o clique do outro lado. Cabe
/// fazer depois, mas não antes de a visita ser sólida sem isso — integridade
/// antes de recurso. Bloquear custa este arquivo.</para>
///
/// <para>Fora de visita, nada muda: a colônia é sua e o dev mode é seu.</para>
/// </summary>
[HarmonyPatch(typeof(DebugActionNode), nameof(DebugActionNode.Enter))]
public static class FerramentasDeDevNaVisita
{
    /// <summary>Categoria das ações deste mod, que continuam liberadas.</summary>
    const string Categoria = "WithFriends";

    [HarmonyPrefix]
    public static bool Antes(DebugActionNode __instance)
    {
        if (!RngDeSessao.Ativo) return true;
        // Navegar pelo menu é inofensivo; só executar é que muda o mundo.
        if (__instance.actionType == DebugActionType.Action && __instance.action == null) return true;
        // As ações deste mod existem justamente para operar a sessão —
        // encerrar a visita, ligar o rastreio de RNG. Bloqueá-las trancaria o
        // jogador do lado de fora da própria ferramenta de diagnóstico.
        if (__instance.category == Categoria || __instance.sourceAttribute?.category == Categoria) return true;

        Messages.Message(
            $"With Friends: \"{__instance.label}\" não roda durante uma visita. " +
            "Ações de debug acontecem só do seu lado e separam as duas simulações.",
            MessageTypeDefOf.RejectInput, historical: false);

        Log.Message($"[WithFriends] ação de debug recusada durante a sessão: {__instance.label}");
        return false;
    }
}
