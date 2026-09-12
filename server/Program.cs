using System.Net.Sockets;
using WithFriends.Protocol;
using WithFriends.Server;
using WithFriends.Server.Colonias;
using WithFriends.Server.Mundo;
using WithFriends.Server.Sessoes;

int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 25555;
string dados = args.Length > 1 ? args[1] : "dados";

using var listener = new Listener(port);
listener.Start();
Console.WriteLine($"WithFriends — coordenador ouvindo em {listener.LocalEndPoint} (dual-stack IPv6/IPv4)");
Console.WriteLine($"Protocolo v{ProtocolVersion.Current} (mínimo aceito: v{ProtocolVersion.MinimumSupported})");
Console.WriteLine($"Checkpoints em {Path.GetFullPath(dados)} — append-only, endereçados por hash");

var alertas = new AlertasConsole();
var armazenamento = new ArmazenamentoEmDisco(dados);
var checkpoints = new CheckpointStore(
    armazenamento,
    // O monitor lê o índice: reiniciar o coordenador não apaga a referência.
    new MonitorIntegridade(alertas, armazenamento),
    alertas);

var mundo = new LogDeEventos(Path.Combine(dados, "mundo", "eventos.jsonl"));
var presenca = new Presenca();
var sessoes = new BrokerDeSessoes(presenca);

var handshake = new HandshakeHandler(
    serverName: Environment.MachineName,
    offered: Capabilities.WorldEvents | Capabilities.Checkpoints | Capabilities.Sessions);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    TcpClient cliente;
    try { cliente = await listener.AcceptAsync(cts.Token); }
    catch (OperationCanceledException) { break; }

    var conexao = new Conexao(cliente, handshake, checkpoints, mundo, presenca, sessoes);
    _ = Task.Run(() => conexao.AtenderAsync(cts.Token), cts.Token);
}
