using System;
using System.IO;

namespace WithFriends.Protocol.Messages;

/// <summary>
/// Metadados verificáveis que todo checkpoint carrega — §7.1 regra 2.
/// Sem eles não há como distinguir "salvou de novo" de "reenviou o mesmo
/// estado congelado com carimbo novo", que é a falha da §15.1.
/// </summary>
public readonly struct CheckpointMetadata
{
    /// <summary>Tick do jogo. Deve ser monotônico — §7.1 regra 3.</summary>
    public long GameTick { get; init; }
    /// <summary>Posição no log de eventos de mundo — §2.1.</summary>
    public long WorldCursor { get; init; }
    /// <summary>Hash dos mods classe <c>world</c> — §8.</summary>
    public string ModSetHash { get; init; }
    /// <summary>Hash do conteúdo do save. Endereça o checkpoint — §7.1 regra 1.</summary>
    public string ContentHash { get; init; }
    /// <summary>Relógio de parede do cliente, UTC. Só para diagnóstico: nunca ordena nada.</summary>
    public DateTime WallClock { get; init; }
    /// <summary>Marca o checkpoint tirado imediatamente antes de uma sessão — §2.3, §7.2.</summary>
    public bool PreSessao { get; init; }

    public void Write(BinaryWriter w)
    {
        w.Write(GameTick);
        w.Write(WorldCursor);
        w.Write(ModSetHash ?? "");
        w.Write(ContentHash ?? "");
        w.Write(WallClock.ToUniversalTime().Ticks);
        w.Write(PreSessao);
    }

    public static CheckpointMetadata Read(BinaryReader r) => new()
    {
        GameTick = r.ReadInt64(),
        WorldCursor = r.ReadInt64(),
        ModSetHash = r.ReadString(),
        ContentHash = r.ReadString(),
        WallClock = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
        PreSessao = r.ReadBoolean(),
    };

    public override string ToString() =>
        $"tick={GameTick} cursor={WorldCursor} conteudo={ContentHash} mods={ModSetHash} " +
        $"relogio={WallClock:O}{(PreSessao ? " [pré-sessão]" : "")}";
}

/// <summary>colonia.checkpoint — o cliente envia; o servidor guarda para durabilidade.</summary>
public sealed class ColoniaCheckpoint : IMessage
{
    public string PlayerId { get; init; } = "";
    public string ColonyId { get; init; } = "";
    public CheckpointMetadata Metadata { get; init; }
    public byte[] Conteudo { get; init; } = Array.Empty<byte>();

    public ColonyIdentity Identity => new(PlayerId, ColonyId);

    public MessageId Id => MessageId.ColoniaCheckpoint;

    public void Write(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(ColonyId);
        Metadata.Write(w);
        w.Write(Conteudo.Length);
        w.Write(Conteudo);
    }

    public static ColoniaCheckpoint Read(BinaryReader r)
    {
        string playerId = r.ReadString();
        string colonyId = r.ReadString();
        var meta = CheckpointMetadata.Read(r);
        int length = r.ReadInt32();
        return new ColoniaCheckpoint
        {
            PlayerId = playerId,
            ColonyId = colonyId,
            Metadata = meta,
            Conteudo = r.ReadBytes(length),
        };
    }
}

/// <summary>
/// colonia.heartbeat — carrega <c>(tick, state_fingerprint)</c>, §7.1 regra 4.
///
/// A impressão digital é do estado **vivo** da simulação, não do último
/// checkpoint: entre dois checkpoints o hash do save fica igual por
/// construção, então compará-lo geraria alarme em toda partida normal.
/// O que precisa mudar a cada heartbeat é a prova de que o jogo andou.
/// Ideia reusada do Multiplayer (MIT, Zetrith) — §14.3.
/// </summary>
public sealed class ColoniaHeartbeat : IMessage
{
    public string PlayerId { get; init; } = "";
    public string ColonyId { get; init; } = "";
    public long GameTick { get; init; }
    public string StateFingerprint { get; init; } = "";

    public ColonyIdentity Identity => new(PlayerId, ColonyId);

    public MessageId Id => MessageId.ColoniaHeartbeat;

    public void Write(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(ColonyId);
        w.Write(GameTick);
        w.Write(StateFingerprint);
    }

