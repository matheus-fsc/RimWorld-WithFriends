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

## O limite da emulação

`wf emular` sobe as duas instâncias em `-batchmode -nographics`. Isso é o que a
torna barata e repetível — e é também o que ela **não** alcança.

Uma instância sem tela não tem mouse, não abre menu flutuante, não desenha e
não passa o cursor sobre nada. A classe de divergência em que *a interface
pergunta à simulação e a simulação responde guardando* (ver `NaInterface`) fica
estruturalmente fora do alcance dela: o rastreio de pathfinding, rodando numa
emulação inteira, conta **zero** consultas de alcançabilidade vindas de fora da
simulação, em todos os ticks.

Então:

| a divergência nasce em | reproduz com |
|---|---|
| simulação (combate, sangue, clima, caminho) | `wf emular --roteiro …` |
| interface (menu, mouse, seleção, câmera) | `wf jogos --caminho` e alguém clicando |

Para as segundas, os rastreios (`--caminho`, `--sangue`) agora também podem ser
ligados em `wf jogos`, que é onde há interface de verdade dos dois lados.

## A bancada: rodar RimWorld sem interface

```
wf rodar MinhaColonia 20000
wf rodar MinhaColonia 20000 --velocidade Ultrafast
```

Uma colônia, sem coordenador, sem visita, sem lockstep — o RimWorld normal
rodando sozinho por N **ticks** e relatando no fim:

```
   [WithFriends/bancada] colônia de pé no tick 22423 — rodando 20000 tick(s) em Superfast.
   [WithFriends/bancada] fim: 20000 tick(s) em 148.3s = 134.9 tick(s)/s (2.25x do tempo real)
   [WithFriends/bancada] 0 erro(s) e 3 aviso(s) durante a corrida
   [WithFriends/bancada] retrato: 6472 things, 17 pawns, …
```

**Ticks, não segundos**: é a unidade que se repete entre máquinas e entre
execuções, e a que faz duas corridas serem comparáveis.

### Por que isto não é sobre multijogador

Nada aqui liga coordenador, sessão ou barreira, e o gerador de números **não**
é isolado: todos os remendos de determinismo perguntam antes se há visita em
andamento, e aqui não há. É o jogo de sempre.

O que a bancada reaproveita é o que custou caro descobrir para a emulação
funcionar, e que não tem nada de multijogador:

| peça | o que resolve |
|---|---|
| lista de cancelamento sem placa de vídeo | `WaterInfo.SetTextures`, `PortraitsCache.Get`, `SubcameraDriver.UpdatePositions`, `Map.MapUpdate`, `Section.RegenerateAllLayers`, `SectionLayer.DrawLayer`, `GUIStyle.CalcSize`, `FloatMenuOption.SetSizeMode`, `Prefs.Save` — cada uma é um NRE que só aparece sem GPU |
| `Root_Entry.Update` para carregar o save | sem GUI o menu principal **nunca** roda; a instância sobe e fica parada para sempre, compilando perfeitamente |
| `WindowsForcePause → false` | uma carta de ameaça pausa o relógio **por existir**, e escrever `CurTimeSpeed` não desfaz |
| contagem de erro e aviso | "rodou 20 mil ticks, 0 erros" é a resposta que um teste de regressão precisa dar |

Para que serve, fora daqui:

- **regressão**: a colônia de teste ainda roda N ticks sem erro depois da sua mudança?
- **medição**: quantos ticks por segundo, e quanto o mod piora isso
- **reprodução**: o mesmo save, o mesmo número de ticks, sem clicar

### O que ela não faz

Não gera mundo nem colônia — precisa de um save pronto. Gerar exigiria dirigir
a tela de criação, que é exatamente o que uma instância sem interface não tem.

E não dirige a interface: nenhum clique, nenhum menu. A classe de bug que nasce
de "a interface perguntou à simulação" continua fora do alcance dela, pelo mesmo
motivo que está fora do alcance do `wf emular` (ver acima).

### Se algum dia virar coisa de outros

O código mora em `client/Bancada/`, fora de `Session/`, de propósito: é a
costura por onde a bancada se separa do mod. O que atravessaria junto é a lista
de cancelamento e os dois ganchos de subida — nada mais.

