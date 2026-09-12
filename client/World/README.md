# client/world

Camada de mundo (§2.1): log de eventos append-only consumido no próprio ritmo.

| Arquivo | Papel |
|---|---|
| `WorldCursorComponent.cs` | `world_cursor` — até onde este cliente já consumiu, dentro do save |
| `MundoAplicador.cs` | aplica evento ao planeta local e avança o cursor |
| `MundoPublicador.cs` | publica o próprio assentamento: nome, tile, riqueza estimada |
| `AssentamentoRemoto.cs` | o `WorldObject` da colônia alheia no mapa-mundo |

O `Def` correspondente está em `client/Defs/WorldObjectDefs/`.

## O que o outro jogador vê de você

Só o que a §4 permite sem presença física: **nome, riqueza estimada e status
online**. A riqueza é arredondada de propósito — ordem de grandeza, não a sua
contabilidade. Não existe mapa transferido, não existe conteúdo, não existe
como espiar.

Isso não é sabor vanilla: fora de sessão não existe estado de mapa
compartilhado, então não existe o que divergir. O mapa **é** transferido — mas
uma vez, no início da sessão, e só o da sessão (§4).

## O planeta é gerado, não sincronizado

Eventos de mundo carregam **índice de tile**, não coordenada. O mesmo índice em
outro planeta aponta para outro lugar ou não existe — e índice inválido entra
direto no `WorldGrid` e quebra o jogo longe da causa.

Por isso `IdentidadeDoPlaneta` hasheia as variáveis de geração (semente,
cobertura, chuva, temperatura, população, densidade de marcos, poluição) e o
coordenador guarda o planeta de registro do mundo. Quem chega com outro
continua logado e jogando; só fica fora do mundo compartilhado (§8, §11).

`MundoAplicador` ainda confere `WorldGrid.InBounds` antes de criar qualquer
objeto: defesa em profundidade, porque um payload inválido nunca pode virar
índice de array.

## Por que não há conflito de ordem

Quem numera é o servidor. O cliente propõe um fato (`mundo.publicar`, sem
`seq`), o servidor atribui a ordem global e difunde. Dois jogadores publicando
no mesmo instante recebem seqs diferentes, e todo mundo aplica na mesma
ordem — por construção, não por acordo.

Quem estava offline pega o atraso pelo cursor ao reconectar. É isso que faz o
mundo compartilhado funcionar sem ninguém online (§11).
