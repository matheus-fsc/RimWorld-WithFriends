namespace WithFriends.Protocol;

/// <summary>Famílias de mensagem — §9.2.</summary>
public enum MessageFamily : byte
{
    Sistema = 0,
    Mundo = 1,
    Colonia = 2,
    Sessao = 3,
    Mercado = 4,
}

/// <summary>
/// Identificador estável de mensagem. O valor numérico é wire format:
/// nunca reordenar, nunca reutilizar um valor aposentado.
/// Mensagem desconhecida é ignorada com log, nunca derruba a conexão (§9.1).
/// </summary>
public enum MessageId : ushort
{
    // sistema.*
    Handshake = 1,
    HandshakeAceito = 2,
    HandshakeRecusado = 3,
    Erro = 4,

    // mundo.*
    MundoEvento = 100,
    MundoSincronizacaoCursor = 101,
    MundoPublicar = 102,
    MundoPresenca = 103,

    // colonia.*
    ColoniaCheckpoint = 200,
    ColoniaHeartbeat = 201,
    ColoniaRestauracao = 202,
    ColoniaAlerta = 203,

    // sessao.*
    SessaoConvite = 300,
    SessaoAceite = 301,
    SessaoRecusa = 302,
    SessaoInicio = 303,
    SessaoComando = 304,
    SessaoBarreira = 305,
    SessaoFim = 306,
    SessaoAborto = 307,
    SessaoMapa = 308,
    SessaoPartida = 309,
    SessaoRessincronizar = 310,

    // mercado.*
    MercadoAnuncio = 400,
    MercadoCompra = 401,
    MercadoEntrega = 402,
    MercadoEscrow = 403,
}

public static class MessageIdExtensions
{
    public static MessageFamily Family(this MessageId id) => (ushort)id switch
    {
        < 100 => MessageFamily.Sistema,
        < 200 => MessageFamily.Mundo,
        < 300 => MessageFamily.Colonia,
        < 400 => MessageFamily.Sessao,
        _ => MessageFamily.Mercado,
    };
}
