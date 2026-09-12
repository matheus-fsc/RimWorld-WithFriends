# Auditoria de fontes locais

Implementa a ADR 0015. Inverte a pergunta do determinismo.

| até agora | agora |
|---|---|
| "qual método divergiu?" | "quais métodos **podem** divergir?" |
| só se responde reproduzindo a falha | se responde sem jogar |
| a lista apodrece a cada atualização | a regra fica, o relatório se refaz |

## Como usar

Debug action **"Auditar fontes locais (IL)"** — no menu principal ou em jogo.
Escreve `WithFriends-auditoria.txt` na pasta de dados do jogo.

Fora da subida de propósito: são dezenas de milhares de métodos.

## As duas metades

**A regra** (`client/Auditoria/FontesLocais.cs`) é C# puro e testado. Lista as
fontes locais — o que responde diferente em cada máquina:

| fonte | por que é local |
|---|---|
| `Verse.CameraDriver` | o que este jogador olha |
| `Verse.KeyBindingDef` | o teclado deste jogador |
| `UnityEngine.Input` | mouse e teclado, por baixo |
| `UnityEngine.Random` | gerador fora do `Verse.Rand` |
| `System.DateTime.Now` | o relógio desta máquina |
| `UnityEngine.Time` | tempo real e taxa de quadros |
| `Verse.Find.CurrentMap/Selector/Targeter/…` | o foco deste jogador |
| `Verse.Prefs`, `DebugSettings`, `DebugViewSettings` | as opções deste jogador |
| `UnityEngine.Screen`, `Event.current` | a tela e o evento desta máquina |

Note o que **não** está: `Find.TickManager`, `Find.World`. São estado
compartilhado, e pôr um deles na lista transformaria o relatório em ruído. Há
teste travando isso.

**O varredor** (`client/Auditoria/AuditorDeIL.cs`) lê o IL de cada método de
`Assembly-CSharp` com `HarmonyLib.PatchProcessor.ReadMethodBody` — sem
Mono.Cecil — e anota quem chama uma fonte.

## Lendo o relatório

Agrupado por fonte, cada uma marcada como já neutralizada ou não, e dentro de
cada uma os chamadores separados por uma heurística de nome:

```
==============================================================================
FONTE  Verse.KeyBindingDef.{KeyDownEvent,IsDownEvent,JustPressed,IsDown}
       o teclado DESTE jogador; foi o que fez empilhar ordem virar substituir
       JÁ NEUTRALIZADA durante o tick de sessão
       3 em código que não parece interface, 47 em código que parece
```

A heurística (`Window`, `Dialog_`, `OnGUI`, `Draw`, `Gizmo`…) **não decide
nada** — interface pode ler câmera e teclado, é o trabalho dela. Ela só diz por
onde começar a ler: as linhas que *não* parecem interface são as suspeitas.

## O que ela não alcança

Continua não cobrindo, e é bom não confundir:

- **ordem de coleção** — a fonte é legítima, o que difere é a ordem
- **corrida de thread** — não há fonte a neutralizar, é a estrutura do jogo
- **cache** — `TryGetMeleeVerb` lê algo legítimo e **guarda** o resultado; é o
  guardar que diverge
- **comandos** — ação de jogador é escopo, não determinismo

## Por que sobrevive a atualização

O que envelhece é lista de chamadores: nome muda, assinatura muda, método some.
O relatório é gerado, não mantido — numa versão nova roda-se de novo e ele se
refaz. O que precisa continuar verdadeiro é só a lista de fontes, que muda de
década em década, não de versão em versão.

## Primeira execução: o que ela achou

`tools/Auditor` — versão offline, lê o DLL com Mono.Cecil, não precisa do jogo
aberto:

```
dotnet run --project tools/Auditor -- <Managed/Assembly-CSharp.dll> auditoria.txt
```

**89.784 métodos com corpo, 4.823 toques em fonte local, 1,3 segundos.**

