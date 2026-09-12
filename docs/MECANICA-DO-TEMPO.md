# A mecânica do tempo compartilhado

Numa visita o tempo é de dois. Isso não é um detalhe de sincronia — é uma
mecânica de jogo, e ela precisa de regras pensadas em vez de emergir do que for
mais fácil de implementar.

## A premissa

O ambiente ideal da visita é **comunicação contínua**, com os dois decidindo
juntos. Todas as regras abaixo saem daí: elas não tentam impedir conflito, elas
tentam dar tempo para a conversa acontecer.

## Regra 1 — Pausar é prioritário, e qualquer um pausa a qualquer instante

Pausar quase nunca é "quero parar". É **pedido de atenção**: alguém viu uma
ameaça que o outro não viu e quer que os dois pensem antes de continuar.

Por isso pausa não negocia. Chegou, valeu.

## Regra 2 — Despausar é consenso, e qualquer um pode

Chegando a um acordo, qualquer um dos dois despausa. O caso comum de dois
despausando quase junto é inofensivo: o resultado é o mesmo, o jogo anda.

## Regra 3 — Número é seguro; espaço não é

Há duas formas de mexer no tempo, e elas têm naturezas diferentes:

| forma | natureza | risco |
|---|---|---|
| teclas `1 2 3` (e `4` em debug) | **absoluta** — "2" sempre quer dizer 2× | nenhum: o último ganha, o jogo segue fluido |
| `espaço` | **alternador** — "o contrário do que estou vendo" | inverte quando o que você vê está velho |

O alternador é o problema, porque "o que estou vendo" pode estar atrasado uma ida
e volta.

**Caso inofensivo** — jogo pausado, os dois apertam espaço:
despausa e pausa de novo. A conversa resolve: *"opa, despausou aí também"*.

**Caso que mata** — jogo andando, um aperta espaço para parar; no mesmo instante
o outro aperta espaço, mas a pausa do primeiro **já chegou na tela dele**, então
o espaço dele quer dizer despausar. A pausa dura um piscar e o combate segue.

## Regra 3.1 — Pausa não entra em fila

O relato de barreira é periódico: sai a cada 250 ms, ou quando um tick alinhado
pede. Clique não é periódico.

Enquanto o clique esperava a vez no relato, o outro lado seguia correndo. A 3×,
250 ms são dezenas de ticks de combate acontecendo **depois** de alguém mandar
parar — e a pessoa aperta de novo achando que falhou, o que no alternador vira um
pedido de **despausar**.

Mexeu no tempo, o relato sai na hora. Pausa é pedido de atenção (Regra 1), e
pedido de atenção não entra em fila.

## Regra 3.2 — O clique só é descartado por algo posterior a ele

Com previsão local, o cliente guarda o que pediu e ignora respostas em trânsito,
que falam de antes do clique. Falta decidir **quando** desistir do pedido — e a
primeira tentativa perguntou a coisa errada:

> o outro mudou por último?

Isso continua verdade muito depois de o outro ter mexido. Então todo clique novo
era descartado como se já tivesse sido superado, e o jogador precisava clicar
várias vezes até um pedido chegar antes da próxima resposta. Exatamente o
"depois que o outro altera, tem que spamar".

A pergunta certa é **se o servidor mudou o tempo depois de eu pedir**, e para
isso não serve autoria: serve um contador que só cresce. O cliente guarda a
versão que valia quando pediu; só uma versão maior encerra o pedido.

Relato periódico não mexe na versão — ela conta mudanças, não passagem de tempo.

Meio segundo depois de uma pausa, sair dela é recusado — venha por espaço ou por
número.

**A proteção nunca atrasa uma pausa.** Ela só recusa a saída; pausar é sempre
imediato, em qualquer situação. Se pausar parecer lento, o problema está em outro
lugar — foi o caso da Regra 3.1.

Meio segundo cobre a ida e volta mais o dedo do outro, e é curto demais para
atrapalhar quem quer mesmo continuar: basta apertar de novo. A recusa volta
explicada, porque tecla que não faz nada parece tecla quebrada:

> *"Alguém acabou de pausar. Aperte de novo se quiser mesmo continuar."*

A proteção vale para qualquer saída da pausa, e não só para o espaço. O
alternador é o que **cria** o caso ruim, mas dentro daqueles 500 ms um número
desfaz a pausa do mesmo jeito — e a pausa é justamente o que não pode ser
desfeito por acidente.

## Regra 5 — Dá para ver quem mexeu

Pedido de atenção sem remetente não chama ninguém. A etiqueta ao lado dos
controles de tempo mostra quem tomou a atitude:

```
math pausou
math: 2×
você pausou
math: pausado (janela aberta aqui)
```

O sufixo entre parênteses é o `ForcePaused` do jogo — pausa que uma janela impõe,
sem ninguém ter pedido. É a única coisa que o botão nunca consegue explicar
sozinho.

### A cor diz para quem o aviso é

| quem mexeu | como aparece |
|---|---|
| você | cinza discreto |
| o outro | amarelo forte, esmaecendo em 5 s |

A etiqueta não é placar, é **aviso**. Quem apertou a tecla já sabe o que fez.
Quem não apertou é justamente quem precisa reparar que o tempo mudou por decisão
do outro — e esse é o caso que a Regra 1 existe para atender.

O destaque esmaece porque aviso que fica aceso para sempre vira parte do cenário
e deixa de avisar. Mas não volta ao cinza: mesmo assentada, a cor continua
diferente da própria, porque saber de quem foi a última decisão sobre o tempo é
útil depois do susto também.

## Regra 6 — Dá para mandar com o jogo parado

Metade do jeito de jogar RimWorld é com o jogo pausado: para, olha, dá as ordens,
despausa. Numa visita isso esbarrava numa restrição do desenho — **comando só
acontece dentro de um tick, e parado não há tick**. Alistar um colono com o jogo
pausado não fazia nada até alguém despausar.

A saída é o relógio dar **um passo**, e todas as ordens pendentes couberem nele:
os comandos acontecem naquele tick e o jogo para de novo. Um tick a 1× são 16 ms
de simulação — não se vê.

A primeira versão dava um passo **por ordem**, e isso aparecia: oito designações
viravam oito ticks de simulação com o jogo "parado", e a construção surgia aos
pedaços em vez de de uma vez.

O passo é reaproveitado enquanto ele **ainda não foi liberado**. Carimbar e
liberar são dois momentos: o comando nasce com o passo marcado, e a liberação sai
no relato seguinte da barreira. Nesse intervalo ninguém pode ter executado o
passo, e todas as ordens do mesmo clique cabem nele. Assim que a barreira libera,
o teto passa do carimbo e o comando seguinte ganha um passo novo — sem ninguém
precisar adivinhar quando o lote fechou.

Antes disso, o passo era derivado do **tick relatado pelo mais lento**, e o
carimbo saía em `mínimo + 1`. Isso quebrava de um jeito só: o mínimo é do lado
mais **atrasado**, e o comando precisa valer para o mais adiantado. Depois de
uma corrida normal os dois estão a passos diferentes — até `FolgaDaBarreira`
deles —, então a primeira construção com o jogo parado nascia para um passo que
o outro lado já tinha executado, era recusada na chegada e encerrava a visita.

Piorava porque o tick relatado nem sempre é o passo atual: com digital pendente,
o cliente relata o passo **da digital**, que é mais velho. O relatado é piso
frouxo. O único número que vale como "com certeza está no futuro de todo mundo"
é o teto que o coordenador ainda não liberou.

Comandos do mesmo tick são aplicados em ordem de carimbo, então dividir o passo é
seguro: os dois lados aplicam os mesmos comandos, na mesma ordem, no mesmo tick.

### O passo também não pode entrar em fila

O coordenador concede o passo, mas o cliente só descobre isso **na próxima
resposta da barreira**. E, parado na barreira, o relato daquele tick já saiu —
então ele caía no fallback de 250 ms.

Cada cliente no seu próprio ciclo de 250 ms significa cada um aplicando num
momento diferente: era o "a construção aparece em tempo diferente para cada
jogador", e com atraso suficiente parecia não aparecer.

