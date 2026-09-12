# ADR 0011 — Ponto de encontro: os dois lados recarregam

**Status:** aceito
**Data:** 2026-09-11
**Origem:** leitura do Multiplayer + divergência medida na primeira visita real

## O problema

A visita entrou, a barreira liberou, os dois simularam juntos — e divergiram
já no **tick 1**. O retrato dos dois lados era idêntico em tudo (tick, mapa,
pawns, facção, clima, narrador, interruptores de debug) menos numa linha:

```
anfitrião  11257 things
visitante  10366 things      → 891 a menos (7,9%)
```

E o consumo de aleatoriedade era consistentemente **3,5× maior** no anfitrião.

A causa: **um jogo vivo e o mesmo jogo recém-carregado não são idênticos.** O
anfitrião roda há minutos — tem motes em voo (fumaça, faíscas, números de
dano), caches quentes, listas em outra ordem. O visitante carregou um save, que
por definição não traz nada disso.

Não é bug de serialização: motes **não são salvos** porque são cosméticos. Não
existe conserto que faça dois estados assim coincidirem.

## Como o Multiplayer resolve

Vale registrar, porque a resposta deles é elegante e não é a que eu esperava.

Para abrir um ponto de entrada, o Multiplayer emite
`CommandType.CreateJoinPoint` — **um comando**, que portanto passa pela fila e
é executado por todos no mesmo tick. Ao executá-lo, cada cliente chama
`SaveLoad.SaveAndReload()`:

```csharp
if (cmdType == CommandType.CreateJoinPoint)
    LongEventHandler.QueueLongEvent(CreateJoinPointAndSendIfHost, "MpCreatingJoinPoint", false, null);
```

Ou seja: **todo mundo salva e recarrega**, inclusive quem já estava jogando. O
snapshot enviado a quem entra é exatamente o estado que todos passaram a ter.

Ninguém compara um jogo vivo com um restaurado, porque depois do ponto de
encontro **não existe mais jogo vivo** — só restaurados.

## Decisão

**O anfitrião recarrega junto com o visitante.**

```
congelar → salvar → mandar o save → os DOIS carregam o mesmo save → barreira → visita
```

Parece desperdício: o anfitrião já tem o jogo na memória. Não é — é o que
elimina a assimetria que torna o determinismo impossível.

Custo: o anfitrião passa a pagar o mesmo carregamento que o visitante
(medido: 3 a 7 s). Cabe no princípio da ADR 0008 — um carregamento anunciado é
aceitável — e tem a virtude de ser **simétrico**: os dois esperam o mesmo.

## Correção irmã: cosmético não entra no estado determinístico

Independente disso, motes passaram a tickar com o RNG **do processo**, não com
o da sessão. Fumaça não deve mover o estado compartilhado nem que os conjuntos
coincidissem.

O princípio vale além dos motes: **o que é cosmético não pode influenciar a
simulação compartilhada.**

E ele tem uma segunda metade, descoberta depois: não basta o cosmético não
*reagir* à simulação — ele não pode **nascer** de estado não compartilhado.
`GenView.ShouldSpawnMotesAt` decide criar um efeito olhando para
`Find.CurrentMap` e para `Find.CameraDriver.CurrentViewRect`. Criar o efeito
sorteia números. Resultado: dois jogadores olhando para cantos diferentes do
mesmo mapa divergiam.

Formulação geral: **estado de visão do jogador — câmera, mapa selecionado,
seleção — nunca pode influenciar a simulação.**

## Outra coisa que o Multiplayer faz e vamos precisar

No mesmo trecho, ao executar comandos:

```csharp
finally
{
    DebugSettings.godMode = prevGodMode;
    Prefs.data.devMode = prevDevMode;
}
```

Eles **forçam godMode/devMode a um valor conhecido durante a execução de
comandos** e restauram depois. São interruptores que mudam caminhos de
simulação e não vivem no save — exatamente a categoria de coisa que quebra
determinismo em silêncio.

Nos nossos testes eles estavam iguais nos dois lados por sorte. Quando a visita
estabilizar, isso vira requisito.

## Como o Multiplayer trata o relógio (para referência futura)

O modelo de tempo deles é diferente do nosso e vale ter anotado:

| | Multiplayer | Aqui |
|---|---|---|
| Ritmo | servidor tem um `gameTimer` próprio e publica `tickUntil`; clientes simulam **até** ele | barreira: ninguém passa do mínimo relatado + 10 |
| Buffer | clientes ficam de propósito 3–7 ticks atrás, acelerando/desacelerando para manter folga | sem buffer: cada liberação é uma ida e volta |
| Quem espera | servidor só avança se alguém está a <40 ticks e **ninguém** a >90 | servidor libera pelo mais lento |
| Entrada | quem entra **simula para frente** até `tickUntil`, com barra de progresso | os dois param e começam juntos |
| Pausa | `FreezeManager`, o anfitrião manda (com espera máxima de 10 s) | consenso: qualquer um pausa |

O buffer deles é a diferença que mais importa em latência: sem ele, cada
avanço custa uma ida e volta. Com sessões delimitadas e dois jogadores na mesma
máquina isso não aperta, mas numa visita pela internet vai apertar — e a
solução já está documentada aqui.
