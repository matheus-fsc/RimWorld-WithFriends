# Medições

> Números colhidos nesta máquina, com o jogo rodando. Sem estimativa: se está
> aqui, foi medido.

## Custo de transferir um mapa (2026-09-11)

Passo B da [ADR 0007](adr/0007-troca-de-estado-antes-do-lockstep.md), primeira
tarefa. Contexto: mapa 250×250, **34.528 things**, 64 pawns, riqueza 14.941 —
uma colônia madura, não um mapa recém-gerado.

| O quê | Cru | Comprimido (gzip) | Serializar | Comprimir |
|---|---|---|---|---|
| **Só o mapa** | 11,09 MB | **0,64 MB** | 915 ms | 93 ms |
| Partida inteira | 13,12 MB | 1,31 MB | 1.382 ms | 133 ms |

### Três conclusões

**1. Serializar um mapa isolado funciona.** Era a dúvida que motivava a
medição — o Multiplayer salva a partida inteira e recarrega, e não estava
claro se era escolha de simplicidade ou impossibilidade. É escolha: o `Scribe`
traz um `Map` sozinho sem reclamar. O bootstrap da visita pode mandar só o
mapa, como a §4 assume.

**2. Comprimir muda a natureza do problema.** 11,09 MB viram **0,64 MB** —
17× menor. O que trafega numa visita é meio megabyte. Por IPv6 direto (§17.4,
caminho 1) isso é instantâneo; o gargalo passa a ser a serialização, não a
rede.

**3. O planeta é o que comprime mal — e é justamente o que não trafega.** A
partida inteira tem só 18% mais bytes crus que o mapa, mas **o dobro**
comprimido. Os ~2 MB a mais são mundo e facções, guardados como blobs base64
que o gzip quase não reduz. A decisão da §4.2 — planeta se gera, não se
transfere — é confirmada por acidente: ela remove exatamente a parte
incompressível.

### Contra o orçamento

A [ADR 0008](adr/0008-escala-alvo-e-ciclo-da-visita.md) não fixa número: o
princípio é que **um tempo de carregamento anunciado é aceitável**, com a
régua do próprio jogo, que já leva alguns segundos para carregar um save.

```
serializar no anfitrião      ~0,9 s
comprimir                    ~0,1 s
transferir 0,64 MB           desprezível em IPv6 direto
descomprimir + carregar      a medir
```

Cerca de **1 segundo** do lado de quem envia. Mesmo que carregar custe o dobro
de serializar, o encontro abre em poucos segundos — confortavelmente dentro do
que o jogo já acostuma o jogador a esperar.

Sobre mapas de fim de jogo: o custo acompanha a **quantidade de coisas**, não
a área. Esta colônia tem 34,5 mil things; uma base de fim de jogo com o triplo
disso ficaria em torno de 3 s para serializar — ainda dentro do orçamento, mas
já sem a folga atual. É a métrica a acompanhar.

### Comparação com o RT

O RT media **1.739 ms para ~1 MB** (§15.6). Aqui são 915 ms para 11 MB — mais
de 20× mais rápido por megabyte. A medição deles provavelmente incluía o custo
de *aplicar* o mapa, não só serializar; é o que a próxima medição vai dizer.

## Bootstrap de visita entre dois jogos (2026-09-11)

Primeira visita real entre duas instâncias, colônia do anfitrião com 5,64 MB
de mapa:

| Etapa | Custo |
|---|---|
| Serializar + comprimir (anfitrião) | 654 ms — 5,64 MB → **0,33 MB** |
| Transferir + verificar + gravar (visitante) | 231 ms |
| Rollback: carregar save de 7,6 MB | **6,9 s** (2,6 s de Scribe, resto em recriação) |

Duas leituras:

1. **A transferência é barata** — 0,9 s somando os dois lados, e a rede não
   aparece na conta por IPv6 local.
2. **Aplicar é o caro.** Carregar a partida inteira custou 6,9 s, ~7× a
   serialização. É o número que o RT provavelmente media (§15.6). Inserir
   **um mapa** numa partida já carregada deve custar bem menos que recarregar
   tudo — mas é essa medição que decide o custo real da visita.

## Inserir um mapa numa partida em andamento (2026-09-11)

