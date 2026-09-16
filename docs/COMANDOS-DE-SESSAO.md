# Comandos de sessão — o que precisa virar comando

> A regra: **o que não é determinístico a partir do estado compartilhado vira
> comando.** Na prática, isso quer dizer toda ação do jogador.
>
> Este documento existe porque a pergunta "dá para copiar do Multiplayer o que
> vai precisar virar comando?" tem uma resposta boa e uma armadilha.

## A resposta: sim, como mapa — não como transcrição

O Multiplayer registra **217 membros** (`SyncMethods.cs` 185, `SyncDelegates.cs`
32), mais 1.347 linhas de serializadores por tipo. Copiar tudo seria herdar a
superfície de manutenção que a §14.5 avisa ser o gargalo real do projeto — e
que a [componentização](SESSAO-COMPONENTES.md) decidiu explicitamente não
herdar.

Mas a lista deles é um **mapa excelente do território**: diz onde as ações do
jogador entram na simulação, sem precisar descobrir cada uma por desync.

A diferença de escopo é grande e é a favor deles… e nossa:

| | Multiplayer | Aqui |
|---|---|---|
| Escopo | partida inteira, permanentemente compartilhada | janela de visita, dois jogadores |
| Precisa sincronizar | pesquisa, políticas, missões, rituais, caravanas, ideologia, comércio, colônia inteira | o que acontece **numa visita** |
| Tamanho do registro | 217 membros | decisão nossa |

Pesquisa, políticas de roupa, aceitar missão, ritual — nada disso acontece
numa visita de ajuda humanitária. Fora do escopo até prova em contrário.

## O que já é comando

| Comando | Membro do jogo | Como apareceu |
|---|---|---|
| `Velocidade` | `TickManager.CurTimeSpeed` | um lado em Superfast: evento de 123 sorteios com um tick de diferença |
| `Alistar` | `Pawn_DraftController.Drafted` | +4 sorteios no tick do clique, +111 dois ticks depois |
| `OrdemDeTrabalho` | `Pawn_JobTracker.TryTakeOrderedJob` | **todo clique-direito passa aqui** |
| `OrdemPriorizada` | `Pawn_JobTracker.TryTakeOrderedJobPrioritizedWork` | o chamador escreve DEPOIS da ordem (abaixo) |
| `Alternar` | qualquer propriedade `bool` registrada | segurar fogo, proibir |
| `Estoque` | `StorageSettings.Priority` + os 8 mutadores de `ThingFilter` | material fora de estoque não gera trabalho (abaixo) |

O terceiro é o de maior alcance: uma intercepção cobre mover, atacar, carregar,
resgatar, lançar poder — praticamente tudo o que um jogador faz numa visita. O
Multiplayer chega à mesma conclusão e sincroniza o mesmo método.

### Interceptar o que é chamado não cobre o que o chamador faz com a resposta

`TryTakeOrderedJobPrioritizedWork` parecia coberto de graça, porque chama
`TryTakeOrderedJob` por dentro. Não estava:

```csharp
if (TryTakeOrderedJob(job, giver.def.tagToGive)) {
    job.workGiverDef = giver.def;
    if (giver.def.prioritizeSustains)
        pawn.mindState.priorityWork.Set(cell, giver.def);
    return true;
}
```

A nossa intercepção de dentro devolve "aceito" para a interface não mostrar
recusa. O chamador acredita — e escreve essas duas coisas na máquina de quem
clicou. `priorityWork` é estado de simulação. O outro lado não escreve nada.
Divergência silenciosa, sem erro nenhum.

É a segunda vez que essa forma aparece: a primeira foi `TogglePaused` escrevendo
`curTimeSpeed` por baixo da propriedade que remendamos. **Remendar a fonte cobre
os chamadores dela, não o que eles fazem depois.**

### `Alternar`: um tipo, muitas chaves

O Multiplayer registra uma linha por propriedade — `FireAtWill`, `Forbidden`,
`StorageSettings.Priority` e mais umas dezenas. São todas a mesma forma: uma
coisa do mapa, um nome, um valor `bool`. Um tipo de comando por linha dessas
seriam oito trocas de protocolo para oito booleanos.