| fonte | simulação | indefinido | interface | |
|---|---:|---:|---:|---|
| `Verse.Find` | 273 | 1115 | 986 | |
| `Verse.DebugViewSettings` | 50 | 235 | 98 | |
| `Verse.CameraDriver` | 32 | 216 | 58 | neutralizada |
| `Verse.Prefs` | 29 | 342 | 238 | |
| `UnityEngine.Time` | 20 | 146 | 102 | |
| `UnityEngine.Event` | 16 | 158 | 467 | |
| `Verse.KeyBindingDef` | 9 | 34 | 57 | neutralizada |
| `UnityEngine.Input` | 0 | 26 | 37 | |
| `UnityEngine.Screen` | 0 | 50 | 17 | |
| `System.DateTime` | 0 | 9 | 0 | |
| `UnityEngine.Random` | 0 | 0 | 3 | |

### Um bug na própria regra, achado na primeira rodada

`KeyBindingDef` não apareceu. Zero chamadores — para uma fonte cujo chamador nós
tínhamos achado **na mão** dois dias antes.

Motivo: no IL, ler propriedade é chamar `get_Nome`. A regra listava
`IsDownEvent`; o IL traz `get_IsDownEvent`. A fonte não casava com nada, e o
relatório dizia silêncio onde havia 100 chamadores.

É o pior modo de falha de uma ferramenta de auditoria — **silêncio parecendo boa
notícia**. Agora os dois lados são normalizados, e há teste para isso.

### A tese da ADR 0015, confirmada

Remendar a fonte `KeyBindingDef` cobriu, além do `TryTakeOrderedJob` que
achamos apanhando:

```
RimWorld.WorkGiver_ConstructDeliverResources.CanUseCarriedResource   →  IsDownEvent
RimWorld.JobGiver_GetEnergy_Charger.<GetClosestCharger>b__0          →  IsDownEvent
RimWorld.Building_CryptosleepCasket.FindCryptosleepCasketFor         →  IsDownEvent
RimWorld.Building_OutfitStand.<GetFloatMenuOptionForForceWear>b__0   →  IsDownEvent
```

Um `WorkGiver` e um `JobGiver` lendo o Shift do jogador dentro da árvore de
decisão. Quatro divergências futuras que **nunca teríamos procurado**, cobertas
por um remendo que já estava no lugar.

### O primeiro achado de verdade: `UnityEngine.Time`

Fonte ainda não neutralizada, com chamadores no caminho do tick:

```
RimWorld.CompSkyfallerRandomizeDirection.CompTick          →  Time.deltaTime
RimWorld.GameConditionManager/MapBrightnessTracker.Tick    →  Time.deltaTime
RimWorld.CompBiosculpterPod.CanReachRequiredIngredients    →  Time.realtimeSinceStartup
RimWorld.Building_Bed.SetBedOwnerTypeByInterface           →  Time.frameCount
RimWorld.CompAbilityEffect_Chunkskip.FindClosestChunks     →  Time.frameCount
```

`CompTick` lendo `deltaTime` é simulação dependendo da **taxa de quadros da
máquina**. Os outros usam `frameCount` e `realtimeSinceStartup` como chave de
cache — e nenhum dos dois significa a mesma coisa em duas máquinas.

### E um limite que a auditoria expôs

`UnityEngine.Time` é `extern`:

```csharp
public static extern float deltaTime
```

Método nativo não tem corpo gerenciado, então **o Harmony não remenda a fonte**.
Para esta, é preciso voltar a remendar chamadores — com transpiler, trocando a
chamada por uma constante dentro de cada um.

O que muda é que agora a lista é de cinco, conhecida e regenerável, em vez de
desconhecida. A ADR 0015 vale onde a fonte é gerenciada; onde não é, a auditoria
pelo menos entrega a lista fechada.

## Cruzando com o Multiplayer

O Zetrith sustenta 602 remendos num mod que funciona. Cada alvo daquela lista é
conhecimento pago com bug de alguém: se ele remenda, houve motivo.

`tools/Auditor` recebe o fonte dele como terceiro argumento, extrai os alvos por
texto e marca os nossos achados:

```
dotnet run --project tools/Auditor -- <Assembly-CSharp.dll> auditoria.txt referencia/repos/Multiplayer/Source
```

**878 alvos extraídos. 37 dos nossos 429 achados de simulação também são
remendados por ele.**

| fonte | simulação | MP também remenda |
|---|---:|---:|
| `Verse.Find` | 273 | 158 |
| `Verse.DebugViewSettings` | 50 | 9 |
| `Verse.CameraDriver` | 32 | 11 |
| `Verse.Prefs` | 29 | 27 |
| `UnityEngine.Time` | 20 | 26 |
| `UnityEngine.Event` | 16 | 50 |
| `Verse.KeyBindingDef` | 9 | 10 |
| `UnityEngine.Input` | 0 | 0 |
| `System.DateTime` | 0 | 0 |
| `UnityEngine.Random` | 0 | 0 |

