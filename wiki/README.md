# Conteúdo da wiki

Estas páginas são a wiki do projeto no GitHub, versionadas aqui junto com o
código para não se perderem e para mudarem no mesmo commit que muda o que elas
descrevem.

Publicar:

    git clone https://github.com/matheus-fsc/RimWorld-WithFriends.wiki.git /tmp/wiki
    cp wiki/*.md /tmp/wiki/ && cd /tmp/wiki && git add -A && git commit && git push

**HTTPS, não SSH.** `git@github.com:...wiki.git` responde "repository not found"
mesmo com a wiki criada, visível e com a aba habilitada. Custou uma tentativa
descobrir isso.

E a wiki só ganha repositório git **depois que a primeira página é criada pela
web** (https://github.com/matheus-fsc/RimWorld-WithFriends/wiki/_new). Antes
disso o clone falha com a mesma mensagem, o que confunde as duas causas.
