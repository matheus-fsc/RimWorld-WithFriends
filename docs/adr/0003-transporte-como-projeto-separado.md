# ADR 0003 — `/transport` como projeto próprio, e Happy Eyeballs no cliente

**Status:** aceito
**Data:** 2026-09-10

## Contexto

A §12 lista `/protocol`, `/server`, `/client`, `/tools`, `/tests`. A §17.1
define `ITransport` como abstração plugável, mas não diz onde ela mora.

Três candidatos foram considerados:

1. **Dentro de `/protocol`** — mistura definição de mensagem com sockets.
   `/protocol` é "fonte única de verdade" justamente por não depender de nada.
2. **Dentro de `/client`** — o cliente é `net48` e roda dentro do jogo. Nada
   ali é testável pelo projeto de testes (`net6.0`), e Happy Eyeballs é
   exatamente o tipo de código que precisa de teste.
3. **Projeto próprio** — `netstandard2.0`, consumido por cliente, ferramentas
   e testes.

## Decisão

`/transport`, projeto `netstandard2.0` que depende só de `/protocol`.

O servidor **não** depende dele: tem o próprio listener, e a assimetria é
real — servidor escuta, cliente escala a lista de endpoints (§17.4).

`DirectTransport` implementa Happy Eyeballs (RFC 8305): resolve o host,
ordena IPv6 primeiro intercalando famílias, dispara as tentativas em paralelo
escalonadas por 250ms e fica com a primeira que conectar. Túneis Teredo
(`2001:0::/32`) e 6to4 (`2002::/16`) vão para o fim da fila (§17.3 regra 8).

## Consequências

- Happy Eyeballs é testável sem o jogo: `tests/DirectTransportTests.cs`
  cobre preferência por IPv6, queda para IPv4 em menos de 3s, remontagem de
  mensagem partida em dois pacotes e mensagem de erro que cita o firewall.
- O parser de endereço (§17.3 regra 2) ganhou 18 casos de teste, incluindo
  `[2804:14c::1]:25555` digitado e colado com espaços.
- `ConnectorRegistry` já existe, com `DirectTransport` registrado. `Steam` e
  `Lan` entram como mais um item, sem mexer no resto — e em instalação
  sem Steam o registro simplesmente não os oferece (§17.5).
- A §12 da arquitetura não lista `/transport`. Este ADR é o registro da
  adição; a estrutura do README reflete o estado real.

## Nota sobre o atraso de 250ms

A RFC 8305 recomenda 50ms entre tentativas. Usamos 250ms de propósito: com
50ms, num host onde IPv4 é local e IPv6 passa pelo provedor, o IPv4 vence a
corrida por latência mesmo quando o IPv6 funciona — e o IPv6 é o caminho que
resolve o CGNAT (§17.2). 250ms dá vantagem real ao caminho que queremos, sem
punir quem só tem IPv4: a queda medida fica bem abaixo de 3 segundos.
