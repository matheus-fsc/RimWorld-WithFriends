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

        // **A amostra é leitura da simulação, não da interface.**
        //
        // `Amostrar` roda depois do tick, e ali `NaInterface.Agora` já é
        // verdadeiro. Isso importa porque há guardas que **mentem de
        // propósito** para a interface — `AlistamentoOtimista` faz
        // `drafter.Drafted` responder o valor pedido, não o real, para o botão
        // não parecer travado.
        //
        // Sem esta declaração o rastreio lia a mentira. Custou caro uma vez: o
        // comparador apontou `draft 1` contra `draft 0` em três colonos e
        // rotulou "ANTES do sorteio divergir — é a causa". Eram exatamente os
        // três pawns com palpite pendente na máquina do anfitrião, e o estado
        // de verdade era igual dos dois lados. Instrumento que mede a si mesmo
        // não mede nada.
        bool eraInterface = NaInterface.Tickando;
        NaInterface.Tickando = true;
        try { AmostrarDeVerdade(tickDeSessao, mapa); }
        finally { NaInterface.Tickando = eraInterface; }
    }

    static void AmostrarDeVerdade(long tickDeSessao, Map mapa)
    {
        var linhas = new List<string> { LinhaDoMapa(mapa) };

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

    /// <summary>
    /// O <c>delta</c> com que <c>HealthTickInterval</c> foi chamado pela última
    /// vez, por pawn.
    ///
    /// <para><b>A última variável da fórmula.</b> Seis divergências seguidas
    /// caíram no mesmo <c>DropBloodFilth</c>:</para>
    ///
    /// <code>
    /// float num = BleedRateTotal * BodySize * (deitado ? 0.0004f : 0.004f);
    /// if (Rand.Chance(num * delta)) DropBloodFilth();
    /// </code>
    ///
    /// <para>Taxa, corpo, postura, hediffs, ritmo e fase batem — a taxa agora em
    /// precisão total, bit a bit. A posição no gerador bate no tick anterior. Só
    /// o <c>delta</c> nunca foi medido: o campo <c>tickDelta</c> que a linha já
    /// mostra é lido <b>depois</b> do tick, quando já foi zerado, então mostra a
    /// fase do ciclo e não o valor que multiplicou a probabilidade.</para>
    ///
    /// <para>Aqui ele é capturado no instante da chamada. Se bater nos dois
    /// lados, a fórmula inteira está eliminada e a causa está fora dela — na
    /// ordem em que os pawns são tickados, que muda qual deles consome qual
    /// sorteio.</para>
    /// </summary>
    /// <summary>
    /// Em que <b>posição</b> cada pawn foi tickado neste tick.
    ///
    /// <para><b>A última hipótese de pé.</b> Seis divergências no mesmo
    /// <c>DropBloodFilth</c>, e a fórmula inteira já foi eliminada: taxa de
    /// sangramento bit a bit, corpo, postura, hediffs, ritmo, fase do ciclo e
    /// agora o <c>delta</c> — todos idênticos nos dois lados, com a mesma
    /// posição no gerador no tick anterior.</para>
    ///
    /// <para>Entradas iguais e sorteio no mesmo lugar só podem dar resultados
    /// diferentes se o sorteio <b>não for o mesmo</b>. E isso acontece se a
    /// <b>ordem</b> em que os pawns são tickados mudar: o pawn X consome o
    /// enésimo número de um lado e o enésimo-primeiro do outro. O total de
    /// sorteios do tick continua batendo — e batia — enquanto o resultado de um
    /// deles vira.</para>
    ///
    /// <para>A ordem vem de <c>TickList</c>, que é ordem de registro, que é
    /// ordem de spawn. Depois de um carregamento é a ordem do save; durante o
    /// jogo, a ordem em que as coisas nasceram.</para>
    /// </summary>
    static readonly Dictionary<int, int> posicaoNoTick = new();

    static int proximaPosicao;

    /// <summary>Chamado no começo de cada passo, antes de tickar.</summary>
    public static void ComecarTick()
    {
        if (!Ligado) return;
        posicaoNoTick.Clear();
        proximaPosicao = 0;
    }

    public static void AnotarOrdem(Pawn pawn)
    {
        if (!Ligado) return;
        if (!posicaoNoTick.ContainsKey(pawn.thingIDNumber))
            posicaoNoTick[pawn.thingIDNumber] = proximaPosicao++;
    }

    static int Ordem(Pawn pawn) =>
        posicaoNoTick.TryGetValue(pawn.thingIDNumber, out var i) ? i : -1;

    static readonly Dictionary<int, int> deltaDaSaude = new();

    public static void AnotarDeltaDaSaude(Pawn pawn, int delta) =>
        deltaDaSaude[pawn.thingIDNumber] = delta;

    static int DeltaDaSaude(Pawn pawn) =>
        deltaDaSaude.TryGetValue(pawn.thingIDNumber, out var d) ? d : -1;

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

    /// <summary>
    /// O estado global do mapa que entra no custo de andar de **todo mundo**.
    ///
    /// <para><b>Por que esta linha existe.</b> Uma divergência apareceu como
    /// dezenas de invasores com o custo de movimento diferente na terceira
    /// casa decimal, todos no mesmo tick:</para>
    ///
    /// <code>
    /// A: Huber  custo 21.050/21.080      B: Huber  custo 21.051/21.082
    /// A: Legend custo 15.129/15.393      B: Legend custo 15.130/15.394
    /// </code>
    ///
    /// <para>Diferença pequena, relativa, e igual para todos — assinatura de um
    /// <b>multiplicador global</b>, não de decisão. E há um só no caminho:</para>
    ///
    /// <code>
    /// // Pawn.TicksPerMove, para todo pawn sem teto sobre a cabeça
    /// num3 /= map.weatherManager.CurMoveSpeedMultiplier;
    ///
    /// CurMoveSpeedMultiplier => Lerp(lastWeather.…, curWeather.…, TransitionLerpFactor);
    /// TransitionLerpFactor   => curWeatherAge / 4000f;   // int, ++ a cada tick
    /// </code>
    ///
    /// <para>Um tick de diferença em <c>curWeatherAge</c> move o multiplicador
    /// em 1/4000 — a ordem exata do que foi medido. E nada disso sorteia, então
    /// a digital (que é estado do RNG) só percebe quando vaza para o
    /// movimento: nesta corrida, 9.700 passos depois.</para>
    ///
    /// <para>O multiplicador vai em precisão total pelo mesmo motivo da taxa de
    /// sangramento: quatro casas diziam "idêntico" enquanto a decisão saía
    /// diferente.</para>
    /// </summary>
    static string LinhaDoMapa(Map mapa)
    {
        var clima = mapa.weatherManager;

        return
            $"    ~mapa   clima idade {clima?.curWeatherAge ?? -1,7} " +
            $"mult {(clima?.CurMoveSpeedMultiplier ?? 0f).ToString("R", System.Globalization.CultureInfo.InvariantCulture),-12} " +
            $"precisao {(clima?.CurWeatherAccuracyMultiplier ?? 0f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
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
            // **Quando o job começou e quando ele expira.**
            //
            // Uma divergência apareceu como um javali trocando `Wait_Wander` por
            // `Wait_MaintainPosture` num lado e não no outro — quinze ticks
            // antes de qualquer diferença de sorteio. O jogo faz isso quando um
            // job **termina** com sucesso e o pawn não está andando:
            //
            //   if (condition == Succeeded && jobDef != Wait_MaintainPosture && …)
            //       if (!pawn.pather.Moving)
            //           StartJob(MakeJob(Wait_MaintainPosture, 1));
            //
            // Ou seja: o job de um lado acabou e o do outro não. E a duração de
            // `Wait_Wander` é `expiryInterval = ticksBetweenWandersRange
            // .RandomInRange` — sorteada na criação.
            //
            // Sem estes dois números não dá para distinguir "começou em ticks
            // diferentes" de "sorteou durações diferentes", e são causas em
            // lugares opostos.
            $"desde {job?.startTick ?? -1,7} expira {job?.expiryInterval ?? -1,6}  " +
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
            // **Precisão total, não quatro casas.**
            //
            // Quatro casas diziam "idêntico" em nove pawns sangrando enquanto a
            // decisão de largar sangue saía diferente. `Rand.Chance(p)` compara
            // `Rand.Value < p`: basta o limiar diferir no sétimo decimal e o
            // sorteio cair naquela fresta.
            //
            // E há um jeito conhecido de isso acontecer: `BleedRateTotal` é uma
            // **soma sobre a lista de hediffs**, e soma de float não é
            // associativa. Ordem de coleção diferente dá somas que só divergem
            // nos últimos bits — uma das seis famílias que nomeamos no começo, e
            // a única que quatro casas escondem.
            //
            // "R" é ida e volta: o texto reconstrói o mesmo float.
            $"sangue {(pawn.health?.hediffSet?.BleedRateTotal ?? 0f).ToString("R"),-12} " +
            $"hediffs {pawn.health?.hediffSet?.hediffs?.Count ?? 0,3}  " +
            $"ritmo {pawn.UpdateRateTicks,3} " +
            $"delta {Delta(pawn),3} saude {DeltaDaSaude(pawn),3} ordem {Ordem(pawn),3} " +
            $"postura {(int)RimWorld.PawnUtility.GetPosture(pawn)} " +
            $"corpo {pawn.BodySize.ToString("R")}";
    }

    public static void Despejar()
    {
        if (porTick.Count == 0)
        {
            Log.Message("[WithFriends] rastreio de pawns vazio");
            return;
        }

        Log.Message(
            $"[WithFriends] rastreio de estado dos pawns — geração {VisitaEmAndamento.UltimaRessincronizacao}, mesmos ticks da digital\n" +
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

/// <summary>
/// Anota com que <c>delta</c> a saúde de cada pawn foi tickada.
///
/// <para>Prefixo puro de leitura — não muda nada, só registra. Ver
/// <c>RastreioDePawns.AnotarDeltaDaSaude</c> para por que esta é a última
/// variável que faltava medir.</para>
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTickInterval))]
public static class DeltaDaSaudeAnotado
{
    // `pawn` é campo privado do tracker: alcançado por reflexão, como todo
    // acoplamento interno.
    static readonly System.Reflection.FieldInfo? CampoDoPawn =
        HarmonyLib.AccessTools.Field(typeof(Pawn_HealthTracker), "pawn");

    [HarmonyLib.HarmonyPrefix]
    public static void Antes(Pawn_HealthTracker __instance, int delta)
    {
        if (!RastreioDePawns.Ligado) return;
        if (CampoDoPawn?.GetValue(__instance) is Pawn pawn)
        {
            RastreioDePawns.AnotarDeltaDaSaude(pawn, delta);
            RastreioDePawns.AnotarOrdem(pawn);
        }
    }
}
