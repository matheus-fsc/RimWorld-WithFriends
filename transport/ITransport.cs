using System;
using System.Threading;
using System.Threading.Tasks;
using WithFriends.Protocol;

namespace WithFriends.Transport;

/// <summary>
/// §17.1 — o transporte é plugável. O projeto nunca fica refém de uma loja
/// nem de um protocolo: Steam é melhoria (§17.5), nunca requisito.
/// </summary>
public interface ITransport
{
    /// <summary>Nome curto para UI e log.</summary>
    string Nome { get; }

    /// <summary>Se este transporte está disponível nesta máquina agora.</summary>
    bool Disponivel { get; }

    Task<IConexao> ConectarAsync(EnderecoServidor endereco, CancellationToken token);
}

/// <summary>
/// Conexão viva com o coordenador. Enviar é síncrono e barato; receber é
/// não-bloqueante, para poder ser chamado de dentro do laço do jogo.
/// </summary>
public interface IConexao : IDisposable
{
    bool Conectado { get; }

    /// <summary>Endpoint efetivamente usado — pode ser IPv6 ou IPv4 (§17.4).</summary>
    string Descricao { get; }

    void Enviar(IMessage mensagem);

    /// <summary>
    /// Devolve <c>true</c> e preenche <paramref name="envelope"/> se havia
    /// mensagem completa esperando. Nunca bloqueia.
    /// </summary>
    bool TentarReceber(out Envelope envelope);
}
