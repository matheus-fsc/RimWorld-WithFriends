using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using WithFriends.Client.Session.Portas;
using WithFriends.Protocol.Determinismo;

namespace WithFriends.Client.Session;

/// <summary>
/// Adaptador do <see cref="IImpressaoDigital"/> para o RimWorld — anel 2.
///
/// A superfície de jogo que este arquivo toca cabe em três linhas:
/// <c>Rand.StateCompressed</c>, <c>Find.TickManager.TicksGame</c> e
/// <c>Find.Maps</c>. É deliberado: no Multiplayer, o subsistema inteiro de
/// detecção de desync toca sete membros, todos fundações do jogo — é o que
/// torna essa parte estável entre versões (docs/SESSAO-COMPONENTES.md §2).
///
/// Ideia reusada de `Source/Client/Desyncs/` do Multiplayer
/// (MIT, Copyright (c) 2018 Zetrith) — ver THIRD_PARTY/Multiplayer-MIT.txt.
/// </summary>
public sealed class ImpressaoDigitalRimWorld : IImpressaoDigital
{
    /// <summary>
    /// <c>Rand.StateCompressed</c> é privado (seed | iterations &lt;&lt; 32).
    /// É o estado inteiro do RNG num ulong — exatamente o que precisa ser
    /// idêntico dos dois lados. Catalogado em <c>CatalogoDePatches</c>.
    /// </summary>
    static readonly MethodInfo? LerEstadoDoRand =
        AccessTools.PropertyGetter(typeof(Rand), "StateCompressed");

    OpiniaoDeSincronia? atual;

    public bool Disponivel => LerEstadoDoRand != null;

    public void IniciarIntervalo(long tick)
    {
        atual = new OpiniaoDeSincronia
        {
            TickInicial = tick,
            TickFinal = tick,
            ModoDeArredondamento = ModoDeArredondamento.Atual(),
        };
    }

    public void AmostrarMundo() => atual?.EstadosDoMundo.Add(EstadoDoRand());

    public void AmostrarMapa(int mapaId) => atual?.EstadosDoMapa(mapaId).Add(EstadoDoRand());

    public void AmostrarComando() => atual?.EstadosDeComandos.Add(EstadoDoRand());

    public OpiniaoDeSincronia FecharIntervalo(long tick)
    {
        var fechada = atual ?? new OpiniaoDeSincronia { TickInicial = tick };
        fechada.TickFinal = tick;
        atual = null;
        return fechada;
    }

    /// <summary>
    /// Os 32 bits **altos** do estado do RNG.
    ///
    /// <c>StateCompressed = seed | (iterations &lt;&lt; 32)</c>. A metade baixa é a
    /// semente, que só muda em <c>Rand.Seed</c>/<c>PushState</c> — quase nunca.
    /// A metade alta é o contador de iterações, que anda a cada número sorteado:
    /// é ela que mede quanta simulação aconteceu, e é ela que diverge quando
    /// dois clientes calculam coisas diferentes.
    ///
    /// O Multiplayer usa exatamente essa metade
    /// (`Desyncs/SyncCoordinator.cs`: <c>(uint)(state &gt;&gt; 32)</c>).
    /// </summary>
    static uint EstadoDoRand()
    {
        // Dentro de uma sessão, o que vale é o RNG **da sessão**: ele começa
        // igual nos dois lados (mesma semente) e avança igual se a simulação
        // for determinística.
        //
        // O `Rand` do processo não serve: ele não é salvo no jogo, então o
        // visitante que acabou de carregar a partida do anfitrião tem um
        // contador completamente diferente. Comparar aquilo acusava desync no
        // tick 0 de toda visita — medindo duas coisas que nunca teriam por que
        // bater.
        if (RngDeSessao.Ativo) return (uint)(RngDeSessao.Estado >> 32);

        return (uint)(EstadoCompleto() >> 32);
    }

    /// <summary>Estado bruto, para diagnóstico: semente e iterações juntas.</summary>
    public static ulong EstadoCompleto()
    {
        try
        {
            if (LerEstadoDoRand != null)
                return (ulong)LerEstadoDoRand.Invoke(null, null);
        }
        catch (Exception)
        {
            // Membro interno mudou: o catálogo já avisou na subida.
        }
        return 0;
    }

    /// <summary>Semente atual — a metade que quase nunca muda.</summary>
    public static uint Semente() => (uint)(EstadoCompleto() & 0xFFFFFFFFul);

    /// <summary>Iterações — quantos números já foram sorteados nesta semente.</summary>
    public static uint Iteracoes() => (uint)(EstadoCompleto() >> 32);
}
