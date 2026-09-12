# ADR 0016 — O tick é guiado pela barreira, não pelo relógio local

**Status:** aceito
**Data:** 2026-09-11
**Origem:** dois lados pausados congelando em ticks diferentes (972 e 952)

## O problema

No vanilla, cada quadro converte tempo real em ticks conforme a velocidade que
**aquele** jogador escolheu. Numa visita isso põe os dois lados em ticks
diferentes o tempo todo, e o mais visível é a pausa: cada um congela onde está.

Medido:

```
J1 parado no tick 972
J2 parado no tick 952
        diferença = 20 = FolgaDaBarreira
```

J2 pausou em 952. J1 não pausou — correu até a barreira travá-lo em
`mínimo + folga`. Os dois "parados", por motivos diferentes, exatamente uma
pista de distância. E nada os aproxima: relógio pausado não anda por mais
liberação que receba.

Disso saíam coisas que eu vinha tratando como bugs separados:

- ordem dada com o jogo parado não fazia nada até alguém despausar;
- aplicar comando "com todos pausados" divergiu, porque "todos pausados" não é
  "todos no mesmo tick";
- velocidade como comando gerou três bugs seguidos — eco, pausa desfeita, dois
  cliques — porque a resposta chega sempre depois da intenção mais nova;
- o consenso de pausa precisava ser uma regra à parte, com o servidor segurando
  a barreira.

## A decisão

**O coordenador mantém o relógio.** Ele avança no ritmo acordado e publica até
onde é seguro simular; o cliente corre até lá o mais rápido que consegue.

```csharp
// servidor
relogio += segundos * 60 * MultiplicadorAcordado(pedidos);
if (relogio > minimoRelatado + FolgaDaBarreira) relogio = minimoRelatado + FolgaDaBarreira;

// cliente
while (TicksGame < LimiteDeTick) DoSingleTick();   // com orçamento de 45 ms por quadro
```

A velocidade local deixa de ser comando e vira **pedido**, que viaja no relato
da barreira.

### O controle do tempo é comum, não é veto

A primeira versão fez "o mais lento manda", lendo §3 ao pé da letra. Em jogo o
defeito aparece na hora: **pausado, só quem pausou consegue despausar** — o
pedido do outro é sempre o mais rápido dos dois, e perde sempre. Controle que
responde a uma pessoa por vez não é compartilhado; é um veto que muda de dono.

Também havia a alternativa "só o anfitrião manda no tempo da colônia dele", que
casaria com a autoridade de comando. Foi descartada por uma consequência que só
aparece olhando adiante: durante a visita o visitante continua dono da própria
colônia, e tirar dele o controle do tempo exigiria **um segundo controlador de
tempo na interface** — além de deixar a imagem estranha de visitantes parados no
tempo enquanto algo acontece na base deles.

Então: **um relógio só, que qualquer um dos dois muda.** Quem mexeu no botão
manda, para todos, inclusive para despausar. Repetir o pedido não conta como
mexer — senão o relato periódico de um lado desfaria a mudança recém-feita do
outro.

E o relógio local dos dois passa a **mostrar** a velocidade que está valendo:
botão que mostra outra coisa é pior que botão que não obedece — e aqui ele
obedece, só que a quem mexeu por último, que pode ser o outro.

## O que sai de graça

**O consenso de pausa deixa de ser regra.** Pausado é multiplicador zero, e
relógio com ritmo zero não anda por mais tempo que passe. "Parado" também deixa
de ser dois estados: não existe mais um lado parado e o outro andando.

**Ninguém mexe no botão de ninguém.** Toda a família de bugs de velocidade
nasceu de um lado impor a velocidade ao outro. Agora cada relógio é do seu dono;
o que é compartilhado é o tick.

**Os dois lados ficam sempre no mesmo tick**, que é a premissa que faltava para
aplicar comando com o jogo parado. Isso volta a ser possível — mas fica para
depois, com a bancada estável.

**A pista muda de significado**: era "quanto simular além do mais lento", agora é
"quanto o relógio pode se afastar do mais lento". Continua sendo o que absorve
rede e quadro ruim sem deixar ninguém para trás.

## O deadlock que ela criou, e o padrão que se repete

A primeira versão relatava a velocidade só depois de a visita começar — antes
disso, `Pausado`. Resultado: a visita não começava.

```
o relógio não anda porque ninguém pede tempo
a barreira não libera o primeiro tick porque o relógio não anda
a visita não começa porque a barreira não libera
ninguém pede tempo porque a visita não começou
```

Antes de a visita começar, os dois lados estão congelados **por nós**, esperando
o outro entrar. Relatar isso como "peço pausa" é dizer que o jogador quer o tempo
parado, e ele não quer — ele está esperando.

É a **quarta** vez que o mesmo engano aparece: congelamento mecânico tratado como
intenção do jogador. As outras três foram `visitaComecou` na bandeira de pausa, o
eco de velocidade, e a pausa imposta indistinguível da pedida.

A regra, agora escrita onde dói: **todo estado que a sessão impõe precisa de par
— o imposto e o pedido — e o que trafega é o pedido.**

## O servidor como ponte, não como intermediário do clique

