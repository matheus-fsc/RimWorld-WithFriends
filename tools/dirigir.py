#!/usr/bin/env python3
"""
dirigir — uma visita inteira, com alguém mexendo na interface, sem ninguém.

Por que existe
--------------
A emulação já roda uma visita sozinha, mas sem interface: nenhum menu aberto,
nenhuma seleção, nenhuma câmera movida. E todas as causas de divergência
achadas até agora nasceram exatamente aí — a ordem dos vizinhos, as células de
zona, o memo de alcançabilidade, o `EndCurrentJob` do "ir aqui".

Com a porta de controle, os gestos da interface deixam de precisar de gente.
Este programa sobe a visita, espera ela ficar de pé, faz os gestos no lado do
anfitrião (só um dos lados mexe na interface — é a assimetria que produz o bug)
e compara os diários no fim.

Uso
---
    tools/dirigir.py ir-aqui
    tools/dirigir.py menus --segundos 240
    tools/dirigir.py --lista

Saída
-----
    0  rodou e os dois lados bateram
    1  não consegui rodar
    3  divergiram (é resultado, não falha)
"""

import argparse
import os
import random
import subprocess
import sys
import time

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

from controle import Controle          # noqa: E402


# ----------------------------------------------------------------------
# Cenários. Cada um recebe a conexão com o ANFITRIÃO e faz gestos de
# interface. O árbitro não é tocado de propósito: é a assimetria entre os
# dois lados que revela a divergência.
# ----------------------------------------------------------------------

def alistados(c):
    return [p for p in c.pawns() if p["alistado"]]


def cenario_ir_aqui(c, log):
    """
    O gesto que encerrava o Goto só na máquina de quem clicava.

    A pausa antes de ler a posição não é detalhe: sem ela o pawn anda entre a
    leitura e o arrasto, `pawn.Position == gotoLoc` deixa de valer, e o gesto
    erra o caminho que interessa.
    """
    pawns = alistados(c)
    if not pawns:
        log("nenhum colono alistado — nada a arrastar")
        return

    ids = " ".join(str(p["id"]) for p in pawns)
    c.cmd(f"selecionar {ids}")

    for volta in range(6):
        alvo = (random.randint(20, 200), random.randint(20, 200))
        for p in pawns:
            c.cmd(f"ir {p['id']} {alvo[0]},{alvo[1]}")
        time.sleep(1.2)

        c.cmd("velocidade Paused")
        agora = {p["id"]: p for p in c.pawns() if p["alistado"]}
        c.cmd("velocidade Fast")

        for p in pawns:
            atual = agora.get(p["id"])
            if atual:
                c.cmd(f"irarrastando {atual['x']},{atual['z']}")

        log(f"volta {volta + 1}: {len(pawns)} arrasto(s) em cima dos próprios colonos")
        time.sleep(2.0)


def cenario_menus(c, log):
    """
    O menu flutuante é o gesto mais caro da interface — é onde rodam os
    `FloatMenuOptionProvider_*`, e foi num tick destes que uma partida contou
    229 mil consultas de alcançabilidade de um lado e zero do outro.
    """
    pawns = alistados(c) or c.pawns()[:3]
    if not pawns:
        log("nenhum pawn — nada a apontar")
        return

    c.cmd("selecionar " + " ".join(str(p["id"]) for p in pawns))

    for volta in range(20):
        x, z = random.randint(10, 240), random.randint(10, 240)
        try:
            c.cmd(f"menu {x},{z}")
        except RuntimeError as e:
            log(f"menu {x},{z}: {e}")
        c.cmd(f"olhar {x},{z}")
        if volta % 5 == 0:
            log(f"{volta + 1} menus abertos")
        time.sleep(0.4)


CENARIOS = {
    "ir-aqui": cenario_ir_aqui,
    "menus": cenario_menus,
}


# ----------------------------------------------------------------------

def jogos_vivos():
    return subprocess.run(["pgrep", "-x", "RimWorldLinux"],
                          stdout=subprocess.DEVNULL).returncode == 0


