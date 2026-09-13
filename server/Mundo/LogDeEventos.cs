using System.Text;
using System.Text.Json;
using WithFriends.Protocol.Messages;

namespace WithFriends.Server.Mundo;

/// <summary>
/// Log de eventos append-only do mundo — §2.1.
///
/// O servidor **ordena**; cada cliente consome do próprio cursor, no próprio
/// tempo. Não existe estado do mundo que se sobrescreve: existe uma sequência
/// de fatos. Como a ordem vem daqui, conflito de ordem não existe por
/// construção.
///
/// O servidor não interpreta payload (§10) — ele numera, grava e entrega.
/// </summary>
public sealed class LogDeEventos
{
    sealed class LinhaEvento
    {
        public long Seq { get; set; }
        public string Autor { get; set; } = "";
        public ushort Tipo { get; set; }
        public string Payload { get; set; } = "";
        public long TimestampLogico { get; set; }

        /// <summary>
        /// O planeta sob o qual o fato foi afirmado. Ausente nas linhas
        /// gravadas antes de o log passar a segregar por planeta: essas herdam
        /// o planeta de registro (ver <c>planetaDeRegistro</c>).
        /// </summary>
        public string? Planeta { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    static readonly UTF8Encoding Utf8SemBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Um fato e o planeta em que ele significa alguma coisa.</summary>
    readonly record struct Anotado(EventoMundo Evento, string Planeta);

    readonly object trava = new();
    readonly List<Anotado> eventos = new();
    readonly string? caminho;

    long ultimoSeq;

    /// <summary>
    /// O planeta que o log adotou antes de segregar por planeta. Serve só para
    /// atribuir dono às linhas antigas, que não trazem o campo.
    /// </summary>
    string planetaDeRegistro = "";

    /// <param name="caminho">
    /// Arquivo <c>.jsonl</c> onde o log é gravado. <c>null</c> mantém tudo em
    /// memória — usado nos testes.
    /// </param>
    public LogDeEventos(string? caminho = null)
    {
        this.caminho = caminho;
        if (caminho != null) Carregar();
    }

    public long UltimoSeq { get { lock (trava) return ultimoSeq; } }

    /// <summary>
    /// O planeta que o log adotou antes de passar a segregar por planeta.
    /// Diagnóstico e migração; não é lei para ninguém.
    /// </summary>
    public string PlanetaDeRegistro { get { lock (trava) return planetaDeRegistro; } }

    /// <summary>Quantos planetas distintos têm fato neste log.</summary>
    public IReadOnlyList<string> PlanetasComEventos
    {
        get { lock (trava) return eventos.Select(e => e.Planeta).Distinct().ToArray(); }
    }

    public int Contagem { get { lock (trava) return eventos.Count; } }

    /// <summary>
    /// Numera, grava e devolve o fato já ordenado, junto do planeta em que ele
    /// significa alguma coisa.
    ///
    /// <para>A numeração é global e monotônica, atravessando planetas: assim o
    /// cursor de um jogador continua válido mesmo que o coordenador passe a
    /// hospedar outro planeta no meio do caminho.</para>
    /// </summary>
    public EventoMundo Publicar(string autor, TipoEventoMundo tipo, byte[] payload, string planeta)
    {
        lock (trava)
        {
            var evento = new EventoMundo
            {
                Seq = ++ultimoSeq,
                Autor = autor,
                Tipo = tipo,
                Payload = payload,
                TimestampLogico = DateTime.UtcNow.Ticks,
            };

            eventos.Add(new Anotado(evento, planeta));
            Anexar(evento, planeta);
            return evento;
        }
    }

    /// <summary>
    /// Tudo o que veio depois do cursor <b>e vale neste planeta</b>, em ordem.
    ///
    /// <para>O filtro por planeta é a regra, não uma otimização: um evento diz
    /// "tile 113533", e tile é índice, não coordenada. Entregar a quem está em
    /// outro planeta é entregar uma coordenada que aponta para outro lugar —
    /// ou para lugar nenhum.</para>
    /// </summary>
    public IReadOnlyList<EventoMundo> Desde(long cursor, string planeta)
    {
        lock (trava)
            return eventos
                .Where(e => e.Evento.Seq > cursor && e.Planeta == planeta)
                .Select(e => e.Evento)
                .OrderBy(e => e.Seq)
                .ToArray();
    }

    void Anexar(EventoMundo evento, string planeta)
    {
        if (caminho == null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        var linha = new LinhaEvento
        {
            Seq = evento.Seq,
            Autor = evento.Autor,
            Tipo = (ushort)evento.Tipo,
            Payload = Convert.ToBase64String(evento.Payload),
            TimestampLogico = evento.TimestampLogico,
            Planeta = planeta,
        };
        File.AppendAllText(caminho, JsonSerializer.Serialize(linha, Json) + "\n", Utf8SemBom);
    }

    /// <summary>Hash de planeta encurtado, para caber numa linha de log.</summary>
    public static string Curto(string planeta) =>
        string.IsNullOrEmpty(planeta) ? "(sem planeta)"
        : planeta.Length > 22 ? planeta[..22] + "…"
        : planeta;

    void Carregar()
    {
        // O planeta de registro é lido primeiro: é ele que dá dono às linhas
        // gravadas antes de o log passar a segregar por planeta.
        string arquivoPlaneta = Path.Combine(Path.GetDirectoryName(caminho!)!, "planeta.txt");
        if (File.Exists(arquivoPlaneta))
            planetaDeRegistro = File.ReadAllText(arquivoPlaneta).Trim();

        if (!File.Exists(caminho)) return;

        foreach (string linha in File.ReadAllLines(caminho!))
        {
            if (linha.Length == 0) continue;
            try
            {
                var lido = JsonSerializer.Deserialize<LinhaEvento>(linha, Json);
                if (lido == null) continue;

                eventos.Add(new Anotado(
                    new EventoMundo
                    {
                        Seq = lido.Seq,
                        Autor = lido.Autor,
                        Tipo = (TipoEventoMundo)lido.Tipo,
                        Payload = Convert.FromBase64String(lido.Payload),
                        TimestampLogico = lido.TimestampLogico,
                    },
                    // Linha sem planeta é anterior à segregação: pertence ao
                    // planeta que o log tinha adotado na época.
                    string.IsNullOrEmpty(lido.Planeta) ? planetaDeRegistro : lido.Planeta));
                ultimoSeq = Math.Max(ultimoSeq, lido.Seq);
            }
            catch (Exception e) when (e is JsonException or FormatException)
            {
                // Linha ilegível não invalida o log: o resto da história do
                // planeta continua válido.
                Console.WriteLine($"!! log de mundo: linha ignorada ({e.Message})");
            }
        }

        var planetas = eventos.Select(e => e.Planeta).Distinct().ToArray();
        Console.WriteLine(
            $"Log de mundo: {eventos.Count} eventos carregados, último seq={ultimoSeq}, " +
            $"{planetas.Length} planeta(s): {string.Join(", ", planetas.Select(Curto))}");
    }
}