Então o protocolo carrega `(coisa, chave, valor)` e quem sabe o que a chave quer
dizer é o registro em `AlternaveisDeSessao`. A próxima custa uma entrada e um
remendo no setter — protocolo intacto, teste de ida e volta intacto.

O que **não** cabe nele: botão cuja ação é um closure escrevendo direto no campo
privado, sem propriedade nenhuma no caminho. "Manter aberta" da porta é assim
(`holdOpenInt`, escrito de dentro do `Command_Toggle`). Não há fonte para
remendar — e é por isso que o Multiplayer tem 227 registros de lambda além dos
305 de método: para cada um desses é preciso identificar o closure.

### Estoque: sincronizar o estado, não a operação

Apareceu caçando "o menu de priorizar não aparece". A causa era regra do jogo —
material fora de estoque não gera trabalho, e sem trabalho não há o que
priorizar. Mas isso quer dizer que **a configuração do estoque decide o que a
simulação faz**, e ela estava inteiramente fora da sessão. Um jogador permitir
aço num estoque muda o que os pawns dos dois lados fazem no tick seguinte.

`ThingFilter` tem oito mutadores, vários com parâmetros que não atravessam a rede
de graça: listas de exceção, um filtro-pai inteiro. O Multiplayer sincroniza a
interação com o widget e precisa de marcadores de contexto (`ThingFilterMarkers`,
`ThingFilterContexts`) só para saber de quem é o filtro que está sendo mexido.

Nós fazemos o contrário: deixamos a interface **calcular** o resultado, lemos o
estado, **desfazemos** o efeito local e mandamos o estado inteiro — quais defs
valem agora, quais filtros especiais estão desligados, as duas faixas.

| | operação | estado |
|---|---|---|
| mutadores cobertos | um registro cada | todos, de uma vez |
| widget novo (ou de mod) | precisa de registro | já coberto |
| dois comandos fora de ordem | meio-termo que ninguém pediu | último vence, coerente |
| bytes | poucos | centenas de nomes de def no pior caso |

O pior caso ("permitir tudo") são uns 20 KB. É comando de jogador, não de tick.

**Em aberto:** hoje o visitante pode mexer no estoque do anfitrião, como pode
designar. Coerente com §4 (designar fica com o visitante porque ele precisa erguer
barricada), mas estoque é mais "arrumação da casa" que "defesa" — vale decidir de
propósito, não por omissão.

## Ajustes de pawn (feito)

Vieram do mapa de decisões (`wf decisoes`), família **pawn** — a que uma visita
exercita de verdade. Um tipo de comando, `AjusteDePawn`, com registro de chaves:

| chave | membro do jogo | por que importa |
|---|---|---|
| `prioridade` | `Pawn_WorkSettings.SetPriority` | muda o que o colono faz no tick seguinte |
| `area` | `Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap` | decide onde ele pode ir, e portanto que trabalho pega |
| `mestre` | `Pawn_PlayerSettings.Master` | a quem o animal obedece |
| `seguirAlistado` | `PawnColumnWorker_FollowDrafted.SetValue` | campo público, sem setter — remendo no chamador |
| `seguirTrabalho` | `PawnColumnWorker_FollowFieldwork.SetValue` | idem |

Mesma ideia de `Alternar` um degrau acima: lá é tudo `bool`; aqui as formas
variam — inteiro com um def, referência, booleano. O payload
`(pawn, chave, número, texto)` cobre as cinco.

Os dois últimos são remendo no **chamador**, contra a ADR 0015, porque não há
fonte: `followDrafted` e `followFieldwork` são campos públicos, e campo não tem
setter. O Multiplayer registra os mesmos dois chamadores pelo mesmo motivo.

**Por que estes primeiro.** Não é enfeite: a divergência que custou o dia 14 foi
uma colona escolhendo `Clean` de um lado e `BuildRoof` do outro. Prioridade de
trabalho e restrição de área são exatamente as entradas dessa escolha.

**Falta ainda nesta frente:** zonas de crescimento (`Zone_Growing.PlantDefToGrow`),
apagar zona (`Zone.Delete`), áreas (`Area.Invert`, `Area.Delete`,
`AreaManager.TryMakeNewAllowed`) e a restrição de área do pawn
(`Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap`). Criar e expandir zona já
é comando, porque é designador.

## O buraco maior não é comando novo: é o payload da ordem

