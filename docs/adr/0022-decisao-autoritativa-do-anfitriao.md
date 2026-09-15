# ADR 0022 — Decisão autoritativa do anfitrião, em vez de determinismo estrito

**Status:** proposto — decisão de rumo, não tomada
**Data:** 2026-09-15
**Origem:** "se a simulação já tiver algumas heurísticas de confiança, como
posição, saúde, dano recebido, itens, não há problema em divergir a simulação em
aspectos — posição da bala, tiro falha pro client, mas depois recebe o dano em
saúde via comando"

## O que se propõe

Parar de exigir que os dois lados simulem **a mesma coisa** e passar a exigir
que decidam **a mesma coisa**. O anfitrião decide; o visitante executa e é
corrigido.

O corte não é entre "importante" e "cosmético". É entre **o que realimenta
decisão** e o que não realimenta:

| aspecto | pode divergir? | por quê |
|---|---|---|
| posição de bala, fumaça, som | sim | já isolamos; não realimenta nada |
| animação, trajeto entre células | sim | mesmo job e mesmo alvo convergem para a mesma chegada |
| dano e saúde | não, mas é **raro** — comando barato |
| **escolha de job** | **não** | é o motor de tudo |

## A conta

Medida em colônia densa (`presetfull`, ~150 pawns, com e sem assalto):

| | taxa | custo |
|---|---|---|
| atribuição de job | 3,6 por tick → 214/s a 1x | ~12,5 KB/s |
| dano | 0,14 por tick → 8/s | desprezível |
| posição (151 pawns, 1 Hz) | — | 1,2 KB/s |
| **total** | | **~14 KB/s** |

A taxa de dano deu **a mesma** com e sem um assalto de 1.500 pontos: combate não
muda a ordem de grandeza, porque dano é raro perto de decisão.

E a deriva sem correção nenhuma, medida no intervalo em que uma corrida real
divergiu e rodou livre por 110 ticks (≈2 min de jogo):

```
  tick  pawns  diferentes  média   máx
   896    151        1      1.00    1.0
   941    151        3      3.52    6.3
  1006    151        5      4.81   13.0
```

Cinco de 151 em lugar diferente, pior caso 13 células. Devagar e contida.

**Ressalva medida:** essa deriva partiu de uma divergência pequena (uma escolha
de job). Uma grande — um assalto chegando por outro lugar — explodiria mais
rápido, e esse caso não foi medido. É ele que decide se a correção pode ser a
1 Hz ou precisa ser mais densa.

## O custo do caminho atual, também medido

O `wf decisoes --forma` cruza os 360 registros de sincronia do Multiplayer com o
que já é comando aqui, e classifica o que falta pela forma do membro:

```
já é comando aqui    11
fora de escopo       95   (8 motivos, cada um citando a seção do desenho)
falta               254
  ├─ bool            11   uma linha no registro de Alternar
  ├─ valor           20   uma linha no registro de AjusteDePawn
  ├─ método         124   intercepção própria, uma a uma
  └─ closure         96   botão cuja ação é lambda
```

Os 96 são o número que importa. Botão cujo efeito é um closure escrevendo num
campo privado não tem fonte para remendar — é preciso **identificar o lambda**,
e é por isso que o Multiplayer tem 227 registros de delegate além dos de método.

E isso é só a frente de comandos. A de determinismo é separada, não tem fim
conhecido, e é refeita a cada versão do RimWorld.

## Por que a troca é legal aqui, e não para o Zetrith

Aceitar divergência custa "os dois jogos deixam de ser o mesmo jogo". Para o
escopo dele — colônia compartilhada permanentemente — isso é inaceitável: os
dois precisam do mesmo save no fim.

Para o nosso, **a cópia do visitante já é descartada** no fim da visita
(ADR 0010). A verdade sempre foi a do anfitrião. Tornar isso explícito não perde
nada que o desenho prometia — é o escopo que autoriza a troca.

## O que muda de forma, não de tamanho

Sob lockstep, cada decisão é um lugar a **interceptar**: onde o clique entra na
simulação. Daí os 96 closures.

Sob decisão autoritativa, o que se transmite é **resultado**, não operação.
`CompRefuelable.TargetFuelLevel` mudou no anfitrião → vai o valor novo. Não
importa que botão o mudou, nem se foi um lambda. Diferença de estado substitui
intercepção de operação, e é aí que os 96 deixam de existir.

O preço dessa substituição é **saber o que mudou** — ou seja, digital por coisa,
que é o passo 1 da ADR seguinte que ainda não existe. Sem localizar diferença,
não há o que transmitir.

## Riscos que não consigo descartar

1. **Reserva.** O sistema de jobs reserva alvos (`ReservationManager`). Se o
   anfitrião decide e o visitante executa, o estado de reserva do visitante
   precisa acompanhar — senão dois pawns recebem trabalho sobreposto e o job
   falha localmente. Não medido.
2. **Necessidades e humor** realimentam decisão. Se derivam no visitante, ele
   mostra números errados; e se ele decidir qualquer coisa sozinho, diverge.
3. **Forçar job não é igual a escolher.** `TryTakeOrderedJob` marca
   `playerForced`, e job forçado não é interrompido pelas mesmas coisas.
   Atribuir 214 por segundo por essa porta mudaria o comportamento. Seria
   preciso um "adote este job" de nível mais baixo.
4. **Ninguém fez.** Não conheço mod de RimWorld com esta arquitetura. Os
   desconhecidos desconhecidos são todos nossos.
5. **Some uma classe de bug e nasce outra.** Em vez de dessincronizar, o
   visitante vê pawn corrigindo posição no meio do passo e dano chegando em quem
   já se moveu. Menos grave — não aborta a sessão — e mais visível.

## Um exemplo que fecha o argumento

A divergência de 15/09, reproduzida com o mesmo número duas vezes:

```csharp
// PreceptComp_UnwillingToDo_Chance.MemberWillingToDo
if (Rand.Value >= chance) return true;   // consome o gerador GLOBAL
```

Uma **pergunta** — "este colono é abstêmio?" — feita de dentro da árvore de
decisão, que sorteia. Um pawn a mais avaliando política de drogas naquele tick
são 32 sorteios de diferença, e a partir daí tudo diverge, inclusive o que nada
tem a ver com droga.

O resultado importa pouquíssimo. O **acoplamento** é que é fatal, e ele existe só
porque o `Rand` é um fluxo único. Sob decisão autoritativa esse caso desaparece
sem ninguém precisar consertá-lo — é o argumento inteiro, num caso real.

## Critério de decisão

Não decidir agora. O que decide é a medição que falta: **a deriva a partir de
uma divergência grande**. Se ela couber numa correção a 1 Hz, a proposta fecha.
Se explodir, o custo de correção sobe e a conta precisa ser refeita.

## O que vale nos dois caminhos

Independente do rumo, não se perde:

- o payload do `Job` inteiro via `Scribe` — necessário para atribuir job de fora
  tanto quanto para transmitir ordem sob lockstep;
- a família **pawn** de comandos (feita) e os 31 herdáveis de graça (11 bool +
  20 valor) — sob lockstep são "o que precisa virar comando", sob decisão
  autoritativa são "o que o anfitrião decide";
- toda a bancada: `wf rodar`, a porta de controle, `wf dirigir`, os rastreios e
  os comparadores. Eles medem simulação, não desenho de rede.

É por isso que dá para continuar trabalhando sem ter decidido.
