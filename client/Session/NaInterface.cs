using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Estamos rodando **interface**, não simulação?
///
/// Conceito reusado do Multiplayer (MIT, Zetrith), `Multiplayer.InInterface`.
/// É definido por exclusão: se não é tick, não é comando e não é carregamento,
/// então é a UI — mouse, câmera, tooltips, menus.
///
/// <para><b>Por que isso importa.</b> Código de interface no RimWorld não é só
/// leitura: ele **pergunta à simulação** e a simulação **responde
/// sorteando e guardando**. O caso clássico:</para>
///
/// <code>
/// Pawn_MeleeVerbs.TryGetMeleeVerb(target)   // escolhe um verbo ao acaso
///                                           // e o CACHEIA em curMeleeVerb
/// </code>
///
/// <para>Passar o mouse sobre um inimigo faz o menu flutuante perguntar "que
/// ataque este pawn usaria?", e a resposta muda o estado do pawn. Um jogador
/// passa o mouse, o outro não — e o próximo ataque corpo a corpo é diferente
/// nos dois lados.</para>
///
/// <para>Medido aqui: ~110 sorteios de diferença, o mesmo evento acontecendo
/// três ticks depois de um lado. "Falhou ao mover a câmera" era isto.</para>
/// </summary>
public static class NaInterface
{
    /// <summary>Estamos dentro de um tick de jogo agora?</summary>
    public static bool Tickando { get; internal set; }

    // `LongEventHandler.currentEvent` é privado: o Multiplayer lê direto porque
    // compila contra um assembly publicizado. Aqui vai por reflexão, uma vez.
    static readonly FieldInfo CampoEventoLongo =
        AccessTools.Field(typeof(LongEventHandler), "currentEvent");

    // Se o campo sumir numa atualização do jogo, o guarda continua valendo — só
    // perde a exceção do carregamento. O catálogo de patches avisa no boot.
    public static bool CampoDeEventoExiste => CampoEventoLongo != null;

    /// <summary>Há um evento longo em curso (carregamento, geração, troca de partida)?</summary>
    public static bool CarregandoAlgo =>
        CampoEventoLongo != null && CampoEventoLongo.GetValue(null) != null;

    public static bool Agora =>
        RngDeSessao.Ativo
        && !Tickando
        && !ComandoDeSessao.Aplicando
        && Current.ProgramState == ProgramState.Playing
        && !CarregandoAlgo;
}