`Job.ExposeData` tem ~65 campos. Nós carregamos 9.

O que fica de fora e dói numa visita de combate:

| Campo | O que quebra |
|---|---|
| `verbToUse` | poderes e psicastes: o job chega sem o verbo |
| `ability` | idem |
| `targetQueueA` / `targetQueueB` / `countQueue` | ordens de vários alvos, ingredientes |
| `workGiverDef` | ver `OrdemPriorizada` acima |

O Multiplayer não enumera campo nenhum: `.ExposeParameter(0)` serializa o `Job`
pelo `Scribe` do próprio jogo (`ScribeUtil.WriteExposable`), o mesmo caminho do
save. Todos os campos viajam, inclusive os que a Ludeon acrescentar na próxima
versão e os que um mod acrescentar hoje.

O preço é resolver referências (`Scribe_References.Look(ref verbToUse, …)`) fora
de um carregamento de save: eles mantêm um `SharedCrossRefs` (50 linhas, mais
remendos que o alimentam conforme as coisas nascem e morrem). É o próximo passo
grande desta frente, e vale mais que qualquer comando novo da lista abaixo.

## O que vem depois, por ordem de probabilidade numa visita

Extraído da lista deles, filtrado pelo que uma visita de ajuda ou combate usa:

| Membro do jogo | O que quebra sem ele |
|---|---|
| `Building_TurretGun.OrderAttack` | mandar torre atacar alvo |
| `Pawn_WorkSettings.SetPriority` | mexer nas prioridades de trabalho |
| `Building_Door` "manter aberta" | closure, sem fonte para remendar |
| `PawnColumnWorker_FollowDrafted.SetValue` | animal seguir quando alistado |
| `Designator_*` (via `DesignationConfirmed`) | marcar para caçar, desconstruir, colher |
| `Pawn_TrainingTracker.SetWantedRecursive` | treinar animal |

E três que só importam se a visita durar bastante:

| Membro | Quando |
|---|---|
| `ResearchManager.SetCurrentProject` | se o visitante mexer na pesquisa do anfitrião |
| `StorageSettings.Priority` / `CopyFrom` | se mexer em estoques |
| `Zone.Delete`, `Plan.Delete` | se mexer em zonas |

## Por que descobrir por desync continua valendo

Ter o mapa não substitui a medição. O traço de RNG diz **o tick exato** e o
**tamanho do salto**, e o tamanho conta o que aconteceu:

```
tick 182   dif   -4    ← a ordem em si
tick 184   dif -111    ← as consequências dela na simulação
```

A lista diz onde procurar; o traço diz o que de fato aconteceu na sua partida.
As duas coisas juntas são muito melhores que qualquer uma sozinha — e a lista
evita o pior cenário, que é descobrir o vigésimo membro um desync por vez.

## Sobre copiar código

Reusar a **lista** de membros é reusar conhecimento do domínio, não código.
Se algum dia um trecho do Multiplayer for adaptado de verdade, ele leva o
cabeçalho de atribuição da §16.2 — como já acontece em
`protocol/Determinismo/ModoDeArredondamento.cs`.

## Designadores (feito)

`Designator.DesignateSingleCell` e `Designator.DesignateThing` viram comando
durante a visita — construir, minerar, cortar, demolir, cancelar. Era a outra
metade do que o jogador faz com o mouse; a primeira, a ordem direta, já passava
por `OrdemViraComando`.

O designador não viaja: é objeto de UI, com estado de arrasto e conta-gotas.
Viaja o suficiente para remontar um igual — o tipo, e para construção o que se
constrói, de que material e virado para onde.

### god mode viaja junto

`Designator_Build.DesignateSingleCell` consulta `DebugSettings.godMode`: ligado,
a construção nasce pronta via `GenSpawn.Spawn`; desligado, vira blueprint. Dois
efeitos diferentes no mapa, e o god mode de cada jogador é dele.

Por isso o comando carrega o god mode **de quem clicou**, e quem aplica o troca
durante a execução. Concordar sobre o comando não adianta se os dois lados
discordam sobre o que ele faz. Era o desync medido ao construir com god mode
ligado no anfitrião.

Resolve a pendência que a ADR 0011 tinha registrado como "forçar godMode/devMode
a valores conhecidos durante a execução de comando" — para designadores. Outras
ações que leem `DebugSettings` continuam abertas.

