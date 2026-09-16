# Conteúdo da wiki

Estas páginas são a wiki do projeto no GitHub, versionadas aqui junto com o
código para não se perderem e para mudarem no mesmo commit que muda o que elas
descrevem.

Publicar, depois que a wiki existir no GitHub:

    git clone git@github.com:matheus-fsc/RimWorld-WithFriends.wiki.git /tmp/wiki
    cp wiki/*.md /tmp/wiki/ && cd /tmp/wiki && git add -A && git commit && git push

A wiki só ganha repositório git depois que a primeira página é criada pela web
(https://github.com/matheus-fsc/RimWorld-WithFriends/wiki/_new). Até lá, `git
clone` da wiki responde "repository not found", mesmo com a aba habilitada.
