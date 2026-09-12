using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Mundo;
using Xunit;

namespace WithFriends.Tests;

/// <summary>§2.1 — log de eventos append-only, ordenado pelo servidor.</summary>
public class MundoTests
{
    static byte[] Assentamento(string colonyId, string nome, int tile, int riqueza) =>
        new AssentamentoPayload
        {
            ColonyId = colonyId,
            Nome = nome,
            Tile = tile,
            Riqueza = riqueza,
        }.ParaBytes();

    [Fact]
    public void Seq_e_atribuido_pelo_servidor_e_e_monotonico()
    {
        var log = new LogDeEventos();

        var a = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 100, 5000));
        var b = log.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado, Assentamento("c2", "Cume", 200, 3000));
        var c = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoRemovido, Assentamento("c1", "Vale", 100, 0));

        Assert.Equal(1, a.Seq);
        Assert.Equal(2, b.Seq);
        Assert.Equal(3, c.Seq);
        Assert.Equal(3, log.UltimoSeq);
    }

    [Fact]
    public void Cada_cliente_consome_do_proprio_cursor()
    {
        // §2.1: dois jogadores no mesmo planeta, cada um no seu tempo.
        var log = new LogDeEventos();
        for (int i = 1; i <= 5; i++)
            log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento($"c{i}", $"Base {i}", i, i * 100));

        var atrasado = log.Desde(0);
        var emDia = log.Desde(4);

        Assert.Equal(5, atrasado.Count);
        Assert.Single(emDia);
        Assert.Equal(5, emDia[0].Seq);
        // Sempre em ordem: quem ordena é o servidor, não o relógio de ninguém.
        Assert.Equal(atrasado.OrderBy(e => e.Seq), atrasado);
    }

    [Fact]
    public void Cursor_a_frente_do_log_nao_devolve_nada()
    {
        var log = new LogDeEventos();
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 1, 10));

        Assert.Empty(log.Desde(99));
    }

    [Fact]
    public void Payload_atravessa_o_servidor_sem_ser_interpretado()
    {
        // §10: o coordenador ordena e entrega; quem entende de RimWorld é o
        // cliente. O payload volta byte a byte como entrou.
        var log = new LogDeEventos();
        var original = new AssentamentoPayload
        {
            ColonyId = "colonia-a",
            Nome = "Fazueli",
            Tile = 4821,
            Riqueza = 123_456,
        };

        var evento = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, original.ParaBytes());
        var lido = evento.Decodificar(AssentamentoPayload.Read);

        Assert.Equal(original.ColonyId, lido.ColonyId);
        Assert.Equal(original.Nome, lido.Nome);
        Assert.Equal(original.Tile, lido.Tile);
        Assert.Equal(original.Riqueza, lido.Riqueza);
    }

    [Fact]
    public void Primeiro_cliente_define_o_planeta_do_mundo()
    {
        var log = new LogDeEventos();

        Assert.Null(log.ConferirPlaneta("sha256:planeta-a"));
        Assert.Equal("sha256:planeta-a", log.Planeta);
        Assert.Null(log.ConferirPlaneta("sha256:planeta-a"));   // o mesmo, de novo
    }

    [Fact]
    public void Planeta_diferente_e_recusado_com_motivo_legivel()
    {
        // Tile é índice, não coordenada: 113533 em outro planeta aponta para
        // outro lugar ou não existe. Aplicar sem conferir quebra o jogo do
        // outro lado longe da causa.
        var log = new LogDeEventos();
        log.ConferirPlaneta("sha256:planeta-a");

        string motivo = log.ConferirPlaneta("sha256:planeta-b")!;

        Assert.Contains("mesma semente", motivo);
        Assert.Contains("índice de tile", motivo);
    }

    [Fact]
    public void Cliente_que_nao_declara_planeta_e_recusado()
    {
        Assert.NotNull(new LogDeEventos().ConferirPlaneta(""));
    }

    [Fact]
    public void Evento_sobrevive_ida_e_volta_na_fiacao()
    {
        var log = new LogDeEventos();
        var evento = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 42, 900));

        using var ms = new MemoryStream();
        new MundoEvento { Evento = evento }.ToEnvelope().WriteTo(ms);
        ms.Position = 0;

        var recebido = Envelope.ReadFrom(ms).Decode(MundoEvento.Read).Evento;

        Assert.Equal(evento.Seq, recebido.Seq);
        Assert.Equal(evento.Autor, recebido.Autor);
        Assert.Equal(evento.Tipo, recebido.Tipo);
        Assert.Equal("Vale", recebido.Decodificar(AssentamentoPayload.Read).Nome);
    }
}

public class LogDeEventosEmDiscoTests : IDisposable
{
    readonly string raiz = Path.Combine(Path.GetTempPath(), "withfriends-mundo-" + Guid.NewGuid().ToString("N"));

    string Caminho => Path.Combine(raiz, "mundo", "eventos.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true);
    }

    [Fact]
    public void O_planeta_sobrevive_a_reinicio_do_coordenador()
    {
        var antes = new LogDeEventos(Caminho);
        antes.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c1", Nome = "Vale", Tile = 10, Riqueza = 500 }.ParaBytes());
        antes.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c2", Nome = "Cume", Tile = 20, Riqueza = 700 }.ParaBytes());

        var depois = new LogDeEventos(Caminho);

        Assert.Equal(2, depois.Contagem);
        Assert.Equal(2, depois.UltimoSeq);
        // A numeração continua de onde parou: seq nunca se repete.
        Assert.Equal(3, depois.Publicar("jogador-1", TipoEventoMundo.AssentamentoRemovido, Array.Empty<byte>()).Seq);
        Assert.Equal("Cume", depois.Desde(1)[0].Decodificar(AssentamentoPayload.Read).Nome);
    }

    [Fact]
    public void Planeta_do_mundo_sobrevive_a_reinicio()
    {
        var antes = new LogDeEventos(Caminho);
        antes.ConferirPlaneta("sha256:planeta-a");

        var depois = new LogDeEventos(Caminho);

        Assert.Equal("sha256:planeta-a", depois.Planeta);
        Assert.NotNull(depois.ConferirPlaneta("sha256:planeta-b"));
    }

    [Fact]
    public void Linha_corrompida_nao_invalida_o_log()
    {
        var log = new LogDeEventos(Caminho);
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c1", Nome = "Vale", Tile = 10, Riqueza = 500 }.ParaBytes());

        File.AppendAllText(Caminho, "{ isto não é json válido\n");
        log.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c2", Nome = "Cume", Tile = 20, Riqueza = 700 }.ParaBytes());

        var recarregado = new LogDeEventos(Caminho);

        Assert.Equal(2, recarregado.Contagem);
    }
}
