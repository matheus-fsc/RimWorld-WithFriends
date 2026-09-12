# ADR 0012 — Efeitos ficam fora do fluxo determinístico

**Status:** aceito
**Data:** 2026-09-11
**Origem:** desync ao mover a câmera + leitura do Multiplayer

## O problema

Dois jogadores na mesma partida, no mesmo tick, com o mesmo estado — mas
olhando para cantos diferentes do mapa — divergiam. Medido: 3 sorteios de
diferença num único tick, com os passos idênticos antes e depois.

A causa está no jogo:

```csharp
public static bool ShouldSpawnMotesAt(this IntVec3 loc, Map map, bool drawOffscreen = true)
{
    if (map != Find.CurrentMap) return false;        // mapa que o jogador vê
    if (drawOffscreen) return true;
    viewRect = Find.CameraDriver.CurrentViewRect;    // posição da câmera
    return viewRect.ExpandedBy(5).Contains(loc);
}
```

Criar um efeito sorteia números. A decisão de criar depende de onde o jogador
está olhando. Logo: **a câmera influencia a simulação.**

Consertar esse método sozinho não bastou — porque não é um método, é uma
categoria. `MoteCounter.Saturated` depende de quantos motes existem *nesta*
máquina. Effecters, sustentadores de som, geradores de cabelo aleatório, o
construtor do `Pawn_DrawTracker`: todos sorteiam, todos são enfeite.

## Como o Multiplayer resolve

Não conserta decisão por decisão. Embrulha a **categoria inteira** em
`Rand.PushState()` / `Rand.PopState()` (`MultiplayerStatic.cs`):

```csharp
var moteMethods  = typeof(MoteMaker).GetMethods(BindingFlags.Static | BindingFlags.Public);
var fleckMethods = typeof(FleckMaker).GetMethods(...).Where(m => m.ReturnType == typeof(void));
var effectMethods = new[] { subSustainerStart, sampleCtor, subSoundPlay,
                            effecterTick, effecterTrigger, effecterCleanup,
                            randomBoltMesh, drawTrackerCtor, randomHair };

foreach (MethodBase m in effectMethods.Concat(moteMethods).Concat(fleckMethods))
    TryPatch(m, randPatchPrefix, finalizer: randPatchFinalizer);
```

O que o efeito sortear é descartado no `PopState`. O fluxo compartilhado nem
sente — **independentemente de o efeito acontecer ou não.**

Eles ainda fazem duas correções pontuais: forçam `makeOffscreen = true` (para
pular a checagem de mapa atual) e neutralizam `MoteCounter.Saturated` por
transpiler.

## Decisão

Adotar as duas camadas, nesta ordem de importância:

1. **Neutralização em lote.** Todo método público de `MoteMaker` e
   `FleckMaker`, os ticks de `Effecter`, e os métodos de som ganham
   prefixo/finalizador que empilha e desempilha o RNG **durante a sessão**.
2. **Decisões determinísticas** onde elas também afetam **ids de coisa**:
   `ShouldSpawnMotesAt` passa a depender só de mapa e célula, e
   `MoteCounter.Saturated` responde `false`. Sem isso, um lado cria um mote
   que o outro não cria, e o contador de ids compartilhado divergiria mesmo
   com o RNG neutralizado.

Por que a camada 1 é melhor do que só a 2: ela **não exige saber quais efeitos
sorteiam**. Mods trazem os seus, versões novas do jogo trazem outros, e o
embrulho cobre todos por construção.

## Sobre a pilha do RNG: não usar, nem aqui

A primeira versão usava `Rand.PushState()`/`PopState()` dentro da mesma
chamada — o uso para o qual a pilha existe, e o que o Multiplayer faz. Pareceu
seguro. Não foi: em jogo apareceram dezenas de

```
InvalidOperationException: Stack empty.
```

A pilha é **estática e compartilhada com o jogo inteiro**: `EnsureStateStackEmpty()`
a esvazia periodicamente, e código de som pode rodar fora da thread principal.
Basta um desses no meio do embrulho para o `PopState` encontrar a pilha vazia.

E a consequência é pior do que o erro: **um finalizador do Harmony que lança
substitui a exceção original do método remendado.** O remédio vira doença — os
dois abortos seguintes muito provavelmente vieram daí, não de determinismo.

A versão final salva e restaura `Rand.StateCompressed` diretamente. Não depende
de pilha, tolera aninhamento, não tem como ficar desbalanceada, e o finalizador
nunca lança.

> Regra que fica: **um finalizador não pode falhar.** Ele roda no caminho de
> exceção do código remendado, e qualquer erro dele apaga a causa original.

## A armadilha maior: criação preguiçosa

Isolar `MoteMaker`, `FleckMaker`, effecters e som (55 métodos) **não bastou**.
Mover a câmera ainda divergia, e com outra assinatura — **+119 sorteios num
único tick**, num lado só. Grande demais para ser fumaça.

A causa:

```csharp
public Pawn_DrawTracker Drawer => drawer ?? (drawer = new Pawn_DrawTracker(this));
```

O objeto de desenho de um pawn é criado **na primeira vez que ele é
desenhado** — ou seja, quando a câmera chega nele. E a construção sorteia.

Dois jogadores olhando para cantos diferentes constroem esses objetos em ticks
diferentes. Não é o efeito que diverge: é a **infraestrutura de desenho
nascendo sob demanda**.

Por isso o embrulho cobre também `Pawn.Drawer`, os construtores de
`Pawn_DrawTracker`, `PawnRenderer` e `PawnTweener`, além de
`PawnStyleItemChooser.RandomHairFor` e `LightningBoltMeshPool.RandomBoltMesh`
— os mesmos que o Multiplayer patcheia.

A regra ganha uma terceira forma: **o que nasce por demanda visual nasce em
momentos diferentes em cada máquina.**

## Consequências

- Fora de sessão nada muda: o jogo sorteia como sempre, e o jogo solo não é
  afetado (§11).
- O catálogo de acoplamentos ganha dezenas de alvos de uma vez. Eles são
  frágeis por natureza (nomes de método de efeito mudam entre versões), mas a
  falha é benigna: um efeito fora do embrulho volta a ser fonte de desync, e o
  traço de RNG aponta o tick.
- Fica registrada a regra que generaliza os três achados desta categoria:
  **estado de visão do jogador — câmera, mapa selecionado, seleção, contadores
  locais — nunca pode influenciar a simulação.**
