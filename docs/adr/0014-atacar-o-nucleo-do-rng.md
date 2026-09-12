# ADR 0014 — Atacar o núcleo do RNG: o que funciona e o que não

**Status:** aceito (fluxo derivado por entidade), rejeitado (fluxo ditado por terceiro)
**Data:** 2026-09-11
**Origem:** "e se quem decide o rand não for mais o player, e sim um servidor dedicado?"

## A proposta

Em vez de interceptar caso a caso, trocar o núcleo: um servidor dedicado gera os
números e os clientes consomem daquele fluxo. Ninguém sorteia nada localmente,
e não seria preciso mapear o jogo passo a passo.

## Por que não resolve

As últimas cinco divergências que **medimos** não foram sobre os números.

| divergência | o que diferia | sorteio envolvido |
|---|---|---|
| câmera (`GetCameraUpdateRate`) | quantas vezes cada pawn tickava | nenhum, na causa |
| teclado (`IsDownEvent`) | empilhar ordem vs substituir | **nenhum** |
| corrida do pathfinding | resultado do job, por escalonamento de thread | nenhum, na causa |
| `GotoWander` (em aberto) | destino escolhido | **mesma contagem, mesmo estado** |

A última é a que encerra o assunto. Registrado pelo rastreio de estado:

```
J1  #1009 Val  pos 137,116  custo 10.892/20.446  dest 145,96
J2  #1009 Val  pos 137,116  custo 10.892/20.446  dest 137,119
```

Mesmo estado de RNG, mesma quantidade de sorteios, destino diferente. Os dois
lados **já estavam recebendo os mesmos números** — porque mesma semente e mesma
contagem produzem a mesma sequência. Um servidor ditando os números entregaria
exatamente os mesmos, e a escolha continuaria diferente.

### E piora o diagnóstico

Um fluxo compartilhado exige que a **ordem de consumo** seja idêntica nos dois
lados — que é precisamente a propriedade que não temos. É circular: precisa da
garantia que deveria substituir.

Pior: hoje a contagem de iterações **é** o detector. Se um lado consumir um
número a mais de um fluxo ditado, os dois saem de fase em silêncio, e a única
coisa que restaria para perceber seria a digital de estado. Trocaríamos um
aborto barulhento por deriva silenciosa.

## O que funciona: derivar, não compartilhar

O problema real que a pergunta identifica é verdadeiro e é o certo a atacar: em
`Verse.Rand`, **um sorteio a mais em qualquer lugar contamina todo o resto para
sempre**. É a herança de acontecimentos que torna cada caso um caso.

A cura não é centralizar a fonte — é **eliminar a herança**. Em vez de um fluxo
onde a posição importa, cada sorteio passa a ser função pura de um contexto
estável:

```
valor = hash(sementeDaSessao, idDaCoisaQueEstaTickando, tickDeJogo, contador)
```

Com isso:

- um sorteio cosmético a mais dentro do tick do pawn A afeta **o pawn A naquele
  tick**, e nada mais;
- um caminho de interface que consulta a simulação não desloca a colônia inteira;
- divergência deixa de ser global e vira local — e local é diagnosticável.

### O jogo já é quase isso

Lendo `Verse.Rand` de verdade, a mutação é muito mais barata do que eu supunha:

```csharp
private static uint seed;
private static uint iterations;

public static float Value => (float)(((double)MurmurHash.GetInt(seed, iterations++) - -2147483648.0) / 4294967295.0);
public static int   Int   => MurmurHash.GetInt(seed, iterations++);
```

**Não é um gerador com estado interno — é uma função pura de `(seed,
iterations)`.** `MurmurHash.GetInt(uint, uint)` é público e estático. O único
"estado" é o contador.

Então o fluxo derivado não substitui o gerador; só troca as **coordenadas**:

```csharp
// hoje
MurmurHash.GetInt(seed, iterations++)

// derivado
MurmurHash.GetInt(sementeDaSessao ^ (uint)idDaCoisa, (uint)(tickDeJogo * K + contadorLocal++))
```

Mesma função de hash, mesma distribuição, mesma qualidade — muda de onde vêm os
dois números. E o próprio jogo já tem a versão sem estado
(`Rand.ValueAsync(int seed)`, para uso fora da thread principal) e escopos
semeados (`Rand.Block(blockSeed)`, `Rand.PushState(seed)`), então o conceito não
é estranho ao código.

