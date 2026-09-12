// Inspirado em Source/Client/Patches/Determinism.cs (StatWorkerGetValuePatch,
// PawnCapacitiesHandlerGetLevelPatch) de rwmt/Multiplayer, MIT,
// Copyright (c) 2018 Zetrith. Ver THIRD_PARTY/Multiplayer-MIT.txt

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Os dois caches que a interface envenena — e que só doem em combate.
///
/// <para><b>Por que estes dois, e por que agora.</b> Todo desync que medimos foi
/// em combate: búfalos, mecanoides, insetos, tiro em parede. Construir, mover,
/// alistar e mexer em estoque nunca quebraram. A explicação está aqui:
/// <c>StatWorker.GetValue</c> e <c>PawnCapacitiesHandler.GetLevel</c> são
/// consultados <b>a cada tick</b> em combate — velocidade de movimento, chance
/// de acerto, precisão de tiro, consciência, dor, manipulação — e quase nunca
/// fora dele. E os dois têm cache.</para>
///
/// <para>Cache não é problema: recalcular dá o mesmo valor. O problema é
/// <b>quem</b> escreve nele. A interface pergunta esses mesmos valores o tempo
/// todo — passar o mouse sobre um pawn, abrir a aba de saúde, desenhar a barra
/// de vida. Se o valor que a interface calculou fica no cache, a simulação lê
/// dali. E a interface de cada jogador olha para lugares diferentes, em momentos
/// diferentes.</para>
///
/// <para>Em combate o mouse está justamente em cima da briga. É por isso que
/// quebra ali e não construindo.</para>
///
/// <para><b>Como o Multiplayer resolve.</b> Inventa um terceiro estado de cache
/// — <c>CachedInInterface</c>, o valor 3 num enum de dois — e a simulação
/// recalcula em vez de reusar o que foi marcado assim. É preciso e custa dois
/// transpiladores.</para>
///
/// <para><b>Como resolvemos.</b> Pelo outro lado da mesma moeda: a interface
/// simplesmente <b>não deixa rastro</b>. Guardamos a entrada do cache antes e a
/// devolvemos depois, então do ponto de vista da simulação a pergunta da
/// interface nunca aconteceu. Mesmo efeito, sem mexer em IL — e o preço é a
/// interface recalcular, que é trabalho de quadro e não de tick.</para>
/// </summary>
public static class CachesDeCombateForaDaInterface
{
    // ------------------------------------------------- StatWorker.GetValue

    static readonly FieldInfo? CacheTemporario =
        AccessTools.Field(typeof(StatWorker), "temporaryStatCache");

    /// <summary>
    /// O que havia no cache de stat antes de a interface perguntar.
    ///
    /// <c>null</c> = não havia entrada, e o certo é removê-la depois.
    /// </summary>
    public readonly struct EntradaDeStat
    {
        public EntradaDeStat(bool guardou, object? anterior, bool existia)
        {
            Guardou = guardou;
            Anterior = anterior;
            Existia = existia;
        }

        public bool Guardou { get; }
        public object? Anterior { get; }
        public bool Existia { get; }
    }

    public static EntradaDeStat AntesDoStat(StatWorker trabalhador, Thing? coisa)
    {
        if (!NaInterface.Agora || coisa == null || CacheTemporario == null)
            return default;

        if (CacheTemporario.GetValue(trabalhador) is not System.Collections.IDictionary cache)
            return default;

        bool existia = cache.Contains(coisa);
        return new EntradaDeStat(true, existia ? cache[coisa] : null, existia);
    }

    public static void DepoisDoStat(StatWorker trabalhador, Thing? coisa, EntradaDeStat antes)
    {
        if (!antes.Guardou || coisa == null || CacheTemporario == null) return;

        if (CacheTemporario.GetValue(trabalhador) is not System.Collections.IDictionary cache) return;

        GuardasDeDeterminismo.Disparou("StatWorker.GetValue (cache da interface desfeito)");

        if (antes.Existia) cache[coisa] = antes.Anterior;
        else cache.Remove(coisa);
    }

    // --------------------------------------- PawnCapacitiesHandler.GetLevel

    static readonly FieldInfo? NiveisCacheados =
        AccessTools.Field(typeof(PawnCapacitiesHandler), "cachedCapacityLevels");

    static readonly Type? TipoDoElemento =
        AccessTools.Inner(typeof(PawnCapacitiesHandler), "CacheElement");

    static readonly FieldInfo? CampoStatus =
        TipoDoElemento != null ? AccessTools.Field(TipoDoElemento, "status") : null;

    static readonly FieldInfo? CampoValor =
        TipoDoElemento != null ? AccessTools.Field(TipoDoElemento, "value") : null;

    static readonly Dictionary<Type, MethodInfo?> IndexadorPorTipo = new();

