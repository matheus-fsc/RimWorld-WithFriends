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
import signal
import subprocess
import sys
import time

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.dirname(AQUI)
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

    O custo do menu é multiplicativo: pawns selecionados × coisas na célula ×
    provedores de opção. A primeira versão deste cenário selecionava três pawns
    e apontava para célula vazia — deu 57 consultas, mil vezes menos que a
    partida real, e passar num teste mil vezes mais leve não diz nada.

    Então: seleção grande, e mira em célula POVOADA, que é onde o menu tem o que
    oferecer.
    """
    todos = c.pawns()
    if not todos:
        log("nenhum pawn — nada a apontar")
        return

    # Seleção grande: o menu roda os provedores para cada pawn selecionado.
    selecao = [p for p in todos if p["colono"]] or todos
    selecao = selecao[:20]
    c.cmd("selecionar " + " ".join(str(p["id"]) for p in selecao))
    log(f"{len(selecao)} pawn(s) selecionados")

    for volta in range(24):
        # Metade das voltas mira em cima de outro pawn (célula povoada, menu
        # cheio); metade em célula qualquer, para variar o caminho.
        if volta % 2 == 0 and len(todos) > 1:
            alvo = random.choice(todos)
            x, z = alvo["x"], alvo["z"]
        else:
            x, z = random.randint(10, 240), random.randint(10, 240)

        try:
            opcoes = c.cmd(f"menu {x},{z}")
        except RuntimeError as e:
            log(f"menu {x},{z}: {e}")
            continue

        c.cmd(f"olhar {x},{z}")

        # O arrasto é o que custa caro: o jogo refaz os destinos a cada célula
        # que o mouse atravessa. Sem ele o cenário mede 87 consultas de
        # alcançabilidade onde a partida real mediu 229 mil.
        if volta % 3 == 0:
            origem = random.choice(selecao)
            try:
                c.cmd(f"arrastar {origem['x']},{origem['z']} {x},{z} 30")
            except RuntimeError as e:
                log(f"arrasto: {e}")

        if volta % 6 == 0:
            log(f"volta {volta + 1}: menu em {x},{z} → {opcoes.split(':')[0]}")
        time.sleep(0.3)


def cenario_ajustes(c, log):
    """
    Prioridade de trabalho, área e mestre — a família `pawn` do mapa de decisões.

    São decisão de simulação, não enfeite: prioridade muda o que o colono faz no
    tick seguinte, que é exatamente a divergência que custou o dia 14 (um lado
    escolhendo Clean, o outro BuildRoof).
    """
    # Colono de verdade, não "quem não está alistado": aquilo pega animal, e
    # animal não tem prioridade de trabalho. A primeira corrida deste cenário
    # exercitou só `area` por causa disso.
    colonos = [p for p in c.pawns() if p["colono"]][:4]
    if not colonos:
        log("nenhum colono livre — nada a ajustar")
        return

    trabalhos = ["Cleaning", "Hauling", "Construction", "Growing", "Cooking"]

    for volta in range(8):
        for p in colonos:
            trabalho = random.choice(trabalhos)
            prioridade = random.randint(1, 4)
            try:
                c.cmd(f"ajuste {p['id']} prioridade {prioridade} {trabalho}")
            except RuntimeError as e:
                log(f"{p['nome']} {trabalho}: {e}")

        # Área: alternar entre "sem restrição" e o que existir no mapa.
        for p in colonos[:2]:
            try:
                c.cmd(f"ajuste {p['id']} area -1")
            except RuntimeError:
                pass

        # Renomear: o gesto que exercita `AjustesDeCoisa` num save qualquer —
        # combustível quer um gerador e o tanque quer Biotech, mas pawn tem em
        # toda colônia. E o nome importa em visita: é como o OUTRO jogador
        # reconhece o colono de quem se está falando.
        for p in colonos[:2]:
            try:
                c.cmd(f"renomear {p['id']} apelido{volta}")
            except RuntimeError as e:
                log(f"renomear {p['id']}: {e}")

        log(f"volta {volta + 1}: {len(colonos)} colono(s) reconfigurados")
        time.sleep(1.5)


def ids_de_area(c):
    """Os ids de área que o mapa tem agora, pela listagem da porta de controle."""
    listagem = c.cmd("zona listar")
    return {int(p.split()[1]) for p in listagem.split(" | ")
            if p.strip().startswith("area ")}


def esperar_area_nova(c, antes, log, tentativas=25):
    """
    Espera uma área que ainda não existia aparecer.

    **Por que esperar.** Dentro de visita, criar área não cria nada na hora: vira
    comando, atravessa o coordenador, e só nasce quando o passo dela é aplicado
    nos DOIS lados. Quem clica não recebe id de volta porque não há id ainda.

    A primeira versão deste cenário supunha criação síncrona, leu o `false` do
    nosso próprio remendo como "o jogo recusou" e desistiu seis vezes seguidas —
    uma corrida inteira sem exercitar uma linha do que tinha acabado de ser
    escrito.
    """
    for _ in range(tentativas):
        time.sleep(0.4)
        novos = ids_de_area(c) - antes
        if novos:
            return min(novos)
    log("a área não apareceu na listagem — o comando não voltou")
    return None


def cenario_zonas(c, log):
    """
    A família zona/área inteira, do lado do anfitrião e de mais ninguém.

    Cada uma destas reescreve o plano de trabalho do mapa: apagar um plantio faz
    o trabalho sumir da lista e todo colono livre escolher outra coisa no mesmo
    tick; inverter uma área muda, num gesto, para onde todo mundo pode ir. É por
    isso que a assimetria aqui vale como teste — se qualquer uma escapar do
    caminho de comando, os dois lados decidem diferente no tick seguinte.

    A ordem é de propósito: cria antes de apagar, para que apagar tenha o que
    apagar mesmo num mapa sem áreas desenhadas à mão. E cada gesto confere o
    efeito na listagem em vez de supor que aconteceu — dentro de visita nada
    acontece na hora.
    """
    antes = ids_de_area(c)
    log(f"antes: {len(antes)} área(s) — {sorted(antes)}")

    feitos = {"criar": 0, "renomear": 0, "inverter": 0, "apagar": 0}

    for volta in range(4):
        nome = f"teste-{volta}"
        log(f"volta {volta + 1}: {c.cmd(f'zona novaarea {nome}')}")

        novo = esperar_area_nova(c, antes, log)
        if novo is None:
            continue
        feitos["criar"] += 1
        log(f"volta {volta + 1}: área {novo} nasceu dos dois lados")

        c.cmd(f"zona renomear {novo} {nome}-renomeada")
        feitos["renomear"] += 1
        time.sleep(0.6)

        c.cmd(f"zona inverter {novo}")
        feitos["inverter"] += 1
        time.sleep(0.6)

        c.cmd(f"zona apagararea {novo}")
        time.sleep(0.4)

        # Apagar também é comando: confere que ela SUMIU, em vez de supor.
        for _ in range(25):
            time.sleep(0.4)
            if novo not in ids_de_area(c):
                feitos["apagar"] += 1
                break
        else:
            log(f"a área {novo} não sumiu — o comando de apagar não voltou")

        antes = ids_de_area(c)

    # A zona de verdade do mapa, por último: é o gesto de maior alcance, e os
    # ticks seguintes correm depois dele.
    listagem = c.cmd("zona listar")
    zonas = [p.split()[1] for p in listagem.split(" | ") if p.strip().startswith("zona ")]
    if zonas:
        log(f"apagando a zona {zonas[0]}: {c.cmd(f'zona apagar {zonas[0]}')}")
    else:
        log("nenhuma zona no mapa para apagar (o save só tem áreas)")

    log("gestos que completaram o caminho de comando: " +
        ", ".join(f"{k} {v}" for k, v in feitos.items()))


def cenario_deriva(c, log):
    """
    Divergência GRANDE de um lado só, e depois deixa correr — a medição da ADR 0022.

    Injeta um assalto no árbitro e em mais ninguém, pela única porta que fura o
    caminho de comando de propósito. Com `--semdigital`, a divergência não é
    detectada nem desfeita, e os dois lados correm livres sob a mesma barreira:
    passos casados, estado se afastando. É a condição da medida.

    O que se quer saber: quanto os pawns COMUNS aos dois lados se afastam com o
    tempo. A deriva já medida partiu de uma divergência pequena (uma escolha de
    job) e deu cinco pawns em 151 depois de dois minutos. A partir de um assalto
    inteiro, ninguém sabe — e é esse número que decide se corrigir posição a
    1 Hz basta.
    """
    porta_arbitro = int(os.environ.get("WF_PORTA_ARBITRO", "0"))
    if not porta_arbitro:
        log("sem porta do árbitro — nada a injetar")
        return

    estado = c.estado()
    log(f"antes: passo {estado.get('passo')}, {estado.get('pawns')} pawns no anfitrião")

    try:
        with Controle(porta_arbitro) as arb:
            log(f"árbitro: {arb.estado().get('pawns')} pawns")
            log("injetando: " + arb.cmd("divergir raid 2000"))
    except (OSError, RuntimeError) as e:
        log(f"não consegui injetar no árbitro: {e}")
        return

    # **Despejar de propósito, porque nada mais vai despejar.**
    #
    # O rastreio de pawns guarda os últimos ticks em memória e só escreve no
    # diário quando a digital acusa divergência. Com `--semdigital` a digital
    # está calada — é essa a condição da medida —, então, sem pedir, os dois
    # lados correm a visita inteira e não gravam uma linha de posição. A
    # primeira versão deste cenário fez exatamente isso: injetou, esperou, e não
    # deixou nada para medir.
    #
    # Então o despejo vira relógio: a cada 20s os dois lados gravam a janela que
    # têm. Pedir aos DOIS no mesmo instante é o que faz as janelas se
    # sobreporem nos mesmos passos, que é o que a comparação precisa.
    log("divergência injetada — despejando dos dois lados a cada 20s")

    segundos = int(os.environ.get("WF_SEGUNDOS", "300"))
    fim = time.time() + max(0, segundos - 30)
    rodada = 0
    while time.time() < fim:
        time.sleep(20)
        rodada += 1
        try:
            with Controle(porta_arbitro) as arb:
                c.cmd("despejar")
                arb.cmd("despejar")
                passo_a = c.estado().get("passo")
                passo_b = arb.estado().get("passo")
            log(f"despejo {rodada}: anfitrião no passo {passo_a}, árbitro no {passo_b}")
        except (OSError, RuntimeError) as e:
            log(f"despejo {rodada} falhou: {e}")
            return


CENARIOS = {
    "ir-aqui": cenario_ir_aqui,
    "menus": cenario_menus,
    "ajustes": cenario_ajustes,
    "deriva": cenario_deriva,
    "zonas": cenario_zonas,
}


# ----------------------------------------------------------------------

LOG_ANFITRIAO = os.path.expanduser(
    "~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Player.log")


LOG_ARBITRO = os.path.expanduser("~/.rimworld-arbitro/Player.log")
SAIDA_LANCADOR = os.path.join(os.environ.get("TMPDIR", "/tmp"), "wf-lancador.log")


def jogos_vivos():
    return subprocess.run(["pgrep", "-x", "RimWorldLinux"],
                          stdout=subprocess.DEVNULL).returncode == 0


def guerra_de_identidade():
    """
    Há duas emulações disputando a mesma identidade?

    Sintoma, do lado do jogo: "Outra conexão entrou como <o meu próprio id>",
    a cada 15s, para sempre. Quem entra com o id de alguém desloca o antigo; se
    os dois lados são o MESMO jogo rodando duas vezes, cada um derruba o outro e
    a visita nunca sai do passo 0.

    Medido na corrida da ADR 0022: três tentativas inteiras (15 minutos) presas
    nisso. Reconhecer custa uma leitura de log e economiza o prazo inteiro.
    """
    for caminho in (LOG_ANFITRIAO, LOG_ARBITRO):
        try:
            with open(caminho, errors="replace") as f:
                if "IdentidadeAssumidaPorOutraConexao" in f.read()[-20000:]:
                    return True
        except OSError:
            pass
    return False


def limpar(processo=None):
    """
    Deixa a máquina sem nada de emulação de pé — e CONFIRMA.

    `pkill` no jogo não basta: quem lança é um script (`wf emular`) que ainda
    pode estar no `dotnet build` quando a morte chega, e sobe o jogo depois
    dela. Era essa a origem da guerra de identidade. Então morre o grupo de
    processos do script primeiro, o jogo depois, e só então se confere.
    """
    if processo is not None:
        try:
            os.killpg(os.getpgid(processo.pid), signal.SIGKILL)
        except (ProcessLookupError, PermissionError, OSError):
            pass

    # Limpo duas vezes seguidas, com folga entre elas: um jogo lançado há um
    # instante ainda não está na tabela de processos, e "limpo" cedo demais é
    # como nasce a segunda emulação.
    limpo = 0
    for _ in range(30):
        # Colchete no padrão: assim ele não casa com o shell que o executa
        # nem com este próprio processo.
        subprocess.run(["pkill", "-9", "-f", "tools/wf emula[r]"], stdout=subprocess.DEVNULL)
        subprocess.run(["pkill", "-9", "-x", "RimWorldLinux"], stdout=subprocess.DEVNULL)
        time.sleep(2)
        limpo = limpo + 1 if not jogos_vivos() and not emulador_vivo() else 0
        if limpo >= 2:
            return True
    return False


def ultima_linha_do_lancador():
    try:
        with open(SAIDA_LANCADOR, errors="replace") as f:
            linhas = [l.strip() for l in f if l.strip()]
        return linhas[-1] if linhas else ""
    except OSError:
        return ""


def emulador_vivo():
    return subprocess.run(["pgrep", "-f", "tools/wf emula[r]"],
                          stdout=subprocess.DEVNULL).returncode == 0


def desistiu():
    """
    O anfitrião disse que desistiu?

    Contar processo não basta: o RimWorld headless às vezes fica preso no
    `Root.Shutdown()` depois de já ter encerrado tudo, e aí "morreu" nunca
    dispara e a espera vai até o prazo. Medido: o anfitrião desistiu às
    22:42:32 e o orquestrador ficou esperando mais três minutos.

    O marcador explícito é mais confiável que a ausência do processo.
    """
    try:
        with open(LOG_ANFITRIAO, errors="replace") as f:
            return "sem árbitro não há visita" in f.read()[-20000:]
    except OSError:
        return False


def lado_vivo(porta):
    """Aquela instância responde e já carregou o mod?"""
    try:
        with Controle(porta, tempo=4.0) as c:
            c.cmd("estado")
            return True
    except (OSError, RuntimeError):
        return False


def gestos_de_aquecimento(porta, log, feitos=[0]):
    """
    Mexer na interface do anfitrião ENQUANTO ele joga sozinho.

    **Por que isto faltava.** O aquecimento sozinho não reproduziu nenhuma das
    duas causas testadas, e o motivo é a bancada ser cega: com
    `-batchmode -nographics` o anfitrião nunca abre uma tela. Mas a família que
    perseguimos não é "estado de processo" e sim, quase toda ela, **estado de
    processo criado pela interface** — o cache de stat que a aba de saúde enche,
    os pensamentos que o painel recalcula, a posição de desenho que o quadro
    interpola.

    Tempo de jogo o aquecimento já dava. O que faltava era uso de interface.
    """
    try:
        with Controle(porta, tempo=4.0) as c:
            pawns = c.pawns()
            if not pawns:
                return
            alvo = random.choice(pawns)
            c.cmd("selecionar " + " ".join(str(p["id"]) for p in pawns[:12]))
            c.cmd(f"olhar {alvo['x']},{alvo['z']}")
            c.cmd(f"menu {alvo['x']},{alvo['z']}")
            feitos[0] += 1
            if feitos[0] % 10 == 1:
                log(f"aquecendo a interface do anfitrião ({feitos[0]} gesto(s))")
    except (OSError, RuntimeError):
        pass   # ainda carregando, ou já entrou na visita


def esperar_visita(porta, prazo, log, processo=None, aquecendo=False):
    """
    Espera a porta responder e a sessão chegar em Simulando.

    Devolve "ok", "morreu", "duplicada" ou "prazo". Distinguir importa: o jogo
    morrer na subida é comum o bastante para merecer nova tentativa — o GC do
    mono estoura de vez em quando no primeiro arranque depois de um build — e
    esperar cinco minutos por um processo que já morreu é desperdício puro.

    O caso mais rápido de todos é o mais comum: o `RimWorldLinux` estoura com
    SIGSEGV em segundos, o script lançador termina, e não há jogo nenhum. Aí não
    há log para ler nem porta para bater — quem responde é a tabela de
    processos. Reconhecer isso em 3s em vez de 300 é a diferença entre cinco
    tentativas em um minuto e cinco tentativas em vinte e cinco.
    """
    limite = time.time() + prazo
    avisou = False
    sem_jogo = 0

    morto_seguido = 0

    while time.time() < limite:
        # **O lançador já terminou e não há jogo — mas não na primeira leitura.**
        #
        # `wf emular` sai assim que solta o `nohup`, e o processo do Unity leva
        # um instante para aparecer na tabela. Concluir "morreu" nessa fresta
        # deixa vivo o jogo que estava nascendo: a tentativa seguinte limpa uma
        # máquina que ainda parece limpa, sobe outro anfitrião, e os dois acabam
        # brigando pela mesma identidade. Foi assim que doze tentativas seguidas
        # se declararam mortas enquanto deixavam doze jogos de pé.
        if processo is not None and processo.poll() is not None and not jogos_vivos():
            morto_seguido += 1
            if morto_seguido >= 5:
                log(f"lançador saiu com {processo.returncode}; última linha: "
                    f"{ultima_linha_do_lancador()!r}")
                return "sumiu"
        else:
            morto_seguido = 0

        try:
            with Controle(porta, tempo=5.0) as c:
                estado = c.estado()
                if estado.get("sessao") == "Simulando" and int(estado.get("passo", 0)) > 0:
                    # **Os DOIS lados, não só este.**
                    #
                    # O arranque do jogo falha sozinho neste ambiente — medido
                    # em 12% a 40%, antes de qualquer mod carregar, logo depois
                    # de a inicialização da Steam falhar. Não é defeito nosso e
                    # não dá para consertar daqui; dá para não começar em cima
                    # dele.
                    #
                    # Começar o cenário com um lado morto gasta a corrida
                    # inteira para descobrir no fim que não havia com quem
                    # comparar.
                    if not lado_vivo(porta + 1):
                        log("o outro lado não responde — ainda não estável")
                        time.sleep(2)
                        continue

                    log(f"os dois lados de pé; visita no passo {estado['passo']}")
                    return "ok"
                if desistiu():
                    return "desistiu"
                if guerra_de_identidade():
                    return "duplicada"

                # Enquanto o anfitrião joga sozinho, mexer na interface dele: é
                # isso que enche os caches que só um lado tem.
                if aquecendo and estado.get("sessao") == "Fora":
                    gestos_de_aquecimento(porta, log)

                if not avisou:
                    log(f"conectado; esperando a visita (sessão={estado.get('sessao')})")
                    avisou = True
                sem_jogo = 0
        except (OSError, RuntimeError):
            # Sem porta ainda é normal durante a carga; sem processo nenhum,
            # não. Três leituras seguidas para não confundir com a troca de
            # partida, em que o anfitrião reabre.
            if desistiu():
                return "desistiu"
            if guerra_de_identidade():
                return "duplicada"

            # **Enquanto o lançador corre, ausência de jogo não é morte.**
            #
            # `wf emular` fecha o que estiver aberto, sobe o coordenador e semeia
            # a pasta do árbitro antes de lançar qualquer coisa — uns dez
            # segundos em que não existe processo de jogo e é certo que não
            # exista. Contar isso como morte abortava a tentativa aos seis
            # segundos e matava, na limpeza seguinte, o lançador que ainda estava
            # trabalhando. Doze tentativas assim, e nenhuma chegou a lançar o
            # jogo.
            if processo is not None and processo.poll() is None:
                sem_jogo = 0
            else:
                sem_jogo = sem_jogo + 1 if not jogos_vivos() else 0
            if sem_jogo >= 3:
                return "sumiu"

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
    ap.add_argument("--aquecimento", type=int, default=0,
                    help="ticks que o anfitrião joga sozinho antes de convidar (ADR 0020)")
    ap.add_argument("--visivel", action="store_true",
                    help="abre os dois jogos com janela, lado a lado, para acompanhar")
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
    os.environ["WF_PORTA_ARBITRO"] = str(args.porta + 1)
    os.environ["WF_SEGUNDOS"] = str(args.segundos)

    if args.cenario == "deriva":
        log("modo medição: digital calada, divergência injetada de um lado só")

    # **Com janela, para conferir com os olhos.**
    #
    # A emulação cega é mais rápida e é o padrão, mas ela só devolve números. Há
    # perguntas que só a tela responde — "o assalto chegou dos dois lados?", "os
    # colonos estão andando ou parados?" — e há defeito que se reconhece em três
    # segundos de vídeo e custa uma hora de diário. As duas janelas ficam lado a
    # lado: anfitrião à esquerda, árbitro à direita.
    if args.visivel:
        log("modo visível: duas janelas lado a lado (anfitrião à esquerda)")

    # **A assimetria que a bancada não tinha.**
    #
    # Três divergências seguidas de partidas de verdade tinham a mesma causa —
    # estado de processo do anfitrião, que não vai para o save — e nenhuma delas
    # a emulação reproduzia, porque aqui os dois lados carregam do zero e entram
    # na visita em segundos. Com aquecimento o anfitrião joga sozinho primeiro,
    # como uma pessoa faz, e a bancada passa a medir o jogo que se joga.
    if args.aquecimento:
        log(f"aquecimento: o anfitrião joga {args.aquecimento} tick(s) sozinho antes de convidar")

    wf = os.path.join(AQUI, "wf")

    # **Compilar uma vez, fora das tentativas.**
    #
    # `wf emular` compila antes de subir o jogo. Repetir isso a cada tentativa
    # põe um build de ~30s entre a morte da tentativa anterior e o nascimento da
    # próxima — e é nessa janela que nasce a segunda emulação.
    log("compilando uma vez (as tentativas não recompilam)")
    for projeto in ("client/Client.csproj", "server/Server.csproj"):
        subprocess.run(["dotnet", "build", os.path.join(RAIZ, projeto), "-v", "q", "--nologo"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def lancar():
        return subprocess.Popen(
            [wf, "emular", args.anfitriao, args.arbitro, str(args.segundos),
             "--controle", str(args.porta)]
            + ([] if args.cenario == "deriva" else ["--caminho"])
            + (["--semdigital"] if args.cenario == "deriva" else [])
            + (["--visivel"] if args.visivel else [])
            + (["--aquecimento", str(args.aquecimento)] if args.aquecimento else [])
            + ["--sem-compilar"],
            # A saída do lançador vai para arquivo, não para o vazio: quando a
            # tentativa falha, o motivo costuma estar nas duas linhas que ele
            # imprimiu antes de morrer — e jogá-las fora foi o que fez uma tarde
            # inteira ser gasta perguntando ao log do jogo uma coisa que o script
            # já tinha dito.
            stdout=open(SAIDA_LANCADOR, "w"), stderr=subprocess.STDOUT,
            start_new_session=True)

    # **Tentar até ficar estável, não tentar um número bonito de vezes.**
    #
    # O arranque falha sozinho neste ambiente entre 12% e 40% das vezes (medido:
    # 3 em 8 numa condição, 1 em 8 noutra, 0 em 16 com a máquina fresca), antes
    # de qualquer mod carregar. Com falha independente de ~40% por lado, cinco
    # tentativas deixam a chance de não conseguir nenhuma abaixo de 1%.
    #
    # E cada tentativa começa do zero: mata tudo antes, porque instância presa
    # no desligamento disputa com a nova.
    #
    # Doze e não cinco: agora que o estouro na subida é reconhecido em segundos
    # (nada de porta, nada de processo), uma tentativa falha custa uns poucos
    # segundos em vez dos cinco minutos do prazo. Tentar mais vezes ficou barato.
    TENTATIVAS = 12
    emulacao = None
    for tentativa in range(1, TENTATIVAS + 1):
        if not limpar(emulacao):
            log("não consegui deixar a máquina limpa — pare os jogos à mão")
            return 1
        # Log zerado: a tentativa anterior deixou nele exatamente as marcas que
        # este laço procura, e ler as de ontem é decidir pelo passado.
        for caminho in (LOG_ANFITRIAO, LOG_ARBITRO):
            try:
                open(caminho, "w").close()
            except OSError:
                pass
        emulacao = lancar()
        log(f"emulação lançada (tentativa {tentativa}); esperando a visita")

        # O prazo cresce com o aquecimento: enquanto ele corre, o anfitrião fica
        # "Fora" de propósito, e o prazo de sempre o mataria no meio.
        prazo = 300 + (args.aquecimento // 4 if args.aquecimento else 0)
        resultado = esperar_visita(args.porta, prazo=prazo, log=log, processo=emulacao,
                                   aquecendo=bool(args.aquecimento))
        if resultado == "ok":
            break

        if resultado != "ok" and tentativa < TENTATIVAS:
            porque = {"duplicada": "duas emulações na mesma identidade",
                      "sumiu": "o processo do jogo sumiu",
                      "desistiu": "o anfitrião desistiu (sem árbitro não há visita)",
                      "prazo": "passou do prazo sem a visita começar"}.get(resultado, resultado)
            log(f"subida instável ({porque}) — tentando de novo ({tentativa + 1}/{TENTATIVAS})")
            continue

        # Desistir sem limpar deixa o último anfitrião vivo — e ele ainda vai
        # lançar o árbitro dele. O próximo que rodar herda os dois como órfãos,
        # com identidade repetida, e paga por um erro que não cometeu.
        log(f"a visita não ficou de pé ({resultado}) — veja os Player.log")
        limpar(emulacao)
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
