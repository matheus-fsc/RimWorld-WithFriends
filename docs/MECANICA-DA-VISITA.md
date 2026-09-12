# A mecânica da visita, como o jogo já a implementa

> Levantado direto do `Assembly-CSharp.dll` 1.6.4871. Serve para derivar o
> encontro (§5) da mecânica existente em vez de inventar uma paralela — foi
> exatamente o erro do RT no mercado (§6).

## O que o RimWorld já resolve

### Entrar num mapa com uma caravana

```csharp
RimWorld.Planet.CaravanEnterMapUtility.Enter(
    Caravan caravan, Map map, CaravanEnterMode enterMode,
    CaravanDropInventoryMode dropInventoryMode = DoNotDrop,
    bool draftColonists = false, Predicate<IntVec3> extraCellValidator = null)
```

É o caminho que o jogo usa quando sua caravana entra num assentamento alheio.
Escolhe borda do mapa, posiciona os pawns, opcionalmente larga o inventário e
opcionalmente já entra com os colonos recrutados.

### Sair do mapa e virar caravana de novo

```csharp
RimWorld.Planet.CaravanExitMapUtility.ExitMapAndCreateCaravan(
    IEnumerable<Pawn> pawns, Faction faction,
    PlanetTile exitFromTile, PlanetTile directionTile, PlanetTile destinationTile)
```

Tira os pawns do mapa, monta a caravana, passa quem não é world pawn para
`Find.WorldPawns`, e ainda dispara a rota até o destino.

**É literalmente o fim da visita.** Não precisamos inventar "retorno": ele já
existe, com nome e assinatura.

### O que uma caravana carrega

```csharp
Caravan.AllThings => CaravanInventoryUtility.AllInventoryItems(this).Concat(pawns);

// e AllInventoryItems é só isto:
foreach (Pawn pawn in caravan.PawnsListForReading)
    foreach (Thing item in pawn.inventory.innerContainer)
        inventoryItems.Add(item);
```

**Não existe porão de carga.** Uma caravana é uma lista de pawns, cada um
carregando as próprias coisas. Isso simplifica muito o delta de retorno: quem
volta são pawns, e o que volta com eles está dentro deles.

## As respostas que o jogo já dá

| Pergunta | Resposta do RimWorld |
|---|---|
| Pawn morre na visita | vira `Corpse`, uma coisa no mapa do anfitrião. Só volta se **outro pawn carregar** o corpo no inventário — igual a atacar um assentamento no jogo base. |
| Pawn fica derrubado | continua pawn; sai na caravana normalmente (carregado por outro, mecânica existente). |
| Saque | o que estiver no inventário dos pawns na hora da saída. Também igual ao jogo base. |
| Coisas largadas no chão | ficam. É a colônia do anfitrião. |
| Construções feitas pelo visitante | ficam. Idem. |

Nenhuma regra nova precisa ser inventada: **o encontro herda a ética do jogo
base.** E onde o jogo base depende de confiança entre jogadores (nada impede o
visitante de sair carregando o aço do anfitrião), a §1.1 já disse que confiança
é premissa — não vamos construir anticheat.

## O que o jogo **não** resolve: pawn atravessando partidas

O `Scribe` de um pawn guarda os trackers por dentro (`Scribe_Deep`):

```
story · guest · royalty · relations (social) · ideo · outfits · drugs
```

Isso é boa notícia — eles viajam junto. A má notícia está **dentro** deles, em
referências a objetos de nível de partida:

| Referência | Onde mora | Visto no log de teste |
|---|---|---|
| `Ideo_8` | `IdeoManager` | `Could not resolve reference to object with loadID Ideo_8` |
| `ApparelPolicy_Anything_1` | banco de políticas do `Game` | idem |
| `DrugPolicy_Social drugs_1` | idem | idem |
| `Thing_Human342` | outro pawn (relação social) | `curParent=RimWorld.DirectPawnRelation` |

E há um perigo pior que a referência quebrada: **o id pode existir dos dois
lados significando coisas diferentes.** Se a partida do anfitrião também tem um
`Ideo_8`, o pawn visitante se liga silenciosamente à ideologia errada — sem
erro, sem aviso.

### Consequência para o desenho

Duas direções, com dificuldades diferentes:

**Volta (visita → colônia do visitante): provavelmente fácil.** O visitante
restaura o `checkpoint_pre_sessao`, onde `Ideo_8`, políticas e os outros pawns
existem com os mesmos ids de quando ele saiu. As referências voltam a resolver
sozinhas.

**Ida (colônia do visitante → partida do anfitrião): é o trabalho de verdade.**
Precisa de remapeamento explícito de ids: levar junto os objetos referenciados
(ideologia, políticas) e realocá-los no espaço de ids do anfitrião, ou
substituí-los por equivalentes locais. Relações com pawns que não foram na
caravana são cortadas — o que, aliás, é o comportamento correto.

## A estratégia da ida (implementada)

`client/Session/MalaDoVisitante.cs`:

1. **Empacotar** — pawns e as ideologias deles no **mesmo arquivo**, com os
   ids originais. É o truque central: como as ideologias viajam junto, o
   próprio `Scribe` resolve as referências dos pawns durante a carga, sem
   remendo nenhum em `LoadedObjectDirectory`.
2. **Desempacotar** — carrega, e **só então** troca os ids: ideologia ganha
   `GetNextIdeoID()` e entra no `IdeoManager` local; pawn ganha
   `GetNextThingID()` e a facção local do visitante.

A ordem é o ponto. Trocar os ids antes da carga quebraria as referências;
depois, elas já foram resolvidas para os objetos certos.

