# Estado atual

Números medidos, não estimados. Data: 16/09/2026.

## O que funciona

Duas instâncias do jogo entram numa visita, simulam o mesmo mapa em lockstep sob
uma barreira comum, trocam comandos de jogador, detectam divergência por
impressão digital do estado do RNG e, quando divergem, voltam a um ponto de
junção em vez de abortar o encontro.

| Medida | Valor |
|---|---|
| Testes automatizados | 153, todos passando |
| Pontos de acoplamento com o jogo, catalogados | 88 |
| Classes de remendo instaladas | 114 |
| Corrida de regressão mais longa sem divergência | 3.992 ticks |

Cada ponto de acoplamento é verificado na subida. Se um sumir numa atualização do
jogo, o recurso correspondente desliga, o log diz qual e por quê, e o jogo
continua abrindo. Jogar sozinho nunca é bloqueado.

## O mapa de decisões

O Multiplayer registra 360 pontos onde uma decisão de jogador precisa virar
comando. Cruzando com o que existe aqui:

| | Quantos |
|---|---|
| Fora de escopo (caravana, comércio, pesquisa, ideologia, missão) | 95 |
| Já viram comando aqui | 24 |
| Faltam | 241 |

Os 241 que faltam, por assunto: `comp` 100, `outros` 64, `construção` 32, `pawn`
25, `bancada` 16, `zona/área` 4.

**A conta honesta do que resta**: 119 dos 241 dependem de identificar a ação de um
botão, que é o caso caro. O resto se divide entre intercepções diretas e famílias
inteiras que ainda não foram abertas.

## O que já vira comando

Ordem direta, ordem priorizada, alistar, designadores (um a um e em arrasto),
velocidade do tempo, encerrar job, prioridade de trabalho, restrição de área,
mestre de animal, seguir alistado, proibir, segurar fogo, cama médica, ponto de
encontro, não cortar planta, nível de combustível, estado do tanque, renomear
pawn, cuidado médico, tratar-se sozinho, resposta a hostilidade, marcar operação,
incidente, estoque, e a família zona/área inteira.

## O que ainda não funciona

A visita ainda **diverge em partidas de verdade**. Cada sessão jogada por gente
revela em média uma causa nova, e a maioria delas não aparece na bancada. Ver
[[Determinismo]] para o porquê e para a lista do que já foi achado.

Estimar prazo para "jogável" seria chute: o que existe é uma taxa de descoberta,
não uma lista fechada. O que dá para dizer com números é que as causas estão
ficando mais raras e mais tardias: as primeiras derrubavam a visita no tick 200,
as últimas no tick 1000 e além.
