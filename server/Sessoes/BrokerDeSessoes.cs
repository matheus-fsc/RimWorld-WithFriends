using WithFriends.Protocol;
using WithFriends.Protocol.Determinismo;
using WithFriends.Protocol.Messages;
using WithFriends.Server.Mundo;

namespace WithFriends.Server.Sessoes;

/// <summary>Um convite ainda sem resposta.</summary>
public sealed class Convite
{
    public string Id { get; init; } = "";
    public string De { get; init; } = "";
    public string Para { get; init; } = "";
    public TipoSessao Tipo { get; init; }
    public string ColoniaAnfitria { get; init; } = "";
    public string ModSetHashDeQuemConvidou { get; init; } = "";
    public DateTime CriadoEm { get; init; }
}

/// <summary>
/// Broker de sessões — §10: convites, barreiras, arbitragem de aborto.
/// O coordenador nunca roda lógica de RimWorld; ele combina quem entra,
/// ordena o que trafega e decide quando abortar.
/// </summary>
public sealed class BrokerDeSessoes
{
    public static readonly TimeSpan ValidadeDoConvite = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Comparações seguidas com digitais diferentes antes de abortar.
    ///
    /// Uma divergência isolada que se corrige não é desync: medido em jogo, os
    /// dois lados fizeram o **mesmo trabalho total** com um tick de diferença,
    /// e o traço reconvergeu em três ticks. Desync de verdade nunca volta.
    /// </summary>
    public const int DivergenciasParaAbortar = 3;

    /// <summary>
    /// Quantas vezes uma sessão pode refazer o ponto de junção antes de o
    /// aborto voltar a ser a resposta.
    ///
    /// <para>Cinco é folgado de propósito. O objetivo é **continuar jogando** e
    /// colher várias divergências por visita em vez de uma; o teto existe só
    /// para o caso em que a causa reaparece imediatamente e a visita vira uma
    /// sequência de recarregamentos.</para>
    /// </summary>
    public const int RessincronizacoesPorSessao = 5;

    readonly object trava = new();
    readonly Dictionary<string, Convite> convites = new();
    readonly Dictionary<string, Sessao> sessoes = new();
    readonly Presenca presenca;
    readonly Func<string> novoId;

    int ordemDoComando;

    public BrokerDeSessoes(Presenca presenca, Func<string>? novoId = null)
    {
        this.presenca = presenca;
        this.novoId = novoId ?? (() => Guid.NewGuid().ToString("N")[..12]);
    }

    /// <summary>
    /// As sessões vivas — inclusive as que estão refazendo o ponto de junção.
    ///
    /// <para>Ressincronizando é um estado <b>vivo</b>: a visita continua, os dois
    /// lados só estão recarregando. Contá-la como morta faria o resto do
    /// coordenador tratar a visita como encerrada bem no meio do conserto.</para>
    /// </summary>
    public IReadOnlyCollection<Sessao> Ativas
    {
        get
        {
            lock (trava)
                return sessoes.Values
                    .Where(s => s.Estado is EstadoSessao.Ativa or EstadoSessao.Ressincronizando)
                    .ToArray();
        }
    }

    public Sessao? PorId(string sessaoId)
    {
        lock (trava) return sessoes.TryGetValue(sessaoId, out var s) ? s : null;
    }

    public Sessao? DoParticipante(string playerId)
    {
        lock (trava)
            return sessoes.Values.FirstOrDefault(s => s.Estado == EstadoSessao.Ativa && s.Tem(playerId));
    }

    /// <summary>Convida. O convite só existe se o convidado estiver online (§11).</summary>
    public IMessage Convidar(string de, SessaoConvite pedido, DateTime agora)
    {
        lock (trava)
        {
            if (pedido.Para == de)
                return Recusa(pedido.ConviteId, "Não dá para convidar a si mesmo.");

            if (presenca.Conectados.All(c => c.PlayerId != pedido.Para))
                return Recusa(pedido.ConviteId,
                    "O outro jogador está offline. Visitar, ajudar e atacar exigem sessão, " +
                    "e sessão exige os dois presentes (§11).");

            if (DoParticipante(de) != null || DoParticipante(pedido.Para) != null)
                return Recusa(pedido.ConviteId, "Um dos jogadores já está em sessão.");

            string id = novoId();
            convites[id] = new Convite
            {
                Id = id,
                De = de,
                Para = pedido.Para,
                Tipo = pedido.Tipo,
                ColoniaAnfitria = pedido.ColoniaAnfitria,
                ModSetHashDeQuemConvidou = pedido.SessionModSetHash,
                CriadoEm = agora,
            };

            return new SessaoConvite
            {
                ConviteId = id,
                De = de,                       // carimbado pelo servidor
                Para = pedido.Para,
                Tipo = pedido.Tipo,
                ColoniaAnfitria = pedido.ColoniaAnfitria,
                SessionModSetHash = pedido.SessionModSetHash,
            };
        }
    }

