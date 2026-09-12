# With Friends

Mod de multiplayer para RimWorld 1.6.

> **Assíncrono por padrão. Síncrono por encontro.**

Cada jogador simula a própria colônia no próprio ritmo, com save próprio.
Quando dois jogadores precisam interagir de verdade, entram numa **sessão** —
uma janela curta, opt-in e determinística, com ponto de retorno imediatamente
antes dela. Fora de sessão não existe sincronia por tick, e jogar sozinho
nunca é bloqueado pela ausência de ninguém.

Arquitetura completa: [`docs/ARQUITETURA.md`](docs/ARQUITETURA.md).
Protocolo: [`docs/PROTOCOLO.md`](docs/PROTOCOLO.md).
Componentização da sessão e mapa de volatilidade:
[`docs/SESSAO-COMPONENTES.md`](docs/SESSAO-COMPONENTES.md).
Medições: [`docs/MEDICOES.md`](docs/MEDICOES.md).
Mecânica da visita no jogo base:
[`docs/MECANICA-DA-VISITA.md`](docs/MECANICA-DA-VISITA.md).
Comandos de sessão e o que ainda falta:
[`docs/COMANDOS-DE-SESSAO.md`](docs/COMANDOS-DE-SESSAO.md).
Ideias ainda não decididas: [`docs/IDEIAS.md`](docs/IDEIAS.md).

## Relação com o Multiplayer (§16.3)

