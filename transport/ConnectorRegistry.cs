using System.Collections.Generic;
using System.Linq;

namespace WithFriends.Transport;

/// <summary>
/// Registro de transportes disponíveis — §17.1. Referência de padrão:
/// `Source/Client/Util/ConnectorRegistry.cs` do Multiplayer (MIT, Zetrith).
///
/// Steam entra aqui como mais um transporte, nunca como requisito: em
/// instalação não-Steam <c>SteamManager.Initialized</c> é <c>false</c> e o
/// registro simplesmente não o oferece (§17.5).
/// </summary>
public static class ConnectorRegistry
{
    static readonly List<ITransport> registrados = new() { new DirectTransport() };

    public static void Registrar(ITransport transporte) => registrados.Add(transporte);

    public static IReadOnlyList<ITransport> Todos => registrados;

    /// <summary>Transportes utilizáveis agora, na ordem da escada da §17.4.</summary>
    public static IReadOnlyList<ITransport> Disponiveis =>
        registrados.Where(t => t.Disponivel).ToList();

    public static ITransport Padrao => Disponiveis.First();
}
