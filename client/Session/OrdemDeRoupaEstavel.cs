using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Empate na ordem das roupas decidido por id, e não por quem chegou primeiro
/// na lista.
///
/// <para><b>Por que isto é simulação e não desenho.</b> O nome engana:
/// <c>SortWornApparelIntoDrawOrder</c> parece arrumar o boneco. Mas a lista que
/// ela ordena é a mesma que <c>ArmorUtility.ApplyArmor</c> percorre quando um
/// tiro acerta — peça por peça, na ordem. Duas ordens diferentes absorvem o
/// dano em ordens diferentes, e um colete que aparece antes de uma jaqueta muda
/// o que sobra para a carne.</para>
///
/// <para><b>Por que empata.</b> A comparação usa a camada de desenho da peça, e
/// duas peças na mesma camada empatam. <c>List.Sort</c> não é estável: com
/// empate, quem decide é a ordem em que as peças estavam na lista — que é a
/// ordem em que foram vestidas, história de processo (ADR 0020). O anfitrião
/// vestiu ao longo da partida dele; o visitante recebeu tudo junto ao carregar o
/// save.</para>
///
/// <para><b>De onde veio.</b> Da lista de determinismo do Multiplayer, que
/// remenda exatamente esta lambda pelo mesmo motivo (<c>Determinism.cs</c>,
/// <c>FixApparelSort</c>). Foi achada comparando o que eles mapeiam com o que
/// mapeamos — e apareceu no rastro de uma divergência nossa, com
/// <c>ArmorUtility.ApplyArmor</c> na pilha do sorteio que diferiu.</para>
///
/// <para>O desempate por <c>thingIDNumber</c> é o mesmo dos dois lados porque o
/// id vem do save.</para>
/// </summary>
[HarmonyPatch]
public static class OrdemDeRoupaEstavel
{
    /// <summary>
    /// A comparação vive numa lambda — <c>&lt;SortWornApparelIntoDrawOrder&gt;b__74_0</c>
    /// —, e o nome dela muda quando o arquivo do jogo muda de ordem. Por isso a
    /// busca é pela forma: no tipo gerado pelo compilador, o método que recebe
    /// duas <c>Apparel</c> e devolve <c>int</c>.
    /// </summary>
    static MethodBase? TargetMethod()
    {
        foreach (var aninhado in typeof(Pawn_ApparelTracker).GetNestedTypes(
                     BindingFlags.Public | BindingFlags.NonPublic))
        foreach (var m in aninhado.GetMethods(
                     BindingFlags.Instance | BindingFlags.Static |
                     BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.ReturnType != typeof(int)) continue;
            var p = m.GetParameters();
            if (p.Length == 2 && p[0].ParameterType == typeof(Apparel)
                              && p[1].ParameterType == typeof(Apparel))
                return m;
        }

        return null;
    }

    [HarmonyPostfix]
    public static void Depois(Apparel a, Apparel b, ref int __result)
    {
        if (__result != 0 || a == null || b == null) return;

        __result = a.thingIDNumber.CompareTo(b.thingIDNumber);
    }
}
