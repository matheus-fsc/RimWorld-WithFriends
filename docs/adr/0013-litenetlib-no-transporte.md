# ADR 0013 — LiteNetLib no transporte: quando, e o que quebra junto

**Status:** registrado, não adotado
**Data:** 2026-09-11
**Origem:** o Multiplayer credita LiteNetLib (RevenantX, MIT) nos agradecimentos

## Por que o Multiplayer precisa e nós (ainda) não

A diferença não é de gosto, é de topologia.

No Multiplayer **o anfitrião é o servidor**: um jogador hospeda da própria
máquina, e os outros precisam chegar até ele — atravessando NAT, roteador
doméstico, às vezes CGNAT. Daí o `NatPunchModule` e o arbiter. Sem isso, cada
partida começaria com "abra a porta 30502 no seu roteador".

Aqui a topologia é estrela com um **coordenador** dedicado: os clientes discam
para fora. Conexão de saída atravessa NAT e CGNAT sem furar nada, e é por isso
que TCP tem bastado. O recurso de cabeça do LiteNetLib não compra nada hoje.

## O que ele compraria

1. **O amigo hospedar sem abrir porta.** Este é o argumento de verdade, e é
   provável: num grupo pequeno, o normal é um deles rodar o coordenador em casa.
   Hoje isso exige encaminhamento de porta — custo de adoção real para o
   público-alvo (§1.1), não hipótese.

2. **Canal não confiável para o que não precisa de ordem.** O cursor no mapa do
   mundo (`WorldCursorComponent`) e a presença são "vale o último": perder um
   pacote é melhor que atrasar o próximo. Hoje trafegam por um canal que garante
   ordem sem que ninguém precise.

3. **Retransmissão mais rápida que o RTO do TCP.** Em lockstep toda mensagem é
   crítica de ordem, então canal confiável-ordenado do LiteNetLib se comporta
   como TCP — o ganho não é evitar bloqueio de cabeça de fila, é reagir a perda
   mais rápido. Importa em velocidade alta, onde um pacote perdido trava a
   barreira de todo mundo.

## O que quebra junto, e precisa ficar escrito

A margem de `TicksDeAtraso = 5` é pequena de propósito, e o argumento é este:

> O cliente só avança quando recebe uma liberação nova, e a liberação nova sai
> **depois** do comando, na mesma conexão ordenada.

Isso vale porque hoje há **uma** ordem — a do TCP. Com LiteNetLib, vale apenas
se comando e barreira andarem no **mesmo canal confiável-ordenado**. Separá-los
em canais diferentes, que é justamente o que a biblioteca convida a fazer,
quebra a garantia **em silêncio**: a sessão passa a encerrar por "comando chegou
tarde" de vez em quando, sem nada no código do jogo ter mudado.

Então, se adotarmos: comando e barreira no mesmo canal ordenado, e a margem
volta a ter de cobrir a velocidade do jogo se alguém separá-los. É um invariante
do protocolo, não detalhe de transporte.

## Decisão

Não adotar agora. `ITransport`/`IConexao` (ADR 0003) existem exatamente para
esta troca ser contida, e o transporte não é o gargalo de nada que estamos
perseguindo — a lentidão que parecia rede era barreira, e a divergência aberta é
de simulação.

**Gatilho para adotar:** quando quisermos que um jogador hospede o coordenador
sem abrir porta. Aí o NAT punch deixa de ser luxo e vira o recurso.

Ao adotar: entrada em `THIRD_PARTY/` (MIT, RevenantX) e crédito no README, como
já é feito com Harmony e Multiplayer.
