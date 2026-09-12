// Adaptado de Source/Client/Patches/Determinism.cs (DrawTrackerTickPatch,
// FloodUnfogPatch) de rwmt/Multiplayer, MIT, Copyright (c) 2018 Zetrith.
// Ver THIRD_PARTY/Multiplayer-MIT.txt

using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// O que o tick faz não depende do que está na tela.
///
/// <para><b>O ponto cego.</b> <c>Pawn.ProcessPostTickVisuals</c> é chamado de
/// dentro do tick e decide assim:</para>
///
/// <code>
/// if (Current.ProgramState != ProgramState.Playing || viewRect.Contains(Position))
///     Drawer.ProcessPostTickVisuals(ticksPassed);
/// </code>
///
/// <para><c>viewRect</c> é o retângulo <b>visível da câmera</b>. Dois jogadores
/// olham para lugares diferentes, então o mesmo pawn tem os visuais processados
/// de um lado e não do outro — dentro do tick, não no desenho.</para>
///
/// <para>E isso não fica nos visuais. <c>Pawn_DrawTracker.ProcessPostTickVisuals</c>
/// adianta o <i>tweener</i> — a mesma posição interpolada que
/// <c>DropBloodSmear</c> lê para decidir onde largar sangue — e dispara pegadas,
/// que são <c>Filth</c>, que é estado salvo. É a mesma família do rastro de
/// sangue, um degrau acima: lá o <b>leitor</b> da posição de desenho era local;
/// aqui é o que a <b>faz andar</b>.</para>
///
/// <para><b>Como o Multiplayer trata.</b> Transpila o método para que o teste
/// <c>viewRect.Contains(…)</c> seja seguido de <c>ldc.i4.1; or</c> — ou seja,
/// sempre verdadeiro. Aqui o mesmo efeito sai reescrevendo o parâmetro, que é o
/// retângulo em si: dentro do tick ele passa a ser o mapa inteiro. Menos frágil
/// que mexer em IL, e diz a intenção em vez de codificá-la.</para>
///
/// <para>Fora do tick, o retângulo continua sendo o da câmera — ali é desenho, e
/// desenhar só o que se vê é o trabalho dele.</para>
/// </summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.ProcessPostTickVisuals))]
public static class VisuaisDeTickForaDaCamera
{
    [HarmonyPrefix]
    public static void Antes(Pawn __instance, ref CellRect viewRect)
    {
        if (!NaInterface.Tickando) return;

        var mapa = __instance.Map;
        if (mapa == null) return;

        GuardasDeDeterminismo.Disparou("ProcessPostTickVisuals (retângulo da câmera)");
        viewRect = CellRect.WholeMap(mapa);
    }
}

/// <summary>
/// Desnevoar não pode perguntar o que está na tela.
///
/// <para><c>FloodUnfogResult.allOnScreen</c> diz se tudo o que acabou de sair da
/// névoa está visível — e quem chama usa isso para decidir se manda uma mensagem
/// e, dependendo da versão, se faz mais trabalho. Dois jogadores olhando para
/// lugares diferentes recebem respostas diferentes.</para>
///
/// <para>Fixo em <c>false</c> dentro do tick, que é o valor "não sei, trate como
/// fora da tela" — o mesmo dos dois lados. É o que o Multiplayer faz, e ali vale
/// sempre que há sessão.</para>
/// </summary>
[HarmonyPatch(typeof(FloodFillerFog), nameof(FloodFillerFog.FloodUnfog))]
public static class DesnevoarForaDaCamera
{
    [HarmonyPostfix]
    public static void Depois(ref FloodUnfogResult __result)
    {
        if (!RelogioDeSessaoRimWorld.EmSessao || !__result.allOnScreen) return;

        GuardasDeDeterminismo.Disparou("FloodUnfog.allOnScreen");
        __result.allOnScreen = false;
    }
}

/// <summary>
/// O multiplicador de velocidade é tabela, não avaliação local.
///
/// <para>O vanilla decide assim em Superfast:</para>
///
/// <code>
/// case TimeSpeed.Superfast:
///     if (Find.Maps.Count == 0) return 18f;
///     if (NothingHappeningInGame()) return 12f;
///     return 6f;
/// </code>
///
/// <para><c>NothingHappeningInGame()</c> é avaliação <b>desta</b> máquina, com
/// cache próprio. Dois lados podem discordar sobre "nada acontecendo" e rodar a
/// 12× e 6× — e o multiplicador entra na interpolação do <i>tweener</i> e no
/// agrupamento de ticks, que é justamente o que já mordeu em combate acelerado.</para>
///
/// <para>O Multiplayer troca o getter inteiro por uma tabela fixa. Aqui é o
/// mesmo, com um detalhe: <c>ForcedNormalSpeed</c> continua valendo, porque ele
/// é comparação de <b>tick</b> (<c>TicksGame &lt; forceNormalSpeedUntil</c>) e
/// portanto igual dos dois lados — desde que o que o dispara seja simulação, que
/// é o caso depois de as preferências saírem do caminho.</para>
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickRateMultiplier), MethodType.Getter)]
public static class RitmoDeTickSemAvaliacaoLocal
{
    [HarmonyPrefix]
    public static bool Antes(TickManager __instance, ref float __result)
    {
        if (!RelogioDeSessaoRimWorld.EmSessao) return true;

        GuardasDeDeterminismo.Disparou("TickRateMultiplier (tabela fixa)");

        if (__instance.CurTimeSpeed == TimeSpeed.Paused) { __result = 0f; return false; }
        if (__instance.slower.ForcedNormalSpeed) { __result = 1f; return false; }

        __result = __instance.CurTimeSpeed switch
        {
            TimeSpeed.Fast => 3f,
            TimeSpeed.Superfast => 6f,
            TimeSpeed.Ultrafast => 15f,
            _ => 1f,
        };
        return false;
    }
}