    public static ColoniaHeartbeat Read(BinaryReader r) => new()
    {
        PlayerId = r.ReadString(),
        ColonyId = r.ReadString(),
        GameTick = r.ReadInt64(),
        StateFingerprint = r.ReadString(),
    };
}

/// <summary>
/// colonia.restauracao — o cliente pede de volta um checkpoint pelo hash;
/// o servidor devolve o conteúdo.
///
/// §7.3: o servidor **nunca impõe** uma versão do save. Ele entrega quando
/// pedido, e quem decide aplicar é o jogador. Pedido e resposta usam a mesma
/// mensagem: conteúdo vazio é pedido, conteúdo preenchido é resposta.
/// </summary>
public sealed class ColoniaRestauracao : IMessage
{
    public string PlayerId { get; init; } = "";
    public string ColonyId { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public byte[] Conteudo { get; init; } = Array.Empty<byte>();
    /// <summary>Preenchido pelo servidor quando não tem o que foi pedido.</summary>
    public string Erro { get; init; } = "";

    public ColonyIdentity Identity => new(PlayerId, ColonyId);

    public bool EhPedido => Conteudo.Length == 0 && Erro.Length == 0;

    public MessageId Id => MessageId.ColoniaRestauracao;

    public void Write(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(ColonyId);
        w.Write(ContentHash);
        w.Write(Erro);
        w.Write(Conteudo.Length);
        w.Write(Conteudo);
    }

    public static ColoniaRestauracao Read(BinaryReader r)
    {
        string playerId = r.ReadString();
        string colonyId = r.ReadString();
        string contentHash = r.ReadString();
        string erro = r.ReadString();
        int tamanho = r.ReadInt32();
        return new ColoniaRestauracao
        {
            PlayerId = playerId,
            ColonyId = colonyId,
            ContentHash = contentHash,
            Erro = erro,
            Conteudo = r.ReadBytes(tamanho),
        };
    }
}

public enum TipoAlerta
{
    /// <summary>Tick veio **menor** que o último aceito — §7.1 regra 3.</summary>
    TickRegrediu = 1,
    /// <summary>
    /// Dois checkpoints com o mesmo conteúdo e o tick avançando — o cliente
    /// está reenviando estado velho com carimbo novo (§15.1).
    /// </summary>
    ConteudoCongelado = 2,
    /// <summary>
    /// A impressão digital do estado vivo não muda enquanto o tick avança:
    /// o cliente diz que simulou, mas nada nele mudou — §7.1 regra 4.
    /// </summary>
    SimulacaoCongelada = 5,
    /// <summary>Hash declarado não bate com o conteúdo recebido.</summary>
    HashNaoConfere = 3,
    /// <summary>Cliente e servidor discordam do estado da colônia — §7.3.</summary>
    DivergenciaDeAutoridade = 4,
}

/// <summary>
/// colonia.alerta — o servidor **nunca aceita em silêncio** (§7.1 regra 3).
/// Aceita, marca como suspeito e avisa em voz alta, dos dois lados.
/// </summary>
public sealed class ColoniaAlerta : IMessage
{
    public string PlayerId { get; init; } = "";
    public string ColonyId { get; init; } = "";
    public TipoAlerta Tipo { get; init; }
    public string Explicacao { get; init; } = "";

    public MessageId Id => MessageId.ColoniaAlerta;

    public void Write(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(ColonyId);
        w.Write((int)Tipo);
        w.Write(Explicacao);
    }

    public static ColoniaAlerta Read(BinaryReader r) => new()
    {
        PlayerId = r.ReadString(),
        ColonyId = r.ReadString(),
        Tipo = (TipoAlerta)r.ReadInt32(),
        Explicacao = r.ReadString(),
    };
}

/// <summary>Cadência do heartbeat, acordada entre cliente e servidor.</summary>
public static class Heartbeat
{
    /// <summary>
    /// 30s. A latência de detecção da §7.1 regra 4 é no máximo dois
    /// intervalos — o critério de M2 é detectar em menos de 1 minuto.
    /// </summary>
    public static readonly TimeSpan IntervaloPadrao = TimeSpan.FromSeconds(30);
}
