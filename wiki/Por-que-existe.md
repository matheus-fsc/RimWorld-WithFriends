# Por que existe

Existem dois mods de multiplayer para RimWorld, e cada um resolve metade do
problema.

| | RimWorld Together | Multiplayer (Zetrith) | Este projeto |
|---|---|---|---|
| Mundo | compartilhado, assíncrono | um só, lockstep | compartilhado, assíncrono |
| Colônias | separadas | uma só | separadas |
| Tempo | independente | global | independente, **compartilhado só em sessão** |
| Risco de divergência | não existe | cresce com jogadores e tempo | **limitado à duração da sessão** |
| Jogar sozinho | sim | não | sim |
| Interação real | fraca | total | **total, dentro do encontro** |

O **Multiplayer** dá interação completa e paga com lockstep permanente sobre um
save compartilhado: ninguém joga sozinho, e o risco de divergência cresce com o
tempo e com o número de jogadores.

O **RimWorld Together** dá independência e paga com interação fraca: o mercado
dele é um menu paralelo que ignora a mecânica do jogo, e a "visita" é uma foto do
mapa fora de qualquer janela sincronizada.

## A terceira resposta

Determinismo total do jogo é caro e frágil. Determinismo **dentro de uma janela
curta e delimitada** é viável, e recuperável: existe um ponto de retorno
imediatamente antes dela.

Então a sincronia deixa de ser um estado permanente e vira um **evento**. Você
joga a sua colônia como sempre jogou. Quando quer ajudar um amigo sob ataque,
vocês entram numa sessão, o mapa dele passa a ser simulado pelos dois em
lockstep, e no fim cada um volta para a sua partida.

Isso muda o que acontece quando algo dá errado. No lockstep permanente, uma
divergência custa a partida. Aqui custa **o encontro**, nunca a colônia: os dois
voltam ao checkpoint pré-sessão e seguem jogando.

## Por que não é um fork do Multiplayer

O Multiplayer tem 393 arquivos e 56.781 linhas construídos sobre uma premissa que
este projeto nega: um save compartilhado, um mundo, lockstep permanente.

Fork significaria arrancar a fundação antes de escrever a primeira linha própria,
e cada atualização do upstream ficaria mais cara conforme a divergência cresce,
justamente no núcleo.

O que se reusa é **conhecimento**, não código: os pontos do jogo que quebram
determinismo, e o porquê. Cada linha da lista de determinismo dele foi paga com o
bug de alguém, e isso vale mais que o código em si. Ver [[Determinismo]].

## O que se herdou, e o que se descobriu sozinho

Das 54 guardas de determinismo do Multiplayer, este projeto cobre 25. As outras
ou estão fora do alcance de uma visita (caravana, mapa-mundo, geração de mapa de
DLC), ou mudaram de forma entre versões do jogo.

Na direção contrária, quatro causas achadas aqui **não existem** na lista dele,
porque nasceram no RimWorld 1.6: a inclinação de quem atira de trás de uma
parede, o desvio que separa dois pawns na tela, o tremor de quem leva um golpe, e
a fase da rotação das torres. As três primeiras entram na origem de cada tiro.

É a prova mais direta de que o problema não estava resolvido: o mod mais maduro
do ecossistema mira uma versão do jogo em que esses caminhos não existiam.
