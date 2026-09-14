using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Cada instância do jogo escreve o próprio diário.
///
/// <para><b>O problema que isto resolve.</b> As duas instâncias do RimWorld
/// escrevem no mesmo <c>Player.log</c>, e a segunda a abrir apaga o da primeira.
/// Numa divergência, o lado que detecta e o lado que causou raramente são o
/// mesmo — e o log que sobrevive é o de quem abriu por último, que é sorteio
/// puro.</para>
///
/// <para>Já custou caro três vezes: uma em que deduzi a causa a partir do lado
/// errado, e duas em que o rastreio de RNG e o de estado de pawn existiam nos
/// dois lados e só um chegou até aqui. Comparar os dois lados é o método — sem
/// os dois arquivos, não há método.</para>
///
/// <para>O arquivo fica em <c>WithFriends/diario/</c>, nomeado pelo início do id
/// do jogador e pela hora de abertura, então as duas instâncias nunca colidem e
/// dá para saber de quem é cada um.</para>
///
/// <para>Só as linhas do mod entram, mais erros e avisos de qualquer origem —
/// um erro do jogo no meio de uma visita costuma ser a causa, não o ruído.</para>
/// </summary>
[HarmonyPatch]
public static class DiarioDaInstancia
{
    static StreamWriter? arquivo;
    static readonly object Trava = new();

    /// <summary>
    /// Linhas guardadas antes de o diário abrir.
    ///
    /// <para>O mod loga durante a carga, antes de existir pasta de save
    /// utilizável. Sem isto, a parte mais interessante — quantos remendos
    /// instalaram, quais falharam — ficava justamente de fora.</para>
    /// </summary>
    static readonly List<string> Antecipadas = new();

    public static string? Caminho { get; private set; }

    public static void Abrir(string playerId)
    {
        lock (Trava)
        {
            if (arquivo != null) return;

            try
            {
                string pasta = Path.Combine(GenFilePaths.SaveDataFolderPath, "WithFriends", "diario");
                Directory.CreateDirectory(pasta);

                string quem = playerId.Length >= 8 ? playerId.Substring(0, 8) : "sem-id";
                Caminho = Path.Combine(pasta, $"{DateTime.Now:yyyyMMdd-HHmmss}-{quem}.log");

                // **Sem AutoFlush.**
                //
                // Com ele, cada linha vira uma chamada ao disco. O despejo de
                // uma ressincronização são ~44 mil linhas e 10,7 MB: quarenta e
                // quatro mil descargas seguidas, no meio do quadro, com o jogo
                // parado esperando. Foi tempo suficiente para a conexão com o
                // coordenador ser derrubada, e a partida que o anfitrião ia
                // mandar morreu num socket já descartado.
                //
                // A descarga passa a ser por linha importante e por lote — ver
                // `Escrever`. Diário é instrumento; instrumento que atrapalha a
                // medição não mede.
                arquivo = new StreamWriter(Caminho, append: true, Encoding.UTF8) { AutoFlush = false };

                foreach (var linha in Antecipadas) arquivo.WriteLine(linha);
                Antecipadas.Clear();

                arquivo.WriteLine($"=== diário aberto: {DateTime.Now:O}, jogador {playerId} ===");

                // **Descarga na abertura.**
                //
                // O resto sai em lote, e quem fecha o arquivo é `Fechar`. Um
                // processo que morre antes disso — crash nativo, `kill`, o
                // jogador fechando pela janela — leva o buffer junto.
                //
                // Custou uma rodada: a instância subiu, carregou o mod, foi
                // fechada, e o diário ficou com ZERO bytes. Nem o cabeçalho,
                // nem as linhas de subida (quantos remendos instalaram, quais
                // falharam) — que são justamente as que dizem se valia a pena
                // olhar o resto.
                //
                // Uma descarga aqui garante que um diário que existe tem, no
                // mínimo, como ser identificado.
                arquivo.Flush();

                Log.Message($"[WithFriends] diário desta instância: {Caminho}");
            }
            catch (Exception e)
            {
                // Diário é instrumento, não recurso: se não dá para escrever, o
                // jogo segue. §14.5.
                Log.Warning($"[WithFriends] não foi possível abrir o diário: {e.Message}");
                arquivo = null;
            }
        }
    }

