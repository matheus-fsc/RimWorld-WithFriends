using System.Text.Json.Serialization;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Server.Colonias;

/// <summary>Uma linha do índice append-only de uma colônia.</summary>
public sealed class EntradaCheckpoint
{
    public long GameTick { get; set; }
    public long WorldCursor { get; set; }
    public string ContentHash { get; set; } = "";
    public string ModSetHash { get; set; } = "";
    public DateTime WallClock { get; set; }
    public DateTime RecebidoEm { get; set; }
    public bool PreSessao { get; set; }
    /// <summary>Aceito, porém com alerta: ver §7.1 regra 3.</summary>
    public bool Suspeito { get; set; }
    [JsonIgnore]
    public IReadOnlyList<ColoniaAlerta> Alertas { get; set; } = Array.Empty<ColoniaAlerta>();
}

/// <summary>
/// Guarda checkpoints para **durabilidade**, nunca para mandar no save do
/// jogador (§2.2, §7.3). Append-only, endereçado por hash, e todo desvio
/// vira alerta em vez de silêncio.
/// </summary>
public sealed class CheckpointStore
{
    readonly IArmazenamento armazenamento;
    readonly MonitorIntegridade monitor;
    readonly ISinkAlertas alertas;

    public CheckpointStore(IArmazenamento armazenamento, MonitorIntegridade monitor, ISinkAlertas alertas)
    {
        this.armazenamento = armazenamento;
        this.monitor = monitor;
        this.alertas = alertas;
    }

    public EntradaCheckpoint Aceitar(ColoniaCheckpoint checkpoint, DateTime agora)
    {
        var identity = checkpoint.Identity;
        var encontrados = new List<ColoniaAlerta>();

        // O hash é recalculado sobre o que chegou: metadado é afirmação do
        // cliente, conteúdo é fato. Se discordarem, vale o fato — e alerta.
        string hashReal = Hashing.OfBytes(checkpoint.Conteudo);
        if (checkpoint.Metadata.ContentHash != hashReal)
        {
            var alerta = new ColoniaAlerta
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                Tipo = TipoAlerta.HashNaoConfere,
                Explicacao =
                    $"O cliente declarou content_hash {checkpoint.Metadata.ContentHash}, " +
                    $"mas o conteúdo recebido tem {hashReal}. O checkpoint foi guardado " +
                    "sob o hash real; os metadados estão marcados como suspeitos.",
            };
            encontrados.Add(alerta);
            alertas.Emitir(alerta);
        }

        var metadata = new CheckpointMetadata
        {
            GameTick = checkpoint.Metadata.GameTick,
            WorldCursor = checkpoint.Metadata.WorldCursor,
            ModSetHash = checkpoint.Metadata.ModSetHash,
            ContentHash = hashReal,
            WallClock = checkpoint.Metadata.WallClock,
            PreSessao = checkpoint.Metadata.PreSessao,
        };

        // Regras 3 e 4 da §7.1. Aceita de qualquer jeito — perder progresso é
        // pior do que guardar um checkpoint suspeito.
        encontrados.AddRange(monitor.Avaliar(identity, metadata, agora));

        // Append-only: gravar conteúdo é idempotente sob o mesmo hash, e o
        // índice sempre ganha uma linha nova. Nada é sobrescrito.
        armazenamento.GravarConteudo(hashReal, checkpoint.Conteudo);

        var entrada = new EntradaCheckpoint
        {
            GameTick = metadata.GameTick,
            WorldCursor = metadata.WorldCursor,
            ContentHash = hashReal,
            ModSetHash = metadata.ModSetHash,
            WallClock = metadata.WallClock,
            RecebidoEm = agora,
            PreSessao = metadata.PreSessao,
            Suspeito = encontrados.Count > 0,
            Alertas = encontrados,
        };

        armazenamento.AnexarAoIndice(identity, IndiceCheckpoints.Serializar(entrada));
        return entrada;
    }

    public IReadOnlyList<ColoniaAlerta> Aceitar(ColoniaHeartbeat heartbeat, DateTime agora) =>
        monitor.Avaliar(heartbeat, agora);

    public IReadOnlyList<EntradaCheckpoint> Historico(ColonyIdentity identity) =>
        IndiceCheckpoints.Ler(armazenamento, identity);

    /// <summary>
    /// Restauração — §7.3: o servidor devolve o conteúdo, o jogador decide.
    /// Nunca aplica nada por cima do save de ninguém.
    /// </summary>
    public byte[] Recuperar(string contentHash) => armazenamento.LerConteudo(contentHash);

    /// <summary>
    /// Atende um pedido de restauração. Devolve erro legível em vez de
    /// exceção: não ter o checkpoint é resposta, não falha de protocolo.
    /// </summary>
    public ColoniaRestauracao Atender(ColoniaRestauracao pedido)
    {
        bool conhecido = Historico(pedido.Identity)
            .Any(entrada => entrada.ContentHash == pedido.ContentHash);

        if (!conhecido)
            return Negar(pedido, "Este checkpoint não pertence a esta colônia, ou nunca chegou aqui.");

        try
        {
            return new ColoniaRestauracao
            {
                PlayerId = pedido.PlayerId,
                ColonyId = pedido.ColonyId,
                ContentHash = pedido.ContentHash,
                Conteudo = Recuperar(pedido.ContentHash),
            };
        }
        catch (IOException e)
        {
            return Negar(pedido, $"O conteúdo está registrado no índice mas não pôde ser lido: {e.Message}");
        }
    }

    static ColoniaRestauracao Negar(ColoniaRestauracao pedido, string erro) => new()
    {
        PlayerId = pedido.PlayerId,
        ColonyId = pedido.ColonyId,
        ContentHash = pedido.ContentHash,
        Erro = erro,
    };
}
