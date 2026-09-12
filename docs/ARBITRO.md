# O árbitro

Uma instância do jogo que **simula e não desenha**, para responder uma pergunta
que duas instâncias não conseguem responder sozinhas: **qual dos dois lados
desvia?**

## Por que ele existe

Depois de seis divergências seguidas no mesmo `DropBloodFilth`, tudo o que o
rastreio mede bate entre os dois lados no tick anterior:

| | |
|---|---|
| posição, job, custo, fila, draft | idênticos |
| taxa de sangramento | idêntica **bit a bit** |
| hediffs, postura, corpo | idênticos |
| ritmo e fase do ciclo | idênticos |
| delta da saúde | idêntico |
| ordem de tick dos pawns | idêntica |
| contador de sorteios | idêntico |

E no tick seguinte um larga sangue e o outro não. Acrescentar mais campos virou
chute: cada um dos últimos quatro eliminou uma hipótese, e não sobrou nenhuma.

Com dois jogadores, digitais diferentes não dizem quem errou. Um terceiro
simulador **sem interface** diz.

## O que ele responde

| resultado | conclusão |
|---|---|
| anfitrião × árbitro diverge | a interface do **anfitrião** está implicada |
| nunca diverge | era a interface do **visitante** humano |
| diverge do mesmo jeito | **não é interface nenhuma** — a família inteira cai |

Os três são resposta. O terceiro é o mais valioso: fecha de uma vez a linha de
investigação que consumiu um dia inteiro.

## Como difere do árbitro do Multiplayer

O dele é um **terceiro participante**, porque a partida dele é de N jogadores.
A nossa visita é de dois por construção — anfitrião e visitante — e generalizar
isso seria a maior parte do trabalho: coordenador, protocolo e testes.

Não precisa. O árbitro entra na **vaga de visitante**, que já existe. Zero
mudança de protocolo.

O preço: durante o experimento o visitante humano não joga. Como o objetivo é
comparar simulações, e não jogar, isso não custa nada.

## Como rodar

```
RimWorldLinux -batchmode -nographics -arbitro -arbitrosave=NomeDoSave \
              -savedatafolder=/caminho/da/instancia
```

- `-arbitro` liga o modo: não desenha, volume zero, conecta sozinho, aceita o
  convite sozinho e **nunca propõe comando**.
- `-arbitrosave=NOME` é a colônia que ele abre. Precisa ser **do mesmo planeta**
  que a do anfitrião, senão o coordenador recusa com "planeta divergente".
- `-savedatafolder` separa o perfil, como na segunda instância humana.

Depois é o fluxo normal: o anfitrião convida, e o árbitro aceita sem que ninguém
clique.

## O que ele não é

Não é desempate de autoridade. Numa visita o anfitrião já é autoritativo por
desenho (§4) — quando divergimos, o estado dele ganha, e é isso que a
ressincronização faz. O árbitro é **instrumento**, não árbitro no sentido de
juiz.

## Os remendos

Vêm do `ArbiterPatches.cs` dele: `GUISkin` vazia, e cancelar
`WaterInfo.SetTextures`, `PortraitsCache.Get` e `SubcameraDriver.UpdatePositions`
— os pontos em que o jogo assume que existe textura mesmo com `-nographics`.

Todos inertes sem `-arbitro`: uma instância normal não passa por nenhum deles.