A leitura tem três lados:

**Confirmação.** `GenTicks.GetCameraUpdateRate` e
`Pawn_JobTracker.TryTakeOrderedJob` aparecem marcados `[MP]`. Foram os dois que
achamos apanhando, cada um custando várias rodadas — ele já os tinha.

**Prioridade.** `UndercaveMapComponent.MapComponentTick → Find.CurrentMap` é um
`MapComponentTick` lendo o mapa que **este** jogador abriu, e ele remenda. Vale
mais que os outros 272 achados de `Verse.Find` justamente por isso.

**Pergunta contrária.** Onde ele **não** remenda, cabe perguntar por quê. Pode
não importar; pode o desenho dele não passar por ali; pode ainda não ter mordido
ninguém. `DateTime` e `UnityEngine.Random` com zero dos dois lados é o caso
tranquilo — dois independentes chegando ao mesmo "não é problema".

## O narrador, e uma coisa que o nosso desenho ganha de graça

Observação de jogo: no Multiplayer o narrador parece mandar menos eventos para um
dos jogadores. É por desenho, e o fonte diz como.

`Factions/FactionRepeater.cs` substitui `Storyteller.StorytellerTick` por um laço
que roda **um storyteller por facção de jogador** — cada um com seu
`StoryWatcher`, que é quem acompanha riqueza e progresso:

```csharp
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.StorytellerTick))]
static bool Prefix() =>
    FactionRepeater.Template(..., d => d.storyteller.StorytellerTick(), ...);
```

E `AllIncidentTargets` é restrito ao mapa em contexto, para um incidente gerado
tickando o mapa X não cair no mapa Y.

Ou seja: cada jogador tem narrador próprio, e o ritmo depende da riqueza e do
histórico **daquela** colônia. Duas colônias diferentes recebem eventos em ritmos
diferentes — não é bug, é a consequência de cada um ter o seu.

**Aqui isso sai de graça.** Fora da visita, cada jogador tem a própria partida e
o próprio narrador, sem nada a sincronizar. Durante a visita os dois simulam a
partida do anfitrião, então roda **um** narrador — o dele — determinístico dos
dois lados, e os incidentes caem na colônia dele, que é onde o encontro está
acontecendo.

Todo o `FactionRepeater` é subsistema que não precisamos ter. É o mesmo corte da
`docs/PROGRESSO.md`: o escopo da visita paga por si.

## Primeiro trabalho saído da auditoria: `UnityEngine.Time`

Cinco chamadores de simulação, tratados em
`client/Session/TempoDeterministico.cs`. Nenhum deles foi achado reproduzindo
divergência — saíram da varredura.

| chamador | o que lia | por que importa |
|---|---|---|
| `CompSkyfallerRandomizeDirection.CompTick` | `deltaTime` | `currentOffset += … * deltaTime`: a posição acumula por taxa de quadros |
| `GameConditionManager.MapBrightnessTracker.Tick` | `deltaTime` | `lerp += …`, e `lerp` está no `ExposeData` — **estado salvo** acumulado por taxa de quadros |
| `CompBiosculpterPod.CanReachRequiredIngredients` | `realtimeSinceStartup` | cache válido por 2 segundos de relógio: um lado acerta, o outro recalcula |
| `CompAbilityEffect_Chunkskip.FindClosestChunks` | `frameCount` | cache com chave no número do quadro |
| `Building_Bed.SetBedOwnerTypeByInterface` | `frameCount` | trava contra dois cliques no mesmo quadro |

Substituições, dentro do tick de sessão:

| do jogo | nosso |
|---|---|
| `Time.deltaTime` | `1f / 60f` — um tick |
| `Time.realtimeSinceStartup` | `TicksGame / 60f` |
| `Time.frameCount` | `TicksGame` |

Por transpiler, porque a fonte é `extern` e o Harmony não a alcança.

### "Tratada" não é uma coisa só

