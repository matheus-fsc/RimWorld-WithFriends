using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Estado de RNG próprio da sessão — ADR 0009.
///
/// Em lockstep ninguém consulta o RNG do outro: os dois **calculam o mesmo**,
/// partindo do mesmo estado com a mesma semente, que vem de
/// <c>SessaoInicio.Semente</c>.
///
/// <para><b>Por que não usar PushState/PopState.</b> Foi a primeira tentativa,
/// e o jogo desfaz: <c>Rand.EnsureStateStackEmpty()</c> roda periodicamente e
/// esvazia qualquer estado empilhado, com o aviso "Random state stack is not
/// empty. There were more calls to PushState than PopState. Fixing." O
/// <c>PopState</c> no fim da sessão então estourava, dos dois lados.</para>
///
/// <para>A pilha do RimWorld é para intervalos curtos — geração de mapa, um
/// incidente — não para durar uma sessão inteira. Então guardamos o estado
/// **nós mesmos** e o trocamos em volta de cada tick, que é exatamente o
/// intervalo que precisa ser determinístico.</para>
/// </summary>
public static class RngDeSessao
{
    static readonly MethodInfo? Ler = AccessTools.PropertyGetter(typeof(Rand), "StateCompressed");
    static readonly MethodInfo? Escrever = AccessTools.PropertySetter(typeof(Rand), "StateCompressed");

    static ulong estadoDaSessao;
    static ulong estadoDoJogo;

    /// <summary>Gerador dos cosméticos, que ninguém precisa sincronizar.</summary>
    static ulong estadoCosmetico;

    /// <summary>
    /// Últimos ticks e o estado do RNG em cada um.
    ///
    /// §14.3 decisão 4: em jogo normal só o resumo trafega; o detalhe é
    /// guardado localmente e só aparece **depois** do desync. Comparar os dois
    /// logs diz em qual tick a simulação começou a divergir — que é a pergunta
    /// que "divergiram no tick 9" não responde, porque 9 é onde se percebeu,
    /// não necessariamente onde começou.
    /// </summary>
    static readonly Queue<(long tick, ulong estado)> historico = new();

    /// <summary>
    /// Ticks guardados no anel.
    ///
    /// 240 era pouco: uma divergência nascida antes disso saía da janela antes
    /// de o aborto acontecer, e o despejo mostrava os dois lados já separados
    /// desde a primeira linha — sem dizer onde começou.
    /// </summary>
    const int TicksGuardados = 4000;

    public static bool Ativo { get; private set; }

    public static int SementeAtual { get; private set; }

    /// <summary>
    /// Estado do RNG **da sessão** — o que precisa ser idêntico nos dois lados.
    ///
    /// Não confundir com <c>Rand.StateCompressed</c>: aquele é o do processo,
    /// vive fora do save e não tem relação nenhuma entre duas máquinas. Medir
    /// aquele para detectar desync compara coisas incomparáveis.
    /// </summary>
    public static ulong Estado => estadoDaSessao;

    public static void Entrar(int semente)
    {
        if (Ativo) return;

        SementeAtual = semente;
        // Mesma semente dos dois lados, contador zerado: o intervalo começa
        // igual para os dois.
        estadoDaSessao = unchecked((uint)semente);
        historico.Clear();
        RastreioDeRng.Limpar();
        RastreioDePawns.Limpar();
        TickGuiadoPelaBarreira.Zerar();
        AlistamentoOtimista.Limpar();
        ControleDeVelocidade.Limpar();
        GuardasDeDeterminismo.Limpar();
        Ativo = true;
        Log.Message($"[WithFriends] RNG de sessão ativo (semente {semente})");
    }

    public static void Sair()
    {
        if (!Ativo) return;
        Ativo = false;
        Log.Message("[WithFriends] RNG de sessão encerrado");
    }

    /// <summary>Iterações do RNG da sessão — o mesmo número que vai na digital.</summary>
    public static uint Iteracoes() => (uint)(estadoDaSessao >> 32);

