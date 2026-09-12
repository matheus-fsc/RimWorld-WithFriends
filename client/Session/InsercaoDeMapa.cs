using System;
using System.Diagnostics;
using System.IO;
using RimWorld.Planet;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// **Caminho descartado — ver ADR 0010.** Mantido porque é o instrumento que
/// produziu a evidência, e porque o diagnóstico que ele imprime vale para
/// qualquer tentativa futura de mexer em mapa entre partidas.
///
/// Insere na partida do visitante o mapa que chegou do anfitrião.
///
/// É a parte arriscada do bootstrap, e a que o RT errou: o `Scribe` resolve
/// referências cruzadas contra o registro da partida **atual**, e misturar os
/// loadIDs do anfitrião com os do visitante é exatamente a receita do
/// [RT #273] (duplicate load IDs, falha ao descarregar mapa, corrupção de
/// cliente).
///
/// Por isso este caminho é conservador: tudo é medido, tudo é registrado, e
/// qualquer falha para antes de tocar na partida em vez de deixar o jogo num
/// meio-termo.
/// </summary>
public static class InsercaoDeMapa
{
    public sealed class Resultado
    {
        public bool Ok { get; init; }
        public int MapaId { get; init; }
        public double MsDesserializar { get; init; }
        public double MsInserir { get; init; }
        public int Things { get; init; }
        public int Pawns { get; init; }
        public string? Erro { get; init; }

        public override string ToString() =>
            Ok
                ? $"mapa {MapaId} inserido: {Things:N0} things, {Pawns} pawns — " +
                  $"{MsDesserializar:N0} ms desserializar + {MsInserir:N0} ms inserir"
                : $"FALHOU ao inserir o mapa {MapaId}: {Erro}";
    }

    public static Resultado Inserir(string caminho, int mapaId)
    {
        if (Current.Game == null)
            return new Resultado { MapaId = mapaId, Erro = "não há partida carregada." };
        if (!File.Exists(caminho))
            return new Resultado { MapaId = mapaId, Erro = $"arquivo não encontrado: {caminho}" };

        Map? mapa = null;
        var relogio = Stopwatch.StartNew();

        try
        {
            Scribe.loader.InitLoading(caminho);
            try
            {
                Scribe_Deep.Look(ref mapa, "map");
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
            return new Resultado { MapaId = mapaId, Erro = $"desserialização: {e.Message}" };
        }

        double msDesserializar = relogio.Elapsed.TotalMilliseconds;
        if (mapa == null)
            return new Resultado { MapaId = mapaId, Erro = "o Scribe devolveu mapa nulo." };

        relogio.Restart();
        try
        {
            Current.Game.AddMap(mapa);
            mapa.FinalizeLoading();
            mapa.Parent?.FinalizeLoading();
            Current.Game.CurrentMap = mapa;
        }
        catch (Exception e)
        {
            // Tentar desfazer: um mapa meio-inserido é pior que nenhum.
            try { Current.Game.DeinitAndRemoveMap(mapa, notifyPlayer: false); }
            catch (Exception limpeza) { Log.Error($"[WithFriends] falha ao desfazer inserção: {limpeza.Message}"); }

            return new Resultado { MapaId = mapaId, Erro = $"inserção: {e.Message}" };
        }

        return new Resultado
        {
            Ok = true,
            MapaId = mapa.uniqueID,
            MsDesserializar = msDesserializar,
            MsInserir = relogio.Elapsed.TotalMilliseconds,
            Things = mapa.listerThings?.AllThings?.Count ?? 0,
            Pawns = mapa.mapPawns?.AllPawns?.Count ?? 0,
        };
    }

    /// <summary>
    /// Fim da visita: o mapa vai embora, sem cache e sem reconciliação
    /// (ADR 0008). Voltar significa sincronizar de novo.
    /// </summary>
    public static void Descartar(int mapaId)
    {
        var mapa = Current.Game?.Maps?.Find(m => m.uniqueID == mapaId);
        if (mapa == null) return;

        try
        {
            Current.Game!.DeinitAndRemoveMap(mapa, notifyPlayer: false);
            Log.Message($"[WithFriends] mapa {mapaId} da visita descartado");
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] falha ao descartar o mapa {mapaId}: {e.Message}");
        }
    }
}
