using System;

namespace WithFriends.Protocol;

/// <summary>
/// Identidade de uma colônia: <c>(player_id, colony_id)</c> — §7.1 regra 5.
/// Nome de arquivo é detalhe de armazenamento e nunca entra aqui: foi
/// exatamente a colisão de nome de arquivo que causou a perda silenciosa
/// descrita na §15.1, quando o modo Morte Permanente renomeou o save.
/// </summary>
public readonly struct ColonyIdentity : IEquatable<ColonyIdentity>
{
    public readonly string PlayerId;
    public readonly string ColonyId;

    public ColonyIdentity(string playerId, string colonyId)
    {
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("PlayerId vazio", nameof(playerId));
        if (string.IsNullOrWhiteSpace(colonyId)) throw new ArgumentException("ColonyId vazio", nameof(colonyId));
        PlayerId = playerId;
        ColonyId = colonyId;
    }

    public bool Equals(ColonyIdentity other) =>
        PlayerId == other.PlayerId && ColonyId == other.ColonyId;

    public override bool Equals(object? obj) => obj is ColonyIdentity other && Equals(other);

    public override int GetHashCode() =>
        unchecked(PlayerId.GetHashCode() * 397 ^ ColonyId.GetHashCode());

    public override string ToString() => $"{PlayerId}/{ColonyId}";
}