Uma ressalva honesta: a lista de cancelamento é de **1.6.4871**. Numa versão
nova ela muda, e descobrir o que mudou custa uma corrida que estoura. É o preço
recorrente de manter uma coisa dessas publicada.

## Dirigir o jogo de fora

O jogo sobe com a porta aberta; qualquer coisa que fale socket manda comandos.

```
wf rodar presetfull 2000000 --controle 25600     # bancada com porta
wf jogos --servidor --controle 25600             # ou as duas instâncias (P e P+1)

wf controle estado
wf controle pawns Kasumi
printf 'alistar 1048 1\nir 1048 100,120\n' | wf controle
```

Vocabulário: `estado`, `pawns [texto]`, `alistar ID 0|1`, `ir ID x,z`,
`incidente DEF [pontos]`, `velocidade NOME`, `despejar`, `sair`.

Gestos de **interface**: `selecionar ID…`, `menu x,z`, `irarrastando x,z`,
`olhar x,z`.

### O limite que a interface parecia impor — e por que ela não impõe

Está dito acima que a emulação headless não alcança a classe de bug que nasce da
interface. Isso continua verdade para **evento de mouse**: em
`-batchmode -nographics` o `OnGUI` não roda, e clique sintético não é consumido
por ninguém.

Mas o que causa divergência nunca foi o mouse — é o **código de interface
rodando e mexendo em estado compartilhado**. Todas as causas achadas até hoje
foram consulta ou escrita feita por ele: a ordem dos vizinhos, as células de
zona, o memo de alcançabilidade, o `EndCurrentJob` do "ir aqui". Nenhuma
precisou de um pixel desenhado.

Então os gestos chamam os **pontos de entrada** que o clique chamaria —
`FloatMenuMakerMap.GetOptions`, `Selector.Select`,
`MultiPawnGotoController.StartInteraction/AddPawn/FinalizeInteraction`,
`CameraDriver.JumpToCurrentMapLoc`. Medido: funcionam numa instância sem tela.

O bug do "ir aqui" reproduzido sem ninguém clicando:

```python
c.cmd("selecionar 1048")
c.cmd("ir 1048 140,150")          # começa um Goto
c.cmd("velocidade Paused")        # congela a posição
p = c.pawns("Kasumi")[0]
c.cmd(f"irarrastando {p['x']},{p['z']}")   # arrasta em cima dele mesmo
# antes:  job=Goto         depois:  job=Wait_Combat
```

A pausa não é detalhe: sem ela o pawn anda entre a leitura e o arrasto, a
condição `pawn.Position == gotoLoc` deixa de valer, e o gesto não toca o
caminho que interessa.

### A regra que faz isto valer alguma coisa

Cada comando entra pelo **mesmo caminho de um clique** — `drafter.Drafted`,
`TryTakeOrderedJob`, `TipoDeComando.Incidente`. Nenhum atalho para dentro da
simulação. Se o caminho do comando estiver quebrado, o teste quebra junto, que é
o que se quer de um teste. Dentro de uma visita, `incidente` vira comando de
sessão e obedece a autoridade da §4; fora dela, dispara local.

### Como roteiro

```python
from controle import Controle

with Controle(25600) as c:
    for p in c.pawns(alistados=False):
        c.cmd(f"alistar {p['id']} 1")
    c.cmd("incidente RaidEnemy 500")
    print(c.estado()["tick"])
```

Cenário passa a ser texto: mudar um teste deixa de custar `dotnet build` e duas
instâncias reabertas.

### Detalhes que custaram

- O socket lê numa thread de fundo, mas **quem executa é o quadro**: as APIs do
  RimWorld não são seguras fora da thread principal. O cliente espera a resposta
  de propósito — quem dirige precisa saber que o comando aconteceu antes de
  mandar o próximo, senão o roteiro vira corrida.
- Só `127.0.0.1`, e só com `-controle=PORTA`. É ferramenta de bancada.
- Na bancada, mudar o relógio direto não bastava: ela reescreve a velocidade a
  cada quadro para impedir que um incidente pause a corrida, e atropelava o
  comando. Quem manda pela porta muda o **alvo** dela.
