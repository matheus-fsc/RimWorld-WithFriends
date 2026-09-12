using System;
using HarmonyLib;
using Verse;
using WithFriends.Client.Session.Portas;

namespace WithFriends.Client.Session;

/// <summary>
/// Adaptador do <see cref="IRelogioDeSessao"/> — anel 2.
///
/// A barreira da §2.3 precisa ser exata: "ninguém passa do tick liberado"
/// perde o sentido se a simulação passar por três ticks antes de alguém
/// perceber. Por isso o freio é um prefixo em <c>TickManager.DoSingleTick</c>,
/// que é onde um tick de verdade acontece — e não um ajuste de velocidade
/// avaliado uma vez por frame.
///
/// É **um** ponto de acoplamento, num método público e antigo. Catalogado em
/// <see cref="Patches.CatalogoDePatches"/>.
/// </summary>
public sealed class RelogioDeSessaoRimWorld : IRelogioDeSessao
{
    /// <summary>
    /// Teto absoluto em <c>TicksGame</c>. <c>long.MaxValue</c> = sem sessão,
    /// o jogador manda no próprio tempo (§3).
    /// </summary>
    /// <summary>
    /// Até que <b>passo</b> da sessão é seguro avançar.
    ///
    /// Era medido em ticks de jogo. Passou a ser medido em passos quando os
    /// dois relógios se separaram: o passo anda mesmo com o jogo pausado, e é
    /// contra ele que a barreira libera.
    /// </summary>
    public static long LimiteDeTick { get; private set; } = long.MaxValue;

    /// <summary>
    /// Estamos dentro do laço de passos, e portanto autorizados a tickar?
    ///
    /// O freio deixou de ser "qual tick de jogo" e passou a ser "quem pediu":
    /// só o laço da sessão pode tickar, porque só ele sabe se este passo é de
    /// simular ou só de aplicar comando.
    /// </summary>
    internal static bool DentroDoPasso { get; private set; }

    /// <summary>Roda exatamente um tick de jogo, por dentro do freio.</summary>
    internal static void TickarUmaVez()
    {
        if (Find.TickManager == null) return;

        DentroDoPasso = true;
        try { Find.TickManager.DoSingleTick(); }
        finally { DentroDoPasso = false; }
    }

    public static bool EmSessao => LimiteDeTick != long.MaxValue;

    /// <summary>Quantas vezes o freio segurou um tick — diagnóstico da barreira.</summary>
    public static long TicksSegurados { get; private set; }

    public long TickAtual => Find.TickManager?.TicksGame ?? 0;

    public bool Pausado => Find.TickManager is { CurTimeSpeed: TimeSpeed.Paused };

    public void LimitarAte(long tick) => LimiteDeTick = tick;

    /// <summary>
    /// Freia a simulação sem precisar de uma instância — usado antes de trocar
    /// de partida, quando não há componente de sessão do outro lado ainda.
    ///
    /// <para>O número em si deixou de importar: o freio agora é "só o laço de
    /// passos tickia". O que esta chamada faz é ligar o freio, e é por isso que
    /// ela continua existindo.</para>
    public static void LimitarAteEstatico(long tick)
    {
        LimiteDeTick = tick;
        Log.Message($"[WithFriends] simulação freada no tick {tick} até a visita ser retomada");
    }

    public void Liberar() => LimiteDeTick = long.MaxValue;

    public void Pausar()
    {
        if (Find.TickManager == null) return;

        // Imposição nossa não é intenção do jogador.
        ControleDeVelocidade.ComoSistema(
            () => Find.TickManager.CurTimeSpeed = TimeSpeed.Paused);
    }

    public void Retomar()
    {
        if (Find.TickManager is { CurTimeSpeed: TimeSpeed.Paused } gerenciador)
            ControleDeVelocidade.ComoSistema(() => gerenciador.CurTimeSpeed = TimeSpeed.Normal);
    }

    internal static bool PodeTickar()
    {
        if (DentroDoPasso) return true;

        TicksSegurados++;
        return false;
    }

    public static void ZerarDiagnostico() => TicksSegurados = 0;

    /// <summary>
    /// O jogo está parado **na barreira** agora, esperando runway.
    ///
    /// É o sinal de "preciso de mais pista". Sem ele, o relato só saía em tick
    /// alinhado ou no fallback de tempo — e como o limite quase nunca cai num
    /// múltiplo de 8, o jogo ficava travado até o fallback estourar.
    /// </summary>
    public static bool NaBarreira
    {
        get
        {
            if (!EmSessao) return false;
            var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
            return sessao != null && sessao.TickDeSessao >= LimiteDeTick;
        }
    }
}

/// <summary>
/// O freio: nenhum tick acontece fora do laço de passos da sessão.
///
/// <para>Antes o freio era "este tick de jogo passou do liberado?". Com dois
/// relógios isso deixou de fazer sentido — o que libera é o <b>passo</b>, e
/// quem decide se um passo simula ou só aplica comando é o laço.</para>
///
/// <para>O contexto determinístico (RNG da sessão, tempo em ticks, comandos)
/// mudou junto para <c>SessaoCliente.ExecutarPasso</c>, que é onde as duas
/// coisas acontecem na ordem certa.</para>
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
public static class BarreiraDeTick
{
    [HarmonyPrefix]
    public static bool Antes() => !RelogioDeSessaoRimWorld.EmSessao
                                  || RelogioDeSessaoRimWorld.PodeTickar();
}