### O arquiteto escapava — remendo na base não alcança a sobrescrita

`Designator.DesignateSingleCell` é **virtual**, e `Designator_Build` sobrescreve.
Remendar a base não alcança a sobrescrita: o Harmony remenda **corpos de método**,
não contratos. Projetar no arquiteto chamava a sobrescrita, que nunca passou pela
intercepção.

O log da sessão dizia isso de graça, e eu não olhei na primeira vez: 25 comandos,
todos `velocidade`, `alistar` e `Goto`. **Nenhum designador.** A intercepção
existia e nunca rodou.

Agora o alvo é a base **e toda subclasse que declara o método**, via
`TargetMethods`, e o boot diz quantas implementações foram remendadas — se uma
atualização mudar a assinatura o número cai para 1 e o sintoma reaparece em
silêncio.

`DesignateMultiCell` não precisa de remendo próprio: a implementação da base
percorre as células chamando `DesignateSingleCell`, e nenhum designador comum a
sobrescreve.

#### Duas armadilhas ao remendar sobrescritas

Remendar as 49 implementações de `DesignateSingleCell` esbarrou em duas coisas
que valem ficar escritas.

**1. O Harmony casa parâmetro de prefixo por nome.** A base declara
`DesignateSingleCell(IntVec3 c)`; `Designator_Deconstruct` declara
`DesignateSingleCell(IntVec3 loc)`. Um prefixo com parâmetro `c` não serve para
a segunda:

```
Patching exception in Designator_Deconstruct::DesignateSingleCell(IntVec3 loc)
Parameter "c" not found
```

A forma posicional (`__0`) não depende do nome e serve para todas.

**2. `PatchAll()` é tudo ou nada.** A exceção acima interrompeu o laço, e as
classes seguintes **nunca foram remendadas** — entre elas o freio de tick da
sessão. O sintoma não foi "construir não funciona": foi **os dois jogos
congelados na barreira**, a três arquivos de distância da causa, com a exceção
real enterrada no boot.

Agora cada classe é remendada por conta própria, com try/catch, e o que falhar é
listado no log sem levar o resto junto — a mesma regra do §14.5 que já valia para
alvo ausente. É o tipo de falha que só aparece uma vez se ficar isolada.

## Velocidade não é comando agendado

Pausar é o único comando que **impede o tick que o aplicaria**.

Com alguém pausado, a barreira volta para o mínimo (§3, consenso de pausa). O
comando de despausar é carimbado para um tick à frente da barreira — e ninguém
chega lá, porque para despausar seria preciso já estar andando. Os dois jogos
congelam, com os comandos pendurados na agenda:

```
comando 1 de 678f5d3b… agendado para o tick 129 (estou em 124)
velocidade Paused proposta como comando
comando 2 de da8fd226… agendado para o tick 130 (estou em 124)
velocidade Normal proposta como comando
comando 3 de da8fd226… agendado para o tick 131 (estou em 124)
```

E não precisa esperar tick nenhum. A velocidade muda **quando** cada lado chega
a um tick, nunca o que acontece dentro dele — quem mantém os dois no mesmo tick
é a barreira. Aplicar na chegada, em ticks possivelmente diferentes, não muda
simulação nenhuma.

Então `TipoDeComando.Velocidade` passa a ser aplicado na chegada, fora da
agenda. Continua sendo comando (os dois lados concordam sobre a velocidade),
mas fora do relógio que ele mesmo controla.

### E o eco

No mesmo log aparecia uma enxurrada de propostas de velocidade alternando
`Paused`/`Normal`. Causa separada: o consenso de pausa mexe na velocidade local,
`PropagarVelocidade` via a mudança e propunha de volta, o outro lado aplicava e
devolvia.

O que a barreira **impõe** não volta como proposta — quem aplica velocidade por
imposição atualiza `ultimaVelocidade` junto.

### Pausa imposta não é pausa pedida

Mesmo com a velocidade aplicada na chegada, uma pausa só ainda travava tudo — e
a causa é um ciclo de um passo:

1. alguém pausa → consenso manda todos pausarem
2. `relogio.Pausar()` põe o relógio local em `Paused`
3. o relato dizia "estou pausado" **lendo o relógio**
4. o servidor via alguém pausado → consenso continua → volta ao passo 2