Marcar `UnityEngine.Time` como neutralizada teria sido quase-mentira: a fonte não
foi tratada, cinco chamadores foram. Se uma versão nova do RimWorld trouxer um
sexto, o relatório diria "resolvido" — o mesmo silêncio-parecendo-boa-notícia que
o bug do `get_` já nos custou uma vez.

Então o estado de cada fonte tem três valores, e o relatório diz qual:

| marca | significa |
|---|---|
| `[fonte]` | a fonte responde neutro no tick — **cobre todo chamador, inclusive os futuros** |
| `[chama]` | a fonte é nativa; chamadores conhecidos tratados — **reveja a lista a cada versão** |
| `[     ]` | ninguém mexeu |

Há teste travando a distinção.

## Segundo trabalho: o mapa atual

`Verse.Find.CurrentMap` aparece em 39 métodos de simulação. No caminho do tick:

```
RimWorld.Building_VoidMonolith.Tick
RimWorld.PitGate.Tick
RimWorld.CompCameraShaker.CompTick
RimWorld.JobGiver_AITrashColonyClose.TryGiveJob      ← decisão de IA
RimWorld.IncidentWorker_AnimalInsanityMass.TryExecuteWorker
RimWorld.CompAnimalInsanityPulser.DoAnimalInsanityPulse
RimWorld.JobDriver_InstallImplant.Install
```

Um `JobGiver` decidindo pelo mapa que **este** jogador está olhando é divergência
esperando um dos dois abrir a vista do mundo.

### Uma camada abaixo do que a auditoria apontou

A auditoria acusou `Find.get_CurrentMap`, mas `Find` só delega:

```csharp
public static Map CurrentMap => Current.Game?.CurrentMap;
```

Remendar a delegação deixaria passar quem chama `Current.Game.CurrentMap`
direto. O remendo vai em `Game.CurrentMap`, que pega os dois caminhos.

Vale como regra geral de leitura do relatório: **ele aponta onde a chamada
aparece, não onde a verdade mora.** Antes de remendar, seguir a delegação até o
fim — uma camada a mais costuma cobrir chamadores que a varredura nem listou,
porque eles usam outro caminho.

Dentro do tick de sessão, o mapa atual passa a ser o mapa da visita. Fora do
tick nada muda: a interface continua vendo o que o jogador abriu, inclusive a
vista do mundo.

## Placar das fontes

| fonte | simulação | estado |
|---|---:|---|
| `Verse.Game.CurrentMap` | (39 via `Find`) | fonte |
| `Verse.CameraDriver` | 32 | fonte |
| `Verse.KeyBindingDef` | 9 | fonte |
| `UnityEngine.Time` | 20 | chamadores (fonte é nativa) |
| `Verse.Find` (WindowStack, Selector, Targeter, …) | 234 | aberta |
| `Verse.DebugViewSettings` | 50 | aberta |
| `Verse.Prefs` | 29 | aberta |
| `UnityEngine.Event` | 16 | aberta |
| `Input`, `Screen`, `DateTime`, `Random` | 0 | nada a fazer |

## Terceiro trabalho: o embrulho que a regra não via

O rastreio de um desync apontou 96 sorteios de um lado só em
`JobGiver_MoveDrugsToInventory.GetPriority` — árvore de decisão rodando num lado
e não no outro. Procurando o que poderia deslocá-la, apareceu isto em
`Pawn_JobTracker.StartJob`:

```csharp
if (addToJobsThisTick && !fromQueue &&
    (!Find.TickManager.Paused || lastJobGivenAtFrame == RealTime.frameCount))
{
    jobsGivenThisTick++;
    …
}
lastJobGivenAtFrame = RealTime.frameCount;
if (jobsGivenThisTick > 10) { … StartErrorRecoverJob … }
```

`Verse.RealTime` — que a nossa regra **não vigiava**. Ela olhava
`UnityEngine.Time`, e o jogo mantém um cache com outro nome:

```csharp
public static void Update() {
    frameCount = Time.frameCount;
    deltaTime  = Time.deltaTime;
    …
}
```

Mesmo relógio, mesmo contador de quadros, 57 leituras invisíveis para a
auditoria. É a segunda vez que a **regra**, não o varredor, foi o buraco — a
primeira foi `get_IsDownEvent`.

### Mas aqui deu para tratar a fonte

