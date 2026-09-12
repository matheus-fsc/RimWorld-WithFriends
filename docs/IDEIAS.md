# Ideias

> Coisas que ainda não são decisão. ADR é para o que foi decidido; aqui fica o
> que vale considerar quando o momento chegar.

## Saudade: separação de quem se ama pesa no humor

**De onde veio:** do primeiro teste da mala do visitante. O log avisou:

```
Pawn Rachel has relation "Bond" with null pawn after loading.
```

O cachorro da Rachel ficou em casa. Tecnicamente, a relação foi cortada porque
o animal não foi na caravana — comportamento correto. Mas ler aquilo sugere
outra coisa: **e se ficar longe custasse alguma coisa?**

**A ideia:** pawns com vínculo afetivo ganham um estado de *saudade* ao passar
tempo longe do outro. Depois de X tempo separados, entra um pensamento negativo
que cresce com a duração.

Quem contaria:

- **animais ligados** (`Bond`) — o par mais evidente, e o que originou a ideia;
- **relações íntimas** — cônjuge, amante, filho.

**Por que combina com este projeto:** a visita é um evento que separa pessoas
de propósito (§5). Hoje a separação é gratuita — o colono some por dias e nada
acontece. Com saudade, decidir quem vai na caravana vira uma escolha com peso,
e voltar para casa vira alívio em vez de logística. Num simulador de histórias,
isso é conteúdo, não atrito.

**O jogo já tem precedente**, então não é mecânica estranha ao RimWorld:

| Existente | O que faz |
|---|---|
| `ThoughtWorker_PsychicBondProximity` | vínculo psíquico (Biotech) pesa conforme a distância |
| `Alert_PsychicBondedSeparated` | o jogo **já alerta** sobre separação de quem tem vínculo |
| `ThoughtWorker_BondedAnimalMaster` / `NotBondedAnimalMaster` | humor conforme a presença do animal ligado |

Ou seja: a peça a construir é um `ThoughtWorker` que olha distância e tempo, no
molde de coisas que já existem — não um sistema novo.

**Perguntas em aberto:**

- Vale só entre mapas/partidas diferentes, ou também dentro do mesmo mapa?
- A saudade acumula com o tempo ou é um degrau depois de X dias?
- Reencontro dá bônus? (provavelmente sim — é o que fecha o arco)
- Entra como parte do mod ou como mod separado? Não tem nada de multiplayer
  nela; funcionaria em jogo solo, o que talvez seja argumento para separar.

**Status:** ideia. Não está no roadmap (§13) e não bloqueia nada.

## Prepatcher: quando valeria a pena

O Multiplayer depende do [Prepatcher](https://github.com/Zetrith/Prepatcher)
(MIT, Zetrith). Vale saber o que ele faz de fato, porque o nome sugere "mais um
Harmony" e não é.

**O que ele é:** um reescritor de assembly que roda **antes** de o jogo
carregar. Três capacidades:

| Capacidade | Como se usa | Para que serve |
|---|---|---|
| **Campos novos em classes do jogo** | `[PrepatcherField] public static extern ref int MeuCampo(this Map map);` | anexar estado a `Map`, `Thing`, `Pawn` sem dicionário e sem lookup |
| **Reescrita de IL** | `[FreePatch] static void F(ModuleDefinition module)` | injetar prefixo direto no corpo do método, ou remover instruções |
| **Provedor de Harmony** | — | satisfaz a dependência `brrainz.harmony` sozinho |

O MP usa as três. O `AddPrepatcherPrefix` deles injeta a chamada do prefixo
direto no IL do método, o que é mais barato que uma intercepção de Harmony em
caminho quente.

**Onde nos ajudaria, concretamente:**

1. **`Thing.DoTick`.** Nosso prefixo roda para **toda coisa, todo tick** — com
   34 mil coisas no mapa medido, é o caminho mais quente do jogo. Hoje ele sai
   cedo por duas leituras de `bool` estático, mas a chamada em si tem custo.
2. **Estado por coisa ou por mapa**, se algum dia a sessão precisar (marcar
   quem pertence à visita, estado de RNG por mapa). Hoje não precisamos —
   `GameComponent` e estáticos dão conta.
3. **Correções de determinismo que exigem mexer no corpo do método**, como o
   MP faz com `MoteCounter.Saturated` via transpiler.

**Por que não agora:**

- O ganho é de desempenho, e desempenho ainda não é problema: a visita é curta
  (§1.1) e o prefixo já sai cedo.
- Vira **dependência obrigatória** para os jogadores. Ela substitui o mod
  Harmony, então o número de mods não aumenta — mas o acoplamento sim.
- Mais uma peça na cadeia de build (pacote NuGet) e mais uma coisa que pode
  quebrar numa atualização do RimWorld.

**Gatilhos que mudariam a resposta:**

- `Thing.DoTick` aparecer em profiling durante uma visita;
- precisarmos de estado por coisa que hoje seria um dicionário grande;
- uma correção de determinismo que só saia com reescrita de IL.

Até lá, fica anotado: é uma ferramenta que resolve problemas que ainda não
temos, e adotar antes da hora é pagar acoplamento adiantado.

## Ressincronizar em vez de abortar

Hoje, divergência que não reconverge em 3 relatos aborta a sessão e devolve as
duas colônias ao checkpoint pré-sessão. Seguro, mas caro: perde-se o encontro
inteiro por causa de um tick.

O maquinário para a alternativa já existe e já é exercitado em toda visita — o
bootstrap: o anfitrião serializa a partida, comprime e envia; o visitante carrega
e retoma no tick combinado. Medido: 10.45 MB → 1.17 MB em 599 ms de um lado,
95 ms para receber do outro.

Então, em vez de abortar: o anfitrião vira autoridade, reenvia o estado, o
visitante recarrega e a visita **continua**. Desync deixa de ser fatal e vira
soluço.

Isto é a intuição de "o anfitrião decide" aplicada na granularidade que funciona
— estado, não sorteio. Ver a seção sobre isso em `MECANICA-DA-VISITA.md`.

Não substitui reduzir a taxa de divergência: ressincronizar a cada poucos
segundos seria pior que abortar. Os dois se somam.