Agora, comando agendado para um tick ainda não liberado faz o relato sair na
hora. É o mesmo remédio da Regra 3.1, do outro lado da conversa: **o que espera
uma resposta não entra em fila.**

Vale notar que os dois lados continuam aplicando no **mesmo tick de simulação** —
o que variava era o instante de relógio de parede em que cada um chegava lá. Para
o jogo é idêntico; para quem está olhando, não era.

A regra que não podia cair continua de pé: os dois aplicam no **mesmo** tick.

Uma tentativa anterior disto falhou por supor que "todos pausados" significava
"todos no mesmo tick" — não significava, porque cada lado parava onde estava e
nada os aproximava. Só depois do relógio único (ADR 0016) a premissa passou a ser
verdadeira, e aí a solução é de três linhas.

## Como o Multiplayer resolve o mesmo problema

Vale registrar porque as nossas regras divergem das dele **de propósito**, e
porque em um ponto os dois chegaram ao mesmo remédio sozinhos.

### Ele usa voto, e o mais lento ganha

```csharp
public TimeSpeed GetLowestTimeVote(int tickableId, bool excludePaused = false)
{
    return (TimeSpeed)playerData.Values
        .SelectMany(p => p.AllTimeVotes.GetOrEmpty(tickableId))
        .DefaultIfEmpty(TimeVote.Paused)
        .Min();
}
```

É o "mais lento manda" que experimentamos e descartamos — com o mesmo defeito:
pausado, o voto do outro nunca ganha, porque é sempre o maior.

Detalhe elegante: `DefaultIfEmpty(TimeVote.Paused)`. Jogador sem voto conta como
pausado, então quem acabou de entrar segura todo mundo até se manifestar.

### A saída dele é uma válvula, não uma regra diferente

```csharp
SendTimeVote(ShouldReset ? TimeVote.PlayerResetTickable : TogglePaused(CurTimeSpeedUI));
```

`ResetAllTimeVotes` limpa o voto de **todos**. O destravamento existe, mas como
ação separada, em vez de ser o comportamento normal do botão.

Nós fomos pelo outro caminho — último clique ganha (Regra 2) — e por isso nunca
precisamos da válvula. São duas respostas coerentes para o mesmo defeito; a dele
preserva o consenso e paga com um gesto a mais, a nossa preserva o gesto e paga
com a possibilidade de o outro desfazer o que você fez.

### A trava, os dois chegaram nela

```csharp
// Prevent multiple players changing the speed too quickly
if (Time.realtimeSinceStartup - AsyncWorldTimeComp.lastSpeedChange < 0.4f)
    return;
```

**0,4 s dele contra 0,5 s nosso.** Duas diferenças: a dele dispara quando *o
outro* muda; a nossa, ao pausar (Regra 4). E a dele bloqueia só as teclas de
atalho, não o clique no botão.

Dois projetos chegando ao mesmo remédio por caminhos independentes é um bom sinal
de que o problema é do domínio, não invenção nossa.

### Duas diferenças estruturais

| | Multiplayer | aqui |
|---|---|---|
| velocidade | é comando, agendada num tick do fluxo determinístico | é voto no relato da barreira, fora do fluxo de comando |
| clique | espera o comando voltar | responde na hora, previsão local (ADR 0016) |

A segunda explica a primeira diferença de sensação: sem previsão, ele **precisa**
da trava de 0,4 s para o clique de dois jogadores não virar bagunça. Com
previsão, a nossa trava serve só para o caso perigoso da pausa.

E a nossa velocidade só pôde sair do fluxo de comando porque o tick já é guiado
pela barreira. É menos maquinário, mas depende de uma decisão que ele não tomou.

## O que fica de fora, por enquanto

- **PvP.** Estas regras nasceram pensando em cooperação com conversa. Num
  encontro hostil, "qualquer um pausa" vira ferramenta de quem está perdendo, e a
  mecânica precisa de outra rodada de desenho.
- **Mais de dois participantes.** Com três, "consenso para despausar" deixa de
  ser óbvio: basta um esquecido para o jogo nunca voltar.
