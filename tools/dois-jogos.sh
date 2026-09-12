#!/usr/bin/env bash
# Abre as duas instâncias do RimWorld para testar sessão.
#
# Por que existe:
#
#   1. Lockstep exige código idêntico dos dois lados. Como o mod é um symlink
#      para dist/, basta um `dotnet build` — mas os dois jogos precisam ser
#      reabertos, porque o RimWorld carrega assembly na subida. Este script
#      compila e reabre na ordem certa.
#
#   2. O `player_id` vive nas settings do mod, dentro da pasta de dados. Duas
#      instâncias na mesma pasta teriam a MESMA identidade (§7.1 regra 5), daí
#      o -savedatafolder na segunda.
#
#   3. O Unity escreve Player.log num caminho fixo, que NÃO acompanha o
#      -savedatafolder. Sem -logFile, a segunda instância sobrescreveria o log
#      da primeira — justamente o que se quer ler quando algo dá errado.
#
# Uso:
#   tools/dois-jogos.sh                 compila, fecha os jogos abertos e abre os dois
#   tools/dois-jogos.sh --sem-build     não recompila
#   tools/dois-jogos.sh --servidor      sobe o coordenador junto
#   tools/dois-jogos.sh --so-segundo    abre só a segunda instância
#   tools/dois-jogos.sh --matar         só fecha o que estiver aberto

set -euo pipefail

JOGO="$HOME/.local/share/Steam/steamapps/common/RimWorld"
DADOS_P1="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
DADOS_P2="$HOME/.rimworld-jogador2"
RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

COMPILAR=1
COM_SERVIDOR=0
SO_SEGUNDO=0
SO_MATAR=0

for arg in "$@"; do
    case "$arg" in
        --sem-build)  COMPILAR=0 ;;
        --servidor)   COM_SERVIDOR=1 ;;
        --so-segundo) SO_SEGUNDO=1 ;;
        --matar)      SO_MATAR=1 ;;
        -h|--help)    sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *)            echo "opção desconhecida: $arg (use --help)" >&2; exit 2 ;;
    esac
done

if [[ ! -x "$JOGO/RimWorldLinux" ]]; then
    echo "RimWorld não encontrado em $JOGO" >&2
    exit 1
fi

echo "== fechando instâncias abertas"
pkill -f RimWorldLinux || true
sleep 1

if [[ $SO_MATAR -eq 1 ]]; then
    echo "pronto."
    exit 0
fi

if [[ $COMPILAR -eq 1 ]]; then
    echo "== compilando o mod"
    dotnet build "$RAIZ/client/Client.csproj" -v q --nologo
    echo "   dist/ atualizado — as duas instâncias carregam o mesmo código"
fi

if [[ $COM_SERVIDOR -eq 1 ]]; then
    echo "== subindo o coordenador"
    dotnet build "$RAIZ/server/Server.csproj" -v q --nologo
    pkill -f WithFriends.Server || true
    sleep 1
    nohup dotnet "$RAIZ/server/bin/Debug/WithFriends.Server.dll" 25555 "$RAIZ/dados" \
        > "$RAIZ/dados-servidor.log" 2>&1 &
    sleep 2
    echo "   log: $RAIZ/dados-servidor.log"
fi

# A segunda instância precisa da lista de mods; as settings do mod NÃO são
# copiadas — é delas que sairia o player_id duplicado.
mkdir -p "$DADOS_P2/Config"
if [[ ! -f "$DADOS_P2/Config/ModsConfig.xml" ]]; then
    cp "$DADOS_P1/Config/ModsConfig.xml" "$DADOS_P2/Config/"
    echo "== lista de mods semeada em $DADOS_P2/Config/ModsConfig.xml"
fi

LOG_P1="$DADOS_P1/Player.log"
LOG_P2="$DADOS_P2/Player.log"

cd "$JOGO"

if [[ $SO_SEGUNDO -eq 0 ]]; then
    echo "== abrindo jogador 1"
    nohup ./RimWorldLinux -logFile "$LOG_P1" > /dev/null 2>&1 &
    sleep 3
fi

echo "== abrindo jogador 2  (pasta de dados: $DADOS_P2)"
nohup ./RimWorldLinux -savedatafolder="$DADOS_P2" -logFile "$LOG_P2" > /dev/null 2>&1 &

cat <<FIM

pronto. logs separados:

  jogador 1   $LOG_P1
  jogador 2   $LOG_P2

acompanhar os dois lado a lado:

  tail -f "$LOG_P1" | grep --line-buffered WithFriends
  tail -f "$LOG_P2" | grep --line-buffered WithFriends

FIM
