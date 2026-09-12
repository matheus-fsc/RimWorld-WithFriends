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
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    static readonly UTF8Encoding Utf8SemBom = new(encoderShouldEmitUTF8Identifier: false);

    readonly object trava = new();
    readonly List<EventoMundo> eventos = new();
    readonly string? caminho;

    long ultimoSeq;
    string planeta = "";

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
    /// Planeta deste mundo. O primeiro cliente a sincronizar define; os
    /// demais precisam bater. Vazio = ainda não definido.
    /// </summary>
    public string Planeta { get { lock (trava) return planeta; } }

    /// <summary>
    /// Confere (e adota, se ainda não houver) a identidade do planeta.
    /// Devolve <c>null</c> se está tudo certo, ou o motivo legível da recusa.
    ///
    /// Recusar é só para eventos de mundo: quem tem outro planeta continua
    /// logado, jogando e guardando checkpoint (§8, §11). O que ele não pode é
    /// publicar coordenadas que não significam nada para os outros.
    /// </summary>
    public string? ConferirPlaneta(string declarado)
    {
        if (string.IsNullOrEmpty(declarado))
            return "O cliente não declarou o planeta.";

        lock (trava)
        {
            if (planeta.Length == 0)
            {
                planeta = declarado;
                Anotar(declarado);
                Console.WriteLine($"Planeta deste mundo definido como {declarado}");
                return null;
            }

            if (planeta == declarado) return null;

            return
                $"O planeta não é o mesmo: este mundo é {planeta}, o seu é {declarado}. " +
                "Os assentamentos são identificados por índice de tile, que só significa " +
                "a mesma coisa no mesmo planeta. Gere o mundo com a mesma semente e as " +
                "mesmas opções de geração do primeiro jogador.";
        }
    }

    void Anotar(string planetaNovo)
    {
        if (caminho == null) return;
        string arquivo = Path.Combine(Path.GetDirectoryName(caminho)!, "planeta.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(arquivo, planetaNovo, Utf8SemBom);
    }

    public int Contagem { get { lock (trava) return eventos.Count; } }

    /// <summary>Numera, grava e devolve o fato já ordenado.</summary>
    public EventoMundo Publicar(string autor, TipoEventoMundo tipo, byte[] payload)
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

            eventos.Add(evento);
            Anexar(evento);
            return evento;
        }
    }

    /// <summary>Tudo o que veio depois do cursor do cliente, em ordem.</summary>
    public IReadOnlyList<EventoMundo> Desde(long cursor)
    {
        lock (trava)
            return eventos.Where(e => e.Seq > cursor).OrderBy(e => e.Seq).ToArray();
    }

    void Anexar(EventoMundo evento)
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
        };
        File.AppendAllText(caminho, JsonSerializer.Serialize(linha, Json) + "\n", Utf8SemBom);
    }

    void Carregar()
    {
        // O planeta é lido primeiro: um mundo pode ter planeta definido e
        // nenhum evento ainda.
        string arquivoPlaneta = Path.Combine(Path.GetDirectoryName(caminho!)!, "planeta.txt");
        if (File.Exists(arquivoPlaneta)) planeta = File.ReadAllText(arquivoPlaneta).Trim();

        if (!File.Exists(caminho))
        {
            if (planeta.Length > 0) Console.WriteLine($"Log de mundo: vazio, planeta {planeta}");
            return;
        }

        foreach (string linha in File.ReadAllLines(caminho!))
        {
            if (linha.Length == 0) continue;
            try
            {
                var lido = JsonSerializer.Deserialize<LinhaEvento>(linha, Json);
                if (lido == null) continue;

                eventos.Add(new EventoMundo
                {
                    Seq = lido.Seq,
                    Autor = lido.Autor,
                    Tipo = (TipoEventoMundo)lido.Tipo,
                    Payload = Convert.FromBase64String(lido.Payload),
                    TimestampLogico = lido.TimestampLogico,
                });
                ultimoSeq = Math.Max(ultimoSeq, lido.Seq);
            }
            catch (Exception e) when (e is JsonException or FormatException)
            {
                // Linha ilegível não invalida o log: o resto da história do
                // planeta continua válido.
                Console.WriteLine($"!! log de mundo: linha ignorada ({e.Message})");
            }
        }

        Console.WriteLine(
            $"Log de mundo: {eventos.Count} eventos carregados, último seq={ultimoSeq}" +
            (planeta.Length > 0 ? $", planeta {planeta}" : ""));
    }
}