    /// <summary>
    /// Aceita. É **aqui** que os mods são comparados — §8: na entrada da
    /// sessão, nunca no login, para que divergência de mod jamais impeça
    /// alguém de jogar sozinho.
    /// </summary>
    public IMessage Aceitar(string quemAceita, SessaoAceite aceite, DateTime agora)
    {
        lock (trava)
        {
            if (!convites.TryGetValue(aceite.ConviteId, out var convite))
                return Recusa(aceite.ConviteId, "Convite desconhecido ou já respondido.");

            if (convite.Para != quemAceita)
                return Recusa(aceite.ConviteId, "Este convite não é para você.");

            if (agora - convite.CriadoEm > ValidadeDoConvite)
            {
                convites.Remove(convite.Id);
                return Recusa(convite.Id, "O convite expirou.");
            }

            if (convite.ModSetHashDeQuemConvidou != aceite.SessionModSetHash)
            {
                convites.Remove(convite.Id);
                return Recusa(convite.Id,
                    "Os mods que afetam a simulação não batem entre os dois jogadores. " +
                    "Sessão exige igualdade nessa classe de mods; fora dela, cada um joga " +
                    "com o que quiser (§8).");
            }

            convites.Remove(convite.Id);

            var sessao = new Sessao
            {
                Id = novoId(),
                Tipo = convite.Tipo,
                Anfitriao = convite.De,
                Visitante = convite.Para,
                ColoniaAnfitria = convite.ColoniaAnfitria,
                Semente = Environment.TickCount,
                TickInicial = 0,
                Estado = EstadoSessao.Ativa,
            };
            sessoes[sessao.Id] = sessao;

            return new SessaoInicio
            {
                SessaoId = sessao.Id,
                Tipo = sessao.Tipo,
                Anfitriao = sessao.Anfitriao,
                Visitante = sessao.Visitante,
                ColoniaAnfitria = sessao.ColoniaAnfitria,
                TickInicial = sessao.TickInicial,
                Semente = sessao.Semente,
                CompararDigitais = sessao.CompararDigitais,
            };
        }
    }

    public void Recusar(string conviteId) { lock (trava) convites.Remove(conviteId); }

    /// <summary>
    /// Agenda um comando para um tick futuro e devolve o agendamento — o mesmo
    /// para os dois lados. Ordem idêntica sem o servidor entender de jogo.
    /// </summary>
    public SessaoComando? Agendar(string autor, SessaoComando proposta) =>
        Agendar(autor, proposta, out _);

    /// <param name="recusa">
    /// Por que não foi agendado, quando dá para explicar. Silêncio numa recusa
    /// é pior que a recusa: o jogador clica, nada acontece, e ele não sabe se
    /// travou ou se não podia.
    /// </param>
    public SessaoComando? Agendar(string autor, SessaoComando proposta, out string? recusa)
    {
        recusa = null;
        lock (trava)
        {
            if (!sessoes.TryGetValue(proposta.SessaoId, out var sessao) || !sessao.Tem(autor))
                return null;

            // Ressincronizando, o passo contra o qual carimbar ainda não existe:
            // os dois lados estão voltando para um estado que está viajando.
            // Recusar com motivo é melhor que carimbar contra o passo velho.
            if (sessao.Estado == EstadoSessao.Ressincronizando)
            {
                recusa = "A visita está se ressincronizando. Tente de novo em um instante.";
                return null;
            }

            if (sessao.Estado != EstadoSessao.Ativa) return null;

            // Decisão de colônia é de quem mora nela (§4). Recusar aqui, antes
            // de carimbar, é o que garante que ninguém aplique metade: o
            // comando recusado nunca chega a existir para lado nenhum.
            if (AutoridadeDeComando.SoDoAnfitriao(AutoridadeDeComando.TipoDe(proposta.Payload))
                && autor != sessao.Anfitriao)
            {
                recusa =
                    "Esta é uma decisão da colônia, e a visita acontece na colônia do anfitrião. " +
                    "Você comanda os seus pawns; as escolhas do lugar são de quem mora nele.";
                return null;
            }

            long alvo = sessao.TickDeComando();
            return new SessaoComando
            {
                SessaoId = sessao.Id,
                Autor = autor,
                TickAlvo = alvo,
                Ordem = ++ordemDoComando,
                Payload = proposta.Payload,
            };
        }
    }

