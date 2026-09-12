using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WithFriends.Protocol;

/// <summary>
/// Enquadramento na fiação: <c>[ushort id][byte flags][int tamanho][payload]</c>.
///
/// O tamanho é explícito para que um receptor consiga pular uma mensagem que
/// não conhece em vez de perder o sincronismo do fluxo (§9.1).
///
/// O payload grande viaja comprimido, decidido por mensagem: saves de RimWorld
/// encolhem ~10× e são o que de fato trafega aqui. A compressão é do
/// **transporte**, não da mensagem — quem escreve a mensagem não precisa saber
/// que ela é grande.
/// </summary>
public readonly struct Envelope
{
    /// <summary>
    /// Teto do payload **cru**. Um checkpoint de colônia madura chegou a
    /// 17,9 MB nos testes, então 16 MiB era pequeno demais: a mensagem saía do
    /// cliente e o servidor a recusava, derrubando a conexão sem explicação.
    /// </summary>
    public const int MaxPayloadBytes = 256 * 1024 * 1024;

    /// <summary>A partir daqui vale a pena tentar comprimir.</summary>
    public const int LimiarDeCompressao = 64 * 1024;

    /// <summary>[ushort id][byte flags][int tamanho]</summary>
    public const int HeaderBytes = 7;

    const byte FlagComprimido = 1;

    public readonly MessageId Id;
    public readonly byte[] Payload;

    public Envelope(MessageId id, byte[] payload)
    {
        Id = id;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>
    /// Escreve cabeçalho e payload numa única chamada: uma mensagem é um
    /// write só, não três — evita fragmentar cada mensagem em segmentos TCP.
    /// </summary>
    public void WriteTo(Stream stream)
    {
        if (Payload.Length > MaxPayloadBytes)
            throw new InvalidDataException(
                $"Payload de {Payload.Length:N0} bytes excede o teto de {MaxPayloadBytes:N0}. " +
                "Recusar aqui é melhor do que escrever um quadro que o outro lado vai rejeitar, " +
                "derrubando a conexão sem explicação.");

        byte[] corpo = Payload;
        byte flags = 0;

        if (Payload.Length >= LimiarDeCompressao)
        {
            byte[] comprimido = Comprimir(Payload);
            if (comprimido.Length < Payload.Length)
            {
                corpo = comprimido;
                flags |= FlagComprimido;
            }
        }

        var frame = new byte[HeaderBytes + corpo.Length];
        frame[0] = (byte)((ushort)Id & 0xFF);
        frame[1] = (byte)((ushort)Id >> 8);
        frame[2] = flags;
        frame[3] = (byte)(corpo.Length & 0xFF);
        frame[4] = (byte)((corpo.Length >> 8) & 0xFF);
        frame[5] = (byte)((corpo.Length >> 16) & 0xFF);
        frame[6] = (byte)((corpo.Length >> 24) & 0xFF);
        Buffer.BlockCopy(corpo, 0, frame, HeaderBytes, corpo.Length);

        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }

    public static Envelope ReadFrom(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var id = (MessageId)r.ReadUInt16();
        byte flags = r.ReadByte();
        int length = r.ReadInt32();
        if (length < 0 || length > MaxPayloadBytes)
            throw new InvalidDataException($"Tamanho de payload inválido: {length}");

        byte[] corpo = ReadExactly(r, length);
        return new Envelope(id, (flags & FlagComprimido) != 0 ? Descomprimir(corpo) : corpo);
    }

    static byte[] Comprimir(byte[] conteudo)
    {
        using var destino = new MemoryStream();
        using (var gzip = new GZipStream(destino, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(conteudo, 0, conteudo.Length);
        return destino.ToArray();
    }

    static byte[] Descomprimir(byte[] comprimido)
    {
        using var origem = new MemoryStream(comprimido, writable: false);
        using var gzip = new GZipStream(origem, CompressionMode.Decompress);
        using var destino = new MemoryStream(comprimido.Length * 4);
        gzip.CopyTo(destino);
        return destino.ToArray();
    }

    static byte[] ReadExactly(BinaryReader r, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = r.Read(buffer, read, count - read);
            if (n == 0) throw new EndOfStreamException($"Esperava {count} bytes, recebi {read}.");
            read += n;
        }
        return buffer;
    }
}

/// <summary>Mensagem que sabe se serializar. Sem reflexão, sempre explícito.</summary>
public interface IMessage
{
    MessageId Id { get; }
    void Write(BinaryWriter w);
}

public static class MessageCodec
{
    public static Envelope ToEnvelope(this IMessage message)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            message.Write(w);
        return new Envelope(message.Id, ms.ToArray());
    }

    public static T Decode<T>(this Envelope envelope, Func<BinaryReader, T> read)
    {
        using var ms = new MemoryStream(envelope.Payload, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return read(r);
    }
}
