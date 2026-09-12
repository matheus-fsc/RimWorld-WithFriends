namespace WithFriends.Protocol;

/// <summary>
/// Versão do protocolo, explícita no handshake — §9.1.
/// Compatibilidade nunca é derivada de reflexão sobre nomes de classe (§15.4).
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Versão que este build fala.</summary>
    public const int Current = 1;

    /// <summary>Versão mais antiga que este build ainda aceita.</summary>
    public const int MinimumSupported = 1;

    public static bool IsCompatible(int other) =>
        other >= MinimumSupported && other <= Current;
}

/// <summary>
/// Capacidades negociadas no handshake. Ausência de uma flag degrada o
/// recurso, nunca derruba a conexão (§9.1).
/// </summary>
[System.Flags]
public enum Capabilities
{
    None = 0,
    WorldEvents = 1 << 0,
    Checkpoints = 1 << 1,
    Sessions = 1 << 2,
    Market = 1 << 3,
    SteamTransport = 1 << 4,
}
