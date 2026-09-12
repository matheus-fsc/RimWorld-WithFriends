using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A mala do visitante: os pawns que vão para a visita e o que eles carregam.
///
/// Uma caravana do RimWorld **é** isto — `Caravan.AllThings` é a união dos
/// inventários dos pawns, não existe porão de carga
/// (ver docs/MECANICA-DA-VISITA.md). Então o pacote é pequeno e o problema não
/// é tamanho: é **referência**.
///
/// Os trackers viajam dentro do pawn (`Scribe_Deep`), mas dentro deles há
/// referências a objetos de nível de partida — ideologia, políticas, relações.
/// Num jogo diferente esses ids ou não existem, ou existem significando outra
/// coisa. Daí a estratégia aqui:
///
/// 1. os objetos referenciados que **precisam** viajar (ideologias) vão no
///    mesmo arquivo, com os ids originais — assim o próprio Scribe resolve as
///    referências durante a carga, sem remendo;
/// 2. só **depois** de carregados os ids são trocados por ids locais novos, e
///    os objetos entram nos gerenciadores do anfitrião.
///
/// O que não viaja é cortado de propósito: relação com um pawn que ficou em
/// casa não deve existir na visita.
/// </summary>
public static class MalaDoVisitante
{
    public sealed class Resultado
    {
        public bool Ok { get; init; }
        public int Pawns { get; init; }
        public int Ideologias { get; init; }
        /// <summary>Ideologias adotadas nesta partida — para devolver na saída.</summary>
        public List<Ideo> IdeologiasAdotadas { get; init; } = new();
        public long Bytes { get; init; }
        public double Ms { get; init; }
        public string? Erro { get; init; }
        public List<Pawn> Recuperados { get; init; } = new();

        public override string ToString() =>
            Ok
                ? $"{Pawns} pawn(s), {Ideologias} ideologia(s), {Bytes / 1024.0:N1} KB, {Ms:N0} ms"
                : $"FALHOU: {Erro}";
    }

    public static string Pasta =>
        Path.Combine(GenFilePaths.SaveDataFolderPath, "WithFriends", "malas");

    public static string Caminho(string nome) => Path.Combine(Pasta, $"{nome}.xml");

