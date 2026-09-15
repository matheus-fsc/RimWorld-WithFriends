using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using WithFriends.Client.Session;

namespace WithFriends.Client.Bancada;

/// <summary>
/// Uma porta por onde dirigir o jogo de fora.
///
/// <para><b>O problema que ela resolve.</b> O roteiro de emulação mora em C#:
/// mudar um cenário custa recompilar e reabrir duas instâncias. E ele é curto
/// de propósito, porque cada passo novo é código. Isso põe um teto baixo no que
/// dá para testar sozinho — justamente quando o gargalo do projeto virou
/// "quantas rodadas de teste cabem no dia".</para>
///
/// <para>Com uma porta, o cenário vira texto: um script em Python (ou qualquer
/// coisa que abra socket) manda comandos, lê a resposta, decide o próximo. Não
/// há build no meio.</para>
///
/// <para><b>A regra de desenho que importa.</b> Cada comando entra pelo
/// <b>mesmo caminho de um clique</b> — <c>drafter.Drafted</c>,
/// <c>TryTakeOrderedJob</c>, <c>TipoDeComando.Incidente</c>. Nenhum atalho para
/// dentro da simulação. Se o caminho do comando estiver quebrado, o teste
/// quebra junto, que é exatamente o que se quer de um teste. Foi o mesmo
/// princípio do <see cref="RoteiroDeEmulacao"/>, e é o que faz esta porta medir
/// o jogo em vez de medir a si mesma.</para>
///
/// <para><b>Thread.</b> O socket aceita e lê numa thread de fundo; as APIs do
/// RimWorld não são seguras fora da principal, então as linhas vão para uma
/// fila e quem executa é o quadro, em <see cref="Atender"/>. É a mesma
/// separação que o jogo faz com os jobs de pathfinding — e a razão é a mesma.</para>
///
/// <para><b>Alcance.</b> Só <c>127.0.0.1</c>, e só quando <c>-controle=PORTA</c>
/// é passado. É ferramenta de bancada; não abre nada para a rede.</para>
/// </summary>
public static class PortaDeControle
{
    public static bool Ativa => Porta > 0;

    static int Porta =>
        GenCommandLine.TryGetCommandLineArg("controle", out string p) &&
        int.TryParse(p, out int v) ? v : 0;

    static TcpListener? ouvinte;
    static readonly object trava = new();
    static readonly Queue<(string linha, StreamWriter resposta)> pendentes = new();

    public static void Abrir()
    {
        if (!Ativa || ouvinte != null) return;

        try
        {
            // Loopback explícito: esta porta manda no jogo, e não tem por que
            // estar disponível para mais ninguém.
            ouvinte = new TcpListener(IPAddress.Loopback, Porta);
            ouvinte.Start();

            new Thread(Aceitar) { IsBackground = true, Name = "WithFriends-controle" }.Start();
            Log.Message($"[WithFriends/controle] ouvindo em 127.0.0.1:{Porta}");
        }
        catch (Exception e)
        {
            Log.Warning($"[WithFriends/controle] não foi possível abrir a porta {Porta}: {e.Message}");
            ouvinte = null;
        }
    }

    static void Aceitar()
    {
        while (ouvinte != null)
        {
            try
            {
                var cliente = ouvinte.AcceptTcpClient();
                new Thread(() => Conversar(cliente)) { IsBackground = true }.Start();
            }
            catch (Exception)
            {
                return;   // ouvinte fechado: é o fim normal
            }
        }
    }

    static void Conversar(TcpClient cliente)
    {
        using (cliente)
        using (var fluxo = cliente.GetStream())
        using (var leitor = new StreamReader(fluxo, Encoding.UTF8))
        using (var escritor = new StreamWriter(fluxo, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                escritor.WriteLine("ok controle do With Friends — 'ajuda' lista os comandos");

                string? linha;
                while ((linha = leitor.ReadLine()) != null)
                {
                    if (linha.Trim().Length == 0) continue;

                    lock (trava) pendentes.Enqueue((linha.Trim(), escritor));

                    // O quadro responde. Esperar aqui é de propósito: quem
                    // dirige precisa saber que o comando aconteceu antes de
                    // mandar o próximo, senão o roteiro vira corrida.
                    if (!EsperarResposta(escritor)) return;
                }
            }
            catch (IOException) { /* cliente foi embora: normal */ }
        }
    }