### O que o primeiro teste mostrou

Empacotar e abrir dois colonos **na mesma partida** (pior caso de colisão)
funcionou no essencial:

```
mala empacotada: 2 pawn(s), 1 ideologia(s), 43.8 KB, 203 ms
mala aberta:     2 pawn(s), 1 ideologia(s), 43.8 KB, 140 ms
  Osborn  id 18076, ideologia Astropolitan 2, facção New Arrivals
  Hahn    id 18077, ideologia Astropolitan 2, facção New Arrivals
```

Ids novos, ideologia religada, originais intactos. **43,8 KB para dois
pawns** — a mala é irrisória perto do mapa (0,64 MB) e da partida (1,31 MB).

Mas o log trouxe erros que revelaram uma peça que faltava:

```
Could not resolve reference to loadID ApparelPolicy_Anything_1
Could not resolve reference to loadID DrugPolicy_Social drugs_1
Could not do PostLoadInit on Pawn_IdeoTracker: NullReferenceException
Error while determining if Osborn should have Need Chemical_Alcohol: NullReferenceException
```

Causa única: **uma carga no meio do jogo começa com o diretório de referências
vazio.** `Scribe.loader.InitLoading` limpa o `LoadedObjectDirectory`, e só o
que vem do arquivo é registrado ali. Facções e políticas desta partida existem
na memória, mas não para o Scribe — então toda referência a elas resolvia para
`null`, e o pawn chegava sem facção e sem política, quebrando em
`PostLoadInit`.

A correção é apresentar os objetos vivos antes de carregar
(`ApresentarObjetosLocais`): facções e as quatro bases de políticas entram no
diretório, e as referências passam a resolver para os equivalentes locais.
Ideologias **não** entram — elas viajam no arquivo, e registrar as locais
criaria conflito de id.

É a mesma necessidade que o Multiplayer resolve com `ScribeUtil.sharedCrossRefs`
mantido sempre populado. Aqui basta popular na hora, porque a carga é pontual.

### O segundo achado: o jogo assume que carga não acontece jogando

Depois de apresentar os objetos locais, as políticas pararam de falhar, mas
dois erros persistiram — e a pilha mostrou que eram **o mesmo**:

```
Error while determining if Blue should have Need Chemical_Alcohol: NRE
Could not do PostLoadInit on Pawn_IdeoTracker: NRE
    ↓ ambos vinham de
  Pawn.get_DevelopmentalStage → Pawn_AgeTracker.RecalculateLifeStageIndex
  → LifeStageWorker_HumanlikeAdult.Notify_LifeStageStarted [0x000e0]
```

No código do jogo:

```csharp
public override void Notify_LifeStageStarted(Pawn pawn, LifeStageDef previousLifeStage)
{
    ...
    if (Current.ProgramState != ProgramState.Playing) return;     // ← sai aqui numa carga normal
    ...
    if (!pawn.IsColonist) return;
    ...
    if (previousLifeStage.developmentalStage.Juvenile())          // ← NRE: previousLifeStage é nulo
```

É um bug do jogo base que ninguém vê, porque numa carga normal o estado é
`MapInitializing` e a função sai antes. Nós carregamos com o jogo rodando, e
caímos direto nele.

A correção é dizer a verdade ao jogo: durante o desempacotamento,
`Current.ProgramState` vira `MapInitializing` e volta depois. Não é truque —
é o que o próprio `Game.LoadGame` faz.

**Lição para o resto do M3:** carregar estado com o jogo rodando é território
onde o jogo base não costuma pisar. Vai haver mais casos assim, e o padrão de
diagnóstico é o mesmo — ler a pilha até achar o `if` que assume o estado.

### Resultado final da ida

Com as duas correções, o fluxo fica limpo:

```
30 objeto(s) locais apresentados ao Scribe para resolução
mala aberta: 3 pawn(s), 1 ideologia(s), 59.3 KB, 81 ms
  Baum      id 20367, ideologia Astropolitan, facção New Arrivals
  Disaster  id 20368, ideologia Astropolitan, facção New Arrivals
  Rachel    id 20369, ideologia Astropolitan, facção New Arrivals
```

Zero `PostLoadInit` falhando, zero NRE de needs. Sobrou **um** aviso, e ele
está certo:

```
Could not resolve reference to loadID Thing_YorkshireTerrier4111
Pawn Rachel has relation "Bond" with null pawn after loading.
```

O cachorro da Rachel ficou em casa. A relação com ele não existe na visita — é
exatamente o comportamento desejado, e o jogo avisa porque no contexto dele
seria erro.

**59 KB para três colonos com inventário.** Contra 0,64 MB do mapa e 1,31 MB da
partida: a mala é ruído estatístico.

### Uma sujeira que o teste revelou

Cada desempacotamento **adiciona** a ideologia do visitante ao `IdeoManager` do
anfitrião. Numa visita só isso é correto — a ideologia do visitante é
genuinamente outra. Mas o anfitrião continua jogando o próprio jogo depois do
encontro, e sem limpeza elas se acumulariam: uma a cada visita, para sempre.

`DevolverIdeologias` remove na saída o que ninguém mais usa. Se algum pawn do
anfitrião converteu-se à ideologia do visitante, ela fica — aí é história do
jogo, não entulho.

### Um cuidado que sobra para a ida de verdade

Entre partidas diferentes, `Faction_12` pode existir nos dois lados
significando facções diferentes — e aí o pawn se ligaria silenciosamente à
facção errada. Por isso `Nacionalizar` **sempre** reatribui a facção local
depois da carga, em vez de confiar no que veio.

