# ADR 0001 — Socket dual-stack, IPv6 primeiro

**Status:** aceito
**Data:** 2026-09-10

## Contexto

No Brasil a maioria dos provedores residenciais entrega IPv4 atrás de CGNAT:
o roteador não tem endereço público e encaminhar porta não funciona. Os mesmos
provedores entregam IPv6 nativo em dual-stack, com endereço globalmente
roteável por dispositivo.

O RimWorld Together tem falha registrada disso ([RT #315]): servidor sem bind
em IPv6 e cliente que não aceita endereço IPv6 nem digitado nem colado.

## Decisão

Um único listener com bind em `::` e `DualMode = true`, atendendo IPv6 e IPv4.
`AddressFamily.InterNetwork` nunca aparece fixo no código.

Implementado em `server/Listener.cs`.

## Consequências

- Dois pares atrás de CGNAT distintos conectam por IPv6 sem relay.
- Parsing de endereço não pode usar `split(':')` — literal IPv6 vem entre
  colchetes: `[2804:14c::1]:25555`.
- O anfitrião ainda precisa de regra de firewall de entrada; IPv6 não tem NAT,
  mas tem firewall. A mensagem de erro precisa dizer isso.
- Endereços SLAAC de privacidade rotacionam: hospedar sempre em `::`, nunca
  num literal que expira.

Coberto por `tests/TransportTests.cs` na parte testável em uma máquina só.

[RT #315]: https://github.com/RimWorld-Together/Rimworld-Together/issues/315
