using System;
using System.Collections.Generic;
using System.Linq;

namespace WithFriends.Client.Auditoria;

/// <summary>
/// Uma fonte local: algo que responde diferente em cada máquina.
/// </summary>
public sealed class FonteLocal
{
    /// <summary>Nome completo do tipo que expõe a fonte.</summary>
    public string Tipo { get; init; } = "";

    /// <summary>
    /// Membros vigiados. Vazio significa <b>qualquer</b> membro do tipo — usado
    /// onde o tipo inteiro é local (câmera, entrada, relógio).
    /// </summary>
    public string[] Membros { get; init; } = Array.Empty<string>();

    /// <summary>Por que responder isto dentro do tick separa as simulações.</summary>
    public string Motivo { get; init; } = "";

    /// <summary>
    /// Como esta fonte está tratada hoje.
    ///
    /// <para>A distinção importa e custou uma quase-mentira no relatório. Tratar
    /// a <b>fonte</b> cobre todo chamador, inclusive os que uma versão futura do
    /// jogo trouxer. Tratar <b>chamadores</b> cobre os que existiam no dia em
    /// que a lista foi feita — e se o relatório dissesse só "neutralizada", um
    /// chamador novo passaria despercebido com cara de resolvido.</para>
    /// </summary>
    public Tratamento Estado { get; init; } = Tratamento.Aberta;

    public override string ToString() =>
        Membros.Length == 0 ? Tipo : $"{Tipo}.{{{string.Join(",", Membros)}}}";
}

public enum Tratamento
{
    /// <summary>Ninguém mexeu.</summary>
    Aberta,

    /// <summary>A fonte responde valor neutro dentro do tick — cobre todo chamador.</summary>
    Fonte,

    /// <summary>
    /// A fonte é inalcançável (nativa) e os chamadores conhecidos foram
    /// tratados um a um. <b>A lista precisa ser revista a cada versão.</b>
    /// </summary>
    Chamadores,
}

/// <summary>
/// O catálogo de fontes locais — o coração da ADR 0015.
///
/// <para>Chamadores são milhares e mudam de nome a cada versão. Fontes são
/// poucas e quase não mudam. Remendar a fonte cobre todo chamador de uma vez,
/// inclusive os que ainda não existem.</para>
///
/// <para>Esta lista é o que o auditor procura no IL do jogo. Ela é a regra; o
/// relatório é o que a regra encontra hoje. Numa atualização do jogo, a regra
/// continua valendo e o relatório se refaz sozinho.</para>
/// </summary>
public static class FontesLocais
{
    public static readonly IReadOnlyList<FonteLocal> Todas = new List<FonteLocal>
    {
        new()
        {
            Tipo = "Verse.CameraDriver",
            Motivo = "o que ESTE jogador está olhando; dois jogadores nunca olham para o mesmo canto",
            Estado = Tratamento.Fonte,   // via GenTicks.GetCameraUpdateRate e ShouldSpawnMotesAt
        },
        new()
        {
            Tipo = "Verse.KeyBindingDef",
            Membros = new[] { "KeyDownEvent", "IsDownEvent", "JustPressed", "IsDown" },
            Motivo = "o teclado DESTE jogador; foi o que fez empilhar ordem virar substituir",
            Estado = Tratamento.Fonte,
        },
        new()
        {
            Tipo = "UnityEngine.Input",
            Motivo = "mouse e teclado DESTE jogador, por baixo do KeyBindingDef",
        },
        new()
        {
            Tipo = "UnityEngine.Random",
            Motivo = "gerador fora do Verse.Rand — não passa pelo RNG da sessão nem pela digital",
        },
        new()
        {
            Tipo = "System.DateTime",
            Membros = new[] { "get_Now", "get_UtcNow", "get_Today" },
            Motivo = "o relógio DESTA máquina",
        },
        new()
        {
            Tipo = "Verse.RealTime",
            Motivo = "cache do jogo para UnityEngine.Time — mesmo relógio e mesmo contador de quadros, um nome a menos",
            // Campos estáticos públicos: dá para escrever neles. Trocados em
            // volta do tick, como o estado do RNG. Ver TempoRealDoTick.
            Estado = Tratamento.Fonte,
        },
        new()
        {
            Tipo = "UnityEngine.Time",
            Motivo = "tempo real e taxa de quadros DESTA máquina, não o tick do jogo",
            // Fonte `extern`: o Harmony não a alcança. Os cinco chamadores de
            // simulação que a auditoria apontou foram tratados um a um, por
            // transpiler, em TempoDoTickNaSimulacao. Chamador novo numa versão
            // futura NÃO está coberto — por isso não é Tratamento.Fonte.
            Estado = Tratamento.Chamadores,
        },
        new()
        {
            Tipo = "Verse.Game",
            Membros = new[] { "get_CurrentMap" },
            Motivo = "a fonte de verdade do mapa atual; Find.CurrentMap só delega para cá",
            Estado = Tratamento.Fonte,
        },
        new()
        {
            Tipo = "Verse.Find",
            Membros = new[] { "get_CurrentMap", "get_Selector", "get_CameraDriver", "get_Targeter", "get_WindowStack", "get_ColonistBar", "get_MainTabsRoot", "get_UIRoot", "get_DesignatorManager" },
            Motivo = "o foco DESTE jogador: mapa aberto, seleção, janela, alvo",
        },
        new()
        {
            Tipo = "Verse.Prefs",
            Motivo = "as opções DESTE jogador",
        },
        new()
        {
            Tipo = "LudeonTK.DebugSettings",
            Motivo = "god mode e afins são de quem clicou; já viaja dentro do comando de designar",
        },
        new()
        {
            Tipo = "Verse.DebugViewSettings",
            Motivo = "sobreposições de debug DESTE jogador",
        },
        new()
        {
            Tipo = "UnityEngine.Screen",
            Motivo = "a resolução DESTA máquina",
        },
        new()
        {
            Tipo = "UnityEngine.Event",
            Membros = new[] { "get_current" },
            Motivo = "o evento de entrada em curso NESTA máquina",
        },
    };