O que fica de fora, de propósito: relações com pawns que ficaram em casa
(o `Scribe` as descarta) e políticas de roupa/droga, que voltam ao padrão
local. Uma relação com alguém que não está na visita não deveria existir na
visita.

## Mecânicas relacionadas que valem consultar depois

```
CaravanVisitUtility          visitar assentamento alheio
CaravanArrivalAction_*       o que acontece ao chegar (entrar, comerciar, atacar)
CaravanFormingUtility        montagem da caravana, downed pawns, transferíveis
CaravanMergeUtility          juntar caravanas
SettlementUtility            gerar mapa de assentamento alheio
```

## A interface não pode mexer na simulação

Durante meses de Multiplayer, Zetrith chegou a um conceito que faltava aqui:
`Multiplayer.InInterface`. Definido **por exclusão** — se não é tick, não é
comando e não é carregamento, então é interface:

```csharp
public static bool InInterface =>
    Client != null && !Ticking && !ExecutingCmds && !reloading
    && Current.ProgramState == ProgramState.Playing
    && LongEventHandler.currentEvent == null;
```

O motivo é mais fundo que "cosmético consome RNG". No RimWorld, código de
interface **pergunta à simulação**, e a simulação **responde sorteando e
guardando**. O exemplo que nos pegou:

```csharp
Pawn_MeleeVerbs.TryGetMeleeVerb(target)   // sorteia um verbo
                                          // e cacheia em curMeleeVerb
```

Quem chama é o menu flutuante — logo, **passar o mouse sobre um inimigo muda o
estado do pawn**. Um jogador passa o mouse, o outro não; o próximo ataque corpo
a corpo diverge. Era exatamente a assinatura medida: ~110 sorteios de diferença,
o mesmo evento acontecendo três ticks depois de um lado
(`tick 1046 J2 +128` / `tick 1049 J1 +139`).

Nossa versão está em `client/Session/NaInterface.cs`. Dentro da interface,
`TryGetMeleeVerb` passa a responder deterministicamente: o primeiro verbo com
peso de seleção não nulo, sem sortear e sem cachear.

Esse é o **terceiro** tipo de fonte de divergência que encontramos, e o padrão
para os próximos:

| tipo | exemplo | remédio |
|---|---|---|
| cosmético consome RNG | motes, flecks, som | `EfeitosNaoDeterministicos` restaura o estado |
| decisão depende da câmera | `ShouldSpawnMotesAt` | responder igual para todos |
| **interface muda a simulação** | `TryGetMeleeVerb` | `NaInterface.Agora` → resposta determinística, sem cache |

## O 1.6 faz a simulação depender da câmera

Esta foi a causa raiz de "falha ao mover a câmera", e não é da família das
anteriores. Não é cosmético consumindo RNG, nem interface mexendo na simulação:
**é a própria simulação andando em velocidades diferentes nos dois lados.**

`Thing.DoTick` não chama `TickInterval` todo tick. Ele acumula `tickDelta` e só
chama quando passa do ritmo do objeto — e o ritmo é:

```csharp
public virtual int UpdateRateTicks => GenTicks.GetCameraUpdateRate(this);

public static int GetCameraUpdateRate(Thing thing) {
    if (!WorldRendererUtility.DrawingMap || thing.MapHeld != Find.CurrentMap) return 15;
    if (!Find.CameraDriver.InViewOf(thing)) return 15;
    return (int)(Find.CameraDriver.CurrentZoom + 1);
}
```

O que está na tela tickia até 15× mais que o que está fora. E `TickInterval`
roda job giver, needs, saúde, inspiração — nada disso é enfeite.

Dois jogadores nunca olham para o mesmo canto. Medido no aborto:

```
tick 152  anfitrião: nada         visitante: 1x InspirationHandler.CheckStartRandomInspiration
tick 153  anfitrião: 3 sorteios   visitante: 33 sorteios em JobGiver_Wander
```

O pawn do visitante estava na tela e pensou; o do anfitrião estava fora e ainda
não. Dois ticks depois o anfitrião pensa também — tarde demais, os dois já
decidiram coisas diferentes.

Remédio em `client/Session/TickIndependenteDaCamera.cs`: durante a sessão,
`GetCameraUpdateRate` devolve 1 para todos. Perde-se a otimização de tick do 1.6
enquanto dura a visita; fora dela nada muda.

### Por que não adianta o anfitrião "ditar o RNG"

Ideia natural, e a medição mostra por que não resolve: a divergência não é
**número diferente**, é **quantidade de chamadas diferente** — 33 sorteios de um
lado contra 3 do outro. Mandar os números do anfitrião pelo fio faria o visitante
consumir 30 números a mais e sair de fase; daí em diante todo sorteio dos dois
seria diferente, só que **sem ninguém detectar** — a digital bateria por acaso ou
não, e o desync viraria silencioso em vez de abortar.

Pior: neste caso o RNG nem era necessário para divergir. Ritmos de tick
diferentes mudam job, need e saúde sem sortear nada. Um lado ditando números não
alcança isso.

Autoridade do anfitrião é uma boa ideia — mas na granularidade certa, que é
**reenviar o estado**, não o sorteio. Ver `docs/IDEIAS.md`.

## Ferramentas de dev durante a visita

Teste proposital: o visitante spawnou uma coisa na colônia do anfitrião pelo
menu de debug. Abortou — e abortou **certo**.

Foram 4472 ticks de lockstep limpo antes disso (contra 160 antes da correção da
câmera). O primeiro tick divergente foi o 4478, e o rastreio nomeou:

```
tick 4478  anfitrião: 10x WildPlantSpawner...   visitante: 9x WildPlantSpawner...
```

A densidade de plantas do mapa mudou porque **o mapa mudou** de um lado só. Uma
ação de debug roda fora do fluxo de tick, na máquina de quem clicou, sem passar
por comando nenhum.

O que faltava não era detecção — era a mensagem dizer o que o jogador fez, em vez
de mostrar dois hashes diferentes. Agora
`client/Session/FerramentasDeDevNaVisita.cs` recusa ações de debug enquanto a
sessão dura, com o nome da ação na recusa. Fora de visita nada muda.

**Por que bloquear e não sincronizar.** O Multiplayer sincroniza ferramentas de
debug e gasta ~640 linhas nisso (`Debug/DebugSync.cs` + `Debug/DebugPatches.cs`),
replicando o clique do outro lado. Cabe fazer depois — integridade antes de
recurso. Bloquear custa um arquivo.

**Ainda aberto:** god mode constrói instantâneo pelo caminho normal de
designator, que ainda não vira comando. Ver `docs/COMANDOS-DE-SESSAO.md`.

## O buffer do lockstep

Sensação de rede ruim em rede local. A causa não era a rede: era a pista.

Duas coisas estavam erradas ao mesmo tempo.

**1. O relato não saía quando mais precisava sair.** Ele ia embora em tick
alinhado (múltiplo de 8, para as digitais serem comparáveis) ou no fallback de
250 ms. Mas o freio cai em `mínimo + folga`, que quase nunca é múltiplo de 8 — o
jogo travava no limite e o próximo tick alinhado estava **do outro lado do
freio**, inalcançável. Sobrava o fallback. Dez ticks a cada 250 ms são 40 ticks
por segundo: menos que 1×, e a 3× o engasgo dos dois lados.

Agora, parado na barreira é sinal de "preciso de mais pista" e não espera
cadência nenhuma.

**2. Um número fazia dois trabalhos.** `TicksDeAtraso = 10` era o atraso de
comando **e** a pista de simulação. São coisas diferentes:

| | para quê | valor |
|---|---|---|
| `TicksDeAtraso` | o comando cair num tick que ninguém simulou ainda | 10 |
| `FolgaDaBarreira` | o jogo não parar a cada ida e volta da rede | 60 |

60 ticks é um segundo de jogo a 1× — pista suficiente para a rede sumir da
conta, e curta o bastante para a divergência ainda ser pega cedo.

### A pista é a latência de comando

Tentei separar as duas e não dá. O comando tem de cair depois de tudo que alguém
possa ter simulado, e a pista é exatamente a licença para simular à frente:
**pista grande, a rede some da conta e o comando demora; pista pequena, o comando
responde e a rede aparece.** É o preço do lockstep, não um ajuste que escapa dele.

A tentativa de escapar custou uma sessão encerrada. Carimbar o comando a partir do
maior tick **relatado** parecia mais apertado e barato:

```csharp
// errado
public long TickDeComando() => TickPorParticipante.Values.Max() + TicksDeAtraso;
```

O furo: a cada quadro o jogo avança vários ticks e relata **uma vez só**, então o
relatado fica atrás do simulado por quanto couber num quadro. Em Superfast deu 31
ticks — o comando chegou carimbado para o tick 228 num lado que já estava em 259:

```
comando de 678f5d3b… chegou tarde: agendado para o tick 228,
e este lado já está em 259. Aplicá-lo agora divergiria do outro lado;
encerrando a sessão.
```

Encerrar foi o comportamento certo, pelo motivo errado. Quem sabe que ninguém
passou de um tick é a **barreira**, não o último relato — é o teto que o próprio
servidor concedeu:

```csharp
public long TickDeComando() => TickLiberado() + TicksDeAtraso;
```

Com a correção, `TicksDeAtraso` vira só margem de trânsito da volta (5), e a
latência passa a ser a pista. Como a pista existia em 60 só para mascarar o stall
de 250 ms — que agora está resolvido na origem, quem trava na barreira relata na
hora — ela volta para 20:

| | antes | agora |
|---|---|---|
| `FolgaDaBarreira` | 60 | 20 |
| `TicksDeAtraso` | 10 | 5 |
| latência do comando | 70 ticks | **25 ticks** |

25 ticks são ~0,4 s a 1×, e menos conforme a velocidade sobe — a latência em
tempo real é `pista / velocidade`, então quem joga rápido sente menos.

## Pico de memória no rollback

Houve um SIGSEGV dentro do GC do mono (`libmonobdwgc`) logo depois de um
rollback, num mapa 250×250 com 16105 things. Um crash no coletor não se explica
de fora, e uma ocorrência não é diagnóstico — mas o pico é real e mensurável: a
partida viva, os bytes do estado pré-rollback recém-serializados e a partida que
vai entrar, tudo ao mesmo tempo.

Dois pontos baratos, feitos: coleta explícita antes do `LoadGame` (o
carregamento já leva segundos, a coleta não aparece), e despejo do histórico de
RNG em pedaços de 500 linhas em vez de uma string de algumas centenas de KB no
heap de objetos grandes — que era alocada exatamente ali, antes do rollback.

Se repetir, vale medir memória antes de mexer em mais coisa.

### A pausa carimbava comando para trás

Segunda sessão encerrada pelo mesmo mecanismo, causa diferente — e esta é
melhor de ver no log do que de explicar:

```
comando 7 de 678f5d3b… agendado para o tick 473 (estou em 468)
comando 8 de da8fd226… agendado para o tick 458 (estou em 453)
comando de da8fd226… chegou tarde: agendado para o tick 458,
e este lado já está em 468. encerrando a sessão.
```

