using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A interface consulta stats; ela não decide qual valor a simulação vai usar.
///
/// <para><b>Como apareceu.</b> Uma divergência que não era de decisão nenhuma:
/// vários pawns com o custo de movimento diferente na terceira casa decimal, no
/// mesmo tick, mesmo job, mesmo destino. A primeira suspeita — o multiplicador
/// de clima — foi instrumentada e <b>refutada</b>: 400 ticks com a mesma idade
/// de clima e multiplicador exatamente 1 dos dois lados.</para>
///
/// <para>O rastreio passou então a gravar as três entradas de
/// <c>TicksPerMove</c>, e a corrida seguinte respondeu num par de linhas:</para>
///
/// <code>
/// A: Huber  vel 4.43383026  mover 1.02  tpm 13.54722  custo 1.543/15.530
/// B: Huber  vel 4.4330883   mover 1.02  tpm 13.54949  custo 1.543/15.530
/// </code>
///
/// <para>A capacidade de mover é <b>idêntica</b>; o stat de velocidade é que
/// difere, na sexta casa significativa. E o custo ainda não tinha divergido —
/// a causa foi pega um degrau acima da consequência.</para>
///
/// <para><b>Por que o stat difere se as entradas são iguais.</b> Porque ele não
/// é recalculado toda vez. <c>StatWorker</c> guarda um cache temporário por
/// coisa, carimbado com o tick do jogo:</para>
///
/// <code>
/// temporaryStatCache: Dictionary&lt;Thing, StatCacheEntry { gameTick, statValue }&gt;
/// GetValue(thing, applyPostProcess, cacheStaleAfterTicks)
/// </code>
///
/// <para>Enquanto a entrada não envelhece, devolve-se o valor guardado. Quem
/// consulta um stat <b>fora do tick</b> — a aba de saúde, o painel de inspeção,
/// a tabela de pawns, uma dica de ferramenta — grava nesse cache. E a interface
/// de um jogador não é a do outro: um lado passa a ler um valor calculado num
/// tick, o outro num tick vizinho, e enquanto os dois valores não envelhecem a
/// simulação anda com números diferentes sem ter sorteado nada.</para>
///
/// <para>É a quarta vez que a mesma forma aparece (ADR 0020): estado derivado
/// que a simulação lê e a interface escreve. Antes foram a ordem dos vizinhos,
/// as células de zona e a memória de alcançabilidade. O remédio é o mesmo de
/// <see cref="CacheDeAlcancabilidadeForaDaInterface"/>: <b>a interface lê, mas
/// não escreve</b>.</para>
///
/// <para><b>O que isto custa.</b> A interface recalcula o stat em vez de
/// aproveitar o cache. É o preço que ela já paga em todas as outras leituras que
/// fizemos sair do caminho da simulação, e ninguém nota um stat recalculado num
/// quadro de interface.</para>
///
/// <para><b>O que isto não faz.</b> Não toca no cache de stats
/// <c>immutable</c>: aquele guarda valor que não depende de estado nenhum, e
/// portanto é o mesmo em qualquer tick e em qualquer máquina.</para>
/// </summary>
[HarmonyPatch]
public static class CacheDeStatForaDaInterface
{
    static System.Reflection.MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(StatWorker), nameof(StatWorker.GetValue),
            new[] { typeof(Thing), typeof(bool), typeof(int) });

    [HarmonyPrefix]
    public static bool Antes(StatWorker __instance, Thing thing, bool applyPostProcess,
                             ref float __result)
    {
        if (!RngDeSessao.Ativo) return true;
        if (!NaInterface.Agora) return true;
        if (thing == null) return true;

        // Calcula direto, sem ler nem gravar o cache temporário. O caminho
        // público existe justamente para isto — é o mesmo que o original chama
        // quando a entrada está velha.
        try
        {
            __result = __instance.GetValue(StatRequest.For(thing), applyPostProcess);
            return false;
        }
        catch (Exception)
        {
            // Stat que não aceita esta forma de pedido: deixa o original tratar.
            // Errar aqui não pode custar mais que um valor de interface.
            return true;
        }
    }
}