O relato estava respondendo à própria imposição. Um jogador pausava uma vez e
ninguém andava nunca mais.

A distinção que faltava: **o que o jogador escolheu** (`velocidadeDesejada`)
versus **o que a barreira impôs** (o relógio). Só a primeira vira relato:

```csharp
Pausado = visitaComecou && velocidadeDesejada == TimeSpeed.Paused,
```

`velocidadeDesejada` muda em dois lugares: quando o jogador mexe no botão, e
quando um comando de velocidade é aplicado. O consenso não encosta nela.

E a imposição precisa ser desfeita por quem a impôs: quando o consenso passa e o
relógio ficou em `Paused` sem ninguém ter pedido, ele volta para a velocidade
desejada. Sem isso o jogo ficava parado esperando um clique do jogador — o mesmo
clique que ninguém consegue dar quando nada se move.

## Autoridade: quem pode propor o quê

Regra do §4 levada a sério: **a visita acontece na colônia do anfitrião**, então
as decisões daquele lugar são de quem mora nele.

| tipo de comando | visitante pode? |
|---|---|
| `Velocidade` | sim — o tempo é negociado (§3) |
| `Alistar` | sim — são os pawns dele |
| `OrdemDeTrabalho` | sim |
| `Designar` | sim — **decisão registrada**, ver abaixo |
| `Incidente` | **não** |

### Por que designar fica com o visitante

Construir e minerar são ordens à colônia, não a um pawn, então caberiam no balde
de "decisão da casa". Ficam fora de propósito: **o visitante precisa poder erguer
uma barricada durante um raid.** A visita existe para mandar tropas e ajudar
(§5); tirar dele a capacidade de preparar defesa esvaziaria isso.

O risco é o visitante bagunçar a colônia alheia — e a resposta é a mesma do
§1.1: não é anti-cheat, é um grupo pequeno de amigos. Quem convidou escolheu
quem entra.

Fica registrado para não ser "arrumado" depois por parecer inconsistente com o
`Incidente`. A diferença é que designar **propõe trabalho** que os pawns da casa
podem ou não fazer, enquanto um incidente **acontece**.

O corte é entre "meus pawns" e "esta colônia". Alistar e mandar trabalhar têm de
continuar valendo para os dois — senão a visita não serve para mandar tropas,
que é o motivo dela existir (§5). Escolhas do lugar — missões, aceitar ou recusar
eventos, provocar acontecimentos — são do anfitrião.

Não é anti-cheat (§1.1 diz que não é o objetivo). É evitar que duas pessoas
respondam ao mesmo diálogo, que é bagunça mesmo entre amigos, e que uma pergunta
com uma resposta receba duas.

### Onde a regra mora

Em `protocol/`, não no cliente: quem aplica é o **coordenador**, no momento de
carimbar. Foi por isso que `TipoDeComando` mudou de lugar — o servidor precisa
entender o primeiro byte do payload. Ele continua sem entender de jogo: lê um
byte, não um comando.

Recusar antes de carimbar é o que garante que ninguém aplique metade — o comando
recusado nunca chega a existir para lado nenhum. E a recusa volta explicada, como
`CodigoErro.SemAutoridade`: silêncio numa recusa é pior que a recusa, porque o
jogador clica, nada acontece, e ele não sabe se travou ou se não podia.

**E recusa não derruba ninguém.** Isso estava escrito no protocolo desde sempre —
"a sessão segue normalmente, só este comando não acontece" — e o cliente não
cumpria: todo erro que não fosse planeta virava carta de "conexão encerrada" mais
`Desconectar()`. Um teste à mão mostrou o preço em 16/09/2026: o visitante clicou
em apagar uma zona, o coordenador recusou por autoridade, e a **visita inteira
terminou** — do lado do anfitrião, `ParticipanteDesconectou`. Um botão proibido
encerrava a partida de duas pessoas. Agora a recusa vira mensagem na tela, que é
onde o jogador olha quando o clique não faz nada.

### O que a lista de só-anfitrião não deve conter

Uma superfície pela metade. `Zona` entrou nessa lista e durou uma tarde: os
designadores de zona nunca foram restritos, então o visitante já encolhia uma
zona célula a célula e a expandia em 117 de uma vez — só o botão de apagar era
proibido. Restringir metade de um gesto não protege decisão nenhuma; só faz o
clique não responder.