O comando 8 nasceu **antes** do comando 7. A barreira recua quando alguém pausa
(§3, o tempo é negociado) — `Pausados.Count > 0` devolve o mínimo, sem a folga.
Carimbar o comando a partir dela fazia o carimbo recuar junto.

O que já foi concedido já pode ter sido simulado. Então o carimbo passa a sair do
**teto concedido**, que só cresce:

```csharp
public long TickDeComando()
{
    long alvo = Math.Max(TetoConcedido, TickLiberado()) + TicksDeAtraso;
    if (alvo <= ultimoCarimbo) alvo = ultimoCarimbo + 1;   // monotonicidade
    ultimoCarimbo = alvo;
    return alvo;
}
```

Isso também explica por que `TicksDeAtraso = 5` basta, mesmo em Superfast: o
cliente só avança quando recebe uma liberação nova, e a liberação nova sai
**depois** do comando, na mesma conexão. A ordem do TCP faz o resto — a margem
não precisa cobrir a velocidade do jogo.

## O pathfinding do 1.6 roda em threads e atravessa o tick

Quarta divergência com a mesma assinatura — ~3 a 4 sorteios, sempre com pawns em
movimento, sempre depois de empilhar ordens com shift + botão direito. Desta vez
o rastreio estava ligado e nomeou:

```
tick 8068  J2:  3x  Pawn_FilthTracker.Notify_EnteredNewCell
                      < Pawn_PathFollower.TryEnterNextPathCell
                      < Pawn_PathFollower.PatherTick < Pawn.Tick
tick 8070  J1:  3x  (o mesmo)
```

**O mesmo pawn entra na mesma célula em ticks diferentes.** A causa está no
jogo:

```csharp
public void PathFinderTick() {
    ForceCompleteScheduledJobs();              // colhe o do tick ANTERIOR
    ...
    ScheduleBatchedPathJobs(lastGridHandle);   // agenda e segue a vida
}
```

O 1.6 agenda a busca de caminho como Unity Jobs em threads de trabalho e não
espera: quem colhe é o tick seguinte. Entre agendar e colher, os jobs rodam **ao
mesmo tempo** que a thread principal tickando os pawns, e leem `pawn.Position` e
estado de porta enquanto `PatherTick` escreve `pawn.Position = nextCell`.

O que cada job enxerga depende do escalonamento de threads — que não é igual em
duas máquinas, nem em duas execuções da mesma. Caminho diferente → célula
seguinte diferente → o pawn entra na célula num tick diferente → e daí em diante
as duas simulações são outras.

A leitura é do Multiplayer (`Patches/PathFinderPatch.cs`, MIT), que descreve a
corrida em detalhe. A correção proposta lá é tirar uma foto das posições na
thread principal e fazer os jobs lerem a foto — mais fiel ao desenho do jogo, e
muito mais código; está desligada no fonte deles (`#if false`).

`client/Session/PathfindingSemCorrida.cs` faz o mais simples: completa os jobs
ainda dentro do `PathFinderTick`, antes de qualquer pawn tickar. Nada muta
enquanto eles rodam, então o resultado volta a ser função do estado. O preço é
perder a sobreposição — a busca de caminho deixa de correr junto com o tick e
passa a custar dentro dele. Só durante a visita.

### Por que este é diferente dos outros três

| tipo | remédio |
|---|---|
| cosmético consome RNG | restaurar o estado em volta |
| decisão depende da câmera | responder igual para todos |
| interface muda a simulação | resposta determinística, sem cache |
| **simulação roda em thread paralela** | **não deixar atravessar o tick** |

Os três primeiros são o jogo perguntando algo à simulação de um lugar errado.
Este é a simulação **correndo consigo mesma** — nenhuma quantidade de cuidado
com RNG resolveria.

## A ideologia sorteia por consulta

Aborto ao interagir com pawn em combate. Rastreio ligado, primeira divergência
no tick 5816:

```
J1: (nada)
J2: 96x  PreceptComp_UnwillingToDo_Chance.MemberWillingToDo
           < Ideo.MemberWillingToDo < IdeoUtility.DoerWillingToDo
           < PawnUtility.IsTeetotaler < PawnUtility.CanTakeDrug
    10x  ThinkNode_PrioritySorter.TryIssueJobPackage
     4x  JobGiver_Wander / WanderUtility
```

Dois fatos separados, e vale não misturar.

**O amplificador, comprovado.** No jogo:

```csharp
public override bool MemberWillingToDo(HistoryEvent ev)
{
    if (Rand.Value >= chance) return true;
    return base.MemberWillingToDo(ev);
}
```

`Rand.Value` puro — **um sorteio por consulta**, sem semente derivada do pawn ou
do evento. `CanTakeDrug` consulta isso para cada droga considerada, e um pawn
recém-alistado em combate consulta muitas: 96 sorteios num tick. Qualquer
assimetria de um tick entre os dois lados vira uma diferença de ~100 sorteios
imediatamente. Não é a causa — é o que torna a causa impossível de ignorar.

**A causa, ainda não fechada.** O que os números dizem é que a árvore de decisão
do pawn rodou de um lado e não do outro naquele tick — `ThinkNode_PrioritySorter`
e `JobGiver_Wander` também só aparecem em J2. O comando que alistou (tick 5809,
`rng +0` nos dois) foi aplicado igual seis ticks antes.

