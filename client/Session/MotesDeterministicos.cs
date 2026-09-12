using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Efeitos visuais não podem depender de para onde o jogador está olhando.
///
/// O jogo decide se cria um mote assim:
///
/// <code>
/// if (map != Find.CurrentMap) return false;          // mapa que o jogador vê
/// if (!loc.InBounds(map)) return false;
/// if (drawOffscreen) return true;
/// viewRect = Find.CameraDriver.CurrentViewRect;      // posição da CÂMERA
/// return viewRect.ExpandedBy(5).Contains(loc);
/// </code>
///
/// As duas primeiras condições e a última dependem de **estado de visão do
/// jogador**, não da simulação. E criar um mote sorteia números (velocidade,
/// rotação, deslocamento). Ou seja: dois jogadores olhando para lugares
/// diferentes do mesmo mapa consomem aleatoriedade diferente — e divergem sem
/// que nada no jogo tenha acontecido de diferente.
///
/// Medido: 3 sorteios de diferença num único tick, 43 ticks depois do último
/// comando, com os passos idênticos antes e depois. A assinatura de um efeito
/// que nasceu de um lado só.
///
/// Dentro da sessão a resposta passa a depender **apenas do mapa e da célula**.
/// Os dois lados criam os mesmos motes, sorteiam os mesmos números, e quem
/// está olhando para onde deixa de importar.
/// </summary>
[HarmonyPatch(typeof(GenView), nameof(GenView.ShouldSpawnMotesAt), typeof(IntVec3), typeof(Map), typeof(bool))]
public static class MotesDeterministicos
{
    [HarmonyPrefix]
    public static bool Antes(IntVec3 loc, Map map, ref bool __result)
    {
        if (!RngDeSessao.Ativo) return true;

        __result = map != null && loc.InBounds(map);
        return false;
    }
}
