# tools

Tudo o que se faz **fora** do jogo (§12). A porta de entrada é uma só:

```
tools/wf
```

```
wf servidor [porta]              sobe o coordenador em primeiro plano
wf jogos [--arbitro SAVE]        abre duas instâncias para jogar/testar à mão
wf emular HOST ARB [segundos]    roda uma visita inteira sozinha e sai
    --visivel                    com janela, para acompanhar em tempo real
wf comparar                      compara os dois diários mais novos
wf auditar                       audita o assembly do jogo (alcance + MP)
wf saves [p1|p2]                 lista os saves de cada instância
wf diarios                       lista os diários mais novos de cada lado
wf matar                         fecha jogos e coordenador
wf status                        o que está rodando agora
```

Variáveis de ambiente, se os caminhos forem outros: `WF_JOGO`, `WF_DADOS_P1`,
`WF_DADOS_P2`, `WF_DADOS_ARB`, `WF_PORTA`.

## O ciclo que importa: emular e comparar

```
wf emular MinhaColonia ColoniaDoVisitante 120 --roteiro raid
# … dois minutos …
wf comparar
```

O `--roteiro` é o que faz a emulação valer: uma visita **parada** exercita
plantas crescendo e animais perambulando, e nenhuma das divergências que
perseguimos apareceu aí. Todas apareceram em **combate**. Sem roteiro, o teste
roda no caminho errado.

| roteiro | o que faz |
|---|---|
| `raid` | alista os colonos, chama um assalto aos 10 s e outro maior aos 60 s |
| `combate` | o mesmo, mais desalistar e realistar no meio |

Tudo pelos mesmos comandos de sessão que um jogador usaria: alistar passa pelo
setter de `Drafted`, o assalto é `TipoDeComando.Incidente`, carimbado pelo
coordenador. Não há atalho para dentro da simulação — se o caminho do comando
estiver quebrado, a emulação quebra junto, que é o que se quer de um teste.

Saída real de uma corrida de dois minutos:

```
ticks comparados: 81  (0..80)
primeira divergência de sorteio: tick 31  (A=522  B=523)

-- locais de chamada no tick 31  (A=24 B=25)
   A=0    B=1
        < Rand.Range
        < FloatRange.get_RandomInRange
        < Filth.SpawnSetup
        < GenSpawn.Spawn
        < FilthMaker.TryMakeFilth
        < Pawn_HealthTracker.DropBloodFilth
        < Pawn_HealthTracker.HealthTickInterval
```

A mesma família que custou um dia inteiro de jogo a dois, agora em um comando.

`emular` sobe o coordenador, abre o anfitrião **sem interface**, e ele chama o
árbitro (docs/ARBITRO.md), convida, despausa e deixa rodar. No fim, os dois lados
despejam histórico de RNG, rastreio por local de chamada e estado de pawn nos
respectivos diários, e o processo fecha.

`comparar` faz o que antes era feito à mão: acha o primeiro tick em que o
contador de sorteios da sessão diverge, diferencia os locais de chamada naquele
tick, e mostra o primeiro pawn com estado diferente — dizendo se ele veio
**antes** do sorteio divergir (causa) ou depois (consequência).

**Por que isto existe.** Reproduzir uma divergência custava dois humanos, duas
janelas e vários minutos de jogo, e cada hipótese testada exigia repetir tudo.
O gargalo nunca foi escrever o guarda; foi jogar.

### Ver acontecendo

```
wf emular MinhaColonia ColoniaDoVisitante 120 --visivel
```

Mesma coisa com janela nos dois lados, cada uma em metade da tela. Mais lento e
rouba o foco, mas é o que se quer quando a pergunta ainda é *"o que está
acontecendo?"* em vez de *"qual tick divergiu?"*.

Duas coisas foram necessárias e nenhuma é óbvia:

- **`-screen-fullscreen 0` do Unity não basta.** O argumento é aplicado e logo
  sobrescrito: o RimWorld chama `Screen.SetResolution` com o que está nas
  preferências dele. Por isso o tamanho é forçado de dentro do jogo, no modo
  automático — e é seguro porque `Prefs.Save` está cancelado ali, então o arquivo
  de preferências do jogador não é tocado.
- **Posição não tem argumento.** Quem posiciona é o gerenciador de janelas; a CLI
  tenta com `xdotool` depois que as duas abrem. Melhor esforço de propósito: se
  falhar, as duas aparecem com o tamanho certo uma sobre a outra, o que é ruim
  mas não impede nada.

## Para ferramenta automática

`wf` é feita para ser chamada por script e por agente, não só por gente:

- saída estável em `stdout`, erros em `stderr`;
- códigos de saída com significado:

| código | quer dizer |
|---|---|
| `0` | deu certo — e, em `comparar`, **os dois lados bateram** |
| `1` | falhou |
| `2` | uso errado |
| `3` | `comparar` **achou divergência** — não é erro, é o resultado |

O `3` é deliberado: divergir é o resultado esperado de uma reprodução. Quem
chama precisa distinguir "não rodou" de "rodou e achou".

Um laço de investigação inteiro cabe em três linhas:

```bash
wf emular "$HOST" "$ARB" 120
sleep 140
wf comparar || [ $? -eq 3 ]   # 3 = achou, e é isso que se quer ler
```

## O resto

- **`Auditor/`** — varre o `Assembly-CSharp.dll` procurando leitura de fonte
  local dentro da simulação, e cruza com os alvos do Multiplayer. `Alcance.cs`
  responde, pelo IL, o que uma visita realmente alcança — corta a lista de 878
  alvos dele para os ~450 que nos dizem respeito. Ver `docs/AUDITORIA.md`.
- **`ClienteFalso/`** — fala o protocolo sem o jogo, usando o mesmo
  `DirectTransport` do mod. Serve para exercitar o coordenador com um `.rws`
  real:

  ```fish
  dotnet run --project tools/ClienteFalso -- [::1]:25555 <save.rws> --incidente
  ```

  O endereço aceita literal IPv6 entre colchetes, IPv4 e nome de host; sem
  porta, usa 25555. `--colonia <id>` reutiliza uma colônia entre execuções.
- **`dois-jogos.sh`** — abre duas instâncias para jogar à mão. `wf jogos`
  encaminha para ele.
- **`comparar-diarios.py`** — o comparador. `wf comparar` encaminha para ele.
- **`servidor.fish`** — sobe só o coordenador.

Ainda não existem, e continuam valendo: inspetor de save, replay de sessão a
partir do log de comandos, e gerador de carga com N clientes falsos.
