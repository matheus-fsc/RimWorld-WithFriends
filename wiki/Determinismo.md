# Determinismo

Duas máquinas rodam o mesmo save, no mesmo tick, com os mesmos comandos. Elas
deveriam chegar ao mesmo estado. Quando não chegam, a culpa quase nunca é da
simulação: é de alguma coisa que **não está no save** ou que **vem da tela**
entrando num cálculo que decide o jogo.

## A observação fundadora

`Verse.Rand` é **um fluxo global e mutável**. Todo sorteio do jogo sai do mesmo
gerador, em ordem. Basta um sorteio a mais de um lado, em qualquer lugar, para
que todos os seguintes saiam diferentes.

Isso significa que **uma janela de interface aberta pode mudar a simulação**, se
ela sortear qualquer coisa. E significa que a divergência aparece longe da causa:
o primeiro sintoma costuma ser um pawn andando meio décimo mais devagar,
setecentos ticks depois.

## As duas famílias

**1. Estado de processo que não está no save.** O anfitrião continua na partida
dele quando a visita começa; o visitante carrega o save. Tudo o que o jogo guarda
em memória e não grava fica diferente entre os dois: listas embaralhadas no
lugar, contadores de id, caches carimbados com o tick, fases de animação.

**2. Estado de quadro entrando no tick.** O que a tela calcula depende de quantos
quadros couberam entre dois ticks, e isso é diferente em cada máquina. Se um
desses valores entra numa conta de simulação, os dois lados divergem sem ter
sorteado nada.

## As causas já achadas

Cada linha custou pelo menos uma sessão perdida e uma comparação de diários.

| Causa | Família | Como entrava na simulação |
|---|---|---|
| Ordem dos vizinhos de uma célula | processo | lista embaralhada no lugar, antes de um sorteio |
| Células de zona embaralhadas | processo | ordem de varredura |
| Memória de alcançabilidade | interface escreve | a interface consultava 229 mil vezes num tick, e gravava o memo |
| Encerrar job do "ir aqui" | comando faltando | arrastar o marcador encerrava o job só de um lado |
| Cache de stat do jogo | processo + interface | valor carimbado com o tick, cheio do que o anfitrião jogou antes |
| Brilho do céu | quadro | `MoveSpeed` tem `StatPart_Glow`, e o brilho é atualizado por `Map.MapUpdate` |
| Tremor de quem leva um golpe | quadro | entra em `DrawPos`, que é a origem de cada tiro |
| Inclinação de quem atira de trás de cobertura | quadro | idem |
| Desvio que separa dois pawns na tela | processo | idem, e a ordem vem da lista da célula |
| Fase da rotação das torres | processo | sorteia do fluxo compartilhado (conserto preventivo) |
| Empate na ordem das roupas | processo | é a ordem em que a armadura absorve o dano |
| Vontade de ideologia | interface sorteia | **todo clique com o botão direito** tirava um número do fluxo |
| Área do designador | comando incompleto | o comando não dizia qual área, e o outro lado recusava tudo |

## O padrão que se repete

Três consertos seguidos saíram do **mesmo cálculo**, em camadas: `DrawPos` soma
cinco coisas, e `Verb_LaunchProjectile.TryCastShot` usa o resultado como origem
do projétil. Posição de desenho virou balística.

Quando isso ficou claro, o método mudou: em vez de adivinhar qual das cinco, o
rastreio passou a **gravar cada termo**. A causa seguinte apareceu numa linha:

```
tranco   tick 776  Kenta:  A (0.020, 0.132)   B (0.023, 0.150)
desenho  tick 776  Kenta:  A (110.520, 90.632)  B (110.523, 90.650)
```

## O método

1. Os dois lados gravam um diário: impressão digital por tick, estado de cada
   pawn e de cada projétil, e a sequência de sorteios por local de chamada.
2. Quando a digital acusa, os dois despejam a janela que têm.
3. `wf comparar` aponta o **primeiro sorteio diferente** e o primeiro estado
   diferente, distinguindo causa de consequência.
4. Se o instrumento não responde, ele ganha um campo novo. Depois disso a causa
   aparece sozinha.

Hipótese que a medição derrubou vale tanto quanto conserto, e fica registrada:
o multiplicador de clima foi medido e refutado (`mult` exatamente 1 dos dois
lados por 400 ticks), e a ordem das listas de célula também (digitais idênticas
no início da visita).

## Quando diverge mesmo assim

A visita não aborta. Ela volta a um **ponto de junção**: os dois recarregam o
mesmo estado e seguem. Se a divergência voltar logo depois, aí sim o encontro
acaba, porque refazer o ponto não resolveria. A colônia dos dois volta ao
checkpoint pré-sessão.

Perde-se o encontro, nunca a colônia.