    /// <summary>
    /// Registra o avanço de um participante e compara impressões digitais.
    /// Devolve a barreira nova ou um aborto, se os dois divergiram.
    /// </summary>
    public IMessage? Barreira(string autor, SessaoBarreira relato) =>
        Barreira(autor, relato, DateTime.UtcNow);

    public IMessage? Barreira(string autor, SessaoBarreira relato, DateTime agora)
    {
        lock (trava)
        {
            if (!sessoes.TryGetValue(relato.SessaoId, out var sessao) || !sessao.Tem(autor))
                return null;

            if (sessao.Estado == EstadoSessao.Ressincronizando)
                return RelatoDurantePonto(sessao, autor, relato);

            if (sessao.Estado != EstadoSessao.Ativa) return null;

            sessao.TickPorParticipante[autor] = relato.Tick;

            // Quem mexeu no botão manda, e vale para os dois. Repetir o pedido
            // não conta como mexer — senão o último relato de um lado desfaria
            // a mudança recém-feita do outro.
            bool despausarRecusado = false;
            if (relato.MudouVelocidade)
            {
                despausarRecusado = !sessao.PedirVelocidade(autor, relato.Velocidade, agora);

                // Sem esta linha, "o comando não está sendo enviado" e "o
                // comando chegou e foi recusado" têm exatamente a mesma cara
                // nos logs do cliente — e passei duas rodadas adivinhando qual
                // dos dois era.
                Console.WriteLine(despausarRecusado
                    ? $"[{sessao.Id}] {autor} pediu {relato.Velocidade}: RECUSADO (pausa recente)"
                    : $"[{sessao.Id}] {autor} pediu {relato.Velocidade}: vale para os dois");
            }

            if (relato.Pausado) sessao.Pausados.Add(autor);
            else sessao.Pausados.Remove(autor);

            // O relógio compartilhado anda aqui, no ritmo que os dois pediram.
            // É ele que faz os dois lados estarem sempre no mesmo tick.
            sessao.AvancarRelogio(agora);

            if (relato.Fingerprint.Length > 0 && sessao.CompararDigitais)
            {
                sessao.Fingerprints[(relato.Tick, autor)] = relato.Fingerprint;

                string outro = sessao.Outro(autor);
                if (sessao.Fingerprints.TryGetValue((relato.Tick, outro), out string? doOutro))
                {
                    if (doOutro != relato.Fingerprint)
                    {
                        sessao.DivergenciasSeguidas++;
                        sessao.TicksSuspeitos.Add(relato.Tick);

                        // Uma divergência isolada não é desync: medimos casos em
                        // que os dois lados fazem o **mesmo trabalho total** com
                        // um tick de diferença, e o traço reconverge sozinho.
                        // Abortar ali mataria a visita por ruído.
                        if (sessao.DivergenciasSeguidas < DivergenciasParaAbortar)
                        {
                            Console.WriteLine(
                                $"!! sessão {sessao.Id}: digitais diferentes no tick {relato.Tick} " +
                                $"({sessao.DivergenciasSeguidas}/{DivergenciasParaAbortar}) — " +
                                OpiniaoDeSincronia.DiferencaEntreResumos(
                                    relato.Fingerprint, doOutro));

                            return new SessaoBarreira
                            {
                                SessaoId = sessao.Id,
                                Autor = "servidor",
                                Tick = relato.Tick,
                                TickLiberado = sessao.TickLiberado(),
                                TodosPausados = sessao.TodosPausados(),
                                VelocidadeAcordada = sessao.Velocidade,
                                QuemMudou = sessao.QuemMudouOTempo,
                                VersaoDoTempo = sessao.VersaoDoTempo,
                                VelocidadeDesdePasso = sessao.VelocidadeDesdePasso,
                                Pausado = sessao.Pausados.Count > 0,
                            };
                        }

                        // **Qual parte divergiu**, não só "divergiu".
                        //
                        // O resumo carrega as partes nomeadas — modo de
                        // arredondamento, RNG por mapa, do mundo, dos comandos.
                        // Cada uma aponta para um lugar diferente, e dizer qual
                        // foi poupa a rodada de comparar rastreio à mão.
                        string ondeDiferiu =
                            OpiniaoDeSincronia.DiferencaEntreResumos(
                                relato.Fingerprint, doOutro)
                            ?? "(partes não reconhecidas)";

                        // Quanto durou a paz depois do último ponto de junção.
                        // É o que separa divergência de estado (volta tarde, o
                        // recarregamento resolveu) de divergência de
                        // comportamento (volta em alguns passos, partindo de
                        // estados idênticos — e aí recarregar não resolve nada).
                        string depoisDoPonto = sessao.PassoDoUltimoPonto < 0
                            ? ""
                            : $" Voltou {relato.Tick - sessao.PassoDoUltimoPonto} passo(s) depois " +
                              $"do ponto de junção — " +
                              (relato.Tick - sessao.PassoDoUltimoPonto <= 64
                                  ? "os dois partiram de estados idênticos e se afastaram de novo, " +
                                    "então é a SIMULAÇÃO que difere, não o estado."
                                  : "o estado restaurado durou; esta é outra causa.");

                        string relatorio =
                            $"Ticks suspeitos: {string.Join(", ", sessao.TicksSuspeitos)}. " +
                            $"No tick {relato.Tick} divergiu em — {ondeDiferiu}. " +
                            $"Último ponto consistente: {sessao.UltimoTickValido}.{depoisDoPonto}";

                        sessao.RelatoriosDeDivergencia.Add(relatorio);

                        // **Ressincronizar em vez de abortar.**
                        //
                        // Abortar transformava toda causa desconhecida em "perdeu
                        // o encontro" — e causa desconhecida só aparece jogando.
                        // Refazendo o ponto de junção, uma visita rende vários
                        // relatórios em vez de um, e as causas restantes aparecem
                        // em lote.
                        if (sessao.Ressincronizacoes < RessincronizacoesPorSessao)
                            return Ressincronizar(sessao, relatorio);

                        return Abortar(sessao, MotivoFimDeSessao.Desync,
                            $"Os dois lados divergiram {sessao.Ressincronizacoes + 1} vezes e " +
                            $"refazer o ponto de junção não resolveu. " + relatorio + " " +
                            "A colônia dos dois volta ao checkpoint pré-sessão — " +
                            "perde-se o encontro, nunca a colônia.\n" +
                            "Divergências desta sessão:\n  " +
                            string.Join("\n  ", sessao.RelatoriosDeDivergencia));
                    }

                    // Bateu: o que estava divergindo se resolveu.
                    if (sessao.DivergenciasSeguidas > 0)
                    {
                        Console.WriteLine(
                            $"   sessão {sessao.Id}: digitais voltaram a bater no tick {relato.Tick} " +
                            $"(eram {sessao.DivergenciasSeguidas} divergência(s) seguidas)");
                        sessao.DivergenciasSeguidas = 0;
                        sessao.TicksSuspeitos.Clear();
                    }

                    sessao.UltimoTickValido = Math.Max(sessao.UltimoTickValido, relato.Tick);
                }
            }

            return new SessaoBarreira
            {
                SessaoId = sessao.Id,
                Autor = "servidor",
                Tick = relato.Tick,
                TickLiberado = sessao.TickLiberado(),
                TodosPausados = sessao.TodosPausados(),
                VelocidadeAcordada = sessao.Velocidade,
                QuemMudou = sessao.QuemMudouOTempo,
                VersaoDoTempo = sessao.VersaoDoTempo,
                VelocidadeDesdePasso = sessao.VelocidadeDesdePasso,
                DespausarRecusado = despausarRecusado,
                Pausado = sessao.Pausados.Count > 0,
            };
        }
    }

