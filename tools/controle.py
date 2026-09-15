#!/usr/bin/env python3
"""
controle — dirige um RimWorld com a porta de controle aberta.

O jogo precisa ter subido com `-controle=PORTA`. Cada comando entra pelo mesmo
caminho de um clique (drafter.Drafted, TryTakeOrderedJob, TipoDeComando.Incidente),
então o que se testa por aqui é o jogo, não um atalho para dentro dele.

Uso
---
    tools/controle.py                       # sessão interativa
    tools/controle.py estado                # um comando e sai
    tools/controle.py --porta 25601 pawns   # outra instância
    echo 'alistar 4231 1
    ir 4231 45,67' | tools/controle.py      # roteiro pela entrada padrão

Como roteiro, em Python
-----------------------
    from controle import Controle
    with Controle() as c:
        for p in c.pawns(alistados=False):
            c.cmd(f"alistar {p['id']} 1")
        c.cmd("incidente RaidEnemy 500")
"""

import argparse
import socket
import sys


class Controle:
    def __init__(self, porta=25600, host="127.0.0.1", tempo=70.0):
        self.ende = (host, porta)
        self.tempo = tempo
        self.sock = None
        self.arq = None

    def __enter__(self):
        self.sock = socket.create_connection(self.ende, timeout=self.tempo)
        self.sock.settimeout(self.tempo)
        self.arq = self.sock.makefile("rw", encoding="utf8", newline="\n")
        self.arq.readline()          # saudação
        return self

    def __exit__(self, *_):
        try:
            self.arq and self.arq.close()
            self.sock and self.sock.close()
        except OSError:
            pass

    def cmd(self, linha):
        """Manda um comando e devolve a resposta, sem o 'ok '/'erro '."""
        self.arq.write(linha + "\n")
        self.arq.flush()
        resposta = self.arq.readline().rstrip("\n")
        if resposta.startswith("erro "):
            raise RuntimeError(resposta[5:])
        return resposta[3:] if resposta.startswith("ok ") else resposta

    def estado(self):
        """O estado como dicionário: tick, velocidade, pawns, passo…"""
        fora = {}
        for par in self.cmd("estado").split():
            if "=" in par:
                k, v = par.split("=", 1)
                fora[k] = v
        return fora

    def pawns(self, filtro="", alistados=None):
        """Lista de pawns. `alistados=True/False` filtra pelo estado."""
        bruto = self.cmd(f"pawns {filtro}".strip())
        fora = []
        for item in bruto.split():
            campos = item.split(":")
            if len(campos) < 5:
                continue
            x, z = campos[2].split(",")
            p = {"id": int(campos[0]), "nome": campos[1],
                 "x": int(x), "z": int(z),
                 "alistado": campos[3] == "alistado", "job": campos[4]}
            if alistados is None or p["alistado"] == alistados:
                fora.append(p)
        return fora


def main():
    ap = argparse.ArgumentParser(description="dirige um RimWorld pela porta de controle")
    ap.add_argument("--porta", type=int, default=25600)
    ap.add_argument("comando", nargs="*", help="comando único; sem ele, lê da entrada")
    args = ap.parse_args()

    try:
        with Controle(args.porta) as c:
            if args.comando:
                # Erro do jogo é resposta, não defeito do cliente: sai com 1 e
                # uma linha, sem traceback.
                try:
                    print(c.cmd(" ".join(args.comando)))
                    return 0
                except RuntimeError as e:
                    print(f"erro: {e}", file=sys.stderr)
                    return 1

            # Sem tty é roteiro pela entrada padrão; com tty é conversa.
            interativo = sys.stdin.isatty()
            if interativo:
                print(f"controle em 127.0.0.1:{args.porta} — 'ajuda' lista, ctrl-d sai")

            for linha in sys.stdin:
                linha = linha.strip()
                if not linha or linha.startswith("#"):
                    continue
                try:
                    print(c.cmd(linha))
                except RuntimeError as e:
                    print(f"erro: {e}", file=sys.stderr)
                    if not interativo:
                        return 1
            return 0

    except (ConnectionRefusedError, OSError) as e:
        print(f"não consegui falar com o jogo em 127.0.0.1:{args.porta}: {e}", file=sys.stderr)
        print("o jogo subiu com -controle=PORTA?", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
