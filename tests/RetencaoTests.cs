using WithFriends.Server.Colonias;
using Xunit;

namespace WithFriends.Tests;

/// <summary>§7.2 — escadinha de retenção, não janela fixa.</summary>
public class RetencaoTests
{
    static readonly DateTime Agora = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    static EntradaCheckpoint Em(DateTime quando, bool preSessao = false, bool suspeito = false) =>
        new()
        {
            RecebidoEm = quando,
            GameTick = quando.Ticks,
            ContentHash = "sha256:" + quando.Ticks,
            PreSessao = preSessao,
            Suspeito = suspeito,
        };

    [Fact]
    public void Ultimas_24h_ficam_de_hora_em_hora()
    {
        // Um checkpoint a cada 10 minutos durante 24h: 144 entradas.
        var historico = Enumerable.Range(0, 144)
            .Select(i => Em(Agora.AddMinutes(-10 * i)))
            .ToList();

        var mantidos = Retencao.Manter(historico, Agora);

        // 24h de baldes horários, mais o balde da hora corrente.
        Assert.InRange(mantidos.Count, 24, 25);
        // O mais recente sempre fica.
        Assert.Contains(historico[0], mantidos);
    }

    [Fact]
    public void Ultimos_7_dias_ficam_dia_a_dia_e_o_resto_sai()
    {
        // 30 dias, 4 checkpoints por dia.
        var historico = Enumerable.Range(0, 30)
            .SelectMany(d => Enumerable.Range(0, 4).Select(h => Em(Agora.AddDays(-d).AddHours(-6 * h))))
            .ToList();

        var mantidos = Retencao.Manter(historico, Agora);
        var descartados = Retencao.Descartar(historico, Agora);

        Assert.Equal(historico.Count, mantidos.Count + descartados.Count);
        // Nada com mais de 7 dias sobrevive (salvo pré-sessão, que não há aqui).
        Assert.All(mantidos, e => Assert.True(Agora - e.RecebidoEm <= TimeSpan.FromDays(7)));
        // E a cauda antiga realmente foi descartada.
        Assert.Contains(descartados, e => Agora - e.RecebidoEm > TimeSpan.FromDays(20));
    }

    [Fact]
    public void Tres_pre_sessao_mais_recentes_ficam_para_sempre()
    {
        var historico = new List<EntradaCheckpoint>
        {
            Em(Agora.AddDays(-400), preSessao: true),
            Em(Agora.AddDays(-300), preSessao: true),
            Em(Agora.AddDays(-200), preSessao: true),
            Em(Agora.AddDays(-100), preSessao: true), // quarto mais antigo: sai
            Em(Agora.AddDays(-100)),
        };

        var mantidos = Retencao.Manter(historico, Agora);
        var preSessaoMantidos = mantidos.Where(e => e.PreSessao).ToList();

        Assert.Equal(Retencao.PreSessoesSempreMantidas, preSessaoMantidos.Count);
        // Os três mais recentes, não três quaisquer.
        Assert.Equal(
            historico.Where(e => e.PreSessao).OrderByDescending(e => e.RecebidoEm).Take(3).ToList(),
            preSessaoMantidos.OrderByDescending(e => e.RecebidoEm).ToList());
    }

    [Fact]
    public void Checkpoint_suspeito_nao_e_o_primeiro_a_ser_descartado()
    {
        // §15.7: numa investigação real o backup quase expirou antes de a
        // causa ser encontrada. Evidência fica.
        var suspeito = Em(Agora.AddDays(-3).AddMinutes(-17), suspeito: true);
        var historico = Enumerable.Range(0, 200)
            .Select(i => Em(Agora.AddMinutes(-30 * i)))
            .Append(suspeito)
            .ToList();

        Assert.Contains(suspeito, Retencao.Manter(historico, Agora));
        Assert.DoesNotContain(suspeito, Retencao.Descartar(historico, Agora));
    }

    [Fact]
    public void Historico_de_seis_horas_nao_e_suficiente()
    {
        // O contraexemplo da §15.7: 6 backups de hora em hora dão 6 horas.
        // A escadinha, com a mesma quantidade de entradas gravadas, alcança dias.
        var historico = Enumerable.Range(0, 500)
            .Select(i => Em(Agora.AddMinutes(-20 * i)))
            .ToList();

        var mantidos = Retencao.Manter(historico, Agora);
        var maisAntigo = mantidos.Min(e => e.RecebidoEm);

        Assert.True(Agora - maisAntigo > TimeSpan.FromDays(6),
            $"a janela coberta foi de apenas {Agora - maisAntigo}");
    }
}
