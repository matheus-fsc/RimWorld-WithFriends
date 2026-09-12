# tools

Ferramentas fora do jogo (§12):

- **inspetor de save** — abre um checkpoint e imprime
  `{ game_tick, world_cursor, mod_set_hash, content_hash, wall_clock }`.
  É o que teria detectado o incidente da §15.1 em minutos.
- **replay de sessão** — reexecuta o log de comandos de uma sessão e aponta o
  tick da divergência.
- **`ClienteFalso/`** — fala o protocolo sem o jogo, usando o mesmo
  `DirectTransport` do mod. Serve para exercitar o coordenador com um `.rws`
  real e para reproduzir o incidente da §15.1 sob demanda:

  ```fish
  dotnet run --project tools/ClienteFalso -- [::1]:25555 <save.rws> --incidente
  ```

  O endereço aceita literal IPv6 entre colchetes, IPv4 e nome de host; sem
  porta, usa 25555. `--colonia <id>` reutiliza uma colônia entre execuções.
- **gerador de carga** — N clientes falsos contra o coordenador, sem o jogo.

## Scripts

> Os `.sh` são bash e têm shebang: chame **`./tools/x.sh`**, não `fish
> tools/x.sh` — o fish tentaria interpretar sintaxe bash e falharia já na
> primeira atribuição de variável. Os `.fish` são fish e valem o mesmo:
> `./tools/x.fish`.

- **`servidor.fish [porta] [dados]`** — para, reconstrói e sobe o coordenador.
  Existe porque o binário em execução trava o próprio build.
- **`dois-jogos.sh`** — compila o mod, fecha o que estiver aberto e sobe as
  duas instâncias, cada uma com pasta de dados e **log próprios**.

  ```
  ./tools/dois-jogos.sh                 compila, fecha e abre os dois
  ./tools/dois-jogos.sh --sem-build     não recompila
  ./tools/dois-jogos.sh --servidor      sobe o coordenador junto
  ./tools/dois-jogos.sh --so-segundo    abre só a segunda instância
  ./tools/dois-jogos.sh --matar         só fecha
  ```

  Resolve três coisas de uma vez: (1) lockstep exige código idêntico, e o jogo
  carrega assembly na subida — build e reabertura têm que andar juntos; (2) o
  `player_id` vive nas settings, então a segunda instância precisa de
  `-savedatafolder` para não duplicar identidade (§7.1 regra 5); (3) o Unity
  escreve `Player.log` num caminho fixo que **não** acompanha o
  `-savedatafolder`, então sem `-logFile` a segunda instância sobrescreveria o
  log da primeira.

Nenhuma delas depende de assembly do RimWorld.

## Reconstruir com o jogo aberto

O mod é publicado em `dist/WithFriends/Assemblies/` de forma **atômica**: o
assembly é escrito ao lado com sufixo `.novo` e renomeado por cima. `Move` no
mesmo sistema de arquivos é atômico, então o jogo vê o arquivo antigo ou o novo,
nunca um pedaço dele.

Isso existe porque o contrário aconteceu: um `dotnet build` com o jogo abrindo ao
mesmo tempo, e o RimWorld leu o DLL pela metade. O sintoma não parecia build —
parecia bug de mod:

```
Could not instantiate a GameComponent of type View.GetSelectionWeight
SaveableFromNode exception: Constructor on type 'SincronizacaoComponent' not found
System.BadImageFormatException: Method has zero rva
```

Nomes de tipo sem sentido e construtores "faltando" que existem no código são a
assinatura de metadata truncada. Se aparecerem de novo, o primeiro lugar a olhar
é a hora do DLL contra a hora do log — não o código.

**O que a publicação atômica não resolve:** trocar o assembly de um jogo que já
está rodando não recarrega nada. O RimWorld lê os assemblies na subida; para
testar código novo é preciso reabrir, que é o que `dois-jogos.sh` faz.
