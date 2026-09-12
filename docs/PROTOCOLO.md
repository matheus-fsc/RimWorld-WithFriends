# Protocolo — catálogo de mensagens

> Fonte da verdade: `/protocol`. Este documento descreve; o código define.
> Versão atual: **1**.

## Regras (§9.1)

1. Versão explícita no handshake, com flags de capacidade.
2. Mensagem desconhecida é **ignorada com log**, nunca derruba a conexão.
3. Compatibilidade nunca derivada de reflexão sobre nomes de classe.
4. Toda recusa traz motivo legível.

## Enquadramento

```
[ushort id][byte flags][int tamanho][payload de `tamanho` bytes]
```

O tamanho explícito é o que torna a regra 2 possível: um receptor que não
conhece o `id` pula `tamanho` bytes e continua lendo o fluxo.

`flags` bit 0 = payload comprimido com gzip. **A compressão é do transporte**,
não da mensagem: quem escreve uma mensagem não precisa saber que ela é grande.
Payloads a partir de 64 KB são comprimidos se o resultado for menor — saves de
RimWorld encolhem cerca de 10×.

Teto do payload cru: **256 MiB**. O teto anterior de 16 MiB era pequeno demais:
um checkpoint de colônia madura chegou a 17,9 MB em teste real, o cliente
escrevia o quadro, o servidor o recusava, e a conexão caía sem explicação dos
dois lados. Agora o limite é conferido **na escrita**, com mensagem legível
para quem enviou.

Strings usam o formato do `BinaryWriter` do .NET (comprimento em prefixo
7-bit, UTF-8). Inteiros são little-endian.

## Numeração

O valor numérico de `MessageId` é wire format. **Nunca reordenar, nunca
reutilizar um valor aposentado.** Faixas por família:

| Faixa | Família |
|---|---|
| 1–99 | `sistema.*` |
| 100–199 | `mundo.*` |
| 200–299 | `colonia.*` |
| 300–399 | `sessao.*` |
| 400–499 | `mercado.*` |

## Mensagens

### sistema.*

| id | Nome | Direção | Campos |
|---|---|---|---|
| 1 | `Handshake` | cliente → servidor | `ProtocolVersion`, `PlayerId`, `DisplayName`, `Capabilities`, `WorldModSetHash` |
| 2 | `HandshakeAceito` | servidor → cliente | `ProtocolVersion`, `ServerName`, `Capabilities` (interseção) |
| 3 | `HandshakeRecusado` | servidor → cliente | `Motivo`, `Explicacao` (texto para o jogador) |
| 4 | `SistemaErro` | servidor → cliente | `Codigo`, `Explicacao` |

`PlayerId` é a identidade. Endereço de rede **nunca** é identidade
(§17.3 regra 5, §15.2).

`WorldModSetHash` cobre apenas mods classe `world` (§8). Divergência **não**
recusa o login — a verificação acontece na entrada da sessão, para nunca
impedir alguém de jogar sozinho.

### colonia.*

| id | Nome | Direção | Campos |
|---|---|---|---|
| 200 | `ColoniaCheckpoint` | cliente → servidor | `PlayerId`, `ColonyId`, `CheckpointMetadata`, `Conteudo` |
| 201 | `ColoniaHeartbeat` | cliente → servidor | `PlayerId`, `ColonyId`, `GameTick`, `StateFingerprint` |
| 202 | `ColoniaRestauracao` | ambos | `PlayerId`, `ColonyId`, `ContentHash`, `Conteudo`, `Erro` |
| 203 | `ColoniaAlerta` | servidor → cliente | `PlayerId`, `ColonyId`, `Tipo`, `Explicacao` |

`CheckpointMetadata` = `{ GameTick, WorldCursor, ModSetHash, ContentHash,
WallClock, PreSessao }` — §7.1 regra 2.

O servidor **recalcula** `ContentHash` sobre o conteúdo recebido e guarda sob
o hash real; o valor declarado serve só para detectar discordância. `WallClock`
é diagnóstico e não ordena nada — quem ordena é `GameTick`.

`StateFingerprint` é a impressão digital do estado **vivo** da simulação, não
o hash do último checkpoint: entre dois checkpoints o `ContentHash` fica igual
por construção. Ver ADR 0004 — a distinção nasceu de falso positivo observado
em jogo.

`ColoniaRestauracao` usa a mesma mensagem nos dois sentidos: conteúdo vazio é
pedido, conteúdo preenchido é resposta. O servidor entrega quando pedido e
**nunca impõe** uma versão do save (§7.3) — o cliente grava o arquivo e a
aplicação continua sendo decisão do jogador. O conteúdo recebido é conferido
contra o hash pedido antes de tocar o disco.

`ColoniaAlerta` nunca é fatal: o checkpoint já foi aceito quando o alerta sai.
Ver ADR 0002 e ADR 0004.