    /// <summary>
    /// O elemento de cache daquela capacidade, ou <c>null</c>.
    ///
    /// <para><c>DefMap&lt;D,V&gt;</c> tem <b>dois</b> indexadores — <c>this[D
    /// def]</c> e <c>this[int index]</c> —, então pedir <c>get_Item</c> pelo nome
    /// dá <c>AmbiguousMatchException</c>. Sem o tipo do parâmetro, o guarda
    /// estourava a cada desenho do painel de inspeção; o erro subia por
    /// <c>CanBeAwake</c> e enchia a tela.</para>
    ///
    /// <para>Todo o corpo vai num <c>try</c>: este guarda é conveniência de
    /// determinismo, e nada aqui vale derrubar o painel de um jogador
    /// (§14.5).</para>
    /// </summary>
    static object? Elemento(PawnCapacitiesHandler tratador, PawnCapacityDef capacidade)
    {
        try
        {
            object? mapa = NiveisCacheados?.GetValue(tratador);
            if (mapa == null) return null;

            var tipo = mapa.GetType();
            if (!IndexadorPorTipo.TryGetValue(tipo, out var indexador))
                IndexadorPorTipo[tipo] = indexador =
                    AccessTools.Method(tipo, "get_Item", new[] { typeof(PawnCapacityDef) });

            return indexador?.Invoke(mapa, new object[] { capacidade });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>CacheStatus.Uncached</c> — o único estado em que desfazer é seguro.
    ///
    /// <para>O enum tem três valores: <c>Uncached</c>, <c>Caching</c>,
    /// <c>Cached</c>. Só interessa o primeiro.</para>
    /// </summary>
    const int NaoCacheado = 0;

    public static (bool guardou, object? status, object? valor) AntesDaCapacidade(
        PawnCapacitiesHandler tratador, PawnCapacityDef capacidade)
    {
        if (!NaInterface.Agora || CampoStatus == null || CampoValor == null) return default;

        object? elemento = Elemento(tratador, capacidade);
        if (elemento == null) return default;

        object? status = CampoStatus.GetValue(elemento);

        // **Só a chamada que COMEÇA um cálculo.**
        //
        // `GetLevel` é reentrante: calcular uma capacidade pergunta outras, e
        // durante isso o elemento fica em `Caching`. Guardar e devolver esse
        // estado transitório deixava a capacidade presa em `Caching` para
        // sempre — e daí em diante o jogo respondia:
        //
        //     Detected infinite stat recursion when evaluating {capacity}
        //     return 0f;
        //
        // Capacidade zero é pawn incapaz: sumiram opções do menu de interação e
        // a tela piscava com o erro. Já em `Cached`, a interface só leu e não há
        // rastro a desfazer.
        if (status == null || (int)status != NaoCacheado) return default;

        return (true, status, CampoValor.GetValue(elemento));
    }

    public static void DepoisDaCapacidade(
        PawnCapacitiesHandler tratador, PawnCapacityDef capacidade,
        (bool guardou, object? status, object? valor) antes)
    {
        if (!antes.guardou || CampoStatus == null || CampoValor == null) return;

        object? elemento = Elemento(tratador, capacidade);
        if (elemento == null) return;

        GuardasDeDeterminismo.Disparou("PawnCapacitiesHandler.GetLevel (cache da interface desfeito)");

        CampoStatus.SetValue(elemento, antes.status);
        CampoValor.SetValue(elemento, antes.valor);
    }
}

/// <summary>
/// A sobrecarga com cache por tick. As outras vão direto ao cálculo e não
/// deixam rastro.
/// </summary>
[HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValue),
    new[] { typeof(Thing), typeof(bool), typeof(int) })]
public static class StatDaInterfaceNaoFicaNoCache
{
    [HarmonyPrefix]
    public static void Antes(StatWorker __instance, Thing thing,
        out CachesDeCombateForaDaInterface.EntradaDeStat __state) =>
        __state = CachesDeCombateForaDaInterface.AntesDoStat(__instance, thing);

    [HarmonyPostfix]
    public static void Depois(StatWorker __instance, Thing thing,
        CachesDeCombateForaDaInterface.EntradaDeStat __state) =>
        CachesDeCombateForaDaInterface.DepoisDoStat(__instance, thing, __state);
}

/// <summary>
/// Capacidades: consciência, movimento, manipulação, visão. Combate pergunta
/// todas, a cada tick.
/// </summary>
[HarmonyPatch(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.GetLevel))]
public static class CapacidadeDaInterfaceNaoFicaNoCache
{
    [HarmonyPrefix]
    public static void Antes(PawnCapacitiesHandler __instance, PawnCapacityDef capacity,
        out (bool guardou, object? status, object? valor) __state) =>
        __state = CachesDeCombateForaDaInterface.AntesDaCapacidade(__instance, capacity);

    [HarmonyPostfix]
    public static void Depois(PawnCapacitiesHandler __instance, PawnCapacityDef capacity,
        (bool guardou, object? status, object? valor) __state) =>
        CachesDeCombateForaDaInterface.DepoisDaCapacidade(__instance, capacity, __state);
}