A primeira versão respondia certo e **parecia emperrada**: o clique ia ao
servidor e só valia na volta, então o jogo continuava andando por um instante
depois de a pessoa mandar parar. Tecnicamente correto, mecanicamente estranho.

A separação que faltava:

| | quem decide |
|---|---|
| **até onde** é seguro simular | o relógio do coordenador |
| **quanto disso** eu consumo agora | o jogador, na hora |

O cliente passa a acumular um orçamento local de ticks conforme a velocidade que
**ele** escolheu, e gasta esse orçamento dentro do que já foi liberado.

**Pausar e desacelerar ficam instantâneos**, e são seguros porque só reduzem:
simular menos do que foi liberado nunca sai da frente de ninguém. Pausado, o
laço nem começa.

**Acelerar continua esperando a volta** — não dá para simular o que ainda não foi
liberado. Mas é uma ida e volta, e nessa direção não se sente.

Com isso o servidor deixa de estar no caminho do dedo do jogador e vira o que ele
deveria ser: a ponte que mantém os dois no mesmo tick.

Dois detalhes que o laço precisa ter:

- **teto do orçamento** (3 ticks): pedir Superfast enquanto o relógio libera 1×
  acumularia crédito, e o jogo dispararia em rajada quando a liberação chegasse;
- **correr atrás** quando o atraso passa de 4 ticks: um engasgo de quadro não
  pode virar atraso permanente, porque quem fica para trás segura o relógio dos
  dois — ele nunca se afasta do mais lento.

## O botão não conta a história toda

Com previsão local, faltava uma coisa: o botão do RimWorld mostra a velocidade
**local**, e o tempo é compartilhado. Quando o outro jogador mexe, há um instante
em que os dois não dizem a mesma coisa.

E havia um defeito de verdade junto. O laço perguntava `CurTimeSpeed == Paused`,
mas o jogo define:

```csharp
public bool Paused => curTimeSpeed == 0 || ForcePaused;
```

`ForcePaused` é a pausa que uma **janela** impõe — a carta de um raid, um
diálogo. Olhando só o botão, o relógio aparecia pausado e o jogo continuava
tickando: exatamente o "relógio na posição pausada e o jogo na velocidade 2".
Agora o laço respeita `Paused`, que é a pergunta certa.

Para o resto, em vez de brigar com o botão do jogo — coisa que já custou caro —
uma etiqueta ao lado dos controles de tempo diz a verdade em voz alta:

```
visita: 2×
visita: pausado — parado por uma janela aberta aqui
```

Só aparece durante a visita, e só diz o que o botão não consegue dizer.

## O tempo virou mecânica

As regras de quem pausa, quando dá para despausar e o que a etiqueta mostra
deixaram de ser consequência da implementação e viraram desenho de jogo. Estão
em `docs/MECANICA-DO-TEMPO.md`.

O que esta ADR sustenta é a base técnica delas: um relógio só, guiado pelo
coordenador, com previsão local para o clique responder na hora.

## Trocar o laço é herdar as guardas dele

Dois crashes, precedidos sempre do mesmo prólogo:

```
Could not regenerate layer Verse.SectionLayer_FogOfWar: NullReferenceException
  at Unity.Collections.LowLevel.Unsafe.UnsafeBitArray.IsSet
  at Verse.SectionLayer_FogOfWar.Regenerate
…
Got a SIGSEGV while executing native code
```

Camada de mapa lendo estrutura nativa já liberada. Depois disso o processo morre
dentro do coletor, e o rastro nativo não aponta para a causa — foi o mesmo
disfarce dos crashes do rastreio de RNG.

A causa é de desenho: **o vanilla não chama `TickManagerUpdate` em qualquer
momento.** Ele tem guardas em volta, e ao substituir o laço eu fiquei com o laço
e sem as guardas. Tickar durante a troca de partida da visita — partida antiga
saindo, nova entrando — é exatamente o intervalo em que o mapa não existe
inteiro.

As guardas de volta, agora explícitas:

| condição | por quê |
|---|---|
| `ProgramState == Playing` | fora do jogo não há o que tickar |
| `Current.Game != null` | idem, e o nulo aqui é o caso da troca |
| sem evento longo em curso | carregamento, geração, troca de save |
| não está trocando de partida | o intervalo da visita |
| `Find.Maps` não vazio | mapa desmontado |

Falhando qualquer uma, não tickamos — e **não devolvemos para o vanilla**, que
tickaria no ritmo local. O ritmo não é dele.

**A lição.** Substituir um laço do jogo é herdar as pré-condições dele. Elas não
estão escritas no método que se remenda: estão espalhadas em quem o chama, e
some-se com elas sem perceber.

## O preço

A velocidade que o jogador escolhe pode não ser a que o jogo anda — se o outro
mexeu depois, vale a dele. É a consequência honesta de um controle compartilhado:
o botão é dos dois, então às vezes ele se move sozinho.

O cliente também deixa de usar a cadência do vanilla, com seu alisamento de
quadro. O orçamento de 45 ms por quadro existe para um cliente atrasado alcançar
o relógio em alguns quadros em vez de travar a interface tentando de uma vez.
