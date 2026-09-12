# Onde estamos, medido contra o Multiplayer

Estimativa, não contabilidade. Serve para decidir escopo, não para prometer data.

## O ponto de partida: o `Rand` do RimWorld é um fluxo global

A observação que motivou esta página está certa, e vale escrever com precisão.

`Verse.Rand` é **um** gerador estático, global e mutável. Não há fluxo por
entidade, por sistema ou por tick. Consequência: todo sorteio depende da ordem
de todos os sorteios anteriores do jogo inteiro — um `Rand.Value` a mais em
qualquer lugar desloca tudo que vem depois, para sempre.

Não é burrice; é uma escolha razoável para um jogo de um jogador só, onde nada
disso importa. Ludeon inclusive semeia parte do que precisa ser estável
(`World.ConstantRandSeed`). O que essa escolha custa é exatamente o que estamos
pagando: em lockstep, **cada caminho de código que sorteia vira um caso a
tratar**, e só se descobre reproduzindo a divergência.

O desenho que baratearia isto seria fluxo semeado por subsistema — cada pawn,
cada mapa, cada incidente com sua própria sequência derivada de `(semente, id,
tick)`. Aí um sorteio a mais não contaminaria os outros.

## O tamanho do Multiplayer

| | |
|---|---|
| linhas (só o cliente) | 46.777 |
| atributos `HarmonyPatch` | 602 |
| registros de sync | 560 |

Esses 602 não são todos determinismo. Divididos por pasta:

| pasta | patches | o que é |
|---|---|---|
| `Patches/` | 237 | determinismo, tick, UI, compatibilidade |
| `Persistent/` | 92 | diálogos e sessões compartilhadas (trade, caravana, ritual) |
| `AsyncTime/` | 43 | cada mapa com sua própria linha do tempo |
| `Desyncs/` | 13 | detecção e relatório |
| resto | 217 | comps, janelas, rede |

## O que o nosso desenho corta

Não por atalho — por decisão registrada em ADR:

| fora de escopo | por quê | tamanho no MP |
|---|---|---|
| tempo assíncrono por mapa | a visita é lockstep em tempo real (§4) | 2.280 linhas, 43 patches |
| diálogos/sessões persistentes | comércio vai pelo planeta, sem lockstep (§6) | 5.181 linhas, 92 patches |
| maior parte dos 560 sync | fora da visita nada é sincronizado | — |
| anti-cheat / arbiter | grupo pequeno de amigos (§1.1) | — |
| ponto de junção sem recarregar | os dois recarregam (ADR 0011) | — |

O corte é real: o MP sincroniza **a partida inteira, para sempre**, então precisa
cobrir pesquisa, políticas, bills, caravanas, ideologia, cada DLC. Nós
sincronizamos **um mapa, durante uma visita**, e descartamos no fim.

## O piso

Duas frentes, e só uma delas tem piso duro.

**Determinismo — o piso.** É propriedade do jogo, não do nosso desenho: se o
código sorteia ou lê estado local dentro do tick, tem de ser tratado, e não há
escopo que escape disso. No MP, o núcleo equivalente é
`Determinism.cs` + `HashCodes.cs` + `Seeds.cs` + `PathFinderPatch.cs` ≈ 1.400
linhas, algo entre 50 e 60 pontos.

Nós temos ~10. Com as cinco famílias já nomeadas (RNG cosmético, câmera,
interface, thread paralela, dispositivo de entrada) e uma sexta em investigação
(ordem de coleção / entrada diferente com mesmo sorteio).

**Comandos — proporcional ao que se quer poder fazer numa visita.** O MP tem 560
porque sincroniza o jogo todo. Dentro de uma visita, o que precisa virar comando
é mover, alistar, atacar, designar, os gizmos de pawn alistado, velocidade e
pouco mais: algo entre 25 e 40. Temos 4.

| frente | MP | escopo nosso | temos |
|---|---|---|---|
| determinismo | 50–60 | 50–60 (não dá para cortar) | ~10 |
| comandos | 560 | 25–40 | 4 |
| linhas de cliente | 46.777 | — | 10.462 (com servidor e protocolo) |

## O que mudou de verdade

Contar pontos engana, porque o custo nunca foi escrever o patch — é **achar** o
caso. Cada um só aparece reproduzindo a divergência.

O progresso que importa é que isso deixou de ser adivinhação:

| instrumento | responde |
|---|---|
| histórico de RNG | em que tick divergiu |
| rastreio de RNG por local | qual chamada consumiu diferente |
| rastreio de estado de pawn | qual pawn e qual campo divergiu, sem sorteio nenhum |
| digital amostrada no tick | detecta em ~25 ticks, não em 187 |

As três últimas causas (câmera, teclado, atraso da detecção) foram achadas em uma
rodada cada. As três anteriores levaram várias, e duas ficaram sem nome.

## Duas direções que mudam o piso

O piso de 50–60 pontos de determinismo pressupõe continuar remendando
chamadores. Duas decisões o baixam, por caminhos ortogonais:

| ADR | o que reduz |
|---|---|
| 0014 — fluxo derivado por entidade | o **raio de dano** de um sorteio a mais: divergência vira local em vez de global |
| 0015 — remendar fontes, não chamadores | o **número de lugares** que podem causar um: uma fonte cobre todos os seus chamadores, inclusive os de versões futuras |

Nenhuma substitui a outra, e nenhuma cobre a corrida de thread, a ordem de
coleção, nem a superfície de comandos.

## As alavancas, por retorno

O auditor de IL não é o teto — ele é o instrumento de uma das alavancas, e não da
maior. Ordenadas por quanto mudam a experiência de jogar:

### 0. Tick guiado pela barreira — **feito** (ADR 0016)

Apareceu depois que as outras foram escritas, e era mesmo a primeira.

Hoje o **relógio local** decide quando tickar — comportamento do vanilla. A
consequência é que os dois lados ficam em ticks diferentes com facilidade, e
pausados eles **congelam separados**: medido, um no tick 972 e o outro no 952,
sem nada que os aproxime.

Disso saem: ordens que não funcionam com o jogo parado, o consenso de pausa como
regra à parte, e toda a família de bugs de "um lado à frente do outro".

No Multiplayer o tick é guiado pelo servidor: o cliente simula até `tickUntil` o
mais rápido que consegue, e a velocidade local é só um pedido. Todos estão sempre
no mesmo tick.

### 1. Ressincronizar em vez de abortar

A única que muda o **modo de falha** em vez da **taxa de falha**. Todo o resto
torna a divergência mais rara; esta torna a divergência barata.

O maquinário já existe e já roda em toda visita: 10,45 MB → 1,17 MB em 599 ms
para serializar, 95 ms para receber. Uma divergência vira um soluço de um
segundo em vez de perder o encontro.

E não depende de conhecer nenhuma causa restante — funciona inclusive para as que
nunca vamos achar. É por isso que vem primeiro.

### 2. Fluxo derivado por entidade (ADR 0014)

Reduz o **raio de dano**: um sorteio a mais deixa de contaminar a colônia inteira
e passa a afetar um pawn num tick. Combina com a 1 — divergência local e barata
pode até ser reparada localmente, sem reenviar a partida.

Três pontos de acoplamento, todos estáveis, e `Verse.Rand` já é função pura de
`(seed, iterations)`. Custa uma digital de estado antes.

### 3. Remendar fontes, não chamadores (ADR 0015)

Reduz o **número de lugares** que podem divergir. Uma fonte cobre todos os seus
chamadores, inclusive os que ainda não existem.

### 4. O auditor de IL

Instrumento da 3: responde "quais métodos de simulação alcançam fonte local" na
subida, em vez de esperar a divergência acontecer. Sozinho ele não conserta nada
— produz uma lista. Mas é o que faz a 3 ser sistemática em vez de reativa, e é o
que se reavalia sozinho a cada atualização do jogo.

## O teto de verdade, e por que não está aqui

O teto é **o visitante não simular**: estado autoritativo do anfitrião chegando
pronto, visitante renderiza e manda comandos. Aí determinismo deixa de ser
requisito — nenhuma das quatro alavancas seria necessária.

O que impede é que o RimWorld não tem delta de estado: são ~10 MB de coisas com
job, need e saúde, e transmitir isso a 60 Hz não existe. Seria preciso construir
um codificador de diferenças.

Vale registrar que essa ideia já apareceu aqui, do próprio autor do projeto:
"verifico o que muda dos bytes de um mapa de um tempo x pra um y, se é possível
reaproveitar coisas". É exatamente a peça que falta — e é o que ligaria a
alavanca 1 (ressincronizar) ao teto, porque ressincronizar com delta barato,
feito com frequência, **é** estado autoritativo.
