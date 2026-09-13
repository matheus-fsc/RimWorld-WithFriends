# ADR 0020 — Estado de processo não está no save

**Status:** aceito, implementado
**Data:** 2026-09-13
**Origem:** "vamos consertar o DropBloodFilth agora" — a sexta divergência
seguida na mesma família, depois de todo instrumento geral dizer que as
entradas eram idênticas.

## O sintoma que não fechava

Seis divergências caíram em `Pawn_HealthTracker.DropBloodFilth`. Os
instrumentos gerais mediram tudo o que sabiam medir, e tudo batia entre os dois
lados: taxa de sangramento bit a bit (formato `"R"`), tamanho do corpo,
postura, hediffs, `UpdateRateTicks`, `tickDelta`, delta do tick de saúde, ordem
de tick, e a posição do gerador no tick anterior.

O rastreio específico (`-rastrearsangue`) fechou o cerco:

```
tick 218   A: Micky em 46,154, espessura 5, engrossa False
tick 218   B: Micky em 46,154, espessura 5, engrossa False
```

Decisão idêntica, célula idêntica, estado da célula idêntico — e mesmo assim o
lado A gastou um `Rand.Range` a mais, vindo de `Filth.SpawnSetup`. Um lado fez
nascer sangue novo; o outro não.

## A causa

`espessura 5, engrossa False` é exatamente a condição que manda o
`FilthMaker.TryMakeFilth` caminhar os vizinhos:

```csharp
if (!c.WalkableByAny(map) || (outFilth != null && !outFilth.CanBeThickened)) {
    if (shouldPropagate) {
        List<IntVec3> list = GenAdj.AdjacentCells8WayRandomized();
        for (int i = 0; i < 8; i++) { ... }   // primeiro vizinho que aceitar
    }
}
```

E do outro lado:

```csharp
public static class GenAdj {
    private static List<IntVec3> adjRandomOrderList;   // vive o processo inteiro

    public static List<IntVec3> AdjacentCells8WayRandomized() {
        if (adjRandomOrderList == null) { /* monta na ordem canônica */ }
        adjRandomOrderList.Shuffle();      // embaralha o que já estava lá
        return adjRandomOrderList;
    }
}
```

O embaralhamento é Fisher–Yates. Com o mesmo gerador e a **mesma ordem de
entrada**, sai a mesma ordem de saída — mas a ordem de entrada é o resto do
embaralhamento anterior, e os anteriores incluem tudo o que cada máquina fez
desde que abriu o jogo: a colônia que o anfitrião jogava sozinho antes de
convidar, a tela que o visitante deixou aberta, o mouse passando.

Por isso o sintoma enganava: os dois lados **sorteavam igual** — sete trocas,
sete números, contador do gerador em dia — e visitavam vizinhos diferentes. A
divergência de gerador só aparecia **depois** da decisão divergente, o que
mandou a investigação para o lado errado seis vezes seguidas.

## A classe do problema

Não é um bug de sangue. É uma classe inteira: **estado que vive no processo e
não viaja no save**. O save carrega a partida; ele não carrega em que ordem
uma lista estática ficou depois de mil embaralhamentos.

É onde os sete anos do Multiplayer mais rendem. O construtor de
`MultiplayerGame` é uma lista de exatamente isto — cada linha paga com uma
caçada como esta.

## A decisão

Duas famílias, dois remédios, porque o problema não é o mesmo.

**1. Contadores e caches → zerar na carga da visita.**
`Room.nextRoomID`, `District.nextDistrictID`, `Region.nextId`,
`CellFinder.mapEdgeCells`, `ListerHaulables.groupCycleIndex`, as chaves de
`DebugSettings`, as sementes guardadas nos `ThingSetMaker`. Depois da carga só
a simulação mexe neles, e a simulação anda igual dos dois lados.

O gancho é `Game.LoadGame` com a visita ativa — a mesma porta por onde os dois
lados entram (ADR 0010) e que todo ponto de junção reabre (ADR 0019).
Implementado em `EstaticosDaSessao`.

**2. Listas embaralhadas no lugar → ordem canônica a cada sorteio.**
Zerar na carga não basta: o primeiro embaralhamento feito pela interface de um
dos lados já separa os dois de novo, e a interface não é sincronizada por
desenho (ADR 0004). Estas precisam da ordem de entrada fixada **em toda**
chamada. Implementado em `OrdemCanonicaAntesDoSorteio`.

Normalizar a entrada não enviesa nada: Fisher–Yates sobre qualquer ordem
inicial dá permutação uniforme. Fixar a entrada só faz a permutação virar
função exclusiva do gerador — que é precisamente a propriedade que falta.

**3. Caso especial: a interface não embaralha.**
`Zone.Cells` e `Plan.Cells` embaralham **uma vez**, na primeira leitura, e
guardam a ordem para o resto da partida. Quem lê primeiro pode ser a interface
— desenhar zona, tooltip, menu de prioridade. Aqui a saída é a do Multiplayer
(`CellsShufflePatchShared`): a interface recebe as células na ordem em que
estão e não marca nada; o embaralhamento fica para o primeiro pedido de dentro
da simulação, que acontece no mesmo tick dos dois lados.

## Como a lista foi levantada

Não por caçada: por varredura. Decompilamos o assembly inteiro e procuramos
toda chamada a `.Shuffle()` cuja lista **não** é reconstruída nas linhas
anteriores — 14 candidatas de 65 chamadas. Destas, as que sobrevivem ao
recarregamento e são lidas pela simulação viraram guarda; as que são limpas e
refeitas a cada chamada (`fireList`, `equalizeCells`, `tmpCells`) não precisam
de nada.

O mesmo método serve para a próxima versão do jogo, e está descrito em
`docs/AUDITORIA.md`.

## O que isto custou e o que devolveu

Antes: divergência a cada poucas centenas de ticks em combate, sempre no
sangue. Depois: duas corridas de ~4.000 ticks cada, com dois assaltos, combate
e cadáveres, **sem uma única divergência** — medidas por `wf emular … --roteiro
raid` e `wf comparar`, sem ninguém clicando.

## Consequências

- Toda visita começa com o estado de processo igual dos dois lados, e todo
  ponto de junção o restaura.
- Guardas novas aparecem no relatório de `GuardasDeDeterminismo`: guarda que
  nunca dispara depois de uma visita inteira é suspeita.
- Fora de visita nada disto roda: mod não muda o jogo de quem joga sozinho.
- `SymbolResolver_SingleThing.tmpRotations` é canonizado na carga, sem remendo:
  o método que a embaralha só roda na geração de estruturas, dentro do tick, e
  ninguém de fora da simulação encosta nela.
