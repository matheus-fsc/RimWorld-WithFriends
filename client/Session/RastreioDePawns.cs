using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
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

        // **Projéteis também.**
        //
        // Uma divergência em combate apareceu como "o tiro acertou no tick 2306
        // de um lado e não do outro", e o rastreio de pawn não via nada: bala
        // não é pawn. O rastreio de RNG só mostrava a consequência — o sorteio
        // do dano e do sangue —, nunca o que fez o impacto cair noutro tick.
        //
        // São poucos por tick, e só enquanto há combate: o custo aparece
        // exatamente quando o diagnóstico é preciso.
        foreach (var projetil in mapa.listerThings.ThingsInGroup(ThingRequestGroup.Projectile)
                     .OfType<Projectile>()
                     .OrderBy(p => p.thingIDNumber))
            linhas.Add(Linha(projetil));

        porTick[tickDeSessao] = linhas;
        ordem.Enqueue(tickDeSessao);
        while (ordem.Count > AmostrasGuardadas) porTick.Remove(ordem.Dequeue());
    }

    static readonly System.Reflection.FieldInfo? DeltaDoTick =
        HarmonyLib.AccessTools.Field(typeof(Thing), "tickDelta");

    /// <summary>
    /// Quantos ticks se acumularam desde a última vez que esta coisa foi
    /// tickada.
    ///
    /// <para>É o <c>delta</c> que chega em <c>TickInterval(delta)</c>, e ele
    /// multiplica probabilidades: <c>Rand.Chance(taxa * delta)</c>. Dois lados
    /// com o mesmo estado e o mesmo sorteio ainda decidem diferente se o delta
    /// diferir.</para>
    ///
    /// <para>Não é o mesmo que <c>UpdateRateTicks</c>, que já está na linha:
    /// aquele é o ritmo <b>pretendido</b>, este é o que de fato se acumulou.
    /// Foram necessários os dois porque o ritmo batia nos dois lados e a
    /// decisão, mesmo assim, não.</para>
    /// </summary>
    static int Delta(Thing coisa)
    {
        try { return DeltaDoTick?.GetValue(coisa) is int d ? d : -1; }
        catch (Exception) { return -1; }
    }

    static readonly System.Reflection.FieldInfo? TicksAteImpacto =
        HarmonyLib.AccessTools.Field(typeof(Projectile), "ticksToImpact");

    static readonly System.Reflection.FieldInfo? Origem =
        HarmonyLib.AccessTools.Field(typeof(Projectile), "origin");

    /// <summary>
    /// A linha de um projétil: onde está, quanto falta para impactar, e em quem.
    ///
    /// <para><c>ticksToImpact</c> é o campo que decide o tick do impacto, e é
    /// ele que precisa bater entre os dois lados. Se ele diverge, o dano cai em
    /// ticks diferentes e todo o resto desanda atrás.</para>
    /// </summary>
    static string Linha(Projectile projetil)
    {
        var alvo = projetil.usedTarget;
        object? restante = null;
        try { restante = TicksAteImpacto?.GetValue(projetil); } catch (Exception) { }

        Vector3 origem = default;
        try { if (Origem?.GetValue(projetil) is Vector3 v) origem = v; } catch (Exception) { }

        return
            $"    ={projetil.thingIDNumber,-7} {projetil.def?.defName ?? "-",-14} " +
            $"pos {projetil.Position.x,3},{projetil.Position.z,3}  " +
            $"exato {projetil.ExactPosition.x,8:F3},{projetil.ExactPosition.z,8:F3}  " +
            $"impacto em {restante ?? "-",-5} " +
            $"origem {origem.x,7:F2},{origem.z,7:F2}  " +
            $"alvo {(alvo.IsValid ? $"{alvo.Cell.x},{alvo.Cell.z}" : "-"),-9} " +
            $"lancador {projetil.Launcher?.thingIDNumber.ToString() ?? "-"}";
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
            $"draft {(pawn.drafter?.Drafted == true ? 1 : 0)}  " +
            // Sangramento e ritmo de atualização: os dois entram na decisão de
            // largar sangue, que já é a segunda divergência que cai aqui.
            //
            //   if (Rand.Chance(bleedRate * bodySize * fator * delta))
            //       DropBloodFilth();
            //
            // O sorteio acontece dos dois lados; o que muda o resultado é o
            // LIMIAR. Sem estes números no rastreio, "um largou sangue e o outro
            // não" não distingue taxa diferente de delta diferente — e são
            // causas em lugares opostos.
            $"sangue {pawn.health?.hediffSet?.BleedRateTotal ?? 0f,7:F4} " +
            $"hediffs {pawn.health?.hediffSet?.hediffs?.Count ?? 0,3}  " +
            $"ritmo {pawn.UpdateRateTicks,3} " +
            $"delta {Delta(pawn),3} " +
            $"postura {(int)RimWorld.PawnUtility.GetPosture(pawn)} " +
            $"corpo {pawn.BodySize,5:F2}";
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
