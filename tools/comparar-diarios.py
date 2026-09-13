#!/usr/bin/env python3
"""Compara os diários dos dois lados e aponta a primeira divergência.

Por que existe: esta comparação foi feita à mão umas oito vezes seguidas, sempre
com os mesmos passos — achar o primeiro tick em que o contador de sorteios da
sessão difere, diferenciar os locais de chamada naquele tick, e diferenciar as
linhas de pawn no tick anterior. Cada rodada custava minutos e um erro de
geração já produziu duas conclusões erradas.

O que ele sabe que a mão esquece:

  * gerações. Depois de um ponto de junção o passo é rebobinado, e o mesmo
    número volta a acontecer com outro estado. Comparar sem separar junta
    momentos diferentes.
  * causa × consequência. O rastreio de pawn costuma acusar antes do de RNG,
    porque uma decisão pode divergir sem consumir sorteio nenhum.
"""
import glob, os, re, sys

P1 = os.path.expanduser("~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/WithFriends/diario")
P2 = [os.path.expanduser("~/.rimworld-arbitro/WithFriends/diario"),
      os.path.expanduser("~/.rimworld-jogador2/WithFriends/diario")]


def mais_novo(pasta):
    arqs = sorted(glob.glob(os.path.join(pasta, "*.log")), key=os.path.getmtime)
    return arqs[-1] if arqs else None


def historico(f):
    """Contador de sorteios da sessão por tick, do primeiro bloco despejado."""
    out, visto = {}, False
    for l in open(f, errors="replace"):
        if "histórico de RNG" in l:
            if visto:
                break
            visto = True
            continue
        if not visto:
            continue
        m = re.search(r"\btick\s+(\d+)\s+rng\s+(\d+)", l)
        if m:
            out[int(m.group(1))] = int(m.group(2))
    return out


def locais(f, alvo):
    dentro, cur, d = False, None, {}
    for l in open(f, errors="replace"):
        if "rastreio de RNG por local de chamada" in l:
            if dentro:
                break
            dentro = True
            continue
        if not dentro:
            continue
        m = re.search(r"\btick\s+(\d+)\s+total", l)
        if m:
            cur = int(m.group(1))
            continue
        if cur != alvo:
            continue
        m2 = re.match(r"\s*(\d+)x\s+(.*)$", l)
        if m2:
            k = m2.group(2).strip()
            d[k] = d.get(k, 0) + int(m2.group(1))
    return d


def pawns(f):
    dentro, tick, d = False, None, {}
    for l in open(f, errors="replace"):
        if "rastreio de estado dos pawns" in l:
            if dentro:
                break
            dentro = True
            continue
        if not dentro:
            continue
        m = re.search(r"\btick (\d+)\s*$", l)
        if m:
            tick = int(m.group(1))
            d.setdefault(tick, {})
            continue
        m2 = re.search(r"[#=](\d+)\s+(.*?)\s*$", l)
        if m2 and tick is not None:
            d[tick][m2.group(1)] = m2.group(2)
    return d


# Campos que descrevem o INSTRUMENTO, não o jogo: `saude` e `ordem` só existem
# depois que aquele pawn foi tickado com o rastreio ligado, e valem -1 até lá.
# Contá-los como diferença produzia "primeiro pawn diferente no tick 0" em toda
# corrida — um falso positivo que apontava para o lado errado da investigação.
INSTRUMENTO = re.compile(r"\s+(?:saude|ordem)\s+-?\d+")


def so_o_jogo(linha):
    return INSTRUMENTO.sub("", linha)


def curto(k, n=6):
    p = [x for x in k.split(" < ")
         if "ContadorDe" not in x and "DynamicMethodDefinition.Verse.Rand" not in x]
    return " < ".join(p[:n])


def main():
    a = mais_novo(P1)
    b = next((mais_novo(p) for p in P2 if os.path.isdir(p) and mais_novo(p)), None)
    if not a or not b:
        print("não achei os dois diários. rodou tools/emulacao.sh?")
        return 1

    print(f"A  {a}\nB  {b}\n")

    ha, hb = historico(a), historico(b)
    comuns = sorted(set(ha) & set(hb))
    if not comuns:
        print("os dois diários não têm histórico de RNG em comum — a visita chegou a começar?")
        return 1

    dif = [t for t in comuns if ha[t] != hb[t]]
    print(f"ticks comparados: {len(comuns)}  ({comuns[0]}..{comuns[-1]})")
    if not dif:
        print("\nNENHUMA DIVERGÊNCIA. Os dois lados sortearam igual o tempo todo.")
        return 0
    # 3 e não 1: divergir é o resultado esperado de uma reprodução, não uma
    # falha da ferramenta. Quem chama precisa distinguir "não rodou" de "rodou
    # e achou".

    codigo = 3
    t = dif[0]
    print(f"primeira divergência de sorteio: tick {t}  (A={ha[t]}  B={hb[t]})\n")

    la, lb = locais(a, t), locais(b, t)
    print(f"-- locais de chamada no tick {t}  (A={sum(la.values())} B={sum(lb.values())})")
    achou = False
    for k in sorted(set(la) | set(lb)):
        if la.get(k, 0) != lb.get(k, 0):
            achou = True
            print(f"   A={la.get(k,0):<4} B={lb.get(k,0):<4}")
            # A cadeia INTEIRA, uma chamada por linha. Truncada em seis níveis
            # ela mostrava "Filth.SpawnSetup < GenSpawn.Spawn < TryMakeFilth" e
            # escondia quem chamou — e é o chamador que diz se foi sangue de
            # ferimento, pegada ou vômito. Cada um leva a um lugar diferente.
            for nivel in curto(k, 99).split(" < "):
                print(f"        < {nivel}")
    if not achou:
        print("   (nenhum — o anel de locais não cobre este tick; ver RastreioDeRng.TicksGuardados)")

    pa, pb = pawns(a), pawns(b)
    print("\n-- primeiro pawn com estado diferente")
    for tick in sorted(set(pa) & set(pb)):
        d = [p for p in sorted(set(pa[tick]) & set(pb[tick]))
             if so_o_jogo(pa[tick][p]) != so_o_jogo(pb[tick][p])]
        if d:
            quando = "ANTES do sorteio divergir — é a causa" if tick < t else "depois — é consequência"
            print(f"   tick {tick} ({quando})")
            for p in d[:3]:
                print(f"     A: {pa[tick][p]}")
                print(f"     B: {pb[tick][p]}")
            break
    else:
        print("   (nenhum — a divergência não chegou a mudar estado de pawn rastreado)")

    return codigo


if __name__ == "__main__":
    sys.exit(main())