`RealTime` expõe **campos estáticos públicos**, não propriedades nativas. Dá para
escrever neles. Em vez de remendar leitores, os valores são trocados em volta do
tick e devolvidos depois — o mesmo padrão do estado do RNG (ADR 0009):

| campo | durante o tick |
|---|---|
| `frameCount` | `TicksGame` |
| `deltaTime`, `realDeltaTime` | `1/60` |
| `lastRealTime`, `unpausedTime` | `TicksGame / 60` |

Um lugar, e os 57 leitores ficam determinísticos — inclusive os que uma versão
futura trouxer. Marcado `[fonte]`, não `[chama]`, e a diferença é real.

Fora do tick nada muda: motes, animação e interface continuam vendo o tempo da
máquina, que é o que precisam ver.

### A lição que se repete

O varredor está certo; quem erra é a lista de fontes. Duas vezes seguidas o
buraco foi de cobertura, não de mecanismo:

| buraco | efeito |
|---|---|
| `IsDownEvent` escrito sem `get_` | fonte com 100 chamadores reportando **zero** |
| `UnityEngine.Time` sem o embrulho `Verse.RealTime` | 57 leituras invisíveis |

Vale o hábito: ao catalogar uma fonte, procurar quem a **embrulha**. O jogo
cacheia bastante coisa, e o cache tem outro nome.

## Mods também entram na varredura

A auditoria in-game passou a varrer **todos os assemblies de mods carregados**,
não só o `Assembly-CSharp`, e prefixa cada achado com o nome do mod:

```
[Some Mod] SomeMod.Comp_Coisa.CompTick   →  Rand.Value
```

O motivo é direto: um mod que sorteia dentro do tick, ou que lê a câmera para
decidir algo, quebra a visita exatamente como o jogo quebraria — e ninguém lê o
código de todos os mods que usa.

Isso não impede o problema; faz dele uma linha no relatório, com remetente, em
vez de um desync sem explicação. Ver `docs/adr/0018-custo-de-atualizacao-e-mods.md`.


## Alcançabilidade: o que uma visita realmente toca

A separação por balde ("parece simulação", "parece interface") é heurística por
nome. Serve para ordenar a leitura; não serve para **cortar** a lista.

O auditor agora responde a pergunta pelo IL: partindo do tick e das portas por
onde um comando reentra na simulação, quais métodos o jogo consegue chamar?

```
alcance: 45.426 de 88.637 métodos alcançáveis
fila real: 1.085 métodos tocam fonte local **e** rodam numa visita (de 2.417)
Multiplayer × visita: 450 dos 878 alvos dele rodam dentro de uma visita
```

Metade do jogo não acontece numa visita: caravana, comércio, pesquisa, mapa do
mundo (ADR 0009). Cada linha dessas na fila era leitura que não virava nada.

### Erra para mais, nunca para menos

Toda dúvida vira "alcançável": chamada virtual alcança todas as sobrescritas,
chamada de interface alcança todas as implementações, sobrecarga alcança todas as
homônimas, e nome curto colide de propósito no cruzamento com o Multiplayer.

A assimetria é deliberada. `[visita]` a mais custa leitura; `[visita]` a menos
descartaria justamente o caminho que diverge.

### O que escapa

Delegate, reflexão e ponteiro de função não aparecem no IL como chamada a um alvo
nomeado, e o RimWorld usa os três — think nodes, work givers, `Action` de gizmo.
Por isso os pontos de entrada incluem mais que o tick: `ThinkNode_Priority`,
`WorkGiver_Scanner`, `JobDriver.MakeNewToils` e `IncidentWorker.TryExecute` estão
lá porque o jogo chega neles por tabela de defs. Sem citá-los, metade do
comportamento de pawn ficaria de fora por um detalhe de despacho, não por não
acontecer numa visita.

O preço aparece: dos 450 alvos herdados, uns 80 ainda citam caravana ou mundo,
puxados por `IncidentWorker.TryExecute` — que **é** alcançável, porque o anfitrião
pode provocar um incidente durante a visita.

### `auditoria.fila-mp.txt`

O arquivo com os 450. É a lista de sete anos de bug real do Multiplayer, cortada
pelo nosso escopo — e se recalcula sozinha a cada versão do jogo.


## A interseção: onde as duas evidências concordam

