# Sessão — o que reusar do Multiplayer e como isolar a volatilidade

> Levantamento medido no clone em `referencia/repos/Multiplayer`
> (commit `4a3be27`), contra RimWorld 1.6.4871.
>
> Pergunta que este documento responde: **o que muda muito a cada versão do
> jogo e o que é estável** — e como desenhar a camada de sessão para que uma
> atualização do RimWorld quebre pouco, em lugares conhecidos.

---

## 1. O tamanho do problema, medido

O cliente do Multiplayer tem **602 atributos `[HarmonyPatch]`** e **199
chamadas `AccessTools.Method`**. É a §14.5 em números: cada patch é um
acoplamento a um detalhe interno, e detalhes internos mudam.

Os tipos mais remendados:

| Tipo do jogo | Patches | Natureza |
|---|---|---|
| `Widgets` | 19 | UI |
| `WindowStack` | 14 | UI |
| `Thing` | 11 | simulação |
| `TickManager` | 9 | tempo |
| `Pawn`, `Pawn_JobTracker` | 13 | simulação |
| `LongEventHandler` | 7 | carregamento |
| `Game`, `Map` | 12 | ciclo de vida |

E o tamanho por subsistema:

| Pasta | Linhas | O que resolve |
|---|---|---|
| `Syncing/` | **7.471** | replicar intenção (métodos, campos, closures) |
| `Windows/` + `UI/` | 9.327 | telas |
| `Patches/` | 5.227 | remendos diretos no jogo |
| `Persistent/` | 5.181 | sessões de comércio, rituais, etc. |
| `Factions/` | 3.356 | multifacção |
| `AsyncTime/` | 2.280 | tempo por mapa |
| `Desyncs/` | **1.557** | detecção de divergência |

---

## 2. Classificação por volatilidade

O critério é objetivo: **quantos membros do jogo o subsistema nomeia**, e
quão internos eles são.

### Estável — poucos membros, todos públicos e antigos

**`Desyncs/` (1.557 linhas).** A superfície inteira de API do jogo que ele
toca:

```
Rand.StateCompressed   Rand.Int    Rand.Value   Rand.stateStack
Find.TickManager       Find.Maps   Prefs.DevMode
```

Sete membros. `ClientSyncOpinion` tem **173 linhas** e é a ideia inteira:

```
startTick, commandRandomStates, worldRandomStates,
mapStates[mapId → randomStates], desyncStackTraceHashes, roundMode
```

Isso quase não muda entre versões porque RNG e contagem de tick são
fundações do jogo, não detalhes de implementação. **É o melhor custo-benefício
do Multiplayer inteiro** e o primeiro candidato a reuso.

**`AsyncTime/ITickable.cs` (23 linhas).** A abstração de tempo cabe numa
interface. Toca `TickManager`, `Maps`, `TimeSpeed` — também fundações.

### Intermediário — poucos membros, porém internos

**Contexto de facção** (`Factions/`, 3.356 linhas). A ideia — trocar
`Faction.OfPlayer` conforme quem está agindo — depende de um punhado de
campos internos. O conceito é nativo do RimWorld; o acesso é que é frágil.

**Congelamento e ciclo de vida de mapa.** `MapGenerator`, `MapDeiniter`,
`Scribe`. APIs públicas e estáveis, mas com ordem de chamada sensível.

### Volátil — centenas de membros nomeados

**`Syncing/` (7.471 linhas).** `SyncMethod.Register` aparece **211 vezes** e
`SyncField` **163**: são ~370 membros do jogo nomeados um a um, mais 1.347
linhas de serializadores por tipo em `SyncDictRimWorld.cs`.

Cada assinatura que muda no RimWorld quebra um registro. **É aqui que mora a
manutenção contínua** que o Multiplayer sustenta há anos.

**`Patches/`, `Windows/`, `UI/` (14.554 linhas).** Remendo direto em UI e
comportamento. A UI do RimWorld é reescrita com frequência.

---

## 3. O que este projeto precisa de verdade

Aqui a arquitetura paga dividendo. O Multiplayer sincroniza **tudo** porque é
lockstep permanente sobre um save compartilhado. Nossa sessão é **delimitada**:
uma janela curta, dois jogadores, um punhado de mapas, e cada um comandando
apenas os próprios pawns (§5, multifacção).

