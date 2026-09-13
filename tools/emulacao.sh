#!/usr/bin/env bash
# Reproduz uma visita inteira sem ninguém clicando.
#
# Por que existe:
#
#   Reproduzir uma divergência custava dois humanos, duas janelas e vários
#   minutos de jogo — e cada hipótese testada exigia repetir tudo. O gargalo
#   nunca foi escrever o guarda; foi JOGAR.
#
#   Com o árbitro (docs/ARBITRO.md) os dois lados podem ser instâncias sem
#   interface. Este script sobe o coordenador, abre o anfitrião headless, e ele
#   chama o árbitro, convida, despausa e deixa rodar. No fim, os dois diários
#   estão no disco para comparar.
#
#   É o RimWorld de verdade — mesma sessão, mesma barreira, mesmos comandos. A
#   única diferença para uma visita normal é a ausência de mouse, que é
#   justamente a variável que se quer isolar.
#
# Uso:
#   tools/emulacao.sh SAVE_ANFITRIAO SAVE_ARBITRO [segundos]
#
# Os dois saves precisam ser do MESMO PLANETA, senão o coordenador recusa com
# "planeta divergente" e nada acontece.

set -euo pipefail

ANFITRIAO_SAVE="${1:-}"
ARBITRO_SAVE="${2:-}"
SEGUNDOS="${3:-120}"

if [[ -z "$ANFITRIAO_SAVE" || -z "$ARBITRO_SAVE" ]]; then
    sed -n '2,/^[^#]/p' "${BASH_SOURCE[0]}" | sed '$d'
    exit 2
fi

JOGO="$HOME/.local/share/Steam/steamapps/common/RimWorld"
DADOS_P1="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
DADOS_ARB="$HOME/.rimworld-arbitro"
RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "== fechando o que estiver aberto"
pkill -f RimWorldLinux || true
pkill -f WithFriends.Server || true
sleep 1

echo "== compilando"
dotnet build "$RAIZ/client/Client.csproj" -v q --nologo
dotnet build "$RAIZ/server/Server.csproj" -v q --nologo

echo "== subindo o coordenador"
nohup dotnet "$RAIZ/server/bin/Debug/WithFriends.Server.dll" 25555 "$RAIZ/dados" \
    > "$RAIZ/dados-servidor.log" 2>&1 &
sleep 2

# A pasta do árbitro precisa da lista de mods; sem ela ele sobe sem o nosso mod
# e não faz nada — e o sintoma seria "o árbitro nunca apareceu online".
mkdir -p "$DADOS_ARB/Config" "$DADOS_ARB/Saves"
cp -n "$DADOS_P1/Config/ModsConfig.xml" "$DADOS_ARB/Config/" 2>/dev/null || true

# O save do árbitro é a colônia do VISITANTE, e ela costuma estar na pasta da
# segunda instância — não na do anfitrião. Procura nas duas.
for origem in "$DADOS_P1/Saves" "$HOME/.rimworld-jogador2/Saves"; do
    if [[ -f "$origem/$ARBITRO_SAVE.rws" ]]; then
        cp -n "$origem/$ARBITRO_SAVE.rws" "$DADOS_ARB/Saves/" 2>/dev/null || true
        break
    fi
done

if [[ ! -f "$DADOS_ARB/Saves/$ARBITRO_SAVE.rws" ]]; then
    echo "save do árbitro não encontrado: $ARBITRO_SAVE" >&2
    exit 1
fi

echo "== anfitrião headless: \"$ANFITRIAO_SAVE\", árbitro: \"$ARBITRO_SAVE\", $SEGUNDOS s"
cd "$JOGO"
nohup ./RimWorldLinux \
    -batchmode -nographics \
    -emulacao -emulacaosave="$ANFITRIAO_SAVE" \
    -arbitrosave="$ARBITRO_SAVE" \
    -emulacaosegundos="$SEGUNDOS" \
    -logFile "$DADOS_P1/Player.log" > /dev/null 2>&1 &

echo "   (o anfitrião lança o árbitro sozinho em $DADOS_ARB)"
echo
echo "acompanhar:"
echo "  tail -f \"$DADOS_P1/Player.log\" | grep --line-buffered WithFriends"
echo
echo "quando acabar, comparar os diários:"
echo "  tools/comparar-diarios.sh"