Por que a árvore rodou num lado só, não sei ainda: a profundidade do rastreio
estava em 6 quadros, e a cadeia da ideologia gasta 5 sozinha — **quem chama
ficou de fora justamente na linha que interessa**. A chave do rastreio virou um
hash FNV-1a dos ponteiros de método, então profundidade saiu de graça e subiu
para 12. A próxima queda mostra o chamador.

## A detecção estava 187 ticks atrasada

Não é um bug de simulação, é do detector — e estava escondendo os outros.

```
primeira divergência real:  tick 6989
primeiro tick suspeito:     tick 7176     (187 ticks depois)
ticks suspeitos:            7176, 7196, 7216     (espaçados de 20, não de 8)
```

A digital era tirada dentro do `Atualizar`, que roda **uma vez por quadro**. Mas
o jogo avança **vários ticks por quadro** — em velocidade alta, mais de oito.
Então o lado pulava por cima dos múltiplos de `TicksEntreRelatos` sem nunca parar
num, e a comparação só acontecia quando o acaso fazia o quadro terminar num tick
alinhado. Daí o espaçamento de 20.

O custo disso não é só demorar a abortar: o rastreio guarda 400 ticks, e a
janela do despejo ia de `aborto − 40` até `aborto + 8`. Com a divergência
nascendo 187 ticks antes da suspeita, **o despejo mostrava só o rastro, nunca a
origem** — foi exatamente o que aconteceu ao empilhar movimentos.

Duas correções:

**A amostra passa a ser tirada no fim do tick**, de dentro do próprio tick
(`SessaoCliente.TickCompleto`), que é o único lugar onde todo tick existe. O
quadro do jogo não serve para isso. Assim os dois lados amostram exatamente os
mesmos ticks, independentemente de quadro, velocidade ou taxa de quadros.

**O despejo passa a mostrar o anel inteiro** (400 ticks), em pedaços, em vez de
uma janela de 48 ticks em volta do aborto.

## O limite do rastreio de RNG

Com a detecção consertada, o despejo finalmente alcançou a origem:

```
detecção: ticks suspeitos 448, 456, 464   (divergência no 423 — 25 ticks, espaçados de 8)

tick 422   J1  3x  Pawn_FilthTracker.Notify_EnteredNewCell
                     < Pawn_PathFollower.TryEnterNextPathCell < PatherTick < Pawn.Tick
           J2  0x
```

De novo o movimento, e **o remendo do pathfinding já estava instalado** (16
pontos de acoplamento verificados no boot). Ou seja: completar os jobs dentro do
tick não resolveu isto. Pode ter resolvido a corrida que o Multiplayer descreve e
ainda assim não ser a causa daqui — são coisas separadas, e eu tratei como se
fossem a mesma.

E aqui o instrumento acaba. O rastreio de RNG só vê quem consome sorteio; o
avanço de um pawn pela célula não consome nada:

```csharp
nextCellCostLeft -= CostToPayThisTick();
if (nextCellCostLeft <= 0) TryEnterNextPathCell();
```

É subtração de float. O que o rastreio mostra é sempre a **consequência** —
`Notify_EnteredNewCell` gastando 3 sorteios um tick antes de um lado — e nunca o
campo que saiu de sincronia.

`client/Session/RastreioDePawns.cs` grava, nos mesmos ticks amostrados pela
digital, o estado que importa para movimento e trabalho: posição, `Moving`,
`nextCellCostLeft`/`nextCellCostTotal`, destino, job atual, tamanho da fila de
jobs e draft. No aborto os dois lados despejam o mesmo bloco, ordenado por id de
pawn, e a primeira linha diferente diz **qual pawn** e **qual campo**.

## O teclado dentro da simulação

O rastreio de estado dos pawns achou na primeira tentativa, e nomeou o campo:

```
J1  #510 Sappy  pos 126,137  custo 11.355/14.500  dest 124,136  job Goto  fila 4  draft 1
J2  #510 Sappy  pos 126,137  custo 11.355/14.500  dest 127,144  job Goto  fila 0  draft 1
```

Mesma posição, mesmo custo de movimento — e **fila 4 contra fila 0**, com o
destino de J2 pulando para cada clique novo. Um lado empilhava as ordens, o
outro substituía.

A causa está em `Pawn_JobTracker.TryTakeOrderedJob`:

```csharp
public bool TryTakeOrderedJob(Job job, JobTag? tag = JobTag.Misc, bool requestQueueing = false) {
    ...
    bool isDownEvent = KeyBindingDefOf.QueueOrder.IsDownEvent;   // o teclado DESTA máquina
    isDownEvent = isDownEvent || requestQueueing;
    if (isDownEvent) { jobQueue.EnqueueLast(job, tag); return true; }   // empilha
    ClearQueuedJobs();                                                  // ou substitui
```

**Empilhar ou substituir depende de quem está com Shift pressionado, lido no
momento em que o job entra.** Quando o comando é aplicado, quem clicou ainda
está com Shift; o outro jogador não está com nada. Mesmo comando, dois efeitos.

E o `requestQueueing` que nós encaminhávamos era `false` — porque no vanilla
quem decide empilhar não é o parâmetro, é a leitura de dentro. Encaminhamos
fielmente um valor que nunca foi a intenção.

Duas metades da correção:

**A intenção é lida uma vez, onde significa algo.** No momento de propor, na
máquina de quem clicou: `requestQueueing || KeyBindingDefOf.QueueOrder.IsDownEvent`.

**Durante o tick, teclado não existe.** `client/Session/TecladoForaDaSimulacao.cs`
faz as quatro consultas de tecla (`KeyDownEvent`, `IsDownEvent`, `JustPressed`,
`IsDown`) responderem `false` enquanto um tick de sessão roda. Aí
`isDownEvent = false || requestQueueing` — exatamente o que o comando carrega.

