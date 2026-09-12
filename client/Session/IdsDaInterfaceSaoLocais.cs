// Adaptado de Source/Client/Patches/UniqueIds.cs (UniqueIdsPatch) de
// rwmt/Multiplayer, MIT, Copyright (c) 2018 Zetrith.
// Ver THIRD_PARTY/Multiplayer-MIT.txt

using HarmonyLib;
using RimWorld;

namespace WithFriends.Client.Session;

/// <summary>
/// Id pedido pela interface é <b>negativo</b> e sai de um contador só desta
/// máquina.
///
/// <para><b>O contador de ids é compartilhado, e a interface mexe nele.</b>
/// Todo id único do RimWorld — coisa, bill, lord, mensagem, tale — sai de
/// <c>UniqueIDsManager.GetNextID(ref nextID)</c>, um contador que anda a cada
/// pedido. A interface pede o tempo todo: uma mensagem na tela, uma carta, um
/// arquivável, um objeto temporário de janela.</para>
///
/// <para>Cada um desses pedidos adianta o contador <b>de um lado só</b>. A
/// próxima coisa que a simulação criar — uma bala, um cadáver, sujeira, um
/// blueprint — recebe ids diferentes nos dois lados. E
/// <c>Thing.thingIDNumber</c> não é etiqueta: ele é semente. Entra em
/// <c>IsHashIntervalTick</c> (que decide em que tick cada coisa age), na rotação
/// sorteada do <c>GenSpawn.Spawn</c>, em desempate de ordenação. Ids diferentes
/// são comportamentos diferentes, para sempre.</para>
///
/// <para><b>A regra.</b> O contador compartilhado só anda dentro do tick e da
/// aplicação de comando — onde os dois lados fazem a mesma coisa na mesma ordem.
/// Fora dali, a interface recebe um id negativo de um contador local, que
/// decresce. Negativo porque nenhum id de simulação é: um id local nunca vai ser
/// confundido com um compartilhado, nem em log nem em comparação.</para>
///
/// <para>Começa em <c>-2</c> porque <c>-1</c> é usado como marca de "não
/// inicializado" em vários lugares do jogo — detalhe herdado do Multiplayer, e
/// vale de graça.</para>
///
/// <para>Isto cobre uma família inteira de divergências de uma vez, e é a razão
/// de o combate doer mais: combate cria coisas o tempo todo — projéteis,
/// sujeira, sangue, cadáveres — e é quando mais mensagens aparecem na tela.</para>
/// </summary>
[HarmonyPatch(typeof(UniqueIDsManager), "GetNextID")]
public static class IdsDaInterfaceSaoLocais
{
    static int locais = -2;

    /// <summary>
    /// A interface está pedindo?
    ///
    /// <para><see cref="NaInterface.Agora"/> já quer dizer "em sessão, fora do
    /// tick e fora de comando", que é exatamente a condição.</para>
    /// </summary>
    static bool ÉPedidoDaInterface => NaInterface.Agora;

    [HarmonyPrefix]
    public static bool Antes() => !ÉPedidoDaInterface;

    [HarmonyPostfix]
    public static void Depois(ref int __result)
    {
        if (!ÉPedidoDaInterface) return;

        GuardasDeDeterminismo.Disparou("id pedido pela interface (local, negativo)");
        __result = locais--;
    }
}
