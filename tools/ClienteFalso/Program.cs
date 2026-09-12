using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Transport;

// Cliente falso: fala o protocolo sem o jogo. Serve para exercitar o
// coordenador com dados reais (um .rws de verdade) e para reproduzir o
// incidente da §15.1 sob demanda.
//
//   ClienteFalso <endereco> <caminho-do-save> [--incidente] [--colonia <id>]
//   ClienteFalso <endereco> --jogador <nome> --tile <n> [--riqueza <n>] [--ficar <s>]
//                            [--remover-ao-sair]
//
// O segundo modo finge ser outro jogador no planeta: publica um assentamento
// e fica online. Serve para ver o M1 funcionando com um jogo só aberto.
//
// Usa o mesmo DirectTransport do mod: o que funciona aqui funciona no jogo.

// --- modo "outro jogador no planeta" (M1) ---
int posJogador = Array.IndexOf(args, "--jogador");
if (posJogador >= 0)
    return await JogadorFalso.Rodar(args, posJogador);

if (args.Length < 2)
{
    Console.Error.WriteLine("uso: ClienteFalso <endereco> <caminho-do-save> [--incidente] [--colonia <id>]");
    Console.Error.WriteLine("     endereco: [::1]:25555 · 192.168.0.4:25555 · casa.exemplo.com");
    return 2;
}

if (!EnderecoServidor.TentarAnalisar(args[0], out var endereco, out string erroEndereco))
{
    Console.Error.WriteLine($"endereço inválido: {erroEndereco}");
    return 2;
}

string caminhoSave = args[1];
bool incidente = args.Contains("--incidente");

string playerId = "jogador-falso-1";
// Colônia nova a cada execução, salvo se pedirem uma explícita: duas rodadas
// independentes não devem parecer regressão de tick uma da outra.
int posColonia = Array.IndexOf(args, "--colonia");
string colonyId = posColonia >= 0 && posColonia + 1 < args.Length
    ? args[posColonia + 1]
    : "colonia-falsa-" + Guid.NewGuid().ToString("N")[..8];

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var conexao = await new DirectTransport().ConectarAsync(endereco, cts.Token);
Console.WriteLine($"conectado em {conexao.Descricao} — colônia {colonyId}");

conexao.Enviar(new Handshake
{
    PlayerId = playerId,
    DisplayName = "cliente falso",
    Capabilities = Capabilities.Checkpoints | Capabilities.WorldEvents,
    WorldModSetHash = "sha256:mods-de-teste",
});

var resposta = Receber(conexao);
if (resposta.Id == MessageId.HandshakeRecusado)
{
    var recusa = resposta.Decode(HandshakeRecusado.Read);
    Console.Error.WriteLine($"recusado ({recusa.Motivo}): {recusa.Explicacao}");
    return 1;
}

var aceito = resposta.Decode(HandshakeAceito.Read);
Console.WriteLine($"aceito por {aceito.ServerName}, protocolo v{aceito.ProtocolVersion}, caps={aceito.Capabilities}");

byte[] conteudo = File.ReadAllBytes(caminhoSave);
string contentHash = Hashing.OfBytes(conteudo);
long tick = 1_000_000;

Console.WriteLine($"save: {Path.GetFileName(caminhoSave)} — {conteudo.Length:N0} bytes, {contentHash}");

conexao.Enviar(new ColoniaCheckpoint
{
    PlayerId = playerId,
    ColonyId = colonyId,
    Conteudo = conteudo,
    Metadata = new CheckpointMetadata
    {
        GameTick = tick,
        WorldCursor = 0,
        ModSetHash = "sha256:mods-de-teste",
        ContentHash = contentHash,
        WallClock = DateTime.UtcNow,
    },
});
Console.WriteLine($"checkpoint enviado (tick {tick})");

// Heartbeats. Com --incidente o tick avança e o conteúdo nunca muda:
// é a §15.1 acontecendo ao vivo.
for (int i = 1; i <= 4; i++)
{
    tick += 2_500;
    // Com --incidente a impressão digital do estado vivo nunca muda, que é o
    // sintoma da §15.1. Sem ela, muda a cada heartbeat, como num jogo normal.
    string fingerprint = incidente
        ? Hashing.OfString("estado congelado")
        : Hashing.OfString($"estado-{i}-{Guid.NewGuid()}");

    conexao.Enviar(new ColoniaHeartbeat
    {
        PlayerId = playerId,
        ColonyId = colonyId,
        GameTick = tick,
        StateFingerprint = fingerprint,
    });
    Console.WriteLine($"heartbeat {i}: tick={tick} estado={fingerprint[..20]}…");

    // Espera curta por alerta; ausência de alerta é o caso saudável.
    await Task.Delay(400);
    while (conexao.TentarReceber(out var envelope))
    {
        var alerta = envelope.Decode(ColoniaAlerta.Read);
        Console.WriteLine($"  !! ALERTA {alerta.Tipo}: {alerta.Explicacao}");
    }
}

