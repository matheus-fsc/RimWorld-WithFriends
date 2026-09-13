using System.Collections.Concurrent;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Server.Mundo;

/// <summary>Alguém conectado agora.</summary>
public interface IDestinatario
{
    string PlayerId { get; }
    string DisplayName { get; }

    /// <summary>
    /// Em que planeta este cliente está agora — o hash das variáveis de
    /// geração do mundo que ele tem aberto. Vazio até ele declarar.
    ///
    /// <para>Muda quando ele carrega outra partida, inclusive ao atravessar
    /// para a colônia de outro jogador numa visita. Por isso é propriedade da
    /// conexão, não do registro: o planeta de alguém é um fato do presente.</para>
    /// </summary>
    string Planeta { get; }

    /// <summary>
    /// O mesmo planeta em português: semente, cobertura, chuva. É o que
    /// permite a quem está sozinho no seu planeta saber <b>o que gerar</b>
    /// para encontrar os outros.
    /// </summary>
    string PlanetaLegivel { get; }

    /// <summary>
    /// Reconsidere se ainda está sozinho no seu planeta.
    ///
    /// <para>A companhia de alguém muda quando <b>outra</b> conexão declara
    /// planeta ou desaparece — não quando esta faz alguma coisa. Sem este
    /// empurrão, quem chegou primeiro nunca saberia que o segundo entrou em
    /// outro planeta, e quem ficou sozinho depois de todo mundo sair
    /// continuaria achando que está acompanhado.</para>
    /// </summary>
    void ReconsiderarPlaneta();

    void Entregar(IMessage mensagem);
}

/// <summary>
/// Quem está online — responsabilidade do coordenador (§10). Estado volátil:
/// nunca entra no log append-only, porque presença não é fato histórico do
/// planeta.
///
/// Também é o canal de difusão: quando um fato novo é ordenado, todos os
/// conectados recebem. Quem estava fora pega pelo cursor quando voltar (§2.1),
/// e é isso que faz o jogo assíncrono funcionar sem ninguém online.
/// </summary>
public sealed class Presenca
{
    readonly ConcurrentDictionary<string, IDestinatario> conectados = new();

    public IReadOnlyCollection<IDestinatario> Conectados => conectados.Values.ToArray();

    /// <summary>Quem está atrás de um <c>player_id</c> agora, se estiver online.</summary>
    public IDestinatario? Para(string playerId) =>
        conectados.TryGetValue(playerId, out var destinatario) ? destinatario : null;

    /// <summary>
    /// Registra a conexão. Se a mesma identidade já estava online, **a nova
    /// vence** e a antiga é devolvida para ser encerrada.
    ///
    /// A alternativa — recusar a segunda — travaria o jogador para fora
    /// sempre que uma conexão morresse sem o servidor perceber (TCP
    /// meio-aberto), que é justamente quando ele mais precisa reconectar.
    /// </summary>
    public IDestinatario? Entrou(IDestinatario destinatario)
    {
        conectados.TryGetValue(destinatario.PlayerId, out var anterior);
        if (anterior == destinatario) anterior = null;
        conectados[destinatario.PlayerId] = destinatario;

        if (anterior != null)
            Console.WriteLine(
                $"!! identidade {destinatario.PlayerId} reconectou: a conexão anterior será encerrada. " +
                "Se são duas instâncias do jogo na mesma máquina, use -savedatafolder para separá-las.");

        // Quem chega recebe a lista de quem já estava.
        foreach (var outro in conectados.Values)
            if (outro.PlayerId != destinatario.PlayerId)
                destinatario.Entregar(Aviso(outro, online: true));

        if (anterior == null)
            Difundir(Aviso(destinatario, online: true), exceto: destinatario.PlayerId);
        Console.WriteLine($"presença: {destinatario.DisplayName} ({destinatario.PlayerId}) online — {conectados.Count} conectado(s)");
        return anterior;
    }

    /// <summary>
    /// Só remove se a conexão registrada ainda for esta: uma conexão antiga
    /// morrendo depois de ter sido substituída não pode derrubar a nova.
    /// </summary>
    public void Saiu(IDestinatario destinatario)
    {
        if (!conectados.TryGetValue(destinatario.PlayerId, out var atual) || atual != destinatario) return;
        conectados.TryRemove(destinatario.PlayerId, out _);

        Difundir(Aviso(destinatario, online: false), exceto: destinatario.PlayerId);
        Console.WriteLine($"presença: {destinatario.DisplayName} ({destinatario.PlayerId}) offline — {conectados.Count} conectado(s)");

        // Quem sobrou pode ter ficado sozinho no planeta dele agora.
        ReconsiderarPlanetas();
    }

    /// <summary>
    /// Quem está online em <b>outro</b> planeta que não este. Vazio significa
    /// "ninguém a avisar": ou você está acompanhado, ou está sozinho no
    /// coordenador, e nenhum dos dois é problema.
    /// </summary>
    public IReadOnlyList<IDestinatario> EmOutroPlaneta(string planeta) =>
        conectados.Values
            .Where(d => d.Planeta.Length > 0 && d.Planeta != planeta)
            .ToArray();

    /// <summary>Quantos estão online neste planeta, contando você.</summary>
    public int NoPlaneta(string planeta) =>
        conectados.Values.Count(d => d.Planeta == planeta);

    /// <summary>
    /// Manda todo mundo reconsiderar com quem divide planeta. Chamado quando a
    /// composição muda: alguém declarou planeta, alguém saiu.
    /// </summary>
    public void ReconsiderarPlanetas()
    {
        foreach (var destinatario in conectados.Values)
        {
            try { destinatario.ReconsiderarPlaneta(); }
            catch (Exception e)
            {
                Console.WriteLine($"reconsideração de planeta falhou para {destinatario.PlayerId}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Difunde só para quem está no mesmo planeta.
    ///
    /// <para>Fato de mundo carrega índice de tile, e índice de tile só
    /// significa a mesma coisa no mesmo planeta. Difundir para todos entregaria
    /// coordenada sem sentido a quem está em outro — que é exatamente o que a
    /// checagem de <c>InBounds</c> do cliente existe para aparar, e é melhor
    /// nunca chegar lá.</para>
    /// </summary>
    public void DifundirNoPlaneta(IMessage mensagem, string planeta, string? exceto = null)
    {
        foreach (var destinatario in conectados.Values)
        {
            if (destinatario.PlayerId == exceto) continue;
            if (destinatario.Planeta != planeta) continue;
            try { destinatario.Entregar(mensagem); }
            catch (Exception e)
            {
                Console.WriteLine($"difusão falhou para {destinatario.PlayerId}: {e.Message}");
            }
        }
    }

    public void Difundir(IMessage mensagem, string? exceto = null)
    {
        foreach (var destinatario in conectados.Values)
        {
            if (destinatario.PlayerId == exceto) continue;
            try
            {
                destinatario.Entregar(mensagem);
            }
            catch (Exception e)
            {
                // Conexão morrendo não pode derrubar a difusão para os outros.
                Console.WriteLine($"difusão falhou para {destinatario.PlayerId}: {e.Message}");
            }
        }
    }

    static MundoPresenca Aviso(IDestinatario destinatario, bool online) => new()
    {
        PlayerId = destinatario.PlayerId,
        DisplayName = destinatario.DisplayName,
        Online = online,
    };
}
