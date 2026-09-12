// Adaptado de Source/Client/Patches/Seeds.cs de rwmt/Multiplayer, MIT,
// Copyright (c) 2018 Zetrith. Ver THIRD_PARTY/Multiplayer-MIT.txt

using System;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Carregar uma partida sorteia — e precisa sortear igual dos dois lados.
///
/// <para><b>Por que isto importa tanto para nós.</b> No Multiplayer, carregar
/// acontece quando alguém entra. Aqui acontece <b>a cada ponto de junção
/// refeito</b>: toda ressincronização recarrega os dois lados (ADR 0019). Se o
/// carregamento consome o gerador de forma diferente em cada máquina, os dois
/// lados voltam de um recarregamento já desalinhados — e a divergência reaparece
/// poucos passos depois, sempre, por mais que se ressincronize.</para>
///
/// <para>Era exatamente o que o diário mostrava: divergência voltando 8 a 24
/// passos depois de <b>todos</b> os pontos de junção.</para>
///
/// <para><b>O que sorteia ao carregar.</b> Muita coisa: <c>PostLoadInit</c> de
/// comps, resolução de referências cruzadas, recálculo de grades. Nada disso é
/// "simulação" no sentido do tick, e nada disso passa pelo nosso relógio de
/// sessão — então nada disso estava protegido.</para>
///
/// <para><b>A saída, que é a dele.</b> Fixar a semente em volta de cada etapa de
/// carregamento, com <c>PushState</c>/<c>PopState</c>. O intervalo é curto e bem
/// delimitado, que é justamente para o que a pilha do RimWorld serve — ao
/// contrário do gerador da sessão inteira, que precisou de outra solução
/// (ver <see cref="RngDeSessao"/>).</para>
///
/// <para>A semente do mapa é <c>HashCombineInt(uniqueID, ConstantRandSeed)</c>:
/// o id do mapa e a semente persistente do planeta. Os dois viajam no save, então
/// os dois lados chegam ao mesmo número sem precisar combinar nada.</para>
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
public static class SementeAoCarregarPartida
{
    /// <summary>
    /// <c>1</c> é o valor que o Multiplayer usa. O número em si não importa —
    /// importa ser o mesmo dos dois lados, e ser fixo em vez de herdado do que a
    /// interface estava fazendo um instante antes.
    /// </summary>
    const int SementeFixa = 1;

    /// <summary>
    /// Só dentro de uma visita.
    ///
    /// <para>O Multiplayer empilha estado em todo carregamento, inclusive fora
    /// de partida multijogador. Aqui não: fora de uma visita, isolar o
    /// carregamento do gerador muda o jogo de quem instalou o mod e não está
    /// jogando com ninguém — e mod não deve mudar jogo de um jogador só.</para>
    /// </summary>
    [HarmonyPrefix]
    public static void Antes(out bool __state)
    {
        __state = VisitaEmAndamento.Ativa;
        if (!__state) return;

        GuardasDeDeterminismo.Disparou("semente fixa ao carregar a partida");
        Rand.PushState();
        Rand.Seed = SementeFixa;
    }

    [HarmonyPostfix]
    public static void Depois(bool __state)
    {
        if (__state) Rand.PopState();
    }
}

/// <summary>
/// O mapa lendo a si mesmo do save.
///
/// <para>Só nos modos de carregamento: <c>ExposeData</c> também roda ao salvar, e
/// ali não há o que semear.</para>
/// </summary>
[HarmonyPatch(typeof(Map), nameof(Map.ExposeData))]
public static class SementeAoLerMapa
{
    [HarmonyPrefix]
    public static void Antes(Map __instance, out bool __state)
    {
        __state = false;
        if (!VisitaEmAndamento.Ativa) return;

        if (Scribe.mode != LoadSaveMode.LoadingVars &&
            Scribe.mode != LoadSaveMode.ResolvingCrossRefs &&
            Scribe.mode != LoadSaveMode.PostLoadInit) return;

        SementesDeCarregamento.Empilhar(__instance, "Map.ExposeData");
        __state = true;
    }

    [HarmonyPostfix]
    public static void Depois(bool __state)
    {
        if (__state) Rand.PopState();
    }
}

/// <summary>
/// O mapa terminando de se montar depois de carregado — grades, regiões,
/// caches. Sorteia, e sorteia bastante.
/// </summary>
[HarmonyPatch(typeof(Map), nameof(Map.FinalizeLoading))]
public static class SementeAoFinalizarMapa
{
    [HarmonyPrefix]
    public static void Antes(Map __instance, out bool __state)
    {
        __state = false;
        if (!VisitaEmAndamento.Ativa) return;

        SementesDeCarregamento.Empilhar(__instance, "Map.FinalizeLoading");
        __state = true;
    }

    [HarmonyPostfix]
    public static void Depois(bool __state)
    {
        if (__state) Rand.PopState();
    }
}

/// <summary>
/// Evento longo agendado de dentro da simulação leva a semente do momento em que
/// foi agendado.
///
/// <para>Um evento longo roda <b>depois</b>, fora do tick, num momento que cada
/// máquina escolhe — quando a tela de carregamento aparece, quando o quadro
/// acaba. Se ele sortear, sorteia num ponto diferente do fluxo em cada lado.</para>
///
/// <para>A saída é tirar um número <b>agora</b>, dentro do tick (onde os dois
/// lados estão iguais), e usá-lo como semente quando a ação finalmente rodar.
/// O sorteio do agendamento é igual dos dois lados; o da execução passa a ser
/// função dele.</para>
/// </summary>
[HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
    new[] { typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>),
            typeof(bool), typeof(bool), typeof(Action) })]
public static class SementeDeEventoLongo
{
    [HarmonyPrefix]
    public static void Antes(ref Action action, ref Action callback)
    {
        // Só o que é agendado de dentro da simulação. Evento longo agendado pela
        // interface é da interface, e semear ali seria mexer no que não é nosso.
        if (!NaInterface.Tickando && !ComandoDeSessao.Aplicando) return;

        GuardasDeDeterminismo.Disparou("semente de evento longo");

        if (action != null)
        {
            int semente = Rand.Int;
            var original = action;
            action = () =>
            {
                Rand.PushState(semente);
                try { original(); }
                finally { Rand.PopState(); }
            };
        }

        if (callback != null)
        {
            int sementeDoRetorno = Rand.Int;
            var original = callback;
            callback = () =>
            {
                Rand.PushState(sementeDoRetorno);
                try { original(); }
                finally { Rand.PopState(); }
            };
        }
    }
}

static class SementesDeCarregamento
{
    /// <summary>
    /// A semente daquele mapa: id do mapa combinado com a semente persistente do
    /// planeta. Os dois viajam no save, então os dois lados chegam ao mesmo
    /// número sem combinar nada.
    /// </summary>
    public static void Empilhar(Map mapa, string onde)
    {
        int semente = Gen.HashCombineInt(mapa.uniqueID, Find.World?.ConstantRandSeed ?? 0);
        GuardasDeDeterminismo.Disparou($"semente fixa em {onde}");
        Rand.PushState(semente);
    }
}