### O quinto tipo

| tipo | remédio |
|---|---|
| cosmético consome RNG | restaurar o estado em volta |
| decisão depende da câmera | responder igual para todos |
| interface muda a simulação | resposta determinística, sem cache |
| simulação roda em thread paralela | não deixar atravessar o tick |
| **simulação lê o dispositivo de entrada** | **entrada não existe dentro do tick** |

Vale a regra geral: teclado e mouse são de **um** jogador; a simulação é dos
dois. Qualquer leitura de entrada dentro do tick é divergência esperando
acontecer, e o rastreio de RNG **nunca** a encontraria — empilhar um job não
sorteia nada. Foi preciso um instrumento que olhasse estado, não sorteio.

## Divergência sem divergência de RNG

O teclado saiu da simulação e a sessão durou mais — mas abortou. Desta vez os
dois instrumentos discordam, e é essa discordância que interessa:

```
rastreio de pawns:  primeira amostra diferente no tick 4056
histórico de RNG:   primeira divergência no tick 4182
```

**126 ticks em que a simulação já estava diferente e a contagem de sorteios
ainda batia.** O que diferia:

```
J1  #1009 Val  pos 137,116  custo 10.892/20.446  dest 145,96    job GotoWander
J2  #1009 Val  pos 137,116  custo 10.892/20.446  dest 137,119   job GotoWander
```

Mesma posição, mesmo custo, **destino diferente** — com o mesmo estado de RNG.
Mesmo estado e mesma quantidade de sorteios produzem os mesmos números. Então os
números foram iguais e a **escolha** foi diferente: o que mudou não foi o
sorteio, foi aquilo sobre o que se sorteou.

Isso aponta para uma família diferente das cinco anteriores — ordem de coleção,
conjunto de candidatos, cache consultado. Um `TryRandomElement` sorteia o mesmo
índice dos dois lados e devolve elementos diferentes se a coleção não estiver na
mesma ordem.

**Ainda é hipótese.** Para confirmar falta responder uma pergunta que o
instrumento não respondia: no tick exato da divergência, os dois lados sortearam
a mesma quantidade? Com amostra de pawn a cada 8 ticks a resposta era
"divergiu entre 4049 e 4056", e causa e efeito cabiam na mesma amostra.

Agora o estado de pawn é amostrado em **todo** tick (anel de 400), e só de quem
está no mapa — pawns guardados em contêiner apareciam em `-1000,-1000` com tudo
zerado, enchendo o despejo. Cruzando o tick exato com o rastreio de RNG:

- **mesma contagem, resultado diferente** → entrada diferente (ordem, candidatos)
- **contagem diferente** → caminho de código diferente

São causas distintas, com remédios distintos, e até agora eu não conseguia
separá-las.

## Cada um comanda os próprios pawns

A regra central do mod, e a que dá sentido à palavra "visita": **o visitante
comanda os pawns que ele trouxe, e só eles; o anfitrião comanda os da casa, e só
eles.** Você trouxe gente para ajudar (§5) — não assumiu o controle da colônia
alheia.

Isso é diferente da autoridade por tipo de comando
(`docs/COMANDOS-DE-SESSAO.md`), que separa "decisão da colônia" de "ordem a
pawn". São dois cortes que se somam:

| | quem pode |
|---|---|
| decisão da colônia (incidente, missão) | só o anfitrião |
| ordem a um pawn | só o dono **daquele** pawn |

### Onde a posse mora, e por que ali

Num dicionário do `SincronizacaoComponent`, que é `GameComponent` e tem
`ExposeData`.

Não é detalhe de arrumação. A visita transfere a partida inteira do anfitrião
(ADR 0010), então **tudo que está no save chega ao outro lado sozinho, com os
mesmos ids**. A posse viaja de carona: sem mensagem nova, sem passo de
sincronização, e sem chance de os dois lados discordarem sobre de quem é quem.

O padrão é o anfitrião: pawn sem registro é da casa, porque já estava lá antes
de alguém chegar. Só quem entra pela mala do visitante ganha dono explícito.

### A recusa é visível

Clicar num pawn que não é seu dá mensagem, não silêncio:

> `With Friends: Dappler não é seu. Numa visita, cada um comanda os próprios pawns.`

Silêncio aqui seria pior que a recusa — o jogador não saberia se o mod travou ou
se a ordem não era dele para dar.

### Ainda manual

O registro acontece hoje na debug action que abre a mala, que é como o fluxo da
"ida" está montado. Quando ele virar automático, o ponto de registro é o mesmo:
quem desempacota sabe de quem são os pawns.

## Só o que vem da interface é ordem do jogador

Sintoma: `"With Friends: Jonas não é seu"` na tela **o tempo todo**, sem ninguém
clicar em nada.

A intercepção do alistar rodava em toda escrita de `Drafted` — e o jogo escreve
ali por conta própria, dentro do tick: pawn derrubado, job encerrado, mecânico
desativado. Cada uma dessas era tratada como clique do jogador, barrada pela
posse e anunciada.

Pior que o aviso: **a lógica do jogo estava sendo bloqueada.** Um pawn que
deveria desalistar sozinho ao cair não conseguia.

O mesmo valia para `TryTakeOrderedJob`, que o próprio `Pawn_JobTracker` chama ao
puxar o próximo da fila:

```csharp
pawn.jobs.TryTakeOrderedJob(queuedJob.job, queuedJob.tag, requestQueueing: true)
```

