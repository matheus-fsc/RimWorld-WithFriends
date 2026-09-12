# ADR 0009 — A visita precisa de RNG isolado

**Status:** aceito
**Data:** 2026-09-11
**Descoberto em:** primeiro teste de visita com dois jogos reais

## Contexto

A primeira visita de verdade entre duas instâncias abortou no tick 0:

```
Os dois lados divergiram no tick 0:
  678f5d3b… calculou sha256:fa9cad9e…
  da8fd226… calculou sha256:8e55ad02…
```

Duas causas, de naturezas diferentes.

**A primeira foi bug de sequenciamento**, já corrigido: o visitante ligava a
comparação ao receber o **arquivo** do mapa, e não ao inserir o mapa na
partida. Comparar antes disso compara dois estados que são legitimamente
diferentes.

**A segunda é estrutural**, e é o assunto desta ADR: mesmo com o mapa
inserido, as digitais **nunca** bateriam.

## O problema

`Verse.Rand` é **um gerador só para a simulação inteira**. A impressão digital
é o contador de iterações desse gerador (ADR 0004).

Durante uma visita, cada cliente tem pelo menos dois mapas ativos: o mapa da
sessão e a própria colônia. A colônia do anfitrião e a do visitante são
diferentes e consomem quantidades diferentes de números aleatórios a cada
tick. O contador global, portanto, diverge por construção — mesmo que o mapa
compartilhado esteja em perfeita sincronia.

Comparar o RNG global numa visita é medir a coisa errada: ele mistura a
simulação compartilhada com a simulação privada de cada um.

## Por que não "o visitante aponta para o RNG do anfitrião"

A ideia natural é deixar o RNG com o dono da colônia e o visitante só consultar.
Ela não funciona, e o motivo vale registrar: consultar significa **receber os
valores pela rede**. Isso é streaming de estado, não lockstep — exatamente o
modelo que a §1 recusa, com sincronia contínua e a latência entrando no meio
de cada sorteio.

Em lockstep ninguém consulta nada: os dois **calculam o mesmo**, porque partem
do mesmo estado com a mesma semente.

Mas a preocupação por trás da ideia está certa — **o narrador do anfitrião não
pode mudar porque alguém o visitou.** A resposta para isso não é o RNG, é a
separação entre simulação e decisão:

| | Quem decide | Como chega ao outro lado |
|---|---|---|
| Simulação do mapa compartilhado | os dois, identicamente | não chega: é recalculada |
| Narrador do anfitrião (raides, eventos) | **só o anfitrião** | vira **comando**, carimbado pelo coordenador |
| Narrador do visitante | ninguém, durante a visita | a colônia dele está parada — ele está fora |

É a regra geral do lockstep: **o que não é determinístico a partir do estado
compartilhado vira comando.** O narrador do anfitrião continua sendo dele, com
a dificuldade dele e a riqueza da colônia dele; o visitante nunca o simula,
só recebe o que ele decidiu.

## Decisão

**O mapa da sessão tem estado de RNG próprio.** Antes de tickar o mapa
compartilhado, o estado global é empilhado e substituído pelo estado da
sessão; ao terminar o tick daquele mapa, o estado da sessão é guardado e o
global volta.

```
PushState → Rand.StateCompressed = estadoDaSessao
    tickar o mapa da sessão
estadoDaSessao = Rand.StateCompressed → PopState
```

A digital da barreira passa a amostrar **só** o estado da sessão, e só dos
mapas que participam dela. O mundo e a colônia privada de cada um ficam de
fora — eles não são compartilhados, e não há o que comparar.

É exatamente o que o Multiplayer faz em `AsyncTime/AsyncTimeComp.cs`
(`randState` por mapa, restaurado em volta do tick). A diferença é o escopo:
lá é permanente e para todos os mapas; aqui é temporário e só para o mapa da
visita.

## O obstáculo: o RimWorld não ticka por mapa

`TickManager.DoSingleTick` chama `MapPreTick` de **todos** os mapas, depois
ticka as *thing lists* — que são globais e misturam coisas de todos os mapas —
e só então segue. Não há um intervalo "o mapa X está simulando" para envolver
com push/pop de RNG.

O Multiplayer resolve isso dando **tick lists próprias a cada mapa**
(`AsyncTime/AsyncTimeComp.cs`: `tickListNormal/Rare/Long` por mapa) e
interceptando `TickList.Add`/`Remove` para rotear cada coisa para a lista do
seu mapa. São ~2.280 linhas, e é o preço de ter tempo assíncrono por mapa
permanentemente.

Para uma sessão delimitada existe um caminho mais barato:

> **Durante a visita, só os mapas da sessão tickam.**

Se nos dois lados a única coisa simulando é o mapa compartilhado, o consumo de
RNG é idêntico por construção.

> **Correção posterior, vinda do primeiro teste com dois jogos:** a primeira
> implementação usava `Rand.PushState(semente)` no início da sessão e
> `PopState` no fim. Não funciona: `Rand.EnsureStateStackEmpty()` roda
> periodicamente e esvazia a pilha, avisando *"Random state stack is not empty.
> There were more calls to PushState than PopState. Fixing."* — e o `PopState`
> do fim estourava dos dois lados.
>
> A pilha do RimWorld é para intervalos curtos (geração de mapa, um incidente),
> não para durar uma sessão. O estado da sessão passou a ser guardado **fora**
> dela e trocado em volta de **cada tick**, no mesmo prefixo/postfixo que já
> segura a barreira. É até melhor: o intervalo determinístico é o tick, e é
> exatamente ele que fica isolado.

