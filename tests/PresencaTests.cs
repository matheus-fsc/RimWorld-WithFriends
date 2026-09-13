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
        public string Planeta { get; set; } = "";
        public string PlanetaLegivel { get; set; } = "";
        public List<IMessage> Recebidas { get; } = new();
        public int Reconsideracoes { get; private set; }

        public void ReconsiderarPlaneta() => Reconsideracoes++;
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

    [Fact]
    public void Fato_de_mundo_so_chega_a_quem_esta_no_mesmo_planeta()
    {
        // Um evento diz "tile 113533", e tile é índice, não coordenada. Em
        // outro planeta esse índice aponta para outro lugar, ou não existe.
        var presenca = new Presenca();
        var aqui = new DestinatarioFalso { PlayerId = "p1", Planeta = "sha256:terra" };
        var la = new DestinatarioFalso { PlayerId = "p2", Planeta = "sha256:marte" };
        presenca.Entrou(aqui);
        presenca.Entrou(la);

        int antesLa = la.Recebidas.Count;
        presenca.DifundirNoPlaneta(new MundoEvento(), "sha256:terra");

        Assert.Single(aqui.Recebidas.OfType<MundoEvento>());
        Assert.Equal(antesLa, la.Recebidas.Count);
    }

    [Fact]
    public void Quem_esta_em_outro_planeta_e_listado_para_o_aviso()
    {
        var presenca = new Presenca();
        var sozinho = new DestinatarioFalso { PlayerId = "p1", Planeta = "sha256:terra" };
        var outro = new DestinatarioFalso { PlayerId = "p2", Planeta = "sha256:marte" };
        var semDeclarar = new DestinatarioFalso { PlayerId = "p3" };
        presenca.Entrou(sozinho);
        presenca.Entrou(outro);
        presenca.Entrou(semDeclarar);

        var emOutro = presenca.EmOutroPlaneta("sha256:terra");

        // Quem ainda não declarou não conta: não se pede a ninguém que gere um
        // planeta que o coordenador não sabe qual é.
        Assert.Single(emOutro);
        Assert.Equal("p2", emOutro[0].PlayerId);
        Assert.Equal(1, presenca.NoPlaneta("sha256:terra"));
    }

    [Fact]
    public void Sair_faz_todo_mundo_reconsiderar_o_planeta()
    {
        // Quem sobrou pode ter acabado de ficar sozinho no planeta dele, e
        // ninguém lhe diria isso: a mudança aconteceu na conexão do outro.
        var presenca = new Presenca();
        var fica = new DestinatarioFalso { PlayerId = "p1", Planeta = "sha256:terra" };
        var vai = new DestinatarioFalso { PlayerId = "p2", Planeta = "sha256:terra" };
        presenca.Entrou(fica);
        presenca.Entrou(vai);

        int antes = fica.Reconsideracoes;
        presenca.Saiu(vai);

        Assert.True(fica.Reconsideracoes > antes);
    }

    sealed class DestinatarioQuebrado : IDestinatario
    {
        public string PlayerId { get; init; } = "";
        public string DisplayName => "";
        public string Planeta => "";
        public string PlanetaLegivel => "";

        public void ReconsiderarPlaneta() => throw new IOException("socket morto");
        public void Entregar(IMessage mensagem) => throw new IOException("socket morto");
    }
}