Aquilo é a simulação andando, não o jogador mandando.

As duas intercepções agora exigem `NaInterface.Agora`. É o mesmo conceito de
sempre, usado numa terceira função: antes servia para impedir a interface de
mexer na simulação, depois para deixar a interface mentir para si mesma, e agora
para **distinguir quem deu a ordem**.

E o aviso passou a ter intervalo mínimo de 3 s por pawn. Repetir a mesma frase
muitas vezes por segundo não informa mais, só atrapalha.

### Pendência que isto deixa à mostra

Com o fluxo da "ida" ainda manual, o visitante não tem pawn registrado — e pawn
sem registro é do anfitrião. Na prática o visitante não comanda ninguém até a
mala ser aberta pela debug action.

A regra está certa; o que falta é o gatilho automático. Ver a seção da mala.

## A sexta família: cache

Depois de nomear cinco fontes de divergência, faltava a maior do Multiplayer —
e ela não é RNG.

**Cache é computado quando alguém olha.** E quem olha é a interface, em momentos
diferentes em cada máquina: um jogador abre a aba social e o outro não, um passa
o mouse num pawn e o outro não. O cálculo grava resultado e carimbo de tempo, e a
simulação passa a ler valores diferentes nos dois lados sem ninguém ter feito
nada errado.

O remédio é sempre o mesmo, e é o guard de sempre: **dentro da interface, não
recalcule.** Quem recalcula é o tick, que acontece igual nos dois lados.

### Dois casos aplicados

**`SituationalThoughtHandler.CheckRecalculateSocialThoughts`** — recalcular
sorteia (a ideologia tira um número por consulta) e carimba
`lastRecalculationTick`. Este apareceu no **nosso próprio rastreio**, na
divergência de 96 sorteios, e eu não reparei:

```
4x Rand.Value < PreceptComp_UnwillingToDo_Chance.MemberWillingToDo
   < … < ThoughtWorker_Drunk.CurrentSocialStateInternal
   < SituationalThoughtHandler.CheckRecalculateSocialThoughts
```

**`Zone.Cells`** — o getter embaralha na primeira leitura e **fica assim**:

```csharp
if (!cellsShuffled) { cells.Shuffle(); cellsShuffled = true; }
```

Se a interface de um jogador ler a zona antes do tick — para desenhar, para um
tooltip — ela embaralha ali, e a ordem daquele lado é outra para sempre.

Isto é exatamente a assinatura que medimos dias atrás e não soubemos explicar:
**mesma contagem de sorteios, resultado diferente**. Não era o sorteio que
divergia; era a lista sobre a qual se sorteia.

### O catálogo do Multiplayer como lista de trabalho

`Patches/Determinism.cs` tem 37 classes, e a maioria é desta família:
`StatWorker.GetValue`, `PawnCapacitiesHandler`, `DangerWatcher.DangerRating`,
`WealthWatcher.ForceRecount`, `Pawn_AbilityTracker.AllAbilitiesForReading`,
`Caravan.ImmobilizedByMass`, `Plan.cellsShuffled`.

Sete anos de comunidade produziram essa lista, e ela é derivável — o guard é o
mesmo em todas. É o caminho mais barato que existe para reduzir o nosso
desconhecido, e não depende de reproduzir cada divergência em jogo.

### Portadas até agora

| guarda | o que não acontece mais na interface |
|---|---|
| `SituationalThoughtHandler.CheckRecalculateSocialThoughts` | recalcular pensamento social (sorteia e carimba tick) |
| `SituationalThoughtHandler.UpdateAllMoodThoughts` | recalcular humor |
| `Zone.Cells` | embaralhar as células da zona, que é permanente |
| `WealthWatcher.ForceRecount` | recontar riqueza |
| `DangerWatcher.DangerRating` | recalcular nível de perigo |
| `Pawn_AbilityTracker.AllAbilitiesForReading` | reconstruir a lista de habilidades |
| `StoryWatcher_PopAdaptation.Notify_PawnEvent` | mover o estado do narrador |
| `AutoSlaughterManager.Notify_ConfigChanged` | sujar a configuração de abate |
| `WorldObjectSelectionUtility.VisibleToCameraNow` | responder pergunta de câmera dentro da simulação |

A técnica é sempre a dele: **prefixo cancela o recálculo, postfixo devolve o
campo em cache**. Sem o postfixo, o método cancelado devolveria o valor default,
que é pior que recalcular.

### Cada guarda se reporta

Guarda que nunca dispara e guarda cujo alvo sumiu numa atualização têm
exatamente a mesma cara — nenhuma. Então cada uma conta os próprios disparos:
uma linha no log na primeira vez, e a debug action **"Guardas de determinismo"**
imprime a tabela:

```
[WithFriends] guardas de determinismo (4 ativa(s)):
       1.204x  DangerWatcher.DangerRating
         318x  WealthWatcher.ForceRecount
          47x  SituationalThoughtHandler.CheckRecalculateSocialThoughts
           2x  Zone.Cells (embaralhar)
```

Guarda ausente da lista depois de uma visita inteira é suspeita: ou o caminho não
foi exercitado, ou o remendo não está pegando.

### O que falta da lista dele

`StatWorker.GetValue` e `PawnCapacitiesHandler.GetLevel` têm cache com estado
próprio (o Multiplayer inventa um `CacheStatus.CachedInInterface` para
distinguir o que foi calculado na interface e recalcular depois). São os dois
mais elaborados, e ficam para uma próxima rodada — junto com `PawnTweener`,
`Pawn.ProcessPostTickVisuals` e `Pawn_RecordsTracker.ExposeData`.