O §4 fala de decisões que admitem **uma resposta só**: missões, aceitar ou
recusar um evento, provocar um acontecimento. Zona é trabalho de colônia, e o
visitante está ali para ajudar.

## Incidente como comando

Nasceu de um impedimento de teste. As ferramentas de debug estão bloqueadas na
visita, e devem estar — mas isso tirava o único jeito de exercitar combate:

```
[WithFriends] ação de debug recusada durante a sessão: 40 points
```

Debug action **"Provocar raid na visita"**: manda `IncidentDefOf.RaidEnemy` com
os pontos de ameaça do momento como comando. Acontece no mesmo tick nos dois
lados, com o mesmo RNG — ao contrário da ação de debug, que acontecia num só.

Só o def e os pontos viajam; o resto dos parâmetros sai de
`StorytellerUtility.DefaultParmsNow`, que é função do estado compartilhado.

Não é gambiarra de teste: é a primeira decisão de colônia, e serve de molde para
as próximas (missões, resposta a evento).

## O comando tem de sortear do fluxo da sessão

Primeiro raid pela via de comando, e desync imediato. O log dizia tudo:

```
comando 1 … no tick 131: incidente RaidEnemy (35 pts) → ok (rng +0)
```

**Um raid gasta cerca de cem sorteios. `+0` não é possível.**

A causa era a ordem dentro do prefixo do tick: os comandos eram aplicados
**antes** de `RngDeSessao.AntesDoTick()`. Ou seja, sorteavam do RNG do
**processo** — que não tem relação nenhuma entre duas máquinas — em vez do da
sessão. Os dois lados geraram raids diferentes, e divergiram no tick seguinte.

A ordem correta estabelece o contexto determinístico primeiro:

```
RngDeSessao.AntesDoTick()        ← estado de RNG da sessão
TempoRealDoTick.AntesDoTick()    ← tempo em ticks, não em quadros
NaInterface.Tickando = true
RastreioDeRng.AbrirTick()
  → só então: AplicarComandosDoTick()
  → e então o tick do jogo
```

### Por que ficou escondido tanto tempo

Porque os comandos que existiam até agora — velocidade, alistar, mover, designar
— quase não sorteiam. O log dizia `(rng +0)` em todos eles, e `+0` parecia
normal. Só um comando que sorteia de verdade tornaria o erro visível.

O `(rng +N)` no log existe exatamente para isso. Estava visível desde o começo;
faltou reparar que `+0` era resposta certa para os comandos errados.

**Regra que fica:** comando novo que sorteie é o teste do fluxo. Se ele reportar
`rng +0`, não está sorteando de onde deveria.

### O jogo ficou impausável

Terceiro bug da mesma família — **estado mecânico confundido com intenção do
jogador** — e o mais teimoso, porque a correção anterior o criou.

Para desfazer a pausa que o consenso impõe, eu tinha posto:

```csharp
else if (visitaComecou && velocidadeDesejada != TimeSpeed.Paused
         && Find.TickManager is { CurTimeSpeed: TimeSpeed.Paused })
{
    Find.TickManager.CurTimeSpeed = velocidadeDesejada;   // "estava imposta"
}
```

A condição não lembra **quem** pausou. Ela lê "o relógio está pausado e ninguém
pediu pausa" — o que também é verdade no instante entre o jogador clicar em
pausa e o relato dele sair. Resultado: a pausa do jogador era desfeita antes de
existir.

Ficou invisível até o raid, porque a carta de evento **pausa o jogo sozinha**:
aí virava um cabo de guerra a cada quadro, e o jogo simplesmente não pausava.

A correção é lembrar a autoria:

```csharp
if (barreira.Pausado) { relogio.Pausar(); pausaImposta = true; }
else if (pausaImposta && relógio pausado) { desfaz; pausaImposta = false; }
```

E escolha do jogador cancela a imposição: mexer no botão limpa `pausaImposta`.

**O padrão, pela terceira vez:** todo estado que a sessão impõe precisa de par —
o imposto e o pedido — e de autoria, para que só quem impôs desfaça. Os três
casos foram `visitaComecou` (congelamento pré-visita relatado como pausa), o eco
de velocidade, e este.

