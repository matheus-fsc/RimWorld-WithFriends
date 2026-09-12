using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;

/// <summary>
/// Durante a visita, quem manda no tick é a barreira — não o relógio local.
///
/// <para><b>O problema que isto resolve.</b> No vanilla, cada quadro converte
/// tempo real em ticks conforme a velocidade escolhida pelo jogador. Numa
/// visita isso põe os dois lados em ticks diferentes o tempo todo, e pausados
/// eles congelam <b>separados</b> — medido, um no tick 972 e o outro no 952,
/// exatamente uma pista de distância. Dali saem ordens que não funcionam com o
/// jogo parado e toda a família de bugs de "um lado à frente do outro".</para>
///
/// <para><b>O que muda.</b> O coordenador mantém um relógio, avança no ritmo
/// que os dois pediram, e publica até onde é seguro simular. O cliente corre
/// até lá o mais rápido que consegue. Os dois ficam sempre no mesmo tick, e a
/// velocidade local vira <b>pedido</b>, não comando — o mais lento manda (§3).
/// A pausa deixa de ser regra à parte: é multiplicador zero.</para>
///
/// <para>É o desenho do Multiplayer, e a razão de lá os clientes não
/// derivarem.</para>
///
/// <para>Fora de sessão o vanilla roda intacto.</para>
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
public static class TickGuiadoPelaBarreira
{
    /// <summary>
    /// Teto de tempo gasto tickando por quadro.
    ///
    /// Ficar para trás é ruim, mas travar a interface é pior: sem isto, um
    /// cliente atrasado tentaria alcançar o relógio de uma vez e pareceria
    /// congelado. Ele alcança em alguns quadros, e a pista existe justamente
    /// para absorver isso.
    /// </summary>
    const double OrcamentoPorQuadroMs = 45.0;

    /// <summary>
    /// Quantos ticks atrás do relógio dá para ficar antes de correr atrás
    /// ignorando o ritmo local.
    ///
    /// Um engasgo de quadro não pode virar atraso permanente: quem fica para
    /// trás segura o relógio dos dois, porque ele nunca se afasta do mais lento.
    /// </summary>
    const int AtrasoQueJustificaCorrer = 4;

    /// <summary>
    /// Teto do orçamento acumulado, em ticks.
    ///
    /// Sem teto, pedir Superfast enquanto o relógio libera 1× acumularia crédito
    /// e o jogo dispararia em rajada quando a liberação chegasse.
    /// </summary>
    const double TetoDoOrcamento = 3.0;

    static readonly FieldInfo? TicksNesteQuadro =
        AccessTools.Field(typeof(TickManager), "ticksThisFrame");

    /// <summary>Ticks que este jogador já "pagou" com tempo real e ainda não gastou.</summary>
    static double orcamento;

    public static void Zerar() => orcamento = 0;

    /// <summary>
    /// Dá para tickar agora sem risco?
    ///
    /// <para><b>Por que isto existe.</b> O vanilla não chama
    /// <c>TickManagerUpdate</c> em qualquer momento — ele tem guardas em volta,
    /// e ao substituir o laço eu fiquei com o laço e sem as guardas. O
    /// resultado foi tickar um mapa que estava sendo desmontado:</para>
    ///
    /// <code>
    /// Could not regenerate layer Verse.SectionLayer_FogOfWar: NullReferenceException
    ///   at Unity.Collections.LowLevel.Unsafe.UnsafeBitArray.IsSet
    /// …
    /// Got a SIGSEGV while executing native code
    /// </code>
    ///
    /// <para>Camada de mapa lendo estrutura nativa que já foi liberada. Depois
    /// disso o processo morre no coletor, e o rastro não aponta para cá.</para>
    ///
    /// <para>A troca de partida da visita é exatamente esse momento: a partida
    /// antiga sai, a nova entra, e entre as duas existe um intervalo em que
    /// tickar não faz sentido.</para>
    /// </summary>
    static bool PodeTickarComSeguranca()
    {
        if (Current.ProgramState != ProgramState.Playing) return false;
        if (Current.Game == null) return false;
        if (NaInterface.CarregandoAlgo) return false;

        if (SessaoCliente.TrocandoDePartida) return false;

        return Find.Maps is { Count: > 0 };
    }

