# Arquitetura — Mod de Multiplayer para RimWorld

> Documento de arquitetura. Define **o que** o sistema é e **por quê**.
> Decisões de implementação ficam em ADRs separados.

---

## 1. Princípio central

> **Assíncrono por padrão. Síncrono por encontro.**

Cada jogador simula a própria colônia no próprio ritmo. Quando dois jogadores precisam interagir de verdade, entram numa **sessão** — uma janela curta, opt-in e determinística, envolvendo só os participantes e só os mapas em jogo.

Fora de sessão, **não existe sincronia por tick**. Nenhuma.

Isso resolve o dilema entre os dois mods existentes:

| | RimWorld Together | Multiplayer (Zetrith) | Este projeto |
|---|---|---|---|
| Mundo | compartilhado, assíncrono | um só, lockstep | compartilhado, assíncrono |
| Colônias | separadas | uma só | separadas |
| Tempo | independente | global | independente, **compartilhado só em sessão** |
| Risco de desync | não existe | cresce com jogadores e tempo | **limitado à duração da sessão** |
| Jogar sozinho | sim | não | sim |
| Interação real | fraca | total | **total, dentro do encontro** |

O ponto-chave: determinismo total do jogo é caro e frágil. Determinismo **dentro de uma janela curta e delimitada** é viável — e recuperável, porque existe um ponto de retorno imediatamente antes dela.

### 1.1 Escala alvo

O nome do projeto é a premissa: **RimWorld com amigos**. Um grupo pequeno que se conhece, não um servidor com oitenta jogadores tentando interagir ao mesmo tempo.

Isso não é modéstia — é o que autoriza quase todas as decisões caras deste documento:

- **A visita pode custar.** Transferir um mapa e sustentar lockstep é pesado. Num grupo pequeno isso acontece algumas vezes por sessão de jogo, com aviso e consentimento — não é tráfego de fundo.
- **O custo é sempre visível.** Nada de sincronia surpresa: o jogador recebe convite, **aceita**, e vê uma tela de sincronização. Se vai demorar, ele sabe por quê e por quanto.
- **Fila de um encontro por vez basta.** Não é preciso resolver N sessões simultâneas, arbitragem complexa ou balanceamento de carga.
- **Confiança é premissa.** O coordenador não valida regra de jogo (§10) porque os participantes se conhecem. Anticheat não é objetivo.

Projetar para oitenta jogadores anônimos mudaria tudo — e daria um mod pior para o caso que realmente importa.

---

## 2. Camadas

### 2.1 Mundo (assíncrono, autoridade do servidor)

Estado compartilhado do planeta: tiles, assentamentos, estradas, rios, objetos de mundo, facções, mercado.

Modelado como **log de eventos append-only**. O servidor ordena; cada cliente consome do próprio cursor, no próprio tempo. Não existe "estado do mundo" que se sobrescreve — existe uma sequência de fatos.

```
evento := { seq, autor, tipo, payload, timestamp_logico }
```

Cada cliente guarda `world_cursor`. Ao conectar, pede tudo a partir dele. A ordenação vem do servidor, então conflito de ordem não existe por construção.

### 2.2 Colônia (local, privada, autoridade do cliente)

O save da sua colônia é **seu**. O cliente é a autoridade sobre a própria simulação. O servidor guarda checkpoints para durabilidade, **não** para mandar no seu jogo.

Essa separação é deliberada e vem de uma falha real (ver §14.1 e §14.3): quando o servidor tenta ser autoridade sobre um save que ele não simula, os dois lados divergem em silêncio.

### 2.3 Sessão (síncrona, lockstep, delimitada)

Uma sessão é um acordo temporário entre N jogadores (v1: N=2) para simular em conjunto um subconjunto de mapas.

```
convite → aceite → congelar → trocar estado → barreira de tick → LOOP → encerrar → commit
```

Dentro da sessão vale lockstep clássico: só comandos trafegam, ambos simulam. Fora dela, nada disso existe.

**Propriedade de segurança:** o congelamento antes da sessão cria um `checkpoint_pre_sessao`. Se der desync, aborta e volta os dois para o checkpoint. Perde-se o encontro, nunca a colônia.

---

## 3. Modelo de tempo

Dentro da sessão o tempo é **negociado**, nunca imposto.

**v1 — consenso de pausa.** O tempo avança apenas enquanto todos os participantes estiverem despausados. Qualquer um pausa, o tempo para para todos. Sem contagem regressiva, sem timer. Simples e previsível.

