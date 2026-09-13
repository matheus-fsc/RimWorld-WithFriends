using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A decisão de largar sangue, passo a passo, dos dois lados.
///
/// <para><b>Por que um rastreio só para isto.</b> Seis divergências seguidas
/// caíram no mesmo <c>DropBloodFilth</c>, e os instrumentos gerais já
/// eliminaram tudo o que sabiam medir: taxa de sangramento bit a bit, corpo,
/// postura, hediffs, ritmo, delta, ordem de tick e posição no gerador — todos
/// idênticos no tick anterior. E mesmo assim um lado larga sangue e o outro
/// não.</para>
///
/// <para>Quando o geral já disse tudo que sabe, o que falta é o específico. Este
/// rastreio registra o <b>caminho inteiro</b> de uma única decisão:</para>
///
/// <code>
/// Rand.Chance(taxa * corpo * fator * delta)   →  passou ou não?
///   FilthMaker.TryMakeFilth(célula, def)      →  engrossou, nasceu, ou recusou?
/// </code>
///
/// <para>Só essas duas respostas separam as três explicações que sobraram: o
/// sorteio saiu diferente; o sorteio saiu igual e a célula estava diferente; ou
/// o sorteio saiu igual, a célula igual, e algo dentro do <c>TryMakeFilth</c>
/// decidiu diferente.</para>
///
/// <para><b>Custo.</b> Uma linha por pawn sangrando por tick, e só enquanto há
/// alguém sangrando. Numa emulação de dois minutos são algumas centenas — barato
/// perto de uma hora de jogo a dois, que era o preço da rodada anterior.</para>
///
/// <para><b>O que ele achou.</b> A terceira explicação, e pela negativa: no
/// tick 218 os dois lados registraram a <b>mesma</b> linha — <c>Micky em
/// 46,154, espessura 5, engrossa False</c> — e só um deles sorteou dentro de
/// <c>Filth.SpawnSetup</c>. Sorteio igual, célula igual, decisão diferente.
/// <c>espessura 5, engrossa False</c> é exatamente a condição que manda o
/// <c>TryMakeFilth</c> caminhar os vizinhos, e a ordem dos vizinhos vinha de
/// uma lista estática embaralhada no lugar, que carregava a história inteira do
/// processo. Ver a ADR 0020 e <see cref="EstaticosDaSessao"/>.</para>
///
/// <para>Ligado por <c>-rastrearsangue</c>: é instrumento de caça, não guarda.
/// Fica porque a família é rica em divergência e o instrumento já provou que
/// responde — mas desligado, e sem nada a dizer enquanto ninguém sangra.</para>
/// </summary>
public static class RastreioDeSangue
{
    public static readonly bool Ligado =
        GenCommandLine.CommandLineArgPassed("rastrearsangue");

    /// <summary>O pawn cuja decisão está sendo acompanhada agora, e onde.</summary>
    static Pawn? emCurso;
    static IntVec3 celula;

    public static void Comecou(Pawn pawn)
    {
        emCurso = pawn;
        celula = pawn.PositionHeld;

        var mapa = pawn.MapHeld;
        var def = pawn.RaceProps?.BloodDef;

        // O que já existe na célula: é isto que decide entre engrossar e nascer,
        // e é a metade da história que o rastreio geral não via.
        string existente = "-";
        if (mapa != null && def != null && celula.InBounds(mapa))
        {
            var achado = celula.GetThingList(mapa).FirstOrDefault(t => t.def == def) as Filth;
            existente = achado == null
                ? "nenhum"
                : $"espessura {achado.thickness}, engrossa {achado.CanBeThickened}";
        }

        Log.Message(
            $"[WithFriends/sangue] tick {Colony.SincronizacaoComponent.Atual?.Sessao.TickDeSessao ?? -1} " +
            $"#{pawn.thingIDNumber} {pawn.LabelShort} em {celula.x},{celula.z} " +
            $"def {def?.defName ?? "-"} — na célula: {existente}");
    }

    public static void Terminou(bool deu)
    {
        if (emCurso == null) return;

        Log.Message($"[WithFriends/sangue]   TryMakeFilth → {(deu ? "sim" : "não")}");
        emCurso = null;
    }
}

/// <summary>
/// Envolve <c>DropBloodFilth</c> para registrar o estado da célula antes e o
/// resultado depois. Prefixo e posfixo puros — não mudam nada.
/// </summary>
[HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.DropBloodFilth))]
public static class SangueRastreado
{
    static readonly System.Reflection.FieldInfo? CampoDoPawn =
        AccessTools.Field(typeof(Pawn_HealthTracker), "pawn");

    [HarmonyPrefix]
    public static void Antes(Pawn_HealthTracker __instance)
    {
        if (!RastreioDeSangue.Ligado) return;
        if (CampoDoPawn?.GetValue(__instance) is Pawn pawn) RastreioDeSangue.Comecou(pawn);
    }

    [HarmonyPostfix]
    public static void Depois()
    {
        if (!RastreioDeSangue.Ligado) return;
        RastreioDeSangue.Terminou(true);
    }
}
