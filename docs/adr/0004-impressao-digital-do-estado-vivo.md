# ADR 0004 — Heartbeat carrega impressão digital do estado vivo

**Status:** aceito
**Data:** 2026-09-10
**Corrige:** [ADR 0002](0002-integridade-de-checkpoint.md)

## Contexto

A ADR 0002 definiu que checkpoint e heartbeat carregam o par
`(game_tick, content_hash)`, e que hash parado com tick avançando é alarme.
Os testes passavam. **Em jogo, alarmou errado três vezes em dez minutos.**

Os três falsos positivos, todos observados no RimWorld de verdade:

1. **Heartbeat entre checkpoints.** O cliente mandava o hash do último
   checkpoint. Entre dois checkpoints esse hash fica igual **por construção** —
   é a mesma coisa guardada. Enquanto isso o tick avança, então o alerta
   disparava em toda partida normal, a cada 30 segundos.
2. **Jogo pausado.** Dois heartbeats seguidos com a partida pausada carregam
   o mesmo tick. A regra dizia "tick menor **ou igual** é regressão", e pausar
   o jogo — estado comuníssimo em RimWorld — virava suspeita de save antigo.
3. **Consequência dos dois:** o jogador recebia carta de alerta jogando
   normalmente. Alerta que dispara sem motivo treina o jogador a ignorá-lo, o
   que é pior do que não ter alerta nenhum.

## Decisão

**Separar duas grandezas que a ADR 0002 tratava como uma.**

| Campo | Onde | Comportamento esperado | Sintoma |
|---|---|---|---|
| `content_hash` | checkpoint | **deve** repetir entre checkpoints | checkpoint novo repetindo o conteúdo do anterior com o tick já adiantado |
| `state_fingerprint` | heartbeat | **deve** mudar sempre que o tick avança | impressão digital parada com o tick avançando |

`state_fingerprint` é uma impressão digital do estado **vivo**, barata o
bastante para ir em todo heartbeat: estado do RNG (`Rand.StateCompressed`,
lido por reflexão), contagem de mapas, e por pawn a posição, o job atual e a
saúde. Serializar 6 MB a cada 30s para isso seria absurdo.

Usar o estado do RNG como impressão digital é ideia reusada do Multiplayer
(MIT, Zetrith) — §14.3: "barato de comparar e diverge imediatamente".

**Tick igual deixa de ser regressão.** Só `tick < último` denuncia save antigo
carregado por cima. Pausar o jogo é estado legítimo.

## Consequências

- O caminho saudável fica **silencioso**: verificado com o cliente falso
  (zero alertas) e com três testes novos que reproduzem exatamente os falsos
  positivos observados em jogo.
- O critério de M2 continua valendo: o incidente da §15.1 é detectado no
  primeiro heartbeat depois de a impressão digital congelar, bem abaixo de
  1 minuto.
- A detecção fica **mais forte**, não mais fraca: antes ela só via "o arquivo
  enviado não muda"; agora vê "a simulação não anda", que é a causa, e ainda
  mantém a checagem de conteúdo repetido no caminho do checkpoint.
- `Rand.StateCompressed` é privado. A leitura por reflexão degrada em silêncio
  se o nome mudar numa versão futura do jogo, e os agregados públicos seguram
  o sinal sozinhos. É acoplamento a detalhe interno — exatamente o risco da
  §14.5, aceito aqui por ser um ponto só e com degradação prevista.

## Adendo — alerta é evento, não estado

Terceira coisa que só o jogo mostrou: com a regra "toda avaliação que bate a
condição emite alerta", uma colônia em estado ruim gerava **uma carta a cada
30 segundos**, para sempre. Ruído treina o jogador a ignorar, que é pior do
que não alertar.

Duas correções:

1. **Emissão só na transição.** Cada tipo de alerta é emitido uma vez por
   episódio e rearmado quando a condição some.
2. **Rebaseline depois de regressão de tick.** O servidor nota, avisa uma vez
   e adota o tick observado como nova referência. O cliente é autoridade sobre
   a própria colônia (§7.3) — o papel do servidor é avisar, não ficar gritando
   porque guarda uma referência mais alta que o cliente já abandonou.

## Adendo 2 — a digital é indício, não prova

Medido numa visita: os dois lados fizeram o **mesmo trabalho total** com um
tick de diferença, e o traço de RNG reconvergeu em três ticks (`dif -2 → -1 →
0`). Abortar no primeiro indício matava a visita por ruído.

O coordenador passou a exigir **três comparações seguidas** divergentes antes
de abortar, e zera o contador assim que uma bate. Desync de verdade não volta a
bater — a divergência cresce.

É a mesma disciplina da ADR 0002 num contexto novo: **indício barato dispara
atenção, não execução.**

## Lição

Os testes da ADR 0002 estavam certos sobre o mecanismo e errados sobre a
premissa: eles assumiam que o hash do save reflete o estado vivo. Nenhuma
quantidade de teste unitário teria mostrado isso — só rodar dentro do jogo
mostrou. Vale para o resto do roadmap: **integridade se valida em jogo, não
só em suíte.**