**v2 — rodadas.** Blocos de N ticks acordados. Um jogador propõe, o outro aceita, ambos simulam o bloco. É a base para PvP.

**v3 — árbitro de tempo.** Para PvP assimétrico: janelas com limite real e resolução automática ao expirar.

Fora de sessão, cada um controla o próprio tempo sem qualquer restrição.

---

## 4. Regra de presença

> **Você só enxerga o que você alcança.**

Para ver a colônia do outro você precisa de presença física lá: caravana, colono, ou uma unidade sua no mapa dele.

**A visita é lockstep em tempo real.** Os dois jogadores simulam o mesmo mapa, ao mesmo tempo, travados no mesmo tick — o visitante não assiste a nada nem recebe atualizações periódicas: ele joga ali, junto, e o que ele faz acontece no mapa do anfitrião no instante em que acontece.

Para isso o estado precisa estar dos dois lados. Transferi-lo é o que **inicia** a visita; não existe caminho alternativo, porque uma colônia vivida não é derivável do tile do planeta. Mas transferir é só o começo: o que sustenta a visita é a sincronia contínua enquanto ela dura.

**O que trafega é a partida do anfitrião, não o mapa isolado** — medido e decidido na [ADR 0010](adr/0010-a-visita-carrega-a-partida-do-anfitriao.md). Um mapa referencia ideologias, políticas, facções e relações que vivem na partida, não nele; sozinho ele abre e não se reconecta a nada. O visitante joga **dentro da partida do anfitrião**, como facção distinta (§5), e volta à própria colônia ao sair.

A regra, então, não é sobre o que trafega. É sobre **quando**:

> **Sincronia em tempo real existe durante a visita, e só durante a visita.**

Essa é a região sensível do sistema inteiro:

- Sincronia contínua significa risco de desync contínuo. O custo de divergir cresce com o tempo e com o número de jogadores — é exatamente o que torna o lockstep global frágil (§1).
- Numa janela curta e delimitada esse custo é aceitável, porque divergir é recuperável: existe um ponto de retorno imediatamente antes dela (§2.3).
- Fora da visita não há sincronia nenhuma, porque não há mapa compartilhado — só o log de eventos do mundo, assíncrono por construção (§2.1).

É o mesmo mecanismo do lockstep global, com o escopo virado do avesso: em vez de aceitar desync permanente para ter interação permanente, aceita-se interação delimitada para que o desync seja sempre recuperável.

