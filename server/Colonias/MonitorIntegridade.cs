using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Server.Colonias;

/// <summary>
/// Estado observado de uma colônia, do ponto de vista do servidor.
/// O servidor não simula nada (§10) — só compara o que o cliente afirma
/// com o que ele afirmou antes.
/// </summary>
public sealed class EstadoColonia
{
    public long UltimoTick { get; internal set; } = long.MinValue;

    /// <summary>Hash do conteúdo do último checkpoint recebido.</summary>
    public string UltimoContentHash { get; internal set; } = "";
    public long TickDoUltimoConteudo { get; internal set; } = long.MinValue;

    /// <summary>Impressão digital do estado vivo, do último heartbeat.</summary>
    public string UltimoFingerprint { get; internal set; } = "";
    public DateTime UltimaMudancaDeFingerprint { get; internal set; }

    /// <summary>
    /// Alertas já emitidos e ainda não normalizados. Existe para não repetir
    /// o mesmo aviso a cada heartbeat: alerta que se repete sozinho vira
    /// ruído, e ruído treina o jogador a ignorar — pior que não alertar.
    /// </summary>
    internal readonly HashSet<TipoAlerta> AlertasAtivos = new();
}

/// <summary>
/// Aplica as regras 3 e 4 da §7.1 — as duas que teriam detectado o incidente
/// da §15.1 em minutos, em vez de no dia seguinte.
///
/// Nada aqui recusa nada: o servidor aceita, marca como suspeito e alerta.
/// Recusar seria perder progresso, que é a falha que este projeto existe para
/// não repetir.
///
/// Duas grandezas distintas, propositalmente:
/// <list type="bullet">
/// <item><c>content_hash</c> (checkpoint) — **deve** ficar igual entre dois
/// checkpoints. Só é sintoma quando um checkpoint novo repete o conteúdo do
/// anterior com o tick já adiantado.</item>
/// <item><c>state_fingerprint</c> (heartbeat) — **deve** mudar sempre que o
/// tick avança, porque é a prova de que a simulação andou.</item>
/// </list>
/// </summary>
public sealed class MonitorIntegridade
{
    readonly Dictionary<ColonyIdentity, EstadoColonia> estados = new();
    readonly ISinkAlertas alertas;
    readonly IArmazenamento? armazenamento;

    /// <param name="armazenamento">
    /// Opcional. Quando informado, a referência de uma colônia vista pela
    /// primeira vez é reconstruída a partir do índice em disco — assim
    /// reiniciar o coordenador não apaga a memória do que já foi observado.
    /// </param>
    public MonitorIntegridade(ISinkAlertas alertas, IArmazenamento? armazenamento = null)
    {
        this.alertas = alertas;
        this.armazenamento = armazenamento;
    }

    public EstadoColonia Estado(ColonyIdentity identity) =>
        estados.TryGetValue(identity, out var e) ? e : new EstadoColonia();

    /// <summary>Avalia um heartbeat <c>(tick, state_fingerprint)</c> — §7.1 regra 4.</summary>
    public IReadOnlyList<ColoniaAlerta> Avaliar(ColoniaHeartbeat heartbeat, DateTime agora)
    {
        var identity = heartbeat.Identity;
        var estado = Obter(identity, heartbeat.GameTick, agora);
        var encontrados = new List<ColoniaAlerta>();

        VerificarRegressaoDeTick(identity, heartbeat.GameTick, estado, "heartbeat", encontrados);

        bool tickAvancou = heartbeat.GameTick > estado.UltimoTick;
        bool congelado = tickAvancou && estado.UltimoFingerprint.Length > 0 &&
                         heartbeat.StateFingerprint == estado.UltimoFingerprint;

        if (congelado)
        {
            var parado = agora - estado.UltimaMudancaDeFingerprint;
            AdicionarUmaVez(estado, encontrados, new ColoniaAlerta
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                Tipo = TipoAlerta.SimulacaoCongelada,
                Explicacao =
                    $"O tick avançou para {heartbeat.GameTick}, mas o estado da colônia não " +
                    $"muda há {parado:g}. O cliente afirma que simulou e nada nele mudou — " +
                    "provavelmente o save que ele acompanha não é o save que ele está jogando " +
                    "(ver §15.1).",
            });
        }
        else
        {
            estado.AlertasAtivos.Remove(TipoAlerta.SimulacaoCongelada);
        }

        if (heartbeat.StateFingerprint != estado.UltimoFingerprint)
        {
            estado.UltimoFingerprint = heartbeat.StateFingerprint;
            estado.UltimaMudancaDeFingerprint = agora;
        }
        if (heartbeat.GameTick > estado.UltimoTick) estado.UltimoTick = heartbeat.GameTick;

