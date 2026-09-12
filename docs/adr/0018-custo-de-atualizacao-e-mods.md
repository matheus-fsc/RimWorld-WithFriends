# ADR 0018 — O custo de atualização e a compatibilidade com mods

**Status:** aceito (direção), com uma medição pendente
**Data:** 2026-09-12
**Origem:** "estamos fazendo um interpretador que quebra a cada update, sem
contar a compatibilidade com mods"

## A preocupação, reformulada

Não dá para tornar o RimWorld determinístico por construção. São ~90 mil métodos
escritos para um jogador só, sem hierarquia que permita substituir comportamento,
e cada versão mexe em parte deles. Qualquer mod pode sortear dentro do tick sem
que ninguém saiba.

Isso é verdade, e não tem solução. A pergunta útil não é "como evitar", é **"o
que acontece quando acontece"**.

## O que já não é verdade

"Quebra a cada update" descreve um mod que não se defende. O nosso se defende em
três camadas, e elas foram construídas por causa de incidentes reais:

| camada | o que faz | de onde veio |
|---|---|---|
| `CatalogoDePatches` | confere cada alvo na subida e desliga o recurso com log legível | §14.5 |
| remendo isolado por classe | uma falha não leva as outras junto | o `PatchAll` que travou os dois jogos |
| auditor de IL | regenera a lista de riscos a cada versão, sem ninguém manter | ADR 0015 |

O auditor é a parte que responde à sua frase. **A lista de chamadores apodrece;
a lista de fontes não.** Numa versão nova, roda-se o auditor e ele diz o que
mudou de lugar — em segundos, sem jogar.

## O que ainda não sei, e como medir

**Nunca sobrevivemos a uma atualização do RimWorld.** Todo o custo de manutenção
que discutimos é estimativa.

A medição honesta: rodar o auditor contra uma versão anterior do jogo e ver
quantos alvos do catálogo ainda resolvem. Duas versões dão a taxa de erosão real,
e ela transforma "quebra a cada update" em um número.

Enquanto esse número não existir, ninguém — nem eu — deveria afirmar que o custo
é aceitável ou proibitivo.

## Mods: auditar o que está carregado

O auditor passou a varrer **todos os assemblies de mods carregados**, não só o
`Assembly-CSharp`. Um mod que sorteia dentro do tick, ou que lê a câmera para
decidir algo, quebra a visita exatamente como o jogo quebraria — e ninguém lê o
código de todos os mods que usa.

Isso não impede o problema. Faz dele uma linha no relatório, com nome do mod,
em vez de um desync sem explicação.

## As duas decisões que tornam o resto sobrevivível

Auditar reduz o desconhecido. Não zera. Para o que sobrar:

**Ressincronizar em vez de abortar.** Divergiu, o estado correto é reenviado e a
visita continua. Um mod desconhecido passa a custar um soluço de um segundo em
vez do encontro inteiro. É a única defesa que funciona contra causa que não se
conhece — e o maquinário já existe e já roda em toda visita.

**Fluxo de RNG derivado por entidade (ADR 0014).** Um sorteio a mais de um mod
deixa de contaminar a colônia e passa a afetar um pawn num tick. Divergência vira
local, e local é barato de consertar.

Juntas, elas mudam a pergunta de "conseguimos mapear tudo?" — que é não — para
"o que não mapeamos custa caro?" — que passa a ser não.

## O que não é caminho

**Servidor dedicado rodando o jogo** é viável (o Multiplayer faz, com
`-arbiter`, uma instância headless) e resolve autoridade e independência do
anfitrião. Mas **não substitui o lockstep**: enquanto o cliente desenhar o mundo,
ele precisa do estado, e sem um codificador de delta ele só obtém o estado
simulando. Ver `docs/PROGRESSO.md`.

**Reescrever o núcleo do jogo** não é rota: o mesmo acoplamento, com mais código
e sem o catálogo para avisar quando quebrar.