| Subsistema do Multiplayer | Precisamos? |
|---|---|
| `Desyncs/` — impressão digital de RNG | **Sim, quase direto.** É o coração da §2.3. |
| `AsyncTime/ITickable` — fila de comandos + velocidade por tickable | **Sim, a ideia.** Uma sessão é um conjunto de tickables sob barreira comum. |
| `Factions/` — contexto por facção | **Sim, a ideia**, para "controlar os próprios colonos no mapa do outro". |
| `Syncing/` — 370 membros registrados | **Não nessa escala.** Ver abaixo. |
| `Persistent/` — sessões de comércio e ritual | Não agora. M5 resolve comércio por consignação (§6). |
| `Windows/`, `UI/` | Não. UI é nossa, e menor. |
| `AsyncTime/TimeControlUI` (484 linhas) | Não. Consenso de pausa (§3 v1) é mais simples. |

**Sobre `Syncing/`:** o Multiplayer precisa replicar qualquer ação possível a
qualquer momento. Nós precisamos replicar apenas o que acontece **dentro da
janela de sessão**. O tamanho do registro vira decisão de escopo, não
consequência inevitável — e cada entrada nova é uma dívida de manutenção
assumida conscientemente, não por acidente.

### O registro, na prática

Começou com dois membros, os dois descobertos por desync medido:

| Comando | Como apareceu |
|---|---|
| `Velocidade` | um lado em Superfast, outro em Normal: evento de 123 sorteios com um tick de diferença |
| `Alistar` | alistar um pawn: +4 sorteios no tick do clique, +111 dois ticks depois, quando ele largou o trabalho e recalculou rota |

O padrão de diagnóstico já está pronto e vale para o próximo: o traço de RNG
mostra o tick exato, e o **tamanho do salto** diz o que foi. Salto pequeno
seguido de salto grande = uma ordem do jogador e suas consequências.

---

## 4. Componentização: três anéis

O princípio: **a volatilidade fica num anel só**, e ela é catalogada.

```
┌─ anel 3 — patches (volátil, catalogado, verificado no boot)
│  ┌─ anel 2 — adaptadores (poucos membros, APIs estáveis)
│  │  ┌─ anel 1 — núcleo (zero RimWorld, testável sem o jogo)
│  │  │   protocolo · transporte · broker · máquina de estados
│  │  │   impressão digital (comparação) · barreira · rollback (decisão)
│  │  └─
│  │   IRelogioDeSessao · IImpressaoDigital · ICongelador
│  │   IAplicadorDeComando · IContextoDeFaccao
│  └─
│   cada patch declarado num catálogo, com alvo e motivo
└─
```

### Anel 1 — núcleo (já existe)

`protocol/`, `transport/`, `server/Sessoes/`. **Zero acoplamento com o jogo**,
83 testes rodando sem RimWorld. Uma atualização do jogo não toca nada aqui.

O que ainda entra: a máquina de estados da sessão no cliente
(`convidado → congelando → simulando → encerrando → abortando`) e a **decisão**
de rollback. Decidir é núcleo; executar é adaptador.

### Anel 2 — adaptadores (o que falta escrever)

Cada porta é pequena e nomeia poucos membros do jogo. Se o RimWorld mudar, o
conserto é aqui, num arquivo por porta:

| Porta | Membros do jogo | Volatilidade |
|---|---|---|
| `IImpressaoDigital` | `Rand.StateCompressed`, `Find.Maps`, `Find.TickManager` | baixa |
| `IRelogioDeSessao` | `TickManager.TicksGame`, `TimeSpeed`, `curTimeSpeed` | baixa |
| `ICongelador` | `Scribe`, `SafeSaver`, `GameDataSaveLoader` | baixa |
| `IAplicadorDeComando` | depende do escopo de comandos que escolhermos | **é a nossa dívida** |
| `IContextoDeFaccao` | `Faction.OfPlayer`, `FactionManager` | média |

### Anel 3 — patches, catalogados

Nenhum `[HarmonyPatch]` solto. Todo patch entra num catálogo com alvo, motivo
e anel afetado, e o mod **verifica no boot** que cada alvo existe:

- alvo sumiu numa atualização → log claro dizendo qual patch e qual método,
  e o recurso correspondente desliga;
- o jogo continua abrindo, e jogar sozinho continua funcionando (§11).

Isso é a §9.1 aplicada ao patching: falha conhecida com motivo legível, em vez
de exceção no meio do carregamento.

---

## 5. Ordem de implementação

1. **`IImpressaoDigital`** — ✅ **feito.** `OpiniaoDeSincronia` no núcleo
   (comparação com motivo legível, 10 testes) e `ImpressaoDigitalRimWorld` no
   anel 2, tocando três membros do jogo. Reusa a ideia do `ClientSyncOpinion`
   e o truque de detecção de modo FP, com atribuição (§16.2).
2. **`ICongelador`** — ✅ **feito.** Congela (pausa + `checkpoint_pre_sessao`
   marcado, enviado ao coordenador), restaura com salvaguarda do estado atual,
   e pede o checkpoint de volta ao servidor quando falta na máquina
   (`colonia.restauracao`, §7.3).