    /// <summary>
    /// Pedaços de nome que sugerem código de interface.
    ///
    /// Só para ordenar o relatório: interface <b>pode</b> ler câmera e teclado,
    /// é o trabalho dela. O que não pode é simulação. Heurística por nome não
    /// decide nada — decide por onde começar a ler.
    /// </summary>
    static readonly string[] CheiroDeInterface =
    {
        "Window", "Dialog_", "ITab_", "MainTab", "Gizmo", "Widgets", "Listing_", "Alert",
        "DoWindowContents", "OnGUI", "DrawGUI", "GUIFor", "Tooltip", "FloatMenu",
        "Designator", "Command_", "Alert_", "Draw", "Render", "Mote", "Fleck", "Selector",
    };

    /// <summary>
    /// Pedaços de nome que sugerem <b>simulação</b> — o que tickia.
    ///
    /// <para>Filtro positivo, e ele rende muito mais que o negativo. "Não parece
    /// interface" é fraco: a primeira execução marcou 26 chamadores de
    /// <c>Input</c> como fora da interface, e todos eram encanamento de entrada
    /// — câmera, Steam Deck, correção de bug do GUI do Unity. Ruído.</para>
    ///
    /// <para>"Parece simulação" é forte: <c>WorkGiver_</c>, <c>JobGiver_</c>,
    /// <c>Building_</c> tocando teclado são achados de verdade, e foi assim que
    /// três deles apareceram de uma vez.</para>
    /// </summary>
    static readonly string[] CheiroDeSimulacao =
    {
        "JobGiver_", "WorkGiver_", "JobDriver_", "ThinkNode_", "Building_", "Verb_",
        "Hediff", "Pawn_", "Comp", "Need_", "Thought_", "IncidentWorker_",
        "LordJob_", "LordToil_", "RitualOutcome", "Recipe", "Ability", "Genepack",
        "Tick", "Pather", "Ingest", "Reachability", "Region", "Lister", "Spawner",
    };

    public static bool CheiraAInterface(string? tipo, string metodo)
    {
        string alvo = $"{tipo}.{metodo}";
        return CheiroDeInterface.Any(p => alvo.Contains(p, StringComparison.Ordinal));
    }

    public static bool CheiraASimulacao(string? tipo, string metodo)
    {
        string alvo = $"{tipo}.{metodo}";
        return CheiroDeSimulacao.Any(p => alvo.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Em que balde a leitura começa. Simulação ganha da interface: um
    /// <c>JobGiver_</c> dentro de uma janela ainda é simulação.
    /// </summary>
    public static string Balde(string? tipo, string metodo) =>
        CheiraASimulacao(tipo, metodo) ? "simulacao"
        : CheiraAInterface(tipo, metodo) ? "interface"
        : "indefinido";

    /// <summary>
    /// Este membro é uma fonte local?
    ///
    /// Puro de propósito: é a regra do auditor, e regra merece teste.
    /// </summary>
    public static FonteLocal? Casar(string? tipoDeclarante, string? membro)
    {
        if (string.IsNullOrEmpty(tipoDeclarante)) return null;

        string? simples = SemAcessador(membro);

        foreach (var fonte in Todas)
        {
            if (fonte.Tipo != tipoDeclarante) continue;
            if (fonte.Membros.Length == 0) return fonte;
            if (membro == null) continue;

            // Normaliza os DOIS lados: a regra pode ser escrita com ou sem
            // `get_`, e o IL sempre traz com.
            foreach (var vigiado in fonte.Membros)
                if (SemAcessador(vigiado) == simples) return fonte;
        }

        return null;
    }

    /// <summary>
    /// <c>get_IsDownEvent</c> → <c>IsDownEvent</c>.
    ///
    /// <para>No IL, ler uma propriedade é chamar <c>get_Nome</c>. Escrevendo a
    /// regra dá para esquecer disso — e o efeito é o pior possível: a fonte não
    /// casa com nada e o relatório diz <b>zero chamadores</b> para algo que tem
    /// dezenas. Silêncio parecendo boa notícia.</para>
    ///
    /// <para>Aconteceu com <c>KeyBindingDef</c>, na primeira execução, para uma
    /// fonte cujo chamador nós já tínhamos achado na mão.</para>
    /// </summary>
    static string? SemAcessador(string? membro)
    {
        if (membro == null) return null;
        if (membro.StartsWith("get_", StringComparison.Ordinal)) return membro.Substring(4);
        if (membro.StartsWith("set_", StringComparison.Ordinal)) return membro.Substring(4);
        return membro;
    }
}
