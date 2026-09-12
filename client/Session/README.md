# client/session

Camada de sessão (§2.3): convite, aceite, congelamento, barreira de tick,
lockstep delimitado, aborto com rollback para `checkpoint_pre_sessao`.

Referência de leitura: `referencia/repos/Multiplayer/Source/Client/AsyncTime/`
(abstração `ITickable`) e `Source/Client/Desyncs/` (impressão digital por
estado de RNG). Todo arquivo derivado leva o cabeçalho de atribuição da §16.2.

## Estado

O lado do **servidor** está pronto (`server/Sessoes/`, ADR 0006): convite,
aceite com verificação de mods (§8), sequenciamento de comandos, barreira
comum, consenso de pausa, comparação de impressões digitais e aborto com
`UltimoTickValido`.

Falta o lado do **cliente**, que é tudo o que exige entender RimWorld:

- congelar a simulação e tirar o `checkpoint_pre_sessao`
- `ITickable` por sessão, consumindo a fila de comandos no tick agendado
- impressão digital de RNG por intervalo
- rollback ao abortar

É a única parte do projeto que não dá para verificar sem dois jogos abertos.
Ver `tools/segundo-rimworld.fish`.
