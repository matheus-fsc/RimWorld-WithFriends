using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Enquanto um comando é aplicado, ninguém tem nada selecionado.
///
/// <para><b>O caso que trouxe isto.</b> O planejador de construção decide
/// assim:</para>
///
/// <code>
/// Plan plan = base.Map.planManager.PlanAt(c);
/// if (plan != null &amp;&amp; plan.Color == colorDef) { SelectedPlan = plan; … }
/// else PlanCells(cells);
///
/// // e dentro de PlanCells:
/// if (SelectedPlan == null) { …procura um plano da mesma cor… }
/// </code>
///
/// <para>E <c>SelectedPlan</c> lê <c>Find.Selector</c> — a seleção <b>daquele</b>
/// jogador. Aplicando o comando do outro lado, quem responde é a seleção de quem
/// está recebendo, que é outra coisa. Mesmo comando, caminhos diferentes.</para>
///
/// <para><b>A regra geral, e é ela que importa.</b> Seleção é interface: é o que
/// <b>este</b> jogador está olhando. Nenhum comando pode depender disso, e não
/// adianta caçar um leitor de cada vez — o planejador foi só o primeiro que
/// apareceu.</para>
///
/// <para>Então a seleção fica vazia durante a aplicação, e volta ao normal
/// depois. Os dois lados caem no mesmo ramo, que é função do mapa e dos dados do
/// comando — e esses viajam.</para>
/// </summary>
public static class SelecaoForaDoComando
{
    static readonly FieldInfo? Selecionados = AccessTools.Field(typeof(Selector), "selected");

    /// <summary>Roda a aplicação com a seleção vazia, e devolve o que estava lá.</summary>
    public static T SemSelecao<T>(Func<T> aplicar)
    {
        var seletor = Find.Selector;
        if (seletor == null || Selecionados?.GetValue(seletor) is not IList lista || lista.Count == 0)
            return aplicar();

        GuardasDeDeterminismo.Disparou("seleção esvaziada durante comando");

        var guardados = new object[lista.Count];
        lista.CopyTo(guardados, 0);
        lista.Clear();

        try { return aplicar(); }
        finally
        {
            // Devolver na mesma ordem: seleção é do jogador, e ele não pediu
            // para perder nada.
            foreach (var item in guardados) lista.Add(item);
        }
    }
}