Reusa mecanismos de [Multiplayer](https://github.com/rwmt/Multiplayer)
(MIT, Zetrith) — em especial detecção de desync por estado de RNG e a
abstração de tempo por tickable. Depende de Harmony e Prepatcher.
**Não é um fork:** a arquitetura aqui é assíncrona com lockstep delimitado
por sessão, enquanto o Multiplayer é lockstep global sobre um save
compartilhado.

Licenças de terceiros em [`THIRD_PARTY/`](THIRD_PARTY/).

## Estrutura

```
docs/           arquitetura, protocolo, ADRs
protocol/       definições compartilhadas — fonte única de verdade
transport/      ITransport plugável: direto (IPv6/IPv4), LAN, Steam (ADR 0003)
server/         coordenador, sem dependência do RimWorld
client/         o mod: world/ session/ colony/ ui/
tools/          inspetor de save, replay de sessão, gerador de carga
tests/          testes do protocolo e do coordenador — rodam sem o jogo
```

## Build

Requer .NET SDK 6+. Nenhum assembly do RimWorld é redistribuído: as
referências vêm do pacote `Krafs.Rimworld.Ref`, casado com a versão instalada.

```fish
dotnet build WithFriends.sln
dotnet test tests/Tests.csproj
```

O cliente é empacotado em `dist/WithFriends/{About,Assemblies}`. Para o jogo
enxergar:

```fish
ln -s (pwd)/dist/WithFriends ~/.steam/steam/steamapps/common/RimWorld/Mods/WithFriends
```

O link aponta para a saída de build: `dotnet build` e reiniciar o jogo já
basta, sem recopiar nada.

### Testar dentro do jogo

Requer o mod **Harmony** (`brrainz.harmony`) ativo **antes do Core** — o
`About.xml` dele declara `loadBefore: Ludeon.RimWorld`.

Com a Steam offline os mods da Workshop não são enumerados (o RimWorld os lê
via Steamworks, e `SteamAPI.Init()` falha), então o jogo remove
`brrainz.harmony` da lista de ativos sozinho. Instale por git, direto na pasta
de mods:

```fish
git clone --depth 1 https://github.com/pardeike/HarmonyRimWorld.git \
  ~/.steam/steam/steamapps/common/RimWorld/Mods/Harmony
```

O repositório já traz os assemblies compilados em `Current/Assemblies/`, então
não há nada a construir. Depois reative `brrainz.harmony` no
`Config/ModsConfig.xml`, na primeira posição.

Com o modo de desenvolvedor ligado: `Debug actions` → categoria
**WithFriends**.

| Ação | O que verifica |
|---|---|
| Imprimir identidade da colônia | `(player_id, colony_id)` estável, independente do nome do save (§7.1 regra 5) |
| Criar checkpoint da colônia | serialização real do save + `content_hash` + metadados (§7.1 regra 2) |
| Conferir hash de conteúdo (2x) | dois checkpoints do mesmo estado têm o mesmo hash |
| Abrir pasta de checkpoints | layout append-only endereçado por hash (§7.1 regra 1) |
| Conectar ao coordenador | Happy Eyeballs, IPv6 primeiro (§17.2–17.4) |
| Enviar checkpoint ao coordenador | caminho completo cliente → servidor |
| Status da conexão | estado, endpoint efetivo, capacidades negociadas |
| Acoplamentos com o jogo | catálogo de patches e se cada alvo existe nesta versão |
| Medir custo de transferir o mapa | serialização e compressão do mapa e da partida (docs/MEDICOES.md) |
| Medir carregamento do mapa de volta | o outro lado da conta: desserialização |
| Empacotar mala do visitante | serializa os colonos selecionados + ideologias (ida da caravana) |
| Abrir mala do visitante aqui | abre a mala na mesma partida — pior caso de colisão de id |
| Convidar para sessão de ensaio | abre sessão com quem estiver online (§2.3) |
| Aceitar convite de sessão | congela, cria ponto de retorno e entra na barreira |
| Status da sessão | tick de sessão, tick liberado, ticks segurados pela barreira |
| Status da visita | papel, sessão, ponto de retorno e save temporário (ADR 0010) |
| Retrato da partida | comparação lado a lado quando a simulação diverge |
| Enviar comando de teste na sessão | exercita o agendamento em tick futuro |
| Encerrar sessão | fim combinado, com commit dos dois lados |
| Impressão digital do intervalo | estado do RNG, modo de arredondamento FP e resumo (§14.3) |
| Congelar e tirar checkpoint pré-sessão | pausa e cria o ponto de retorno da §2.3 |
| Voltar ao checkpoint pré-sessão | rollback, com confirmação e salvaguarda do estado atual |
| Pedir checkpoint pré-sessão ao coordenador | busca o ponto de retorno no servidor (§7.3) |
| Descongelar | devolve a velocidade de jogo anterior |
| Simular incidente de save órfão | reproduz a §15.1 na sua colônia; o alerta volta como carta |
| Publicar meu assentamento | põe sua colônia no mapa-mundo dos outros (§4) |
| Listar assentamentos no planeta | cursor de mundo e colônias alheias conhecidas |
| Esquecer assentamentos remotos | limpa a projeção local sem tocar no log do servidor |
| Reconstruir mundo do zero | zera o cursor; a próxima conexão reconstrói o planeta do log |

O endereço do coordenador fica em `Opções → Mod settings → With Friends`, e
aceita `[2804:14c::1]:25555`, `192.168.0.4:25555` ou nome de host. Por padrão o
mod **conecta sozinho** ao entrar numa partida, tentando de novo a cada 15s
enquanto não conseguir — e jogar sozinho nunca depende disso (§11).

Com a conexão viva, três coisas acontecem sozinhas:

- **heartbeat** a cada 30s, com a impressão digital do estado vivo (ADR 0004);
- **checkpoint** junto de todo save do jogo, pegando carona no autosave sem
  custo novo de serialização, com piso configurável entre envios (ADR 0005);
- **alertas** do servidor chegam como **carta no jogo**, não só no log.

Ao conectar, o cliente pede o log de mundo a partir do próprio `world_cursor`
e publica a própria colônia. Assentamentos de outros jogadores aparecem no
mapa-mundo com nome, riqueza estimada e status online — e nada mais (§4).

### Duas instâncias

Sessão precisa de dois jogos. Um script cuida do ciclo inteiro — compilar,
fechar, reabrir os dois com pastas de dados e logs separados:

```fish
./tools/dois-jogos.sh --servidor
```

(É bash com shebang — chame com `./`, não com `fish tools/...`.)

Depois de qualquer mudança no mod, é esse o comando: lockstep exige **código
idêntico dos dois lados**, e o RimWorld carrega assembly na subida.

Para ver o mundo assíncrono com um jogo só aberto, suba um jogador falso:

```fish
dotnet run --project tools/ClienteFalso -- "[::1]:25555" --jogador Vale --tile 4821 --riqueza 15000 --ficar 300
```

Os checkpoints ficam em `<pasta de saves>/../WithFriends/checkpoints/`, com
nome derivado do hash — nunca do nome que você deu à colônia.

Rodar o coordenador (dual-stack IPv6/IPv4, porta padrão 25555):

```fish
dotnet run --project server
```

Ambiente de referência local (assemblies e clones de leitura, fora do
controle de versão): [`REFERENCIAS.md`](REFERENCIAS.md).

## Estado

**M0 — Fundação**, **M2 — Durabilidade** e **M1 — Mundo assíncrono**
concluídos. M2 veio antes de M1 e M3 de propósito: integridade antes de
recurso (§13).

O critério de M2 — reproduzir o incidente de save órfão e detectar em menos de
1 minuto — está verificado em `tests/DurabilidadeTests.cs` e reproduzível ao
vivo com um save real:

```fish
dotnet run --project server -- 25555 dados

# atenção: no fish, '<' e '>' são redirecionamento — sempre entre aspas
set save ~/".config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Saves/MinhaColonia.rws"
dotnet run --project tools/ClienteFalso -- "[::1]:25555" "$save" --incidente
```

Ver o roadmap completo na §13 da arquitetura.

| Milestone | Estado |
|---|---|
| M0 Fundação — protocolo versionado, handshake, coordenador, transporte | pronto |
| M1 Mundo assíncrono — log de eventos, assentamentos, presença | pronto |
| M2 Durabilidade — checkpoints, hash, tick monotônico, alertas | pronto |
| M3 Sessão — convite, lockstep, pausa, aborto | ensaio funcional; falta troca de estado (ADR 0007) |
| M4 Interações | — |
| M5 Comércio | — |

Licença: [MIT](LICENSE).
