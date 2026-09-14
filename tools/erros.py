#!/usr/bin/env python3
"""
erros — o que deu errado nos dois lados, agrupado, e o que deu errado em um só.

Por que existe
--------------
Uma corrida deixa milhares de linhas de erro e aviso, e quase todas são a mesma
linha repetida. A última medida: 993 erros num diário, todos idênticos — o NRE
do atlas de textura, que é ruído conhecido de instância sem placa de vídeo.
Lidas uma a uma, elas escondem as três que importam.

Mas o achado que este programa existe para dar é outro: **erro que acontece em
um lado só**. Os dois lados rodam a mesma simulação; uma exceção que nasce só
num deles é, por definição, uma diferença — e das mais baratas de investigar,
porque vem com pilha.

Uso
---
    tools/erros.py            # compara os diários mais novos dos dois lados
    tools/erros.py --todos    # inclui o ruído conhecido
"""

import glob
import os
import re
import sys

P1 = os.path.expanduser("~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/WithFriends/diario")
P2 = [os.path.expanduser("~/.rimworld-arbitro/WithFriends/diario"),
      os.path.expanduser("~/.rimworld-jogador2/WithFriends/diario")]

# Ruído conhecido: acontece sempre, dos dois lados, e não é defeito nosso.
# Cada entrada precisa de um motivo — lista de silêncio sem motivo vira lugar
# onde bug se esconde.
RUIDO = [
    (r"Could not execute post-long-event action",
     "atlas de textura sem placa de vídeo — instância headless, acontece nos dois lados"),
    (r"Shader .* is not supported on this GPU",
     "idem"),
]

# O que apaga para agrupar: número, id, posição, hash, tempo. O que sobra é a
# forma da linha, que é o que se quer contar.
NORMALIZA = [
    (r"sha256:[0-9a-f]+", "<hash>"),
    (r"\b\d+[,.]\d+\b", "<n>"),
    (r"\b\d+\b", "<n>"),
    (r"\[Ref [0-9A-F]+\]", "[Ref]"),
]


def mais_novo(pasta):
    arqs = sorted(glob.glob(os.path.join(pasta, "*.log")), key=os.path.getmtime)
    return arqs[-1] if arqs else None


def assinatura(texto):
    for padrao, troca in NORMALIZA:
        texto = re.sub(padrao, troca, texto)
    return texto.strip()[:160]


def eh_ruido(texto):
    for padrao, motivo in RUIDO:
        if re.search(padrao, texto):
            return motivo
    return None


def ler(caminho):
    """(assinatura → [nivel, quantas, primeira linha inteira, primeiro/último tempo])"""
    achados = {}
    for linha in open(caminho, encoding="utf8", errors="replace"):
        m = re.match(r"^(\d\d:\d\d:\d\d\.\d\d\d) (ERRO|AVISO) (.*)$", linha.rstrip("\n"))
        if not m:
            continue
        hora, nivel, texto = m.groups()
        chave = (nivel, assinatura(texto))
        if chave not in achados:
            achados[chave] = {"n": 0, "exemplo": texto, "de": hora, "ate": hora}
        achados[chave]["n"] += 1
        achados[chave]["ate"] = hora
    return achados


def main():
    todos = "--todos" in sys.argv

    a = mais_novo(P1)
    b = max((c for c in (mais_novo(p) for p in P2) if c), key=os.path.getmtime, default=None)
    if not a:
        print("não achei diário nenhum.")
        return 1

    print(f"A  {a}")
    print(f"B  {b or '(nenhum)'}\n")

    da = ler(a)
    db = ler(b) if b else {}

    chaves = sorted(set(da) | set(db), key=lambda k: -(da.get(k, {}).get("n", 0) + db.get(k, {}).get("n", 0)))

    so_de_um, comuns, ruidos = [], [], []
    for k in chaves:
        motivo = eh_ruido(k[1])
        if motivo and not todos:
            ruidos.append((k, motivo))
        elif b and (k in da) != (k in db):
            so_de_um.append(k)
        else:
            comuns.append(k)

    if so_de_um:
        print("== SÓ DE UM LADO — é aqui que se olha primeiro\n")
        for k in so_de_um:
            lado = "A" if k in da else "B"
            d = da.get(k) or db[k]
            print(f"  [{k[0]}] {lado} apenas, {d['n']}x  ({d['de']} → {d['ate']})")
            print(f"     {d['exemplo'][:150]}\n")
    elif b:
        print("== nenhum erro ou aviso exclusivo de um dos lados.\n")

    if comuns:
        print("== nos dois lados\n")
        for k in comuns:
            na, nb = da.get(k, {}).get("n", 0), db.get(k, {}).get("n", 0)
            d = da.get(k) or db[k]
            print(f"  [{k[0]}] A={na} B={nb}")
            print(f"     {d['exemplo'][:150]}\n")

    if ruidos:
        print("== ruído conhecido (use --todos para ver inteiro)\n")
        for k, motivo in ruidos:
            na, nb = da.get(k, {}).get("n", 0), db.get(k, {}).get("n", 0)
            print(f"  [{k[0]}] A={na} B={nb}  — {motivo}")

    # Código de saída: 3 quando há assimetria, pelo mesmo motivo do comparador —
    # achar não é falhar, e quem chama precisa distinguir os dois.
    return 3 if so_de_um else 0


if __name__ == "__main__":
    sys.exit(main())
