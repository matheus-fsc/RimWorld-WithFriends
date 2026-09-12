using System.IO;

namespace WithFriends.Protocol.Messages;

/// <summary>
/// sistema.handshake — primeira mensagem do cliente.
/// A identidade é <c>PlayerId</c>, nunca o endereço de rede (§17.3 regra 5, §15.2).
/// </summary>
public sealed class Handshake : IMessage
{
    public int ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;
    public string PlayerId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Capabilities Capabilities { get; init; }
    /// <summary>Hash do conjunto de mods classe <c>world</c> apenas — §8.</summary>
    public string WorldModSetHash { get; init; } = "";

    public MessageId Id => MessageId.Handshake;

    public void Write(BinaryWriter w)
    {
        w.Write(ProtocolVersion);
        w.Write(PlayerId);
        w.Write(DisplayName);
        w.Write((int)Capabilities);
        w.Write(WorldModSetHash);
    }

    public static Handshake Read(BinaryReader r) => new()
    {
        ProtocolVersion = r.ReadInt32(),
        PlayerId = r.ReadString(),
        DisplayName = r.ReadString(),
        Capabilities = (Capabilities)r.ReadInt32(),
        WorldModSetHash = r.ReadString(),
    };
}

/// <summary>sistema.handshake.aceito — capacidades efetivas são a interseção.</summary>
public sealed class HandshakeAceito : IMessage
{
    public int ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;
    public string ServerName { get; init; } = "";
    public Capabilities Capabilities { get; init; }

    public MessageId Id => MessageId.HandshakeAceito;

    public void Write(BinaryWriter w)
    {
        w.Write(ProtocolVersion);
        w.Write(ServerName);
        w.Write((int)Capabilities);
    }

    public static HandshakeAceito Read(BinaryReader r) => new()
    {
        ProtocolVersion = r.ReadInt32(),
        ServerName = r.ReadString(),
        Capabilities = (Capabilities)r.ReadInt32(),
    };
}

public enum MotivoRecusa
{
    VersaoIncompativel = 1,
    ModsDeMundoDivergentes = 2,
    ServidorCheio = 3,
    Banido = 4,
}

/// <summary>
/// sistema.handshake.recusado — toda recusa traz motivo legível,
/// nunca desconexão muda (§9.1, §15.4).
/// </summary>
public sealed class HandshakeRecusado : IMessage
{
    public MotivoRecusa Motivo { get; init; }
    /// <summary>Texto para o jogador: o que houve e o que fazer.</summary>
    public string Explicacao { get; init; } = "";

    public MessageId Id => MessageId.HandshakeRecusado;

    public void Write(BinaryWriter w)
    {
        w.Write((int)Motivo);
        w.Write(Explicacao);
    }

    public static HandshakeRecusado Read(BinaryReader r) => new()
    {
        Motivo = (MotivoRecusa)r.ReadInt32(),
        Explicacao = r.ReadString(),
    };
}
