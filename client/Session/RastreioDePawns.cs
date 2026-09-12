using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Estado dos pawns tick a tick — o que o rastreio de RNG não alcança.
///
/// <para><b>O limite do outro instrumento.</b> <see cref="RastreioDeRng"/> só vê
/// quem consome sorteio. Mas o avanço de um pawn pela célula não sorteia nada:</para>
///
/// <code>
/// nextCellCostLeft -= CostToPayThisTick();
/// if (nextCellCostLeft &lt;= 0) TryEnterNextPathCell();
/// </code>
///
/// <para>É subtração de float. Quando os dois lados divergem aqui, o rastreio de
/// RNG só mostra a <b>consequência</b> — <c>Notify_EnteredNewCell</c> gastando 3
/// sorteios num tick de um lado e no seguinte do outro — e nunca o campo que
/// saiu de sincronia.</para>
///
/// <para>Este rastreio grava o estado que importa para movimento e trabalho, nos
/// mesmos ticks amostrados pela digital. No aborto, os dois lados despejam o
/// mesmo bloco: a primeira linha diferente diz <b>qual pawn</b> e <b>qual
/// campo</b>.</para>
/// </summary>
public static class RastreioDePawns
{
    /// <summary>
    /// Ticks guardados. Agora **todo** tick, não um a cada oito.
    ///
    /// <para>Com amostra a cada 8 ticks o instrumento dizia "divergiu entre 4049
    /// e 4056" — sete ticks de incerteza, tempo suficiente para a causa e o
    /// efeito caberem dentro da mesma amostra. E, cruzando com o rastreio de
    /// RNG, era impossível saber se no tick da divergência os dois lados tinham
    /// sorteado a mesma quantidade.</para>
    ///
    /// <para>Essa pergunta é o divisor de águas: <b>mesma contagem de sorteios e
    /// resultado diferente</b> significa entrada diferente (ordem de coleção,
    /// conjunto de candidatos) e não caminho de código diferente. São causas
    /// distintas e remédios distintos.</para>
    /// </summary>
    const int AmostrasGuardadas = 400;

    static readonly Queue<long> ordem = new();
    static readonly Dictionary<long, List<string>> porTick = new();

    public static bool Ligado { get; set; } = true;

    public static void Limpar()
    {
        ordem.Clear();
        porTick.Clear();
    }

    public static void Amostrar(long tickDeSessao, int mapaId)
    {
        if (!Ligado || mapaId < 0) return;

        var mapa = Find.Maps?.FirstOrDefault(m => m.uniqueID == mapaId);
        if (mapa == null) return;

        var linhas = new List<string>();

        // Ordem por id: a ordem das listas do jogo não é contrato.
        //
        // Só quem está no mapa: pawns guardados em contêiner aparecem em
        // -1000,-1000 com tudo zerado, enchem o despejo e nunca divergem.
        foreach (var pawn in mapa.mapPawns.AllPawns.Where(p => p.Spawned).OrderBy(p => p.thingIDNumber))
            linhas.Add(Linha(pawn));

        porTick[tickDeSessao] = linhas;
        ordem.Enqueue(tickDeSessao);
        while (ordem.Count > AmostrasGuardadas) porTick.Remove(ordem.Dequeue());
    }

    static string Linha(Pawn pawn)
    {
        var pather = pawn.pather;
        var job = pawn.CurJob;

        return
            $"    #{pawn.thingIDNumber,-7} {pawn.LabelShort,-14} " +
            $"pos {pawn.Position.x,3},{pawn.Position.z,3}  " +
            $"mov {(pather?.Moving == true ? 1 : 0)} " +
            $"custo {pather?.nextCellCostLeft ?? 0f,8:F3}/{pather?.nextCellCostTotal ?? 0f,8:F3}  " +
            $"dest {(pather?.Destination.IsValid == true ? $"{pather.Destination.Cell.x},{pather.Destination.Cell.z}" : "-"),-9} " +
            $"job {job?.def?.defName ?? "-",-22} " +
            $"fila {pawn.jobs?.jobQueue?.Count ?? 0}  " +
            $"draft {(pawn.drafter?.Drafted == true ? 1 : 0)}";
    }

    public static void Despejar()
    {
        if (porTick.Count == 0)
        {
            Log.Message("[WithFriends] rastreio de pawns vazio");
            return;
        }

        Log.Message(
            "[WithFriends] rastreio de estado dos pawns — mesmos ticks da digital\n" +
            "  (compare com o do outro jogador; a primeira linha diferente diz qual pawn e qual campo)");

        foreach (var tick in ordem.OrderBy(t => t))
        {
            var texto = new StringBuilder();
            texto.AppendLine($"  tick {tick}");
            foreach (var linha in porTick[tick]) texto.AppendLine(linha);
            Log.Message(texto.ToString());
        }
    }
}
