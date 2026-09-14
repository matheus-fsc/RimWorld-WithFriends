using System;
using HarmonyLib;
using UnityEngine;
using Verse;
using WithFriends.Client.Session;

namespace WithFriends.Client.Bancada;

/// <summary>
/// Rodar uma colônia sem ninguém olhando, contar o que aconteceu, e sair.
///
/// <para><b>O que isto não tem a ver com visita.</b> Nada. Não há coordenador,
/// não há sessão, não há barreira, e o gerador de números não é isolado: é o
/// RimWorld normal, rodando sozinho. Os remendos de determinismo todos ficam
/// desligados, porque todos eles perguntam antes se há visita em andamento.</para>
///
/// <para><b>Por que existe mesmo assim.</b> Tudo o que foi preciso descobrir
/// para o <see cref="ModoEmulacao"/> funcionar — a lista do que cancelar sem
/// placa de vídeo, o gancho certo para carregar um save sem menu, impedir que
/// uma carta pause a corrida — não é sobre multijogador. É sobre <b>rodar o
/// jogo sem interface e medir</b>, que serve a qualquer mod:</para>
///
/// <list type="bullet">
/// <item>teste de regressão: "minha colônia roda 20 mil ticks sem erro?"</item>
/// <item>medição: quantos ticks por segundo, e quanto isso piora com o mod</item>
/// <item>reprodução: o mesmo save, o mesmo número de ticks, sem clicar</item>
/// </list>
///
/// <para>Este arquivo mora fora de <c>Session/</c> de propósito: é a costura por
/// onde a bancada se separa do mod, se algum dia valer a pena publicá-la.</para>
///
/// <para><b>O que ele não faz.</b> Não gera mundo nem colônia: precisa de um
/// save pronto. Gerar exigiria dirigir a tela de criação, que é justamente o
/// que uma instância sem interface não tem.</para>
///
/// <code>
/// RimWorldLinux -batchmode -nographics \
///     -rodar -rodarsave=MinhaColonia -rodarticks=20000 \
///     -logFile /tmp/corrida.log
/// </code>
/// </summary>
public static class ModoAvulso
{
    public static readonly bool Ativo = GenCommandLine.CommandLineArgPassed("rodar");

    /// <summary>
    /// Quantos ticks de jogo rodar. <b>Ticks, não segundos</b>: é a unidade que
    /// se repete entre máquinas e entre execuções, e a que faz duas corridas
    /// serem comparáveis.
    /// </summary>
    static int Ticks =>
        GenCommandLine.TryGetCommandLineArg("rodarticks", out string t) &&
        int.TryParse(t, out int v) ? v : 10_000;

    /// <summary>
    /// A velocidade que a bancada mantém.
    ///
    /// <para>Ela é reescrita a cada quadro, porque incidente, carta e
    /// recarregamento pausam e aqui não há quem retome. Mas isso atropelava
    /// quem dirige de fora: <c>velocidade Ultrafast</c> pela porta de controle
    /// respondia <c>ok</c> e o quadro seguinte punha de volta o valor da linha
    /// de comando.</para>
    ///
    /// <para>Então quem manda pela porta muda o <b>alvo</b>, não o relógio. O
    /// quadro continua garantindo que ninguém pause sozinho — só que agora
    /// garantindo a velocidade que foi pedida por último.</para>
    /// </summary>
    public static TimeSpeed Velocidade { get; set; } =
        GenCommandLine.TryGetCommandLineArg("rodarvelocidade", out string v)
        && Enum.TryParse<TimeSpeed>(v, ignoreCase: true, out var escolhida)
            ? escolhida
            : TimeSpeed.Superfast;

    static int tickInicial = -1;
    static float comecouEm;
    static bool encerrando;

    /// <summary>Chamado a cada quadro, enquanto houver partida.</summary>
    public static void Acompanhar()
    {
        if (!Ativo || encerrando) return;
        if (Current.Game == null || Find.TickManager == null) return;

        if (tickInicial < 0)
        {
            tickInicial = Find.TickManager.TicksGame;
            comecouEm = Time.realtimeSinceStartup;
            Log.Message(
                $"[WithFriends/bancada] colônia de pé no tick {tickInicial} — " +
                $"rodando {Ticks} tick(s) em {Velocidade}.");
        }

        // Despausar sempre: um incidente, uma carta ou um recarregamento podem
        // pausar, e aqui não há ninguém para retomar.
        if (Find.TickManager.CurTimeSpeed != Velocidade)
            Find.TickManager.CurTimeSpeed = Velocidade;

        int andados = Find.TickManager.TicksGame - tickInicial;
        if (andados < Ticks) return;

        Encerrar(andados, Time.realtimeSinceStartup - comecouEm);
    }

    static void Encerrar(int andados, float segundos)
    {
        encerrando = true;

        Log.Message(
            $"[WithFriends/bancada] fim: {andados} tick(s) em {segundos:F1}s " +
            $"= {andados / Math.Max(segundos, 0.001f):F1} tick(s)/s " +
            $"({andados / 60f / Math.Max(segundos, 0.001f):F2}x do tempo real)");

        Log.Message(
            $"[WithFriends/bancada] {DiarioDaInstancia.Erros} erro(s) e " +
            $"{DiarioDaInstancia.Avisos} aviso(s) durante a corrida " +
            "— qualquer um deles é motivo para olhar o diário.");

        RetratoDaPartida.Registrar("fim da corrida");

        Log.Message($"[WithFriends/bancada] diário: {DiarioDaInstancia.Caminho}");
        DiarioDaInstancia.Fechar();

        Root.Shutdown();
    }
}

/// <summary>
/// A corrida avulsa abre o save dela sozinha, pelo mesmo gancho da emulação —
/// <c>Root_Entry.Update</c>, porque sem interface o menu principal nunca roda.
/// </summary>
[HarmonyPatch(typeof(Root_Entry), nameof(Root_Entry.Update))]
public static class CorridaAvulsaCarregaSozinha
{
    static bool jaTentou;

    [HarmonyPostfix]
    public static void Depois()
    {
        if (!ModoAvulso.Ativo || jaTentou) return;
        if (LongEventHandler.AnyEventNowOrWaiting) return;

        jaTentou = true;

        if (!GenCommandLine.TryGetCommandLineArg("rodarsave", out string nome) ||
            string.IsNullOrWhiteSpace(nome))
        {
            Log.Error(
                "[WithFriends/bancada] -rodar sem -rodarsave=NOME: não há colônia para rodar. " +
                "A bancada não gera mundo — ela roda um save que já existe.");
            Root.Shutdown();
            return;
        }

        Log.Message($"[WithFriends/bancada] abrindo a colônia \"{nome}\"…");
        LongEventHandler.QueueLongEvent(
            () => GameDataSaveLoader.LoadGame(nome), "LoadingLongEvent", true, null);
    }
}
