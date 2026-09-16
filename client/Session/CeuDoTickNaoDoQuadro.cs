using System;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// O brilho do céu que a simulação lê vem do <b>tick</b>, não do último quadro
/// desenhado.
///
/// <para><b>A caçada que terminou aqui.</b> Uma divergência voltava sessão após
/// sessão com a mesma assinatura: velocidade de pawn diferente na sexta casa
/// decimal, logo nos primeiros ticks, com a capacidade de mover idêntica dos
/// dois lados.</para>
///
/// <code>
/// vel  tick 116  #37382 Igor:  A 1.392797   B 1.392656
/// tpm  tick 116                A 45.21164   B 45.21622
/// </code>
///
/// <para>Duas hipóteses caíram no caminho — o multiplicador de clima (medido:
/// igual a 1 dos dois lados por 400 ticks) e o cache de stat (consertado, e a
/// divergência voltou). O que restou foi ler a <b>definição do stat</b>, no XML
/// do jogo:</para>
///
/// <code>
/// &lt;defName&gt;MoveSpeed&lt;/defName&gt;
/// &lt;parts&gt;
///   &lt;li Class="StatPart_Glow"&gt;          ← 0,80 no escuro; 1,00 com brilho ≥ 0,30
/// </code>
///
/// <para><b>A velocidade de todo pawn humano depende da luz onde ele está.</b> E
/// a luz tem um componente que o jogo atualiza no quadro:</para>
///
/// <code>
/// wf auditar --escritores SkyManager::curSkyGlowInt   → SkyManager.SkyManagerUpdate
/// wf auditar --chamadores SkyManager::SkyManagerUpdate → Map.MapUpdate
/// </code>
///
/// <para><c>MapUpdate</c> é quadro, não tick. Duas máquinas desenham em ritmos
/// diferentes, e entre dois ticks uma pode rodar três quadros e a outra um — o
/// valor que a simulação lê passa a depender de quando o último quadro caiu.
/// Como o brilho do sol muda continuamente, a diferença é pequena e constante:
/// exatamente a sexta casa decimal que vínhamos perseguindo.</para>
///
/// <para><b>O conserto é do Multiplayer</b>, que resolve o mesmo problema por
/// outro motivo (lá cada mapa corre no seu tempo). A linha dele diz tudo:</para>
///
/// <code>
/// // Reset the effects of SkyManager.SkyManagerUpdate
/// map.skyManager.curSkyGlowInt = map.skyManager.CurrentSkyTarget().glow;
/// </code>
///
/// <para>Antes de cada tick, recalcular o brilho a partir do estado do jogo. O
/// que o quadro escreveu vale para a tela; a simulação usa o do tick.</para>
///
/// <para><b>Uma vez por tick, não por consulta.</b> <c>CurrentSkyTarget</c> é
/// caro e <c>GetStatValue(MoveSpeed)</c> é chamado por pawn por tick. Recalcular
/// no começo do tick custa uma vez; remendar o getter custaria centenas.</para>
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
public static class CeuDoTickNaoDoQuadro
{
    /// <summary><c>CurrentSkyTarget</c> é privado — alcançado por delegate, que
    /// é o que se usa em caminho quente (ver <see cref="PosicaoDeDesenhoForaDaSimulacao"/>).</summary>
    static readonly Func<SkyManager, SkyTarget>? AlvoAtual =
        AccessTools.Method(typeof(SkyManager), "CurrentSkyTarget") != null
            ? AccessTools.MethodDelegate<Func<SkyManager, SkyTarget>>(
                AccessTools.Method(typeof(SkyManager), "CurrentSkyTarget"))
            : null;

    [HarmonyPrefix]
    public static void Antes()
    {
        if (!RngDeSessao.Ativo || AlvoAtual == null) return;

        var mapas = Find.Maps;
        if (mapas == null) return;

        for (int i = 0; i < mapas.Count; i++)
        {
            var ceu = mapas[i]?.skyManager;
            if (ceu == null) continue;

            try { ceu.ForceSetCurSkyGlow(AlvoAtual(ceu).glow); }
            catch (Exception) { /* mapa a meio caminho de carregar: o tick seguinte pega */ }
        }

        GuardasDeDeterminismo.Disparou("céu do tick, não do quadro");
    }
}
