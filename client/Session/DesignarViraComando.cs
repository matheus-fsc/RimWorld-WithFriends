using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;
using WithFriends.Client.Colony;

namespace WithFriends.Client.Session;

/// <summary>
/// Construir, minerar, cortar, demolir e cancelar viram comando durante a visita.
///
/// <para>Era o outro metade do que o jogador faz com o mouse — a primeira,
/// a ordem direta, já passava por <see cref="OrdemViraComando"/>. Designar não
/// passava por lugar nenhum: acontecia na hora, só do lado de quem clicou.</para>
///
/// <para><b>Com god mode fica pior.</b> <c>Designator_Build.DesignateSingleCell</c>
/// consulta <c>DebugSettings.godMode</c>: ligado, a construção nasce pronta via
/// <c>GenSpawn.Spawn</c>; desligado, vira blueprint. São dois efeitos diferentes
/// no mapa, e o god mode de cada jogador é dele. Por isso o comando carrega o
/// god mode **de quem clicou** — concordar sobre o comando não adianta se os
/// dois lados discordam sobre o que ele faz.</para>
///
/// <para>Fora de sessão nada disso roda: o designador age na hora, como sempre.</para>
/// </summary>
public static class DesignarViraComando
{
    /// <summary>
    /// <c>true</c> se o clique deve virar comando em vez de acontecer agora.
    /// </summary>
    /// <summary>
    /// Esta designação veio do jogador, e não do jogo?
    ///
    /// <para>Mesmo guard que o alistar e as ordens já tinham, e que aqui
    /// faltava. O Multiplayer abre os três interceptadores dele com a mesma
    /// linha (<c>if (!Multiplayer.InInterface) return true;</c>), e por bom
    /// motivo: designador chamado de dentro do jogo é simulação andando, não
    /// clique. Tratar como clique bloqueia a lógica do jogo e manda comando que
    /// ninguém pediu.</para>
    /// </summary>
    static bool DoJogador(out SessaoCliente? sessao)
    {
        sessao = SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return false;
        if (ComandoDeSessao.Aplicando) return false;   // é o comando chegando

        return NaInterface.Agora;
    }

    static bool Interceptar(Designator designador, LocalTargetInfo alvo)
    {
        if (!DoJogador(out var sessao)) return false;

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao!.Atual!.SessaoId,
            Payload = ComandoDeSessao.Designar(designador, alvo),
        });

        Log.Message($"[WithFriends] {designador.GetType().Name} proposto como comando");
        return true;   // o designador só age quando voltar carimbado
    }

    /// <summary>
    /// O método na classe base **e todas as sobrescritas**.
    ///
    /// <para>Este é o erro que deixou o arquiteto passar batido. Remendar
    /// <c>Designator.DesignateSingleCell</c> não alcança
    /// <c>Designator_Build.DesignateSingleCell</c>: são corpos de método
    /// diferentes, e o Harmony remenda corpos, não contratos. Projetar no
    /// arquiteto chamava a sobrescrita, que nunca passou por aqui — o log da
    /// sessão mostrou 25 comandos e nenhum designador entre eles.</para>
    /// </summary>
    static IEnumerable<MethodBase> Sobrescritas(string nome, params System.Type[] assinatura)
    {
        var tipos = typeof(Designator).AllSubclasses().Concat(new[] { typeof(Designator) });

        var alvos = tipos
            .Select(t => (MethodBase?)AccessTools.DeclaredMethod(t, nome, assinatura))
            .Where(m => m != null)
            .ToList();

        // Vale a pena dizer o número em voz alta: se uma atualização do jogo
        // mudar a assinatura, ele cai para 1 (só a base) e o arquiteto volta a
        // escapar em silêncio — que foi exatamente o bug.
        Log.Message($"[WithFriends] {alvos.Count} implementação(ões) de {nome} viram comando em sessão");
        return alvos!;
    }

    [HarmonyPatch]
    public static class EmCelula
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            Sobrescritas(nameof(Designator.DesignateSingleCell), typeof(IntVec3));

        // `__0` e não `c`: o Harmony casa parâmetro de prefixo **por nome**, e
        // o nome muda entre as sobrescritas — a base chama de `c`,
        // `Designator_Deconstruct` chama de `loc`. Com o nome, o remendo
        // estourava naquela classe.
        [HarmonyPrefix]
        public static bool Antes(Designator __instance, IntVec3 __0)
        {
            return !Interceptar(__instance, new LocalTargetInfo(__0));
        }
    }

    /// <summary>
    /// O arrasto inteiro num comando só.
    ///
    /// <para>Sem isto, cada célula do arrasto virava um comando: uma parede de
    /// 40 células eram 40 comandos, 40 carimbos e — com o jogo pausado, onde
    /// cada comando ganha um passo de um tick — 40 passos. Dava a impressão de
    /// o jogo estar construindo "um por um" e de perder ordens pelo caminho.</para>
    ///
    /// <para>A implementação da base já percorre as células chamando
    /// <c>DesignateSingleCell</c>; interceptar aqui, **antes** do laço, troca N
    /// comandos por um.</para>
    /// </summary>
    [HarmonyPatch]
    public static class EmVariasCelulas
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            Sobrescritas(nameof(Designator.DesignateMultiCell), typeof(IEnumerable<IntVec3>));

        [HarmonyPrefix]
        public static bool Antes(Designator __instance, IEnumerable<IntVec3> __0)
        {
            if (!DoJogador(out var sessao)) return true;

            var celulas = __0 as IReadOnlyList<IntVec3> ?? new List<IntVec3>(__0);
            if (celulas.Count == 0) return true;

            WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
            {
                SessaoId = sessao!.Atual!.SessaoId,
                Payload = ComandoDeSessao.DesignarVarias(__instance, celulas),
            });

            // Quantas o jogo aceitaria AQUI, na hora do arrasto. Comparando com
            // o número do outro lado, dá para ver se o estado mudou no caminho.
            int aceitasAqui = 0;
            foreach (var celula in celulas)
                if (__instance.CanDesignateCell(celula).Accepted) aceitasAqui++;

            Log.Message(
                $"[WithFriends] {__instance.GetType().Name} em {celulas.Count} célula(s) " +
                $"proposto como um comando só (o jogo aceitaria {aceitasAqui} aqui)");
            return false;
        }
    }

    [HarmonyPatch]
    public static class EmCoisa
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            Sobrescritas(nameof(Designator.DesignateThing), typeof(Thing));

        [HarmonyPrefix]
        public static bool Antes(Designator __instance, Thing __0)
        {
            return !Interceptar(__instance, new LocalTargetInfo(__0));
        }
    }
}
