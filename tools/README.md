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
wf emular MinhaColonia ColoniaDoVisitante 120
# … dois minutos …
wf comparar
```

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

Mesma coisa com janela nos dois lados. Mais lento e rouba o foco, mas é o que se
quer quando a pergunta ainda é *"o que está acontecendo?"* em vez de *"qual tick
divergiu?"*.

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