    /// <summary>Quantos comandos deste cliente ainda não foram atendidos.</summary>
    static bool EsperarResposta(StreamWriter escritor)
    {
        for (int i = 0; i < 6000; i++)   // teto de ~60s: comando que trava não trava o cliente
        {
            lock (trava)
                if (!pendentes.Any(p => p.resposta == escritor)) return true;

            Thread.Sleep(10);
        }

        try { escritor.WriteLine("erro tempo esgotado — o jogo não atendeu"); } catch { }
        return false;
    }

    /// <summary>
    /// Chamado a cada quadro — <b>com partida aberta ou sem</b>.
    ///
    /// <para>A primeira versão era bombeada por um <c>GameComponent</c>, que só
    /// existe dentro de uma partida. Numa instância dirigida por gente isso
    /// quebra o uso principal: quem abre o jogo cai no menu, quem está do outro
    /// lado da porta conecta, manda <c>estado</c> — e fica pendurado até o teto
    /// de tempo, porque não há quadro de partida para responder.</para>
    ///
    /// <para>Agora quem bombeia são os dois <c>Update</c> de raiz: o do menu e o
    /// da partida. Responder "fora de partida" é uma resposta.</para>
    /// </summary>
    public static void Atender()
    {
        if (!Ativa) return;

        while (true)
        {
            (string linha, StreamWriter resposta) pedido;
            lock (trava)
            {
                if (pendentes.Count == 0) return;
                pedido = pendentes.Peek();
            }

            string resposta;
            try { resposta = Executar(pedido.linha); }
            catch (Exception e) { resposta = $"erro {e.GetType().Name}: {e.Message}"; }

            try { pedido.resposta.WriteLine(resposta); } catch { }

            lock (trava) pendentes.Dequeue();
        }
    }

    static string Executar(string linha)
    {
        var partes = linha.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string comando = partes[0].ToLowerInvariant();

        switch (comando)
        {
            case "ajuda":
                return "ok estado | pawns [texto] | alistar ID 0|1 | ir ID x,z | " +
                       "incidente DEF [pontos] | velocidade NOME | despejar | sair " +
                       "|| interface: selecionar ID… | menu x,z | irarrastando x,z | olhar x,z";

            case "estado":
                return Estado();

            case "pawns":
                return Pawns(partes.Length > 1 ? partes[1] : null);

            case "alistar":
                return Alistar(partes);

            case "ir":
                return Ir(partes);

            case "incidente":
                return Incidente(partes);

            case "velocidade":
                return Velocidade(partes);

            case "selecionar":
                return Selecionar(partes);

            case "menu":
                return Menu(partes);

            case "irarrastando":
                return IrArrastando(partes);

            case "olhar":
                return Olhar(partes);

            case "despejar":
                RastreioDePawns.Despejar();
                return "ok rastreio de pawns despejado no diário";

            case "sair":
                Log.Message("[WithFriends/controle] 'sair' recebido — encerrando.");
                DiarioDaInstancia.Fechar();
                LongEventHandler.ExecuteWhenFinished(Root.Shutdown);
                return "ok saindo";

            default:
                return $"erro comando desconhecido: {comando} (tente 'ajuda')";
        }
    }

    static string Estado()
    {
        if (Current.Game == null) return "ok fora de partida";

        var mapa = Find.CurrentMap;
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;

        return
            $"ok tick={Find.TickManager.TicksGame} " +
            $"velocidade={Find.TickManager.CurTimeSpeed} " +
            $"pausado={Find.TickManager.Paused} " +
            $"mapa={(mapa != null ? mapa.uniqueID.ToString() : "-")} " +
            $"pawns={mapa?.mapPawns.AllPawnsSpawned.Count ?? 0} " +
            $"colonos={mapa?.mapPawns.FreeColonistsSpawnedCount ?? 0} " +
            $"sessao={(sessao != null ? sessao.Estado.ToString() : "-")} " +
            $"passo={sessao?.TickDeSessao ?? -1}";
    }

