using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Campos que a interface escreve <b>direto</b>, sem passar por setter nenhum —
/// e como fazê-los virar comando mesmo assim.
///
/// <para><b>O problema.</b> Depois das famílias pawn, zona/área e das três
/// propriedades de valor, o que sobra no mapa de decisões são 23 campos públicos
/// e 96 botões cuja ação é uma lambda. Campo público não tem setter; lambda é um
/// método gerado pelo compilador, com nome que muda quando o arquivo muda de
/// ordem. Remendar um a um é o caminho que levou o Multiplayer a 227 registros
/// de lambda.</para>
///
/// <para><b>A saída, herdada dele.</b> Não é preciso saber QUEM escreveu no
/// campo — basta saber que <b>a interface esteve ali</b> e que o valor mudou.
/// Então:</para>
///
/// <list type="number">
/// <item>antes da chamada de interface, anota-se o valor atual;</item>
/// <item>depois, compara-se;</item>
/// <item>se mudou: desfaz-se a mudança local e propõe-se o comando.</item>
/// </list>
///
/// <para>O jogador vê o valor voltar por um instante e assumir o novo quando o
/// comando volta carimbado — exatamente o que já acontece com qualquer outro
/// clique dentro de uma visita.</para>
///
/// <para><b>Por que isto é barato.</b> Não se paga por lambda, paga-se por
/// <b>lugar da interface</b>. O Multiplayer cobre essa família inteira com 40
/// pontos de vigilância. Um ponto novo custa um remendo e uma linha dizendo o
/// que observar ali.</para>
///
/// <para><b>Menu suspenso é o caso que não cabe no par antes/depois.</b> O
/// desenho do botão não muda nada: a escrita acontece quando o jogador ESCOLHE,
/// muito depois de o desenho terminar. Por isso <see cref="EnvolverMenu"/>
/// embrulha a ação de cada item do menu, em vez do desenho.</para>
/// </summary>
public static class CamposObservados
{
    /// <summary>
    /// Um campo vigiado: como ler, como escrever, e o que o payload carrega.
    ///
    /// <para>O valor viaja como <c>float</c> porque cobre os três formatos que
    /// aparecem aqui — booleano, enum e número. Quem sabe convertê-lo de volta é
    /// o próprio descritor.</para>
    /// </summary>
    sealed class Campo
    {
        public Func<Thing, float?> Ler = null!;
        public Action<Thing, float> Escrever = null!;
        public string Descrever = "";
    }

    static readonly Dictionary<string, Campo> Registro = new()
    {
        // Cuidado médico: decide que remédio o colono recebe quando cai. Numa
        // visita cai gente das duas colônias, e a escolha muda o tratamento que
        // a simulação aplica no tick seguinte.
        ["cuidadoMedico"] = new Campo
        {
            Ler = t => t is Pawn { playerSettings: not null } p ? (float)p.playerSettings.medCare : null,
            Escrever = (t, v) =>
            {
                if (t is Pawn { playerSettings: not null } p)
                    p.playerSettings.medCare = (MedicalCareCategory)(int)v;
            },
            Descrever = "cuidado médico",
        },

        // Tratar-se sozinho: muda quem faz o trabalho de medicina, e portanto o
        // que os outros colonos fazem enquanto isso.
        ["autoTratar"] = new Campo
        {
            Ler = t => t is Pawn { playerSettings: not null } p ? (p.playerSettings.selfTend ? 1f : 0f) : null,
            Escrever = (t, v) =>
            {
                if (t is Pawn { playerSettings: not null } p) p.playerSettings.selfTend = v != 0f;
            },
            Descrever = "tratar-se sozinho",
        },

        // Resposta a hostilidade: atacar, fugir ou ignorar. É decisão de
        // combate, e combate é o que uma visita mais exercita.
        ["respostaAHostilidade"] = new Campo
        {
            Ler = t => t is Pawn { playerSettings: not null } p
                ? (float)p.playerSettings.hostilityResponse
                : null,
            Escrever = (t, v) =>
            {
                if (t is Pawn { playerSettings: not null } p)
                    p.playerSettings.hostilityResponse = (HostilityResponseMode)(int)v;
            },
            Descrever = "resposta a hostilidade",
        },
    };

    /// <summary>
    /// O que está sendo vigiado agora. O <c>null</c> é marca de início: cada
    /// <see cref="Antes"/> empilha uma, e <see cref="Depois"/> desempilha até
    /// achá-la. Pilha e não lista porque interface aninha — uma aba desenha
    /// dentro de outra.
    /// </summary>
    static readonly Stack<(Thing dono, string chave, float valor)?> vigiados = new();

    public static void Antes()
    {
        if (!EmVisita()) return;
        vigiados.Push(null);
    }

    /// <summary>Anota o valor atual de um campo, para comparar depois.</summary>
    public static void Observar(Thing? dono, string chave)
    {
        if (!EmVisita() || dono == null) return;
        if (!Registro.TryGetValue(chave, out var campo)) return;
        if (campo.Ler(dono) is not { } valor) return;

        vigiados.Push((dono, chave, valor));
    }

