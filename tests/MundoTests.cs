using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Mundo;
using Xunit;

namespace WithFriends.Tests;

/// <summary>§2.1 — log de eventos append-only, ordenado pelo servidor.</summary>
public class MundoTests
{
    const string Terra = "sha256:planeta-a";
    const string Marte = "sha256:planeta-b";

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

        var a = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 100, 5000), Terra);
        var b = log.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado, Assentamento("c2", "Cume", 200, 3000), Terra);
        var c = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoRemovido, Assentamento("c1", "Vale", 100, 0), Terra);

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
            log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento($"c{i}", $"Base {i}", i, i * 100), Terra);

        var atrasado = log.Desde(0, Terra);
        var emDia = log.Desde(4, Terra);

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
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 1, 10), Terra);

        Assert.Empty(log.Desde(99, Terra));
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

        var evento = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, original.ParaBytes(), Terra);
        var lido = evento.Decodificar(AssentamentoPayload.Read);

        Assert.Equal(original.ColonyId, lido.ColonyId);
        Assert.Equal(original.Nome, lido.Nome);
        Assert.Equal(original.Tile, lido.Tile);
        Assert.Equal(original.Riqueza, lido.Riqueza);
    }

    [Fact]
    public void Cada_planeta_so_recebe_os_proprios_fatos()
    {
        // Tile é índice, não coordenada: 113533 em outro planeta aponta para
        // outro lugar ou não existe. Entregar fato de um planeta a quem está em
        // outro quebra o jogo do outro lado longe da causa.
        var log = new LogDeEventos();
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 100, 5000), Terra);
        log.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado, Assentamento("c2", "Cume", 200, 3000), Marte);
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c3", "Foz", 300, 100), Terra);

        Assert.Equal(new long[] { 1, 3 }, log.Desde(0, Terra).Select(e => e.Seq));
        Assert.Equal(new long[] { 2 }, log.Desde(0, Marte).Select(e => e.Seq));
    }

    [Fact]
    public void Seq_e_global_e_nao_reinicia_por_planeta()
    {
        // A numeração atravessa planetas de propósito: assim o cursor de um
        // jogador continua valendo mesmo que o coordenador passe a hospedar
        // outro planeta no meio do caminho.
        var log = new LogDeEventos();
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Array.Empty<byte>(), Terra);
        var emMarte = log.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado, Array.Empty<byte>(), Marte);

        Assert.Equal(2, emMarte.Seq);
    }

    [Fact]
    public void Planeta_desconhecido_nao_recebe_nada()
    {
        var log = new LogDeEventos();
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 1, 10), Terra);

        Assert.Empty(log.Desde(0, "sha256:planeta-que-ninguem-gerou"));
    }

    [Fact]
    public void Evento_sobrevive_ida_e_volta_na_fiacao()
    {
        var log = new LogDeEventos();
        var evento = log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Assentamento("c1", "Vale", 42, 900), Terra);

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
    const string Terra = "sha256:planeta-a";
    const string Marte = "sha256:planeta-b";

    readonly string raiz = Path.Combine(Path.GetTempPath(), "withfriends-mundo-" + Guid.NewGuid().ToString("N"));

    string Caminho => Path.Combine(raiz, "mundo", "eventos.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true);
    }

    [Fact]
    public void O_log_sobrevive_a_reinicio_do_coordenador()
    {
        var antes = new LogDeEventos(Caminho);
        antes.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c1", Nome = "Vale", Tile = 10, Riqueza = 500 }.ParaBytes(), Terra);
        antes.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c2", Nome = "Cume", Tile = 20, Riqueza = 700 }.ParaBytes(), Terra);

        var depois = new LogDeEventos(Caminho);

        Assert.Equal(2, depois.Contagem);
        Assert.Equal(2, depois.UltimoSeq);
        // A numeração continua de onde parou: seq nunca se repete.
        Assert.Equal(3, depois.Publicar("jogador-1", TipoEventoMundo.AssentamentoRemovido, Array.Empty<byte>(), Terra).Seq);
        Assert.Equal("Cume", depois.Desde(1, Terra)[0].Decodificar(AssentamentoPayload.Read).Nome);
    }

    [Fact]
    public void O_planeta_de_cada_fato_sobrevive_a_reinicio()
    {
        var antes = new LogDeEventos(Caminho);
        antes.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado, Array.Empty<byte>(), Terra);
        antes.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado, Array.Empty<byte>(), Marte);

        var depois = new LogDeEventos(Caminho);

        Assert.Single(depois.Desde(0, Terra));
        Assert.Single(depois.Desde(0, Marte));
        Assert.Equal(2, depois.PlanetasComEventos.Count);
    }

    [Fact]
    public void Linha_antiga_sem_planeta_herda_o_planeta_de_registro()
    {
        // Migração: o log guardava um planeta só, num arquivo à parte, e as
        // linhas não diziam a qual pertenciam. Elas pertencem àquele.
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Caminho)!, "planeta.txt"), Terra);
        File.WriteAllText(Caminho,
            "{\"Seq\":1,\"Autor\":\"jogador-1\",\"Tipo\":1,\"Payload\":\"\",\"TimestampLogico\":0}\n");

        var log = new LogDeEventos(Caminho);

        Assert.Single(log.Desde(0, Terra));
        Assert.Empty(log.Desde(0, Marte));
        Assert.Equal(Terra, log.PlanetaDeRegistro);
    }

    [Fact]
    public void Linha_corrompida_nao_invalida_o_log()
    {
        var log = new LogDeEventos(Caminho);
        log.Publicar("jogador-1", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c1", Nome = "Vale", Tile = 10, Riqueza = 500 }.ParaBytes(), Terra);

        File.AppendAllText(Caminho, "{ isto não é json válido\n");
        log.Publicar("jogador-2", TipoEventoMundo.AssentamentoPublicado,
            new AssentamentoPayload { ColonyId = "c2", Nome = "Cume", Tile = 20, Riqueza = 700 }.ParaBytes(), Terra);

        var recarregado = new LogDeEventos(Caminho);

        Assert.Equal(2, recarregado.Contagem);
    }
}