    public SessaoFim Encerrar(string sessaoId, MotivoFimDeSessao motivo, string explicacao)
    {
        lock (trava)
        {
            if (sessoes.TryGetValue(sessaoId, out var sessao))
                sessao.Estado = EstadoSessao.Encerrada;

            return new SessaoFim { SessaoId = sessaoId, Motivo = motivo, Explicacao = explicacao };
        }
    }

    /// <summary>Alguém caiu: a sessão acaba, e cada lado faz commit do que tem.</summary>
    public SessaoFim? ParticipanteSaiu(string playerId)
    {
        lock (trava)
        {
            var sessao = sessoes.Values.FirstOrDefault(s => s.Estado == EstadoSessao.Ativa && s.Tem(playerId));
            if (sessao == null) return null;

            sessao.Estado = EstadoSessao.Encerrada;
            return new SessaoFim
            {
                SessaoId = sessao.Id,
                Motivo = MotivoFimDeSessao.ParticipanteDesconectou,
                Explicacao = $"{playerId} desconectou. A sessão terminou no tick {sessao.UltimoTickValido}.",
            };
        }
    }

    /// <summary>
    /// Relato que chega enquanto os dois refazem o ponto de junção.
    ///
    /// <para>Quem já chegou no tick novo fica registrado; quem ainda não chegou
    /// recebe o pedido de novo — a mensagem é idempotente de propósito, para que
    /// um lado que a perdeu no meio de um recarregamento não deixe a sessão
    /// pendurada para sempre.</para>
    ///
    /// <para>Com os dois no tick novo, a visita volta a andar. Nada de digital
    /// aqui: as digitais do passo velho já foram apagadas, e comparar contra
    /// elas acusaria divergência na hora.</para>
    /// </summary>
    IMessage RelatoDurantePonto(Sessao sessao, string autor, SessaoBarreira relato)
    {
        long alvo = sessao.UltimoTickValido;

        if (relato.Tick >= alvo)
            sessao.TickPorParticipante[autor] = relato.Tick;

        bool osDoisChegaram = sessao.TickPorParticipante.Count >= 2;
        if (!osDoisChegaram)
        {
            // Quem já chegou não recebe o pedido de novo. A resposta é
            // difundida para os dois, e repetir "ressincronize" para quem acabou
            // de voltar é pedir que ele recomece — laço.
            //
            // O reenvio existe para o lado que perdeu a mensagem no meio do
            // recarregamento, e esse é exatamente o que ainda não relatou.
            if (relato.Tick >= alvo) return null;

            return new SessaoRessincronizar
            {
                SessaoId = sessao.Id,
                Explicacao = "aguardando o outro lado terminar de recarregar",
                TickAlvo = alvo,
                Numero = sessao.Ressincronizacoes,
            };
        }

        sessao.Estado = EstadoSessao.Ativa;
        Console.WriteLine(
            $"~~ sessão {sessao.Id}: ponto de junção refeito no tick {alvo} — a visita continua");

        return new SessaoBarreira
        {
            SessaoId = sessao.Id,
            Autor = "servidor",
            Tick = relato.Tick,
            TickLiberado = sessao.TickLiberado(),
            TodosPausados = sessao.TodosPausados(),
            VelocidadeAcordada = sessao.Velocidade,
            QuemMudou = sessao.QuemMudouOTempo,
            VersaoDoTempo = sessao.VersaoDoTempo,
            VelocidadeDesdePasso = sessao.VelocidadeDesdePasso,
            Pausado = sessao.Pausados.Count > 0,
        };
    }

