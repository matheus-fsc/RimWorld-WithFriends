using System.IO;

namespace WithFriends.Protocol.Messages;

public enum CodigoErro
{
    /// <summary>Outra conexão assumiu esta identidade — §7.1 regra 5.</summary>
    IdentidadeAssumidaPorOutraConexao = 1,
    /// <summary>O servidor não conseguiu processar a mensagem, mas segue vivo.</summary>
    FalhaAoProcessar = 2,

    /// <summary>
    /// Este cliente é o único no planeta dele, e há gente online em outro.
    ///
    /// <para>Não é recusa: o planeta pertence ao fato, e cada cliente recebe os
    /// eventos do seu (ADR 0021). É aviso — quem está sozinho no próprio
    /// planeta não vê ninguém no mapa-mundo, e sem isto pareceria defeito. A
    /// explicação traz a descrição do planeta dos outros, que é o que permite
    /// regerar e encontrar.</para>
    ///
    /// <para>Mandado uma vez por planeta, não a cada sincronização.</para>
    /// </summary>
    SozinhoNoPlaneta = 3,

    /// <summary>
    /// O comando é decisão da colônia, e quem propôs está visitando. Ver
    /// <see cref="AutoridadeDeComando"/>. A sessão segue normalmente — só este
    /// comando não acontece.
    /// </summary>
    SemAutoridade = 4,
}

/// <summary>
/// sistema.erro — o servidor precisa dizer algo grave sem derrubar a conexão
/// em silêncio (§9.1). Toda recusa e toda falha trazem motivo legível.
/// </summary>
public sealed class SistemaErro : IMessage
{
    public CodigoErro Codigo { get; init; }
    public string Explicacao { get; init; } = "";

    public MessageId Id => MessageId.Erro;

    public void Write(BinaryWriter w)
    {
        w.Write((int)Codigo);
        w.Write(Explicacao);
    }

    public static SistemaErro Read(BinaryReader r) => new()
    {
        Codigo = (CodigoErro)r.ReadInt32(),
        Explicacao = r.ReadString(),
    };
}
