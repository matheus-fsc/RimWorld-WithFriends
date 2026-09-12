using System;
using HarmonyLib;
using Verse;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;

/// <summary>
/// Captura a <b>intenção</b> do jogador sobre o tempo, não a mudança de estado.
///
/// <para><b>A diferença, que custou uma rodada de teste.</b> Antes eu comparava
/// <c>CurTimeSpeed</c> com o último valor visto e chamava a diferença de
/// "clique". Isso perde o caso mais comum de frustração: apertar <c>2</c>
/// quando o relógio local já está em 2 — porque o reflexo do servidor o pôs
/// lá — não muda nada localmente e portanto não gerava pedido nenhum, mesmo que
/// o servidor estivesse em outra velocidade.</para>
///
/// <para>Daí o "spamo 2 e não muda": a tecla funcionava só quando, por acaso, o
/// valor local fosse diferente do pedido.</para>
///
/// <para><b>A correção.</b> Todo <i>set</i> de <c>CurTimeSpeed</c> feito pelo
/// jogador é intenção, tenha ou não mudado o valor. O jogo já escreve ali a
/// partir do botão e das teclas 1/2/3, então é o funil certo.</para>
///
/// <para>As escritas do próprio mod — reflexo do servidor, congelamento,
/// retomada — não são intenção, e passam por <see cref="ComoSistema"/>.</para>
/// </summary>
public static class ControleDeVelocidade
{
    static bool comoSistema;

    /// <summary>A última intenção do jogador, esperando para ser relatada.</summary>
    public static TimeSpeed? Pendente { get; private set; }

    /// <summary>
    /// Escreve no relógio sem que isso conte como intenção do jogador.
    ///
    /// Usado por tudo que o mod impõe: o reflexo da velocidade acordada, o
    /// congelamento antes da visita, a retomada. Sem esta marcação, cada
    /// imposição voltaria ao servidor como se fosse um clique — o eco que já
    /// custou três bugs.
    /// </summary>
    public static void ComoSistema(Action escrita)
    {
        bool antes = comoSistema;
        comoSistema = true;
        try { escrita(); }
        finally { comoSistema = antes; }
    }

    public static void Registrar(TimeSpeed velocidade)
    {
        if (comoSistema) return;

        Pendente = velocidade;
        Log.Message($"[WithFriends/tempo] intenção do jogador: {velocidade}");
    }

    /// <summary>Tira a intenção da fila. <c>null</c> se não havia nenhuma.</summary>
    public static TimeSpeed? Consumir()
    {
        var pendente = Pendente;
        Pendente = null;
        return pendente;
    }

    public static void Limpar() => Pendente = null;
}

/// <summary>
/// O funil das teclas <c>1 2 3</c> e dos botões de velocidade, que escrevem na
/// propriedade.
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
public static class IntencaoDeVelocidade
{
    [HarmonyPostfix]
    public static void Depois(TickManager __instance, TimeSpeed value)
    {
        if (!EmSessao()) return;
        ControleDeVelocidade.Registrar(value);
    }

    internal static bool EmSessao() =>
        Colony.SincronizacaoComponent.Atual?.Sessao is { Estado: EstadoSessaoLocal.Simulando };
}

/// <summary>
/// O funil da <b>pausa</b>, que não passa pela propriedade.
///
/// <para>O jogo escreve o campo direto:</para>
///
/// <code>
/// public void TogglePaused() {
///     …
///     else if (curTimeSpeed != 0) { prePauseTimeSpeed = curTimeSpeed; curTimeSpeed = TimeSpeed.Paused; }
/// }
/// </code>
///
/// <para>Espaço e botão de pausa passam por aqui, e só por aqui. Remendando só
/// a propriedade, a intenção de pausar <b>nunca</b> era capturada — o log
/// mostrava dezenas de "intenção do jogador: Fast/Superfast" e nenhum
/// "Pausado", e o jogo ficava impausável depois de algumas trocas de
/// velocidade.</para>
///
/// <para>Vale como lembrete geral: propriedade pública não garante que todo
/// mundo passe por ela. O próprio tipo costuma escrever no campo.</para>
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TogglePaused))]
public static class IntencaoDePausa
{
    [HarmonyPostfix]
    public static void Depois(TickManager __instance)
    {
        if (!IntencaoDeVelocidade.EmSessao()) return;

        // O valor DEPOIS do toggle: é o que o jogador acabou de pedir. Se o
        // jogo tiver recusado (PlayerCanControl), este valor é o antigo e o
        // pedido é inofensivo.
        ControleDeVelocidade.Registrar(__instance.CurTimeSpeed);
    }
}