        return Emitir(encontrados);
    }

    /// <summary>Avalia os metadados de um checkpoint — §7.1 regras 2 e 3.</summary>
    public IReadOnlyList<ColoniaAlerta> Avaliar(ColonyIdentity identity, CheckpointMetadata metadata, DateTime agora)
    {
        var estado = Obter(identity, metadata.GameTick, agora);
        var encontrados = new List<ColoniaAlerta>();

        VerificarRegressaoDeTick(identity, metadata.GameTick, estado, "checkpoint", encontrados);

        // O sintoma da §15.1: conteúdo idêntico ao do checkpoint anterior,
        // mas com o tick já adiantado. Foi assim que o RT gravou o mesmo
        // estado por ~5 horas com carimbo de tempo novo.
        if (estado.UltimoContentHash.Length > 0 &&
            metadata.ContentHash == estado.UltimoContentHash &&
            metadata.GameTick > estado.TickDoUltimoConteudo)
        {
            AdicionarUmaVez(estado, encontrados, new ColoniaAlerta
            {
                PlayerId = identity.PlayerId,
                ColonyId = identity.ColonyId,
                Tipo = TipoAlerta.ConteudoCongelado,
                Explicacao =
                    $"Checkpoint novo no tick {metadata.GameTick} com conteúdo idêntico ao do " +
                    $"tick {estado.TickDoUltimoConteudo} (hash {metadata.ContentHash}). " +
                    "O jogo avançou e o save não mudou: o arquivo que está sendo enviado " +
                    "provavelmente ficou órfão — ver §15.1.",
            });
        }
        else
        {
            estado.AlertasAtivos.Remove(TipoAlerta.ConteudoCongelado);
        }

        if (metadata.ContentHash != estado.UltimoContentHash)
        {
            estado.UltimoContentHash = metadata.ContentHash;
            estado.TickDoUltimoConteudo = metadata.GameTick;
        }
        if (metadata.GameTick > estado.UltimoTick) estado.UltimoTick = metadata.GameTick;

        return Emitir(encontrados);
    }

    EstadoColonia Obter(ColonyIdentity identity, long tick, DateTime agora)
    {
        if (estados.TryGetValue(identity, out var estado)) return estado;

        estado = Restaurar(identity) ?? new EstadoColonia { UltimoTick = tick };
        estado.UltimaMudancaDeFingerprint = agora;
        return estados[identity] = estado;
    }

    /// <summary>
    /// Reconstrói a referência a partir do índice append-only. Recupera tick e
    /// hash de conteúdo; a impressão digital do estado vivo não é persistida —
    /// heartbeat não é gravado — então ela é rearmada no primeiro heartbeat
    /// depois do reinício, sem gerar alerta falso.
    /// </summary>
    EstadoColonia? Restaurar(ColonyIdentity identity)
    {
        if (armazenamento == null) return null;

        var historico = IndiceCheckpoints.Ler(armazenamento, identity);
        if (historico.Count == 0) return null;

        var ultimo = historico.OrderByDescending(e => e.GameTick).First();
        Console.WriteLine(
            $"[{identity}] referência restaurada do disco: tick={ultimo.GameTick} " +
            $"conteudo={ultimo.ContentHash} ({historico.Count} checkpoints no índice)");

        return new EstadoColonia
        {
            UltimoTick = ultimo.GameTick,
            UltimoContentHash = ultimo.ContentHash,
            TickDoUltimoConteudo = ultimo.GameTick,
        };
    }

    /// <summary>
    /// Regra 3: o tick só anda para frente. Tick **igual** não é regressão —
    /// é o jogo pausado, que é estado normal e comuníssimo em RimWorld.
    /// Só tick menor denuncia save antigo carregado por cima.
    ///
    /// Depois de alertar, o servidor **adota o tick observado como nova
    /// referência**. O cliente é autoridade sobre a própria colônia (§7.3):
    /// o papel do servidor é notar e avisar uma vez, não ficar gritando a
    /// cada heartbeat porque a referência antiga era mais alta.
    /// </summary>
    static void VerificarRegressaoDeTick(
        ColonyIdentity identity, long tick, EstadoColonia estado, string origem, List<ColoniaAlerta> encontrados)
    {
        if (estado.UltimoTick == long.MinValue || tick >= estado.UltimoTick)
        {
            estado.AlertasAtivos.Remove(TipoAlerta.TickRegrediu);
            return;
        }

        AdicionarUmaVez(estado, encontrados, new ColoniaAlerta
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            Tipo = TipoAlerta.TickRegrediu,
            Explicacao =
                $"{origem} com tick {tick}, mas o último aceito foi {estado.UltimoTick}. " +
                "O tick do jogo só anda para frente: provavelmente um save antigo foi " +
                "carregado por cima desta colônia.",
        });

        // Rebaseline: a partir daqui o monitoramento volta ao normal.
        estado.UltimoTick = tick;
    }

    /// <summary>
    /// Emite o alerta só na transição para o estado ruim. Enquanto a condição
    /// persistir, silêncio; quando ela sumir, o tipo é rearmado.
    /// </summary>
    static void AdicionarUmaVez(EstadoColonia estado, List<ColoniaAlerta> encontrados, ColoniaAlerta alerta)
    {
        if (estado.AlertasAtivos.Add(alerta.Tipo))
            encontrados.Add(alerta);
    }

    IReadOnlyList<ColoniaAlerta> Emitir(List<ColoniaAlerta> encontrados)
    {
        foreach (var alerta in encontrados) alertas.Emitir(alerta);
        return encontrados;
    }
}
