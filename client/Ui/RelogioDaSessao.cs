using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Ui;

/// <summary>
/// Mostra o tempo que está valendo para os dois, ao lado dos controles de tempo.
///
/// <para>Numa visita o relógio é compartilhado: qualquer um dos dois muda, e
/// vale para todos. O botão do RimWorld mostra a velocidade <b>local</b>, e
/// quando o outro jogador mexe há um instante em que os dois não dizem a mesma
/// coisa — além da pausa que uma janela impõe, que aparece no botão sem ser
/// escolha de ninguém.</para>
///
/// <para>Em vez de brigar com o botão do jogo, esta etiqueta diz a verdade em
/// voz alta. Só aparece durante a visita, e só quando há o que esclarecer.</para>
/// </summary>
[HarmonyPatch(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))]
public static class RelogioDaSessao
{
    [HarmonyPostfix]
    public static void Depois(Rect timerRect)
    {
        var sessao = SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: Session.EstadoSessaoLocal.Simulando }) return;

        // Mostrar o **efetivo**, não o acordado.
        //
        // A velocidade acordada chega do servidor e atrasa uma ida e volta;
        // mostrando só ela, a etiqueta ficava parada enquanto o jogo já tinha
        // respondido ao clique — exatamente ao contrário da previsão local, que
        // existe para o clique valer na hora.
        var gerenciador = Find.TickManager;
        bool paradoAqui = gerenciador is { Paused: true };

        // Quem mexeu vem antes do quê: numa visita a pausa é pedido de atenção,
        // e pedido sem remetente não chama ninguém.
        //
        // Enquanto ninguém mexeu, a etiqueta fala do **tempo**, não de pessoa.
        // Antes ela caía no nome do anfitrião, e o próprio anfitrião via o
        // próprio nome como se alguém tivesse feito algo — informação invertida,
        // e sem cor, porque "eu" nunca é destaque.
        string autor = NomeDe(sessao.QuemMudouOTempo);

        string estado = paradoAqui ? "pausado" : Nome(sessao.VelocidadeAcordada);

        string texto = autor.Length > 0
            ? (paradoAqui ? $"{autor} pausou" : $"{autor}: {estado}")
            : $"tempo compartilhado: {estado}";

        if (gerenciador is { ForcePaused: true })
            texto += " (janela aberta aqui)";

        var caixa = new Rect(timerRect.x - 240f, timerRect.y, 236f, timerRect.height);

        Text.Font = GameFont.Tiny;
        Text.Anchor = TextAnchor.MiddleRight;
        GUI.color = Cor(sessao);
        Widgets.Label(caixa, texto);
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
    }

    /// <summary>
    /// Quem mexeu vê discreto; quem não mexeu vê chamativo.
    ///
    /// <para>A etiqueta não é placar, é aviso. Quem apertou a tecla já sabe o
    /// que fez — para ele, cinza. Quem <b>não</b> apertou é justamente quem
    /// precisa reparar que o tempo mudou por decisão do outro, e é o caso que a
    /// pausa existe para atender: alguém viu uma ameaça e pediu atenção.</para>
    ///
    /// <para>O destaque esmaece depois de alguns segundos. Aviso que fica aceso
    /// para sempre vira parte do cenário e deixa de avisar.</para>
    /// </summary>
    static Color Cor(Session.SessaoCliente sessao)
    {
        var discreta = new Color(1f, 1f, 1f, 0.65f);
        if (sessao.QuemMudouOTempo.Length == 0) return discreta;
        if (sessao.QuemMudouOTempo == WithFriendsMod.Settings.PlayerIdOuNovo()) return discreta;

        const float SegundosDeDestaque = 5f;
        float desde = Time.realtimeSinceStartup - sessao.QuandoMudouOTempo;
        float forca = Mathf.Clamp01(1f - desde / SegundosDeDestaque);

        // Mesmo assentada, a cor continua diferente da própria: saber de quem
        // foi a última decisão sobre o tempo é útil depois do susto também.
        var assentada = new Color(1f, 0.92f, 0.55f, 0.60f);
        var chamativa = new Color(1f, 0.85f, 0.20f, 1f);
        return Color.Lerp(assentada, chamativa, forca);
    }

    static string NomeDe(string playerId)
    {
        if (playerId.Length == 0) return "";
        if (playerId == WithFriendsMod.Settings.PlayerIdOuNovo()) return "você";

        return SincronizacaoComponent.Online.TryGetValue(playerId, out string? nome) && nome.Length > 0
            ? nome
            : "o outro";
    }

    static string Nome(VelocidadeDeSessao velocidade) => velocidade switch
    {
        VelocidadeDeSessao.Pausado => "pausado",
        VelocidadeDeSessao.Normal => "1×",
        VelocidadeDeSessao.Rapido => "2×",
        VelocidadeDeSessao.MuitoRapido => "3×",
        _ => "ultra",
    };
}
