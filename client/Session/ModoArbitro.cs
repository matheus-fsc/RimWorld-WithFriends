// Adaptado de Source/Client/Patches/ArbiterPatches.cs e MultiplayerStatic.cs de
// rwmt/Multiplayer, MIT, Copyright (c) 2018 Zetrith.
// Ver THIRD_PARTY/Multiplayer-MIT.txt

using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Uma instância que simula e <b>não desenha</b>.
///
/// <para><b>A pergunta que sobrou.</b> Depois de seis divergências no mesmo
/// <c>DropBloodFilth</c>, tudo que dá para medir bate entre os dois lados: taxa
/// de sangramento bit a bit, corpo, postura, hediffs, ritmo, fase do ciclo,
/// delta da saúde, ordem de tick dos pawns e a posição no gerador. E mesmo assim
/// um larga sangue e o outro não.</para>
///
/// <para>Sobrou uma pergunta que nenhum campo novo responde: <b>qual dos dois
/// desvia?</b> Com dois jogadores, digitais diferentes não dizem quem errou.</para>
///
/// <para><b>O que o Multiplayer faz.</b> Lança uma terceira instância do jogo com
/// <c>-batchmode -nographics -arbiter</c> — o árbitro — que simula junto e serve
/// de desempate. Ele não tem interface, então não tem câmera, mouse, janela em
/// foco nem cache preenchido por desenho.</para>
///
/// <para><b>O que fazemos diferente, e é o que barateia.</b> Ele precisa de um
/// terceiro participante porque a partida dele é de N jogadores. A nossa visita
/// é de dois por construção — anfitrião e visitante — e generalizar isso seria a
/// maior parte do trabalho, no coordenador, no protocolo e nos testes.</para>
///
/// <para>Não precisa. O que se quer saber é se uma simulação <b>sem interface</b>
/// concorda com o anfitrião ou com o visitante. Então o árbitro entra na visita
/// como <b>visitante</b>, na vaga que já existe:</para>
///
/// <list type="bullet">
/// <item>anfitrião humano × árbitro diverge → a interface do anfitrião está
/// implicada;</item>
/// <item>nunca diverge → a interface do <b>visitante</b> humano estava, e é ali
/// que se procura;</item>
/// <item>diverge do mesmo jeito → não é interface nenhuma, e a causa é outra —
/// o que também é resposta, e fecha a família inteira.</item>
/// </list>
///
/// <para>Zero mudança de protocolo. A sessão continua de dois.</para>
/// </summary>
public static class ModoArbitro
{
    /// <summary>
    /// Ligado por <c>-arbitro</c> na linha de comando.
    ///
    /// <para>Lido uma vez: <c>GenCommandLine</c> varre os argumentos a cada
    /// chamada, e isto é consultado em caminho de desenho.</para>
    /// </summary>
    public static readonly bool Ativo =
        GenCommandLine.CommandLineArgPassed("arbitro");

    /// <summary>
    /// Esta instância roda sem ninguém na frente — árbitro ou emulação.
    ///
    /// <para>Os remendos de "não desenhar" valem para as duas: a emulação também
    /// sobe com <c>-nographics</c>, e o jogo assume textura nos mesmos lugares.</para>
    /// </summary>
    public static bool SemNinguemNaFrente => Ativo || ModoEmulacao.Ativo;

    /// <summary>
    /// Prepara a instância. Chamado na subida do mod.
    ///
    /// <para>Volume zero porque som sorteia (grão, volume) e, mesmo isolado do
    /// gerador da sessão, é trabalho que um árbitro não precisa fazer.</para>
    /// </summary>
    public static void Preparar()
    {
        if (SemNinguemNaFrente) EmJanelaMetadeDaTela();

        if (!Ativo) return;

        Prefs.VolumeGame = 0;
        Prefs.VolumeMusic = 0;
        Prefs.VolumeAmbient = 0;

        Log.Message(
            "[WithFriends] modo árbitro: esta instância simula e não desenha. " +
            "Ela entra na visita como visitante e só compara digitais.");
    }