O que o RT fez errado não foi transferir mapa: foi transferir mapa **como recurso avulso de visualização** — uma foto, fora de qualquer janela sincronizada, sem caminho incremental (medido: **1739 ms** de serialização para ~1 MB; ver [RT #273] — duplicate load IDs, falha ao descarregar mapa, corrupção de cliente). Foto não é visita.

Sem presença, você vê o assentamento no mapa-mundo: nome, riqueza estimada, status online. Nada mais.

### 4.1 A visita termina, e a partida visitada vai embora

Quando o encontro acaba, o visitante volta para a própria colônia — restaurando o `checkpoint_pre_sessao` — e **a partida do anfitrião é descarregada do jogo dele**. Não fica cópia, não fica cache, não fica nada para reconciliar depois.

O que volta com ele é a **caravana**: os pawns que foram, como estão agora, e o que carregam. Isso é transferência de caravana, não reconciliação de estado — a diferença entre "estes pawns voltaram assim" e "concilie duas versões da minha colônia".

Entrar de novo significa sincronizar de novo. É mais caro em banda e infinitamente mais barato em corretude: um estado guardado do lado do visitante envelheceria a cada tick que o anfitrião jogasse sozinho, e reconciliar dois estados divergentes é precisamente o problema que este projeto recusa (§1).

O custo aparece na hora certa e com consentimento:

```
convite → o jogador ACEITA → tela de sincronização → visita → fim → descarrega
```

Nunca acontece do nada. O jogador sabe que vai esperar, sabe por quê, e escolheu esperar.

### 4.2 O planeta não se transfere — se gera igual

O mapa-mundo é a exceção barata: ele é **determinístico a partir da semente e das opções de geração**, todas escolhidas pelo jogador na criação do mundo. Dois jogadores com a mesma semente e a mesma cobertura têm o mesmo planeta, tile por tile.

Então o planeta nunca trafega. O que trafega é a **identidade** dele — um hash das variáveis de geração — declarada por cada cliente ao sincronizar o cursor.

Isso é requisito, não otimização: eventos de mundo carregam **índice de tile**, não coordenada. Em outro planeta o mesmo índice aponta para outro lugar, ou não existe. Índice de tile inválido não dá erro de validação — entra direto no `WorldGrid` e quebra o jogo longe da causa.

**A identidade pertence ao fato, não ao coordenador.** Cada evento do log sabe em que planeta ele vale, e cada cliente só recebe os do seu. Vários planetas coexistem no mesmo coordenador sem que nenhum recuse os outros — quem está num planeta simplesmente não vê quem está em outro, que é a verdade da situação (ADR 0021).

Quem está sozinho no planeta dele continua logado, jogando e guardando checkpoint; o coordenador só o avisa, uma vez, **com a descrição do planeta dos outros** — semente, cobertura, chuva — que é o que permite regerar e encontrar (§8, §11).

---

## 5. Interações

Todas herdam o mesmo fluxo de sessão. Uma implementação, vários usos.

| Interação | Participantes | Mapas na sessão | Observações |
|---|---|---|---|
| **Visitar** | visitante + anfitrião | mapa do anfitrião | visitante controla os próprios pawns |
| **Ajudar na defesa** | defensor + aliado | mapa do defensor | aliado traz e comanda os próprios colonos |
| **Ataque conjunto** | N atacantes | mapa alvo | multifacção no mesmo mapa |
| **PvP** | atacante + defensor | mapa do defensor | exige modelo de tempo v2+ |
| **Comércio presencial** | ambos | mapa do anfitrião | caravana fisicamente presente; opcional, o comércio normal é pelo planeta (§6) |

**Multifacção** (ideia boa do Zetrith): dentro de uma sessão cada jogador controla apenas os próprios pawns, como facções distintas no mesmo mapa. Isso já é conceito nativo do RimWorld — não precisa inventar.

**Para que servem as visitas.** Ajuda humanitária e militar: mandar tropas, socorrer um assentamento sob ataque, atacar junto. São eventos — começam, acontecem e terminam, e no fim o jogador volta para a base principal e o mapa é descarregado (§4.1).

O que **não** é motivo para sessão: comércio e troca de bens, que acontecem pelo planeta sem lockstep nenhum (§6). Se dá para resolver de forma assíncrona, resolve-se de forma assíncrona — sessão é para quando as duas pessoas precisam estar no mesmo lugar ao mesmo tempo.

---

## 6. Comércio

**Comércio não precisa de sessão.** Ele acontece pelo planeta — camada de mundo, assíncrona, sem lockstep e sem transferência de mapa. É a interação mais frequente entre jogadores e também a mais barata, e as duas coisas juntas não são coincidência: o que precisa ser caro é o encontro presencial, não a troca de bens.

**Mas nada impede o comércio presencial.** Se dois jogadores quiserem, a caravana chega ao mapa do outro numa visita e as mercadorias mudam de mão pela mecânica que o jogo já tem: descarregar no chão, deixar para trás, entregar em mãos. Não é o caminho eficiente — é o caminho **vivido**, e num simulador de histórias isso vale por si. Quem joga para narrar, e quem grava vídeo, provavelmente vai preferir esse.

As duas formas convivem porque respondem a perguntas diferentes: o catálogo resolve *"preciso de aço"*; a caravana presencial resolve *"quero que essa entrega tenha acontecido"*.

Crítica ao modelo do RT: o mercado dele é um menu paralelo que ignora a mecânica do jogo. Você anuncia num diálogo e o item some do nada.

**Proposta: consignação com saída física.**

1. Você marca itens numa **zona de consignação** (mecânica de beacon).
2. Os itens **saem fisicamente do seu mapa** e vão para escrow no servidor.
3. Aparecem no catálogo, visíveis para todos, com o seu preço.
4. O comprador paga; os bens chegam no **drop spot** dele.
5. A prata volta para o seu escrow e chega na próxima vez que você conectar.

Por que funciona bem:

- É **assíncrono por natureza** — não exige ninguém online.
- Usa mecânica existente (beacon, drop spot), não um menu inventado.
- O item sai do mapa de verdade, então não existe duplicação nem "vendi e ainda tenho".
- O escrow é estado simples: quem, o quê, quanto, preço.

**Modos por disponibilidade:**

| Situação | Disponível |
|---|---|
| Ambos online, caravana presente | negociação direta em sessão, barganha completa |
| Ambos online, sem presença | transferência combinada |
| Um offline | consignação / catálogo |

---

## 7. Durabilidade e integridade

Esta seção existe porque perda silenciosa de progresso é a pior falha possível — e foi o que motivou o projeto.

### 7.1 Regras invioláveis

1. **Checkpoint nunca sobrescreve.** Append-only, endereçado por hash.
2. **Todo checkpoint carrega metadados verificáveis:**
   ```
   { game_tick, world_cursor, mod_set_hash, content_hash, wall_clock }
   ```
3. **`game_tick` deve ser monotônico.** Se o cliente envia checkpoint com tick menor ou igual ao anterior, o servidor **aceita, marca como suspeito e alerta em voz alta**. Nunca aceita em silêncio.
4. **Heartbeat carrega `(tick, content_hash)`.** Se o hash não muda entre heartbeats enquanto o tick avança, algo está errado — avisa na hora.
5. **Nome de arquivo nunca é identidade.** Identidade é `(player_id, colony_id)`. Nome de arquivo é detalhe de armazenamento.

A regra 3 sozinha teria detectado em minutos o incidente que custou 5 horas de progresso, em vez de aparecer só no dia seguinte.

### 7.2 Retenção

Escadinha, não janela fixa:

```
últimas 24h  → a cada hora
últimos 7d   → a cada dia
sempre       → os 3 checkpoints marcados como "pré-sessão"
```

Motivo: 6 backups de hora em hora dão 6 horas de histórico. Numa investigação real isso quase expirou antes de a causa ser encontrada.

### 7.3 Autoridade

**O cliente é autoridade sobre a própria colônia.** O servidor nunca impõe uma versão do save por cima.

Se divergirem, o sistema **para e pergunta**, mostrando tick e hash dos dois lados. Nunca escolhe sozinho.

---

## 8. Compatibilidade de mods

O RT trata todos os mods igual: lista exata, ordem exata, settings iguais. Resultado prático: um mod cosmético a mais bloqueia o login.

**Proposta: classificação por impacto.**

| Classe | Exige igualdade | Exemplos |
|---|---|---|
| `world` | sim, sempre | adiciona defs, facções, biomas |
| `session` | só para entrar em sessão | altera combate, pawns, jobs |
| `client` | nunca | UI, sons, texturas, ferramentas |

A classificação é declarada pelo autor do mod, e o servidor pode sobrescrever. A verificação acontece **na entrada da sessão**, não no login — assim divergência de mod nunca impede alguém de jogar sozinho.

---

## 9. Protocolo

### 9.1 Regras

- **Versão explícita** no handshake, com flags de capacidade.
- **Mensagem desconhecida é ignorada com log**, nunca derruba a conexão.
- Compatibilidade **nunca** derivada de reflexão sobre nomes de classe.
- Toda recusa de conexão traz **motivo legível**, não desconexão muda.

O RT compara o conjunto de nomes de packet managers via reflection. Consequência: qualquer manager novo no servidor quebra todos os clientes existentes, e a mensagem de erro não diz o que houve.

### 9.2 Famílias de mensagem

```
sessao.*     convite, aceite, recusa, inicio, comando, barreira, fim, aborto
mundo.*      evento, sincronizacao_cursor
colonia.*    checkpoint, heartbeat, restauracao
mercado.*    anuncio, compra, entrega, escrow
sistema.*    handshake, versao, capacidades, erro
```

---

## 10. Papel do servidor

**Coordenador, não simulador.** O servidor nunca roda lógica de RimWorld — ele não tem as assemblies do jogo e não deve ter.

**Responsabilidades:**

- Registro de jogadores, colônias e assentamentos
- Log de eventos de mundo (ordenação)
- Broker de sessões (convites, barreiras, arbitragem de aborto)
- Escrow do mercado
- Armazenamento de checkpoints
- Presença (quem está online)

**Não-responsabilidades:**

- Simular colônia
- Ser autoridade sobre save de jogador
- Validar regras de jogo

Consequência boa: o servidor fica pequeno, testável sem o jogo, e roda em qualquer lugar.

---

## 11. Degradação offline

| Recurso | Amigo offline |
|---|---|
| Jogar a própria colônia | ✅ normal |
| Ver o mundo e assentamentos | ✅ |
| Mercado / consignação | ✅ |
| Caravana rumo à colônia dele | ✅ enfileira e chega quando ele voltar |
| Visitar, ajudar, atacar | ❌ exige sessão |

O jogo solo nunca é bloqueado por ausência de ninguém. Isso é requisito, não consequência.

---

## 12. Estrutura do repositório

```
/docs
  ARQUITETURA.md        este documento
  PROTOCOLO.md          catálogo de mensagens, versionado
  /adr                  decisões arquiteturais numeradas
/protocol               definições compartilhadas (fonte da verdade)
/server                 coordenador — sem dependência do RimWorld
/client                 mod do RimWorld
  /world                camada de mundo
  /session              camada de sessão e lockstep
  /colony               checkpoints e integridade
  /ui
/tools                  inspetor de save, replay de sessão, gerador de carga
/tests
```

`/protocol` é **fonte única de verdade** e não depende nem do servidor nem do cliente. A ausência disso no RT é o que torna impossível contribuir só com uma das metades.

---

## 13. Roadmap

**M0 — Fundação.** Protocolo versionado, handshake, servidor coordenador, registro de jogadores. Sem jogo ainda.
*Critério:* dois clientes falsos conectam, negociam versão e trocam heartbeat.

**M1 — Mundo assíncrono.** Log de eventos, assentamentos no mapa-mundo, presença.
*Critério:* dois jogadores se veem no planeta, cada um no seu tempo.

**M2 — Durabilidade.** Checkpoints, hash, tick monotônico, alertas, restauração.
*Critério:* **reproduzir o incidente de save órfão e o sistema detectar em menos de 1 minuto.**

**M3 — Sessão.** Convite, aceite, congelamento, lockstep de 2 jogadores, consenso de pausa, aborto com rollback.
*Critério:* visitar a colônia do outro e sair sem divergência.

**M4 — Interações.** Multifacção, ajuda em defesa, ataque conjunto.

**M5 — Comércio.** Consignação, escrow, catálogo, entrega.

M2 vem antes de M3 de propósito. **Integridade antes de recurso.**

---

## 14. Fundação técnica disponível

Levantamento verificado — versões, licenças e tamanhos conferidos diretamente.

### 14.1 O que o RimWorld entrega

**As assemblies não são ofuscadas.** `Assembly-CSharp.dll` tem 16.154 tipos e 90.768 métodos com nomes originais:

```
RimWorld           5917 tipos      Verse.AI            278
Verse              1754            Verse.AI.Group      129
RimWorld.QuestGen   381            RimWorld.BaseGen    125
RimWorld.Planet     308            LudeonTK             50
```

Qualquer decompilador devolve C# praticamente idêntico ao original. Engenharia reversa de verdade — adivinhar nomes — não é mais necessária.

**Pontos de extensão, todos públicos:**

| Tipo | Uso no projeto |
|---|---|
| `Verse.GameComponent` | estado por partida (cursor de mundo, sessão ativa) |
| `RimWorld.Planet.WorldComponent` | estado por mundo |
| `Verse.MapComponent` | estado por mapa |
| `Verse.TickManager` | controle de tempo |
| `Verse.Rand` | estado de RNG — crítico para lockstep |
| `Verse.Scribe` | serialização de save |
| `RimWorld.FactionManager` | facções |
| `RimWorld.Planet.Caravan` | caravanas (regra de presença, §4) |
| `Verse.MapGenerator` / `MapDeiniter` | ciclo de vida de mapa |

**Dados abertos:** 582 arquivos XML em `Data/Core/Defs/`, e uma pasta `Source/` na instalação com 43 arquivos `.cs` oficiais de exemplo.

### 14.2 O que já existe sob licença permissiva

| Projeto | Licença | O que resolve |
|---|---|---|
| [rwmt/Multiplayer](https://github.com/rwmt/Multiplayer) | **MIT** | lockstep, desync, tempo assíncrono, multifacção |
| [Zetrith/Prepatcher](https://github.com/Zetrith/Prepatcher) | MIT | adicionar campos em classes do jogo |
| [rwmt/MultiplayerAPI](https://github.com/rwmt/MultiplayerAPI) | MIT | mods declararem compatibilidade MP |
| [pardeike/Harmony](https://github.com/pardeike/Harmony) | MIT | patching de método |

**MIT permite ler, reusar e derivar, pedindo apenas atribuição.** Isso vale para o mod que resolveu a parte mais difícil do problema.

### 14.3 Mecanismos do Multiplayer que valem estudar

Estrutura: `Source/{Client, Common, Server, MultiplayerLoader, Tests}` — com testes.

**`Client/AsyncTime/ITickable.cs` — 23 linhas, e é a abstração inteira de tempo:**

```csharp
public interface ITickable
{
    int TickableId { get; }
    Queue<ScheduledCommand> Cmds { get; }
    float TimeToTickThrough { get; set; }
    TimeSpeed DesiredTimeSpeed { get; set; }
    float TickRateMultiplier(TimeSpeed speed);
    void Tick();
    void ExecuteCmd(ScheduledCommand cmd);
}
```

Cada tickable tem **fila de comandos e velocidade de tempo próprias**. É a mesma ideia da §2.3 deste documento, e serve direto: uma sessão é um conjunto de tickables avançando sob barreira comum.

**`Client/Desyncs/` — detecção por estado de RNG, não por hash de estado.**

`ClientSyncOpinion` é uma "opinião" periódica sobre um intervalo de ticks:

```
startTick, commandRandomStates, worldRandomStates,
mapStates[mapId → randomStates], desyncStackTraceHashes, roundMode
```

`CheckForDesync(other)` compara e devolve **motivo legível**, não booleano:

```
"FP round mode doesn't match"      "Wrong random state on map {id}"
"Map instances don't match"        "Wrong random state for the world"
"Random state from commands doesn't match"
```

Cinco decisões daí que vamos adotar:

1. **Impressão digital é o estado do RNG**, barato de comparar e diverge imediatamente.
2. **Granularidade por mapa** — você sabe *onde* divergiu, não só *que* divergiu.
3. **Modo de arredondamento de ponto flutuante é comparado** — armadilha clássica de determinismo.
4. **Stack traces completos só trafegam depois do desync**; em jogo normal só os hashes.
5. **`lastValidTick`** é mantido explicitamente — é o último ponto comprovadamente consistente, e portanto o **ponto de rollback natural** para o aborto de sessão da §2.3.

Detectado o desync, o `SyncCoordinator` **para de simular na hora** em vez de seguir divergindo, e gera relatório. Backlog limitado a 30 opiniões.

**`Client/Syncing/` — sincroniza intenção, não estado.** A API de registro:

```
RegisterSyncMethod        chamada de método vira comando replicado
RegisterSyncField         mudança de campo vira comando
RegisterSyncDelegate      closures e float menus
RegisterSyncWorker        serializador customizado por tipo
RegisterPauseLock         condição que força pausa
```

`RegisterPauseLock` é reaproveitável quase direto para o **consenso de pausa** da §3.

**`Client/Factions/` — multifacção já implementado**, incluindo criação de facção, contexto por facção e telas de setup. É a base de "controlar os próprios colonos no mapa do outro" (§5).

### 14.4 O que este projeto tem de genuinamente novo

Honestidade sobre o delta, para não reinventar o que existe:

| Já existe no Multiplayer | Novo aqui |
|---|---|
| Tempo assíncrono **por mapa, dentro de uma partida** | Assíncrono **entre saves independentes** |
| Lockstep permanente | Lockstep **delimitado por sessão**, com rollback |
| Multifacção | Multifacção **como recurso de encontro** |
| — | Regra de presença (§4) |
| — | Integridade de checkpoint (§7) |
| — | Comércio por consignação física (§6) |
| — | Classificação de mods por impacto (§8) |

O tempo assíncrono do Multiplayer roda dentro de **um save compartilhado**. A diferença aqui é cada jogador ter save próprio e persistência independente — que é justamente o que permite jogar sozinho.

### 14.5 Riscos

O gargalo não é o jogo ser fechado. É **superfície de manutenção**: 90 mil métodos mudam a cada versão, e todo patch de Harmony é acoplamento a um detalhe interno. O Multiplayer sustenta isso há anos, mas com esforço contínuo.

Daí a §8 (classificar mods por impacto) e a §9.1 (ignorar mensagem desconhecida em vez de derrubar) existirem: elas limitam quanto do sistema quebra quando o jogo muda.

---

## 15. Apêndice — lições medidas no RimWorld Together

Tudo aqui foi observado diretamente, com log dos dois lados.

**14.1 Perda silenciosa por colisão de nome de arquivo.**
O modo Morte Permanente renomeia o save; o RT indexa por `RT - <ip>-<porta> - <usuario>`. Quando divergiram, o arquivo do RT ficou órfão e o cliente passou a enviar o mesmo estado congelado por ~5 horas. O servidor gravava com carimbo novo e conteúdo velho. Nada avisou.
→ §7.1 regras 3, 4 e 5.

**14.2 Identidade de save atrelada a endereço de rede.**
Trocar o IP do servidor faz os saves do jogador "sumirem".
→ §7.1 regra 5.

**14.3 Autoridade ambígua.**
Um toggle global decide quem manda no save. Errado nos dois sentidos: de um lado ignora o servidor, do outro sobrescreve o cliente. Não existe detecção de divergência.
→ §7.3.

**14.4 Handshake por reflexão.**
Comparar nomes de classe impede evoluir o servidor sem quebrar todo cliente, e falha sem mensagem útil.
→ §9.1.

**14.5 Enforcement de mods tudo-ou-nada.**
Um mod cliente a mais bloqueou o login — inclusive o mod escrito para melhorar a experiência.
→ §8.

**14.6 Transferência de mapa inteiro sem incremental, e fora de qualquer janela.**
1739 ms de serialização medidos; aplicar exige reconstruir terreno, things, pawns e telhados. Não há caminho de delta — e pior, a transferência era um recurso avulso de visualização, não um momento delimitado.
→ §4: transferir é necessário; o que não pode é transferir continuamente.

**14.7 Retenção curta de backup.**
6 backups de hora em hora = 6 horas. Numa investigação real, quase expirou antes da causa ser encontrada.
→ §7.2.

**14.8 Cliente fechado.**
O servidor é aberto, mas cliente e definições de pacote são binários pré-compilados. Impossível contribuir com a metade que importa.
→ §12.

[RT #273]: https://github.com/RimWorld-Together/Rimworld-Together/issues/273

---

## 16. Decisão — repositório próprio, não fork

**Repositório novo.** O Multiplayer tem 393 arquivos e 56.781 linhas construídos sobre uma premissa que este projeto nega: um save compartilhado, um mundo, lockstep permanente. Fork significaria arrancar a fundação antes de escrever a primeira linha própria, e cada merge com o upstream ficaria mais caro conforme a divergência cresce — justamente no núcleo.

Fork faria sentido para **melhorar** o Multiplayer. O objetivo aqui é outro.

### 16.1 Licença deste projeto

**MIT.** Coerente com a motivação do projeto, compatível com reusar código MIT sem atrito, e não impõe a terceiros a restrição que inviabilizou contribuir com o RT.

GPL foi considerada e descartada: código MIT entra em projeto GPL, o contrário não. Copyleft fecharia portas que este levantamento acabou de encontrar abertas.

### 16.2 Atribuição

`Copyright (c) 2018 Zetrith`, MIT, com a cláusula:

> The above copyright notice and this permission notice shall be included in all copies or **substantial portions** of the Software.

Isso separa dois casos:

| Situação | Obrigação |
|---|---|
| Ler o código e escrever o nosso | crédito no README (cortesia) |
| Copiar código, mesmo adaptado | aviso de copyright + texto da licença junto |

Arquitetura e ideias não são cobertas por copyright. Trechos de código são.

**Estrutura de conformidade:**

```
/THIRD_PARTY/
  Multiplayer-MIT.txt     licença integral + copyright do Zetrith
  Harmony-MIT.txt
  Prepatcher-MIT.txt
```

Todo arquivo derivado carrega cabeçalho:

```csharp
// Baseado em Source/Client/Desyncs/ClientSyncOpinion.cs
// de rwmt/Multiplayer, commit <hash>, MIT, Copyright (c) 2018 Zetrith
// Ver THIRD_PARTY/Multiplayer-MIT.txt
```

### 16.3 Posicionamento público

O README declara explicitamente o que o projeto é e o que não é, para não haver ambiguidade sobre a relação com o Multiplayer:

> Reusa mecanismos de [Multiplayer](https://github.com/rwmt/Multiplayer) (MIT, Zetrith) — em especial detecção de desync por estado de RNG e a abstração de tempo por tickable. Depende de Harmony e Prepatcher. **Não é um fork:** a arquitetura aqui é assíncrona com lockstep delimitado por sessão, enquanto o Multiplayer é lockstep global sobre um save compartilhado.

O clone do Multiplayer é mantido como **referência de leitura**, fora do controle de versão:

```
referencia/multiplayer/     (no .gitignore)
```

---

## 17. Transporte

### 17.1 Princípio

> **O transporte é plugável. O projeto nunca fica refém de uma loja nem de um protocolo.**

```
ITransport
  ├── DirectTransport     IPv6 e IPv4, qualquer loja
  ├── SteamTransport      P2P + convite de amigo (opcional)
  └── LanTransport        descoberta local
```

O `Source/Client/Util/ConnectorRegistry.cs` do Multiplayer é exatamente esse padrão e serve de referência.

### 17.2 IPv6 é requisito, não recurso

**Motivação: CGNAT.** No Brasil, a maioria dos provedores residenciais entrega IPv4 atrás de Carrier-Grade NAT. O roteador não tem endereço público — encaminhar porta simplesmente não funciona, e o jogador é empurrado para VPN ou relay de terceiro.

**IPv6 resolve na raiz.** Os mesmos provedores entregam IPv6 nativo em dual-stack, com endereço globalmente roteável por dispositivo. Conexão direta, sem NAT, sem relay, sem VPN.

Verificação numa máquina residencial brasileira típica:

```
IPv4  192.168.0.4                                (privado, atrás de CGNAT)
IPv6  2804:14c:5b41:8f81:cb25:88b4:8100:6b8e     (global, via RouterAdvertisement)
```

Enquanto o IPv4 exige contorno, o IPv6 já está lá e é roteável.

### 17.3 Regras de implementação

Derivadas de falhas reais — ver [RT #315], aberta em 02/09/2026, que relata o servidor não fazendo bind em IPv6 e o cliente não aceitando endereço IPv6 digitado nem colado.

1. **Socket dual-stack por padrão.** Bind em `::` com `DualMode = true`, atendendo IPv4 e IPv6 no mesmo listener. Nunca fixar `AddressFamily.InterNetwork` no código.
2. **Aceitar literal IPv6 com colchetes.** `[2804:14c::1]:25555` tem que funcionar digitado e colado. Parsing de endereço nunca por `split(':')`.
3. **Happy Eyeballs (RFC 8305).** Tentar IPv6 e IPv4 em paralelo, com vantagem inicial pequena para o IPv6, e ficar com o que conectar primeiro. Nunca serializar as tentativas com timeout longo.
4. **O servidor anuncia todos os seus endpoints alcançáveis**; o cliente percorre a lista. Endpoint não é configuração única.
5. **Endereço nunca é identidade.** Identidade é `player_id`. Ver §15.2 — atrelar save a endereço de rede foi causa raiz de perda de progresso no RT.
6. **Atenção a endereços temporários.** SLAAC gera endereços de privacidade que rotacionam. Para hospedar, usar o estável ou fazer bind em `::`, nunca fixar um literal que expira.
7. **IPv6 não tem NAT, mas tem firewall.** O anfitrião ainda precisa de regra de entrada. A mensagem de erro deve dizer isso explicitamente em vez de "falha ao conectar".
8. **Teredo e túneis 6to4 são último recurso.** Presentes em muitas máquinas Windows, instáveis na prática. Depriorizar na ordem de tentativa.

### 17.4 Escada de conectividade

Ordem de tentativa, do melhor para o pior:

| # | Caminho | Requisito | Custo |
|---|---|---|---|
| 1 | **IPv6 direto** | dual-stack nas duas pontas | nenhum |
| 2 | **IPv4 direto** | sem CGNAT ou com porta encaminhada | configuração de roteador |
| 3 | **LAN** | mesma rede local | nenhum |
| 4 | **Steam P2P** | ambos com o jogo na Steam | nenhum, mas exclui outras lojas |
| 5 | **Relay do usuário** | servidor próprio ou VPN de malha | infra de terceiro |

O caminho 1 é o alvo. Todo o resto é degradação.

### 17.5 Steam como melhoria, nunca como requisito

O RimWorld embarca `com.rlabrecque.steamworks.net.dll` **inclusive na build GOG**, e mods rodam no processo do jogo — herdando o contexto Steamworks sem redistribuir SDK.

O Multiplayer já faz isso (`NetworkingSteam.cs`, `SteamIntegration.cs`), com P2P, convite pela lista de amigos, nome e avatar na UI. Vale também notar o uso de `SteamUGC.SubscribeItem`: ao entrar num servidor, o cliente pode se inscrever sozinho nos mods faltantes da Workshop — resolvendo boa parte da dor descrita na §8.

**Mas:** em instalação não-Steam, `SteamManager.Initialized` é `false`. Transporte Steam-only excluiria jogadores de GOG — incluindo autores deste projeto. Por isso o caminho 1 é o baseline e o Steam é camada opcional.

### 17.6 Critérios de teste

O transporte só é considerado pronto quando:

- Servidor aceita conexão IPv6 e IPv4 no mesmo listener
- Cliente conecta com literal IPv6 entre colchetes, digitado e colado
- Dois pares atrás de CGNAT distintos conectam por IPv6 sem relay
- Falha de firewall produz mensagem que diz o que fazer
- Steam ausente não impede nenhuma funcionalidade além do convite por amigo

[RT #315]: https://github.com/RimWorld-Together/Rimworld-Together/issues/315
