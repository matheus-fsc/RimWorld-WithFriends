# ADR 0015 — Remendar as fontes, não os chamadores

**Status:** aceito como direção
**Data:** 2026-09-11
**Origem:** "nada impede a gente de fazer o próprio motor, um core autoral, para
quebrar esse ciclo pesado de mapear tudo"

## Três leituras da proposta, e só uma serve

**1. Escrever nosso próprio Harmony.** Não compra nada. O trabalho do Harmony é
desviar chamadas de método, e ele faz isso bem. A dor nunca foi desviar — foi
**saber qual método desviar**. Um detourer novo não responde essa pergunta, e
custaria os bugs de um.

**2. Nosso motor decidir a simulação, com o jogo virando camada de desenho.**
A simulação **é** o RimWorld: 90 mil métodos, jobs, needs, clima, ideologia,
DLCs. "Decidir" aqui significa reimplementar o jogo. Não é uma rota.

**3. Inverter a superfície de remendo.** Esta serve, e é a boa.

## A inversão

Estivemos remendando **chamadores**:

| remendo | o que ele é |
|---|---|
| `TryGetMeleeVerb` | um chamador que consulta a simulação da interface |
| `GetCameraUpdateRate` | um chamador que lê a câmera |
| `ShouldSpawnMotesAt` | outro chamador que lê a câmera |
| `ForceCompleteScheduledJobs` | um chamador que atravessa a thread |

Chamadores são milhares, mudam de nome a cada versão, e cada um só é descoberto
reproduzindo uma divergência. É o ciclo pesado.

Mas as **fontes** de não-determinismo são poucas, e quase não mudam:

| fonte | por que é local |
|---|---|
| `Find.CameraDriver` | o que **este** jogador está olhando |
| `KeyBindingDef.*` | o teclado **deste** jogador |
| `UnityEngine.Input.*` | o mouse **deste** jogador |
| `DateTime.Now`, `Time.*` | o relógio **desta** máquina |
| `UnityEngine.Random` | fluxo fora do `Verse.Rand` |
| `Find.CurrentMap`, `Find.Selector` | o foco **deste** jogador |
| `Prefs.*`, `DebugSettings.*` | as opções **deste** jogador |

Remendar a fonte neutraliza **todos** os chamadores de uma vez, inclusive os que
ainda não conhecemos e os que aparecerem numa atualização futura.

**Já fizemos isso duas vezes, e funcionou.** `TecladoForaDaSimulacao` não remenda
`TryTakeOrderedJob`: remenda `KeyBindingDef`, e com isso cobre todo chamador que
pergunte por uma tecla dentro do tick. `EfeitosNaoDeterministicos` trata 60
métodos em lote. A proposta é generalizar o que já está provado, não inventar.

A regra fica: **dentro do tick de sessão, fonte local responde valor neutro.**
Fora do tick, tudo normal — a interface continua vendo câmera, teclado e seleção.

## O auditor

A parte autoral de verdade não é o remendo — é saber onde ainda vaza.

`HarmonyLib.PatchProcessor.ReadMethodBody(MethodBase)` devolve o IL de qualquer
método sem precisar de Cecil. Dá para varrer `Assembly-CSharp` inteiro na subida
(com cache por hash do assembly, como o Prepatcher faz) e responder:

> quais métodos alcançáveis a partir de um tick tocam uma fonte local?

Isso é um **relatório**, não uma lista de remendos. Ele transforma a pergunta
"qual método divergiu?" — que só se responde reproduzindo a falha — em "quais
métodos **podem** divergir?", que se responde antes de jogar, e de novo a cada
atualização do jogo, sozinho.

É a diferença entre um catálogo que apodrece e uma regra que se reavalia.

## O que isto não resolve

Honestidade sobre o alcance, porque a proposta promete mais do que ela entrega:

| família | coberta? |
|---|---|
| simulação lê dispositivo de entrada | **sim** — é exatamente o padrão |
| decisão depende da câmera | **sim** |
| interface muda a simulação | parcialmente: neutralizar a fonte evita a leitura, mas `TryGetMeleeVerb` **cacheia** o resultado — cache continua sendo caso a caso |
| simulação em thread paralela | não — não há fonte a neutralizar, é a estrutura do jogo |
| ordem de coleção / mesmo sorteio, escolha diferente | não — a fonte é legítima, o que difere é a ordem |
| ações do jogador virarem comando | não — é escopo, não determinismo |

Ou seja: quebra o ciclo pesado para **duas famílias e meia de cinco**, e essas
são justamente as que mais aparecem. As outras continuam caso a caso.

## Ressalva que só apareceu ao rodar

Fonte `extern` não se remenda. `UnityEngine.Time.deltaTime` é método nativo, sem
corpo gerenciado — o Harmony não alcança. Para essas, volta-se a remendar
chamadores, com transpiler.

A auditoria continua pagando: ela entrega a **lista fechada** de chamadores, que
era exatamente o que não se tinha. Ver `docs/AUDITORIA.md`.

## Decisão

1. **Remendar fontes antes de chamadores**, sempre que houver uma fonte. Um
   remendo de fonte vale mais que dez de chamador, e envelhece melhor.
2. **Construir o auditor de IL** e rodá-lo na subida, com cache. Saída: lista de
   métodos de simulação que alcançam fonte local, para virar remendo de fonte ou
   exceção justificada.
3. **Não** escrever detourer próprio, e **não** perseguir motor de simulação
   próprio.

Combina com a ADR 0014 (fluxo derivado por entidade): aquela reduz o **raio de
dano** de um sorteio a mais; esta reduz o **número de lugares** que podem causar
um. São ortogonais, e nenhuma das duas substitui a outra.
