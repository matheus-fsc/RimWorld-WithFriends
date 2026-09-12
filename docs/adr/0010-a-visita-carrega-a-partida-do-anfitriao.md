# ADR 0010 — A visita carrega a partida do anfitrião, não só o mapa

**Status:** aceito
**Data:** 2026-09-11
**Substitui:** a premissa de "transferir o mapa" da [ADR 0007](0007-troca-de-estado-antes-do-lockstep.md)

## O que o teste mostrou

Inserir um mapa serializado numa partida em andamento falhou, e os erros são
específicos:

```
Could not resolve reference to object with loadID Ideo_8 of type RimWorld.Ideo
Could not resolve reference to object with loadID ApparelPolicy_Anything_1
Could not resolve reference to object with loadID DrugPolicy_Social drugs_1
Could not resolve reference to object with loadID Thing_Human342 of type Verse.Pawn
   curParent=RimWorld.DirectPawnRelation
Could not do PostLoadInit on RimWorld.Pawn_IdeoTracker: NullReferenceException
Exception while rebuilding dirty regions: ArgumentOutOfRangeException
```

Todos apontam para a mesma coisa: **um mapa não é autocontido.**

Os pawns dele referenciam objetos que vivem na *partida*, não no mapa:

| Referência perdida | Onde mora de verdade |
|---|---|
| `Ideo_8` | `IdeoManager`, no `Game` |
| `ApparelPolicy`, `DrugPolicy` | `Game.outfitDatabase`, `drugPolicyDatabase` |
| `Thing_Human342` (relações) | outro pawn, que pode estar em outro mapa ou no mundo |
| facções, pesquisa, história | `World` e `Game` |

Serializar "só o mapa" produz um arquivo que abre — a medição anterior estava
certa quanto a isso — mas que **não se reconecta** a nada quando aberto em
outra partida.

E há um segundo problema, independente: os ids de coisa são contadores por
partida. Dois jogos independentes geram ids sobrepostos, então inserir coisas
de um no outro colide. Foi o que os `Tried to pass pawn X to world, but it's
already here` mostraram.

Esta é exatamente a categoria de bug do [RT #273] que a §4 cita — e agora
sabemos por que ela existe: não é implementação desleixada, é uma
impossibilidade de desenho.

## Decisão

**O bootstrap da visita transfere a partida do anfitrião, não o mapa.**

Durante a visita, o visitante joga **dentro da partida do anfitrião**, como uma
facção distinta (multifacção, §5). Isso resolve as duas causas de uma vez:

- Referências cruzadas resolvem, porque tudo o que o mapa referencia veio
  junto — ideologias, políticas, facções, relações, mundo.
- Não há colisão de id, porque existe **um espaço de ids só** enquanto a visita
  dura.

E resolve de brinde o que a [ADR 0009](0009-rng-isolado-por-sessao.md) pedia: a
colônia privada do visitante nem está carregada durante a visita, então não há
nada para congelar nem RNG para isolar — os dois lados estão rodando a mesma
partida.

## O custo é aceitável, e já foi medido

| | Cru | Comprimido | Serializar |
|---|---|---|---|
| Só o mapa | 11,09 MB | 0,64 MB | 915 ms |
| **Partida inteira** | 13,12 MB | **1,31 MB** | 1.382 ms |

Dobrar o tamanho comprimido e somar meio segundo é barato pelo que se compra:
um estado que **funciona**. E confirma por outro caminho a escolha do
Multiplayer, que faz save completo e recarrega para entrar num jogo — não era
simplicidade, era necessidade.

## O que o visitante faz com a própria colônia

Nada, durante a visita: ela não está carregada. O ciclo é:

```
congelar → checkpoint pré-sessão → carregar a partida do anfitrião →
visita → sair → restaurar o checkpoint pré-sessão → aplicar a volta da caravana
```

O checkpoint pré-sessão, que já existe desde o M2, deixa de ser só rede de
segurança e passa a ser **parte do fluxo normal**: é para ele que o visitante
volta ao fim de toda visita, não só no aborto.

## O problema que isto abre: o retorno da caravana

Se o visitante volta ao próprio checkpoint, tudo o que ele fez na visita se
perderia — inclusive pawns feridos, mortos, e o que ganhou ou gastou.

A volta precisa ser um **delta pequeno e explícito**: os pawns que foram, como
estão agora, e o que carregam. É a mesma informação que uma caravana já
carrega no RimWorld, e é ordens de magnitude menor que um mapa.

Isso vira o assunto da próxima ADR. O que fica decidido aqui é que o retorno é
**transferência de caravana**, não reconciliação de estado — a diferença entre
"estes pawns voltaram assim" e "concilie duas versões da minha colônia".

## Consequências

- A §4 precisa ser corrigida de novo: onde diz "o mapa trafega", é a partida.
- `CongeladorDeMapas` e o isolamento de RNG da ADR 0009 **deixam de ser
  necessários** para a visita. Ficam no código por enquanto — são baratos e o
  ensaio ainda os exercita — mas saem do caminho crítico.
- `InsercaoDeMapa` vira `CarregamentoDaPartidaDaVisita`, apoiado em
  `GameDataSaveLoader.LoadGame`, que é caminho testado do jogo.
- O aborto fica **mais** simples: voltar ao checkpoint pré-sessão já é o que o
  fim normal faz.

[RT #273]: https://github.com/RimWorld-Together/Rimworld-Together/issues/273
