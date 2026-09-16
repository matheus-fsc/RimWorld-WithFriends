// Inspirado em Source/Client/MultiplayerGame.cs de rwmt/Multiplayer, MIT,
// Copyright (c) 2018 Zetrith. Ver THIRD_PARTY/Multiplayer-MIT.txt

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.BaseGen;
using Verse;
using Verse.AI;

namespace WithFriends.Client.Session;

/// <summary>
/// O estado que não está no save.
///
/// <para><b>A descoberta.</b> Seis divergências seguidas caíram em
/// <c>DropBloodFilth</c>, e todo instrumento geral dizia que as entradas eram
/// idênticas: mesma taxa de sangramento bit a bit, mesma célula, mesma
/// espessura, mesmo gerador. E ainda assim um lado nascia sangue novo e o outro
/// não.</para>
///
/// <para>A causa estava aqui dentro:</para>
///
/// <code>
/// // FilthMaker.TryMakeFilth — quando a célula já tem sangue que não engrossa
/// List&lt;IntVec3&gt; list = GenAdj.AdjacentCells8WayRandomized();
/// for (int i = 0; i &lt; 8; i++) { ... }   // tenta os vizinhos nessa ordem
///
/// // GenAdj
/// private static List&lt;IntVec3&gt; adjRandomOrderList;     // vive o processo inteiro
/// adjRandomOrderList.Shuffle();                         // embaralha o que já estava lá
/// </code>
///
/// <para>O embaralhamento é Fisher–Yates: com o <b>mesmo</b> gerador e a
/// <b>mesma</b> ordem de entrada, sai a mesma ordem de saída. Mas a ordem de
/// entrada é o que sobrou do embaralhamento anterior — e os embaralhamentos
/// anteriores incluem tudo o que cada máquina fez desde que abriu o jogo:
/// a colônia que o anfitrião estava jogando sozinho, a tela que o visitante
/// deixou aberta, o passar do mouse.</para>
///
/// <para>Por isso o sintoma era tão traiçoeiro: os dois lados <b>sorteavam
/// igual</b> — sete trocas, sete números, contador do gerador em dia — e
/// mesmo assim visitavam vizinhos diferentes. Um achava célula livre e nascia
/// sangue (mais sorteios, em <c>CanMakeFilth</c> e <c>Filth.SpawnSetup</c>); o
/// outro não achava e voltava sem sortear. A divergência de gerador aparecia
/// <b>depois</b> da decisão divergente, o que mandou a investigação para o lado
/// errado durante seis rodadas.</para>
///
/// <para><b>Estado de processo, não estado de partida.</b> É a classe inteira do
/// problema, e é onde os sete anos do Multiplayer mais rendem: o construtor de
/// <c>MultiplayerGame</c> é uma lista de coisas exatamente como esta, cada uma
/// paga com uma caçada. Aqui elas estão separadas em duas famílias, porque têm
/// remédios diferentes:</para>
///
/// <list type="number">
/// <item><b>Contadores e caches</b> — <c>nextRoomID</c>, células de borda,
/// índices de ciclo. Zerar no começo da visita basta: o que vier depois é a
/// simulação, e a simulação anda igual dos dois lados. É o que
/// <see cref="Zerar"/> faz.</item>
/// <item><b>Listas que são embaralhadas no lugar</b> — esta aqui. Zerar no
/// começo <b>não</b> basta: o primeiro embaralhamento feito pela interface de um
/// dos lados já separa os dois de novo, e a interface não é sincronizada por
/// desenho (ADR 0004). Estas precisam de ordem canônica <b>a cada</b> sorteio —
/// ver <see cref="OrdemCanonicaAntesDoSorteio"/>.</item>
/// </list>
///
/// <para>Uma observação que vale para as duas: normalizar a ordem de entrada não
/// enviesa nada. Fisher–Yates sobre qualquer ordem inicial dá permutação
/// uniforme; fixar a entrada só faz a permutação virar função do gerador, que é
/// precisamente o que se quer.</para>
/// </summary>
public static class EstaticosDaSessao
{
    /// <summary>
    /// Zera o estado de processo antes de a partida da visita ser carregada.
    ///
    /// <para>Roda nos <b>dois</b> lados e em <b>toda</b> carga de visita —
    /// inclusive a de cada ponto de junção — porque os dois lados entram na
    /// visita pela mesma porta: pelo save (ver <c>BootstrapDaPartida</c>).</para>
    ///
    /// <para>Antes da carga, não depois: parte disto é consumida durante o
    /// próprio carregamento (ids de sala, células de borda), e zerar depois
    /// deixaria justamente a parte carregada divergente.</para>
    /// </summary>
    public static void Zerar(string motivo)
    {
        int feitos = 0;
        var faltando = new List<string>();

        void Campo(Type tipo, string nome, object? valor)
        {
            var campo = AccessTools.Field(tipo, nome);
            if (campo == null) { faltando.Add($"{tipo.Name}.{nome}"); return; }

            try { campo.SetValue(null, valor); feitos++; }
            catch (Exception e) { faltando.Add($"{tipo.Name}.{nome} ({e.GetType().Name})"); }
        }

        void Limpar(Type tipo, string nome)
        {
            var campo = AccessTools.Field(tipo, nome);
            if (campo == null) { faltando.Add($"{tipo.Name}.{nome}"); return; }

            try { (campo.GetValue(null) as IList)?.Clear(); feitos++; }
            catch (Exception e) { faltando.Add($"{tipo.Name}.{nome} ({e.GetType().Name})"); }
        }

        // Listas embaralhadas no lugar. A ordem canônica por sorteio é a guarda
        // de verdade; zerar aqui só garante que a primeira visita já comece
        // limpa, mesmo que algum caminho escape do remendo.
        Campo(typeof(GenAdj), "adjRandomOrderList", null);
        Campo(typeof(Toils_Ingest), "cardinals", GenAdj.CardinalDirections.ToList());
        Campo(typeof(Toils_Ingest), "diagonals", GenAdj.DiagonalDirections.ToList());

        // Caches de mapa que sobrevivem à troca de partida. `mapEdgeCells` é
        // reconstruída sob demanda e depois **embaralhada** por quem sorteia
        // ponto de chegada — é o caminho de assalto, que é onde caçamos.
        Campo(typeof(CellFinder), "mapEdgeCells", null);
        Campo(typeof(CellFinder), "mapSingleEdgeCells", new List<IntVec3>[4]);

        // **Os caches de stat, que não estão no save e mudam a velocidade.**
        //
        // `StatWorker` guarda o valor de cada stat por coisa, carimbado com o
        // tick (`temporaryStatCache`). O worker vive na base de defs, ou seja,
        // dura o processo inteiro — e nada disso vai para o save.
        //
        // O anfitrião chega à visita com esses caches cheios do que ele jogou
        // antes; o visitante carrega o save e começa vazio. Enquanto uma entrada
        // velha não envelhece, um lado lê o valor guardado e o outro calcula o
        // atual — e a simulação anda com velocidades diferentes sem ter sorteado
        // nada.
        //
        // Medido: logo no tick 14 de uma visita, com a capacidade de mover
        // IDÊNTICA dos dois lados e o stat diferente na sexta casa:
        //
        //   A: Huber  vel 4.33074331  mover 1.02  tpm 13.96581
        //   B: Huber  vel 4.331489    mover 1.02  tpm 13.96341
        //
        // O guarda de <c>CacheDeStatForaDaInterface</c> impede a interface de
        // ENVENENAR daqui para a frente; este zera o que já estava envenenado
        // antes de a visita começar. São as duas metades do mesmo problema.
        int statsLimpos = 0;
        foreach (var stat in DefDatabase<StatDef>.AllDefsListForReading)
        {
            try { stat.Worker?.TryClearCache(); statsLimpos++; }
            catch (Exception) { /* stat sem worker: não guarda nada */ }
        }
        if (statsLimpos > 0) feitos++;

        // Contadores de id. Não sorteiam, mas entram em comparação e ordenação —
        // e id diferente para a mesma sala vira decisão diferente mais adiante.
        Campo(typeof(Room), "nextRoomID", 1);
        Campo(typeof(District), "nextDistrictID", 1);
        Campo(typeof(Region), "nextId", 1);

        // As quatro rotações candidatas da geração de estruturas: estático,
        // embaralhado no lugar, e sem ordem canônica por chamada.
        //
        // Aqui basta zerar na carga, em vez de remendar o método que embaralha:
        // ele só roda na geração de estruturas, que acontece dentro do tick e
        // igual nos dois lados. Os dois entram na visita com a mesma ordem, e
        // daí em diante ninguém de fora da simulação encosta nela.
        feitos += CanonizarRotacoes(faltando);

        // Onde o carregamento de coisas para arrastar parou da última vez.
        Campo(typeof(ListerHaulables), "groupCycleIndex", 0);
        Limpar(typeof(ListerHaulables), "cellCycleIndices");

        feitos += ZerarPreferenciasDeDepuracao(faltando);
        feitos += ZerarSementesDeConjuntos(faltando);

        Log.Message(
            $"[WithFriends] estáticos de processo zerados ({motivo}): {feitos} item(ns)" +
            (faltando.Count == 0 ? "." : $"; não encontrados: {string.Join(", ", faltando)}"));

        GuardasDeDeterminismo.Disparou("estáticos de processo zerados na carga da visita");
    }

