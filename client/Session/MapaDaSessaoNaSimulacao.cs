using HarmonyLib;
using Verse;
using WithFriends.Client.Colony;

namespace WithFriends.Client.Session;

/// <summary>
/// Dentro do tick, o "mapa atual" é o da visita — não o que este jogador abriu.
///
/// <para><b>Como foi achado.</b> Auditoria de IL: <c>Verse.Find.CurrentMap</c>
/// aparece em 39 métodos de simulação. Entre eles, no caminho do tick:</para>
///
/// <code>
/// RimWorld.Building_VoidMonolith.Tick
/// RimWorld.PitGate.Tick
/// RimWorld.CompCameraShaker.CompTick
/// RimWorld.JobGiver_AITrashColonyClose.TryGiveJob      ← decisão de IA
/// RimWorld.IncidentWorker_AnimalInsanityMass.TryExecuteWorker
/// RimWorld.CompAnimalInsanityPulser.DoAnimalInsanityPulse
/// RimWorld.JobDriver_InstallImplant.Install
/// </code>
///
/// <para>Um <c>JobGiver</c> decidindo com base em qual mapa <b>este</b> jogador
/// está olhando é divergência esperando um dos dois abrir o mundo.</para>
///
/// <para><b>Por que <c>Game.CurrentMap</c> e não <c>Find.CurrentMap</c>.</b>
/// Porque <c>Find</c> só delega:</para>
///
/// <code>
/// public static Map CurrentMap => Current.Game?.CurrentMap;
/// </code>
///
/// <para>Remendar a delegação deixaria passar quem chama
/// <c>Current.Game.CurrentMap</c> direto. Remendar a fonte pega os dois
/// caminhos — é a ADR 0015 aplicada uma camada abaixo do que a auditoria
/// apontou.</para>
///
/// <para>Fora do tick nada muda: a interface continua vendo o mapa que o jogador
/// abriu, inclusive a vista do mundo.</para>
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Getter)]
public static class MapaDaSessaoNaSimulacao
{
    [HarmonyPrefix]
    public static bool Antes(Game __instance, ref Map? __result)
    {
        if (!RngDeSessao.Ativo || !NaInterface.Tickando) return true;

        int id = SincronizacaoComponent.Atual?.Sessao?.MapaDaVisita ?? -1;
        if (id < 0) return true;

        var mapa = __instance.Maps?.Find(m => m.uniqueID == id);
        if (mapa == null) return true;   // ainda não entrou; vale o do jogo

        __result = mapa;
        return false;
    }
}