    static string Pawns(string? filtro)
    {
        var mapa = Find.CurrentMap;
        if (mapa == null) return "erro sem mapa";

        var achados = mapa.mapPawns.AllPawnsSpawned
            .Where(p => filtro == null || (p.LabelShort?.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            .OrderBy(p => p.thingIDNumber)
            .Take(60)
            .Select(p =>
                $"{p.thingIDNumber}:{p.LabelShort}:{p.Position.x},{p.Position.z}:" +
                $"{(p.drafter?.Drafted == true ? "alistado" : "livre")}:" +
                $"{p.CurJobDef?.defName ?? "-"}");

        return "ok " + string.Join(" ", achados);
    }

    static string Alistar(string[] partes)
    {
        if (partes.Length < 3) return "erro uso: alistar ID 0|1";
        if (!int.TryParse(partes[1], out int id)) return "erro id inválido";

        var pawn = Achar(id);
        if (pawn == null) return $"erro pawn {id} não encontrado";

        // Separar os dois casos importa: "não existe" e "existe mas não é
        // alistável" mandam procurar em lugares opostos, e animal ou visitante
        // aparece no `pawns` como qualquer outro.
        if (pawn.drafter == null)
            return $"erro {pawn.LabelShort} não é alistável (animal, visitante ou prisioneiro)";

        bool valor = partes[2] != "0";
        pawn.drafter.Drafted = valor;
        return $"ok {pawn.LabelShort} {(valor ? "alistado" : "liberado")}";
    }

    static string Ir(string[] partes)
    {
        if (partes.Length < 3) return "erro uso: ir ID x,z";
        if (!int.TryParse(partes[1], out int id)) return "erro id inválido";

        var celula = partes[2].Split(',');
        if (celula.Length < 2 || !int.TryParse(celula[0], out int x) || !int.TryParse(celula[1], out int z))
            return "erro célula inválida (use x,z)";

        var pawn = Achar(id);
        if (pawn?.jobs == null) return $"erro pawn {id} não encontrado";

        var alvo = new IntVec3(x, 0, z);
        if (!alvo.InBounds(pawn.Map)) return "erro célula fora do mapa";

        // O mesmo que o clique faz: o jogo resolve para o possível mais perto.
        var destino = RCellFinder.BestOrderedGotoDestNear(alvo, pawn);
        if (!destino.IsValid) return "erro sem destino alcançável perto dali";

        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, destino), JobTag.Misc);
        return $"ok {pawn.LabelShort} → {destino.x},{destino.z}";
    }

    static string Incidente(string[] partes)
    {
        if (partes.Length < 2) return "erro uso: incidente DEF [pontos]";

        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        var mapa = Find.CurrentMap;
        if (mapa == null) return "erro sem mapa";

        float pontos = partes.Length > 2 && float.TryParse(partes[2], out float p)
            ? p
            : StorytellerUtility.DefaultThreatPointsNow(mapa);

        // Dentro de visita é comando de sessão (§4, só o anfitrião). Fora dela,
        // não há a quem pedir: dispara local, que é o que a bancada quer.
        if (sessao is { Estado: EstadoSessaoLocal.Simulando, Atual: not null })
        {
            WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
            {
                SessaoId = sessao.Atual.SessaoId,
                Payload = ComandoDeSessao.Incidente(partes[1], pontos),
            });
            return $"ok incidente {partes[1]} ({pontos:F0} pontos) proposto como comando";
        }

        var def = DefDatabase<IncidentDef>.GetNamedSilentFail(partes[1]);
        if (def == null) return $"erro incidente desconhecido: {partes[1]}";

