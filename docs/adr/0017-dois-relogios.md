# ADR 0017 — Dois relógios: o dos comandos e o da simulação

**Status:** aceito
**Data:** 2026-09-12
**Origem:** construção com o jogo pausado não aparecia inteira; leitura do Multiplayer

## O problema

Com um relógio só — o tick da sessão **era** o `TicksGame` — a única forma de um
comando acontecer com o jogo pausado era simular um tick. Daí saíam dois
defeitos que pareciam de designador e eram de arquitetura:

- **pausar deixava de ser pausar**: cada ordem dada com o jogo parado avançava
  o tempo de verdade;
- **as construções escorriam**: cada passo dependia de uma ida e volta da
  barreira, então vinte designações apareciam aos pedaços.

## O que o Multiplayer faz

```csharp
RunCmds();                       // roda SEMPRE, antes de qualquer tick
if (LongEventHandler.eventQueue.Count == 0) DoUpdate(out var worked);
```

```csharp
foreach (ITickable tickable in AllTickables) {
    if (tickable.TimePerTick(tickable.DesiredTimeSpeed) == 0) continue;  // pausado: não simula
    TickTickable(tickable);
}
ConstantTicker.Tick();
Timer += 1;                      // o Timer avança mesmo pausado
```

Dois relógios: `TickPatch.Timer`, contra o qual os comandos são carimbados, anda
sempre; os ticks de cada mapa são pulados quando aquele mapa está pausado.

## A decisão

O mesmo desenho, com os nossos nomes:

```
para cada passo liberado pela barreira:
    aplicar os comandos daquele passo      ← sempre
    DoSingleTick()                          ← só se não estiver pausado
```

`SessaoCliente.TickDeSessao` deixou de ser `TicksGame - tickGameNoInicio` e virou
contador próprio — o **passo**. `ExecutarPasso(simular)` monta o contexto
determinístico, aplica os comandos e, se for o caso, roda um tick.

O freio mudou de pergunta junto. Era "este tick de jogo passou do liberado?";
virou "quem está pedindo?" — só o laço de passos tickia, porque só ele sabe se o
passo é de simular ou só de aplicar comando.

## As consequências, ditas antes de implementar

**O "tick de sessão" não é mais "tick simulado".** É um passo. Tudo que derivava
de `TicksGame` passou a derivar dele: o freio, a amostragem da digital, os dois
rastreios. Foi aí que concentrei o cuidado — é onde um erro passaria despercebido.

**A digital passa a ser amostrada em passos sem simulação.** Barato, e na
verdade útil: prova que os dois lados estão alinhados mesmo parados.

**Um risco novo, que se concretizou na primeira sessão:** os dois lados podem
discordar sobre **quais passos simulam**.

```
passo 404  J1 +0 sorteios   J2 +12
passo 405  J1 +0 sorteios   J2 +11
```

Mesmo número de passo, um simulando e o outro não. A causa era `simular` ser
decidido localmente (`Find.TickManager.Paused`) — bastou os dois lados
discordarem por um instante sobre estar pausados.

**A regra que faltava: se um passo simula ou não é decisão compartilhada.** Quem
decide é a velocidade acordada, que vem do coordenador. Ou os dois simulam aquele
passo, ou nenhum.

A previsão local da pausa continua existindo, mas mudou de forma: pausar
localmente **para de consumir passos** até o coordenador concordar, em vez de
consumir passos sem simular. Parar é sempre seguro — simular menos do que foi
liberado nunca sai na frente de ninguém. O que não é seguro é andar o passo de um
jeito diferente do outro lado.

O retrato da partida passou a mostrar os dois números lado a lado
(`passo N (X simulados, Y só com comando)`), porque essa é a comparação que
denuncia o caso. E denunciou, na sessão seguinte:

```
J1: passo 393 (368 simulados, 25 só com comando)
J2: passo 393 (367 simulados, 26 só com comando)
```

