using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Mundo;
using Xunit;

namespace WithFriends.Tests;

public class PresencaTests
{
    sealed class DestinatarioFalso : IDestinatario
    {
        public string PlayerId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public List<IMessage> Recebidas { get; } = new();

        public void Entregar(IMessage mensagem) => Recebidas.Add(mensagem);
    }

    [Fact]
    public void Quem_chega_recebe_a_lista_de_quem_ja_estava()
    {
        var presenca = new Presenca();
        var primeiro = new DestinatarioFalso { PlayerId = "p1", DisplayName = "math" };
        var segundo = new DestinatarioFalso { PlayerId = "p2", DisplayName = "vizinho" };

        presenca.Entrou(primeiro);
        presenca.Entrou(segundo);

        Assert.Contains(segundo.Recebidas.OfType<MundoPresenca>(), m => m.PlayerId == "p1" && m.Online);
        Assert.Contains(primeiro.Recebidas.OfType<MundoPresenca>(), m => m.PlayerId == "p2" && m.Online);
    }

    [Fact]
    public void Sair_avisa_os_outros()
    {
        var presenca = new Presenca();
        var primeiro = new DestinatarioFalso { PlayerId = "p1" };
        var segundo = new DestinatarioFalso { PlayerId = "p2" };
        presenca.Entrou(primeiro);
        presenca.Entrou(segundo);

        presenca.Saiu(segundo);

        Assert.Contains(primeiro.Recebidas.OfType<MundoPresenca>(), m => m.PlayerId == "p2" && !m.Online);
        Assert.Single(presenca.Conectados);
    }

    [Fact]
    public void Mesma_identidade_reconectando_desloca_a_conexao_antiga()
    {
        // Dois RimWorlds na mesma máquina compartilham a pasta de dados e,
        // portanto, o mesmo player_id. A nova conexão vence — recusar
        // travaria o jogador para fora depois de um TCP meio-aberto.
        var presenca = new Presenca();
        var antiga = new DestinatarioFalso { PlayerId = "p1", DisplayName = "math" };
        var nova = new DestinatarioFalso { PlayerId = "p1", DisplayName = "math" };

        presenca.Entrou(antiga);
        var deslocada = presenca.Entrou(nova);

        Assert.Same(antiga, deslocada);
        Assert.Single(presenca.Conectados);
        Assert.Same(nova, presenca.Conectados.First());
    }

    [Fact]
    public void Conexao_antiga_morrendo_depois_nao_derruba_a_nova()
    {
        var presenca = new Presenca();
        var antiga = new DestinatarioFalso { PlayerId = "p1" };
        var nova = new DestinatarioFalso { PlayerId = "p1" };
        presenca.Entrou(antiga);
        presenca.Entrou(nova);

        presenca.Saiu(antiga);   // chega atrasado

        Assert.Single(presenca.Conectados);
        Assert.Same(nova, presenca.Conectados.First());
    }

    [Fact]
    public void Difusao_ignora_destinatario_com_conexao_morrendo()
    {
        var presenca = new Presenca();
        var quebrado = new DestinatarioQuebrado { PlayerId = "p1" };
        var sadio = new DestinatarioFalso { PlayerId = "p2" };
        presenca.Entrou(quebrado);
        presenca.Entrou(sadio);
        sadio.Recebidas.Clear();

        presenca.Difundir(new MundoPresenca { PlayerId = "p3", Online = true });

        Assert.Single(sadio.Recebidas);
    }

    sealed class DestinatarioQuebrado : IDestinatario
    {
        public string PlayerId { get; init; } = "";
        public string DisplayName => "";

        public void Entregar(IMessage mensagem) => throw new IOException("socket morto");
    }
}