    public static void Fechar()
    {
        lock (Trava)
        {
            arquivo?.Flush();
            arquivo?.Dispose();
            arquivo = null;
        }
    }

    /// <summary>
    /// A última mensagem aceita era nossa? Ver <see cref="Escrever"/>.
    /// </summary>
    static bool ultimaFoiNossa;

    /// <summary>Linhas comuns entre duas descargas ao disco.</summary>
    const int DescarregarACada = 512;

    /// <summary>
    /// Teto de tempo entre descargas.
    ///
    /// <para>O teto por linhas sozinho deixa um buraco: numa visita parada, ou
    /// numa que loga pouco, as últimas linhas podem ficar horas no buffer — e
    /// se o processo morre de repente, elas somem. O limite por tempo fecha
    /// isso sem voltar ao <c>AutoFlush</c>, que era o problema original.</para>
    /// </summary>
    static readonly TimeSpan DescarregarNoMaximoACada = TimeSpan.FromSeconds(2);

    static int desdeADescarga;
    static DateTime ultimaDescarga = DateTime.UtcNow;

    static void Escrever(string nivel, string texto)
    {
        if (texto == null) return;

        if (nivel == "msg")
        {
            // **Continuação conta como nossa.**
            //
            // O histórico de RNG sai uma linha por tick, cada uma numa chamada
            // de `Log.Message` separada e começando com espaços — não com
            // `[WithFriends]`. O filtro por prefixo guardava só o cabeçalho e
            // jogava fora justamente o corpo que se compara entre os dois
            // lados. Sobrou um diário com o índice e sem o livro.
            bool nossa = texto.StartsWith("[WithFriends");
            bool continuacao = ultimaFoiNossa && (texto.StartsWith(" ") || texto.StartsWith("\t"));

            if (!nossa && !continuacao) { ultimaFoiNossa = false; return; }
            ultimaFoiNossa = true;
        }

        string linha = $"{DateTime.Now:HH:mm:ss.fff} {nivel} {texto}";

        lock (Trava)
        {
            if (arquivo == null)
            {
                // Antes de abrir, guarda — mas com teto: se o diário nunca
                // abrir, isto não pode virar um vazamento silencioso.
                if (Antecipadas.Count < 2000) Antecipadas.Add(linha);
                return;
            }

            try
            {
                arquivo.WriteLine(linha);

                // Erro e aviso vão para o disco na hora: são exatamente o que
                // se procura quando o processo morre em seguida, e é justamente
                // aí que o que ficou no buffer se perde.
                //
                // O resto sai em lote. `DescarregarACada` é um meio-termo
                // medido: linha demais no buffer arrisca perder contexto num
                // crash, descarga demais era o problema original.
                var agora = DateTime.UtcNow;
                if (nivel != "msg"
                    || ++desdeADescarga >= DescarregarACada
                    || agora - ultimaDescarga >= DescarregarNoMaximoACada)
                {
                    arquivo.Flush();
                    desdeADescarga = 0;
                    ultimaDescarga = agora;
                }
            }
            catch { /* disco cheio, arquivo removido: o jogo não para por isso */ }
        }
    }

    // O `Log` do Verse é o funil de tudo: remendar a fonte cobre todos os
    // chamadores, inclusive os do próprio jogo (ADR 0015).
    //
    // Os tipos do parâmetro são obrigatórios: `Log.Message` tem duas
    // sobrecargas (`string` e `object`), e sem dizer qual o Harmony recusa a
    // classe inteira — as três gravações junto. A de `object` chama a de
    // `string`, então esta cobre as duas.
    [HarmonyPatch(typeof(Log), nameof(Log.Message), typeof(string)), HarmonyPostfix]
    public static void DepoisDaMensagem(string text) => Escrever("msg", text);

    [HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string)), HarmonyPostfix]
    public static void DepoisDoAviso(string text) => Escrever("AVISO", text);

    [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string)), HarmonyPostfix]
    public static void DepoisDoErro(string text) => Escrever("ERRO", text);
}
