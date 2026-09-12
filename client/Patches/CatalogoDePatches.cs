using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Patches;

/// <summary>Um ponto de acoplamento com o interior do RimWorld.</summary>
public sealed class AlvoDePatch
{
    public Type Tipo { get; init; } = null!;
    public string Membro { get; init; } = "";
    /// <summary>Recurso que para de funcionar se o alvo sumir.</summary>
    public string Recurso { get; init; } = "";
    public string Motivo { get; init; } = "";

    public override string ToString() => $"{Tipo.Name}.{Membro}";
}

/// <summary>
/// Catálogo de tudo o que este mod remenda no jogo — anel 3 da
/// componentização (docs/SESSAO-COMPONENTES.md).
///
/// Existe por causa da §14.5: 90 mil métodos mudam a cada versão, e todo
/// patch de Harmony é acoplamento a um detalhe interno. O Multiplayer sustenta
/// 602 patches há anos, com esforço contínuo. A resposta aqui não é evitar
/// patches — é **saber exatamente quais são** e falhar de forma legível.
///
/// Na subida, cada alvo é verificado. Se algum sumiu numa atualização:
/// o recurso correspondente desliga, o log diz qual patch e qual método, e o
/// jogo continua abrindo. Jogar sozinho nunca é bloqueado (§11).
/// </summary>
public static class CatalogoDePatches
{
    static readonly List<AlvoDePatch> Alvos = new()
    {
        new AlvoDePatch
        {
            Tipo = typeof(GameDataSaveLoader),
            Membro = nameof(GameDataSaveLoader.SaveGame),
            Recurso = "checkpoint automático",
            Motivo = "ler o save que o jogo acabou de escrever, sem serializar de novo (ADR 0005)",
        },
        // Os dois caches que a interface envenena, e que só doem em combate.
        // Campos privados: se o jogo renomear qualquer um, o recurso desliga
        // com aviso legível em vez de silenciosamente deixar de proteger.
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.UniqueIDsManager),
            Membro = "GetNextID",
            Recurso = "ids da interface são locais",
            Motivo = "contador de ids é compartilhado e a interface o adianta de um lado só; " +
                     "thingIDNumber é semente de IsHashIntervalTick e da rotação sorteada",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Projectile),
            Membro = "UpdateRateTicks",
            Recurso = "ritmo de atualização fora da câmera",
            Motivo = "projétil sobrescreve o getter e decide por InViewOf: a bala andava " +
                     "de 1 em 1 tick para quem olhava e de 15 em 15 para o outro",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.StatWorker),
            Membro = "temporaryStatCache",
            Recurso = "caches de combate fora da interface",
            Motivo = "valor de stat calculado pela interface não pode ficar no cache da simulação",
        },
        new AlvoDePatch
        {
            Tipo = typeof(PawnCapacitiesHandler),
            Membro = "cachedCapacityLevels",
            Recurso = "caches de combate fora da interface",
            Motivo = "nível de capacidade calculado pela interface não pode ficar no cache da simulação",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Rand),
            Membro = "StateCompressed",
            Recurso = "detecção de desync em sessão",
            Motivo = "estado do RNG é a impressão digital da simulação (§14.3 decisão 1)",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.WealthWatcher),
            Membro = nameof(RimWorld.WealthWatcher.ForceRecount),
            Recurso = "cache não recalcula na interface",
            Motivo = "recontar riqueza varre o mapa e grava; feito por abrir uma aba, muda o estado de um lado só",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.DangerWatcher),
            Membro = "dangerRatingInt",
            Recurso = "cache não recalcula na interface",
            Motivo = "o nível de perigo é calculado e guardado; sem o campo, o prefixo devolveria o valor default",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_AbilityTracker),
            Membro = "allAbilitiesCached",
            Recurso = "cache não recalcula na interface",
            Motivo = "idem: o postfixo devolve esta lista quando o recálculo é cancelado",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.StoryWatcher_PopAdaptation),
            Membro = nameof(RimWorld.StoryWatcher_PopAdaptation.Notify_PawnEvent),
            Recurso = "cache não recalcula na interface",
            Motivo = "alimenta o estado do narrador; disparado ao desenhar, move o narrador de um lado só",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Planet.WorldObjectSelectionUtility),
            Membro = nameof(RimWorld.Planet.WorldObjectSelectionUtility.VisibleToCameraNow),
            Recurso = "cache não recalcula na interface",
            Motivo = "pergunta de câmera respondida dentro da simulação é a câmera decidindo o jogo",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.SituationalThoughtHandler),
            Membro = "cachedSocialThoughts",
            Recurso = "cache não recalcula na interface",
            Motivo = "recalcular pensamento social sorteia e carimba tick; abrir a aba social de um lado só diverge",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Zone),
            Membro = nameof(Zone.Cells),
            Recurso = "cache não recalcula na interface",
            Motivo = "o getter embaralha na primeira leitura e fica; se a interface ler antes do tick, a ordem muda para sempre",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_DraftController),
            Membro = nameof(RimWorld.Pawn_DraftController.Drafted),
            Recurso = "botão de alistar responde na hora",
            Motivo = "o gizmo lê Drafted; sem palpite na interface o jogador clica duas vezes",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_DraftController),
            Membro = "draftedInt",
            Recurso = "botão de alistar responde na hora",
            Motivo = "ler o valor real sem passar pelo palpite; sem ele o log mente sobre o estado",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.TimeControls),
            Membro = nameof(RimWorld.TimeControls.DoTimeControlsGUI),
            Recurso = "etiqueta do relógio da visita",
            Motivo = "o botão mostra a velocidade local; numa visita o tempo é compartilhado e precisa ser dito",
        },
        new AlvoDePatch
        {
            Tipo = typeof(TickManager),
            Membro = nameof(TickManager.DoSingleTick),
            Recurso = "barreira de tick em sessão",
            Motivo = "segurar a simulação no tick liberado, exato e não por frame (§2.3)",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Rand),
            Membro = nameof(Rand.PushState),
            Recurso = "RNG isolado de sessão",
            Motivo = "os dois lados partem do mesmo estado de RNG na visita (ADR 0009)",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Thing),
            Membro = nameof(Thing.DoTick),
            Recurso = "congelar mapas fora da sessão",
            Motivo = "durante a visita só os mapas da sessão simulam (ADR 0009)",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Map),
            Membro = nameof(Map.MapPreTick),
            Recurso = "congelar mapas fora da sessão",
            Motivo = "idem, no nível do mapa",
        },
        new AlvoDePatch
        {
            Tipo = typeof(CrossRefHandler),
            Membro = "loadedObjectDirectory",
            Recurso = "chegada de pawns visitantes",
            Motivo = "apresentar objetos vivos ao Scribe para as referências resolverem",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.WealthWatcher),
            Membro = nameof(RimWorld.WealthWatcher.ForceRecount),
            Recurso = "cache não recalcula na interface",
            Motivo = "recontar riqueza varre o mapa e grava; feito por abrir uma aba, muda o estado de um lado só",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.DangerWatcher),
            Membro = "dangerRatingInt",
            Recurso = "cache não recalcula na interface",
            Motivo = "o nível de perigo é calculado e guardado; sem o campo, o prefixo devolveria o valor default",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_AbilityTracker),
            Membro = "allAbilitiesCached",
            Recurso = "cache não recalcula na interface",
            Motivo = "idem: o postfixo devolve esta lista quando o recálculo é cancelado",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.StoryWatcher_PopAdaptation),
            Membro = nameof(RimWorld.StoryWatcher_PopAdaptation.Notify_PawnEvent),
            Recurso = "cache não recalcula na interface",
            Motivo = "alimenta o estado do narrador; disparado ao desenhar, move o narrador de um lado só",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Planet.WorldObjectSelectionUtility),
            Membro = nameof(RimWorld.Planet.WorldObjectSelectionUtility.VisibleToCameraNow),
            Recurso = "cache não recalcula na interface",
            Motivo = "pergunta de câmera respondida dentro da simulação é a câmera decidindo o jogo",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.SituationalThoughtHandler),
            Membro = "cachedSocialThoughts",
            Recurso = "cache não recalcula na interface",
            Motivo = "recalcular pensamento social sorteia e carimba tick; abrir a aba social de um lado só diverge",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Zone),
            Membro = nameof(Zone.Cells),
            Recurso = "cache não recalcula na interface",
            Motivo = "o getter embaralha na primeira leitura e fica; se a interface ler antes do tick, a ordem muda para sempre",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_DraftController),
            Membro = nameof(RimWorld.Pawn_DraftController.Drafted),
            Recurso = "alistar dentro de sessão",
            Motivo = "ordem do jogador vira comando, aplicado no mesmo tick nos dois lados (§2.3)",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Verse.AI.Pawn_JobTracker),
            Membro = nameof(Verse.AI.Pawn_JobTracker.TryTakeOrderedJob),
            Recurso = "ordens do jogador dentro de sessão",
            Motivo = "todo clique-direito passa aqui; uma intercepção cobre quase tudo (§2.3)",
        },
        new AlvoDePatch
        {
            Tipo = typeof(GenView),
            Membro = nameof(GenView.ShouldSpawnMotesAt),
            Recurso = "determinismo de efeitos visuais em sessão",
            Motivo = "criar mote sorteia números, e a decisão dependia da câmera do jogador",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_MeleeVerbs),
            Membro = nameof(RimWorld.Pawn_MeleeVerbs.TryGetMeleeVerb),
            Recurso = "interface não muda a simulação",
            Motivo = "passar o mouse num inimigo sorteava e cacheava o verbo de ataque do pawn",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.WealthWatcher),
            Membro = nameof(RimWorld.WealthWatcher.ForceRecount),
            Recurso = "cache não recalcula na interface",
            Motivo = "recontar riqueza varre o mapa e grava; feito por abrir uma aba, muda o estado de um lado só",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.DangerWatcher),
            Membro = "dangerRatingInt",
            Recurso = "cache não recalcula na interface",
            Motivo = "o nível de perigo é calculado e guardado; sem o campo, o prefixo devolveria o valor default",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_AbilityTracker),
            Membro = "allAbilitiesCached",
            Recurso = "cache não recalcula na interface",
            Motivo = "idem: o postfixo devolve esta lista quando o recálculo é cancelado",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.StoryWatcher_PopAdaptation),
            Membro = nameof(RimWorld.StoryWatcher_PopAdaptation.Notify_PawnEvent),
            Recurso = "cache não recalcula na interface",
            Motivo = "alimenta o estado do narrador; disparado ao desenhar, move o narrador de um lado só",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Planet.WorldObjectSelectionUtility),
            Membro = nameof(RimWorld.Planet.WorldObjectSelectionUtility.VisibleToCameraNow),
            Recurso = "cache não recalcula na interface",
            Motivo = "pergunta de câmera respondida dentro da simulação é a câmera decidindo o jogo",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.SituationalThoughtHandler),
            Membro = "cachedSocialThoughts",
            Recurso = "cache não recalcula na interface",
            Motivo = "recalcular pensamento social sorteia e carimba tick; abrir a aba social de um lado só diverge",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Zone),
            Membro = nameof(Zone.Cells),
            Recurso = "cache não recalcula na interface",
            Motivo = "o getter embaralha na primeira leitura e fica; se a interface ler antes do tick, a ordem muda para sempre",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_DraftController),
            Membro = nameof(RimWorld.Pawn_DraftController.Drafted),
            Recurso = "botão de alistar responde na hora",
            Motivo = "o gizmo lê Drafted; sem palpite na interface o jogador clica duas vezes",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.Pawn_DraftController),
            Membro = "draftedInt",
            Recurso = "botão de alistar responde na hora",
            Motivo = "ler o valor real sem passar pelo palpite; sem ele o log mente sobre o estado",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.TimeControls),
            Membro = nameof(RimWorld.TimeControls.DoTimeControlsGUI),
            Recurso = "etiqueta do relógio da visita",
            Motivo = "o botão mostra a velocidade local; numa visita o tempo é compartilhado e precisa ser dito",
        },
        new AlvoDePatch
        {
            Tipo = typeof(TickManager),
            Membro = nameof(TickManager.TogglePaused),
            Recurso = "intenção de pausa do jogador",
            Motivo = "espaço e botão de pausa escrevem o campo direto, sem passar pela propriedade",
        },
        new AlvoDePatch
        {
            Tipo = typeof(TickManager),
            Membro = nameof(TickManager.CurTimeSpeed),
            Recurso = "intenção de velocidade do jogador",
            Motivo = "é o funil de botão, teclas 1/2/3 e espaço; sem ele só se vê mudança de estado, não intenção",
        },
        new AlvoDePatch
        {
            Tipo = typeof(TickManager),
            Membro = nameof(TickManager.TickManagerUpdate),
            Recurso = "tick guiado pela barreira",
            Motivo = "o ritmo do tick passa a vir do coordenador; sem isto cada lado anda no próprio relógio e eles derivam",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RealTime),
            Membro = "frameCount",
            Recurso = "tempo real determinístico em sessão",
            Motivo = "cache de UnityEngine.Time lido por 57 métodos, incluindo Pawn_JobTracker.StartJob",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Game),
            Membro = nameof(Game.CurrentMap),
            Recurso = "mapa da sessão na simulação",
            Motivo = "39 métodos de simulação decidem pelo mapa que ESTE jogador abriu, incluindo um JobGiver",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.CompSkyfallerRandomizeDirection),
            Membro = "CompTick",
            Recurso = "tempo de tick na simulação",
            Motivo = "acumula a posição do skyfaller por Time.deltaTime, que é a taxa de quadros da máquina",
        },
        new AlvoDePatch
        {
            Tipo = typeof(RimWorld.CompAbilityEffect_Chunkskip),
            Membro = "FindClosestChunks",
            Recurso = "tempo de tick na simulação",
            Motivo = "cache com chave no número do quadro, que não é o mesmo em duas máquinas",
        },
        new AlvoDePatch
        {
            Tipo = typeof(KeyBindingDef),
            Membro = "IsDownEvent",
            Recurso = "teclado fora da simulação em sessão",
            Motivo = "TryTakeOrderedJob lê o Shift na hora de aplicar; empilhar ou substituir dependia de quem segurava a tecla",
        },
        new AlvoDePatch
        {
            Tipo = typeof(PathFinder),
            Membro = "ForceCompleteScheduledJobs",
            Recurso = "pathfinding determinístico em sessão",
            Motivo = "no 1.6 a busca de caminho roda em threads e atravessa o tick, lendo posição de pawn que a thread principal escreve",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Designator),
            Membro = nameof(Designator.DesignateMultiCell),
            Recurso = "arrasto de designador em um comando só",
            Motivo = "sem isto cada célula do arrasto vira um comando, e pausado vira um passo de tick por célula",
        },
        new AlvoDePatch
        {
            Tipo = typeof(Designator),
            Membro = nameof(Designator.DesignateSingleCell),
            Recurso = "construir/minerar/demolir dentro de sessão",
            Motivo = "designar acontecia na hora, só do lado de quem clicou",
        },
        new AlvoDePatch
        {
            Tipo = typeof(LudeonTK.DebugActionNode),
            Membro = nameof(LudeonTK.DebugActionNode.Enter),
            Recurso = "bloqueio de ferramentas de dev em sessão",
            Motivo = "ação de debug roda fora do fluxo de tick, só de um lado, e separa as simulações",
        },
        new AlvoDePatch
        {
            Tipo = typeof(GenTicks),
            Membro = nameof(GenTicks.GetCameraUpdateRate),
            Recurso = "simulação independente da câmera em sessão",
            Motivo = "no 1.6 o ritmo de tick de cada coisa vem da câmera; dois jogadores olham para lugares diferentes",
        },
        new AlvoDePatch
        {
            Tipo = typeof(LongEventHandler),
            Membro = "currentEvent",
            Recurso = "interface não muda a simulação",
            Motivo = "distinguir carregamento de interface; sem ele o guarda vale, só perde essa exceção",
        },
    };

    /// <summary>Recursos desligados porque o alvo não existe nesta versão.</summary>
    public static IReadOnlyCollection<string> RecursosDesligados { get; private set; } = Array.Empty<string>();

    public static IReadOnlyCollection<AlvoDePatch> Todos => Alvos;

    /// <summary>
    /// Confere todos os alvos e devolve os que faltam. Chamar **antes** de
    /// <c>PatchAll</c>: é melhor descobrir aqui, com mensagem clara, do que
    /// numa exceção no meio do carregamento.
    /// </summary>
    public static IReadOnlyList<AlvoDePatch> Verificar()
    {
        var faltando = Alvos.Where(alvo => !Existe(alvo)).ToList();

        if (faltando.Count == 0)
        {
            Log.Message($"[WithFriends] {Alvos.Count} ponto(s) de acoplamento verificado(s) — todos presentes.");
        }
        else
        {
            RecursosDesligados = faltando.Select(a => a.Recurso).Distinct().ToArray();

            foreach (var alvo in faltando)
                Log.Warning(
                    $"[WithFriends] alvo ausente nesta versão do RimWorld: {alvo}\n" +
                    $"  recurso desligado: {alvo.Recurso}\n" +
                    $"  para que servia: {alvo.Motivo}\n" +
                    "  O resto do mod continua funcionando; jogar sozinho não é afetado.");
        }

        return faltando;
    }

    static bool Existe(AlvoDePatch alvo)
    {
        try
        {
            // Por nome, não por assinatura: `AccessTools.Method` estoura em
            // sobrecarga (AmbiguousMatchException), e o catch devolvia "alvo
            // ausente" para métodos que existem. Foi o que aconteceu com
            // GenView.ShouldSpawnMotesAt, que tem duas sobrecargas — o log
            // dizia que o determinismo de motes estava desligado, e não estava.
            return AccessTools.GetDeclaredMethods(alvo.Tipo).Any(m => m.Name == alvo.Membro)
                || AccessTools.Property(alvo.Tipo, alvo.Membro) != null
                || AccessTools.Field(alvo.Tipo, alvo.Membro) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Resumo para debug action e para o relatório de versão.</summary>
    public static string Relatorio()
    {
        var linhas = Alvos.Select(alvo =>
            $"  {alvo,-44} {(Existe(alvo) ? "ok" : "AUSENTE")}  ({alvo.Recurso})");
        return $"[WithFriends] acoplamentos com o jogo ({Alvos.Count}):\n" + string.Join("\n", linhas);
    }
}
