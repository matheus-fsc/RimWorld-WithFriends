using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Numa corrida sem ninguém olhando, nada pode pausar o jogo.
///
/// <para><b>O buraco.</b> A emulação já despausa a cada quadro — a
/// ressincronização volta pausada de propósito, e não há quem retome. Mas isso
/// só cobre a <b>velocidade</b>:</para>
///
/// <code>
/// // TickManager
/// public bool Paused      => curTimeSpeed != 0 ? ForcePaused : true;
/// public bool ForcePaused => Find.WindowStack.WindowsForcePause
///                         || LongEventHandler.ForcePause
///                         || Find.TilePicker.Active
///                         || ...;
/// </code>
///
/// <para>Uma carta de ameaça, um diálogo de escolha, um seletor de tile — cada
/// um abre uma janela com <c>forcePause</c>, e o relógio para <b>por ela
/// existir</b>. Escrever <c>CurTimeSpeed</c> não desfaz isso. Uma corrida longa
/// que encontrasse o primeiro incidente com carta ficaria parada até o prazo
/// acabar, e o diário terminaria cheio de nada.</para>
///
/// <para><b>Escopo.</b> Só na emulação e no árbitro — instâncias que existem
/// para rodar sozinhas. Numa visita de gente de verdade a pausa por janela é
/// legítima: é o jogador lendo a carta, e a barreira faz o outro lado esperar,
/// que é o desenho do §3. Aqui não há jogador para esperar.</para>
/// </summary>
[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowsForcePause), MethodType.Getter)]
public static class JanelaNaoPausaAEmulacao
{
    [HarmonyPostfix]
    public static void Depois(ref bool __result)
    {
        if (!__result) return;
        if (!ModoEmulacao.Ativo && !ModoArbitro.Ativo) return;

        __result = false;
        GuardasDeDeterminismo.Disparou("janela não pausa a emulação");
    }
}