return 0;

static Envelope Receber(IConexao conexao)
{
    var limite = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < limite)
    {
        if (conexao.TentarReceber(out var envelope)) return envelope;
        Thread.Sleep(20);
    }
    throw new TimeoutException("o coordenador não respondeu em 15s");
}


/// <summary>
/// Finge ser outro jogador: publica um assentamento no log de mundo e fica
/// online. É o par que falta para verificar o critério de M1 — "dois
/// jogadores se veem no planeta" — sem abrir dois RimWorlds.
/// </summary>
static class JogadorFalso
{
    public static async Task<int> Rodar(string[] args, int posJogador)
    {
        if (!EnderecoServidor.TentarAnalisar(args[0], out var endereco, out string erro))
        {
            Console.Error.WriteLine($"endereço inválido: {erro}");
            return 2;
        }

        string nome = Valor(args, posJogador) ?? "Vizinho";
        int tile = int.Parse(Opcao(args, "--tile") ?? "100");
        int riqueza = int.Parse(Opcao(args, "--riqueza") ?? "12000");
        int ficar = int.Parse(Opcao(args, "--ficar") ?? "120");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var conexao = await new DirectTransport().ConectarAsync(endereco, cts.Token);
        Console.WriteLine($"conectado em {conexao.Descricao} como \"{nome}\"");

        string playerId = "jogador-falso-" + nome.ToLowerInvariant();
        conexao.Enviar(new Handshake
        {
            PlayerId = playerId,
            DisplayName = nome,
            Capabilities = Capabilities.WorldEvents,
            WorldModSetHash = "sha256:mods-de-teste",
        });

        var resposta = Receber(conexao);
        if (resposta.Id == MessageId.HandshakeRecusado)
        {
            var recusa = resposta.Decode(HandshakeRecusado.Read);
            Console.Error.WriteLine($"recusado ({recusa.Motivo}): {recusa.Explicacao}");
            return 1;
        }

        conexao.Enviar(new MundoSincronizacaoCursor { Cursor = 0 });
        conexao.Enviar(new MundoPublicar
        {
            Tipo = TipoEventoMundo.AssentamentoPublicado,
            Payload = new AssentamentoPayload
            {
                ColonyId = "colonia-de-" + nome.ToLowerInvariant(),
                Nome = nome,
                Tile = tile,
                Riqueza = riqueza,
            }.ParaBytes(),
        });
        Console.WriteLine($"assentamento \"{nome}\" publicado no tile {tile} (riqueza {riqueza:N0})");
        Console.WriteLine($"ficando online por {ficar}s — Ctrl+C encerra");

        var fim = DateTime.UtcNow.AddSeconds(ficar);
        while (DateTime.UtcNow < fim)
        {
            while (conexao.TentarReceber(out var envelope))
                Relatar(envelope);
            await Task.Delay(100);
        }

        if (args.Contains("--remover-ao-sair"))
        {
            conexao.Enviar(new MundoPublicar
            {
                Tipo = TipoEventoMundo.AssentamentoRemovido,
                Payload = new AssentamentoPayload
                {
                    ColonyId = "colonia-de-" + nome.ToLowerInvariant(),
                    Nome = nome,
                    Tile = tile,
                    Riqueza = 0,
                }.ParaBytes(),
            });
            Console.WriteLine("assentamento removido do planeta");
            await Task.Delay(300);
        }

        Console.WriteLine("saindo — os outros devem ver este jogador ficar offline");
        return 0;
    }

    static void Relatar(Envelope envelope)
    {
        switch (envelope.Id)
        {
            case MessageId.MundoEvento:
            {
                var evento = envelope.Decode(MundoEvento.Read).Evento;
                var payload = evento.Decodificar(AssentamentoPayload.Read);
                Console.WriteLine($"  ← seq {evento.Seq} {evento.Tipo} de {evento.Autor}: {payload.Nome} no tile {payload.Tile}");
                break;
            }
            case MessageId.MundoPresenca:
            {
                var presenca = envelope.Decode(MundoPresenca.Read);
                Console.WriteLine($"  ← presença: {presenca.DisplayName} {(presenca.Online ? "online" : "offline")}");
                break;
            }
            default:
                Console.WriteLine($"  ← {envelope.Id}");
                break;
        }
    }

    static string? Valor(string[] args, int pos) =>
        pos + 1 < args.Length ? args[pos + 1] : null;

    static string? Opcao(string[] args, string nome)
    {
        int pos = Array.IndexOf(args, nome);
        return pos >= 0 ? Valor(args, pos) : null;
    }

    static Envelope Receber(IConexao conexao)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            if (conexao.TentarReceber(out var envelope)) return envelope;
            Thread.Sleep(20);
        }
        throw new TimeoutException("o coordenador não respondeu em 15s");
    }
}
