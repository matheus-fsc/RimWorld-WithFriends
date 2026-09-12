using System;
using System.IO;
using Verse;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Client.World;

namespace WithFriends.Client.Colony;

/// <summary>
/// Produz um checkpoint da colônia atual: escreve o save com a mesma
/// maquinaria do jogo, calcula o hash do conteúdo e monta os metadados
/// verificáveis da §7.1 regra 2.
///
/// Escreve na **nossa** pasta, com nome derivado do hash — nunca na pasta de
/// saves do jogador e nunca com o nome que o jogador deu à colônia.
/// </summary>
public static class CheckpointWriter
{
    public static string PastaCheckpoints =>
        Path.Combine(GenFilePaths.SaveDataFolderPath, "WithFriends", "checkpoints");

    /// <summary>
    /// Serializa a partida atual e devolve conteúdo + metadados.
    /// Deve rodar fora do tick (o Scribe não é reentrante).
    /// </summary>
    public static Checkpoint Criar(bool preSessao = false)
    {
        if (Current.Game == null)
            throw new InvalidOperationException("Não há partida carregada.");

        Directory.CreateDirectory(PastaCheckpoints);
        string pendente = Path.Combine(PastaCheckpoints, "pendente.rws");

        // Mesmo corpo de GameDataSaveLoader.SaveGame, com caminho nosso.
        SafeSaver.Save(pendente, "savegame", delegate
        {
            ScribeMetaHeaderUtility.WriteMetaHeader();
            Game jogo = Current.Game;
            Scribe_Deep.Look(ref jogo, "game");
        });

        byte[] conteudo = File.ReadAllBytes(pendente);
        string contentHash = Hashing.OfBytes(conteudo);

        // Endereçado por hash, append-only: se já existe, é o mesmo conteúdo.
        string definitivo = Path.Combine(PastaCheckpoints, NomeDeArquivo(contentHash));
        if (File.Exists(definitivo)) File.Delete(pendente);
        else File.Move(pendente, definitivo);

        var identity = ColonyIdentityComponent.Atual!.Identity;

        return new Checkpoint(
            identity,
            new CheckpointMetadata
            {
                GameTick = Find.TickManager.TicksGame,
                WorldCursor = Current.Game.GetComponent<WorldCursorComponent>()?.WorldCursor ?? 0,
                ModSetHash = ModSetHash.Calcular(),
                ContentHash = contentHash,
                WallClock = DateTime.UtcNow,
                PreSessao = preSessao,
            },
            conteudo,
            definitivo);
    }

    /// <summary>
    /// Monta um checkpoint a partir de um <c>.rws</c> que o próprio jogo
    /// acabou de escrever. Evita serializar tudo de novo: o autosave já pagou
    /// esse custo, e serializar duas vezes seria dobrar o engasgo por nada.
    /// </summary>
    public static Checkpoint DeArquivo(string caminho, bool preSessao = false)
    {
        byte[] conteudo = File.ReadAllBytes(caminho);
        var identity = ColonyIdentityComponent.Atual!.Identity;

        return new Checkpoint(
            identity,
            new CheckpointMetadata
            {
                GameTick = Find.TickManager.TicksGame,
                WorldCursor = Current.Game.GetComponent<WorldCursorComponent>()?.WorldCursor ?? 0,
                ModSetHash = ModSetHash.Calcular(),
                ContentHash = Hashing.OfBytes(conteudo),
                WallClock = DateTime.UtcNow,
                PreSessao = preSessao,
            },
            conteudo,
            caminho);
    }

    /// <summary>
    /// Heartbeat <c>(tick, state_fingerprint)</c> — §7.1 regra 4.
    /// A impressão digital é do estado vivo (ver <see cref="FingerprintVivo"/>),
    /// não do último checkpoint: entre checkpoints o hash do save fica igual
    /// por construção.
    /// </summary>
    public static ColoniaHeartbeat Heartbeat()
    {
        var identity = ColonyIdentityComponent.Atual!.Identity;
        return new ColoniaHeartbeat
        {
            PlayerId = identity.PlayerId,
            ColonyId = identity.ColonyId,
            GameTick = Find.TickManager.TicksGame,
            StateFingerprint = FingerprintVivo.Calcular(),
        };
    }

    static string NomeDeArquivo(string contentHash) =>
        contentHash.Substring(Hashing.Prefix.Length) + ".rws";
}

public readonly struct Checkpoint
{
    public readonly ColonyIdentity Identity;
    public readonly CheckpointMetadata Metadata;
    public readonly byte[] Conteudo;
    public readonly string Caminho;

    public Checkpoint(ColonyIdentity identity, CheckpointMetadata metadata, byte[] conteudo, string caminho)
    {
        Identity = identity;
        Metadata = metadata;
        Conteudo = conteudo;
        Caminho = caminho;
    }

    public ColoniaCheckpoint ParaMensagem() => new()
    {
        PlayerId = Identity.PlayerId,
        ColonyId = Identity.ColonyId,
        Metadata = Metadata,
        Conteudo = Conteudo,
    };
}
