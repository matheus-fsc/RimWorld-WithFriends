using System.Text;
using WithFriends.Protocol;
using WithFriends.Server.Colonias;
using Xunit;

namespace WithFriends.Tests;

public class ArmazenamentoTests : IDisposable
{
    readonly string raiz = Path.Combine(Path.GetTempPath(), "withfriends-testes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true);
    }

    [Fact]
    public void Em_disco_o_caminho_vem_do_hash_e_da_identidade()
    {
        var armazenamento = new ArmazenamentoEmDisco(raiz);
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        var conteudo = Encoding.UTF8.GetBytes("save de verdade");
        string hash = Hashing.OfBytes(conteudo);

        armazenamento.GravarConteudo(hash, conteudo);
        armazenamento.AnexarAoIndice(identity, "{\"GameTick\":1}");
        armazenamento.AnexarAoIndice(identity, "{\"GameTick\":2}");

        Assert.True(armazenamento.ConteudoExiste(hash));
        Assert.Equal(conteudo, armazenamento.LerConteudo(hash));
        Assert.Equal(2, armazenamento.LerIndice(identity).Count);

        // Nenhum arquivo carrega o nome do save do jogador: o layout é
        // derivado de (player_id, colony_id) e do hash — §7.1 regra 5.
        var arquivos = Directory.GetFiles(raiz, "*", SearchOption.AllDirectories);
        Assert.All(arquivos, caminho => Assert.DoesNotContain("save de verdade", Path.GetFileName(caminho)));
    }

    [Fact]
    public void Gravar_o_mesmo_hash_duas_vezes_e_idempotente()
    {
        var armazenamento = new ArmazenamentoEmDisco(raiz);
        var conteudo = Encoding.UTF8.GetBytes("estado");
        string hash = Hashing.OfBytes(conteudo);

        armazenamento.GravarConteudo(hash, conteudo);
        armazenamento.GravarConteudo(hash, conteudo);

        Assert.Single(Directory.GetFiles(Path.Combine(raiz, "conteudo"), "*.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public void Monitor_reconstroi_a_referencia_depois_de_reinicio()
    {
        // Limitação registrada na ADR 0002 e exposta em uso real: o servidor
        // guardava "último tick visto" só em memória, e reiniciar deixava a
        // primeira mensagem seguinte sem nada com que comparar.
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        var alertas = new AlertasEmMemoria();
        var armazenamento = new ArmazenamentoEmDisco(raiz);

        var antes = new CheckpointStore(
            armazenamento, new MonitorIntegridade(alertas, armazenamento), alertas);
        var bytes = Encoding.UTF8.GetBytes("estado no tick 500000");
        antes.Aceitar(new Protocol.Messages.ColoniaCheckpoint
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            Conteudo = bytes,
            Metadata = new Protocol.Messages.CheckpointMetadata
            {
                GameTick = 500_000,
                ContentHash = Hashing.OfBytes(bytes),
                ModSetHash = "sha256:mods",
                WallClock = DateTime.UtcNow,
            },
        }, DateTime.UtcNow);

        // Coordenador reiniciado: instâncias novas, mesmo diretório.
        var depois = new CheckpointStore(
            new ArmazenamentoEmDisco(raiz),
            new MonitorIntegridade(alertas, new ArmazenamentoEmDisco(raiz)),
            alertas);

        // Um save antigo carregado por cima ainda é detectado.
        var encontrados = depois.Aceitar(new Protocol.Messages.ColoniaHeartbeat
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            GameTick = 400_000,
            StateFingerprint = "sha256:qualquer",
        }, DateTime.UtcNow);

        Assert.Contains(encontrados, a => a.Tipo == Protocol.Messages.TipoAlerta.TickRegrediu);
    }

    [Fact]
    public void Primeiro_heartbeat_depois_do_reinicio_nao_alerta_a_toa()
    {
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        var alertas = new AlertasEmMemoria();
        var armazenamento = new ArmazenamentoEmDisco(raiz);

        var antes = new CheckpointStore(
            armazenamento, new MonitorIntegridade(alertas, armazenamento), alertas);
        var bytes = Encoding.UTF8.GetBytes("estado");
        antes.Aceitar(new Protocol.Messages.ColoniaCheckpoint
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            Conteudo = bytes,
            Metadata = new Protocol.Messages.CheckpointMetadata
            {
                GameTick = 1_000,
                ContentHash = Hashing.OfBytes(bytes),
                ModSetHash = "sha256:mods",
                WallClock = DateTime.UtcNow,
            },
        }, DateTime.UtcNow);

        var depois = new CheckpointStore(
            new ArmazenamentoEmDisco(raiz),
            new MonitorIntegridade(alertas, new ArmazenamentoEmDisco(raiz)),
            alertas);

        // A impressão digital não é persistida: o primeiro heartbeat só
        // rearma a referência, sem acusar nada.
        var encontrados = depois.Aceitar(new Protocol.Messages.ColoniaHeartbeat
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            GameTick = 2_000,
            StateFingerprint = "sha256:estado-vivo",
        }, DateTime.UtcNow);

        Assert.Empty(encontrados);
    }

    [Fact]
    public void Store_em_disco_sobrevive_a_reinicio_do_servidor()
    {
        var identity = new ColonyIdentity("jogador-1", "colonia-a");
        var alertas = new AlertasEmMemoria();

        var primeiro = new CheckpointStore(
            new ArmazenamentoEmDisco(raiz), new MonitorIntegridade(alertas), alertas);
        var bytes = Encoding.UTF8.GetBytes("estado guardado");
        primeiro.Aceitar(new Protocol.Messages.ColoniaCheckpoint
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            Conteudo = bytes,
            Metadata = new Protocol.Messages.CheckpointMetadata
            {
                GameTick = 42,
                ContentHash = Hashing.OfBytes(bytes),
                ModSetHash = "sha256:mods",
                WallClock = DateTime.UtcNow,
            },
        }, DateTime.UtcNow);

        // Outro processo, mesmo diretório.
        var depoisDoReinicio = new CheckpointStore(
            new ArmazenamentoEmDisco(raiz), new MonitorIntegridade(alertas), alertas);

        var historico = depoisDoReinicio.Historico(identity);
        Assert.Single(historico);
        Assert.Equal(42, historico[0].GameTick);
        Assert.Equal(bytes, depoisDoReinicio.Recuperar(historico[0].ContentHash));
    }
}
