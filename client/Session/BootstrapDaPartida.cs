using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using RimWorld;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;

/// <summary>
/// O bootstrap da visita: a **partida** do anfitrião atravessa para o
/// visitante (ADR 0010).
///
/// Não é o mapa. Um mapa referencia ideologias, políticas, facções e relações
/// que vivem na partida; sozinho ele abre e não se reconecta a nada — medido,
/// e é o que o RT #273 sempre foi.
///
/// Medido: 13,12 MB de save → 1,31 MB comprimidos, ~1,4 s para serializar.
/// </summary>
public static class BootstrapDaPartida
{
    /// <summary>
    /// Anfitrião: manda a partida congelada **e recarrega a própria**.
    ///
    /// Recarregar parece desperdício — o anfitrião já tem o jogo na memória.
    /// Não é: um jogo vivo e o mesmo jogo recém-carregado **não são idênticos**.
    /// O vivo tem motes em voo, caches quentes, listas em outra ordem. Medimos
    /// 891 coisas de diferença entre os dois lados, e o consumo de RNG do
    /// anfitrião era 3,5× o do visitante.
    ///
    /// É o que o Multiplayer faz para criar um "join point"
    /// (`CommandType.CreateJoinPoint` → `SaveLoad.SaveAndReload()` em **todos**
    /// os clientes, não só em quem entra). Os dois lados partem do mesmo
    /// estado restaurado, não de "um vivo e um restaurado".
    /// </summary>
    public static void Enviar(string sessaoId, long tick, SessaoInicio inicio, string? hashPreSessao)
    {
        var relogio = Stopwatch.StartNew();

        Checkpoint checkpoint;
        try
        {
            checkpoint = CheckpointWriter.Criar();
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] não foi possível serializar a partida da visita: {e.Message}");
            return;
        }

        byte[] comprimido = Comprimir(checkpoint.Conteudo);
        relogio.Stop();

        Log.Message(
            $"[WithFriends] enviando a partida da visita: " +
            $"{checkpoint.Conteudo.Length / 1024.0 / 1024.0:N2} MB → " +
            $"{comprimido.Length / 1024.0 / 1024.0:N2} MB em {relogio.Elapsed.TotalMilliseconds:N0} ms");

        WithFriendsMod.Cliente.Enviar(new SessaoPartida
        {
            SessaoId = sessaoId,
            Tick = tick,
            TamanhoCru = checkpoint.Conteudo.Length,
            ContentHash = checkpoint.Metadata.ContentHash,
            Comprimido = comprimido,
        });

        // O anfitrião entra na visita pela mesma porta que o visitante: pelo
        // save. Assim os dois começam do mesmo estado restaurado.
        string nome = NomeDoSave(sessaoId);
        File.WriteAllBytes(GenFilePaths.FilePathForSavedGame(nome), checkpoint.Conteudo);

        VisitaEmAndamento.Comecar(inicio, hashPreSessao, nome, souVisitante: false);
        RelogioDeSessaoRimWorld.LimitarAteEstatico(tick);

        Messages.Message("Preparando a visita…", MessageTypeDefOf.NeutralEvent, historical: false);
        GameDataSaveLoader.LoadGame(nome);
    }

    static string NomeDoSave(string sessaoId) => $"WithFriends-visita-{sessaoId}";

    /// <summary>
    /// Visitante: confere, grava e **entra na partida do anfitrião**.
    ///
    /// Daqui em diante ele está jogando lá dentro. A colônia dele volta no fim
    /// da visita, pelo <c>checkpoint_pre_sessao</c> — que é por isso que o
    /// congelamento acontece **antes** de qualquer coisa (§2.3).
    /// </summary>
    public static void Receber(SessaoPartida mensagem, SessaoInicio inicio, string? hashPreSessao)
    {
        var relogio = Stopwatch.StartNew();

        byte[] cru;
        try
        {
            cru = Descomprimir(mensagem.Comprimido, mensagem.TamanhoCru);
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] a partida da visita chegou corrompida: {e.Message}");
            return;
        }

        string real = Hashing.OfBytes(cru);
        if (real != mensagem.ContentHash)
        {
            Log.Error(
                $"[WithFriends] a partida recebida não confere: esperava {mensagem.ContentHash}, " +
                $"veio {real}. Nada foi gravado.");
            return;
        }

        // LoadGame resolve pelo nome dentro da pasta de saves.
        string nome = NomeDoSave(mensagem.SessaoId);
        File.WriteAllBytes(GenFilePaths.FilePathForSavedGame(nome), cru);
        relogio.Stop();

        Log.Message(
            $"[WithFriends] partida da visita recebida e verificada: " +
            $"{mensagem.Comprimido.Length / 1024.0 / 1024.0:N2} MB → " +
            $"{cru.Length / 1024.0 / 1024.0:N2} MB em {relogio.Elapsed.TotalMilliseconds:N0} ms");

        // O que precisa sobreviver à troca de partida vai para fora dela.
        VisitaEmAndamento.Comecar(inicio, hashPreSessao, nome, souVisitante: true);

        // **Antes** de carregar: o freio de tick é estático e sobrevive à
        // troca de partida, então ele já chega armado do outro lado.
        //
        // Sem isto o jogo recém-carregado ticka alguns frames antes de a
        // sessão ser retomada — e o visitante fica à frente do anfitrião, que
        // está congelado. Foi o que abortou a primeira visita: 2 ticks de
        // diferença, divergência real.
        RelogioDeSessaoRimWorld.LimitarAteEstatico(mensagem.Tick);

        Messages.Message("Entrando na visita…", MessageTypeDefOf.PositiveEvent, historical: false);
        GameDataSaveLoader.LoadGame(nome);
    }

    /// <summary>Fim da visita: o save temporário não fica para trás (ADR 0008).</summary>
    public static void Descartar(string? nome)
    {
        if (string.IsNullOrEmpty(nome)) return;

        try
        {
            string caminho = GenFilePaths.FilePathForSavedGame(nome);
            if (File.Exists(caminho))
            {
                File.Delete(caminho);
                Log.Message($"[WithFriends] save da visita descartado: {nome}");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[WithFriends] não foi possível descartar {nome}: {e.Message}");
        }
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
