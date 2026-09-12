using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace WithFriends.Client.World;

/// <summary>
/// A colônia de outro jogador no mapa-mundo.
///
/// §4 — **você só enxerga o que você alcança**. Sem presença física, isto aqui
/// é tudo o que existe do outro lado: nome, riqueza estimada e se ele está
/// online. Não há mapa transferido, não há conteúdo, não há como espiar.
/// A regra não é sabor: elimina a transferência de mapa inteiro (1739 ms
/// medidos no RT) e a categoria inteira de bug de "aplicar mapa remoto".
/// </summary>
public class AssentamentoRemoto : WorldObject
{
    public string playerId = "";
    public string colonyId = "";
    public string nomeColonia = "";
    public int riquezaEstimada;
    public bool online;

    public override string Label =>
        string.IsNullOrEmpty(nomeColonia) ? base.Label : nomeColonia;

    public override bool HasName => !string.IsNullOrEmpty(nomeColonia);

    public override string GetInspectString()
    {
        var texto = new StringBuilder();
        texto.AppendLine($"Colônia de outro jogador ({playerId})");
        texto.AppendLine($"Riqueza estimada: {riquezaEstimada:N0}");
        texto.Append(online ? "Online agora" : "Offline");
        return texto.ToString();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref playerId, "playerId", "");
        Scribe_Values.Look(ref colonyId, "colonyId", "");
        Scribe_Values.Look(ref nomeColonia, "nomeColonia", "");
        Scribe_Values.Look(ref riquezaEstimada, "riquezaEstimada", 0);
        // 'online' não é persistido: presença é volátil por definição.
    }
}

[DefOf]
public static class WithFriendsDefOf
{
    public static WorldObjectDef WithFriends_AssentamentoRemoto = null!;

    static WithFriendsDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(WithFriendsDefOf));
}
