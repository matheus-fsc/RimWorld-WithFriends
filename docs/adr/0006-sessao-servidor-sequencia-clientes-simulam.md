# ADR 0006 — Na sessão, o servidor sequencia; os clientes simulam

**Status:** aceito
**Data:** 2026-09-10

## Contexto

A §2.3 define sessão como acordo temporário para simular em conjunto, com
lockstep clássico dentro dela. A §10 diz que o servidor é coordenador, não
simulador — ele não tem as assemblies do jogo e não deve ter.

As duas coisas parecem brigar: lockstep costuma exigir um árbitro que entenda
o jogo. Este ADR registra como elas convivem.

## Decisão

O coordenador faz três coisas, nenhuma delas exigindo saber o que um comando
significa:

1. **Sequencia comandos.** O cliente propõe um comando sem tick. O servidor
   atribui `TickAlvo = barreira + atraso` e um número de `Ordem` global, e
   devolve o mesmo agendamento para os dois lados. Ordem idêntica nos dois
   clientes sem interpretar payload.
2. **Sustenta a barreira.** Cada participante relata "simulei até o tick N".
   O liberado é o mínimo entre os participantes, mais o atraso. Ninguém passa
   do mais lento.
3. **Compara impressões digitais.** Cada relato pode carregar o estado de RNG
   do intervalo. Dois valores diferentes para o mesmo tick = desync.

O comando entra num tick **futuro** (10 ticks de atraso) porque um comando
agendado para o presente exigiria que um dos lados voltasse no tempo.

## Consequências

- O servidor continua pequeno e testável sem o jogo: os 13 testes de sessão
  rodam sem RimWorld nenhum.
- Detectado desync, a sessão **para na hora** e o aborto carrega
  `UltimoTickValido` — o último tick em que os dois concordaram, que é o ponto
  de rollback natural (§2.3, §14.3 decisão 5).
- A mensagem de aborto diz **onde** divergiu, não só que divergiu:
  `"divergiram no tick 200: jogador-1 calculou rng:um, jogador-2 calculou
  rng:outro"`. Motivo legível, nunca booleano (§14.3 decisão 2).
- Pausa é consenso (§3 v1): qualquer participante pausado congela a barreira
  onde está. Sem timer, sem contagem regressiva.
- A verificação de mods acontece **no aceite do convite** (§8), nunca no
  login. Divergência de mod recusa a sessão e não tira ninguém do servidor.

## O que fica com o cliente

Tudo o que exige entender RimWorld: congelar a simulação, tirar o
`checkpoint_pre_sessao`, aplicar comandos no tick agendado, calcular a
impressão digital de RNG, e executar o rollback no aborto.

É a parte grande do M3 e é a única que não dá para verificar sem dois jogos
abertos.

## Nota sobre identidade duplicada

Duas instâncias do RimWorld na mesma máquina compartilham a pasta de dados e,
portanto, as settings do mod — onde mora o `player_id`. Duas conexões com a
mesma identidade quebrariam presença e sessão de maneiras confusas.

Decisão: **a conexão nova vence e a antiga é encerrada com motivo legível**.
Recusar a segunda travaria o jogador para fora sempre que uma conexão morresse
sem o servidor perceber (TCP meio-aberto) — justamente quando ele mais precisa
reconectar.

Para testar de verdade, a segunda instância precisa de pasta própria:
`RimWorldLinux -savedatafolder=<caminho>`. `./tools/dois-jogos.sh` cuida disso,
semeia a lista de mods sem copiar as settings — é dali que sairia o
`player_id` duplicado — e dá `-logFile` separado a cada instância, porque o
`Player.log` do Unity fica num caminho fixo que não acompanha o
`-savedatafolder`.