3. **`IRelogioDeSessao`** + tickable de sessão — ✅ **feito.** Barreira exata
   por prefixo em `TickManager.DoSingleTick`, consenso de pausa, agendamento e
   aplicação de comandos no tick carimbado, encerramento e aborto com rollback.
   Validável com dois jogos via `TipoSessao.Ensaio` (ADR 0007).
4. **RNG isolado de sessão** (ADR 0009) + **mapa compartilhado** +
   `IAplicadorDeComando`: bootstrap do mapa do
   anfitrião no visitante e, dali em diante, lockstep de tempo real sobre ele
   (§4, ADR 0007). Escopo mínimo de comandos: *visitar* (§5) — caravana chega,
   pawns do visitante entram no mapa do anfitrião e ele os comanda ali, ao
   vivo.
5. **`IContextoDeFaccao`** — multifacção, quando visitar funcionar.

A ordem é deliberada: cada passo é verificável, e o primeiro que precisa de
dois jogos abertos é o 3.

---

## 6. Armadilhas encontradas na verificação em jogo

### 6.1 A metade errada do estado do RNG

O estado do RNG do RimWorld é `StateCompressed = seed | (iterations << 32)`.

A primeira implementação gravou os **32 bits baixos** — a semente, que só muda
em `Rand.Seed` ou `PushState`, ou seja, quase nunca. O resultado parecia
funcionar: valor não-nulo, estável, diferente entre as duas instâncias. Mas
seria uma impressão digital quase cega: duas simulações divergentes com a
mesma semente teriam o mesmo valor.

A metade certa é a **alta** — o contador de iterações, que anda a cada número
sorteado. É ela que mede quanta simulação aconteceu. O Multiplayer usa
exatamente essa (`(uint)(state >> 32)` em `Desyncs/SyncCoordinator.cs`).

Vale como lição para o resto do M3: a verificação in-game confirmou que a
*leitura* funciona, não que o *valor* tinha significado. Para o próximo passo,
o teste precisa provar que a digital **muda quando a simulação anda** — não só
que ela existe.

### 6.2 `content_hash` identifica bytes, não estado de jogo

Dois congelamentos no **mesmo tick**, com o jogo pausado, produziram hashes
diferentes:

```
congelado no tick 21686 — ... e5d14cfd...
congelado no tick 21686 — ... e7cd198d...
```

Causa: `GameInfo.realPlayTimeInteracting` é serializado no save e acumula
**tempo real**, não ticks. Entre dois cliques passam segundos, e o arquivo
muda mesmo sem nada ter sido simulado.

Consequência prática: `content_hash` identifica **um arquivo**, não um estado
de simulação. Serve para endereçar checkpoint, verificar integridade de
transferência e detectar reenvio do mesmo arquivo — mas **não** para responder
"os dois lados estão no mesmo estado". Essa pergunta é da impressão digital de
RNG, e a separação da ADR 0004 já estava certa por outro motivo.

Efeito colateral: a deduplicação por hash quase nunca dispara na prática.
Não é problema — append-only continua correto, só ocupa mais disco.

### 6.3 Conexão morta que se dizia viva

`TcpClient.Connected` só vira `false` depois que uma operação de I/O falha.
Com o coordenador reiniciado, o cliente continuou reportando `Conectado`, e
todo envio — **inclusive o checkpoint pré-sessão** — foi enfileirado para um
socket morto e sumiu sem uma linha de log.

Foi a falha mais grave até agora, e do tipo exato que este projeto existe para
não repetir: perda silenciosa. Três correções:

1. `Conectado` faz `Poll(SelectRead) && Available == 0` — FIN do outro lado é
   detectado antes de qualquer escrita.
2. Sair do laço de rede sem cancelamento passa o estado para `Falhou` com
   motivo, em vez de continuar dizendo "Conectado".
3. `Enviar` devolve `false` quando não há conexão, e o congelamento avisa o
   jogador na tela: *"ponto de retorno salvo só nesta máquina"*. Um ponto de
   retorno que existe em um disco só não é garantia, é esperança.

## 7. Atribuição

Reuso de **ideia** não exige nada além de cortesia; reuso de **código**, mesmo
adaptado, exige o cabeçalho da §16.2. As ideias catalogadas aqui vêm de
[Multiplayer](https://github.com/rwmt/Multiplayer) (MIT, Copyright (c) 2018
Zetrith). Todo arquivo que derivar de código de lá levará:

```csharp
// Baseado em Source/Client/Desyncs/ClientSyncOpinion.cs
// de rwmt/Multiplayer, commit 4a3be27, MIT, Copyright (c) 2018 Zetrith
// Ver THIRD_PARTY/Multiplayer-MIT.txt
```
