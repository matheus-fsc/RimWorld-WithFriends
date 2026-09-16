# With Friends

Mod de multiplayer para RimWorld 1.6.

> **Assíncrono por padrão. Síncrono por encontro.**

Cada jogador simula a própria colônia, no próprio ritmo, com save próprio. Quando
dois jogadores precisam interagir de verdade, entram numa **sessão**: uma janela
curta, aceita pelos dois, determinística, com ponto de retorno imediatamente
antes dela.

Fora de sessão não existe sincronia por tick. Nenhuma. E jogar sozinho nunca é
bloqueado pela ausência de ninguém.

## Por onde começar

| Página | O que responde |
|---|---|
| [[Por que existe]] | o que este mod faz que os dois existentes não fazem |
| [[Estado atual]] | o que funciona hoje, com números, e o que falta |
| [[Determinismo]] | por que duas máquinas divergem, e as treze causas já achadas |
| [[Bancada]] | como reproduzir uma visita inteira sem ninguém clicando |

## A premissa que autoriza o resto

O nome é a premissa: RimWorld **com amigos**. Um grupo pequeno que se conhece,
não um servidor com oitenta jogadores anônimos.

Isso não é modéstia, é o que torna viáveis as decisões caras do projeto. A visita
pode custar, porque acontece poucas vezes por sessão de jogo, com aviso e
consentimento. Uma fila de um encontro por vez basta. E confiança é premissa: o
coordenador não valida regra de jogo, porque os participantes se conhecem.

Projetar para oitenta desconhecidos mudaria tudo, e daria um mod pior para o caso
que importa.

## Licença e origem

Reusa mecanismos do [Multiplayer](https://github.com/rwmt/Multiplayer) (MIT,
Zetrith), em especial a detecção de divergência por estado de RNG e a abstração
de tempo. **Não é um fork**: a arquitetura aqui nega a premissa que sustenta
aquele código. Ver [[Por que existe]].
