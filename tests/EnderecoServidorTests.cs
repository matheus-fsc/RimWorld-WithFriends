using WithFriends.Transport;
using Xunit;

namespace WithFriends.Tests;

/// <summary>
/// §17.3 regra 2 — literal IPv6 entre colchetes tem que funcionar digitado e
/// colado. Parsing nunca por `split(':')`. Falha real: RT #315.
/// </summary>
public class EnderecoServidorTests
{
    [Theory]
    [InlineData("[2804:14c::1]:25555", "2804:14c::1", 25555)]
    [InlineData("[::1]:25600", "::1", 25600)]
    [InlineData("[2804:14c:5b41:8f81:cb25:88b4:8100:6b8e]:9999", "2804:14c:5b41:8f81:cb25:88b4:8100:6b8e", 9999)]
    [InlineData("[::1]", "::1", 25555)]
    [InlineData("::1", "::1", 25555)]
    [InlineData("2804:14c::1", "2804:14c::1", 25555)]
    [InlineData("192.168.0.4:25555", "192.168.0.4", 25555)]
    [InlineData("192.168.0.4", "192.168.0.4", 25555)]
    [InlineData("servidor.exemplo.com:1234", "servidor.exemplo.com", 1234)]
    [InlineData("servidor.exemplo.com", "servidor.exemplo.com", 25555)]
    [InlineData("  [::1]:25600  ", "::1", 25600)]
    public void Aceita_as_formas_que_um_jogador_realmente_digita(string texto, string host, int porta)
    {
        Assert.True(EnderecoServidor.TentarAnalisar(texto, out var endereco, out string erro), erro);
        Assert.Equal(host, endereco.Host);
        Assert.Equal(porta, endereco.Porta);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[2804:14c::1:25555")]      // falta ']'
    [InlineData("[naoehipv6]:25555")]
    [InlineData("servidor:porta")]
    [InlineData("servidor:70000")]
    [InlineData("servidor:0")]
    [InlineData("1:25555")]      // IPv6 digitado sem colchetes, ':' comidos
    [InlineData("12345:25555")]
    public void Recusa_com_motivo_legivel(string texto)
    {
        Assert.False(EnderecoServidor.TentarAnalisar(texto, out _, out string erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Host_todo_numerico_sugere_os_colchetes()
    {
        Assert.False(EnderecoServidor.TentarAnalisar("1:25555", out _, out string erro));
        Assert.Contains("[::1]:25555", erro);
    }

    [Fact]
    public void Round_trip_preserva_os_colchetes_do_IPv6()
    {
        var endereco = EnderecoServidor.Analisar("[2804:14c::1]:25555");
        Assert.Equal("[2804:14c::1]:25555", endereco.ToString());
        Assert.Equal(endereco.Host, EnderecoServidor.Analisar(endereco.ToString()).Host);
    }

    [Fact]
    public void Endereco_IPv4_nao_ganha_colchetes()
    {
        Assert.Equal("192.168.0.4:25555", EnderecoServidor.Analisar("192.168.0.4:25555").ToString());
    }
}
