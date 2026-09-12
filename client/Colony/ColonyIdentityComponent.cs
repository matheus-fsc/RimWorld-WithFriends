using System;
using Verse;
using WithFriends.Protocol;

namespace WithFriends.Client.Colony;

/// <summary>
/// <c>colony_id</c> — nasce com a partida e vive dentro do save.
///
/// É o que torna a identidade estável quando o **nome do arquivo muda**:
/// o modo Morte Permanente renomeia o save, e no RT isso deixou o arquivo
/// indexado órfão por horas sem ninguém perceber (§15.1). Aqui o nome do
/// arquivo é irrelevante — a colônia é (player_id, colony_id).
/// </summary>
public class ColonyIdentityComponent : GameComponent
{
    string colonyId = "";

    public ColonyIdentityComponent(Game game) { }

    public string ColonyId
    {
        get
        {
            if (string.IsNullOrEmpty(colonyId))
                colonyId = Guid.NewGuid().ToString("D");
            return colonyId;
        }
    }

    public ColonyIdentity Identity =>
        new(WithFriendsMod.Settings.PlayerIdOuNovo(), ColonyId);

    public override void ExposeData()
    {
        Scribe_Values.Look(ref colonyId, "colonyId", "");
    }

    public static ColonyIdentityComponent? Atual =>
        Current.Game?.GetComponent<ColonyIdentityComponent>();
}
