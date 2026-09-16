#!/usr/bin/env python3
"""
deriva — quanto dois lados se afastam depois de uma divergência grande.

Por que existe
--------------
A ADR 0022 propõe que o anfitrião mande correções em vez de ressincronizar o
mapa inteiro, e a conta só fecha se a deriva couber numa correção a 1 Hz. Isso
não se decide por argumento: decide-se medindo o afastamento real dos pawns
COMUNS aos dois lados, tick a tick, depois de uma divergência deliberada.

O que mede
----------
Para cada tick presente nos dois diários, entre os pawns que existem dos dois
lados: quantos estão em posições diferentes, a distância média e a máxima. Pawn
que só existe de um lado (o assalto injetado) fica de fora de propósito — o que
se quer saber é o custo de corrigir o que os dois lados TÊM em comum.

Uso
---
    tools/deriva.py DIARIO_A DIARIO_B
"""

import math
import re
import sys
from collections import defaultdict

# A primeira linha de um despejo vem com carimbo de hora ("11:57:26.335 msg
# tick 757"); as seguintes, não. Exigir o começo da linha lia só uma em cada
# quatrocentas — e o resultado era "nenhum tick em comum".
TICK = re.compile(r"^(?:\d\d:\d\d:\d\d\.\d+ \w+)?\s*tick (\d+)\s*$")
PAWN = re.compile(r"^\s*#(\d+)\s+\S.*?\bpos\s+(-?\d+),\s*(-?\d+)\b")


def ler(caminho):
    """{tick: {id: (x, z)}} — só o PRIMEIRO despejo de cada tick."""
    por_tick = {}
    atual = None
    with open(caminho, errors="replace") as f:
        for linha in f:
            m = TICK.match(linha)
            if m:
                t = int(m.group(1))
                # Os números de passo se repetem depois de um resync, e um
                # despejo periódico reescreve ticks que já apareceram. O
                # primeiro é o que vale: misturar gerações foi o que já produziu
                # uma diferença inventada num relatório anterior.
                atual = por_tick.setdefault(t, {}) if t not in por_tick else None
                continue
            if atual is None:
                continue
            m = PAWN.match(linha)
            if m:
                atual[int(m.group(1))] = (int(m.group(2)), int(m.group(3)))
    return por_tick


def main():
    if len(sys.argv) != 3:
        print(__doc__.strip())
        return 2

    a, b = ler(sys.argv[1]), ler(sys.argv[2])
    ticks = sorted(set(a) & set(b))
    if not ticks:
        print("nenhum tick em comum nos dois diários")
        return 1

    print(f"ticks em comum: {len(ticks)}  ({ticks[0]}..{ticks[-1]})\n")
    print(f"{'tick':>7} {'comuns':>7} {'só A':>5} {'só B':>5} "
          f"{'difer.':>7} {'%':>6} {'média':>7} {'máx':>6}")

    linhas = []
    for t in ticks:
        pa, pb = a[t], b[t]
        comuns = set(pa) & set(pb)
        if not comuns:
            continue
        distancias = [math.dist(pa[i], pb[i]) for i in comuns]
        difer = [d for d in distancias if d > 0]
        linhas.append((t, len(comuns), len(pa) - len(comuns), len(pb) - len(comuns),
                       len(difer),
                       100.0 * len(difer) / len(comuns),
                       sum(difer) / len(difer) if difer else 0.0,
                       max(distancias)))

    # Uma linha a cada 50 ticks: a tabela inteira são milhares, e o que se lê
    # aqui é a TENDÊNCIA, não o valor de um tick específico.
    passo = max(1, len(linhas) // 60)
    for l in linhas[::passo]:
        print(f"{l[0]:>7} {l[1]:>7} {l[2]:>5} {l[3]:>5} {l[4]:>7} "
              f"{l[5]:>5.1f}% {l[6]:>7.2f} {l[7]:>6.1f}")

    primeira, ultima = linhas[0], linhas[-1]
    print(f"\nprimeiro tick medido {primeira[0]}: {primeira[4]}/{primeira[1]} "
          f"diferentes ({primeira[5]:.1f}%), média {primeira[6]:.2f}, máx {primeira[7]:.1f}")
    print(f"último  tick medido {ultima[0]}: {ultima[4]}/{ultima[1]} "
          f"diferentes ({ultima[5]:.1f}%), média {ultima[6]:.2f}, máx {ultima[7]:.1f}")

    # O número que decide a ADR 0022: corrigir a 1 Hz custa mandar a posição dos
    # que estão diferentes, uma vez por segundo. 60 ticks é um segundo de jogo
    # em velocidade normal.
    pico = max(linhas, key=lambda l: l[4])
    print(f"pico de divergentes: {pico[4]} de {pico[1]} no tick {pico[0]} "
          f"({pico[5]:.1f}%)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
