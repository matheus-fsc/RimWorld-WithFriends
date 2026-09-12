using System.Collections.Generic;
using System.Linq;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Quantas vezes cada guarda de determinismo entrou em ação.
///
/// <para><b>Por que contar em vez de logar.</b> Estas guardas rodam na
/// interface, muitas vezes por quadro. Logar cada uma encheria o arquivo e
/// esconderia o resto. Mas não logar nada deixa uma dúvida pior: <b>a guarda
/// está funcionando ou o alvo dela sumiu numa atualização?</b> Guarda que nunca
/// dispara e guarda que não existe têm exatamente a mesma cara.</para>
///
/// <para>Então: uma linha na primeira vez que cada uma dispara, e um contador
/// para o resto. A debug action "Guardas de determinismo" imprime a tabela.</para>
///
/// <para>Guarda com zero disparos depois de uma visita inteira é suspeita — ou
/// aquele caminho não foi exercitado, ou o remendo não está pegando.</para>
/// </summary>
public static class GuardasDeDeterminismo
{
    static readonly Dictionary<string, long> disparos = new();

    public static void Disparou(string guarda)
    {
        if (!disparos.TryGetValue(guarda, out long antes))
        {
            disparos[guarda] = 1;
            Log.Message($"[WithFriends/guarda] {guarda} entrou em ação pela primeira vez");
            return;
        }

        disparos[guarda] = antes + 1;
    }

    public static void Limpar() => disparos.Clear();

    public static string Relatorio()
    {
        if (disparos.Count == 0)
            return "[WithFriends] nenhuma guarda de determinismo disparou ainda.\n" +
                   "  Ou a sessão não exercitou esses caminhos, ou os remendos não estão pegando.";

        var linhas = disparos
            .OrderByDescending(p => p.Value)
            .Select(p => $"  {p.Value,10:N0}x  {p.Key}");

        return $"[WithFriends] guardas de determinismo ({disparos.Count} ativa(s)):\n" +
               string.Join("\n", linhas) + "\n" +
               "  Guarda ausente desta lista depois de uma visita inteira é suspeita:\n" +
               "  ou o caminho não foi exercitado, ou o alvo sumiu numa atualização do jogo.";
    }
}
