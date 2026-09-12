# Referências locais

Tudo em `referencia/` está no `.gitignore` — é material de leitura/compilação, nunca versionado.

```
referencia/
  jogo/
    steam-1.6.4871/Managed/   assemblies (Linux) + Source/ oficial de exemplo
  repos/
    Multiplayer/              rwmt/Multiplayer        MIT   4a3be27
    Prepatcher/               Zetrith/Prepatcher      MIT   9fd1738
    MultiplayerAPI/           rwmt/MultiplayerAPI     MIT   c9a8860
    Harmony/                  pardeike/Harmony        MIT   e7872dc
    Rimworld-Together/        RimWorld-Together       —     230ee83  (referência negativa, §15)
```

Licenças integrais copiadas para `THIRD_PARTY/` conforme §16.2.

## Contra qual assembly compilar

Fonte única: `referencia/jogo/steam-1.6.4871/Managed/Assembly-CSharp.dll`,
da cópia Steam original. As assemblies são IL gerenciado — o mesmo código
roda em qualquer loja, então uma instalação basta para desenvolver.

## Testar o caminho sem Steam (§17.5)

Não há instalação GOG neste ambiente. O caminho "sem Steam"
(`SteamManager.Initialized == false`) se testa na própria cópia Steam,
executando o binário fora do cliente Steam:

```fish
cd ~/.steam/steam/steamapps/common/RimWorld
env -u SteamAppId -u SteamOverlayGameId ./RimWorldLinux
```

Sem o cliente Steam rodando e sem `SteamAppId` no ambiente, o Steamworks
não inicializa — que é exatamente a condição de um jogador de outra loja.
O critério da §17.6 ("Steam ausente não impede nenhuma funcionalidade além
do convite por amigo") é verificável assim.

## Decompilar

`ilspycmd` 7.2.1 instalado como dotnet tool global (`~/.dotnet/tools`);
7.x porque o SDK local é .NET 6 — o 8.x exige .NET 8.

```fish
set -x PATH $PATH $HOME/.dotnet/tools

# um tipo
ilspycmd -t Verse.TickManager referencia/jogo/steam-1.6.4871/Managed/Assembly-CSharp.dll

# árvore inteira em projeto C# (lento, ~90k métodos)
ilspycmd -p -o decompilado/ referencia/jogo/steam-1.6.4871/Managed/Assembly-CSharp.dll
```

`decompilado/` também está no `.gitignore`.

## LiteNetLib

<https://github.com/RevenantX/LiteNetLib> — UDP confiável com canais, gerência de
conexão e NAT punchthrough, MIT. É o transporte do Multiplayer, que o credita nos
agradecimentos.

Não usamos: nossa topologia é estrela com coordenador dedicado, e conexão de
saída atravessa NAT sem furar nada. Ver `docs/adr/0013-litenetlib-no-transporte.md`
para o gatilho de adoção e para o invariante de canal que vem junto.

## Extraindo conhecimento do Multiplayer

`tools/Auditor` recebe o fonte do Multiplayer e marca quais dos nossos achados
ele já remenda — 878 alvos extraídos por texto dos atributos `HarmonyPatch`,
`MpPrefix`/`MpPostfix` e dos registros `Sync*`.

Não é para copiar código (o dele é MIT, o nosso desenho é outro) — é para separar
suspeita de prioridade. Ver `docs/AUDITORIA.md`.

## Controle de tempo no Multiplayer

`AsyncTime/TimeControlUI.cs`, `AsyncTime/AsyncWorldTimeComp.cs` e
`Comp/Game/MultiplayerGameComp.cs` — voto por jogador com "o mais lento ganha",
válvula de escape `ResetAllTimeVotes`, e trava de 0,4 s depois que o outro muda.

Comparado com o nosso desenho em `docs/MECANICA-DO-TEMPO.md`.
