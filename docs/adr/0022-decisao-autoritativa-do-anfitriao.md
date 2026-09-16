# ADR 0022 — Decisão autoritativa do anfitrião, em vez de determinismo estrito

**Status:** proposto, com escopo reduzido pela medição de 16/09/2026
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

## Critério de decisão — medido em 16/09/2026

A medição que faltava foi feita: `wf dirigir deriva`, dois lados sob a mesma
barreira, digital calada (`--semdigital`, para que a divergência não seja
detectada nem desfeita), e um assalto de 2000 pontos injetado **só no árbitro**
no passo 25. Depois disso, ninguém toca em nada por cinco minutos; os dois lados
despejam o estado dos pawns a cada 20s e `tools/deriva.py` compara a posição dos
pawns COMUNS aos dois lados, tick a tick.

**Controle, primeiro.** A mesma corrida sem injeção: 380 ticks, 122 pawns,
**0 diferentes, distância zero**. O instrumento mede o que diz medir.

**Com a divergência injetada:**

```
   tick  comuns  só A  só B  difer.      %   média    máx
    766     154     0     1     143  92.9%    6.37   16.6
   2183     154     0     1     151  98.1%    8.21   27.2
   4690     154     0     1     153  99.4%   10.05   46.2
   7247     154     0     1     153  99.4%   10.20   47.4
   9065     154     0     1     151  98.1%   37.16   98.2
  11397     125    13    14     125 100.0%   36.82  161.6
  14512     115    16    17     115 100.0%   28.78  119.4
```

Três coisas, e a terceira é a que decide.

**A deriva não é gradual: ela é imediata.** No primeiro tick medido — 766
passos, uns treze segundos de jogo depois da injeção — **93% dos pawns já estão
em posição diferente**, com média de 6,4 células. Não há janela em que poucos
divirjam e o resto acompanhe. O `Rand` é um fluxo único: uma decisão a mais de um
lado desloca todos os sorteios seguintes, e todo pawn que sorteia qualquer coisa
sai do lugar. A hipótese de "diverge em aspectos" não sobrevive ao primeiro
segundo.

**A distância, essa sim, é modesta e estável.** Média de 6 células que vira 10 e
fica em 10 por dois minutos inteiros (ticks 4000–7000). Não explode; oscila. Só
depois do tick 9000 pula para 37 — e cai de novo para 29 no fim. Corrigir posição
a 1 Hz é barato em bytes: 150 pawns × (id + x + z) ≈ 1,8 KB/s, oito vezes menos
que os 14 KB/s já medidos para a decisão autoritativa.

**O que não cabe em correção é o conjunto, não a posição.** Os pawns comuns caem
de 154 para 115 ao longo da corrida: ao fim, 16 existem só de um lado e 17 só do
outro. Isso não é afastamento, é divergência estrutural — pawns morrem de um lado
e não do outro, e nenhuma correção de posição conserta um pawn que não existe. É
aqui que a conta de "corrigir o que diverge" deixa de ser sobre largura de banda.

### A decisão

A proposta **não fecha na forma em que foi escrita**, e não é por causa da
largura de banda — essa passa folgada. É porque a premissa "divergir em aspectos"
não descreve o que acontece: com um `Rand` único, uma divergência grande vira,
em treze segundos, uma divergência em tudo.

O que sobrevive da proposta é a parte autoritativa sobre **existência e saúde**,
não sobre posição: quem vive, quem morre, quanto dano levou. Essas são poucas,
raras e caras de errar — exatamente o perfil que justifica autoridade. Posição de
bala e passo de pawn podem divergir porque são baratas de corrigir; mas só
divergem barato enquanto o conjunto de pawns for o mesmo dos dois lados.

Fica **proposto**, com o escopo reduzido: autoridade sobre existência e saúde,
lockstep para o resto. A medição a fazer agora é outra: quanto tempo leva, sob
lockstep com comandos cobrindo as famílias que faltam, para o conjunto de pawns
divergir sozinho — se nunca divergir, a autoridade sobre existência é seguro, não
arquitetura.

## O que vale nos dois caminhos

Independente do rumo, não se perde:

- o payload do `Job` inteiro via `Scribe` — necessário para atribuir job de fora
  tanto quanto para transmitir ordem sob lockstep;
- a família **pawn** de comandos (feita), a **zona/área** (feita) e os herdáveis
  de graça — sob lockstep são "o que precisa virar comando", sob decisão
  autoritativa são "o que o anfitrião decide";

  > **Correção de 16/09/2026: eram 6, não 31.** A conta de "31 herdáveis de graça
  > (11 bool + 20 valor)" saiu de um classificador que não separava **campo** de
  > **propriedade**. Os dois guardam um `bool`, mas só a propriedade tem setter —
  > e setter é onde se remenda. Campo público é escrito direto, quase sempre de
  > dentro da lambda de um botão, e aí não há fonte: é o caso caro, o mesmo dos
  > 227 registros de lambda do Multiplayer. Depois de separar os dois: 3
  > propriedades `bool`, 3 de valor, e 23 campos que não são de graça nenhuma.
  > O classificador agora lê o metadado do assembly em vez de um decompilado
  > (`wf decisoes --forma`), e responde certo.
- toda a bancada: `wf rodar`, a porta de controle, `wf dirigir`, os rastreios e
  os comparadores. Eles medem simulação, não desenho de rede.

É por isso que dá para continuar trabalhando sem ter decidido.
