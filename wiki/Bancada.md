# Bancada

Reproduzir uma divergência custava dois humanos, duas janelas e vários minutos de
jogo. E cada hipótese testada exigia repetir tudo. O gargalo nunca foi escrever o
remendo: foi **jogar**.

A bancada resolve isso. Tudo abaixo roda sem ninguém clicando.

```
wf servidor            sobe o coordenador
wf jogos               abre duas instâncias para jogar à mão
wf rodar SAVE          roda UMA colônia sem interface e relata ticks/s e erros
wf emular HOST ARB     roda uma visita inteira sozinha e sai
wf dirigir CENARIO     visita inteira com gestos de interface, e compara no fim
wf comparar [A B]      compara dois diários e aponta o primeiro sorteio diferente
wf deriva A B          quanto os dois lados se afastam, tick a tick
wf erros               agrupa erros e avisos, destacando os de um lado só
wf decisoes            mapa das decisões de jogador, extraído do Multiplayer
wf auditar             audita o assembly do jogo
```

## A regra de desenho que importa

Cada comando entra pelo **mesmo caminho de um clique**. Nenhum atalho para dentro
da simulação. Se o caminho do comando estiver quebrado, o teste quebra junto, que
é exatamente o que se quer de um teste.

## O árbitro

A segunda instância pode ser um **árbitro**: um jogo sem interface, numa pasta de
dados própria, que o anfitrião lança sozinho, convida, e que simula a visita do
outro lado. É RimWorld de verdade, mesma sessão, mesma barreira, mesmos comandos.
A única diferença para uma visita normal é a ausência de mouse, que é justamente
a variável que se quer isolar.

## As consultas que decidem onde remendar

```
wf auditar --escritores Pawn_PlayerSettings::selfTend
   RimWorld.HealthCardUtility.DrawOverviewTab

wf auditar --chamadores HealthCardUtility::CreateSurgeryBill
   RimWorld.Pawn_GuestTracker.GuestTrackerTickInterval     <- tick
   <GenerateSurgeryOption>b__2 / b__4 / b__5 / b__6        <- interface
   ...
```

Sem elas, achar onde remendar é ler código decompilado à mão. Com elas são dois
segundos, e a resposta vem completa: se dois lugares escrevem no campo, os dois
aparecem, e remendar só um seria divergência silenciosa.

Foi a segunda consulta que decidiu remendar a fonte em vez dos nove chamadores da
cirurgia, e a primeira que fechou o caso do clique com o botão direito.

## O limite conhecido da bancada

A emulação roda sem interface. Boa parte das causas que restam **nasce da
interface**: caches que a aba de saúde enche, posições que o quadro interpola.

Isso foi testado, não suposto: três experimentos desligaram um conserto conhecido
e rodaram a bancada com aquecimento e gestos, e os três vieram limpos. Está
registrado em `docs/MEDICOES.md`. Enquanto isso não mudar, **uma partida de gente
continua sendo o detector mais sensível que existe**, e é por isso que quase toda
causa nova chega por uma sessão jogada.