**Não funciona, e o motivo é de desenho.** Ver
[ADR 0010](adr/0010-a-visita-carrega-a-partida-do-anfitriao.md).

```
Could not resolve reference to object with loadID Ideo_8 of type RimWorld.Ideo
Could not resolve reference to object with loadID ApparelPolicy_Anything_1
Could not resolve reference to object with loadID Thing_Human342 (DirectPawnRelation)
Could not do PostLoadInit on RimWorld.Pawn_IdeoTracker: NullReferenceException
Exception while rebuilding dirty regions: ArgumentOutOfRangeException
```

Um mapa **não é autocontido**: seus pawns referenciam ideologias, políticas,
facções e relações que vivem na partida. O arquivo abre — a medição anterior
estava certa — mas não se reconecta a nada em outra partida.

Consequência: o bootstrap passa a transferir a **partida inteira**
(1,31 MB comprimidos, 1.382 ms), e o visitante joga dentro dela.

## A mala do visitante (2026-09-11)

Três colonos com inventário, empacotados e abertos na **mesma partida** (pior
caso de colisão de id):

| Etapa | Custo |
|---|---|
| Empacotar 3 pawns + 1 ideologia | **59,3 KB**, 7–12 ms |
| Abrir na partida (com resolução de referências) | 81–175 ms |

Perto do resto, é ruído: o mapa são 0,64 MB comprimidos, a partida 1,31 MB.
**A ida da caravana não custa nada** — o trabalho ali é de referência, não de
tamanho.

## Primeira visita completa entre dois jogos (2026-09-11)

O visitante **entrou na partida do anfitrião**. O bootstrap da ADR 0010
funcionou de ponta a ponta:

| Etapa | Custo |
|---|---|
| Serializar + comprimir a partida (anfitrião) | 907 ms — 5,70 MB → **0,88 MB** |
| Receber, verificar, gravar e carregar (visitante) | entrou e retomou a sessão |
| Rollback do anfitrião (save de 5,7 MB) | 6,5 s (2,5 s de Scribe) |

```
[anfitrião] enviando a partida da visita: 5.70 MB → 0.88 MB em 907 ms
[visitante] visita retomada dentro da partida do anfitrião — tick 2181, mapa 0
```

E abortou, por dois motivos — os dois corrigidos:

1. **Dois ticks de diferença.** Anfitrião parado em 2179, visitante em 2181: o
   jogo recém-carregado tickou alguns frames antes de a sessão ser retomada.
   Divergência real, não falso positivo. Correção: o freio de tick é estático e
   passa a ser armado **antes** da troca de partida, e o anfitrião só descongela
   quando a barreira anda pela primeira vez.
