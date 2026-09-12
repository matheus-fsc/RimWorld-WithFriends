using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;

namespace WithFriends.Client.Session;

/// <summary>
/// Neutraliza a aleatoriedade do que é **efeito**, não simulação.
///
/// A ideia vem do Multiplayer (MIT, Zetrith), `MultiplayerStatic.cs`: em vez de
/// tornar cada decisão cosmética determinística uma por uma, embrulha-se cada
/// método de efeito em <c>Rand.PushState()</c> / <c>Rand.PopState()</c>. O que
/// ele sortear é descartado, e o fluxo compartilhado nem sente.
///
/// É melhor do que consertar decisão por decisão porque não exige saber de
/// antemão quais efeitos sorteiam números — e mods trazem os seus.
///
/// Aqui só vale **dentro da sessão**: fora dela o jogo sorteia como sempre
/// sorteou, e o jogo solo não muda em nada (§11).
///
/// <para><b>Por que não usar a pilha do RNG.</b> A primeira versão usava
/// <c>Rand.PushState()</c>/<c>PopState()</c>, como o Multiplayer. Em jogo, isso
/// produziu <c>InvalidOperationException: Stack empty</c> às dezenas — a pilha
/// é estática e compartilhada com o jogo inteiro, inclusive com
/// <c>Rand.EnsureStateStackEmpty()</c>, que a esvazia, e com código de som que
/// pode rodar fora da thread principal.</para>
///
/// <para>Pior: o finalizador lançando exceção **substitui** a exceção original
/// do método remendado, então a quebra contamina o jogo. Os dois abortos
/// seguintes provavelmente vieram daí.</para>
///
/// <para>Salvar e restaurar o estado direto não depende de pilha nenhuma: é
/// idempotente, tolera aninhamento e não tem como ficar desbalanceado.</para>
/// </summary>
public static class EfeitosNaoDeterministicos
{
    /// <summary>Métodos cujo consumo de RNG não pode contar para a sessão.</summary>
    public static IEnumerable<MethodBase> Alvos()
    {
        // Motes: fumaça, faíscas, texto de dano.
        foreach (var m in typeof(MoteMaker).GetMethods(BindingFlags.Static | BindingFlags.Public))
            yield return m;

        // Flecks: a geração nova de efeitos, mesmo papel.
        var fleckMaker = AccessTools.TypeByName("RimWorld.FleckMaker");
        if (fleckMaker != null)
            foreach (var m in fleckMaker.GetMethods(BindingFlags.Static | BindingFlags.Public)
                         .Where(m => m.ReturnType == typeof(void)))
                yield return m;

        // Effecters: partículas presas a coisas.
        foreach (var nome in new[] { nameof(Effecter.EffectTick), nameof(Effecter.Cleanup) })
        {
            var m = AccessTools.Method(typeof(Effecter), nome);
            if (m != null) yield return m;
        }

        // **Criação preguiçosa de coisas de desenho.**
        //
        // `Pawn.Drawer` é `drawer ?? (drawer = new Pawn_DrawTracker(this))`:
        // nasce quando o pawn é **desenhado pela primeira vez**, ou seja,
        // quando a câmera chega nele. Dois jogadores olhando para lugares
        // diferentes criam esses objetos em ticks diferentes — e a construção
        // sorteia.
        //
        // Medido: +119 sorteios num tick só, num lado só, ao mover a câmera.
        // O Multiplayer patcheia o mesmo construtor pelo mesmo motivo.
        var pawnDrawer = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Drawer));
        if (pawnDrawer != null) yield return pawnDrawer;

        foreach (var tipo in new[] { typeof(Pawn_DrawTracker), typeof(PawnRenderer), typeof(PawnTweener) })
        {
            var ctor = tipo.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault();
            if (ctor != null) yield return ctor;
        }

        // Cabelo/estilo sorteado e malha de raio: da lista do Multiplayer.
        foreach (var (tipoNome, membro) in new[]
                 {
                     ("RimWorld.PawnStyleItemChooser", "RandomHairFor"),
                     ("Verse.LightningBoltMeshPool", "RandomBoltMesh"),
                 })
        {
            var tipo = AccessTools.TypeByName(tipoNome);
            if (tipo == null) continue;
            var m = AccessTools.Method(tipo, membro) ?? AccessTools.PropertyGetter(tipo, membro);
            if (m != null) yield return m;
        }

        // Som: sustainers escolhem amostras sorteando.
        foreach (var tipo in new[] { typeof(SubSustainer), typeof(SoundStarter) })
        foreach (var m in tipo.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(m => m.DeclaringType == tipo && !m.IsAbstract && m.ReturnType == typeof(void)))
            yield return m;
    }

    public static int Instalar(Harmony harmony)
    {
        var prefixo = new HarmonyMethod(AccessTools.Method(typeof(EfeitosNaoDeterministicos), nameof(Antes)));
        var finalizador = new HarmonyMethod(AccessTools.Method(typeof(EfeitosNaoDeterministicos), nameof(Depois)));

        int instalados = 0;
        foreach (var alvo in Alvos())
        {
            try
            {
                harmony.Patch(alvo, prefix: prefixo, finalizer: finalizador);
                instalados++;
            }
            catch (Exception)
            {
                // Método que não dá para remendar (genérico, abstrato, inline)
                // simplesmente fica de fora: é efeito, não simulação.
            }
        }

        Log.Message($"[WithFriends] {instalados} método(s) de efeito isolados do RNG da sessão");
        return instalados;
    }

    /// <summary>Estado guardado entre o prefixo e o finalizador.</summary>
    public struct EstadoSalvo
    {
        public bool Guardou;
        public ulong Valor;
    }

    public static void Antes(out EstadoSalvo __state)
    {
        __state = default;
        if (!RngDeSessao.Ativo) return;

        var estado = RngDeSessao.LerEstadoBruto();
        if (estado == null) return;

        __state.Guardou = true;
        __state.Valor = estado.Value;
    }

    /// <summary>
    /// Devolve o RNG ao ponto anterior à chamada: o que o efeito sorteou some.
    ///
    /// Nunca lança. Um finalizador que lança substitui a exceção original do
    /// método remendado — o remédio viraria doença.
    /// </summary>
    public static void Depois(EstadoSalvo __state)
    {
        if (__state.Guardou) RngDeSessao.EscreverEstadoBruto(__state.Valor);
    }
}

/// <summary>
/// <c>MoteCounter.Saturated</c> depende de quantos motes existem **nesta**
/// máquina — e os dois lados nunca têm os mesmos. Se ele decide se um mote
/// nasce, decide de forma diferente em cada lado.
/// </summary>
[HarmonyPatch(typeof(MoteCounter), nameof(MoteCounter.Saturated), MethodType.Getter)]
public static class SaturacaoDeterministica
{
    [HarmonyPrefix]
    public static bool Antes(ref bool __result)
    {
        if (!RngDeSessao.Ativo) return true;

        __result = false;
        return false;
    }
}
