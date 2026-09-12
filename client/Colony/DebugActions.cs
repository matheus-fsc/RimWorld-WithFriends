using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using LudeonTK;
using RimWorld;
using WithFriends.Client.Net;
using WithFriends.Transport;
using Verse;

namespace WithFriends.Client.Colony;

/// <summary>
/// Ações de dev para verificar a §7 dentro do jogo, sem servidor.
/// Menu: Dev mode → Debug actions → categoria "WithFriends".
/// </summary>
public static class DebugActions
{
    /// <summary>
    /// Sem estado próprio: tudo o que precisa lembrar mora no
    /// SincronizacaoComponent, que é por partida.
    /// </summary>
    static readonly Session.CongeladorRimWorld Congelador = new();

    /// <summary>
    /// Liga o rastreio de RNG por local de chamada — caro, então só quando há
    /// uma divergência para caçar. Ver <see cref="Session.RastreioDeRng"/>.
    /// </summary>
    [DebugAction("WithFriends", "Rastrear RNG da sessão (liga/desliga)",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void AlternarRastreioDeRng()
    {
        Session.RastreioDeRng.Ligado = !Session.RastreioDeRng.Ligado;
        Log.Message(
            $"[WithFriends] rastreio de RNG {(Session.RastreioDeRng.Ligado ? "LIGADO" : "desligado")} " +
            $"— custo até agora: {Session.RastreioDeRng.Custo}");
    }

    /// <summary>
    /// Varre o IL do jogo e relata quem toca fonte local — ADR 0015.
    ///
    /// Fora da subida de propósito: são dezenas de milhares de métodos.
    /// </summary>
    /// <summary>
    /// Chama um raid dentro da visita, como comando.
    ///
    /// <para>As ferramentas de debug do jogo estão bloqueadas durante a sessão,
    /// e devem estar: elas acontecem de um lado só. Mas isso tirava o único
    /// jeito de exercitar combate — o log mostrava
    /// "ação de debug recusada durante a sessão: 40 points".</para>
    ///
    /// <para>Aqui o incidente vira comando: acontece no mesmo tick nos dois
    /// lados, com o mesmo RNG. E só o anfitrião pode — é decisão da colônia
    /// dele (§4).</para>
    /// </summary>
    [DebugAction("WithFriends", "Provocar raid na visita",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ProvocarRaidNaVisita()
    {
        var sessao = SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: Session.EstadoSessaoLocal.Simulando } || sessao.Atual == null)
        {
            Messages.Message(
                "With Friends: isto é para usar dentro de uma visita.",
                MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        float pontos = StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap);
        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = Session.ComandoDeSessao.Incidente(
                IncidentDefOf.RaidEnemy.defName, pontos),
        });

        Log.Message($"[WithFriends] raid proposto como comando ({pontos:F0} pts)");
    }

    [DebugAction("WithFriends", "Guardas de determinismo",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void GuardasDeDeterminismo() =>
        Log.Message(Session.GuardasDeDeterminismo.Relatorio());

    [DebugAction("WithFriends", "Auditar fontes locais (IL)",
        allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    public static void AuditarFontesLocais()
    {
        try
        {
            string caminho = Auditoria.AuditorDeIL.Rodar();
            Messages.Message(
                $"With Friends: auditoria escrita em {caminho}",
                MessageTypeDefOf.TaskCompletion, historical: false);
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] auditoria de IL falhou: {e}");
        }
    }

    /// <summary>
    /// Mede o custo do rastreio de RNG — ver <see cref="Session.RastreioDeRng.Medir"/>.
    ///
    /// Existe para a decisão de deixá-lo ligado por padrão sair de número em vez
    /// de impressão. Já erramos os dois lados disso: desliguei por impressão, e
    /// a impressão estava certa pelo motivo errado.
    /// </summary>
    [DebugAction("WithFriends", "Medir custo do rastreio de RNG",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void MedirCustoDoRastreio() =>
        Log.Message($"[WithFriends] custo do rastreio: {Session.RastreioDeRng.Medir()}");

    [DebugAction("WithFriends", "Criar checkpoint da colônia",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void CriarCheckpoint()
    {
        try
        {
            var checkpoint = CheckpointWriter.Criar();
            var sincronizacao = SincronizacaoComponent.Atual;
            if (sincronizacao != null)
                sincronizacao.UltimoContentHash = checkpoint.Metadata.ContentHash;
            Log.Message(
                $"[WithFriends] checkpoint de {checkpoint.Identity}\n" +
                $"  {checkpoint.Metadata}\n" +
                $"  {checkpoint.Conteudo.Length:N0} bytes em {checkpoint.Caminho}");
            Messages.Message(
                $"Checkpoint criado: tick {checkpoint.Metadata.GameTick}, " +
                $"{checkpoint.Conteudo.Length / 1024:N0} KB",
                MessageTypeDefOf.TaskCompletion, historical: false);
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] falha ao criar checkpoint: {e}");
        }
    }

    [DebugAction("WithFriends", "Imprimir identidade da colônia",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void ImprimirIdentidade()
    {
        var componente = ColonyIdentityComponent.Atual;
        if (componente == null)
        {
            Log.Warning("[WithFriends] sem partida carregada.");
            return;
        }

        Log.Message(
            $"[WithFriends] identidade = {componente.Identity}\n" +
            $"  nome do save (irrelevante para a identidade): " +
            $"{Find.GameInfo.permadeathModeUniqueName ?? "(sem morte permanente)"}\n" +
            $"  morte permanente: {Find.GameInfo.permadeathMode}\n" +
            $"  mod_set_hash: {ModSetHash.Calcular()}\n" +
            $"  tick: {Find.TickManager.TicksGame}");
    }

    /// <summary>
    /// Reproduz a §15.1 do lado do cliente: dois checkpoints seguidos com o
    /// jogo parado têm o mesmo content_hash. Se o tick tivesse avançado entre
    /// eles com o hash igual, o servidor alertaria em até dois heartbeats.
    /// </summary>
    [DebugAction("WithFriends", "Conferir hash de conteúdo (2x)",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ConferirHash()
    {
        var primeiro = CheckpointWriter.Criar();
        var segundo = CheckpointWriter.Criar();
        bool iguais = primeiro.Metadata.ContentHash == segundo.Metadata.ContentHash;

        Log.Message(
            $"[WithFriends] hash 1: {primeiro.Metadata.ContentHash}\n" +
            $"[WithFriends] hash 2: {segundo.Metadata.ContentHash}\n" +
            $"[WithFriends] iguais: {iguais} (tick {primeiro.Metadata.GameTick} → {segundo.Metadata.GameTick})");
    }

    [DebugAction("WithFriends", "Abrir pasta de checkpoints",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void ListarCheckpoints()
    {
        var pasta = CheckpointWriter.PastaCheckpoints;
        if (!Directory.Exists(pasta))
        {
            Log.Message($"[WithFriends] ainda não existe: {pasta}");
            return;
        }

        var arquivos = new DirectoryInfo(pasta).GetFiles("*.rws")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        Log.Message(
            $"[WithFriends] {arquivos.Count} checkpoint(s) em {pasta}\n" +
            string.Join("\n", arquivos.Take(10).Select(f => $"  {f.Name}  {f.Length / 1024:N0} KB  {f.LastWriteTimeUtc:O}")));
    }

    // --- rede (§17) ---

    [DebugAction("WithFriends", "Conectar ao coordenador",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void Conectar()
    {
        if (!Conexao.Conectar(out string erro))
            Log.Error($"[WithFriends] {erro}");
    }

    [DebugAction("WithFriends", "Desconectar",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void Desconectar()
    {
        WithFriendsMod.Cliente.Desconectar();
        Log.Message("[WithFriends] desconectado.");
    }

    [DebugAction("WithFriends", "Enviar checkpoint ao coordenador",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void EnviarCheckpoint()
    {
        var cliente = WithFriendsMod.Cliente;
        if (!cliente.Conectado)
        {
            Log.Warning($"[WithFriends] não conectado (estado: {cliente.Estado}).");
            return;
        }

        var checkpoint = CheckpointWriter.Criar();
        var sincronizacao = SincronizacaoComponent.Atual;
        if (sincronizacao != null)
            sincronizacao.UltimoContentHash = checkpoint.Metadata.ContentHash;

        cliente.Enviar(checkpoint.ParaMensagem());
        Log.Message(
            $"[WithFriends] checkpoint enviado: {checkpoint.Conteudo.Length:N0} bytes, " +
            $"tick {checkpoint.Metadata.GameTick}, {checkpoint.Metadata.ContentHash}");
    }

    /// <summary>
    /// Reproduz a §15.1 com a SUA colônia: manda heartbeats com o tick
    /// avançando e a impressão digital congelada, que é o que um save órfão
    /// produz. O alerta deve voltar como carta no jogo em segundos.
    /// </summary>
    [DebugAction("WithFriends", "Simular incidente de save órfão",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void SimularIncidente()
    {
        var cliente = WithFriendsMod.Cliente;
        if (!cliente.Conectado)
        {
            Log.Warning($"[WithFriends] não conectado (estado: {cliente.Estado}).");
            return;
        }

        var identity = ColonyIdentityComponent.Atual!.Identity;
        // Colônia descartável: a simulação manda ticks futuros, e adotá-los
        // como referência na colônia real faria os heartbeats seguintes (com
        // o tick verdadeiro, menor) parecerem regressão.
        string colonyIdFalsa = identity.ColonyId + "-simulacao";
        string congelada = FingerprintVivo.Calcular();
        long tick = Find.TickManager.TicksGame;

        for (int i = 0; i <= 3; i++)
        {
            cliente.Enviar(new Protocol.Messages.ColoniaHeartbeat
            {
                PlayerId = identity.PlayerId,
                ColonyId = colonyIdFalsa,
                GameTick = tick + i * 2_500,   // o jogo "avançou"
                StateFingerprint = congelada,  // ... e nada mudou
            });
        }

        Log.Message(
            $"[WithFriends] simulando save órfão em {colonyIdFalsa}: 4 heartbeats de {tick} a " +
            $"{tick + 7_500} com a impressão digital travada em {congelada}. " +
            "O alerta deve chegar como carta em segundos.");
    }

    // --- mundo (§2.1, §4) ---

    [DebugAction("WithFriends", "Publicar meu assentamento",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void PublicarAssentamento() => World.MundoPublicador.PublicarProprioAssentamento();

    [DebugAction("WithFriends", "Listar assentamentos no planeta",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void ListarAssentamentos()
    {
        var remotos = Find.WorldObjects.AllWorldObjects
            .OfType<World.AssentamentoRemoto>()
            .ToList();

        long cursor = Current.Game.GetComponent<World.WorldCursorComponent>()?.WorldCursor ?? 0;

        Log.Message(
            $"[WithFriends] cursor de mundo: {cursor}\n" +
            $"  {remotos.Count} assentamento(s) de outros jogadores:\n" +
            string.Join("\n", remotos.Select(a =>
                $"    {a.nomeColonia} — tile {a.Tile}, riqueza ~{a.riquezaEstimada:N0}, " +
                $"{(a.online ? "online" : "offline")} ({a.playerId})")));
    }

    [DebugAction("WithFriends", "Esquecer assentamentos remotos",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void EsquecerAssentamentos()
    {
        int removidos = World.MundoAplicador.EsquecerTodos();
        Log.Message($"[WithFriends] {removidos} assentamento(s) remoto(s) removido(s) do planeta local.");
    }

    /// <summary>
    /// Zera o cursor: na próxima conexão o mundo é reconstruído do log
    /// inteiro. É a rede de segurança do modelo append-only — o estado local
    /// é sempre derivável dos fatos (§2.1).
    /// </summary>
    [DebugAction("WithFriends", "Reconstruir mundo do zero",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void ReconstruirMundo()
    {
        World.MundoAplicador.EsquecerTodos();
        var cursor = Current.Game.GetComponent<World.WorldCursorComponent>();
        if (cursor != null) cursor.WorldCursor = 0;
        Log.Message("[WithFriends] cursor zerado — reconecte para reconstruir o planeta do log.");
    }

    // --- sessão (§2.3) ---

    static Session.SessaoCliente? Sessao => SincronizacaoComponent.Atual?.Sessao;

    [DebugAction("WithFriends", "Convidar para sessão de ensaio",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ConvidarParaEnsaio() => Convidar(Protocol.Messages.TipoSessao.Ensaio);

    static void Convidar(Protocol.Messages.TipoSessao tipo)
    {
        string eu = WithFriendsMod.Settings.PlayerIdOuNovo();
        var outro = SincronizacaoComponent.Online.FirstOrDefault(p => p.Key != eu);

        if (outro.Key == null)
        {
            Log.Warning("[WithFriends] ninguém mais online. Sessão exige os dois presentes (§11).");
            return;
        }

        var identidade = ColonyIdentityComponent.Atual!;
        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoConvite
        {
            Para = outro.Key,
            Tipo = tipo,
            ColoniaAnfitria = identidade.ColonyId,
            SessionModSetHash = ModSetHash.Calcular(),
        });
        Log.Message($"[WithFriends] convite de {tipo} enviado para {outro.Value} ({outro.Key})");
    }

    [DebugAction("WithFriends", "Convidar para visita (manda o mapa)",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ConvidarParaVisita() => Convidar(Protocol.Messages.TipoSessao.Visitar);

    [DebugAction("WithFriends", "Aceitar convite de sessão",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void AceitarConvite() => Sessao?.Aceitar();

    [DebugAction("WithFriends", "Encerrar sessão",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void EncerrarSessao() => Sessao?.PedirEncerramento();

    /// <summary>
    /// Por que "Priorizar…" não aparece para o pawn selecionado, na coisa sob o
    /// cursor.
    ///
    /// <para>Duas vezes seguidas eu suspeitei do mod e a causa era regra do
    /// jogo: alistado não recebe a opção, e sem material alcançável o blueprint
    /// também não gera trabalho. Adivinhar sai caro — esta ação refaz o mesmo
    /// laço do <c>FloatMenuOptionProvider_WorkGivers</c> e diz, para cada
    /// doador de trabalho, qual porta fechou.</para>
    /// </summary>
    [DebugAction("WithFriends", "Por que não dá para priorizar aqui?",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void PorQueNaoPrioriza()
    {
        var pawn = Find.Selector.SelectedPawns.FirstOrDefault();
        if (pawn == null) { Log.Message("[WithFriends] selecione um pawn primeiro."); return; }

        var celula = UI.MouseCell();
        var coisas = celula.GetThingList(pawn.Map);
        var alvo = coisas.FirstOrDefault(c => c.def.selectable) ?? coisas.FirstOrDefault();

        var linhas = new System.Text.StringBuilder();
        linhas.AppendLine(
            $"[WithFriends] por que {pawn.LabelShort} não prioriza em {celula} " +
            $"(alvo: {alvo?.LabelShort ?? "célula vazia"}):");
        linhas.AppendLine($"  alistado: {pawn.Drafted} " +
                          "(alistado só recebe doadores com canBeDoneWhileDrafted)");

        foreach (var tipo in DefDatabase<WorkTypeDef>.AllDefsListForReading)
        foreach (var doadorDef in tipo.workGiversByPriority)
        {
            if (doadorDef.Worker is not WorkGiver_Scanner scanner) continue;
            if (!scanner.def.directOrderable) continue;

            string porque;
            try
            {
                if (pawn.Drafted && !doadorDef.canBeDoneWhileDrafted) porque = "pawn alistado";
                else if (pawn.workSettings?.GetPriority(tipo) == 0) porque = "tipo de trabalho desligado neste pawn";
                else
                {
                    Verse.AI.JobFailReason.Clear();
                    bool tem = alvo != null
                        ? !scanner.ShouldSkip(pawn, true) && scanner.HasJobOnThing(pawn, alvo, true)
                        : !scanner.ShouldSkip(pawn, true) && scanner.HasJobOnCell(pawn, celula, true);

                    porque = tem
                        ? "TEM TRABALHO — a opção deveria aparecer"
                        : Verse.AI.JobFailReason.HaveReason
                            ? $"sem trabalho: {Verse.AI.JobFailReason.Reason}"
                            : "sem trabalho (sem motivo declarado — é o caso que some do menu em silêncio)";
                }
            }
            catch (System.Exception e) { porque = $"EXCEÇÃO: {e.Message}"; }

            // Só o que interessa: o silêncio total polui, e o que se procura é
            // o doador que deveria ter dado trabalho.
            if (porque.StartsWith("TEM TRABALHO") || porque.StartsWith("EXCEÇÃO") ||
                porque.StartsWith("sem trabalho: "))
                linhas.AppendLine($"  {doadorDef.defName}: {porque}");
        }

        linhas.AppendLine("  (doadores sem motivo declarado foram omitidos — são a maioria e é normal)");
        Log.Message(linhas.ToString());
    }

    [DebugAction("WithFriends", "Retrato da partida",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void Retrato() => Session.RetratoDaPartida.Registrar("sob demanda");

    [DebugAction("WithFriends", "Status da visita",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void StatusDaVisita() =>
        Log.Message(
            $"[WithFriends] visita: {(Session.VisitaEmAndamento.Ativa ? "ativa" : "nenhuma")}\n" +
            $"  papel: {(Session.VisitaEmAndamento.SouVisitante ? "visitante" : "anfitrião/nenhum")}\n" +
            $"  sessão: {Session.VisitaEmAndamento.Inicio?.SessaoId ?? "(nenhuma)"}\n" +
            $"  ponto de retorno: {Session.VisitaEmAndamento.HashPreSessao ?? "(nenhum)"}\n" +
            $"  save da visita: {Session.VisitaEmAndamento.SaveDaVisita ?? "(nenhum)"}");

    [DebugAction("WithFriends", "Status da sessão",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void StatusDaSessao()
    {
        var sessao = Sessao;
        if (sessao == null) { Log.Warning("[WithFriends] sem partida."); return; }

        Log.Message(
            $"[WithFriends] sessão: {sessao.Estado}\n" +
            $"  id: {sessao.Atual?.SessaoId ?? "(nenhuma)"}\n" +
            $"  tick de sessão: {sessao.TickDeSessao} / liberado {sessao.TickLiberado}\n" +
            $"  ticks segurados pela barreira: {Session.RelogioDeSessaoRimWorld.TicksSegurados}\n" +
            $"  online: {string.Join(", ", SincronizacaoComponent.Online.Select(p => p.Value))}");
    }

    [DebugAction("WithFriends", "Enviar comando de teste na sessão",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ComandoDeTeste()
    {
        var sessao = Sessao;
        if (sessao?.Atual == null) { Log.Warning("[WithFriends] não há sessão ativa."); return; }

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = System.Text.Encoding.UTF8.GetBytes($"olá do tick {sessao.TickDeSessao}"),
        });
        Log.Message("[WithFriends] comando de teste proposto ao coordenador");
    }

    /// <summary>
    /// Verifica a porta de impressão digital com uma instância só: amostra o
    /// estado do RNG ao longo de alguns ticks e mostra o resumo.
    /// </summary>
    [DebugAction("WithFriends", "Impressão digital do intervalo",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ImpressaoDigital()
    {
        var digital = new Session.ImpressaoDigitalRimWorld();
        if (!digital.Disponivel)
        {
            Log.Error("[WithFriends] Rand.StateCompressed não encontrado — ver Acoplamentos com o jogo.");
            return;
        }

        long tick = Find.TickManager.TicksGame;
        digital.IniciarIntervalo(tick);

        // Três amostras com consumo de RNG entre elas, para que o contador de
        // iterações apareça andando. Num intervalo de sessão de verdade, cada
        // amostra sai depois de um tick — e é o próprio jogo que consome.
        for (int i = 0; i < 3; i++)
        {
            digital.AmostrarMundo();
            foreach (var mapa in Find.Maps)
                digital.AmostrarMapa(mapa.uniqueID);
            Rand.Value.ToString();   // simula consumo de um tick
        }

        var opiniao = digital.FecharIntervalo(tick);

        Log.Message(
            $"[WithFriends] opinião de sincronia {opiniao.TickInicial}–{opiniao.TickFinal}\n" +
            $"  arredondamento FP: {opiniao.ModoDeArredondamento}\n" +
            $"  RNG agora: semente={Session.ImpressaoDigitalRimWorld.Semente()} " +
            $"iterações={Session.ImpressaoDigitalRimWorld.Iteracoes()}\n" +
            $"  amostras do mundo: [{string.Join(", ", opiniao.EstadosDoMundo)}]\n" +
            string.Join("\n", opiniao.EstadosPorMapa.Select(p =>
                $"  mapa {p.Key}: [{string.Join(", ", p.Value)}]")) + "\n" +
            $"  resumo: {opiniao.Resumo()}");
    }

    /// <summary>Congela e produz o ponto de retorno da §2.3.</summary>
    [DebugAction("WithFriends", "Congelar e tirar checkpoint pré-sessão",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void CongelarPreSessao()
    {
        try
        {
            string hash = Congelador.CongelarECheckpoint();
            Messages.Message(
                $"Congelado. Ponto de retorno: {hash.Substring(7, 12)}…",
                MessageTypeDefOf.TaskCompletion, historical: false);
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] falha ao congelar: {e}");
        }
    }

    [DebugAction("WithFriends", "Descongelar",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Descongelar() => Congelador.Descongelar();

    /// <summary>
    /// Rollback manual. Pergunta antes: voltar no tempo descarta o que foi
    /// jogado desde o congelamento, e o sistema nunca escolhe sozinho (§7.3).
    /// </summary>
    [DebugAction("WithFriends", "Voltar ao checkpoint pré-sessão",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void VoltarAoPreSessao()
    {
        string? hash = Congelador.HashPreSessao;
        if (hash == null)
        {
            Log.Warning("[WithFriends] não há checkpoint pré-sessão nesta partida. Congele primeiro.");
            return;
        }

        long tickAtual = Find.TickManager.TicksGame;
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            $"Voltar a colônia ao checkpoint pré-sessão {hash.Substring(7, 12)}…?\n\n" +
            $"Tick atual: {tickAtual}. O que foi jogado desde o congelamento sai do jogo — " +
            "mas nada é apagado: o estado atual é guardado num checkpoint novo antes de voltar.",
            () => Congelador.Restaurar(hash),
            destructive: true));
    }

    [DebugAction("WithFriends", "Pedir checkpoint pré-sessão ao coordenador",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void PedirPreSessao()
    {
        string? hash = Congelador.HashPreSessao;
        if (hash == null)
        {
            Log.Warning("[WithFriends] não há hash pré-sessão nesta partida para pedir.");
            return;
        }
        Session.CongeladorRimWorld.Solicitar(hash);
    }

    /// <summary>
    /// Primeira tarefa do passo B (ADR 0007): quanto custa levar um mapa para
    /// o outro lado.
    /// </summary>
    [DebugAction("WithFriends", "Medir custo de transferir o mapa",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void MedirMapa()
    {
        var mapa = Find.CurrentMap;
        var linhas = new List<string>
        {
            "[WithFriends] custo de transferir um mapa",
            "  " + Session.MedicaoDeMapa.Contexto(mapa),
            "",
            Session.MedicaoDeMapa.SoOMapa(mapa).ToString(),
            Session.MedicaoDeMapa.PartidaInteira().ToString(),
        };
        Log.Message(string.Join("\n", linhas));
        linhas.Clear();
        Log.Message(string.Join("\n", linhas));
    }

    /// <summary>
    /// O outro lado da conta: o mapa medido volta do disco? Rodar depois de
    /// "Medir custo de transferir o mapa".
    /// </summary>
    [DebugAction("WithFriends", "Medir carregamento do mapa de volta",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void MedirCarregamento() =>
        Log.Message(
            "[WithFriends] carregamento do mapa\n" +
            Session.MedicaoDeMapa.CarregarDeVolta(Find.CurrentMap.uniqueID));

    /// <summary>
    /// Testa a inserção sem rede: pega o mapa que "Medir custo de transferir"
    /// gerou e tenta inseri-lo nesta partida. É o caminho mais arriscado do
    /// bootstrap, e dá para exercitá-lo com uma instância só.
    /// </summary>
    [DebugAction("WithFriends", "Inserir o mapa medido nesta partida",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void InserirMapaMedido()
    {
        string caminho = Session.MedicaoDeMapa.CaminhoDoMapa(Find.CurrentMap.uniqueID);
        var resultado = Session.InsercaoDeMapa.Inserir(caminho, Find.CurrentMap.uniqueID);
        Log.Message($"[WithFriends] {resultado}");

        if (resultado.Ok)
            Log.Warning(
                "[WithFriends] este mapa foi inserido para teste e NÃO faz parte de uma sessão. " +
                $"Use \"Descartar mapa inserido\" para removê-lo (id {resultado.MapaId}).");
    }

    [DebugAction("WithFriends", "Descartar mapa inserido",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void DescartarMapaInserido() =>
        Session.InsercaoDeMapa.Descartar(Find.CurrentMap.uniqueID);

    // --- mala do visitante (ida da caravana) ---

    /// <summary>
    /// Empacota os colonos selecionados (ou os dois primeiros) para uma visita.
    /// </summary>
    [DebugAction("WithFriends", "Empacotar mala do visitante",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void EmpacotarMala()
    {
        var escolhidos = Find.Selector.SelectedPawns
            .Where(p => p.IsColonist && !p.Dead)
            .ToList();

        if (escolhidos.Count == 0)
            escolhidos = Find.CurrentMap.mapPawns.FreeColonists.Take(2).ToList();

        var resultado = Session.MalaDoVisitante.Empacotar(
            escolhidos, Session.MalaDoVisitante.Caminho("teste"));

        Log.Message(
            $"[WithFriends] mala empacotada: {resultado}\n" +
            $"  quem vai: {string.Join(", ", escolhidos.Select(p => p.LabelShort))}");
    }

    /// <summary>
    /// Abre a mala **nesta mesma partida** — de propósito o pior caso possível
    /// para colisão de id, já que os originais continuam aqui. Se passa nisto,
    /// passa entre partidas diferentes.
    /// </summary>
    [DebugAction("WithFriends", "Abrir mala do visitante aqui",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void AbrirMala()
    {
        var resultado = Session.MalaDoVisitante.Desempacotar(
            Session.MalaDoVisitante.Caminho("teste"), Faction.OfPlayer);

        Log.Message($"[WithFriends] mala aberta: {resultado}");
        if (!resultado.Ok) return;

        var mapa = Find.CurrentMap;
        foreach (var pawn in resultado.Recuperados)
        {
            if (!CellFinder.TryFindRandomCellNear(
                    mapa.Center, mapa, 20, c => c.Standable(mapa), out var celula))
                celula = CellFinder.RandomEdgeCell(mapa);

            GenSpawn.Spawn(pawn, celula, mapa);
            Log.Message(
                $"[WithFriends]   {pawn.LabelShort} em {celula} — " +
                $"id {pawn.thingIDNumber}, ideologia {pawn.Ideo?.name ?? "(nenhuma)"}, " +
                $"facção {pawn.Faction?.Name ?? "(nenhuma)"}");
        }

        // Quem chega pela mala é do visitante, e continua sendo depois de a
        // partida viajar — a posse mora no GameComponent, que vai no save.
        Session.PosseDePawns.Registrar(
            resultado.Recuperados, WithFriendsMod.Settings.PlayerIdOuNovo());
    }

    [DebugAction("WithFriends", "Acoplamentos com o jogo",
        allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    public static void Acoplamentos() => Log.Message(Patches.CatalogoDePatches.Relatorio());

    [DebugAction("WithFriends", "Status da conexão",
        allowedGameStates = AllowedGameStates.Playing)]
    public static void Status()
    {
        var cliente = WithFriendsMod.Cliente;
        Log.Message(
            $"[WithFriends] estado={cliente.Estado} destino={cliente.Descricao}\n" +
            $"  capacidades: {cliente.CapacidadesNegociadas}\n" +
            $"  último hash enviado: {SincronizacaoComponent.Atual?.UltimoContentHash ?? "(nenhum)"}\n" +
            $"  último erro: {(cliente.UltimoErro.Length > 0 ? cliente.UltimoErro : "(nenhum)")}");
    }
}
