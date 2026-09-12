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
    /// Prepara a instância. Chamado na subida do mod.
    ///
    /// <para>Volume zero porque som sorteia (grão, volume) e, mesmo isolado do
    /// gerador da sessão, é trabalho que um árbitro não precisa fazer.</para>
    /// </summary>
    public static void Preparar()
    {
        if (!Ativo) return;

        Prefs.VolumeGame = 0;
        Prefs.VolumeMusic = 0;
        Prefs.VolumeAmbient = 0;

        Log.Message(
            "[WithFriends] modo árbitro: esta instância simula e não desenha. " +
            "Ela entra na visita como visitante e só compara digitais.");
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

        string argumentos =
            $"-batchmode -nographics -arbitro -arbitrosave=\"{cfg.arbitroSave}\" " +
            $"-savedatafolder=\"{pasta}\" -logFile \"{log}\"";

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
/// O árbitro carrega a colônia dele sozinho.
///
/// <para>Aceitar uma visita exige estar <b>dentro de uma partida</b>: o
/// visitante congela a própria colônia, cria o ponto de retorno e publica o
/// assentamento dele. Uma instância em <c>-batchmode</c> sobe no menu principal
/// e ficaria ali para sempre.</para>
///
/// <para>Então <c>-arbitrosave=NOME</c> diz qual save abrir. Deve ser a colônia
/// do visitante humano — mesmo planeta, mesma semente — senão o coordenador
/// recusa com "planeta divergente" e o experimento nem começa.</para>
/// </summary>
[HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI))]
public static class ArbitroCarregaSozinho
{
    static bool jaTentou;

    [HarmonyPostfix]
    public static void Depois()
    {
        if (!ModoArbitro.Ativo || jaTentou) return;
        jaTentou = true;

        if (!GenCommandLine.TryGetCommandLineArg("arbitrosave", out string nome) ||
            string.IsNullOrWhiteSpace(nome))
        {
            Log.Warning(
                "[WithFriends] modo árbitro sem -arbitrosave=NOME: não há colônia para " +
                "entrar na visita, e esta instância não vai fazer nada.");
            return;
        }

        Log.Message($"[WithFriends] árbitro abrindo a colônia \"{nome}\"…");
        LongEventHandler.QueueLongEvent(
            () => GameDataSaveLoader.LoadGame(nome), "LoadingLongEvent", true, null);
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
        if (!ModoArbitro.Ativo) return true;

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
        yield return AccessTools.Method(typeof(WaterInfo), nameof(WaterInfo.SetTextures));
        yield return AccessTools.Method(typeof(PortraitsCache), nameof(PortraitsCache.Get));
        yield return AccessTools.Method(typeof(SubcameraDriver), nameof(SubcameraDriver.UpdatePositions));
    }

    [HarmonyPrefix]
    public static bool Antes() => !ModoArbitro.Ativo;
}
