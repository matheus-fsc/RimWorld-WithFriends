# Arquitetura

Três peças, e a terceira quase não sabe de jogo.

```mermaid
flowchart TB
    subgraph A["Jogador A (anfitrião)"]
        CA["RimWorld + mod"]
        SA[("save próprio")]
    end
    subgraph B["Jogador B (visitante)"]
        CB["RimWorld + mod"]
        SB[("save próprio")]
    end
    C{{"Coordenador<br/>(dotnet, sem RimWorld)"}}
    D[("checkpoints<br/>e log de eventos")]

    CA <-->|"TCP, protocolo v1"| C
    CB <-->|"TCP, protocolo v1"| C
    C --- D
    CA --- SA
    CB --- SB
```

O coordenador **não valida regra de jogo**. Ele carimba ordem, guarda checkpoint,
difunde evento de mundo e aplica uma única regra de autoridade: decisão de
colônia é de quem mora nela. Ele lê um byte do comando, não um comando.

Isso é o que permite ele rodar em qualquer lugar e nunca precisar acompanhar
atualização do RimWorld.

## Os dois regimes

```mermaid
flowchart LR
    F["Fora de sessão<br/>cada um no seu ritmo"] -->|"convite aceito"| S["Em sessão<br/>lockstep no mapa do anfitrião"]
    S -->|"fim ou aborto"| F

    F -.->|"checkpoint, eventos de mundo,<br/>mercado"| F
    S -.->|"comando, barreira,<br/>impressão digital"| S
```

**Fora de sessão não existe sincronia por tick. Nenhuma.** O que trafega é
assíncrono: checkpoint da colônia, eventos de mundo, presença. Jogar sozinho
nunca depende de ninguém estar online.

**Dentro da sessão** os dois simulam o mesmo mapa, tick a tick, sob uma barreira
comum. É caro e frágil, e por isso dura pouco e tem ponto de retorno.

## O ciclo de uma visita

```mermaid
sequenceDiagram
    participant A as Anfitrião
    participant C as Coordenador
    participant B as Visitante

    A->>C: SessaoConvite
    C->>B: SessaoConvite
    B->>C: SessaoAceite
    Note over C: compara SessionModSetHash<br/>aqui, nunca no login

    C-->>A: SessaoInicio (TickInicial, Semente)
    C-->>B: SessaoInicio (TickInicial, Semente)

    Note over A,B: os dois congelam e guardam<br/>checkpoint pré-sessão

    A->>B: SessaoPartida (a partida do anfitrião, comprimida)
    Note over B: carrega a partida do anfitrião<br/>(ADR 0010)

    loop a cada tick
        A->>C: SessaoBarreira (Tick, Fingerprint)
        B->>C: SessaoBarreira (Tick, Fingerprint)
        C-->>A: TickLiberado = mínimo dos dois
        C-->>B: TickLiberado = mínimo dos dois
    end

    A->>C: SessaoComando (sem tick)
    Note over C: agenda para barreira + 10<br/>e carimba Ordem
    C-->>A: SessaoComando (TickAlvo, Ordem)
    C-->>B: SessaoComando (TickAlvo, Ordem)

    A->>C: SessaoFim
    C-->>B: SessaoFim
    Note over A,B: cada um volta para a própria partida
```

Quatro regras sustentam isso:

1. **O comando vai sem tick.** Quem agenda é o coordenador, para `barreira + 10`,
   e carimba a ordem. Os dois lados recebem o mesmo agendamento sem que o
   servidor entenda de RimWorld.
2. **`TickLiberado` é o mínimo entre os participantes.** Ninguém passa do mais
   lento, e qualquer um pausado congela a barreira.
3. **A impressão digital é o estado do RNG do intervalo.** Dois valores
   diferentes para o mesmo tick significam divergência.
4. **O ponto de rollback viaja junto**, no `UltimoTickValido`.

## O que acontece quando diverge

```mermaid
flowchart TD
    T["digitais diferentes<br/>no mesmo tick"] --> R["volta ao último<br/>ponto consistente"]
    R --> J["os dois recarregam<br/>o mesmo estado"]
    J --> Q{"divergiu de novo<br/>logo depois?"}
    Q -->|não| OK["a visita continua"]
    Q -->|sim| AB["é a SIMULAÇÃO que difere,<br/>não o estado"]
    AB --> CK["os dois voltam ao<br/>checkpoint pré-sessão"]
    CK --> F["perde-se o encontro,<br/>nunca a colônia"]
```

Refazer o ponto de junção resolve divergência de **estado**. Quando os dois
partem de um estado idêntico e se afastam de novo, o que difere é a simulação, e
insistir não adianta. Ver [[Determinismo]].

## Por dentro do cliente

```mermaid
flowchart TB
    subgraph Sessao["Sessão"]
        BR["barreira"] --> TK["tick guiado"]
        TK --> FP["impressão digital"]
        CMD["comandos de jogador"] --> TK
    end

    subgraph Guardas["Guardas de determinismo"]
        G1["interface lê,<br/>mas não escreve"]
        G2["efeito não<br/>sorteia do fluxo"]
        G3["quadro não<br/>entra no tick"]
        G4["estado de processo<br/>zerado na entrada"]
    end

    UI["interface do jogador"] -->|"vira comando"| CMD
    UI -.->|"bloqueado"| Guardas
    Guardas --> TK
```

Cada guarda existe por causa de uma divergência real, medida e reproduzida. As
treze já achadas estão em [[Determinismo]], com a família de cada uma.

## Onde ler mais

* [Arquitetura completa](https://github.com/matheus-fsc/RimWorld-WithFriends/blob/main/docs/ARQUITETURA.md)
* [Protocolo, mensagem por mensagem](https://github.com/matheus-fsc/RimWorld-WithFriends/blob/main/docs/PROTOCOLO.md)
* [Decisões registradas (ADR)](https://github.com/matheus-fsc/RimWorld-WithFriends/tree/main/docs/adr)