Isso também faz sentido na ficção: o visitante está lá, não em casa. A colônia
dele não avança enquanto ele não está olhando — o que, num jogo onde cada um
tem o próprio tempo (§3), é o comportamento natural e não uma limitação.

## Segunda correção: a digital também tem de medir o RNG certo

Alinhados os ticks, a visita ainda abortava no tick 0 — e o motivo era do mesmo
tipo, um nível acima.

A impressão digital lia `Rand.StateCompressed`, o RNG **do processo**. Esse
estado não é salvo no jogo: quando o visitante carrega a partida do anfitrião,
ele traz o contador do próprio processo, que nunca teve relação com o do outro.

> Comparar o RNG do processo entre duas máquinas é comparar duas coisas que não
> têm por que bater. A digital acusava desync porque estava medindo a coisa
> errada — de novo.

A digital passou a amostrar o **estado da sessão** (`RngDeSessao.Estado`), que
nasce da semente que o coordenador entregou igual aos dois e avança junto com o
tick compartilhado. No tick 0, antes de qualquer simulação, os dois valem
exatamente a semente — e portanto batem.

É a terceira vez que o mesmo erro aparece com outra roupa: **medir o estado do
processo em vez do estado do jogo.** Primeiro a metade errada do RNG, depois o
`content_hash` que incluía tempo real, agora o gerador do processo.

## Terceira correção: a digital não pode depender do ritmo de quem relata

Com os ticks alinhados e a semente igual, a visita **ainda** abortava no tick 0.

A opinião acumulava amostras entre um relato e outro: o anfitrião, que passou
segundos parado no tick 0 esperando o visitante carregar, tinha amostrado uma
vez e depois reportava intervalos vazios; o visitante, recém-chegado, reportava
o intervalo com uma amostra. Estados idênticos, resumos diferentes.

> A impressão digital tem de ser **função do estado no tick**, não de quantas
> vezes aquele lado reportou.

A amostragem passou a ser uma por relato, sobre o tick corrente. Dois lados no
mesmo tick com o mesmo estado produzem o mesmo resumo, quantas vezes reportarem.

Detectar *em qual tick* a divergência começou — a granularidade fina do
`ClientSyncOpinion` — continua possível e é assunto de outra hora: exige
amostrar exatamente uma vez por tick simulado, dentro do laço de tick, não no
relato.

## Quarta correção: postfix roda mesmo quando o prefix barra

Com tudo o mais alinhado, a digital **ainda** divergia no tick 0 — e desta vez
a culpa era de como o Harmony funciona.

O freio da barreira é um prefix em `TickManager.DoSingleTick` que devolve
`false` para barrar o tick. O RNG da sessão era trocado num par
prefix/postfix no mesmo método. Só que **postfix roda mesmo quando o prefix
devolve `false`**: o original é pulado, o postfix não.

Resultado: todo tick barrado chamava `DepoisDoTick` sem o `AntesDoTick`
correspondente — e `DepoisDoTick` grava o `Rand` do processo dentro do estado
da sessão. Como os dois lados ficam barrados por tempos diferentes (o
anfitrião espera o visitante carregar), o estado da sessão divergia **antes de
qualquer simulação acontecer**.

A correção é uma bandeira `__state` do prefix para o postfix: só desfaz quem
fez.

Vale como regra geral para o resto do M3: **em todo par prefix/postfix onde o
prefix pode barrar, o postfix precisa saber se houve prefixo de verdade.**

## Consequências

- `IImpressaoDigital` deixa de amostrar `Find.Maps` inteiro. Passa a receber
  quais mapas pertencem à sessão.
- Congelar os mapas fora da sessão vira **requisito** do bootstrap, não
  detalhe: é o que torna o push/pop único suficiente.
- O narrador passa a ser fonte de comandos. `IAplicadorDeComando` ganha seu
  primeiro tipo de comando de verdade: "o narrador do anfitrião decidiu X".
- **O jogo solo não é afetado**: fora de sessão nada disso é acionado, e o
  `Rand` continua exatamente como o RimWorld o usa.
- A semente inicial do RNG de sessão vem de `SessaoInicio.Semente`, que o
  coordenador já sorteia e entrega igual aos dois lados — o campo existia
  desde o começo e agora tem função.

## O que o teste provou de bom

O aborto foi um sucesso operacional, e vale registrar: convite, aceite,
congelamento dos dois lados, transferência do mapa (5,64 MB → 0,33 MB em
654 ms, recebido e verificado em 231 ms), detecção de divergência no tick
exato, aborto, e **rollback dos dois lados para o checkpoint pré-sessão** —
com o estado descartado guardado antes, em cada máquina.

A propriedade de segurança da §2.3 rodou de ponta a ponta, com dois jogos
reais: perdeu-se o encontro, não se perdeu colônia nenhuma.