    /// <summary>
    /// As chaves de depuração do jogo voltam ao padrão dentro da visita.
    ///
    /// <para>Elas são globais do processo e mudam a simulação de verdade —
    /// <c>enableStoryteller</c>, <c>enableRandomMentalStates</c>,
    /// <c>godMode</c>. Um lado com dano desligado e o outro não é divergência
    /// garantida, e nada disso viaja no save.</para>
    ///
    /// <para>O jeito é o do Multiplayer, e é mais esperto do que parece: zerar
    /// todos os <c>bool</c> e então <b>reinvocar o construtor estático</b>, que
    /// restaura os que nascem ligados (<c>enableDamage</c> e companhia). Assim
    /// não há lista de padrões para manter desatualizada.</para>
    /// </summary>
    static int ZerarPreferenciasDeDepuracao(List<string> faltando)
    {
        try
        {
            foreach (var campo in typeof(DebugSettings)
                         .GetFields(BindingFlags.Public | BindingFlags.Static))
                if (!campo.IsLiteral && campo.FieldType == typeof(bool))
                    campo.SetValue(null, false);

            typeof(DebugSettings).TypeInitializer?.Invoke(null, null);
            return 1;
        }
        catch (Exception e)
        {
            faltando.Add($"DebugSettings ({e.GetType().Name})");
            return 0;
        }
    }

