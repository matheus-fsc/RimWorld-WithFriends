# ADR 0005 — Checkpoint automático pega carona no save do jogo; referência sobrevive a reinício

**Status:** aceito
**Data:** 2026-09-10
**Fecha pendências de:** [ADR 0002](0002-integridade-de-checkpoint.md)

## Contexto

Duas lacunas conhecidas ficaram expostas assim que o sistema rodou de verdade:

1. **O envio de checkpoint era manual.** A §7 existe porque perda silenciosa
   acontece quando ninguém está olhando — depender de o jogador lembrar de
   clicar é a mesma falha com outro nome.
2. **A referência do monitor vivia só em memória.** Reiniciar o coordenador
   zerava `último tick visto`, e a primeira mensagem depois não tinha com o
   que ser comparada. Durante o desenvolvimento isso aconteceu três vezes em
   uma tarde.

## Decisão 1 — checkpoint pendurado no save do próprio jogo

Postfix de Harmony em `Verse.GameDataSaveLoader.SaveGame(string fileName)`.
Depois que o jogo grava, lemos **o arquivo que ele acabou de escrever**,
calculamos o hash e enviamos.

Alternativas descartadas:

| Alternativa | Por que não |
|---|---|
| Timer de N minutos serializando a partida | Serializar custa segundos e trava o frame num momento arbitrário. O jogador levaria um engasgo sem entender por quê. |
| Postfix em `Autosaver.DoAutosave` | Pega só o autosave, e não dá acesso ao nome do arquivo — teríamos que serializar de novo. |
| Serializar por conta própria no autosave | Dobraria o custo do engasgo, para produzir bytes idênticos aos que o jogo acabou de gravar. |

Pegar carona custa **zero** serialização nova: o autosave já pagou a conta, e
num momento em que o engasgo é esperado. Cobre também o save manual.

Guardas: piso configurável entre envios (5 min por padrão), só com conexão
viva, e `try/catch` em volta de tudo — nada aqui pode quebrar o salvamento do
jogador, cujo arquivo já está no disco de qualquer forma.

## Decisão 2 — referência reconstruída do índice

Ao ver uma colônia pela primeira vez, o monitor lê o índice append-only em
disco e adota o último tick e o último `content_hash` como referência.

A impressão digital do estado vivo **não** é restaurada: heartbeat não é
gravado, e inventar um valor geraria alerta falso. Ela é rearmada no primeiro
heartbeat depois do reinício — uma janela de 30s sem detecção de simulação
congelada, contra uma detecção de regressão de tick que passa a funcionar
sempre.

## Consequências

- Reiniciar o coordenador deixa de ser um ponto cego. O log diz o que
  restaurou: `referência restaurada do disco: tick=… conteudo=… (N checkpoints)`.
- Uma linha corrompida no índice é ignorada com aviso, sem invalidar o resto:
  o histórico continua sendo evidência utilizável.
- A durabilidade do M2 passa a acontecer sozinha, no ritmo do próprio jogo.
- Novo acoplamento a um método interno do RimWorld (`SaveGame`), com o risco
  da §14.5. Mitigado por ser assinatura pública, estável há várias versões, e
  por falhar de forma contida se sumir.
