using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.World;

/// <summary>
/// Consome o log de eventos do mundo e o aplica ao planeta local — §2.1.
///
/// O cursor é do cliente: cada um consome no próprio tempo. Aplicar um evento
/// e avançar o cursor acontece junto, então reconectar depois de um crash
/// reentrega o que faltava sem duplicar o que já entrou.
/// </summary>
public static class MundoAplicador
{
    public static void Aplicar(EventoMundo evento)
    {
        var cursor = Current.Game?.GetComponent<WorldCursorComponent>();
        if (cursor == null) return;

        // Fato meu volta pelo log como o de qualquer um: ignorar é o certo,
        // porque a minha colônia já existe no meu planeta.
        bool meu = evento.Autor == WithFriendsMod.Settings.PlayerIdOuNovo();

        if (!meu)
        {
            switch (evento.Tipo)
            {
                case TipoEventoMundo.AssentamentoPublicado:
                    Publicar(evento.Autor, evento.Decodificar(AssentamentoPayload.Read));
                    break;

                case TipoEventoMundo.AssentamentoRemovido:
                    Remover(evento.Autor, evento.Decodificar(AssentamentoPayload.Read).ColonyId);
                    break;

                default:
                    // §9.1: tipo desconhecido não trava o consumo do log —
                    // o cursor avança e a vida segue.
                    Log.Message($"[WithFriends] evento de mundo não tratado: {evento.Tipo} (seq {evento.Seq})");
                    break;
            }
        }

        cursor.WorldCursor = evento.Seq;
    }

    static void Publicar(string playerId, AssentamentoPayload payload)
    {
        // Tile é índice, não coordenada: um índice de outro planeta ou aponta
        // para outro lugar, ou não existe. Aplicar sem conferir já causou
        // ExecutionEngineException no meio da UI, longe da causa.
        if (!IdentidadeDoPlaneta.TileExiste(payload.Tile))
        {
            Log.Warning(
                $"[WithFriends] assentamento \"{payload.Nome}\" de {playerId} ignorado: " +
                $"o tile {payload.Tile} não existe neste planeta " +
                $"({Find.WorldGrid?.TilesCount ?? 0} tiles). " +
                "Os dois jogadores precisam do mesmo planeta — mesma semente e mesmas " +
                "opções de geração.");
            return;
        }

        var existente = Encontrar(playerId, payload.ColonyId);

        if (existente != null)
        {
            existente.nomeColonia = payload.Nome;
            existente.riquezaEstimada = payload.Riqueza;
            if (existente.Tile.tileId != payload.Tile) existente.Tile = new PlanetTile(payload.Tile);
            return;
        }

        var assentamento = (AssentamentoRemoto)WorldObjectMaker.MakeWorldObject(
            WithFriendsDefOf.WithFriends_AssentamentoRemoto);
        assentamento.playerId = playerId;
        assentamento.colonyId = payload.ColonyId;
        assentamento.nomeColonia = payload.Nome;
        assentamento.riquezaEstimada = payload.Riqueza;
        assentamento.Tile = new PlanetTile(payload.Tile);
        Find.WorldObjects.Add(assentamento);

        Messages.Message(
            $"With Friends: {payload.Nome} apareceu no planeta.",
            MessageTypeDefOf.NeutralEvent, historical: false);
        Log.Message($"[WithFriends] assentamento de {playerId} em {payload.Tile}: {payload.Nome}");
    }

    static void Remover(string playerId, string colonyId)
    {
        var assentamento = Encontrar(playerId, colonyId);
        if (assentamento != null) Find.WorldObjects.Remove(assentamento);
    }

    public static void AtualizarPresenca(MundoPresenca presenca)
    {
        foreach (var assentamento in Todos().Where(a => a.playerId == presenca.PlayerId))
            assentamento.online = presenca.Online;

        Messages.Message(
            $"With Friends: {presenca.DisplayName} está {(presenca.Online ? "online" : "offline")}.",
            MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>
    /// Remove do planeta local tudo o que veio do log. Não apaga nada no
    /// servidor: o log é a verdade, isto aqui é só a projeção local dele.
    /// </summary>
    public static int EsquecerTodos()
    {
        var remotos = Todos().ToList();
        foreach (var assentamento in remotos) Find.WorldObjects.Remove(assentamento);
        return remotos.Count;
    }

    static IEnumerable<AssentamentoRemoto> Todos() =>
        Find.WorldObjects.AllWorldObjects.OfType<AssentamentoRemoto>();

    static AssentamentoRemoto? Encontrar(string playerId, string colonyId) =>
        Todos().FirstOrDefault(a => a.playerId == playerId && a.colonyId == colonyId);
}