    /// <summary>
    /// As sementes guardadas dentro dos geradores de conjunto de coisas.
    ///
    /// <para><c>ThingSetMaker_Nutrition</c> e <c>ThingSetMaker_MarketValue</c>
    /// guardam <c>nextSeed</c> <b>entre chamadas</b>, e os geradores moram nos
    /// defs — ou seja, vivem o processo inteiro, exatamente como um estático.
    /// É o que decide a carga de um pod, a recompensa de uma missão, o saque de
    /// um assalto.</para>
    ///
    /// <para>Os defs não expõem os filhos de forma uniforme (<c>Sum</c> tem
    /// <c>options</c>, <c>Conditional</c> tem um filho só), então a varredura é
    /// reflexiva a partir da raiz de cada def, com limite de profundidade para
    /// não se perder em ciclo.</para>
    /// </summary>
    static int ZerarSementesDeConjuntos(List<string> faltando)
    {
        const int SementeFixa = 1;

        try
        {
            int tocados = 0;
            var vistos = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var def in DefDatabase<ThingSetMakerDef>.AllDefs)
                Visitar(def.root, 0);

            void Visitar(object? alvo, int profundidade)
            {
                if (alvo == null || profundidade > 6 || !vistos.Add(alvo)) return;

                var semente = AccessTools.Field(alvo.GetType(), "nextSeed");
                if (semente != null && semente.FieldType == typeof(int))
                {
                    semente.SetValue(alvo, SementeFixa);
                    tocados++;
                }

                foreach (var campo in AccessTools.GetDeclaredFields(alvo.GetType())
                             .Concat(alvo.GetType().BaseType is { } b
                                 ? AccessTools.GetDeclaredFields(b)
                                 : new List<FieldInfo>()))
                {
                    if (campo.IsStatic || campo.FieldType.IsPrimitive || campo.FieldType == typeof(string))
                        continue;

                    object? valor;
                    try { valor = campo.GetValue(alvo); } catch { continue; }

                    if (valor is IEnumerable itens and not string)
                        foreach (var item in itens) Visitar(item, profundidade + 1);
                    else if (valor != null && !valor.GetType().IsValueType)
                        Visitar(valor, profundidade + 1);
                }
            }

            return tocados > 0 ? 1 : 0;
        }
        catch (Exception e)
        {
            faltando.Add($"ThingSetMaker.nextSeed ({e.GetType().Name})");
            return 0;
        }
    }

    static int CanonizarRotacoes(List<string> faltando)
    {
        var campo = AccessTools.Field(typeof(SymbolResolver_SingleThing), "tmpRotations");
        if (campo?.GetValue(null) is not Rot4[] rotacoes || rotacoes.Length != 4)
        {
            faltando.Add("SymbolResolver_SingleThing.tmpRotations");
            return 0;
        }

        for (int i = 0; i < 4; i++) rotacoes[i] = new Rot4(i);
        return 1;
    }

    sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// Zerar acontece na carga da partida da visita — a mesma porta por onde os dois
/// lados entram, e a mesma que todo ponto de junção reabre.
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
public static class EstaticosZeradosAoCarregar
{
    [HarmonyPrefix]
    public static void Antes()
    {
        if (!VisitaEmAndamento.Ativa) return;
        EstaticosDaSessao.Zerar("carga da partida da visita");
    }
}