## A pausa parou de brigar com o jogador

Pausar, despausar e trocar velocidade precisavam de **dois cliques**: o primeiro
era desfeito pela resposta da barreira em trânsito, que carregava um consenso de
antes do clique.

Foi o terceiro bug seguido nascido da mesma ideia — o consenso mexendo no botão
de velocidade do jogador — e o último não tinha conserto limpo. Lembrar a
autoria (`pausaImposta`) resolvia um caso e criava outro, porque a defasagem é
real: a resposta sempre reflete um relato mais velho que a intenção mais nova.

**A barreira já é o consenso.** Com alguém pausado, o coordenador para de liberar
tick, e nada anda dos dois lados — sem tocar no relógio de ninguém. O relógio
local voltou a ser do jogador, e só dele; o que o consenso controla é o tick, que
é o que ele deveria controlar desde o começo.

## Dar ordens com o jogo parado

Com isso apareceu o que estava escondido atrás do bug: **comando só acontece
dentro de um tick, e pausado não há tick.** Alistar um pawn com o jogo parado não
fazia nada até alguém despausar. O RimWorld se joga pausado o tempo todo — isso
não é detalhe, é o jogo não funcionando.

A saída usa a distinção que o coordenador já tem:

| bandeira | significa |
|---|---|
| `Pausado` | **este** cliente quer o tempo parado |
| `TodosPausados` | **todos** pediram pausa |

A ideia era: com todos parados, os dois lados estão no mesmo tick e param ali,
então aplicar agora acontece igual nos dois. **A premissa é falsa, e custou um
desync.**

```
J1  comando 10 … agendado para o tick 978 (estou em 972)
J2  comando 10 … agendado para o tick 978 (estou em 952)
```

Os dois pausados, os dois aplicando "agora", **20 ticks de distância**. J2 gastou
234 sorteios no tick 952 que J1 gastou no 972.

Pausa para cada lado **onde ele está**, e a barreira segura a liberação no
mínimo — então quem estava à frente continua à frente enquanto a pausa durar.
Não há mecanismo que os aproxime: o que está atrás também está pausado, e
relógio pausado não anda por mais liberação que receba.

Revertido. `TodosPausados` continua na mensagem: é a informação certa para
mostrar "todos pausados" e é peça da correção de verdade, abaixo.

### A correção de verdade: tick guiado pela barreira

A assimetria de fundo é que **o relógio local decide quando tickar**. É o
comportamento do vanilla, e é o que deixa os dois lados em ticks diferentes.

O Multiplayer não tem esse problema porque lá o tick é guiado pelo servidor: o
cliente simula até `tickUntil` o mais rápido que consegue, e a velocidade local é
só um pedido de quão rápido aquilo avança. Todos os clientes estão sempre **no
mesmo tick**, e pausar é o servidor parar de avançar.

Adotar isso resolve de uma vez:

- comandos parados, porque "parado" passa a ser um tick comum
- a classe inteira de bugs de "um lado à frente do outro"
- o consenso de pausa, que vira consequência em vez de regra à parte

Não é pequeno: mexe em `RelogioDeSessaoRimWorld` e na barreira, e muda o que a
velocidade do jogo significa. Fica registrado como o próximo passo estrutural.

Enquanto isso, ordens dadas com o jogo parado valem quando o tempo voltar a
andar.

## O botão precisa responder antes do comando voltar

Alistar exigia dois cliques. O log mostra o que acontecia:

```
Engie: desalistar proposto como comando      ← clique 1
Engie: desalistar proposto como comando      ← clique 2, mesmo pedido
comando 19 … Engie desalistado
```

**O primeiro clique funcionava.** O gizmo de alistar é um alternador que lê
`drafter.Drafted`, e numa visita esse valor só muda quando o comando volta
carimbado. O botão continuava no estado antigo, o jogador achava que tinha
falhado e clicava de novo — e o alternador, recalculando sobre o mesmo estado,
pedia exatamente a mesma coisa.

Nada quebrava (os dois pedidos eram idênticos), mas a sensação era de botão
emperrado.

### Previsão local, só na interface

Dentro da interface, `Drafted` passa a responder o valor **pedido**. Dentro do
tick, responde o valor real.

