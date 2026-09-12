#!/usr/bin/env fish
# Sobe o coordenador, reconstruindo antes.
#
# O projeto do servidor não gera apphost justamente para que compilar com ele
# no ar funcione; este script só encapsula build + execução.
#
#   tools/servidor.fish [porta] [pasta-de-dados]

set -l porta 25555
set -l dados dados
if test -n "$argv[1]"
    set porta $argv[1]
end
if test -n "$argv[2]"
    set dados $argv[2]
end

set -l raiz (dirname (status --current-filename))/..

dotnet build $raiz/server/Server.csproj -v q --nologo
or exit 1

exec dotnet $raiz/server/bin/Debug/WithFriends.Server.dll $porta $dados