### mundo.*

| id | Nome | Direção | Campos |
|---|---|---|---|
| 100 | `MundoEvento` | servidor → cliente | `EventoMundo` já ordenado |
| 101 | `MundoSincronizacaoCursor` | cliente → servidor | `Cursor` |
| 102 | `MundoPublicar` | cliente → servidor | `Tipo`, `Payload` |
| 103 | `MundoPresenca` | servidor → cliente | `PlayerId`, `DisplayName`, `Online` |

`EventoMundo` = `{ Seq, Autor, Tipo, Payload, TimestampLogico }` — §2.1.

Três regras que sustentam o modelo:

1. **`MundoPublicar` não carrega `Seq`.** Quem numera é o servidor, e é isso
   que elimina conflito de ordem por construção.
2. **`Autor` é a conexão, não o que o cliente diz ser.** O servidor carimba.
3. **O payload atravessa sem ser interpretado** (§10). O coordenador ordena e
   entrega; quem entende de RimWorld é o cliente.

`TimestampLogico` é diagnóstico. Quem ordena é `Seq`.

`MundoPresenca` **não** entra no log append-only: presença é estado volátil,
não fato histórico do planeta.

### sessao.*

| id | Nome | Direção | Campos |
|---|---|---|---|
| 300 | `SessaoConvite` | ambos | `ConviteId`, `De`, `Para`, `Tipo`, `ColoniaAnfitria`, `SessionModSetHash` |
| 301 | `SessaoAceite` | cliente → servidor | `ConviteId`, `SessionModSetHash` |
| 302 | `SessaoRecusa` | ambos | `ConviteId`, `Explicacao` |
| 303 | `SessaoInicio` | servidor → ambos | `SessaoId`, `Tipo`, `Anfitriao`, `Visitante`, `TickInicial`, `Semente` |
| 304 | `SessaoComando` | ambos | `SessaoId`, `Autor`, `TickAlvo`, `Ordem`, `Payload` |
| 305 | `SessaoBarreira` | ambos | `SessaoId`, `Autor`, `Tick`, `Fingerprint`, `TickLiberado`, `Pausado` |
| 306 | `SessaoFim` | ambos | `SessaoId`, `Motivo`, `Explicacao` |
| 307 | `SessaoAborto` | servidor → ambos | `SessaoId`, `Motivo`, `Explicacao`, `UltimoTickValido` |
| 308 | `SessaoMapa` | ambos | *descartado — ver ADR 0010* |
| 309 | `SessaoPartida` | anfitrião → visitante | `SessaoId`, `Tick`, `TamanhoCru`, `ContentHash`, `Comprimido` |

O fluxo da §2.3:

```
convite → aceite → congelar → trocar estado → barreira de tick → LOOP → encerrar → commit
```

Quatro regras (ver ADR 0006):

1. **`SessaoComando` vai sem `TickAlvo`.** O servidor agenda para
   `barreira + 10 ticks` e carimba `Ordem`. Os dois lados recebem o mesmo
   agendamento — ordem idêntica sem o servidor entender de RimWorld.
2. **`TickLiberado` é o mínimo entre os participantes.** Ninguém passa do mais
   lento; qualquer um pausado congela a barreira (§3, consenso de pausa).
3. **`Fingerprint` é o estado do RNG do intervalo.** Dois valores diferentes
   para o mesmo tick = desync: a sessão para na hora.
4. **`SessaoAborto` carrega `UltimoTickValido`** — o último tick em que os dois
   concordaram, que é o ponto de rollback. Perde-se o encontro, nunca a
   colônia.

`SessionModSetHash` é comparado **no aceite** (§8), nunca no login.

O payload de `SessaoComando` começa com um byte de tipo. O primeiro tipo é
`Velocidade` (1): mudar a velocidade do tempo durante a sessão vira comando,
para que os dois lados a apliquem no mesmo tick (§3). Tipo desconhecido é
ignorado com log, como qualquer mensagem desconhecida (§9.1).

`SessaoPartida` é o bootstrap da visita: o anfitrião manda a **partida
inteira** comprimida (medido: 13,12 MB → 1,31 MB) e o visitante entra nela. Um
mapa isolado não serve — ele referencia ideologias, políticas e facções que
vivem na partida (ADR 0010). O conteúdo é conferido contra o hash antes de
tocar o disco.

### mercado.*

Ids reservados em `protocol/MessageId.cs`; payloads são definidos no M5.
Ver `docs/ARQUITETURA.md` §13.

## Compatibilidade

`ProtocolVersion.MinimumSupported..Current` define a faixa aceita. Fora dela,
`HandshakeRecusado` com `VersaoIncompativel` e explicação dizendo qual lado
atualizar. Nunca desconexão muda.
