using System.Linq;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Retrato barato da partida, para comparar os dois lados quando a simulação
/// diverge.
///
/// A digital de RNG diz **que** divergiu e **quando**; ela não diz *o quê*.
/// Este retrato responde a pergunta seguinte: os dois lados estão simulando a
/// mesma coisa? Basta colocar as duas linhas lado a lado.
/// </summary>
public static class RetratoDaPartida
{
    public static string Tirar(string momento)
    {
        if (Current.Game == null) return $"[WithFriends] retrato ({momento}): sem partida";

        var mapa = Find.CurrentMap;
        var pawns = mapa?.mapPawns;

        return
            $"[WithFriends] retrato da partida ({momento}):\n" +
            $"  tick {Find.TickManager.TicksGame}, velocidade {Find.TickManager.CurTimeSpeed}, " +
            $"pausado {Find.TickManager.Paused}\n" +
            // Os dois relógios lado a lado (ADR 0017). Se os passos baterem e
            // os simulados não, os dois lados discordaram sobre quais passos
            // simulavam — e é desync na certa.
            $"  passo {Passos()}\n" +
            $"  mapas: {Find.Maps.Count} " +
            $"[{string.Join(", ", Find.Maps.Select(m => $"{m.uniqueID}:{m.Size.x}x{m.Size.z}"))}]\n" +
            $"  mapa atual {mapa?.uniqueID.ToString() ?? "-"}: " +
            $"{mapa?.listerThings?.AllThings?.Count ?? 0} things " +
            $"({PorCategoria(mapa)}), " +
            $"{pawns?.AllPawnsSpawned?.Count ?? 0} pawns no mapa " +
            $"({pawns?.FreeColonistsSpawnedCount ?? 0} colonos), " +
            $"{mapa?.listerBuildings?.allBuildingsColonist?.Count ?? 0} construções\n" +
            $"  facção: {Faction.OfPlayer?.Name ?? "-"}, " +
            $"facções: {Find.FactionManager?.AllFactions?.Count() ?? 0}, " +
            $"world pawns: {Find.WorldPawns?.AllPawnsAliveOrDead?.Count ?? 0}\n" +
            $"  clima: {mapa?.weatherManager?.curWeather?.defName ?? "-"}, " +
            $"estação: {GenLocalDate.Season(mapa)}, " +
            $"narrador: {Find.Storyteller?.def?.defName ?? "-"} " +
            $"({Find.Storyteller?.difficultyDef?.defName ?? "-"})\n" +
            // godMode e devMode mudam caminhos de simulação: se diferirem entre
            // os dois lados, o determinismo acaba antes de começar.
            $"  devMode {Prefs.DevMode}, godMode {DebugSettings.godMode}, " +
            $"narradorLigado {DebugSettings.enableStoryteller}, " +
            $"danoLigado {DebugSettings.enableDamage}, " +
            $"surtosLigados {DebugSettings.enableRandomMentalStates}, " +
            $"doencasLigadas {DebugSettings.enableRandomDiseases}, " +
            $"semAnimais {DebugSettings.noAnimals}\n" +
            $"  ordem das listas de célula: {OrdemDasCelulas(mapa)}";
    }

    /// <summary>
    /// Uma digital da ORDEM em que as coisas estão listadas em cada célula.
    ///
    /// <para><b>Por que isto merece uma linha no retrato.</b> A ordem da lista
    /// de uma célula é a ordem em que as coisas entraram nela — história de
    /// processo, não estado do save (ADR 0020). E ela decide simulação: quando
    /// uma bala atravessa uma célula, <c>Projectile.CheckForFreeIntercept</c>
    /// percorre essa lista e sorteia por candidato, <b>parando no primeiro
    /// acerto</b>. Duas ordens diferentes, dois alvos diferentes.</para>
    ///
    /// <para>O anfitrião não recarrega na junção — ele continua na partida dele
    /// —, então as listas dele carregam tudo o que aconteceu antes da visita. O
    /// visitante monta as dele carregando o save, na ordem do arquivo. Se esta
    /// digital diferir entre os dois lados no início da visita, está provado; se
    /// bater, a suspeita cai e é preciso procurar noutro lugar.</para>
    ///
    /// <para>Só células com mais de uma coisa entram: onde há uma só não há
    /// ordem, e elas são a maioria esmagadora do mapa.</para>
    /// </summary>
    static string OrdemDasCelulas(Map? mapa)
    {
        if (mapa?.thingGrid == null) return "-";

        int celulas = 0;
        ulong digital = 1469598103934665603UL;   // FNV-1a, 64 bits

        foreach (var celula in mapa.AllCells)
        {
            var lista = mapa.thingGrid.ThingsListAtFast(celula);
            if (lista == null || lista.Count < 2) continue;

            celulas++;
            foreach (var coisa in lista)
            {
                digital ^= (ulong)coisa.thingIDNumber;
                digital *= 1099511628211UL;
            }
        }

        return $"{digital:x16} ({celulas} célula(s) com mais de uma coisa)";
    }

    public static void Registrar(string momento) => Log.Message(Tirar(momento));

    /// <summary>
    /// Quebra as coisas do mapa por categoria, com os transitórios separados.
    ///
    /// Motes (fumaça, faíscas, números de dano) são <c>Thing</c> como qualquer
    /// outra, mas **não são salvos**: existem só enquanto a partida roda. Duas
    /// instâncias da mesma partida nunca terão o mesmo conjunto deles — e se
    /// eles consumirem RNG no tick, o determinismo acaba aí.
    /// </summary>
    static string Passos()
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao == null) return "(fora de sessão)";

        return $"{sessao.TickDeSessao} " +
               $"({sessao.passosSimulados} simulados, {sessao.passosSemSimular} só com comando)";
    }

    static string PorCategoria(Map? mapa)
    {
        if (mapa?.listerThings?.AllThings == null) return "-";

        var todas = mapa.listerThings.AllThings;
        int motes = todas.Count(t => t is Mote);
        int filth = todas.Count(t => t is Filth);
        int plantas = todas.Count(t => t.def?.category == ThingCategory.Plant);
        int itens = todas.Count(t => t.def?.category == ThingCategory.Item);
        int construcoes = todas.Count(t => t.def?.category == ThingCategory.Building);

        return
            $"motes {motes}, filth {filth}, plantas {plantas}, " +
            $"itens {itens}, construções {construcoes}, " +
            $"resto {todas.Count - motes - filth - plantas - itens - construcoes}";
    }
}