É `NaInterface` usado ao contrário do habitual: até aqui ele servia para impedir
a interface de mexer na simulação; agora serve para deixar a interface mentir
**só para si mesma**. A simulação nunca vê o palpite.

O palpite expira em dois segundos. Comando que não voltou nesse tempo não vai
voltar, e botão que mente para sempre é pior que botão lento.

### O palpite só morre para o comando dele

Primeira versão encerrava o palpite quando **qualquer** comando daquele pawn
chegava. Com dois cliques rápidos isso invertia o botão na cara do jogador:

```
palpite: pawn 189 → alistado
alistar proposto          ← clique 1
palpite: pawn 189 → livre
desalistar proposto       ← clique 2
palpite confirmado        ← ⚠ encerrado pelo comando do clique 1
comando 1 … Grey alistado ← botão salta para "alistado"
comando 2 … Grey livre    ← e só então volta
```

Era o "às vezes ativa e desativa". Não era instabilidade: era **ordem** — comando
antigo respondendo a uma intenção que já tinha sido substituída.

Agora o palpite só é encerrado por um comando cujo valor é o dele. Comando
antigo passa, muda o estado real, e o botão continua mostrando o que o jogador
pediu por último.

### A regra geral

Toda ação de jogador que vira comando tem este problema, porque o desenho é o
mesmo: o clique não muda o estado, o comando muda. Onde o controle for um
**alternador** que lê o estado, ele precisa de palpite na interface — senão a
pessoa clica duas vezes.

Já resolvidos assim: velocidade e pausa (previsão no relógio local, ADR 0016) e
alistar. Os próximos candidatos são os alternadores que ainda não viraram
comando — `FireAtWill`, prioridades de trabalho, áreas.

## O arrasto é um comando, não quarenta

Planejar e construir estavam engasgando: cancelar uma área deixava células para
trás, e construir uma parede saía "uma por vez".

A causa é aritmética. `DesignateMultiCell` percorre as células chamando
`DesignateSingleCell`, e nós interceptávamos o **singular** — então arrastar
sobre 40 células gerava:

- 40 comandos propostos,
- 40 carimbos do coordenador,
- e, com o jogo pausado, **40 passos de um tick** (um por comando, Regra 6).

Daí a impressão de o jogo construir um por um e de perder ordens: não perdia,
estava enfileirando.

Agora a intercepção é no `DesignateMultiCell`, **antes** do laço: um comando
carregando a lista de células. Do outro lado, quem percorre é o próprio jogo —
com o `CanDesignateCell` e o `Finalize` dele, não uma imitação nossa.

O clique único continua indo pelo `DesignateSingleCell`, que é o caminho que o
jogo usa quando não há arrasto.

## Só o que vem da interface vira comando — os três casos

A regra apareceu três vezes, em três lugares, e vale registrar junta porque é a
mesma:

| intercepção | o que o jogo também chama por conta própria |
|---|---|
| `Pawn_DraftController.Drafted` | pawn derrubado, job encerrado, mecânico desativado |
| `Pawn_JobTracker.TryTakeOrderedJob` | puxar o próximo job da fila |
| `Designator.Designate*` | designador usado por código do jogo |

Em todos, tratar a chamada interna como clique do jogador faz duas coisas
ruins: bloqueia a lógica do jogo e manda comando que ninguém pediu.

O guard é `NaInterface.Agora` nos três. O Multiplayer abre os três
interceptadores de designador dele com exatamente a mesma linha:

```csharp
if (!Multiplayer.InInterface) return true;
```

Dois projetos chegando à mesma primeira linha é um bom sinal de que ela é
obrigatória, não opcional.

### Uma pista que não era

O Multiplayer também cancela `Designator.Finalize(true)` fora de comando, e isso
parecia explicar o "às vezes a construção aparece, às vezes não" — porque o
`DesignatorManager` chama `Finalize` logo depois do `DesignateSingleCell` que nós
cancelamos.

Fui ver o que `Finalize(true)` faz: chama `FinalizeDesignationSucceeded()`, e nem
`Designator_Build`, nem `Designator_Place`, nem `Designator_Cancel` sobrescrevem
isso — só toca o som de sucesso. O patch dele ali é de feedback, não de correção.

Fica registrado para não ser reinvestigado.
