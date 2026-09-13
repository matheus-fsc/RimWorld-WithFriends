# ADR 0021 — O planeta pertence ao fato, não ao coordenador

**Status:** aceito, implementado
**Data:** 2026-09-13
**Origem:** "agora conserta o planeta divergente" — o aviso que aparecia em
**todas** as corridas, para **todos** os clientes, inclusive quando os dois
estavam no mesmo planeta.

## O que estava acontecendo

O coordenador guardava **um** planeta — o do primeiro cliente que sincronizasse
— num arquivo `mundo/planeta.txt`, e recusava todos os outros:

```
[cliente] planeta divergente: este mundo é sha256:8228cab1…, o seu é sha256:ddd967aa…
[cliente] evento de mundo descartado: planeta divergente
```

O arquivo era de **11 de setembro**. O planeta gravado nele veio de um mundo de
teste que conectou uma vez e nunca mais. Dois dias depois, com os dois
jogadores no mesmo planeta novo, o coordenador recusava **os dois** em nome de
um planeta que ninguém mais tinha — e não havia como desfazer sem apagar
arquivo na mão.

O erro não era o limite: o limite é real, e é o da §4.2 (índice de tile só
significa a mesma coisa no mesmo planeta). O erro era **onde o planeta morava**.
Como propriedade global e permanente do coordenador, ele só podia envelhecer:
o primeiro a chegar decidia para sempre, e "para sempre" incluía mundos
descartados.

## A decisão

O planeta passa a ser propriedade do **fato**, não do coordenador.

- Cada evento do log é gravado com o planeta em que foi afirmado.
- `Desde(cursor, planeta)` devolve só o que vale naquele planeta.
- A difusão também filtra: `DifundirNoPlaneta`.
- Não há mais recusa. **Vários planetas coexistem** no mesmo coordenador, cada
  um com a sua fatia do log, e nenhum tem poder sobre os outros.

A numeração continua global e monotônica de propósito, atravessando planetas:
assim o cursor de um jogador nunca é invalidado por alguém ter aberto outro
mundo.

O planeta de cada conexão é declarado no `mundo.sincronizacao_cursor` e
**redeclarado a cada partida carregada** — inclusive ao atravessar para a
colônia de outro jogador numa visita. É um fato do presente, não do registro.

### O que sobra: informação, não recusa

Dois amigos que geraram mundos diferentes não se veem, e sem aviso isso parece
o mod quebrado. Então o coordenador avisa — mas só quem está **sozinho** no
próprio planeta havendo gente em outro, uma vez por planeta, e com a
**descrição** do planeta dos outros:

```
Quem está online, e em que planeta:
  math: semente "Vault", cobertura 30%, 4885 tiles, chuva Normal, …
```

O hash diz se dois planetas são o mesmo; ele não ensina a gerar o do outro.
Sem a descrição, "vocês estão em planetas diferentes" é um aviso que ninguém
consegue atender — que era exatamente o caso antes.

A carta deixou de ser `NegativeEvent`: não é falha, é informação.

## Migração

Linhas antigas do `eventos.jsonl` não trazem o campo. Elas herdam o
`planeta.txt` da época, que continua sendo lido — só deixou de ser escrito.
Nada se perde e nada precisa ser apagado: os fatos do planeta fóssil
simplesmente ficam onde estão, invisíveis para quem não está nele.

## Um bug vizinho, consertado junto

`MundoPublicador` publicava o assentamento durante uma visita. Lá dentro o
"primeiro assentamento da facção do jogador" é o do **anfitrião**, no planeta do
anfitrião — e ia para o log com o id de colônia do visitante. Não era
hipotético: o checkpoint automático publica junto, e ele continua correndo
durante a visita. Agora não publica enquanto a visita durar.

## Consequências

- Não existe mais estado global de planeta para envelhecer. A classe do bug
  some junto com o campo.
- Quem está em outro planeta continua logado, jogando, guardando checkpoint e
  **podendo receber visita** — a visita transfere a partida inteira (ADR 0010) e
  nunca dependeu de os planetas baterem.
- O coordenador passa a ser, literalmente, o coordenador de vários mundos. Não
  era o objetivo, mas é a consequência honesta de guardar o planeta no lugar
  certo.
