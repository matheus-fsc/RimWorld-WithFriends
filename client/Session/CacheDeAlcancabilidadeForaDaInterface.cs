using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A interface pergunta se dá para chegar lá; ela não decide a resposta para
/// a simulação.
///
/// <para><b>Como apareceu.</b> Uma divergência real, com o rastreio de caminho
/// ligado dos dois lados. O instrumento conta, por tick, quantas consultas de
/// alcançabilidade vieram de dentro da simulação e quantas de fora:</para>
///
/// <code>
/// anfitrião:  simulação 1.270   interface 229.367   (42 ticks)
/// visitante:  simulação 1.075   interface       0
///
/// passo 1111  alcançabilidade: 0 na simulação, 20.622 fora dela
/// passo 1114  alcançabilidade: 0 na simulação,  7.131 fora dela   ← o tick da divergência
/// </code>
///
/// <para>Duzentas e vinte e nove mil consultas de um lado, zero do outro — e a
/// divergência de estado nasce exatamente num dos ticks em que a interface do
/// anfitrião estava varrendo o mapa.</para>
///
/// <para><b>Por que isso importa.</b> <c>Reachability.CanReach</c> não é
/// leitura: ela <b>memoriza</b> o resultado por par de distritos, em
/// <c>ReachabilityCache</c>. Um memo só é neutro enquanto é invalidado
/// corretamente; qualquer entrada que sobreviva a uma mudança que deveria
/// apagá-la vira uma resposta velha — e só para o lado que consultou antes.
/// A interface de um jogador não é a do outro, então a memória dos dois deixa
/// de ser a mesma.</para>
///
/// <para>É a terceira vez hoje que a mesma forma aparece: estado derivado que a
/// simulação lê e a interface escreve (ADR 0020). Antes foi a ordem dos
/// vizinhos, depois as células de zona; aqui é a memória de alcançabilidade.
/// O remédio é o mesmo — e é o de <see cref="CachesDeCombateForaDaInterface"/>:
/// <b>a interface lê, mas não escreve</b>.</para>
///
/// <para><b>O que isto custa.</b> A interface refaz a busca em vez de aproveitar
/// o memo. Ela já faz isso dezenas de milhares de vezes por tick sem ninguém
/// notar — e o preço de não pagar era a visita parar.</para>
///
/// <para><b>O que isto não faz.</b> Não mexe no contador de alcance
/// (<c>reachedIndex</c>) nem nas marcas que ele deixa nas regiões: essas se
/// invalidam sozinhas, porque cada consulta incrementa o contador antes de
/// marcar e só reconhece marca igual à atual. Marca velha é automaticamente
/// "não alcançada", em qualquer ordem. O memo é a única parte que atravessa
/// chamadas — e por isso é a única guardada.</para>
/// </summary>
[HarmonyPatch(typeof(ReachabilityCache), nameof(ReachabilityCache.AddCachedResult))]
public static class CacheDeAlcancabilidadeForaDaInterface
{
    [HarmonyPrefix]
    public static bool Antes()
    {
        if (!NaInterface.Agora) return true;

        GuardasDeDeterminismo.Disparou("interface não memoriza alcançabilidade");
        return false;
    }
}
