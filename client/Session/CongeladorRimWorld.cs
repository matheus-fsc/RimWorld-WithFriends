using System;
using System.IO;
using RimWorld;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Client.Session.Portas;

namespace WithFriends.Client.Session;

/// <summary>
/// Adaptador do <see cref="ICongelador"/> — anel 2.
///
/// Implementa a propriedade de segurança da §2.3: antes de qualquer tick
/// compartilhado, a colônia é congelada e vira um <c>checkpoint_pre_sessao</c>.
/// Se a sessão abortar, volta-se a ele. **Perde-se o encontro, nunca a
/// colônia.**
///
/// Membros do jogo tocados: <c>TickManager.CurTimeSpeed</c>,
/// <c>GameDataSaveLoader.LoadGame</c>, <c>GenFilePaths</c>. Todos públicos e
/// antigos.
/// </summary>
public sealed class CongeladorRimWorld : ICongelador
{
    /// <summary>Prefixo do arquivo temporário de restauração, visível em Saves.</summary>
    public const string PrefixoDeRestauracao = "WithFriends-restauracao-";

    /// <summary>
    /// Estado do congelamento vive no <see cref="SincronizacaoComponent"/>, que
    /// é por partida e persiste no save.
    ///
    /// Já foi estático aqui e deu errado de duas formas: sobrevivia a um
    /// <c>LoadGame</c> — apontando para o ponto de retorno de outra colônia — e
    /// não enxergava o jogador despausando pela UI do jogo.
    /// </summary>
    static SincronizacaoComponent Estado =>
        SincronizacaoComponent.Atual
        ?? throw new InvalidOperationException("Não há partida carregada.");

    public string? HashPreSessao =>
        SincronizacaoComponent.Atual is { HashPreSessao.Length: > 0 } estado ? estado.HashPreSessao : null;

    public string CongelarECheckpoint()
    {
        var estado = Estado;

        // Congelar é garantir que está pausado AGORA, não lembrar que um dia
        // pausamos. O jogador pode ter despausado pela UI do jogo entre um
        // congelamento e outro — e aí o segundo precisa pausar de novo.
        if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
        {
            if (!estado.Congelado) estado.VelocidadeAnterior = Find.TickManager.CurTimeSpeed;
            Session.ControleDeVelocidade.ComoSistema(
                () => Find.TickManager.CurTimeSpeed = TimeSpeed.Paused);
        }
        estado.Congelado = true;

        // preSessao: true marca o checkpoint como ponto de retorno — a §7.2
        // mantém os três mais recentes desses para sempre.
        var checkpoint = CheckpointWriter.Criar(preSessao: true);
        estado.HashPreSessao = checkpoint.Metadata.ContentHash;

        // Durabilidade: o ponto de retorno também vai para o coordenador, para
        // sobreviver a um problema na máquina local (§2.2).
        //
        // Se não for, o jogador precisa saber **na hora**: um ponto de retorno
        // que só existe em um disco não é uma garantia, é uma esperança.
        if (!WithFriendsMod.Cliente.Enviar(checkpoint.ParaMensagem()))
        {
            Log.Warning(
                $"[WithFriends] o ponto de retorno {checkpoint.Metadata.ContentHash} existe " +
                "APENAS nesta máquina — o coordenador não recebeu.");
            Messages.Message(
                "With Friends: ponto de retorno salvo só nesta máquina (sem conexão).",
                MessageTypeDefOf.CautionInput, historical: false);
        }

        estado.UltimoContentHash = estado.HashPreSessao;

        Log.Message(
            $"[WithFriends] congelado no tick {checkpoint.Metadata.GameTick} — " +
            $"checkpoint pré-sessão {estado.HashPreSessao} ({checkpoint.Conteudo.Length:N0} bytes)");

        return estado.HashPreSessao;
    }