    public static void Depois()
    {
        if (!EmVisita()) return;

        while (vigiados.Count > 0)
        {
            var item = vigiados.Pop();
            if (item is not { } v) return;   // a marca: acabou esta vigilância

            var campo = Registro[v.chave];
            if (campo.Ler(v.dono) is not { } agora || agora == v.valor) continue;

            // **Desfaz e propõe.** A simulação continua com o valor antigo até o
            // comando voltar carimbado; sem desfazer, este lado já teria mudado
            // e o outro não — que é a divergência que tudo isto evita.
            campo.Escrever(v.dono, v.valor);

            if (v.dono is Pawn pawn && !PosseDePawns.EhMeu(pawn))
            {
                PosseDePawns.AvisarQueNaoEhSeu(pawn);
                continue;
            }

            GuardasDeDeterminismo.Disparou($"campo observado {v.chave} virou comando");
            AjustesDeCoisa.DeixarPassar(v.dono, v.chave, agora);
        }
    }

    /// <summary>Aplica o comando já carimbado. Chamado por <see cref="AjustesDeCoisa"/>.</summary>
    public static bool Aplicar(Thing dono, string chave, float valor)
    {
        if (!Registro.TryGetValue(chave, out var campo)) return false;
        campo.Escrever(dono, valor);
        return true;
    }

    public static string Descrever(string chave) =>
        Registro.TryGetValue(chave, out var campo) ? campo.Descrever : chave;

    public static bool Conhece(string chave) => Registro.ContainsKey(chave);

    static bool EmVisita() =>
        Colony.SincronizacaoComponent.Atual?.Sessao
            is { Estado: EstadoSessaoLocal.Simulando, Atual: not null };

    /// <summary>
    /// Embrulha a ação de cada item de um menu suspenso.
    ///
    /// <para>Aqui o par antes/depois em volta do desenho não serve: o desenho só
    /// abre o menu, e a escrita acontece quando o jogador escolhe, quadros
    /// depois. Então a vigilância vai em volta da ESCOLHA.</para>
    /// </summary>
    public static IEnumerable<Widgets.DropdownMenuElement<T>> EnvolverMenu<T>(
        IEnumerable<Widgets.DropdownMenuElement<T>> itens, Thing? dono, params string[] chaves)
    {
        foreach (var item in itens)
        {
            var original = item.option.action;
            if (original != null)
                item.option.action = () =>
                {
                    Antes();
                    foreach (var chave in chaves) Observar(dono, chave);
                    try { original(); }
                    finally { Depois(); }
                };

            yield return item;
        }
    }
}

/// <summary>
/// A aba de saúde, de onde sai "tratar-se sozinho".
///
/// <para>O alvo é privado, e por isso vai por <c>TargetMethods</c> em vez de
/// atributo: <c>nameof</c> não alcança o que não é público. Quem apontou este
/// método foi a pergunta "quem escreve neste campo?" — <c>wf auditar
/// --escritores Pawn_PlayerSettings::selfTend</c> responde
/// <c>HealthCardUtility.DrawOverviewTab</c>, e mais nada.</para>
/// </summary>
[HarmonyPatch]
public static class VigiarAbaDeSaude
{
    static System.Reflection.MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(HealthCardUtility), "DrawOverviewTab");

    [HarmonyPrefix]
    public static void Antes(Pawn pawn)
    {
        CamposObservados.Antes();
        CamposObservados.Observar(pawn, "autoTratar");
    }

    [HarmonyPostfix]
    public static void Depois() => CamposObservados.Depois();
}

/// <summary>
/// O menu de cuidado médico. Postfix e não prefix: o que se embrulha é a ação de
/// cada item, e os itens só existem depois que o método os gera.
///
/// <para>É o caso que o par antes/depois não pega. A escrita está numa lambda
/// (<c>&lt;MedicalCareSelectButton_GenerateMenu&gt;b__0</c>) que roda quando o
/// jogador escolhe, quadros depois de o botão ter sido desenhado.</para>
/// </summary>
[HarmonyPatch]
public static class VigiarMenuDeCuidadoMedico
{
    static System.Reflection.MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(MedicalCareUtility), "MedicalCareSelectButton_GenerateMenu");

    [HarmonyPostfix]
    public static IEnumerable<Widgets.DropdownMenuElement<MedicalCareCategory>> Depois(
        IEnumerable<Widgets.DropdownMenuElement<MedicalCareCategory>> __result, Pawn p) =>
        CamposObservados.EnvolverMenu(__result, p, "cuidadoMedico");
}

/// <summary>O menu de resposta a hostilidade: atacar, fugir, ignorar.</summary>
[HarmonyPatch]
public static class VigiarMenuDeHostilidade
{
    static System.Reflection.MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(HostilityResponseModeUtility), "DrawResponseButton_GenerateMenu");

    [HarmonyPostfix]
    public static IEnumerable<Widgets.DropdownMenuElement<HostilityResponseMode>> Depois(
        IEnumerable<Widgets.DropdownMenuElement<HostilityResponseMode>> __result, Pawn p) =>
        CamposObservados.EnvolverMenu(__result, p, "respostaAHostilidade");
}
