# client/colony

Checkpoints e integridade (§7). O cliente é autoridade sobre a própria colônia;
o servidor guarda checkpoints para durabilidade, nunca para mandar no save.

| Arquivo | Papel |
|---|---|
| `PlayerIdentity.cs` | `player_id` por instalação, nas settings do mod |
| `ColonyIdentityComponent.cs` | `colony_id` por partida, dentro do save |
| `ModSetHash.cs` | hash do conjunto de mods (classificação da §8 vem em M3) |
| `CheckpointWriter.cs` | serializa a partida, calcula `content_hash`, monta os metadados |
| `FingerprintVivo.cs` | impressão digital do estado vivo, para o heartbeat (ADR 0004) |
| `SincronizacaoComponent.cs` | heartbeat automático e alertas do servidor como carta no jogo |
| `CheckpointAutomatico.cs` | postfix em `SaveGame`: checkpoint pega carona no save do jogo (ADR 0005) |
| `DebugActions.cs` | verificação dentro do jogo, sem servidor |

Invariantes (implementadas no servidor, ver ADR 0002):
- checkpoint append-only, endereçado por hash
- metadados `{ game_tick, world_cursor, mod_set_hash, content_hash, wall_clock }`
- `game_tick` monotônico — regressão é aceita, marcada como suspeita e alertada
- heartbeat com `(tick, state_fingerprint)` — impressão digital parada com o
  tick andando é alarme (ADR 0004)
- identidade é `(player_id, colony_id)`, nunca nome de arquivo

Envio automático: heartbeat a cada 30s e checkpoint junto de todo save do
jogo, ambos enquanto a conexão estiver viva. O envio manual (debug action)
continua existindo para teste.
