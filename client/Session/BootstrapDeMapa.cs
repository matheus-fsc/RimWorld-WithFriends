using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using RimWorld;
using Verse;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;

/// <summary>
/// O bootstrap da visita: o mapa do anfitrião atravessa para o visitante.
///
/// Acontece **uma vez**, no início (§4). O que sustenta a visita depois é o
/// lockstep de tempo real, não mais transferência. E no fim o mapa é
/// descartado, sem cache nem reconciliação (ADR 0008).
///
/// Orçamento: 5 segundos com aviso na tela. Medido em colônia madura: ~0,9 s
/// para serializar, 0,64 MB para trafegar (docs/MEDICOES.md).
/// </summary>
public static class BootstrapDeMapa
{
    public static string PastaRecebidos =>
        Path.Combine(GenFilePaths.SaveDataFolderPath, "WithFriends", "mapas-recebidos");

    /// <summary>Anfitrião: serializa, comprime e manda.</summary>
    public static void Enviar(string sessaoId, Map mapa, long tick)
    {
        var relogio = Stopwatch.StartNew();

        var medicao = MedicaoDeMapa.SoOMapa(mapa);
        if (medicao.Erro != null)
        {
            Log.Error($"[WithFriends] não foi possível serializar o mapa da sessão: {medicao.Erro}");
            return;
        }

        byte[] cru = File.ReadAllBytes(MedicaoDeMapa.CaminhoDoMapa(mapa.uniqueID));
        byte[] comprimido = Comprimir(cru);
        relogio.Stop();

        Log.Message(
            $"[WithFriends] enviando mapa {mapa.uniqueID} da sessão: " +
            $"{cru.Length / 1024.0 / 1024.0:N2} MB → {comprimido.Length / 1024.0 / 1024.0:N2} MB " +
            $"em {relogio.Elapsed.TotalMilliseconds:N0} ms");

        WithFriendsMod.Cliente.Enviar(new SessaoMapa
        {
            SessaoId = sessaoId,
            MapaId = mapa.uniqueID,
            Tick = tick,
            TamanhoCru = cru.Length,
            ContentHash = Hashing.OfBytes(cru),
            Comprimido = comprimido,
        });
    }

    /// <summary>
    /// Visitante: confere, descomprime e grava.
    ///
    /// **Ainda não insere o mapa na partida** — isso é o próximo passo, e tem
    /// ordem própria (cross-refs, registro em Find.Maps, MapDrawer). Aqui o
    /// mapa chega, é verificado e fica pronto no disco.
    /// </summary>
    public static void Receber(SessaoMapa mensagem)
    {
        var relogio = Stopwatch.StartNew();

        byte[] cru;
        try
        {
            cru = Descomprimir(mensagem.Comprimido, mensagem.TamanhoCru);
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] mapa da sessão chegou corrompido (descompressão): {e.Message}");
            return;
        }

        // Metadado é afirmação, conteúdo é fato — a mesma regra da ADR 0002,
        // agora no caminho da sessão.
        string real = Hashing.OfBytes(cru);
        if (real != mensagem.ContentHash)
        {
            Log.Error(
                $"[WithFriends] o mapa recebido não confere: esperava {mensagem.ContentHash}, " +
                $"veio {real}. Nada foi gravado.");
            return;
        }

        Directory.CreateDirectory(PastaRecebidos);
        string destino = Path.Combine(PastaRecebidos, $"sessao-{mensagem.SessaoId}-mapa-{mensagem.MapaId}.xml");
        File.WriteAllBytes(destino, cru);
        relogio.Stop();

        Log.Message(
            $"[WithFriends] mapa {mensagem.MapaId} recebido e verificado: " +
            $"{mensagem.Comprimido.Length / 1024.0 / 1024.0:N2} MB → " +
            $"{cru.Length / 1024.0 / 1024.0:N2} MB em {relogio.Elapsed.TotalMilliseconds:N0} ms\n" +
            $"  tick de sessão do congelamento: {mensagem.Tick}\n" +
            $"  em {destino}");

        Messages.Message(
            $"Mapa da visita recebido ({cru.Length / 1024 / 1024} MB).",
            MessageTypeDefOf.TaskCompletion, historical: false);

        Colony.SincronizacaoComponent.Atual?.Sessao.MapaRecebido(mensagem.MapaId, destino);
    }

    static byte[] Comprimir(byte[] conteudo)
    {
        using var destino = new MemoryStream();
        using (var gzip = new GZipStream(destino, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(conteudo, 0, conteudo.Length);
        return destino.ToArray();
    }

    static byte[] Descomprimir(byte[] comprimido, int tamanhoEsperado)
    {
        using var origem = new MemoryStream(comprimido, writable: false);
        using var gzip = new GZipStream(origem, CompressionMode.Decompress);
        using var destino = new MemoryStream(Math.Max(tamanhoEsperado, 1024));
        gzip.CopyTo(destino);
        return destino.ToArray();
    }
}