    /// <summary>
    /// Manda os dois lados refazerem o ponto de junção e congela a barreira até
    /// eles voltarem.
    ///
    /// <para>Enquanto a sessão está <see cref="EstadoSessao.Ressincronizando"/>,
    /// nada é liberado e nenhum comando é carimbado: o estado para o qual os
    /// dois vão voltar ainda está viajando, e carimbar contra o passo antigo
    /// produziria comando para um tick que deixou de existir.</para>
    /// </summary>
    SessaoRessincronizar Ressincronizar(Sessao sessao, string relatorio)
    {
        sessao.Ressincronizacoes++;
        sessao.Estado = EstadoSessao.Ressincronizando;

        long alvo = sessao.UltimoTickValido;
        sessao.RecomecarEm(alvo);

        Console.WriteLine(
            $"~~ sessão {sessao.Id}: ressincronizando ({sessao.Ressincronizacoes}/" +
            $"{RessincronizacoesPorSessao}) a partir do tick {alvo} — {relatorio}");

        return new SessaoRessincronizar
        {
            SessaoId = sessao.Id,
            Explicacao = relatorio,
            TickAlvo = alvo,
            Numero = sessao.Ressincronizacoes,
        };
    }

    SessaoAborto Abortar(Sessao sessao, MotivoFimDeSessao motivo, string explicacao)
    {
        sessao.Estado = EstadoSessao.Encerrada;
        return new SessaoAborto
        {
            SessaoId = sessao.Id,
            Motivo = motivo,
            Explicacao = explicacao,
            UltimoTickValido = sessao.UltimoTickValido,
        };
    }

    static SessaoRecusa Recusa(string conviteId, string explicacao) =>
        new() { ConviteId = conviteId, Explicacao = explicacao };
}