Implementação: prefixo nos dois primitivos durante o tick de sessão, com um
contexto ajustado em `Thing.DoTick` dizendo quem está tickando; sorteios fora de
qualquer coisa caem num fluxo do mapa ou do mundo.

### Três coisas que têm de vir junto

1. **A digital atual morre.** Hoje ela é `iterations` — o contador do fluxo
   global. Se os sorteios param de avançá-lo, o detector some. Precisa virar
   digital de **estado** antes, não depois (o rastreio de estado de pawn é o
   primeiro passo nessa direção).
2. **Escopos semeados têm de ser respeitados.** `PushState(seed)` e `Block()`
   existem para seções reproduzíveis (geração de mapa, entre outras). Derivar
   dentro deles quebraria o que já é determinístico. Regra: só derivar com a
   pilha de estado vazia e dentro do tick de sessão.
3. **Atribuição.** "Quem está tickando" precisa ser barato e correto; fora de
   `Thing.DoTick` (mapa, mundo, incidentes) o contexto é o tick.

### O que isso **não** resolve

Nada das causas que não são sorteio: ritmo de tick pela câmera, leitura de
teclado, corrida de thread, ordem de coleção. Essas continuam exigindo um caso
cada.

O ganho é outro, e é grande: **raio de dano**. Os 96 sorteios da ideologia
(`PreceptComp_UnwillingToDo_Chance`, um `Rand.Value` por consulta) viraram, na
prática, uma explosão a partir de um tick de assimetria. Com fluxo derivado,
aquilo teria ficado contido num pawn.

## A outra metade da pergunta: quebrar a cada atualização

Esta é a parte em que a ideia de atacar o núcleo **realmente ganha**, e por um
motivo diferente do determinismo.

Um remendo é acoplamento a um detalhe interno do jogo (§14.5). Quanto mais
remendos, mais superfície para uma atualização quebrar — o Multiplayer sustenta
602 com esforço contínuo. Então a pergunta certa não é só "quantos casos", é
**quantos pontos de acoplamento, e quão estáveis**.

Comparando as duas rotas por esse critério:

| rota | pontos de acoplamento | estabilidade |
|---|---|---|
| interceptar caso a caso | ~50–60, em métodos internos específicos | baixa: `TryGetMeleeVerb`, `GetCameraUpdateRate`, `ForceCompleteScheduledJobs` mudam de nome, assinatura e existência |
| fluxo derivado por entidade | 3: `Rand.Value`, `Rand.Int`, `Thing.DoTick` | alta: são a espinha do jogo — e os dois primeiros **já são remendados hoje** pelo rastreio, então a remendabilidade está provada, não suposta |
| fluxo ditado por terceiro | os mesmos ~50–60 **mais** o transporte do fluxo | pior que a primeira |

Três pontos estáveis contra sessenta instáveis é o argumento mais forte a favor
do fluxo derivado — mais forte, inclusive, que o argumento de determinismo.

Vale a ressalva: as causas que não são sorteio (câmera, teclado, thread, ordem)
continuam sendo remendos frágeis, um a um. O fluxo derivado não os elimina; ele
evita que **outros sessenta** precisem existir.

E o amortecedor já está no lugar para os que restarem: `CatalogoDePatches`
confere cada alvo na subida, e desde o incidente do `PatchAll` cada classe é
remendada isoladamente. Alvo que sumiu numa atualização desliga o recurso dele
com log legível — não derruba o mod, não trava o jogo, e jogar sozinho nunca é
bloqueado (§11).

## Decisão

1. **Fluxo derivado por entidade** — aceito como direção. É o que torna o
   determinismo uma propriedade local em vez de global, e é a resposta certa
   para "não mapear o jogo inteiro".
2. **Fluxo ditado por terceiro** — rejeitado. Não corrige nenhuma causa medida,
   exige a garantia que deveria substituir, e transforma aborto em deriva
   silenciosa.
3. **Ressincronizar em vez de abortar** (`docs/IDEIAS.md`) continua valendo e
   combina com (1): raio de dano menor torna a ressincronização barata e rara.

Ordem sugerida: ressincronização primeiro (o maquinário já existe e já roda em
toda visita), fluxo derivado depois, porque ele muda como toda a simulação
sorteia e merece uma bancada estável para ser medido.
