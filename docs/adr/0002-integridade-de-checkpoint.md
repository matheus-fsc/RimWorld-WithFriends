# ADR 0002 — Integridade de checkpoint: aceitar, marcar, alertar

**Status:** aceito
**Data:** 2026-09-10

## Contexto

O incidente da §15.1 custou ~5 horas de progresso. O modo Morte Permanente
renomeou o save; o RimWorld Together indexava por `RT - <ip>-<porta> -
<usuario>`. Quando os dois divergiram, o arquivo do RT ficou órfão e o cliente
passou horas enviando o mesmo estado congelado. O servidor gravava com carimbo
de tempo novo e conteúdo velho. **Nada avisou** — a falha só apareceu no dia
seguinte.

Três decisões precisavam ser tomadas: o que é identidade, o que é evidência de
divergência, e o que fazer quando ela aparece.

## Decisão

**Identidade é `(player_id, colony_id)`.** Nome de arquivo é detalhe de
armazenamento. `player_id` vive nas settings do mod (por instalação),
`colony_id` nasce com a partida e vive dentro do save. Renomear o arquivo —
o que a Morte Permanente faz — não muda nada.

**Evidência é o par `(game_tick, content_hash)`**, carregado por todo
checkpoint e todo heartbeat:

- tick que não é maior que o último aceito → `TickRegrediu`
- tick que avança com hash de conteúdo parado → `ConteudoCongelado`
- hash declarado que não bate com o conteúdo recebido → `HashNaoConfere`

**A resposta é sempre aceitar, marcar como suspeito e alertar** — nunca
recusar e nunca silenciar. Recusar significaria descartar o progresso de
alguém com base numa heurística; silenciar foi exatamente a falha original.

## Consequências

- O servidor guarda checkpoints suspeitos junto com os sadios, e a retenção
  (§7.2) protege os suspeitos dentro da janela de 7 dias: são evidência de
  investigação.
- O conteúdo é endereçado pelo hash **recalculado sobre o que chegou**, não
  pelo hash que o cliente declarou. Metadado é afirmação; conteúdo é fato.
- Com heartbeat a cada 30s, a latência de detecção é de no máximo dois
  intervalos. O critério de M2 — detectar em menos de 1 minuto — é verificado
  em `tests/DurabilidadeTests.cs`.
- ~~O servidor mantém estado por colônia em memória. Reinício perde o "último
  tick visto".~~ **Resolvido na [ADR 0005](0005-checkpoint-automatico-e-referencia-persistida.md):**
  a referência é reconstruída do índice em disco.

> **Correções posteriores:** a [ADR 0004](0004-impressao-digital-do-estado-vivo.md)
> separa `content_hash` de `state_fingerprint` e conserta três falsos
> positivos observados em jogo. A [ADR 0005](0005-checkpoint-automatico-e-referencia-persistida.md)
> fecha as duas pendências operacionais.

## Alternativas descartadas

**Hash de estado do jogo em vez de hash do arquivo.** Mais preciso, mas exige
o servidor entender o save — e o servidor não simula RimWorld (§10).

**Recusar checkpoint com tick regredido.** Um jogador que legitimamente
carrega um save antigo perderia a capacidade de guardar progresso. Perder
progresso é a falha que este projeto existe para não repetir.
