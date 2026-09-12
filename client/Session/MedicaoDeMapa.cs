using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Verse;
using WithFriends.Client.Colony;

namespace WithFriends.Client.Session;

/// <summary>
/// Quanto custa levar **um mapa** para o outro lado.
///
/// Primeira tarefa do passo B da ADR 0007. A pergunta não é acadêmica: o
/// bootstrap da visita transfere um mapa, e a ADR 0008 diz que o teto de
/// tolerância é "o que um amigo aceita esperar olhando para uma barra".
///
/// Mede três coisas, porque são três custos diferentes:
/// 1. serializar só o mapa (o que a visita precisa);
/// 2. serializar a partida inteira (o que o checkpoint já faz hoje);
/// 3. comprimir (o que de fato trafega).
/// </summary>
public static class MedicaoDeMapa
{
    public readonly struct Resultado
    {
        public string Nome { get; init; }
        public long Bytes { get; init; }
        public long BytesComprimidos { get; init; }
        public double MsSerializacao { get; init; }
        public double MsCompressao { get; init; }
        public string? Erro { get; init; }

        public override string ToString() =>
            Erro != null
                ? $"  {Nome,-28} FALHOU: {Erro}"
                : $"  {Nome,-28} {Bytes / 1024.0 / 1024.0,7:N2} MB  →  " +
                  $"{BytesComprimidos / 1024.0 / 1024.0,6:N2} MB comprimido  " +
                  $"({MsSerializacao,7:N0} ms + {MsCompressao,5:N0} ms)";
    }

    public static string CaminhoDoMapa(int mapaId) =>
        Path.Combine(PastaDeMedicao, $"mapa-{mapaId}.xml");

    static string PastaDeMedicao =>
        Path.Combine(GenFilePaths.SaveDataFolderPath, "WithFriends", "medicao");

    /// <summary>
    /// Serializa um mapa sozinho, com o mesmo Scribe do jogo.
    ///
    /// Pode falhar: um mapa referencia mundo, facções e coisas fora dele, e o
    /// Scribe espera o contexto de uma partida inteira. Se falhar, o erro **é**
    /// o resultado — e explica por que o Multiplayer salva o jogo todo e
    /// recarrega em vez de serializar mapa isolado.
    /// </summary>
    public static Resultado SoOMapa(Map mapa)
    {
        Directory.CreateDirectory(PastaDeMedicao);
        string caminho = CaminhoDoMapa(mapa.uniqueID);
        var relogio = Stopwatch.StartNew();

        try
        {
            var alvo = mapa;
            Scribe.saver.InitSaving(caminho, "mapaDeSessao");
            try
            {
                Scribe_Deep.Look(ref alvo, "map");
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            relogio.Stop();

            byte[] conteudo = File.ReadAllBytes(caminho);
            var (comprimido, msCompressao) = Comprimir(conteudo);

            return new Resultado
            {
                Nome = $"só o mapa {mapa.uniqueID}",
                Bytes = conteudo.Length,
                BytesComprimidos = comprimido,
                MsSerializacao = relogio.Elapsed.TotalMilliseconds,
                MsCompressao = msCompressao,
            };
        }
        catch (Exception e)
        {
            Scribe.ForceStop();
            return new Resultado { Nome = $"só o mapa {mapa.uniqueID}", Erro = e.Message };
        }
    }

    /// <summary>A partida inteira — o que o checkpoint do M2 já faz.</summary>
    public static Resultado PartidaInteira()
    {
        var relogio = Stopwatch.StartNew();
        try
        {
            var checkpoint = CheckpointWriter.Criar();
            relogio.Stop();
            var (comprimido, msCompressao) = Comprimir(checkpoint.Conteudo);

            return new Resultado
            {
                Nome = "partida inteira",
                Bytes = checkpoint.Conteudo.Length,
                BytesComprimidos = comprimido,
                MsSerializacao = relogio.Elapsed.TotalMilliseconds,
                MsCompressao = msCompressao,
            };
        }
        catch (Exception e)
        {
            return new Resultado { Nome = "partida inteira", Erro = e.Message };
        }
    }

    /// <summary>
    /// O outro lado da conta: desserializar o mapa que chegou.
    ///
    /// Não insere o mapa na partida — só reconstrói o objeto e mede. Inserir é
    /// o bootstrap de verdade e tem ordem própria (cross-refs, registro em
    /// Find.Maps, MapDrawer). Aqui a pergunta é mais básica: **o Scribe
    /// consegue trazer de volta?**
    /// </summary>
    public static Resultado CarregarDeVolta(int mapaId)
    {
        string caminho = CaminhoDoMapa(mapaId);
        if (!File.Exists(caminho))
            return new Resultado { Nome = "carregar de volta", Erro = $"não existe {caminho} — meça primeiro." };

        var relogio = Stopwatch.StartNew();
        Map? lido = null;

        try
        {
            Scribe.loader.InitLoading(caminho);
            try
            {
                Scribe_Deep.Look(ref lido, "map");
                Scribe.loader.FinalizeLoading();
            }
            catch
            {
                Scribe.ForceStop();
                throw;
            }
            relogio.Stop();

            long bytes = new FileInfo(caminho).Length;
            return new Resultado
            {
                Nome = $"carregar de volta ({lido?.listerThings?.AllThings?.Count ?? 0:N0} things)",
                Bytes = bytes,
                BytesComprimidos = bytes,
                MsSerializacao = relogio.Elapsed.TotalMilliseconds,
                MsCompressao = 0,
            };
        }
        catch (Exception e)
        {
            Scribe.ForceStop();
            return new Resultado { Nome = "carregar de volta", Erro = e.Message };
        }
    }

    static (long bytes, double ms) Comprimir(byte[] conteudo)
    {
        var relogio = Stopwatch.StartNew();
        using var destino = new MemoryStream();
        using (var gzip = new GZipStream(destino, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(conteudo, 0, conteudo.Length);
        relogio.Stop();
        return (destino.Length, relogio.Elapsed.TotalMilliseconds);
    }

    public static string Contexto(Map mapa) =>
        $"mapa {mapa.uniqueID}: {mapa.Size.x}x{mapa.Size.z}, " +
        $"{mapa.listerThings.AllThings.Count:N0} things, " +
        $"{mapa.mapPawns.AllPawns.Count} pawns, " +
        $"riqueza {mapa.wealthWatcher.WealthTotal:N0}";
}