2. **`Rand.PopState` estourando.** `Rand.EnsureStateStackEmpty()` roda
   periodicamente e esvazia qualquer estado empilhado ("Random state stack is
   not empty… Fixing"). A pilha do RimWorld é para intervalos curtos, não para
   durar uma sessão. Correção: guardar o estado da sessão fora da pilha e
   trocá-lo em volta de **cada tick**, que é o intervalo que precisa ser
   determinístico.

O ciclo de segurança funcionou de novo: os dois voltaram ao
`checkpoint_pre_sessao`, e o visitante apagou o save temporário da visita.

### Segunda tentativa: 1 tick de diferença, causa diferente

Com as duas correções acima, a distância caiu de 2 ticks para 1 — e a causa
era outra, mais sutil:

```
[anfitrião] sessão iniciada — tick de jogo 131
[visitante] visita retomada dentro da partida do anfitrião — tick de jogo 132
```

**`GameDataSaveLoader.LoadGame` enfileira a carga num long event.** A partida
antiga continua atualizando por vários frames depois da chamada — e o nosso
`GameComponentUpdate` via a visita ativa e retomava a sessão **dentro da
colônia do próprio visitante**, que então reportava a digital dela.

A divergência era real e a mensagem estava certa: eram dois estados diferentes
mesmo. Só que um deles não era o que a gente pensava.

Correção: guardar a referência da partida de origem e só retomar quando
`Current.Game` for **outro objeto**. Trocar de partida não é um instante — é um
intervalo, e o código precisa saber disso.

### Terceira tentativa: o mesmo intervalo, outro sintoma

Ainda 2 ticks. O freio **foi** armado corretamente:

```
[visitante] simulação freada no tick 697 até a visita ser retomada
[visitante] visita retomada dentro da partida do anfitrião — tick de jogo 699
```

Mas a partida **antiga** continuava atualizando durante o intervalo da carga, e
ao processar uma barreira recalculava o freio com o próprio tick
(`tickGameNoInicio` = 795, o tick da colônia do visitante) — desfazendo o freio
de 697. Quando a partida do anfitrião carregava, já estava liberada.

Correção: enquanto a troca não terminou, a partida antiga **não fala sobre a
sessão** — não reporta barreira, não agenda comando, não mexe no relógio.

Padrão que se repete: as três falhas do bootstrap foram todas sobre o
*intervalo* entre pedir a carga e ela acontecer. Não é um instante, e cada
parte do código que assume que é, quebra de um jeito diferente.

## Um teto que era pequeno demais (2026-09-11)

Checkpoint de colônia madura: **17.987.906 bytes**. O teto do envelope era
16 MiB (16.777.216) — **1,2 MB a menos do que o necessário**.

O modo de falha foi pior que o limite: o cliente escrevia o quadro sem
conferir, o servidor o recusava ao ler, a conexão morria, e os dois lados só
viam `The socket has been shut down`. Nada dizia "a mensagem era grande demais".

Três correções:

1. **Compressão no transporte.** Payload ≥ 64 KB viaja comprimido se encolher.
   Saves de RimWorld encolhem ~10×: os 17,9 MB viram ~1,8 MB na fiação.
2. **Teto conferido na escrita**, com mensagem legível para quem enviou — em
   vez de descobrir no outro lado.
3. **Quadro inválido no servidor vira `SistemaErro`** com explicação, não
   desconexão muda (§9.1).

## A visita começou (2026-09-11)

Depois de sete correções de sequenciamento, os dois lados entraram na mesma
partida, no mesmo tick, com a mesma semente — e a barreira liberou:

```
[anfitrião] visita começou — barreira liberou o tick 10
[visitante] visita começou — barreira liberou o tick 10
```

Simularam **juntos até o tick 9**, e divergiram ali. É a primeira divergência
que não é de montagem: os dois estavam de fato simulando o mesmo jogo.

E depois dela, a oitava: **deadlock de pausa**. Os dois lados congelam
esperando o outro entrar e reportavam isso como "pausado". O coordenador,
honrando o consenso de pausa (§3), segurava a barreira — e como a barreira
nunca liberava, ninguém descongelava. Sessão travada, sem saída nem pela UI.

A distinção que faltava: **congelamento de entrada não é pausa do jogador.**
O cliente agora só reporta pausa depois que a visita começou de fato.

As sete correções anteriores, na ordem em que apareceram — todas do mesmo tipo, "o
código assumia que algo era um instante quando era um intervalo, ou media o
estado do processo quando queria o do jogo":

1. teto do envelope pequeno demais (17,9 MB > 16 MiB)
2. anfitrião tickando enquanto o save viajava
3. `LoadGame` assíncrono: sessão retomada na partida errada
4. partida antiga desfazendo o freio de tick
5. digital medindo o RNG do processo, não o da sessão
6. digital dependendo da cadência de quem relata
7. postfix do Harmony rodando mesmo com o prefix barrando, contaminando o RNG

Para achar o oitavo, o instrumento mudou: cada tick registra
`(tick, estado do RNG)` num anel de 240 entradas, despejado no log **no
aborto** (§14.3 decisão 4). Comparar os dois despejos lado a lado diz em qual
tick a divergência **começou** — que não é necessariamente aquele em que foi
percebida.

## A divergência real: vivo contra restaurado (2026-09-11)

Com a visita finalmente começando, os dois lados divergiram no **tick 1** — o
tick 9 era só onde os relatos coincidiram.

| tick | visitante | anfitrião | consumo no tick |
|---:|---:|---:|---|
| 1 | 12 | 45 | 12 / **45** |
| 5 | 59 | 194 | 15 / **33** |
| 10 | 115 | 402 | 7 / **43** |

O anfitrião consumia **3,5× mais aleatoriedade por tick**, do primeiro ao
último. E o retrato dos dois lados era idêntico em tudo menos numa linha:

```
anfitrião  11257 things
visitante  10366 things   → 891 a menos (7,9%)
```

Um jogo **vivo** e o mesmo jogo **recém-carregado** não são iguais: o vivo tem
motes em voo, caches quentes, listas em outra ordem. Ver
[ADR 0011](adr/0011-ponto-de-encontro-os-dois-recarregam.md) — a correção é o
anfitrião recarregar também, que é o que o Multiplayer faz para criar um ponto
de entrada.

## Lockstep funcionando — e o primeiro desync legítimo (2026-09-11)

Com os dois lados recarregando do mesmo save (ADR 0011), a visita **rodou**:

```
tick 129   rng 1262   |   tick 129   rng 1262   ✓
tick 130   rng 1270   |   tick 130   rng 1270   ✓
tick 131   rng 1277   |   tick 131   rng 1277   ✓
tick 132   rng 1284   |   tick 132   rng 1284   ✓
tick 133   rng 1293   |   tick 133   rng 1293   ✓
```

**Consumo de RNG idêntico, tick a tick, por mais de 130 ticks.** A simulação do
RimWorld é determinística entre duas máquinas — a pergunta que abria o M3 está
respondida: dá.

A divergência veio depois, e o traço mostra exatamente o quê:

| tick | jogador 1 | jogador 2 | diferença |
|---:|---:|---:|---:|
| 358 | 3516 | 3393 | 123 |
| 361 | 3537 | 3414 | 123 |
| **362** | 3547 | **3537** | 10 |

O valor do jogador 2 no tick 362 é exatamente o do jogador 1 no tick 361: **um
evento de ~123 sorteios aconteceu com um tick de diferença entre os dois.**

E o retrato no aborto dá o motivo:

```
jogador 1: velocidade Superfast
jogador 2: velocidade Normal
```

Velocidades diferentes fazem os dois atravessarem os mesmos ticks em momentos
distintos. Correção: **velocidade do tempo virou comando de sessão** —
carimbada pelo coordenador e aplicada no mesmo tick nos dois lados (§3).

É o primeiro comando de verdade do `IAplicadorDeComando`, e ele chegou pela
regra que o define: *o que não é determinístico a partir do estado
compartilhado vira comando.*

## Detecção tardia: o aborto não diz quando divergiu (2026-09-11)

Com a velocidade sincronizada, a visita durou de 365 para **1699 ticks**. Mas o
traço mostrou que a divergência já existia no primeiro tick da janela
guardada (1463):

```
tick  1463   J1 20624   J2 20681   dif -57
tick  1464   J1 20633   J2 20689   dif -56
```

Dois problemas de instrumento, não de simulação:

1. **O anel guardava 240 ticks.** A origem saiu da janela antes do aborto.
   Passou para 4000.
2. **Os relatos de barreira eram por tempo real (200 ms).** Em Superfast isso
   é ~12 ticks, e cada lado relatava ticks diferentes — o coordenador só
   compara digitais **do mesmo tick**, então a comparação quase não acontecia.
   Uma divergência nascida por volta do tick 1400 só foi percebida no 1699.

Agora o relato é em ticks **alinhados** (múltiplos de 8), com um relato por
tempo apenas para a barreira continuar andando com o jogo pausado. Os dois
lados passam a relatar os mesmos ticks, e a comparação acontece a cada 8.

É a decisão 1 da §14.3 cobrando o que promete: a digital **diverge
imediatamente** — mas só se alguém estiver comparando.

## A primeira ordem de jogo (2026-09-11)

Com os relatos alinhados, o instrumento acertou o tick exato:

```
tick 181   J1 1908   J2 1908   igual
tick 182   J1 1920   J2 1924   dif -4      ← o clique em "alistar"
tick 183   J1 1931   J2 1936   dif -5
tick 184   J1 1940   J2 2051   dif -111    ← o pawn largou o trabalho e recalculou rota
```

A assinatura conta a história inteira: **um salto pequeno no tick da ordem,
um salto grande dois ticks depois**, quando as consequências da ordem se
espalharam pela simulação.

Alistar passou a ser comando. É o segundo membro do registro, e o primeiro que
é ordem de jogo de verdade.

## A câmera influenciando a simulação (2026-09-11)

Com 31 comandos aplicados nos mesmos ticks dos dois lados (Goto, AttackMelee),
a divergência veio 43 ticks **depois** do último comando — e era minúscula:

```
tick 1462   J1 24786 (+18)   J2 24786 (+18)   igual
tick 1463   J1 24803 (+17)   J2 24800 (+14)   dif +3
tick 1464   J1 24811 (+ 8)   J2 24808 (+ 8)   dif +3
```

Três sorteios, uma vez só, e os passos voltam a ser idênticos depois. Não é
uma ordem do jogador nem uma consequência dela: é **um evento que aconteceu de
um lado só**.

A causa está no jogo:

```csharp
public static bool ShouldSpawnMotesAt(this IntVec3 loc, Map map, bool drawOffscreen = true)
{
    if (map != Find.CurrentMap) return false;        // mapa que o jogador vê
    if (!loc.InBounds(map)) return false;
    if (drawOffscreen) return true;
    viewRect = Find.CameraDriver.CurrentViewRect;    // posição da CÂMERA
    return viewRect.ExpandedBy(5).Contains(loc);
}
```

Criar um mote sorteia números (velocidade, rotação, deslocamento). E a decisão
de criar depende de **para onde o jogador está olhando**. Dois jogadores na
mesma partida, olhando para cantos diferentes do mapa, consomem aleatoriedade
diferente.

> Não basta o cosmético não *reagir* à simulação: ele não pode **nascer** de
> um estado que não é compartilhado.

Correção: dentro da sessão a resposta depende só do mapa e da célula. Os dois
lados criam os mesmos motes e sorteiam os mesmos números.

Esta é a terceira variação do mesmo tema, e vale a formulação geral:
**estado de visão do jogador — câmera, mapa selecionado, seleção — nunca pode
influenciar a simulação.**

Consertar `ShouldSpawnMotesAt` sozinho **não bastou** — mover a câmera ainda
divergia. Porque não é um método, é uma categoria: `MoteCounter.Saturated`
depende de quantos motes existem nesta máquina, e effecters, sons e cabelos
aleatórios sorteiam também. Ver [ADR 0012](adr/0012-efeitos-fora-do-fluxo-determinista.md):
a solução é embrulhar a **categoria inteira** em push/pop de RNG, como o
Multiplayer faz — assim não é preciso saber de antemão quais efeitos sorteiam.

## Estado inicial finalmente idêntico (2026-09-11)

Depois da ADR 0011 e do tratamento de motes, os retratos dos dois lados ficaram
byte a byte iguais:

```
mapa atual 0: 34111 things (motes 0, filth 1838, plantas 20702,
              itens 1015, construções 10508, resto 48), 46 pawns
```

Inclusive `motes 0` nos dois — recém-carregados, sem efeito nenhum em voo.

E o desync seguinte teve assinatura nova: **+119 sorteios num tick, num lado
só**, ao mover a câmera. Grande demais para fumaça, e a causa é mais sutil:
`Pawn.Drawer` cria o objeto de desenho **na primeira vez que o pawn é
desenhado**. A câmera chegando num pawn novo constrói infraestrutura — e a
construção sorteia. Ver ADR 0012.

## Quando o remédio vira doença (2026-09-11)

O embrulho de efeitos com `PushState`/`PopState` produziu dezenas de:

```
InvalidOperationException: Stack empty.
```

A pilha do `Rand` é estática e compartilhada: `EnsureStateStackEmpty()` a
esvazia periodicamente, e som pode rodar fora da thread principal. E como um
**finalizador do Harmony que lança substitui a exceção original** do método
remendado, o erro não ficou contido — os dois abortos daquela rodada
provavelmente vieram do embrulho, não de determinismo.

Correção: salvar e restaurar `Rand.StateCompressed` direto, sem pilha. E a
regra geral: **finalizador não pode falhar.**

## A digital é indício, não prova (2026-09-11)

Sem nenhum `Stack empty` no log, duas sessões abortaram — com a **mesma
assinatura**, e ela muda o diagnóstico:

```
sessão 1, ticks 641–644:   J1 total +70   J2 total +70
sessão 2, ticks 162–166:   J1 total +58   J2 total +58

dif  -2  →  -1  →  0      os traços RECONVERGEM
```

**O mesmo trabalho total, distribuído com um tick de diferença.** Não é estado
divergindo: é alguma coisa acontecendo um tick antes de um lado e o traço
voltando a bater sozinho três ticks depois.

Abortar ali mata a visita por ruído. Desync de verdade **nunca** volta a bater —
a divergência cresce, não se fecha.

Correção: o coordenador passou a exigir **3 comparações seguidas** com digitais
diferentes antes de abortar, zerando o contador assim que uma bate. O aborto
passa a listar os ticks suspeitos.

Isso é a ADR 0002 aplicada aqui: **aceitar, marcar e alertar** — em vez de
matar no primeiro indício. A digital é um indício barato e imediato; a prova é
ela **persistir**.

## Desync confirmado: o mesmo evento, 3 ticks depois (2026-09-11)

Com a histerese em vigor, o coordenador esperou a confirmação e abortou só
quando ela veio:

```
!! digitais diferentes no tick 1048 (1/3) — aguardando confirmação
!! digitais diferentes no tick 1056 (2/3) — aguardando confirmação
!! ABORTADA. Ticks suspeitos: 1048, 1056, 1064.
```

E o traço mostra o que aconteceu:

```
tick 1046   J1 +18    J2 +128    ← evento de ~110 sorteios em J2
tick 1049   J1 +139   J2 +17     ← o MESMO evento em J1, três ticks depois
```

**O mesmo trabalho, com três ticks de atraso de um lado.** Não é trabalho a
mais nem a menos: é o mesmo evento acontecendo em ticks diferentes — a
assinatura de um pawn começando um trabalho (caminho, reserva) fora de sincronia.

O log do jogo mostra os dois jogadores dando ordens ao **mesmo pawn** ao mesmo
tempo. Duas instrumentações novas para a próxima rodada:

1. **Comando que chega tarde agora falha alto.** Se um comando é agendado para
   um tick que este lado já passou, ele nunca seria aplicado aqui e seria
   aplicado lá — desync garantido e silencioso. Agora encerra a sessão com
   mensagem, em vez de engolir.
2. **Cada comando aplicado registra quanto custou em aleatoriedade.** Se os
   dois lados gastarem valores diferentes no mesmo comando, a causa é o
   comando; se gastarem igual, a causa está fora dele.

## A medir

- [ ] Carregar o mapa de volta: quanto custa desserializar (`Medir
      carregamento do mapa de volta`).
- [x] ~~Inserir o mapa numa partida em andamento~~ — **não é caminho viável**,
      ver acima e ADR 0010.
- [ ] Carregar a partida do anfitrião como visita: custo real de
      `LoadGame` sobre um save recebido (a referência é 6,9 s medidos no
      rollback, que faz exatamente isso).
- [x] ~~Tamanho do "retorno da caravana"~~ — 59,3 KB para 3 pawns, ver acima.
- [ ] Carregar a partida do anfitrião no visitante: o outro lado do bootstrap.
- [ ] **Quanto de um mapa muda entre dois momentos.** Serializar o mesmo mapa
      em T e em T+N e comparar os bytes. É o número que decide se vale mandar
      só a diferença numa visita seguinte (ADR 0008, "otimização futura") — e
      sem ele qualquer conversa sobre delta é chute.

## Custo do determinismo na visita (partida longa)

Duas decisões tomadas para caçar divergência saíram caras quando a visita passou
a durar de verdade. Sintoma relatado: visitante travado, e os **dois** lados
engasgando a 3× — assinatura de custo de CPU, não de rede.

| decisão | por que era cara | como ficou |
|---|---|---|
| rastreio de RNG por local de chamada | uma pilha gerenciada por sorteio; a conta de "11 a 26 sorteios por tick" valia para 160 ticks com poucos pawns | desligado por padrão, liga na debug action "Rastrear RNG da sessão" |
| `GetCameraUpdateRate` → 1 para tudo | jogava fora a otimização de tick inteira do 1.6 | `Pawn` → 1, resto → 15 |

O segundo merece nota. O que o jogo quer com esse número é "o que o jogador olha
responde rápido, o resto pode ser grosso" — só a primeira metade depende da
câmera. A segunda a gente mantém, e 15 é exatamente o que o vanilla já usa para
tudo que está fora da tela: nada aqui sai da faixa que o próprio jogo considera
aceitável. Pawn fino mantém o movimento liso; plantas, filth e construções voltam
a tickar em lote.

## O rastreio de RNG passou a se medir

Três abortos com a mesma assinatura — ~4 sorteios de diferença num tick, logo
depois de empilhar movimentos com shift + botão direito — e nenhum deles
identificado, porque o rastreio estava desligado por custo.

Custo que eu nunca medi: desliguei por impressão, no mesmo dia em que a lentidão
tinha **outra** causa comprovada (`GetCameraUpdateRate` devolvendo 1 para tudo).
Pode ter sido a decisão certa pelo motivo errado.

Então duas mudanças:

**O caminho quente ficou barato.** Ele montava uma string por sorteio
(`string.Join` sobre nomes de método). Agora guarda só os ponteiros dos métodos
da pilha; nome e texto só existem no despejo, uma vez por aborto.

**Ele se mede.** `RastreioDeRng.Custo` acumula sorteios rastreados e tempo
gasto, e sai no despejo e na debug action:

```
custo do diagnóstico: 41233 sorteio(s) em 780 ms (18.9 µs por sorteio)
```

Com isso a decisão de deixar ligado sai de número, não de impressão. A ordem de
grandeza que importa: a visita consome de 11 a 27 sorteios por tick, então o
custo por segundo a 1× é `sorteios/tick × 60 × µs`.

## Quanto custa o rastreio de RNG: medido

```
custo do diagnóstico: 183880 sorteio(s) em 16244 ms (88.3 µs por sorteio)
```

**88 µs por sorteio**, não os ~19 que eu chutei. A ~10 sorteios por tick, dá
cerca de 53 ms por segundo de jogo a 1× — 5% —, e proporcionalmente mais em
velocidade alta.

Ou seja: desligar por padrão estava certo, e agora por número. Fica ligado
apenas enquanto houver divergência para caçar — e pagou-se sozinho: foi com ele
ligado que a corrida do pathfinding apareceu.

## Os crashes eram o rastreio

Quatro crashes ao longo do projeto, três deles no rollback, todos sem
diagnóstico. Eu tinha registrado "SIGSEGV dentro do GC do mono" e atribuído a
pico de memória — reduzi o pico duas vezes e não resolveu, porque a causa era
outra.

O quarto trouxe pilha, e ela nomeia:

```
Caught fatal signal - signo:7
#3  (wrapper managed-to-native) System.RuntimeType:get_Namespace
#4  RastreioDeRng:Registrar ()
#7  (wrapper dynamic-method) Verse.Rand.get_Int_Patch1 ()
#12 RimWorld.RCellFinder:RandomWanderDestFor
```

A pilha de qualquer sorteio passa pelos métodos dinâmicos que o Harmony gera.
Perguntar `Namespace`, `MethodHandle` ou `MetadataToken` a um método dinâmico é
pedir metadado a algo que não tem — e no Mono isso **não estoura exceção que dê
para pegar**: mata o processo com sinal. O `try/catch` em volta, que eu tinha
posto justamente para "o rastreio nunca derrubar o tick", não serve para nada
contra isso.

Os três crashes anteriores encaixam: aconteciam logo depois do despejo, que
chama `Nomear` e lê `DeclaringType.Name` de cada quadro — mesmo pedido, mesmo
tipo de método, mesmo fim. "No rollback" era coincidência de vizinhança: o
despejo acontece logo antes.

**A correção.** Identidade por `RuntimeHelpers.GetHashCode`, que é o endereço do
objeto e não pergunta nada. Nome só no despejo, cada quadro no seu try/catch,
com `(dinâmico)` ou `(sem metadado)` no lugar quando não der.

De quebra o caminho quente ficou bem mais barato: os 88 µs por sorteio eram, em
boa parte, reflexão.

**A lição.** `try/catch` só protege do que é exceção. Chamada para código nativo
que recebe entrada inválida não é exceção — é sinal, e o processo morre. Quando
o caminho passa por metadado de método dinâmico, a defesa é **não perguntar**,
não é embrulhar em try.
