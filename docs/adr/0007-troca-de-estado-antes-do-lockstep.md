# ADR 0007 — Sessão precisa de troca de estado antes de o lockstep significar algo

**Status:** aceito
**Data:** 2026-09-10

## Contexto

Ao implementar a barreira de tick apareceu a pergunta que a §2.3 resolve numa
palavra e a implementação não pode: **o que exatamente os dois lados simulam
em conjunto?**

O fluxo da §2.3 é `convite → aceite → congelar → trocar estado → barreira de
tick → LOOP → encerrar → commit`. O "trocar estado" está lá, e é ele que
carrega todo o peso: o visitante não tem o mapa do anfitrião, e não tem como
gerá-lo — uma colônia vivida não é derivável do tile do planeta.

A §4 foi **corrigida** por causa desta ADR. A redação original dizia "nada de
snapshot de mapa transferido pela rede", o que lida como proibição e não era o
que se queria dizer.

A correção veio em duas passadas, e a segunda desfez um erro meu.

Primeiro: *transferir o mapa é a única forma de tornar a experiência tangível
de fato; essa é a região sensível, por isso só em visitas e não o tempo todo —
para evitar sync contínuo e desync sempre.*

Eu li isso como "o mapa trafega uma vez, no início da sessão" — e errei o
alvo. A correção seguinte foi explícita: **a visita tem que ser lockstep em
tempo real.**

A diferença importa. Transferir o mapa é o que *inicia* a visita; o que a
*sustenta* é a sincronia contínua enquanto ela dura. Os dois jogadores
simulam o mesmo mapa, travados no mesmo tick — o visitante joga ali, junto,
não assiste a atualizações periódicas.

A regra, então, não é sobre o que trafega, é sobre **quando**: sincronia em
tempo real existe durante a visita, e só durante a visita. O erro do RT não
foi transferir mapa — foi transferir uma **foto**, fora de qualquer janela
sincronizada. Foto não é visita.

## Decisão

Separar em dois passos, e ser explícito sobre o que cada um prova.

**Passo A — sessão de ensaio (`TipoSessao.Ensaio`), feito.** Os dois lados
simulam sob a mesma barreira, cada um na própria colônia, **sem** mapa
compartilhado. Valida tempo, barreira exata, consenso de pausa, agendamento de
comando, encerramento e aborto com rollback.

Como as colônias são diferentes, divergir é o esperado: `SessaoInicio` carrega
`CompararDigitais = false` e o broker não aborta por digital diferente. Sem
essa flag, todo ensaio abortaria no primeiro relato — o mecanismo certo
disparando pelo motivo errado.

**Passo B — mapa compartilhado em lockstep de tempo real.** Duas partes, nesta
ordem:

1. **Bootstrap:** o anfitrião serializa o mapa da visita e o visitante o
   carrega, entrando no mesmo estado e no mesmo tick.
2. **Lockstep contínuo:** dali em diante os dois simulam aquele mapa em
   conjunto, sob a barreira que o coordenador já sustenta, trocando comandos e
   impressões digitais a cada intervalo. A digital passa a comparar estados
   equivalentes — e só aí ela significa alguma coisa.

O passo 2 é o que faz a visita ser visita. O passo 1 existe só para viabilizá-lo.

No fim da visita o mapa é **descarregado** do lado do visitante — sem cache,
sem delta, sem reconciliação. Ver [ADR 0008](0008-escala-alvo-e-ciclo-da-visita.md).

## Consequências

- O ensaio é honesto sobre o que não prova: ele **não** demonstra determinismo
  nem visita. Demonstra que a máquina de tempo, a barreira e o rollback
  funcionam com dois jogos reais — que é a metade que não dá para testar
  sozinho, e que o lockstep de verdade vai usar sem mudar uma linha.
- `CompararDigitais` é campo de protocolo, não gambiarra de teste: sessões
  futuras podem legitimamente não comparar (por exemplo, um espectador).
- O passo B reabre uma medição do RT que vale refazer: 1739 ms para ~1 MB. Um
  checkpoint de colônia real nesta máquina tem ~6 a 14 MB — mas o checkpoint é
  a partida inteira (mundo, facções, todos os mapas), e a sessão precisa de
  **um mapa**. Medir o custo real de serializar um mapa só é a primeira tarefa
  do passo B.
- O planeta **não** entra nessa conta: ele é determinístico a partir da semente
  e das opções de geração (§4.1), então só a identidade trafega. Já
  implementado: o log de mundo tem um planeta de registro, e quem chega com
  outro fica de fora do mundo compartilhado com motivo legível — sem perder o
  login nem o checkpoint.

## Barreira: por que um patch em `DoSingleTick`

"Ninguém passa do tick liberado" perde o sentido se a simulação avançar três
ticks antes de alguém perceber. Ajustar velocidade uma vez por frame é
aproximado; um prefixo em `TickManager.DoSingleTick` é exato, e é **um** ponto
de acoplamento num método público e antigo — catalogado no
`CatalogoDePatches`, com desligamento previsto se sumir.

Fora de sessão o limite é `long.MaxValue`, então o custo é uma comparação de
inteiros por tick e o jogo solo nunca é afetado (§11).