        var parms = StorytellerUtility.DefaultParmsNow(def.category, mapa);
        parms.points = pontos;
        return def.Worker.TryExecute(parms)
            ? $"ok incidente {def.defName} disparado ({pontos:F0} pontos)"
            : $"erro incidente {def.defName} recusou executar";
    }

    static string Velocidade(string[] partes)
    {
        if (partes.Length < 2) return "erro uso: velocidade Normal|Fast|Superfast|Ultrafast|Paused";
        if (!Enum.TryParse<TimeSpeed>(partes[1], ignoreCase: true, out var v))
            return $"erro velocidade desconhecida: {partes[1]}";

        // Na bancada, mudar o relógio direto não adianta: ela o reescreve a
        // cada quadro para impedir que um incidente pause a corrida. Quem manda
        // aqui muda o alvo dela.
        if (ModoAvulso.Ativo) ModoAvulso.Velocidade = v;

        Find.TickManager.CurTimeSpeed = v;
        return $"ok velocidade {v}";
    }

    // ------------------------------------------------------------------
    // Gestos de interface
    //
    // **Por que chamar direto em vez de mover o mouse.** Numa instância
    // `-batchmode -nographics` o `OnGUI` não roda — foi por isso que carregar um
    // save precisou sair de `MainMenuDrawer.MainMenuOnGUI` para
    // `Root_Entry.Update`. Sem laço de interface, evento de mouse sintético não
    // é consumido por ninguém.
    //
    // E o que causa divergência não é o mouse: é o **código de interface
    // rodando**. Todas as causas achadas até hoje foram consulta ou escrita
    // feita por ele — a ordem dos vizinhos, as células de zona, o memo de
    // alcançabilidade, o `EndCurrentJob` do "ir aqui". Nenhuma precisou de um
    // pixel desenhado.
    //
    // Então a bancada chama os pontos de entrada que o clique chamaria. Não é o
    // mouse; é tudo o que vem depois dele.
    // ------------------------------------------------------------------

    static string Selecionar(string[] partes)
    {
        var seletor = Find.Selector;
        if (seletor == null) return "erro sem seletor (instância sem interface montada)";

        seletor.ClearSelection();

        int quantos = 0;
        for (int i = 1; i < partes.Length; i++)
        {
            if (!int.TryParse(partes[i], out int id)) continue;
            var pawn = Achar(id);
            if (pawn == null) continue;
            seletor.Select(pawn, playSound: false, forceDesignatorDeselect: false);
            quantos++;
        }

        return $"ok {quantos} selecionado(s)";
    }

    /// <summary>
    /// Monta o menu flutuante naquela célula — o clique com o botão direito.
    ///
    /// <para>É o gesto mais caro da interface e o mais suspeito: é aqui que
    /// rodam os <c>FloatMenuOptionProvider_*</c>, e foi num tick destes que uma
    /// partida contou <b>229 mil</b> consultas de alcançabilidade de um lado e
    /// zero do outro.</para>
    /// </summary>
    static string Menu(string[] partes)
    {
        if (partes.Length < 2) return "erro uso: menu x,z";

        var seletor = Find.Selector;
        if (seletor == null) return "erro sem seletor (instância sem interface montada)";

        var celula = LerCelula(partes[1]);
        if (!celula.IsValid) return "erro célula inválida (use x,z)";

        var pawns = seletor.SelectedPawns;
        if (pawns.Count == 0) return "erro nenhum pawn selecionado (use 'selecionar ID…')";

        var opcoes = FloatMenuMakerMap.GetOptions(pawns, celula.ToVector3Shifted(), out _);

        return $"ok {opcoes.Count} opção(ões): " +
               string.Join(" | ", opcoes.Take(8).Select(o => o.Label.Replace(' ', '_')));
    }

    /// <summary>
    /// O "ir aqui" arrastado, que é o caminho exato de
    /// <c>MultiPawnGotoController</c> — e foi ele que encerrava o <c>Goto</c> na
    /// máquina de quem clicava, sem passar por comando nenhum.
    /// </summary>
    static string IrArrastando(string[] partes)
    {
        if (partes.Length < 2) return "erro uso: irarrastando x,z";

        var seletor = Find.Selector;
        if (seletor == null) return "erro sem seletor (instância sem interface montada)";

        var celula = LerCelula(partes[1]);
        if (!celula.IsValid) return "erro célula inválida (use x,z)";

        var pawns = seletor.SelectedPawns.ToList();
        if (pawns.Count == 0) return "erro nenhum pawn selecionado";

        seletor.gotoController.StartInteraction(celula);
        foreach (var pawn in pawns) seletor.gotoController.AddPawn(pawn);
        seletor.gotoController.FinalizeInteraction();

        return $"ok {pawns.Count} pawn(s) mandados para {celula.x},{celula.z} pelo arrasto";
    }

    /// <summary>
    /// Move a câmera. Exercita tudo o que lê para onde o jogador está olhando —
    /// motes, ritmo de atualização, desenho de pawn.
    /// </summary>
    static string Olhar(string[] partes)
    {
        if (partes.Length < 2) return "erro uso: olhar x,z";

        var celula = LerCelula(partes[1]);
        if (!celula.IsValid) return "erro célula inválida (use x,z)";
        if (Find.CameraDriver == null) return "erro sem câmera";

        Find.CameraDriver.JumpToCurrentMapLoc(celula);
        return $"ok olhando para {celula.x},{celula.z}";
    }

    static IntVec3 LerCelula(string texto)
    {
        var partes = texto.Split(',');
        return partes.Length >= 2 && int.TryParse(partes[0], out int x) && int.TryParse(partes[1], out int z)
            ? new IntVec3(x, 0, z)
            : IntVec3.Invalid;
    }

    static Pawn? Achar(int id) =>
        Find.Maps?.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == id);
}

/// <summary>
/// A porta responde no menu principal também — ver <see cref="PortaDeControle.Atender"/>.
/// </summary>
[HarmonyPatch(typeof(Root_Entry), nameof(Root_Entry.Update))]
public static class ControleNoMenu
{
    [HarmonyPostfix]
    public static void Depois() => PortaDeControle.Atender();
}

/// <summary>E dentro da partida, que é onde a maior parte dos comandos vale.</summary>
[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
public static class ControleNaPartida
{
    [HarmonyPostfix]
    public static void Depois() => PortaDeControle.Atender();
}
