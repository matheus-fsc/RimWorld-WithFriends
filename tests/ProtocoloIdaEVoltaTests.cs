using System.IO;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using Xunit;

namespace WithFriends.Tests;

/// <summary>
/// Toda mensagem tem de voltar do fio igual ao que entrou.
///
/// <para>Existe por causa de um bug que custou quatro rodadas de teste em jogo.
/// <c>SessaoBarreira.MudouVelocidade</c> foi acrescentado à classe e <b>não</b>
/// ao <c>Write</c>/<c>Read</c>. O cliente preenchia, o servidor lia sempre
/// <c>false</c>, e nenhuma velocidade era adotada — o tempo voltava a 1× para
/// sempre, ninguém aparecia como autor, e a etiqueta ficava sem cor.</para>
///
/// <para>Nada disso parecia um bug de serialização: parecia comando não sendo
/// enviado, etiqueta quebrada, pausa voltando sozinha. Campo que existe na
/// classe e não no fio falha em silêncio, e silêncio manda a gente procurar no
/// lugar errado.</para>
/// </summary>
public class ProtocoloIdaEVoltaTests
{
    static T IdaEVolta<T>(T mensagem, System.Func<BinaryReader, T> ler) where T : IMessage
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            mensagem.Write(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        return ler(r);
    }

    [Fact]
    public void SessaoBarreira_volta_inteira()
    {
        var original = new SessaoBarreira
        {
            SessaoId = "sessao-1",
            Autor = "jogador-a",
            Tick = 1234,
            Fingerprint = "rng:abcdef",
            TickLiberado = 1300,
            Pausado = true,
            TodosPausados = true,
            Velocidade = VelocidadeDeSessao.MuitoRapido,
            MudouVelocidade = true,
            VelocidadeAcordada = VelocidadeDeSessao.Rapido,
            QuemMudou = "jogador-b",
            VersaoDoTempo = 42,
            VelocidadeDesdePasso = 777,
            DespausarRecusado = true,
        };

        var voltou = IdaEVolta(original, SessaoBarreira.Read);

        Assert.Equal(original.SessaoId, voltou.SessaoId);
        Assert.Equal(original.Autor, voltou.Autor);
        Assert.Equal(original.Tick, voltou.Tick);
        Assert.Equal(original.Fingerprint, voltou.Fingerprint);
        Assert.Equal(original.TickLiberado, voltou.TickLiberado);
        Assert.Equal(original.Pausado, voltou.Pausado);
        Assert.Equal(original.TodosPausados, voltou.TodosPausados);
        Assert.Equal(original.Velocidade, voltou.Velocidade);
        Assert.Equal(original.MudouVelocidade, voltou.MudouVelocidade);
        Assert.Equal(original.VelocidadeAcordada, voltou.VelocidadeAcordada);
        Assert.Equal(original.QuemMudou, voltou.QuemMudou);
        Assert.Equal(original.VersaoDoTempo, voltou.VersaoDoTempo);
        Assert.Equal(original.VelocidadeDesdePasso, voltou.VelocidadeDesdePasso);
        Assert.Equal(original.DespausarRecusado, voltou.DespausarRecusado);
    }

    [Fact]
    public void SessaoBarreira_com_valores_nao_padrao_nao_perde_nenhum_campo()
    {
        // Valores distintos do padrão em TODOS os campos: um campo esquecido no
        // Write volta como o padrão, e é exatamente assim que o bug se
        // disfarçou de "o comando não está sendo enviado".
        var original = new SessaoBarreira
        {
            SessaoId = "s", Autor = "a", Tick = 1, Fingerprint = "f", TickLiberado = 2,
            Pausado = true, TodosPausados = true,
            Velocidade = VelocidadeDeSessao.Ultra,
            MudouVelocidade = true,
            VelocidadeAcordada = VelocidadeDeSessao.Ultra,
            QuemMudou = "q", VersaoDoTempo = 9, VelocidadeDesdePasso = 5, DespausarRecusado = true,
        };

        var voltou = IdaEVolta(original, SessaoBarreira.Read);
        var padrao = new SessaoBarreira();

        Assert.NotEqual(padrao.MudouVelocidade, voltou.MudouVelocidade);
        Assert.NotEqual(padrao.QuemMudou, voltou.QuemMudou);
        Assert.NotEqual(padrao.VersaoDoTempo, voltou.VersaoDoTempo);
        Assert.NotEqual(padrao.VelocidadeDesdePasso, voltou.VelocidadeDesdePasso);
        Assert.NotEqual(padrao.DespausarRecusado, voltou.DespausarRecusado);
        Assert.NotEqual(padrao.Velocidade, voltou.Velocidade);
        Assert.NotEqual(padrao.VelocidadeAcordada, voltou.VelocidadeAcordada);
    }

    [Fact]
    public void SessaoComando_volta_inteiro()
    {
        var original = new SessaoComando
        {
            SessaoId = "s", Autor = "a", TickAlvo = 99, Ordem = 7,
            Payload = new byte[] { 3, 1, 4, 1, 5 },
        };

        var voltou = IdaEVolta(original, SessaoComando.Read);

        Assert.Equal(original.SessaoId, voltou.SessaoId);
        Assert.Equal(original.Autor, voltou.Autor);
        Assert.Equal(original.TickAlvo, voltou.TickAlvo);
        Assert.Equal(original.Ordem, voltou.Ordem);
        Assert.Equal(original.Payload, voltou.Payload);
    }
}