    /// <summary>
    /// Volta a colônia ao checkpoint indicado.
    ///
    /// Antes de qualquer coisa, guarda o **estado atual** num checkpoint novo.
    /// Voltar no tempo não pode apagar nada: append-only vale também para o
    /// rollback (§7.1 regra 1). Se o aborto tiver sido um engano, o estado
    /// descartado continua recuperável.
    /// </summary>
    public void Restaurar(string contentHash, bool guardarEstadoAtual = true)
    {
        string origem = CaminhoDoCheckpoint(contentHash);
        if (!File.Exists(origem))
            throw new FileNotFoundException(
                $"Checkpoint {contentHash} não está nesta máquina. " +
                "Peça a restauração ao coordenador antes de voltar.", origem);

        // Voltando de uma visita, o jogo carregado é o do ANFITRIÃO — guardá-lo
        // como checkpoint da colônia do visitante seria gravar o save de outra
        // pessoa sob a identidade dele.
        if (guardarEstadoAtual)
        {
            try
            {
                var antes = CheckpointWriter.Criar();
                Log.Message($"[WithFriends] estado antes do rollback guardado como {antes.Metadata.ContentHash}");
            }
            catch (Exception e)
            {
                // Não impede o rollback: o ponto de retorno é o que importa agora.
                Log.Warning($"[WithFriends] não foi possível guardar o estado atual antes do rollback: {e.Message}");
            }
        }

        // LoadGame resolve pelo nome dentro da pasta de saves, então o
        // checkpoint precisa aparecer lá. O nome é derivado do hash — nome de
        // arquivo continua sendo detalhe de armazenamento (§7.1 regra 5).
        string nome = PrefixoDeRestauracao + contentHash.Substring(Protocol.Hashing.Prefix.Length, 12);
        string destino = GenFilePaths.FilePathForSavedGame(nome);
        File.Copy(origem, destino, overwrite: true);

        Log.Message($"[WithFriends] restaurando {contentHash} a partir de {destino}");

        // O rollback é o pico de memória do mod inteiro: a partida viva, os
        // bytes que acabamos de serializar e a partida que vai entrar, todas ao
        // mesmo tempo. Num mapa 250x250 com 16 mil things isso é muito, e houve
        // um SIGSEGV dentro do GC do mono exatamente aqui.
        //
        // Não é prova de causa — um crash no coletor não se explica de fora —
        // mas o pico é real e este é o único ponto em que dá para baixá-lo de
        // graça: o carregamento já leva segundos, a coleta não aparece.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        GameDataSaveLoader.LoadGame(nome);
    }

    public void Descongelar()
    {
        var estado = SincronizacaoComponent.Atual;
        if (estado == null || !estado.Congelado) return;

        Session.ControleDeVelocidade.ComoSistema(
            () => Find.TickManager.CurTimeSpeed = estado.VelocidadeAnterior);
        estado.Congelado = false;
        Log.Message($"[WithFriends] descongelado — velocidade {estado.VelocidadeAnterior}");
    }

    /// <summary>
    /// Pede ao coordenador um checkpoint que não está nesta máquina — §7.3.
    /// O servidor entrega; quem decide aplicar continua sendo o jogador.
    /// </summary>
    public static void Solicitar(string contentHash)
    {
        var identidade = ColonyIdentityComponent.Atual;
        var cliente = WithFriendsMod.Cliente;

        if (identidade == null || !cliente.Conectado)
        {
            Log.Warning("[WithFriends] sem conexão com o coordenador para pedir o checkpoint.");
            return;
        }

        cliente.Enviar(new Protocol.Messages.ColoniaRestauracao
        {
            PlayerId = identidade.Identity.PlayerId,
            ColonyId = identidade.Identity.ColonyId,
            ContentHash = contentHash,
        });
        Log.Message($"[WithFriends] pedindo {contentHash} ao coordenador…");
    }

    /// <summary>
    /// Grava localmente o checkpoint que o coordenador devolveu. **Não
    /// restaura sozinho**: o servidor nunca impõe uma versão do save (§7.3).
    /// </summary>
    public static void Receber(Protocol.Messages.ColoniaRestauracao resposta)
    {
        if (resposta.Erro.Length > 0)
        {
            Log.Warning($"[WithFriends] o coordenador não devolveu {resposta.ContentHash}: {resposta.Erro}");
            return;
        }

        string real = Protocol.Hashing.OfBytes(resposta.Conteudo);
        if (real != resposta.ContentHash)
        {
            // Metadado é afirmação, conteúdo é fato (ADR 0002).
            Log.Error(
                $"[WithFriends] o conteúdo devolvido não confere: pedi {resposta.ContentHash}, " +
                $"veio {real}. Nada foi gravado.");
            return;
        }

        Directory.CreateDirectory(CheckpointWriter.PastaCheckpoints);
        string destino = CaminhoDoCheckpoint(resposta.ContentHash);
        File.WriteAllBytes(destino, resposta.Conteudo);

        Log.Message(
            $"[WithFriends] checkpoint {resposta.ContentHash} recebido " +
            $"({resposta.Conteudo.Length:N0} bytes) e gravado em {destino}. " +
            "Use \"Voltar ao checkpoint pré-sessão\" para aplicá-lo.");
    }

    public static string CaminhoDoCheckpoint(string contentHash) =>
        Path.Combine(
            CheckpointWriter.PastaCheckpoints,
            contentHash.Substring(Protocol.Hashing.Prefix.Length) + ".rws");
}
