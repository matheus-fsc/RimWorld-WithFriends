using WithFriends.Protocol.Determinismo;
using Xunit;

namespace WithFriends.Tests;

/// <summary>
/// §14.3 — impressão digital por estado de RNG, com motivo legível.
/// A comparação nunca devolve booleano: devolve o que houve e onde.
/// </summary>
public class ImpressaoDigitalTests
{
    static OpiniaoDeSincronia Opiniao(long tickInicial = 100, params uint[] mundo)
    {
        var opiniao = new OpiniaoDeSincronia { TickInicial = tickInicial, TickFinal = tickInicial + mundo.Length };
        opiniao.EstadosDoMundo.AddRange(mundo);
        return opiniao;
    }

    [Fact]
    public void Opinioes_iguais_nao_acusam_nada()
    {
        var uma = Opiniao(100, 1, 2, 3);
        var outra = Opiniao(100, 1, 2, 3);
        uma.EstadosDoMapa(1).AddRange(new uint[] { 10, 20 });
        outra.EstadosDoMapa(1).AddRange(new uint[] { 10, 20 });

        Assert.Null(uma.Comparar(outra));
        Assert.Equal(uma.Resumo(), outra.Resumo());
    }

    [Fact]
    public void Modo_de_arredondamento_e_conferido_primeiro()
    {
        // Decisão 3: é a armadilha mais barata de diagnosticar e a mais cara
        // de descobrir depois. Vem antes de qualquer estado de RNG.
        var uma = Opiniao(100, 1, 2, 3);
        var outra = Opiniao(100, 9, 9, 9);   // RNG também diverge
        outra.ModoDeArredondamento = ModoDeArredondamentoFP.ParaZero;

        string motivo = uma.Comparar(outra)!;

        Assert.Contains("arredondamento", motivo);
        Assert.DoesNotContain("RNG", motivo);
    }

    [Fact]
    public void Divergencia_no_mapa_diz_qual_mapa_e_qual_tick()
    {
        // Decisão 2: granularidade por mapa — saber ONDE divergiu.
        var uma = Opiniao(1000);
        var outra = Opiniao(1000);
        uma.EstadosDoMapa(7).AddRange(new uint[] { 5, 5, 5, 5 });
        outra.EstadosDoMapa(7).AddRange(new uint[] { 5, 5, 9, 5 });

        string motivo = uma.Comparar(outra)!;

        Assert.Contains("mapa 7", motivo);
        Assert.Contains("tick 1002", motivo);   // 1000 + índice 2
    }

    [Fact]
    public void Divergencia_no_mundo_diz_o_tick()
    {
        var uma = Opiniao(500, 1, 2, 3, 4);
        var outra = Opiniao(500, 1, 2, 99, 4);

        Assert.Contains("tick 502", uma.Comparar(outra)!);
    }

    [Fact]
    public void Mapas_diferentes_sao_detectados_antes_do_RNG()
    {
        var uma = Opiniao(100);
        var outra = Opiniao(100);
        uma.EstadosDoMapa(1).Add(5);
        outra.EstadosDoMapa(2).Add(5);

        Assert.Contains("mapas em jogo não batem", uma.Comparar(outra)!);
    }

    [Fact]
    public void Comandos_aplicados_em_ordem_diferente_sao_detectados()
    {
        var uma = Opiniao(100, 1, 2);
        var outra = Opiniao(100, 1, 2);
        uma.EstadosDeComandos.AddRange(new uint[] { 10, 20 });
        outra.EstadosDeComandos.AddRange(new uint[] { 20, 10 });

        Assert.Contains("ordem diferente", uma.Comparar(outra)!);
    }

    [Fact]
    public void Intervalo_truncado_conta_como_divergencia()
    {
        // Um lado simulou menos do que afirmou: não dá para chamar de igual.
        var uma = Opiniao(100, 1, 2, 3);
        var outra = Opiniao(100, 1, 2);

        Assert.Contains("tick 102", uma.Comparar(outra)!);
    }

