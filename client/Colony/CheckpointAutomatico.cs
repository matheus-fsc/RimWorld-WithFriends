using System;
using System.IO;
using HarmonyLib;
using Verse;
using WithFriends.Client.Net;

namespace WithFriends.Client.Colony;

/// <summary>
/// Checkpoint automático, pendurado no save do próprio jogo.
///
/// Por que aqui e não num timer: serializar a partida custa segundos e trava
/// o frame. O jogo já paga esse custo no autosave, num momento em que o
/// jogador espera o engasgo. Pegar carona significa **zero** custo novo — só
/// lemos o arquivo que acabou de ser escrito, em vez de serializar de novo.
///
/// A §7 existe porque perda silenciosa acontece quando ninguém está olhando.
/// Depender de o jogador lembrar de clicar seria repetir isso.
/// </summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
public static class CheckpointAutomatico
{
    static DateTime ultimoEnvio = DateTime.MinValue;

    [HarmonyPostfix]
    public static void AposSalvar(string fileName)
    {
        // Nada aqui pode quebrar o salvamento do jogador. Se der errado,
        // registra e segue — o save dele já está no disco de qualquer forma.
        try
        {
            Tentar(fileName);
        }
        catch (Exception e)
        {
            Log.Warning($"[WithFriends] falha ao enviar checkpoint automático: {e.Message}");
        }
    }

    static void Tentar(string fileName)
    {
        var settings = WithFriendsMod.Settings;
        if (!settings.checkpointAoSalvar) return;

        var cliente = WithFriendsMod.Cliente;
        if (!cliente.Conectado || Current.Game == null) return;

        // Durante a visita o jogo carregado é o do ANFITRIÃO. Mandar isso como
        // checkpoint da colônia do visitante seria gravar o save de outra
        // pessoa sob a identidade dele — §7.1 regra 5 ao contrário.
        if (Session.VisitaEmAndamento.SouVisitante)
        {
            Log.Message("[WithFriends] checkpoint automático suspenso: você está visitando.");
            return;
        }

        var intervalo = TimeSpan.FromMinutes(Math.Max(0, settings.intervaloMinimoMinutos));
        if (DateTime.UtcNow - ultimoEnvio < intervalo) return;

        string caminho = GenFilePaths.FilePathForSavedGame(fileName);
        if (!File.Exists(caminho))
        {
            Log.Warning($"[WithFriends] save não encontrado depois de salvar: {caminho}");
            return;
        }

        var checkpoint = CheckpointWriter.DeArquivo(caminho);
        cliente.Enviar(checkpoint.ParaMensagem());
        ultimoEnvio = DateTime.UtcNow;

        var sincronizacao = SincronizacaoComponent.Atual;
        if (sincronizacao != null)
            sincronizacao.UltimoContentHash = checkpoint.Metadata.ContentHash;

        // A riqueza da colônia mudou desde a última vez; o mapa-mundo dos
        // outros jogadores merece saber (§4).
        World.MundoPublicador.PublicarProprioAssentamento();

        Log.Message(
            $"[WithFriends] checkpoint automático: {checkpoint.Conteudo.Length:N0} bytes, " +
            $"tick {checkpoint.Metadata.GameTick}, {checkpoint.Metadata.ContentHash}");
    }
}
