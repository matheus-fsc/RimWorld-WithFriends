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

## Estimativa para funcionar de verdade

Estimativa sem premissa é chute, então as premissas vão junto.

**O que conta como "funcionar de verdade"**: uma visita de dez a quinze minutos,
com combate, sobrevivendo sem divergência, com as decisões de jogador que uma
visita exercita já virando comando. Não é "tudo do jogo sincronizado": é o
encontro inteiro sem o jogo desfazer o que alguém acabou de fazer.

### Frente 1: determinismo (a incerta)

Não há lista fechada. O que há é uma taxa, e ela está caindo. Medido em
16/09/2026, o tick em que a visita divergiu, sessão após sessão, conforme as
causas foram sendo consertadas:

```
216  224  200  512  496  421  1000  1160  1744
```

A tendência é clara e vale mais que a média: as primeiras divergências matavam a
visita em três segundos de jogo, as últimas em quase trinta. Treze causas achadas
em um dia de sessões, e as quatro últimas vieram de partidas de gente, não da
bancada.

**Estimativa**: entre 5 e 15 causas restantes nessa família, com intervalo largo
de propósito. A base é fraca (uma amostra de um dia), e o fator que mais pesa é
que as causas restantes são cada vez mais raras, o que significa sessões mais
longas para encontrá-las. Em ritmo de algumas sessões jogadas por semana, isso é
**um a três meses**.

O que encurtaria: um arnês que enxergue interface. Hoje a emulação roda sem
tela, e isso está medido e registrado como limite conhecido (ver [[Bancada]]).

### Frente 2: comandos (a contável)

Esta dá para contar, e é a parte confortável da estimativa.

| | Quantos | Custo por item |
|---|---|---|
| Já viram comando | 24 | feito |
| Intercepção direta (`método`) | 70 | baixo, uma a uma |
| Campo público em lambda de botão | 23 | médio, a máquina já existe |
| Botão cuja ação é lambda (`closure`) | 96 | médio, a máquina já existe |
| Tipo não encontrado no assembly | 52 | a investigar, provavelmente DLC |

A máquina que resolve os dois casos caros (vigiar o campo em volta da chamada de
interface) **já está escrita e funcionando** com três campos. Depois dela, cada
item vira uma entrada num registro, não um problema novo.

Mas nem todos os 241 são necessários para "funcionar de verdade". A visita
exercita ordem, combate, saúde, zona e bancada de trabalho. Estimando por essas
famílias: **60 a 80 itens** cobrem o que um encontro toca de verdade, e são o
alvo real. Em ritmo de uma família por sessão de trabalho, **três a seis semanas**.

### A conta junta

As duas frentes correm em paralelo, e a primeira domina. Sob as premissas acima:

> **Dois a quatro meses** para uma visita de dez minutos com combate sobreviver
> de forma confiável, mantendo o ritmo atual de algumas sessões jogadas por
> semana.

O que mais pode mudar esse número, para cima ou para baixo, é a **frente 1**: se
as próximas três sessões vierem limpas, o intervalo cai pela metade; se
aparecerem duas causas novas de famílias ainda não vistas, dobra.

### O que já é certo

Mesmo no pior caso, duas coisas não voltam atrás: a visita **nunca custa a
colônia** (o checkpoint pré-sessão é anterior a tudo), e cada causa achada fica
registrada com a medição que a provou, em [[Determinismo]] e em
`docs/MEDICOES.md`, inclusive as hipóteses que a medição derrubou.