    /// <summary>
    /// Meia tela, em janela — para as duas instâncias caberem lado a lado.
    ///
    /// <para><b>Por que não basta <c>-screen-fullscreen 0</c>.</b> O argumento do
    /// Unity é aplicado e logo em seguida sobrescrito: o RimWorld chama
    /// <c>Screen.SetResolution</c> com o que está nas preferências dele, e o que
    /// estava lá era tela cheia. Medido — as duas janelas abriam em 1920×1200,
    /// uma em cima da outra.</para>
    ///
    /// <para>Mexer aqui é seguro porque <c>Prefs.Save</c> está cancelado nas
    /// instâncias automáticas: a preferência do jogador no disco não é tocada.</para>
    ///
    /// <para>Posição é outra história — o Unity não tem argumento para isso, e
    /// quem posiciona é o gerenciador de janelas. A CLI tenta com
    /// <c>xdotool</c> depois que as duas abrem, em melhor esforço.</para>
    /// </summary>
    static void EmJanelaMetadeDaTela()
    {
        if (!GenCommandLine.CommandLineArgPassed("emulacaovisivel")) return;

        try
        {
            int largura = Display.main.systemWidth / 2;
            int altura = Display.main.systemHeight - 80;

            Prefs.FullScreen = false;
            Screen.SetResolution(largura, altura, FullScreenMode.Windowed);

            Log.Message($"[WithFriends] janela de emulação: {largura}×{altura}.");
        }
        catch (Exception e)
        {
            Log.Warning($"[WithFriends] não consegui ajustar a janela: {e.Message}");
        }
    }

    /// <summary>
    /// O anfitrião esperando o árbitro aparecer para convidá-lo.
    ///
    /// <para>Não dá para convidar e lançar ao mesmo tempo: o convite exige o
    /// outro <b>online</b> (§11, sessão exige os dois presentes). Então a ordem
    /// é lançar, esperar aparecer, e só então convidar.</para>
    /// </summary>
    public static bool Esperando { get; private set; }

    static float desistirEm;

    /// <summary>Quanto se espera o árbitro subir. Carregar uma colônia leva uns segundos.</summary>
    const float SegundosParaSubir = 90f;

    /// <summary>
    /// Lança a instância do árbitro e passa a esperar por ela.
    ///
    /// <para>O executável é o <b>desta</b> instância — mesmo binário, mesmos
    /// mods, mesma versão. Qualquer outra coisa seria comparar simulações
    /// diferentes, que é o oposto do que o árbitro serve para fazer.</para>
    /// </summary>
    public static bool Lancar()
    {
        if (Ativo)
        {
            Log.Warning("[WithFriends] um árbitro não lança outro árbitro.");
            return false;
        }

        var cfg = WithFriendsMod.Settings;

        // A linha de comando ganha das opções: numa emulação quem escolhe o save
        // é o script, e depender do que está gravado no perfil tornaria o
        // experimento dependente de um estado invisível.
        if (GenCommandLine.TryGetCommandLineArg("arbitrosave", out string daLinha) &&
            !string.IsNullOrWhiteSpace(daLinha))
            cfg.arbitroSave = daLinha;

        if (string.IsNullOrWhiteSpace(cfg.arbitroSave))
        {
            Log.Error(
                "[WithFriends] não há save de árbitro configurado. " +
                "Opções do mod → \"Save do árbitro\" (docs/ARBITRO.md).");
            return false;
        }

        string executavel;
        try
        {
            executavel = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] não consegui descobrir o executável do jogo: {e.Message}");
            return false;
        }

        string pasta = string.IsNullOrWhiteSpace(cfg.arbitroPastaDeDados)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rimworld-arbitro")
            : cfg.arbitroPastaDeDados;

        // Log próprio: o Unity escreve Player.log num caminho fixo que **não**
        // acompanha o -savedatafolder, e sem isto o árbitro apagaria o log do
        // anfitrião — justamente o que se quer ler depois.
        string log = System.IO.Path.Combine(pasta, "Player.log");
        System.IO.Directory.CreateDirectory(pasta);

        // **Sem tela por padrão, com tela quando se quer ver.**
        //
        // Uma emulação cega roda mais rápido e não rouba o foco; uma visível
        // deixa acompanhar o que está sendo testado, que é o que se quer quando
        // a pergunta ainda é "o que está acontecendo?" em vez de "qual tick
        // divergiu?".
        bool visivel = GenCommandLine.CommandLineArgPassed("emulacaovisivel");
        string semTela = visivel ? "" : "-batchmode -nographics ";

