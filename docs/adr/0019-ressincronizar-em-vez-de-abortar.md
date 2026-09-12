# ADR 0019 — Ressincronizar em vez de abortar

**Estado:** aceito. Servidor e cliente implementados; falta medir em jogo.

## O problema

Divergir era o fim. Três digitais seguidas sem bater e a sessão abortava: os dois
voltavam ao checkpoint pré-sessão, o encontro se perdia.

Isso tornava **cada causa desconhecida cara**. E causa desconhecida só aparece
jogando: o auditor de IL encontra o que *pode* divergir, mas o que *diverge* só
se descobre no meio de um combate contra mecanoides, uma vez, depois de vinte
minutos de jogo.

Pior: uma visita rendia **um** relatório de divergência. A primeira causa
escondia todas as outras — a simulação parava antes de chegar nelas.

## A decisão

Divergiu: refazer o **ponto de junção** e continuar.

É o mesmo caminho do bootstrap (ADR 0010), disparado por divergência em vez de
por alguém entrando. O anfitrião manda a partida e **os dois** recarregam. É o
`CreateJoinPoint` do Multiplayer, que faz `SaveAndReload()` em todos os clientes
— não só em quem chega.

Os dois recarregarem não é desperdício. Um jogo vivo e o mesmo jogo recém-
carregado não são idênticos: medimos 891 coisas de diferença e 3,5× de consumo
de RNG entre um lado vivo e um lado restaurado. Sair de estados diferentes é
exatamente o que produziria a próxima divergência.

## Como funciona

| | |
|---|---|
| gatilho | 3 digitais seguidas sem bater (inalterado) |
| de onde recomeça | último tick em que os dois bateram |
| quem manda o estado | o anfitrião — a visita acontece na colônia dele (§4) |
| durante | sessão em `Ressincronizando`: barreira congelada, comando recusado com motivo |
| volta a andar | quando os dois relatam o tick novo |
| orçamento | 5 por sessão; depois disso, aborto com o histórico inteiro |

O pedido é **idempotente** e repetido enquanto os dois não chegam: um lado que o
perca no meio de um recarregamento não deixa a visita pendurada.

## O que isto muda além de não perder o encontro

**Diagnóstico em lote.** A simulação continua depois de cada divergência, então
uma visita rende vários relatórios em vez de um. As causas restantes aparecem
juntas em vez de uma por sessão — e é por isso que esta alavanca vem antes de
continuar caçando guardas um a um.

**Não depende de conhecer a causa.** Funciona inclusive para as que nunca vamos
achar, e para as que vierem de mods de terceiros.

## Os riscos, e o que foi feito

**Perder a colônia do visitante.** `VisitaEmAndamento.Comecar` passou a ser
chamada também no ponto de junção refeito — e ali quem chama está dentro da
partida do anfitrião, cujo congelador não conhece hash nenhum. Apagar o
`HashPreSessao` apagaria o caminho de volta. Agora um `null` nunca sobrescreve um
ponto de retorno existente: perder o encontro é aceitável, perder a colônia não
é (§2.3).

**Laço de ressincronização.** Uma causa que reaparece em dez segundos reapareceria
na décima vez também, e a visita viraria uma sequência de recarregamentos. Daí o
orçamento — e, esgotado ele, o aborto carrega **todas** as divergências
registradas, não só a última.

**Carimbo contra o passo velho.** Enquanto o estado novo viaja, o passo contra o
qual carimbar ainda não existe. Comando é recusado com motivo, e
`Sessao.RecomecarEm` zera relato, teto, carimbo e digitais juntos — deixar
qualquer um para trás repetiria o erro que matava a construção pausada.

## O que ainda não está feito

- Medir o custo real em jogo (serializar + transferir + os dois recarregarem).
- Reaplicar comandos perdidos entre o último tick válido e a detecção: hoje eles
  somem. São poucos segundos de ordens, e refazê-las é do jogador.
- Delta em vez da partida inteira. É o que ligaria esta alavanca ao teto descrito
  em `PROGRESSO.md`: ressincronizar barato e com frequência **é** estado
  autoritativo.


## O laço, e por que o Multiplayer não tem um

**Ele não ressincroniza sozinho.** Ao detectar desync, para: `ClearSimulating()`,
`session.desynced = true`, e abre uma janela com cinco botões. O "Try resync" é
`Rejoiner.DoRejoin()` — `ClearAllMapsAndWorld()`, `Current.Game = null`, e rebaixa
tudo do anfitrião. Manual, e mais pesado que o nosso.

Então a ausência de laço lá não é recuperação melhor: é **não haver tentativa
automática**. Ele para no primeiro desync e pergunta. O que faz parecer polido é
outra coisa — desync é raro, por sete anos de guardas.

Nosso laço é consequência de uma escolha deliberada: continuar jogando. O que
faltava era um limite que não fosse só contar tentativas.

### O limite que faltava

Se a divergência volta **menos de 64 passos** depois do ponto de junção, os dois
lados partiram de um estado idêntico e se afastaram de novo — é a simulação que
difere, e refazer o ponto vai dar no mesmo. A sessão desiste ali, com o
diagnóstico, em vez de gastar as cinco tentativas.

64 passos são pouco mais de um segundo a 1×: tempo de sobra para uma divergência
de **estado** aparecer, e curto demais para uma de **comportamento** esperar.

O orçamento de cinco continua valendo para o outro caso — pontos que duram e
depois divergem de novo, que é quando ressincronizar de fato ajuda.
