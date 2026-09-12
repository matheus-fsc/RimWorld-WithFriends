# ADR 0008 — Escala alvo e ciclo de vida da visita

**Status:** aceito
**Data:** 2026-09-11

## Contexto

Ao desenhar o bootstrap do mapa apareceram perguntas que não são técnicas:
quanto pode custar uma visita? o mapa fica guardado do lado do visitante? o
que precisa de sessão e o que não precisa?

As três dependem de uma premissa que não estava escrita em lugar nenhum.

## Decisão 1 — escala alvo é um grupo pequeno

**RimWorld com amigos**, não um servidor público. Um punhado de pessoas que se
conhecem, não oitenta jogadores anônimos interagindo simultaneamente.

Isso autoriza decisões que num MMO seriam inaceitáveis:

| Decisão | Só é aceitável porque o grupo é pequeno |
|---|---|
| Transferir um mapa inteiro para abrir a visita | acontece poucas vezes por sessão de jogo, com consentimento |
| Lockstep de tempo real durante a visita | um encontro por vez, sem arbitragem de carga |
| Coordenador não valida regra de jogo (§10) | os participantes se conhecem; anticheat não é objetivo |
| Sem fila, sem balanceamento, sem sharding | não há demanda concorrente |

Projetar para oitenta jogadores anônimos mudaria tudo — e daria um mod pior
para o caso que realmente importa.

## Decisão 2 — o mapa da visita é descartado no fim

Quando o encontro acaba, o visitante volta para a própria base e o mapa do
anfitrião é **descarregado**. Não fica cópia nem cache. Entrar de novo
sincroniza de novo.

Alternativa descartada: guardar o mapa para acelerar a próxima visita. Ela
troca banda por um problema muito pior — o mapa guardado envelhece a cada tick
que o anfitrião joga sozinho, e voltar exigiria reconciliar dois estados
divergentes. Reconciliação de estado divergente é exatamente o que este
projeto recusa (§1); pagá-la para economizar uma transferência seria trocar o
barato pelo caro.

## Decisão 3 — um tempo de carregamento é aceitável

Não há orçamento em milissegundos. O que está decidido é o princípio:

> **Abrir uma visita pode levar um tempo de carregamento, desde que seja um
> momento anunciado.**

A régua é o próprio jogo: carregar um save ou gerar o primeiro mapa já custa
alguns segundos, e ninguém reclama — porque é esperado e tem feedback. A
visita entra na mesma categoria. Poucos segundos é confortável; a fronteira
exata é questão de sensação, não de contrato.

Medido em colônia madura (250×250, 34.528 things): ~0,9 s para serializar,
~0,1 s para comprimir, 0,64 MB para trafegar (`docs/MEDICOES.md`). Há folga de
vários múltiplos.

### Otimização futura: delta entre estados

Se um dia o custo incomodar, a saída mais promissora **não** é acelerar a
serialização — é mandar menos: comparar o mapa de agora com uma cópia de um
momento anterior e transferir só a diferença.

Isso parece contradizer a Decisão 2 (descartar o mapa), mas não contradiz, e a
distinção é o ponto inteiro:

| | Cache de estado | Base de delta |
|---|---|---|
| Para que serve | jogar sobre ele | reconstruir o novo |
| É autoridade? | sim — e por isso envelhece | não — é só um dicionário de compressão |
| Precisa reconciliar? | **sim**, e é aí que mora o problema | não: o resultado é sempre o mapa do anfitrião, byte a byte |

Guardar bytes antigos para **reconstruir** os novos é compressão. Guardar um
mapa antigo para **continuar jogando nele** é reconciliação — e essa continua
proibida.

Investigar exige medir primeiro: quanto de um mapa muda entre dois momentos de
jogo. Sem esse número não dá para saber se o delta compensa a complexidade.

## Decisão 4 — o custo é sempre visível e consentido

```
convite → o jogador ACEITA → tela de sincronização → visita → fim → descarrega
```

Sincronia nunca acontece de fundo nem de surpresa. O jogador vê que vai
esperar, sabe por quê, e escolheu esperar. Isso transforma o custo da §4 de
defeito em contrato.

## Decisão 5 — comércio não usa sessão

Troca de bens acontece pela camada de mundo, assíncrona (§6). É a interação
mais frequente entre jogadores e também a mais barata — e as duas coisas
juntas não são coincidência.

A regra geral: **se dá para resolver de forma assíncrona, resolve-se de forma
assíncrona.** Sessão é para quando as duas pessoas precisam estar no mesmo
lugar ao mesmo tempo — ajuda humanitária, tropas, defesa conjunta, ataque
conjunto.

## Consequências

- O bootstrap do mapa pode ser simples e direto: serializa, manda, carrega.
  Sem delta, sem cache, sem invalidação — três subsistemas que não precisam
  existir.
- `MapDeiniter` no fim da visita passa a ser parte do fluxo de encerramento,
  junto do commit (§2.3).
- A tela de sincronização é requisito de produto, não enfeite: é ela que
  torna o custo aceitável.
- A medição do passo B (ADR 0007) está feita e cabe no orçamento com folga:
  ver `docs/MEDICOES.md`.