def esperar_visita(porta, prazo, log):
    """
    Espera a porta responder e a sessão chegar em Simulando.

    Devolve "ok", "morreu" ou "prazo". Distinguir os três importa: o jogo
    morrer na subida é comum o bastante para merecer nova tentativa — o GC do
    mono estoura de vez em quando no primeiro arranque depois de um build — e
    esperar cinco minutos por um processo que já morreu é desperdício puro.
    """
    limite = time.time() + prazo
    avisou = False
    sem_jogo = 0

    while time.time() < limite:
        try:
            with Controle(porta, tempo=5.0) as c:
                estado = c.estado()
                if estado.get("sessao") == "Simulando" and int(estado.get("passo", 0)) > 0:
                    log(f"visita de pé no passo {estado['passo']}")
                    return "ok"
                if not avisou:
                    log(f"conectado; esperando a visita (sessão={estado.get('sessao')})")
                    avisou = True
                sem_jogo = 0
        except (OSError, RuntimeError):
            # Sem porta ainda é normal durante a carga; sem processo nenhum,
            # não. Três leituras seguidas para não confundir com a troca de
            # partida, em que o anfitrião reabre.
            sem_jogo = sem_jogo + 1 if not jogos_vivos() else 0
            if sem_jogo >= 3:
                return "morreu"

        time.sleep(2)

    return "prazo"


def main():
    ap = argparse.ArgumentParser(description="visita dirigida pela porta de controle")
    ap.add_argument("cenario", nargs="?", help=f"um de: {', '.join(CENARIOS)}")
    ap.add_argument("--lista", action="store_true")
    ap.add_argument("--anfitriao", default="presetfull")
    ap.add_argument("--arbitro", default="presetfull")
    ap.add_argument("--segundos", type=int, default=180)
    ap.add_argument("--porta", type=int, default=25600)
    ap.add_argument("--semente", type=int, default=None,
                    help="semente do sorteio dos gestos, para repetir a mesma corrida")
    args = ap.parse_args()

    if args.lista or not args.cenario:
        for nome, fn in CENARIOS.items():
            print(f"  {nome:10} {(fn.__doc__ or '').strip().splitlines()[0]}")
        return 0

    if args.cenario not in CENARIOS:
        print(f"cenário desconhecido: {args.cenario} (use --lista)", file=sys.stderr)
        return 1

    # Semente registrada sempre: um gesto sorteado que achou bug precisa poder
    # ser refeito igual.
    semente = args.semente if args.semente is not None else random.randrange(1 << 30)
    random.seed(semente)

    def log(m):
        print(f"[dirigir] {m}", flush=True)

    log(f"semente {semente} — repita com --semente {semente}")

    wf = os.path.join(AQUI, "wf")

    def lancar():
        subprocess.Popen(
            [wf, "emular", args.anfitriao, args.arbitro, str(args.segundos),
             "--controle", str(args.porta), "--caminho"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)

    for tentativa in (1, 2):
        lancar()
        log(f"emulação lançada (tentativa {tentativa}); esperando a visita")

        resultado = esperar_visita(args.porta, prazo=300, log=log)
        if resultado == "ok":
            break

        if resultado == "morreu" and tentativa == 1:
            log("o jogo morreu na subida — tentando de novo")
            time.sleep(3)
            continue

        log(f"a visita não ficou de pé ({resultado}) — veja os Player.log")
        return 1

    try:
        with Controle(args.porta) as c:
            CENARIOS[args.cenario](c, log)
            log("gestos terminados; deixando a visita correr até o prazo")
    except (OSError, RuntimeError) as e:
        log(f"a porta caiu no meio dos gestos: {e}")

    # A emulação encerra sozinha no prazo dela — mas esperar o processo SUMIR é
    # esperar demais: o RimWorld headless às vezes fica preso no
    # `Root.Shutdown()` depois de já ter despejado tudo. Medido: os dois lados
    # gravaram o diário e o anfitrião ficou de pé indefinidamente.
    #
    # Então há prazo, e no fim o que sobrou é morto. Os dados já estão no disco.
    limite = time.time() + args.segundos + 180
    while jogos_vivos() and time.time() < limite:
        time.sleep(5)

    if jogos_vivos():
        log("alguém ficou preso no desligamento — encerrando à força")
        subprocess.run(["pkill", "-9", "-x", "RimWorldLinux"], stdout=subprocess.DEVNULL)
        time.sleep(2)

    log("corrida terminada; comparando")
    return subprocess.run([sys.executable, os.path.join(AQUI, "comparar-diarios.py")]).returncode


if __name__ == "__main__":
    sys.exit(main())
