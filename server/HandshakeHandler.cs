using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Server;

/// <summary>
/// Decide o handshake por versão declarada e capacidades — nunca por reflexão
/// sobre nomes de classe (§9.1, §15.4). Toda recusa carrega motivo legível.
/// </summary>
public sealed class HandshakeHandler
{
    readonly string serverName;
    readonly Capabilities offered;

    public HandshakeHandler(string serverName, Capabilities offered)
    {
        this.serverName = serverName;
        this.offered = offered;
    }

    public IMessage Handle(Handshake hello)
    {
        if (!ProtocolVersion.IsCompatible(hello.ProtocolVersion))
        {
            return new HandshakeRecusado
            {
                Motivo = MotivoRecusa.VersaoIncompativel,
                Explicacao =
                    $"O cliente fala a versão {hello.ProtocolVersion} do protocolo; " +
                    $"este servidor aceita de {ProtocolVersion.MinimumSupported} a {ProtocolVersion.Current}. " +
                    "Atualize o mod ou o servidor.",
            };
        }

        // Divergência de mods NÃO é verificada aqui: a checagem acontece na
        // entrada da sessão, para nunca impedir alguém de jogar sozinho (§8).
        return new HandshakeAceito
        {
            ServerName = serverName,
            Capabilities = offered & hello.Capabilities,
        };
    }
}
