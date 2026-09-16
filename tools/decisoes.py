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
import subprocess
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
MP = os.path.join(AQUI, "..", "referencia", "repos", "Multiplayer",
                  "Source", "Client", "Syncing", "Game")
CLIENTE = os.path.join(AQUI, "..", "client")
RAIZ = os.path.dirname(AQUI)

# O que já vira comando aqui, LIDO DO CÓDIGO.
#
# A primeira versão desta lista era escrita à mão, e envelheceu em silêncio:
# cinco comandos de pawn foram implementados e o mapa continuou dizendo que
# faltavam. Lista mantida à mão é exatamente o que o resto do projeto evita —
# "o relatório é gerado, não mantido".
#
# A fonte é o catálogo de remendos, que o mod verifica na subida: se um alvo
# sumir numa atualização do jogo, ele avisa. E o filtro é o próprio texto do
# catálogo, porque lá cada entrada diz para que serve.
CATALOGO = os.path.join(AQUI, "..", "client", "Patches", "CatalogoDePatches.cs")

# O que o catálogo não cobre: intercepções que não são remendo de membro único
# (designadores são descobertos por varredura, o filtro de estoque é estado).
NOSSOS_A_MAO = {
    "Designator.DesignateSingleCell":    "Designar (descoberto por varredura)",
    "Designator.DesignateMultiCell":     "DesignarVarias (idem)",
    "Designator.DesignateThing":         "Designar (idem)",
    "StorageSettings.Priority":          "Estoque (estado, não membro)",
    "ThingFilter":                       "Estoque (idem)",
    "TickManager.CurTimeSpeed":          "Velocidade",
    "CompForbiddable.Forbidden":         "Alternar",
    "Pawn_DraftController.FireAtWill":   "Alternar",
    "Pawn_JobTracker.TryTakeOrderedJobPrioritizedWork": "OrdemPriorizada",
    "PawnColumnWorker_FollowDrafted.SetValue":   "AjusteDePawn (remendo no chamador)",
    "PawnColumnWorker_FollowFieldwork.SetValue": "AjusteDePawn (idem)",
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


def nossos():
    """(tipo.membro → recurso) do catálogo, só o que é comando de sessão."""
    fora = dict(NOSSOS_A_MAO)
    try:
        texto = open(CATALOGO, encoding="utf8", errors="replace").read()
    except OSError:
        return fora

    # Cada entrada é um bloco `new AlvoDePatch { Tipo = …, Membro = …, Recurso = … }`
    padrao = (
        r'Tipo\s*=\s*typeof\(([\w\.]+)\)\s*,\s*Membro\s*=\s*'
        r'(?:nameof\(\s*[\w\.]*?(\w+)\s*\)|"(\w+)")\s*,\s*Recurso\s*=\s*"([^"]*)"'
    )

    for bloco in re.finditer(padrao, texto):
        tipo = bloco.group(1).split(".")[-1]
        membro = bloco.group(2) or bloco.group(3)
        recurso = bloco.group(4)

        # Só o que é ordem do jogador. "RNG isolado", "barreira de tick" e
        # companhia são determinismo, não decisão — entram na outra conta.
        if re.search(r"dentro de sessão|vira comando", recurso):
            fora[f"{tipo}.{membro}"] = recurso

    return fora


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


JOGO = os.environ.get(
    "WF_JOGO", os.path.expanduser("~/.local/share/Steam/steamapps/common/RimWorld"))
ASSEMBLY = os.path.join(JOGO, "RimWorldLinux_Data", "Managed", "Assembly-CSharp.dll")
AUDITOR = os.path.join(RAIZ, "tools", "Auditor", "bin", "Debug", "WithFriends.Auditor.dll")


def formas(alvos):
    """
    A forma de cada membro no jogo: bool, valor, closure ou método.

    É o que separa "herdável de graça" de "trabalho de verdade". Um `bool` cabe
    no registro de `Alternar` com uma linha; um valor simples cabe no de
    `AjusteDePawn` ou no de `AjustesDeZona`; um método precisa de intercepção
    própria — e se ele DEVOLVE botões, a decisão não está nele, está na lambda
    que o botão executa. É o caso caro, e a razão de o Multiplayer ter 227
    registros de lambda.

    **De onde vem a resposta.** Do metadado do assembly, pelo `--formas` do
    auditor. A primeira versão lia um assembly DECOMPILADO com expressão
    regular: dependia do `ilspycmd` instalado, de 200 MB de C# gerado e de a
    formatação do decompilador não mudar — e ainda assim confundia um método com
    outro de mesmo nome em classe vizinha. O metadado responde a mesma pergunta
    sem nada disso.
    """
    if not os.path.exists(ASSEMBLY):
        return None

    if not os.path.exists(AUDITOR):
        r = subprocess.run(["dotnet", "build", os.path.join(RAIZ, "tools/Auditor/Auditor.csproj"),
                            "-v", "q", "--nologo"], capture_output=True)
        if r.returncode != 0 or not os.path.exists(AUDITOR):
            return None

    saida = subprocess.run(["dotnet", AUDITOR, ASSEMBLY, "--formas"],
                           capture_output=True, text=True)
    if saida.returncode != 0:
        return None

    # Um membro pode aparecer mais de uma vez (sobrecargas, herança achatada). A
    # forma mais informativa ganha: quem tem um `bool` por trás é chave, mesmo
    # que exista um método de mesmo nome.
    # A forma mais remendável ganha: propriedade tem setter, campo não tem onde
    # remendar, e um método de mesmo nome não muda isso.
    peso = {"bool": 4, "valor": 3, "campo": 2, "closure": 1, "método": 0}
    tabela = {}
    for linha in saida.stdout.splitlines():
        nome, _, forma = linha.partition("\t")
        if forma and peso.get(forma, -1) > peso.get(tabela.get(nome, ""), -1):
            tabela[nome] = forma

    return {alvo: tabela.get(alvo, "?") for alvo in alvos}


def fora_de_escopo(alvo):
    for padrao, motivo in FORA_DE_ESCOPO:
        if re.search(padrao, alvo, re.I):
            return motivo
    return None


def main():
    todos = "--todos" in sys.argv
    so_nossos = "--nossos" in sys.argv
    por_forma = "--forma" in sys.argv

    NOSSOS = nossos()
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

    if por_forma:
        f = formas(faltando)
        if f is None:
            print(f"\n  (--forma precisa do assembly decompilado em {DECOMPILADO})")
        else:
            contagem = {}
            for alvo in faltando:
                contagem.setdefault(f[alvo], []).append(alvo)

            legenda = {
                "bool":    "propriedade bool: uma linha no registro de Alternar",
                "valor":   "propriedade: uma linha no registro de AjusteDePawn ou AjustesDeZona",
                "campo":   "campo público: não há setter para remendar — a decisão está na lambda do botão",
                "closure": "botão cuja ação é lambda — o caso caro (MP: 227 registros)",
                "método":  "intercepção própria, uma a uma",
                "?":       "tipo não encontrado no assembly",
            }
            print("\n== por forma do membro — o que é herdável de graça\n")
            for chave in ("bool", "valor", "campo", "método", "closure", "?"):
                itens = contagem.get(chave, [])
                if itens:
                    print(f"  {chave:<9} {len(itens):>3}   {legenda[chave]}")
            if todos:
                for chave in ("bool", "valor", "campo"):
                    print(f"\n  -- {chave}")
                    for alvo in contagem.get(chave, []):
                        print(f"     {alvo}")

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