    /// <summary>Lê o estado bruto do RNG do processo. <c>null</c> se indisponível.</summary>
    public static ulong? LerEstadoBruto()
    {
        try { return Ler == null ? null : (ulong)Ler.Invoke(null, null)!; }
        catch (Exception) { return null; }
    }

    /// <summary>Escreve o estado bruto do RNG do processo.</summary>
    public static void EscreverEstadoBruto(ulong estado)
    {
        try { Escrever?.Invoke(null, new object[] { estado }); }
        catch (Exception) { /* o catálogo já avisou na subida */ }
    }

    /// <summary>
    /// Sai do RNG da sessão para rodar algo cosmético (um mote).
    ///
    /// O que é cosmético não pode mexer no estado determinístico: motes não
    /// são salvos, então os dois lados nunca terão os mesmos, e qualquer
    /// sorteio que eles façam separa as simulações para sempre.
    /// </summary>
    public static void SairDoTickCosmetico()
    {
        if (!Ativo || Ler == null || Escrever == null) return;

        try
        {
            estadoDaSessao = (ulong)Ler.Invoke(null, null)!;
            Escrever.Invoke(null, new object[] { estadoCosmetico });
        }
        catch (Exception) { Ativo = false; }
    }

    /// <summary>Volta ao RNG da sessão depois do cosmético.</summary>
    public static void VoltarAoTickCosmetico()
    {
        if (!Ativo || Ler == null || Escrever == null) return;

        try
        {
            estadoCosmetico = (ulong)Ler.Invoke(null, null)!;
            Escrever.Invoke(null, new object[] { estadoDaSessao });
        }
        catch (Exception) { Ativo = false; }
    }

    /// <summary>
    /// Despeja o histórico no log. Chamado no aborto: é com estes dois
    /// despejos, lado a lado, que se descobre o primeiro tick divergente.
    /// </summary>
    public static void Despejar(string motivo)
    {
        if (historico.Count == 0)
        {
            Log.Message($"[WithFriends] sem histórico de RNG para despejar ({motivo})");
            return;
        }

        // Em pedaços, não numa string só: 4000 ticks viram algumas centenas de
        // KB de uma vez, alocados no heap de objetos grandes, e o despejo
        // acontece logo antes do rollback — que já é o pico de memória.
        const int PorMensagem = 500;

        Log.Message(
            $"[WithFriends] histórico de RNG da sessão ({motivo}) — semente {SementeAtual}, " +
            $"{historico.Count} tick(s):");

        var linhas = historico
            .Select(e => $"    tick {e.tick,6}  rng {e.estado >> 32,12}  seed {(uint)e.estado,12}")
            .ToList();

        for (var i = 0; i < linhas.Count; i += PorMensagem)
            Log.Message(string.Join("\n", linhas.Skip(i).Take(PorMensagem)));
    }

    /// <summary>Entra no estado da sessão. Chamado antes de cada tick compartilhado.</summary>
    public static void AntesDoTick()
    {
        if (!Ativo || Ler == null || Escrever == null) return;

        try
        {
            estadoDoJogo = (ulong)Ler.Invoke(null, null)!;
            Escrever.Invoke(null, new object[] { estadoDaSessao });
        }
        catch (Exception)
        {
            Ativo = false;   // o catálogo já avisou na subida
        }
    }

    /// <summary>Guarda o estado da sessão e devolve o do jogo.</summary>
    public static void DepoisDoTick(long tickDeSessao)
    {
        if (!Ativo || Ler == null || Escrever == null) return;

        try
        {
            estadoDaSessao = (ulong)Ler.Invoke(null, null)!;
            Escrever.Invoke(null, new object[] { estadoDoJogo });

            historico.Enqueue((tickDeSessao, estadoDaSessao));
            while (historico.Count > TicksGuardados) historico.Dequeue();
        }
        catch (Exception)
        {
            Ativo = false;
        }
    }
}
