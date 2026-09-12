using Verse;

namespace WithFriends.Client.World;

/// <summary>
/// Cursor no log de eventos de mundo (§2.1). Cada cliente consome do próprio
/// cursor, no próprio tempo — não existe estado de mundo que se sobrescreve.
/// Persiste no save do jogador via Scribe.
/// </summary>
public class WorldCursorComponent : GameComponent
{
    /// <summary>Último <c>seq</c> de evento de mundo já aplicado localmente.</summary>
    public long WorldCursor;

    public WorldCursorComponent(Game game) { }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref WorldCursor, "worldCursor", 0L);
    }
}
