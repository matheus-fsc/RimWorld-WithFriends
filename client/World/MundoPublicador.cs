using System.Linq;
using RimWorld;
using UnityEngine;
using RimWorld.Planet;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.World;

/// <summary>
/// Publica no log de mundo o que os outros jogadores podem saber da sua
/// colônia sem estar lá — §4: nome, riqueza estimada, tile. Nada além disso.
/// </summary>
public static class MundoPublicador
{
    /// <summary>
    /// Riqueza é arredondada de propósito. O outro jogador vê ordem de
    /// grandeza, não a sua contabilidade.
    /// </summary>
    const int Granularidade = 100;

    public static void PublicarProprioAssentamento()
    {
        var cliente = WithFriendsMod.Cliente;
        if (!cliente.Conectado || Current.Game == null) return;

        // **Durante uma visita, não.**
        //
        // O visitante está dentro da partida do anfitrião: o primeiro
        // assentamento da facção do jogador que ele acha ali é o do
        // **anfitrião**, no planeta do anfitrião. Publicar isso afirmaria o
        // assentamento alheio como próprio, com o id de colônia do visitante.
        //
        // O checkpoint automático publica junto, e ele continua correndo
        // durante a visita — então isto não é hipotético.
        if (Session.VisitaEmAndamento.Ativa) return;

        var assentamento = Find.WorldObjects.Settlements
            .FirstOrDefault(s => s.Faction == Faction.OfPlayer);
        if (assentamento == null) return;

        var identidade = ColonyIdentityComponent.Atual;
        if (identidade == null) return;

        var payload = new AssentamentoPayload
        {
            ColonyId = identidade.ColonyId,
            Nome = assentamento.Label,
            Tile = assentamento.Tile.tileId,
            Riqueza = Arredondar(RiquezaDe(assentamento)),
        };

        cliente.Enviar(new MundoPublicar
        {
            Tipo = TipoEventoMundo.AssentamentoPublicado,
            Payload = payload.ParaBytes(),
        });

        Log.Message(
            $"[WithFriends] assentamento publicado: {payload.Nome} " +
            $"em {payload.Tile}, riqueza ~{payload.Riqueza:N0}");
    }

    static float RiquezaDe(Settlement assentamento) =>
        assentamento.HasMap ? assentamento.Map.wealthWatcher.WealthTotal : 0f;

    static int Arredondar(float valor) =>
        (int)(Mathf.Round(valor / Granularidade) * Granularidade);
}