    /// <summary>Lado do visitante: empacota quem vai.</summary>
    public static Resultado Empacotar(IEnumerable<Pawn> pawns, string caminho)
    {
        var lista = pawns.Where(p => p != null).Distinct().ToList();
        if (lista.Count == 0)
            return new Resultado { Erro = "nenhum pawn para empacotar." };

        // Ideologias referenciadas pelos que vão. Vão no mesmo arquivo para o
        // Scribe resolver as referências sozinho na carga.
        var ideologias = lista
            .Select(p => p.Ideo)
            .Where(i => i != null)
            .Distinct()
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        var relogio = Stopwatch.StartNew();

        try
        {
            Scribe.saver.InitSaving(caminho, "malaDoVisitante");
            try
            {
                Scribe_Collections.Look(ref ideologias, "ideologias", LookMode.Deep);
                Scribe_Collections.Look(ref lista, "pawns", LookMode.Deep);
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
        }
        catch (Exception e)
        {
            Scribe.ForceStop();
            return new Resultado { Erro = $"empacotar: {e.Message}" };
        }

        relogio.Stop();
        return new Resultado
        {
            Ok = true,
            Pawns = lista.Count,
            Ideologias = ideologias.Count,
            Bytes = new FileInfo(caminho).Length,
            Ms = relogio.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// Lado do anfitrião: abre a mala nesta partida.
    ///
    /// Os pawns saem daqui **sem estar no mapa** — quem os coloca lá é
    /// <c>CaravanEnterMapUtility</c> ou um spawn direto, que é decisão de quem
    /// chamou.
    /// </summary>
    public static Resultado Desempacotar(string caminho, Faction faccaoLocal)
    {
        if (!File.Exists(caminho))
            return new Resultado { Erro = $"arquivo não encontrado: {caminho}" };
        if (Current.Game == null)
            return new Resultado { Erro = "não há partida carregada." };

        List<Ideo>? ideologias = null;
        List<Pawn>? pawns = null;
        var relogio = Stopwatch.StartNew();

        // O jogo assume que carregar pawn só acontece fora do jogo rodando.
        // Ver ComentarioSobreProgramState, abaixo.
        var estadoAnterior = Current.ProgramState;
        Current.ProgramState = ProgramState.MapInitializing;

        try
        {
            Scribe.loader.InitLoading(caminho);
            try
            {
                // Sem isto, TODA referência a objeto vivo desta partida
                // resolve para null — e o pawn chega sem facção e sem
                // políticas, quebrando em PostLoadInit.
                ApresentarObjetosLocais();

                Scribe_Collections.Look(ref ideologias, "ideologias", LookMode.Deep);
                Scribe_Collections.Look(ref pawns, "pawns", LookMode.Deep);
                Scribe.loader.FinalizeLoading();
            }
            catch
            {
                Scribe.ForceStop();
                throw;
            }
        }
        catch (Exception e)
        {
            return new Resultado { Erro = $"desempacotar: {e.Message}" };
        }
        finally
        {
            Current.ProgramState = estadoAnterior;
        }

        if (pawns == null || pawns.Count == 0)
            return new Resultado { Erro = "a mala não trouxe pawn nenhum." };

        var adotadas = AdotarIdeologias(ideologias);
        foreach (var pawn in pawns) Nacionalizar(pawn, faccaoLocal);

        relogio.Stop();
        return new Resultado
        {
            Ok = true,
            Pawns = pawns.Count,
            Ideologias = adotadas.Count,
            IdeologiasAdotadas = adotadas,
            Bytes = new FileInfo(caminho).Length,
            Ms = relogio.Elapsed.TotalMilliseconds,
            Recuperados = pawns,
        };
    }

    /// <remarks>
    /// <para><b>Sobre trocar <c>Current.ProgramState</c> durante a carga.</b></para>
    ///
    /// <para>
    /// `LifeStageWorker_HumanlikeAdult.Notify_LifeStageStarted` tem um `return`
    /// logo no início quando o estado **não** é `Playing` — e, depois dele,
    /// acessa `previousLifeStage.developmentalStage` sem conferir se é nulo.
    /// Carregando um colono com o jogo rodando, `previousLifeStage` é nulo e o
    /// método estoura.
    /// </para>
    ///
    /// <para>
    /// É bug do jogo base, mas que ninguém vê: numa carga normal o estado é
    /// `MapInitializing`, então a função sai antes. Fazer o mesmo aqui não é
    /// truque — é dizer a verdade ao jogo: **isto é uma carga.** O próprio
    /// `Game.LoadGame` faz exatamente essa troca.
    /// </para>
    ///
    /// <para>
    /// Sintoma que isso resolve, de uma vez: `Error while determining if X
    /// should have Need Chemical_Alcohol` e `Could not do PostLoadInit on
    /// Pawn_IdeoTracker` — os dois vinham do mesmo NRE, por caminhos
    /// diferentes (`Pawn.DevelopmentalStage`).
    /// </para>
    /// </remarks>
    static class ComentarioSobreProgramState { }

    /// <summary>
    /// Campo privado: o dicionário <c>loadID → objeto</c> que o Scribe usa
    /// para resolver referências.
    /// </summary>
    static readonly FieldInfo? CampoDoDiretorio =
        AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory");

    /// <summary>
    /// Apresenta ao Scribe os objetos **vivos** desta partida, para que as
    /// referências dos pawns que chegam resolvam para os equivalentes locais.
    ///
    /// Uma carga no meio do jogo começa com o diretório vazio: só o que vem do
    /// arquivo existe ali. É por isso que um pawn recém-chegado aparecia sem
    /// facção e sem políticas — não era dado faltando, era ninguém para
    /// resolver a referência.
    ///
    /// Ideologias **não** entram: elas viajam no arquivo, e registrar as
    /// locais aqui criaria conflito de id com as que chegaram.
    ///
    /// O Multiplayer resolve isso mantendo um diretório sempre populado
    /// (`ScribeUtil.sharedCrossRefs`); aqui basta popular na hora da carga,
    /// porque a carga é pontual.
    /// </summary>
    static void ApresentarObjetosLocais()
    {
        if (CampoDoDiretorio?.GetValue(Scribe.loader.crossRefs) is not LoadedObjectDirectory diretorio)
        {
            Log.Warning(
                "[WithFriends] não foi possível alcançar o diretório de referências do Scribe — " +
                "os pawns podem chegar sem facção ou sem políticas.");
            return;
        }

        int registrados = 0;
        void Registrar(ILoadReferenceable? objeto)
        {
            if (objeto == null) return;
            try { diretorio.RegisterLoaded(objeto); registrados++; }
            catch (Exception) { /* id já em uso: o que veio no arquivo tem precedência */ }
        }

        foreach (var faccao in Find.FactionManager.AllFactions) Registrar(faccao);
        foreach (var politica in Current.Game.outfitDatabase.AllOutfits) Registrar(politica);
        foreach (var politica in Current.Game.drugPolicyDatabase.AllPolicies) Registrar(politica);
        foreach (var politica in Current.Game.foodRestrictionDatabase.AllFoodRestrictions) Registrar(politica);
        foreach (var politica in Current.Game.readingPolicyDatabase.AllReadingPolicies) Registrar(politica);

        Log.Message($"[WithFriends] {registrados} objeto(s) locais apresentados ao Scribe para resolução");
    }

    /// <summary>
    /// As ideologias que vieram passam a existir nesta partida, com ids novos.
    ///
    /// Trocar o id **depois** da carga é o ponto: durante a carga eles
    /// precisavam ser os originais, senão as referências dos pawns não
    /// resolveriam.
    /// </summary>
    static List<Ideo> AdotarIdeologias(List<Ideo>? ideologias)
    {
        var adotadas = new List<Ideo>();
        if (ideologias == null) return adotadas;

        var gerenciador = Find.IdeoManager;

        foreach (var ideo in ideologias.Where(i => i != null))
        {
            if (gerenciador.IdeosListForReading.Contains(ideo)) continue;

            ideo.id = Find.UniqueIDsManager.GetNextIdeoID();
            if (gerenciador.Add(ideo)) adotadas.Add(ideo);
        }

        return adotadas;
    }

    /// <summary>
    /// Fim da visita: as ideologias que vieram com o visitante vão embora com
    /// ele.
    ///
    /// Sem isto elas se acumulariam na partida do anfitrião — uma a cada
    /// visita, para sempre. O anfitrião continua jogando o próprio jogo depois
    /// do encontro, e o encontro não pode deixar entulho.
    ///
    /// Só remove o que ninguém mais usa: se algum pawn que ficou adotou a
    /// ideologia do visitante, ela fica (e aí é história do jogo, não lixo).
    /// </summary>
    public static int DevolverIdeologias(IEnumerable<Ideo> adotadas)
    {
        int removidas = 0;

        foreach (var ideo in adotadas.Where(i => i != null).Distinct())
        {
            bool emUso = PawnsFinder.All_AliveOrDead.Any(p => p.Ideo == ideo);
            if (emUso)
            {
                Log.Message($"[WithFriends] ideologia \"{ideo.name}\" ficou: ainda há quem a siga aqui.");
                continue;
            }

            if (Find.IdeoManager.Remove(ideo)) removidas++;
        }

        if (removidas > 0)
            Log.Message($"[WithFriends] {removidas} ideologia(s) do visitante devolvida(s)");

        return removidas;
    }

    /// <summary>
    /// Dá ao pawn um lugar nesta partida: id de coisa novo (para não colidir
    /// com nada daqui) e a facção local que representa o visitante.
    /// </summary>
    static void Nacionalizar(Pawn pawn, Faction faccaoLocal)
    {
        pawn.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();

        try
        {
            if (pawn.Faction != faccaoLocal) pawn.SetFaction(faccaoLocal);
        }
        catch (Exception e)
        {
            Log.Warning($"[WithFriends] não foi possível trocar a facção de {pawn.LabelShort}: {e.Message}");
        }
    }
}