Mesmo passo, um simulado a mais de um lado — a primeira correção não bastou.

### "Compartilhada" tinha de ser por passo, não por instante

Usar a velocidade acordada resolve *quem* decide, mas não *quando*. Cada cliente
recebe a mudança em um momento diferente: um executa o passo 367 antes de a pausa
chegar, o outro depois. Mesmo número de passo, decisões opostas.

A velocidade agora vem com o **passo a partir do qual vale**
(`VelocidadeDesdePasso`), e o corte é o primeiro passo que ninguém podia ter
executado ainda — o relógio no instante da mudança. Quem já passou por ele usou a
velocidade antiga dos dois lados; quem não passou usará a nova dos dois lados.

O cliente guarda as últimas mudanças e responde `PassoSimula(passo)` por número,
dentro do laço. Uma mudança no meio do laço passa a valer exatamente do passo
dela em diante.

**A lição, que vale para todo o desenho:** num sistema de passos, qualquer
decisão que afete simulação tem de ser função do **passo**, nunca do relógio de
parede nem do que cada lado já ficou sabendo.

**O que não mudou:** checkpoint, rollback e o carimbo de comando no servidor já
trabalhavam neste modelo. A ADR 0016 é pré-requisito — sem o relógio no
coordenador, não haveria o que separar.

## O off-by-one que veio junto

Sintoma em jogo, e ele descreve o defeito melhor que qualquer explicação:

> construo uma linha com 10 paredes, nada aparece; construo 1 parede, aparecem
> as 10 — mas não a nova.

`TickLiberado` é o **primeiro passo que ainda não pode ser executado**: o cliente
anda enquanto `passo < liberado`. Pausado, o coordenador empurrava o relógio
exatamente **até** o passo do comando — que então ficava no limite, e o laço
parava antes dele. Só o comando seguinte, ao empurrar o limite, fazia o anterior
rodar.

A correção é uma linha (`relogio = alvo + 1`), mas a lição é de vocabulário:
"liberado até X" precisava dizer se X está dentro ou fora, e não dizia. O teste
agora afirma a relação estrita, com o motivo escrito.

## A fronteira mordeu duas vezes

Separar os relógios trocou o significado de "estou no passo N", e duas guardas
ficaram com o significado antigo.

**No coordenador.** `TickLiberado` é o primeiro passo que **não** pode ser
executado. Empurrar o relógio até o passo do comando deixava esse passo fora, e
a ordem só acontecia quando a próxima empurrasse o limite. Sintoma: dez paredes
não apareciam; a décima primeira fazia as dez aparecerem, menos ela.

**No cliente.** `TickDeSessao` é o **próximo** passo, não o último executado. A
guarda de comando atrasado era `TickAlvo <= agora`, herdada de quando o passo era
o tick de jogo e "estou em N" queria dizer "já simulei N". Com os relógios
separados, ela passou a recusar comandos válidos — e recusar ali **encerra a
sessão**:

```
comando chegou tarde: agendado para o tick 1156, e este lado já está em 1156
```

Os dois casos são o mesmo erro em espelho, e nenhum apareceu enquanto o relógio
andava sozinho com o tempo — o limite passava por cima do alvo de qualquer jeito.
Só quando o passo passou a ser empurrado **exatamente** até o comando é que a
ambiguidade virou bug.

**Regra que ficou:** todo número de passo que atravessa a fronteira precisa dizer
se é inclusivo ou exclusivo, e o teste precisa afirmar qual.

## O que ficou de fora

O coordenador continua concedendo **um passo por lote de comandos** com o jogo
pausado, em vez de deixar o relógio correr livre. É mais barato e resolve o que
importa, agora que o passo não simula: as ordens acontecem sem o tempo andar.

Se a latência de um passo por ida e volta ainda incomodar, o próximo degrau é o
relógio de comandos correr livre durante a pausa — que é literalmente o que o
`Timer` do Multiplayer faz.