        string argumentos =
            semTela + $"-arbitro -arbitrosave=\"{cfg.arbitroSave}\" " +
            $"-savedatafolder=\"{pasta}\" -logFile \"{log}\"" +
            // Mesmo prazo do anfitrião: os dois precisam despejar, e só quem
            // sabe quanto tempo a emulação vai durar é quem a começou.
            (ModoEmulacao.Ativo
                ? $" -emulacaosegundos={ModoEmulacao.DuracaoConfigurada:F0}"
                : "") +
            (visivel ? " -emulacaovisivel" : "");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executavel,
                Arguments = argumentos,
                UseShellExecute = false,
            });
        }
        catch (Exception e)
        {
            Log.Error($"[WithFriends] não consegui lançar o árbitro: {e.Message}");
            return false;
        }

        Esperando = true;
        desistirEm = Time.realtimeSinceStartup + SegundosParaSubir;

        Log.Message(
            $"[WithFriends] árbitro lançado (save \"{cfg.arbitroSave}\", pasta {pasta}). " +
            "Convido assim que ele aparecer online.");
        Messages.Message(
            "With Friends: árbitro subindo… o convite sai sozinho quando ele entrar.",
            MessageTypeDefOf.NeutralEvent, historical: false);
        return true;
    }

    static float arbitroAcabaEm = -1f;

    /// <summary>
    /// O árbitro também tem prazo, e é o mesmo do anfitrião.
    ///
    /// <para>Sem isto, só o anfitrião despejava os rastreios no fim: o árbitro
    /// era morto junto com o processo e o diário dele terminava sem histórico
    /// nenhum — e a comparação, que é a razão de tudo isto existir, não tinha o
    /// que comparar.</para>
    /// </summary>
    public static void AcompanharPrazo()
    {
        if (!Ativo) return;

        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando }) return;

        // Mesma regra do anfitrião: conta da primeira liberação da barreira.
        if (!VisitaEmAndamento.JaComecou) return;

        if (arbitroAcabaEm < 0f)
        {
            // Um pouco depois do anfitrião: se os dois fecharem no mesmo
            // instante, um pode morrer no meio do próprio despejo.
            arbitroAcabaEm = Time.realtimeSinceStartup + ModoEmulacao.DuracaoConfigurada + 3f;
            return;
        }

        if (Time.realtimeSinceStartup < arbitroAcabaEm) return;

        ModoEmulacao.Encerrar(sessao, "árbitro");
    }

    /// <summary>
    /// Chamado a cada quadro pelo componente de sincronização: convida assim que
    /// o árbitro aparecer, ou desiste se ele não subir.
    /// </summary>
    public static void AcompanharSubida(Action convidar, Func<bool> alguemMaisOnline)
    {
        if (!Esperando) return;

        if (alguemMaisOnline())
        {
            Esperando = false;
            Log.Message("[WithFriends] árbitro online — convidando.");
            convidar();
            return;
        }

        if (Time.realtimeSinceStartup < desistirEm) return;

        Esperando = false;
        Log.Error(
            "[WithFriends] o árbitro não apareceu online a tempo. " +
            "Veja o Player.log da pasta dele: save inexistente e planeta divergente " +
            "são as duas causas comuns.");
    }
}

/// <summary>
/// Sem interface gráfica, a <c>GUISkin</c> não existe e o jogo estoura ao
/// desenhar. Um esqueleto vazio basta — nada vai ser visto.
/// </summary>
[HarmonyPatch(typeof(GUI), nameof(GUI.skin), MethodType.Getter)]
public static class ArbitroSemSkin
{
    static GUISkin? vazia;

    [HarmonyPrefix]
    public static bool Antes(ref GUISkin __result)
    {
        if (!ModoArbitro.SemNinguemNaFrente) return true;

        __result = vazia ??= ScriptableObject.CreateInstance<GUISkin>();
        return false;
    }
}

/// <summary>
/// Coisas que só existem para desenhar e estouram sem placa de vídeo.
///
/// <para>O Multiplayer cancela as mesmas. A lista é curta porque
/// <c>-nographics</c> já cuida da maior parte: o que sobra são os pontos em que
/// o jogo assume que existe textura.</para>
/// </summary>
[HarmonyPatch]
public static class ArbitroNaoDesenha
{
    static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        // A lista é a dele, e cada linha tem um motivo que só aparece rodando:
        // a primeira tentativa levou o jogo a passar minutos redesenhando
        // seções de mapa que ninguém ia ver.
        yield return AccessTools.Method(typeof(WaterInfo), nameof(WaterInfo.SetTextures));
        yield return AccessTools.Method(typeof(PortraitsCache), nameof(PortraitsCache.Get));
        yield return AccessTools.Method(typeof(SubcameraDriver), nameof(SubcameraDriver.UpdatePositions));

        // Desenho do mapa: o grosso do trabalho inútil numa instância cega.
        yield return AccessTools.Method(typeof(Map), nameof(Map.MapUpdate));
        yield return AccessTools.Method(typeof(Section), nameof(Section.RegenerateAllLayers));
        yield return AccessTools.Method(typeof(SectionLayer), nameof(SectionLayer.DrawLayer));

        // Medir texto exige fonte, e fonte exige interface.
        yield return AccessTools.Method(typeof(GUIStyle), nameof(GUIStyle.CalcSize));
        yield return AccessTools.Method(typeof(FloatMenuOption), nameof(FloatMenuOption.SetSizeMode));

        // Preferências: a instância automática não pode escrever por cima das
        // do jogador — ela roda com volume zero e outras coisas mexidas.
        yield return AccessTools.Method(typeof(Prefs), nameof(Prefs.Save));
    }

    [HarmonyPrefix]
    public static bool Antes() => !ModoArbitro.SemNinguemNaFrente;
}