    [Fact]
    public void Resumo_muda_quando_qualquer_parte_muda()
    {
        var baseline = Opiniao(100, 1, 2, 3);
        baseline.EstadosDoMapa(1).Add(7);

        var mudouMapa = Opiniao(100, 1, 2, 3);
        mudouMapa.EstadosDoMapa(1).Add(8);

        var mudouMundo = Opiniao(100, 1, 2, 4);
        mudouMundo.EstadosDoMapa(1).Add(7);

        var mudouModo = Opiniao(100, 1, 2, 3);
        mudouModo.EstadosDoMapa(1).Add(7);
        mudouModo.ModoDeArredondamento = ModoDeArredondamentoFP.ParaCima;

        var resumos = new[] { baseline.Resumo(), mudouMapa.Resumo(), mudouMundo.Resumo(), mudouModo.Resumo() };

        Assert.Equal(4, resumos.Distinct().Count());
    }

    [Fact]
    public void Resumo_do_mesmo_estado_no_mesmo_tick_e_sempre_igual()
    {
        // A digital é função do estado no tick — não de quantas vezes o lado
        // reportou. Quando ela acumulava entre relatos, quem esperava mais
        // tempo parado no mesmo tick mandava um resumo diferente de quem
        // acabava de chegar, e a visita abortava sem que nada no jogo
        // estivesse diferente.
        OpiniaoDeSincronia NoTick(long tick, uint estado)
        {
            var opiniao = new OpiniaoDeSincronia { TickInicial = tick, TickFinal = tick };
            opiniao.EstadosDoMapa(0).Add(estado);
            return opiniao;
        }

        string umLado = NoTick(0, 7).Resumo();
        string outroLado = NoTick(0, 7).Resumo();

        Assert.Equal(umLado, outroLado);
        Assert.NotEqual(umLado, NoTick(1, 7).Resumo());   // tick diferente
        Assert.NotEqual(umLado, NoTick(0, 8).Resumo());   // estado diferente
    }

    [Fact]
    public void Numero_de_amostras_diferente_muda_o_resumo()
    {
        // O contrário do teste acima, e a razão de ele existir: acumular
        // amostras em ritmos diferentes produz resumos diferentes para o mesmo
        // estado. Por isso a amostragem passou a ser uma por relato.
        var um = new OpiniaoDeSincronia { TickInicial = 0, TickFinal = 0 };
        um.EstadosDoMapa(0).Add(7);

        var outro = new OpiniaoDeSincronia { TickInicial = 0, TickFinal = 0 };
        outro.EstadosDoMapa(0).Add(7);
        outro.EstadosDoMapa(0).Add(7);

        Assert.NotEqual(um.Resumo(), outro.Resumo());
    }

    [Fact]
    public void Opiniao_sobrevive_ida_e_volta_na_fiacao()
    {
        var original = Opiniao(4242, 1, 2, 3);
        original.EstadosDoMapa(3).AddRange(new uint[] { 9, 8, 7 });
        original.EstadosDeComandos.Add(55);
        original.HashesDeStackTrace.Add(-12345);
        original.ModoDeArredondamento = ModoDeArredondamentoFP.ParaBaixo;

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            original.Write(w);
        ms.Position = 0;
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);

        var lida = OpiniaoDeSincronia.Read(r);

        Assert.Null(original.Comparar(lida));
        Assert.Equal(original.Resumo(), lida.Resumo());
        Assert.Equal(original.HashesDeStackTrace, lida.HashesDeStackTrace);
    }

    [Fact]
    public void Esta_maquina_arredonda_para_o_mais_proximo()
    {
        // Se isto falhar, algo no processo mexeu no modo de FP — e aí
        // determinismo entre máquinas está comprometido de saída.
        Assert.Equal(ModoDeArredondamentoFP.MaisProximo, ModoDeArredondamento.Atual());
    }
}