Cruzando `[visita]` com `[MP]` — o que a simulação de uma visita alcança **e** o
Multiplayer já remenda — sobram 55 linhas. A maioria é ruído da
sobre-aproximação (diálogos e janelas puxados por `IncidentWorker.TryExecute` →
carta → `WindowStack`). O que sobrou de real tinha todo a mesma forma:

**Um valor que cada jogador escolhe no menu de opções, lido de dentro do tick.**

| preferência | onde entra | o que quebra |
|---|---|---|
| `AutomaticPauseMode` | `LetterStack.ReceiveLetter` | um lado pausa na carta, o outro não |
| `PauseOnLoad` | carregamento | com a ressincronização os dois recarregam: um volta parado |
| `AdaptiveTrainingEnabled` | treino de animal | aprendizado diferente dos dois lados |
| `PreferredNames` | `PawnBioAndNameGenerator` | pawn novo com nome diferente — e nome é estado salvo |
| `MaxNumberOfPlayerSettlements` | decisão de incidente | incidente acontece dentro da visita |

Não há sorteio envolvido em nenhuma: **nenhum rastreio de RNG jamais as
mostraria**. É divergência por configuração, e só uma lista cruzada acha.

A saída é a do ADR 0014 em outra escala: em vez de combinar o valor, derivar —
dentro da visita todo mundo usa o padrão do jogo, e não há o que combinar. A
preferência do jogador continua intacta no arquivo dele.

### E uma que não é determinismo, é o mesmo sintoma

`Prefs.RunInBackground`. Sem ela, o jogo para quando a janela perde o foco — e
numa visita isso trava **os dois**, porque a barreira não anda com um lado
congelado. Do lado de dentro é indistinguível de desconexão, e duas instâncias na
mesma máquina (que é como se testa) nunca estão as duas em foco. Forçada durante
a visita e devolvida no fim.


## A primeira divergência achada pelo rastreio, e não por leitura

Vale registrar inteira, porque é o primeiro caso em que o instrumento respondeu
sozinho — e porque a leitura sozinha não teria achado.

Comparando os dois diários de uma visita, o **histórico de RNG da sessão** deu o
tick exato da primeira diferença:

```
tick 6130   A rng 63153   B rng 63152      ← um sorteio de diferença
```

O **rastreio por local de chamada**, no mesmo tick, deu a cadeia:

```
Pawn.TickInterval
  < Pawn_HealthTracker.HealthTickInterval
    < Pawn_HealthTracker.DropBloodSmear
      < FilthMaker.TryMakeFilth
        < GenSpawn.Spawn
          < Filth.SpawnSetup
            < FloatRange.RandomInRange  →  Rand.Range
```

E o jogo decide assim:

```csharp
if (pawn.Crawling && pawn.Spawned)
    if (!lastSmearDropPos.HasValue ||
        Vector3.Distance(pawn.DrawPos, lastSmearDropPos.Value) > …)
        DropBloodSmear();
```

`DrawPos` é `PawnTweener.TweenedPos`: posição **de quadro**, interpolada com
`RealTime.deltaTime`. Duas máquinas desenham em ritmos diferentes; uma janela em
foco e outra não, ainda mais. O pawn que rasteja sangrando larga sangue em
lugares diferentes nos dois lados — e sangue é `Filth`, estado salvo, cujo
sorteio de espessura desloca o gerador da sessão para sempre.

Explica o padrão inteiro: divergia em **combate** (búfalos, mecanoides, insetos,
tiro em construção) e nunca construindo, movendo ou mexendo em estoque. Combate é
o que derruba pawn, e pawn derrubado rasteja e sangra. E explica por que
ressincronizar não resolvia: os dois voltavam iguais e continuavam desenhando em
ritmos diferentes.

### O que isso diz sobre o método

`PawnTweener.TweenedPos` estava na lista de guardas do Multiplayer por portar
**desde o começo**, e apareceu na interseção `[visita] × [MP]` da alcançabilidade.
Ou seja: a lista cruzada já apontava para ele, e mesmo assim eu o deixei para
depois por três rodadas, caçando hipóteses.

A lição não é "leia a lista". É que a lista dá **candidatos** e o rastreio dá
**evidência** — e sem evidência não dá para ordenar candidatos, porque todos
parecem plausíveis. O que faltava não era saber que `TweenedPos` é local; era
saber que era *ele*, naquele tick, por aquela cadeia.
