using System;
using System.IO;

namespace WithFriends.Protocol.Messages;

/// <summary>
/// Tipo de fato registrado no log de mundo. O servidor **não interpreta** o
/// payload (§10): ele ordena e entrega. Quem entende de RimWorld é o cliente.
/// </summary>
public enum TipoEventoMundo : ushort
{
    AssentamentoPublicado = 1,
    AssentamentoRemovido = 2,
}

/// <summary>
/// Um fato no log append-only do mundo — §2.1:
/// <c>evento := { seq, autor, tipo, payload, timestamp_logico }</c>
///
/// Não existe "estado do mundo" que se sobrescreve; existe uma sequência de
/// fatos. A ordenação vem do servidor, então conflito de ordem não existe por
/// construção.
/// </summary>
public sealed class EventoMundo
{
    /// <summary>Ordem global, atribuída pelo servidor. Monotônica.</summary>
    public long Seq { get; init; }
    /// <summary>Quem afirmou o fato. Sempre um <c>player_id</c>.</summary>
    public string Autor { get; init; } = "";
    public TipoEventoMundo Tipo { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
    /// <summary>Relógio lógico do servidor. Diagnóstico; quem ordena é <see cref="Seq"/>.</summary>
    public long TimestampLogico { get; init; }

    public void Write(BinaryWriter w)
    {
        w.Write(Seq);
        w.Write(Autor);
        w.Write((ushort)Tipo);
        w.Write(Payload.Length);
        w.Write(Payload);
        w.Write(TimestampLogico);
    }

    public static EventoMundo Read(BinaryReader r)
    {
        long seq = r.ReadInt64();
        string autor = r.ReadString();
        var tipo = (TipoEventoMundo)r.ReadUInt16();
        int tamanho = r.ReadInt32();
        byte[] payload = r.ReadBytes(tamanho);
        return new EventoMundo
        {
            Seq = seq,
            Autor = autor,
            Tipo = tipo,
            Payload = payload,
            TimestampLogico = r.ReadInt64(),
        };
    }

    public T Decodificar<T>(Func<BinaryReader, T> ler)
    {
        using var ms = new MemoryStream(Payload, writable: false);
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);
        return ler(r);
    }

    public static byte[] Codificar(Action<BinaryWriter> escrever)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            escrever(w);
        return ms.ToArray();
    }
}

/// <summary>
/// Assentamento visível no mapa-mundo. Só o que a §4 permite ver sem presença
/// física: nome, riqueza estimada, onde fica. Nada de conteúdo de mapa.
/// </summary>
public readonly struct AssentamentoPayload
{
    public string ColonyId { get; init; }
    public string Nome { get; init; }
    /// <summary>Tile do planeta. O mundo é o mesmo para todos.</summary>
    public int Tile { get; init; }
    /// <summary>Riqueza estimada — §4: aproximada de propósito.</summary>
    public int Riqueza { get; init; }

    public void Write(BinaryWriter w)
    {
        w.Write(ColonyId ?? "");
        w.Write(Nome ?? "");
        w.Write(Tile);
        w.Write(Riqueza);
    }

    public static AssentamentoPayload Read(BinaryReader r) => new()
    {
        ColonyId = r.ReadString(),
        Nome = r.ReadString(),
        Tile = r.ReadInt32(),
        Riqueza = r.ReadInt32(),
    };

    public byte[] ParaBytes() => EventoMundo.Codificar(Write);
}

/// <summary>mundo.evento — servidor → cliente, já ordenado.</summary>
public sealed class MundoEvento : IMessage
{
    public EventoMundo Evento { get; init; } = new();

    public MessageId Id => MessageId.MundoEvento;

    public void Write(BinaryWriter w) => Evento.Write(w);

    public static MundoEvento Read(BinaryReader r) => new() { Evento = EventoMundo.Read(r) };
}

/// <summary>
/// mundo.publicar — cliente → servidor, propondo um fato.
/// Sem <c>seq</c>: quem numera é o servidor, e é isso que elimina conflito
/// de ordem (§2.1).
/// </summary>
public sealed class MundoPublicar : IMessage
{
    public TipoEventoMundo Tipo { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public MessageId Id => MessageId.MundoPublicar;

    public void Write(BinaryWriter w)
    {
        w.Write((ushort)Tipo);
        w.Write(Payload.Length);
        w.Write(Payload);
    }

    public static MundoPublicar Read(BinaryReader r)
    {
        var tipo = (TipoEventoMundo)r.ReadUInt16();
        int tamanho = r.ReadInt32();
        return new MundoPublicar { Tipo = tipo, Payload = r.ReadBytes(tamanho) };
    }
}

/// <summary>
/// mundo.sincronizacao_cursor — cliente → servidor: "estou no <c>seq</c> tal,
/// me manda o que veio depois". Cada cliente consome no próprio tempo (§2.1).
/// </summary>
public sealed class MundoSincronizacaoCursor : IMessage
{
    public long Cursor { get; init; }

    /// <summary>
    /// Identidade do planeta deste cliente — hash das variáveis de geração.
    ///
    /// O mundo compartilhado pressupõe **o mesmo planeta**: um evento diz
    /// "tile 113533", e tile é índice, não coordenada. Em outro planeta esse
    /// índice aponta para outro lugar ou nem existe. Não é preciso sincronizar
    /// o planeta — ele é determinístico a partir da semente e das opções de
    /// geração. Basta conferir que são as mesmas.
    /// </summary>
    public string Planeta { get; init; } = "";

    /// <summary>
    /// O mesmo planeta em português — semente, cobertura, chuva, temperatura.
    ///
    /// <para>O hash diz se dois planetas são o mesmo; ele não diz <b>como
    /// gerar</b> o do outro. Sem esta linha, "vocês estão em planetas
    /// diferentes" é um aviso que ninguém consegue atender.</para>
    /// </summary>
    public string PlanetaLegivel { get; init; } = "";

    public MessageId Id => MessageId.MundoSincronizacaoCursor;

    public void Write(BinaryWriter w)
    {
        w.Write(Cursor);
        w.Write(Planeta);
        w.Write(PlanetaLegivel);
    }

    public static MundoSincronizacaoCursor Read(BinaryReader r) => new()
    {
        Cursor = r.ReadInt64(),
        Planeta = r.ReadString(),
        PlanetaLegivel = r.ReadString(),
    };
}

/// <summary>
/// mundo.presenca — quem está online agora. Estado volátil, **não** vai para
/// o log append-only: presença não é um fato histórico do planeta.
/// </summary>
public sealed class MundoPresenca : IMessage
{
    public string PlayerId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Online { get; init; }

    public MessageId Id => MessageId.MundoPresenca;

    public void Write(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(DisplayName);
        w.Write(Online);
    }

    public static MundoPresenca Read(BinaryReader r) => new()
    {
        PlayerId = r.ReadString(),
        DisplayName = r.ReadString(),
        Online = r.ReadBoolean(),
    };
}
