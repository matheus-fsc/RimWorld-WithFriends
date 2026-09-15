#!/usr/bin/env python3
"""
decisoes — o mapa das decisões de jogador, extraído do Multiplayer.

Por que existe
--------------
O Zetrith sustenta 602 remendos e ~560 registros de sincronia num mod que
funciona. Sob lockstep, essa lista responde "o que precisa ser determinístico".
Mas ela responde outra pergunta também, e é a que interessa a uma arquitetura
de decisão autoritativa: **onde a decisão do jogador entra na simulação**.

É a mesma referência lida pela outra metade. Cada `SyncMethod.Register` é um
lugar em que um clique vira mudança de estado — ou seja, exatamente o que
precisaria virar comando.

Reusar a lista é reusar conhecimento de domínio, não código: cada linha dela foi
paga com o bug de alguém.

Uso
---
    tools/decisoes.py                # o que falta, agrupado
    tools/decisoes.py --todos        # inclusive o que já cobrimos
    tools/decisoes.py --nossos       # só o que já é comando aqui
"""

import os
import re
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
MP = os.path.join(AQUI, "..", "referencia", "repos", "Multiplayer",
                  "Source", "Client", "Syncing", "Game")
CLIENTE = os.path.join(AQUI, "..", "client")

# O que já vira comando aqui. Escrito à mão porque é curto e porque enganar-se
# para mais seria pior do que não ter a lista: esconderia trabalho real.
NOSSOS = {
    "Pawn_DraftController.Drafted":            "Alistar",
    "Pawn_DraftController.FireAtWill":         "Alternar",
    "CompForbiddable.Forbidden":               "Alternar",
    "Pawn_JobTracker.TryTakeOrderedJob":       "OrdemDeTrabalho",
    "Pawn_JobTracker.TryTakeOrderedJobPrioritizedWork": "OrdemPriorizada",
    "Pawn_JobTracker.EndCurrentJob":           "EncerrarJob",
    "Designator.DesignateSingleCell":          "Designar",
    "Designator.DesignateMultiCell":           "DesignarVarias",
    "Designator.DesignateThing":               "Designar",
    "StorageSettings.Priority":                "Estoque",
    "ThingFilter":                             "Estoque",
    "TickManager.CurTimeSpeed":                "Velocidade",
}

# Assuntos que a visita não toca (§4: decisão de colônia é de quem mora nela, e
# uma visita dura minutos). Fora de escopo até prova em contrário.
FORA_DE_ESCOPO = [
    (r"Research", "pesquisa não acontece numa visita"),
    (r"Ideo|Precept|Ritual", "ideologia é da colônia, e ritual dura mais que a visita"),
    (r"Caravan|Transporter|Shuttle|Travel", "caravana sai do mapa; a visita é num mapa só"),
    (r"Quest|Storyteller", "missão é decisão de colônia (§4)"),
    (r"Trade|Market", "comércio vai pelo planeta, sem lockstep (§6)"),
    (r"Policy|Outfit|FoodRestriction|DrugPolicy|Reading|Timetable",
     "política é arrumação de casa, não de visita — decidir de propósito"),
    (r"Faction|Royal|Title|Permit", "relação de facção é da colônia"),
    (r"Genes?|Xenotype|Growth", "biotecnologia é projeto longo"),
]


def alvos_do_mp():
    """(tipo.membro, arquivo) de cada registro de sincronia do Multiplayer."""
    fora = {}
    for nome in ("SyncMethods.cs", "SyncDelegates.cs", "SyncFields.cs"):
        caminho = os.path.join(MP, nome)
        if not os.path.isfile(caminho):
            continue
        texto = open(caminho, encoding="utf8", errors="replace").read()

        # SyncMethod.Register(typeof(X), nameof(Y.Z))  e  Sync.Field(typeof(X), "y")
        for m in re.finditer(
                r"(?:SyncMethod\.Register|SyncDelegate\.Register|Sync\.Field|SyncMethod\.Lambda|"
                r"MpCompat\w*)\s*\(\s*typeof\(([\w\.]+)\)\s*,\s*(?:nameof\(\s*[\w\.]*?(\w+)\s*\)|\"(\w+)\")",
                texto):
            tipo = m.group(1).split(".")[-1]
            membro = m.group(2) or m.group(3)
            fora.setdefault(f"{tipo}.{membro}", nome)
    return fora


def fora_de_escopo(alvo):
    for padrao, motivo in FORA_DE_ESCOPO:
        if re.search(padrao, alvo, re.I):
            return motivo
    return None


def main():
    todos = "--todos" in sys.argv
    so_nossos = "--nossos" in sys.argv

    alvos = alvos_do_mp()
    if not alvos:
        print(f"não achei os registros do Multiplayer em {MP}", file=sys.stderr)
        return 1

    cobertos, fora, faltando = [], [], []
    for alvo in sorted(alvos):
        if alvo in NOSSOS:
            cobertos.append(alvo)
        elif (motivo := fora_de_escopo(alvo)):
            fora.append((alvo, motivo))
        else:
            faltando.append(alvo)

    print(f"registros de sincronia do Multiplayer: {len(alvos)}\n")

    if so_nossos or todos:
        print(f"== já é comando aqui ({len(cobertos)})\n")
        for a in cobertos:
            print(f"  {a:<52} {NOSSOS[a]}")
        print()
        if so_nossos:
            return 0

    # Agrupado por assunto, e não em lista corrida: 259 linhas não é um plano,
    # é uma parede. O que decide ordem de ataque é o tamanho de cada família e
    # o quanto ela aparece numa visita de minutos.
    familias = {}
    for a in faltando:
        tipo = a.split(".")[0]
        chave = (
            "pawn"      if tipo.startswith("Pawn") or tipo in ("Ability",) else
            "construção" if tipo.startswith("Building") or tipo.startswith("Blueprint") or tipo.startswith("Frame") else
            "bancada"   if tipo.startswith("Bill") else
            "zona/área" if tipo.startswith("Area") or tipo.startswith("Zone") or tipo.startswith("Plan") else
            "animal"    if "Training" in tipo or tipo.startswith("Pawn_Training") else
            "comp"      if tipo.startswith("Comp") else
            "outros"
        )
        familias.setdefault(chave, []).append(a)

    print(f"== FALTA ({len(faltando)}), por assunto\n")
    for chave, itens in sorted(familias.items(), key=lambda p: -len(p[1])):
        print(f"  {chave:<12} {len(itens):>3}   {', '.join(i.split('.')[0] for i in itens[:4])}…")

    if todos:
        print()
        for chave, itens in sorted(familias.items()):
            print(f"\n  -- {chave}")
            for a in itens:
                print(f"     {a}")

    print(f"\n== fora de escopo ({len(fora)})\n")
    vistos = set()
    for a, motivo in fora:
        if motivo not in vistos:
            vistos.add(motivo)
            print(f"  {motivo}")
    print(f"\n  ({len(fora)} registros, agrupados pelos {len(vistos)} motivos acima)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