    [HarmonyPrefix]
    public static bool Antes(TickManager __instance)
    {
        if (!RelogioDeSessaoRimWorld.EmSessao) return true;

        Escrever(__instance, 0);

        // Nada de tickar enquanto o mundo não está inteiro. Não devolve para o
        // vanilla: ele tickaria no ritmo local, e o ritmo não é dele.
        if (!PodeTickarComSeguranca()) return false;

        // **Previsão local.** O relógio compartilhado diz até onde é seguro
        // simular; quanto disso este jogador consome agora é dele.
        //
        // Isso é o que faz pausa e velocidade responderem na hora. Antes o
        // clique só valia depois de ir ao servidor e voltar — funcionava, mas
        // parecia emperrado, porque o jogo continuava andando por um instante
        // depois de a pessoa mandar parar.
        //
        // É seguro porque só **reduz**: parar ou ir devagar significa simular
        // menos do que já foi liberado, nunca mais. Acelerar continua exigindo a
        // volta do servidor — não dá para simular o que ainda não foi liberado —
        // mas isso é uma ida e volta, e não se sente.
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando }) return false;

        // `Paused`, não `CurTimeSpeed == Paused`:
        //
        //     public bool Paused => curTimeSpeed == 0 || ForcePaused;
        //
        // `ForcePaused` é a pausa que uma janela impõe — a carta de um raid, um
        // diálogo. Olhando só o botão, o relógio aparecia pausado enquanto o
        // jogo continuava tickando.
        // **Se um passo simula ou não é decisão COMPARTILHADA.**
        //
        // Estava local (`__instance.Paused`), e isso desincronizou na primeira
        // vez que os dois lados discordaram sobre estar pausados:
        //
        //     passo 404  J1 +0 sorteios   J2 +12
        //     passo 405  J1 +0 sorteios   J2 +11
        //
        // Mesmo número de passo, um simulando e o outro não. Com dois relógios,
        // o passo é o contrato: ou os dois simulam aquele passo, ou nenhum.
        // Quem decide é a velocidade acordada, que vem do coordenador.
        // A velocidade que vale para o **próximo passo** — não a que vale agora.
        bool acordadoPausado = !sessao.PassoSimula(sessao.TickDeSessao);
        bool pausadoAqui = __instance.Paused;

        // Previsão local da pausa: parar de consumir passos até o coordenador
        // responder. Parar é sempre seguro — simular menos do que foi liberado
        // nunca sai na frente de ninguém. O que **não** é seguro é andar o passo
        // de um jeito diferente do outro lado.
        //
        // **Ela dura só até a resposta chegar**, e essa é a parte que faltava.
        //
        // Sem o limite, o palpite virava impasse: o jogador pausa no passo 2014,
        // o coordenador concorda a partir do 2016, e os comandos dados parado
        // nascem para o 2016. Para chegar lá é preciso andar o 2014 e o 2015 —
        // que a velocidade acordada diz que simulam. Segurando esses dois passos
        // para sempre, a construção só aparecia quando alguém despausava. Eram
        // dez paredes esperando dois ticks de 16 ms.
        //
        // Depois da resposta, quem decide é a velocidade acordada, passo a
        // passo: os passos anteriores à pausa simulam (não dá para ver), e do
        // passo da pausa em diante o relógio anda sem simular — que é o que faz
        // ordem dada com o jogo parado acontecer.
        if (pausadoAqui && !acordadoPausado)
        {
            if (sessao.EsperandoRespostaDoTempo)
            {
                orcamento = 0;
                return false;
            }

            // A resposta chegou e o passo simula assim mesmo: são os passos
            // entre o clique e o passo em que a pausa passou a valer. Eles não
            // pedem licença ao relógio local — o orçamento aqui é zero, porque o
            // botão local diz "parado", e esperar por ele seria esperar para
            // sempre.
            ConsumirPassos(__instance, sessao, semLimiteDeRitmo: true);
            return false;
        }

        if (acordadoPausado)
        {
            // Parado para os dois: os passos andam, ninguém simula, e é isso
            // que faz uma ordem dada com o jogo parado acontecer.
            orcamento = 0;
            ConsumirPassos(__instance, sessao, semLimiteDeRitmo: true);
            return false;
        }

        long atras = RelogioDeSessaoRimWorld.LimiteDeTick - sessao.TickDeSessao;
        bool correndoAtras = atras > AtrasoQueJustificaCorrer;

        orcamento += Time.deltaTime * GenTicks.TicksPerRealSecond
                     * RitmoDaSessao.Multiplicador(
                         (VelocidadeDeSessao)(byte)__instance.CurTimeSpeed);
        if (orcamento > TetoDoOrcamento) orcamento = TetoDoOrcamento;

        ConsumirPassos(__instance, sessao, semLimiteDeRitmo: correndoAtras);

        if (orcamento < 0) orcamento = 0;
        return false;   // o vanilla não roda: o ritmo não é dele
    }

    /// <summary>
    /// Anda os passos liberados. <paramref name="simular"/> decide se cada
    /// passo roda um tick de jogo ou só aplica os comandos dele.
    /// </summary>
    static void ConsumirPassos(
        TickManager gerenciador, SessaoCliente sessao, bool semLimiteDeRitmo)
    {
        var cronometro = Stopwatch.StartNew();
        int feitos = 0;
        long passoAntes = sessao.TickDeSessao;
        bool estourouOrcamento = false;
        bool simulouAlgum = false;

        while (sessao.TickDeSessao < RelogioDeSessaoRimWorld.LimiteDeTick)
        {
            // Decidido **por passo**: uma mudança de velocidade no meio do laço
            // vale a partir do passo dela, igual nos dois lados.
            bool simular = sessao.PassoSimula(sessao.TickDeSessao);

            // O orçamento é o ritmo local, e vale só para simulação: aplicar
            // comando não consome tempo de jogo nenhum.
            if (simular && !semLimiteDeRitmo && orcamento < 1.0) break;

            long antesDoPasso = sessao.TickDeSessao;
            sessao.ExecutarPasso(simular);
            feitos++;
            if (simular) { orcamento -= 1.0; simulouAlgum = true; }

            // Passo que não anda é laço que não termina. Acontece se
            // `ExecutarPasso` desistir no meio — por exemplo, se o estado da
            // sessão mudar debaixo do laço. Sem esta saída, o quadro inteiro
            // seria gasto girando e o jogo pareceria travado.
            if (sessao.TickDeSessao == antesDoPasso)
            {
                Log.Warning(
                    $"[WithFriends] passo {antesDoPasso} não avançou — saindo do laço para não travar o quadro");
                break;
            }

            if (cronometro.Elapsed.TotalMilliseconds > OrcamentoPorQuadroMs)
            {
                estourouOrcamento = true;
                break;
            }
        }

        // Um quadro que gasta o orçamento inteiro, quadro após quadro, é a
        // diferença entre "está correndo atrás" e "travou". Sem este aviso, os
        // dois têm exatamente a mesma cara para quem está jogando.
        if (estourouOrcamento) AvisarSeInsistir(sessao, feitos, passoAntes);
        else quadrosSeguidosNoTeto = 0;

        Escrever(gerenciador, simulouAlgum ? feitos : 0);
    }

    static int quadrosSeguidosNoTeto;
    static float ultimoAviso;

    static void AvisarSeInsistir(SessaoCliente sessao, int feitos, long passoAntes)
    {
        quadrosSeguidosNoTeto++;
        if (quadrosSeguidosNoTeto < 30) return;
        if (Time.realtimeSinceStartup - ultimoAviso < 2f) return;

        ultimoAviso = Time.realtimeSinceStartup;
        Log.Warning(
            $"[WithFriends] {quadrosSeguidosNoTeto} quadros seguidos gastando o orçamento inteiro: " +
            $"passo {passoAntes} → {sessao.TickDeSessao} ({feitos} no último quadro), " +
            $"liberado até {RelogioDeSessaoRimWorld.LimiteDeTick}. " +
            "Ou a simulação está pesada demais, ou o laço não está andando.");
    }

    static void Escrever(TickManager gerenciador, int valor)
    {
        try { TicksNesteQuadro?.SetValue(gerenciador, valor); }
        catch (System.Exception) { /* estatística de interface; o catálogo avisa */ }
    }
}
