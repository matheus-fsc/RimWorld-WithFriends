using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Os ajustes de pawn — prioridade de trabalho, área, mestre, seguir.
///
/// <para><b>De onde veio a lista.</b> Do mapa de decisões do Multiplayer
/// (<c>wf decisoes</c>), família <b>pawn</b>: 31 registros, e estes são os que
/// uma visita de minutos exercita de verdade. Reusar a lista é reusar
/// conhecimento de domínio — cada linha dela foi paga com o bug de alguém.</para>
///
/// <para><b>Por que importa.</b> Não é enfeite: prioridade de trabalho e
/// restrição de área mudam <b>o que o colono faz no tick seguinte</b>. É
/// exatamente a classe de divergência que custou o dia — um lado escolhendo
/// <c>Clean</c> e o outro <c>BuildRoof</c>. Com o ajuste virando comando, os
/// dois lados decidem a partir da mesma configuração.</para>
///
/// <para><b>A forma.</b> Mesma ideia de <see cref="AlternaveisDeSessao"/> um
/// degrau acima: lá é tudo <c>bool</c>; aqui as formas variam — inteiro com um
/// def (prioridade), referência (área, mestre), booleano (seguir). O payload
/// <c>(pawn, chave, número, texto)</c> cobre os quatro, e quem sabe o que a
/// chave quer dizer é o registro abaixo. Acrescentar o próximo custa uma
/// entrada, sem tocar no protocolo.</para>
///
/// <para><b>O que não cabe.</b> <c>followDrafted</c> e <c>followFieldwork</c>
/// são <b>campos públicos</b>, não propriedades — não há setter para remendar.
/// Por isso a intercepção é no chamador (<c>PawnColumnWorker_*.SetValue</c>),
/// que é o que o Multiplayer também faz. Remendar a fonte é a regra (ADR 0015),
/// mas quando não há fonte, não há.</para>
/// </summary>
public static class AjustesDePawn
{
    public delegate void Aplicador(Pawn pawn, int numero, string texto);

    static readonly Dictionary<string, Aplicador> Registro = new()
    {
        // Prioridade de trabalho: o número é a prioridade, o texto é o def.
        ["prioridade"] = (pawn, numero, texto) =>
        {
            var tipo = DefDatabase<WorkTypeDef>.GetNamedSilentFail(texto);
            if (tipo == null || pawn.workSettings == null) return;
            ComoSistema(() => pawn.workSettings.SetPriority(tipo, numero));
        },

        // Restrição de área: o número é o id da área, -1 para nenhuma.
        ["area"] = (pawn, numero, _) =>
        {
            if (pawn.playerSettings == null) return;

            var area = numero < 0
                ? null
                : pawn.Map?.areaManager?.AllAreas.FirstOrDefault(a => a.ID == numero);

            ComoSistema(() => pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area);
        },

        // Mestre do animal: o número é o id do pawn, -1 para nenhum.
        ["mestre"] = (pawn, numero, _) =>
        {
            if (pawn.playerSettings == null) return;
            var mestre = numero < 0 ? null : ComandoDeSessao.EncontrarPawn(numero);
            ComoSistema(() => pawn.playerSettings.Master = mestre);
        },

        // Campos públicos: escrita direta, sem setter no caminho.
        ["seguirAlistado"] = (pawn, numero, _) =>
        {
            if (pawn.playerSettings != null) pawn.playerSettings.followDrafted = numero != 0;
        },

        ["seguirTrabalho"] = (pawn, numero, _) =>
        {
            if (pawn.playerSettings != null) pawn.playerSettings.followFieldwork = numero != 0;
        },
    };

    /// <summary>
    /// Enquanto vale, os setters remendados deixam passar: é o comando
    /// chegando, não o jogador clicando. Mesmo cuidado de
    /// <see cref="AlternaveisDeSessao.Aplicando"/>, pelo mesmo motivo.
    /// </summary>
    public static bool Aplicando { get; private set; }

    static void ComoSistema(Action escrever)
    {
        Aplicando = true;
        try { escrever(); }
        finally { Aplicando = false; }
    }

    public static string Aplicar(int pawnId, string chave, int numero, string texto)
    {
        if (!Registro.TryGetValue(chave, out var aplicar))
            return $"ajuste desconhecido ({chave}) — versões diferentes do mod?";

        var pawn = ComandoDeSessao.EncontrarPawn(pawnId);
        if (pawn == null) return $"pawn {pawnId} não encontrado para {chave}";

        aplicar(pawn, numero, texto);
        return $"{pawn.LabelShortCap}: {chave} → {numero}{(texto.Length > 0 ? $" ({texto})" : "")}";
    }

    public static byte[] Comando(int pawnId, string chave, int numero, string texto = "")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Protocol.Messages.TipoDeComando.AjusteDePawn);
        w.Write(pawnId);
        w.Write(chave);
        w.Write(numero);
        w.Write(texto);
        return ms.ToArray();
    }

    /// <summary>
    /// Vira comando e recusa a escrita local. <c>true</c> quando o original
    /// deve rodar mesmo assim.
    /// </summary>
    public static bool DeixarPassar(Pawn? pawn, string chave, int numero, string texto = "")
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando || Aplicando) return true;

        // Só o clique. O jogo mexe nessas mesmas coisas por dentro — o mestre
        // sai quando o animal é vendido, a área cai quando é apagada — e aquilo
        // é simulação, que já acontece igual nos dois lados.
        if (!NaInterface.Agora) return true;
        if (pawn == null) return true;

        if (!PosseDePawns.EhMeu(pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(pawn);
            return false;
        }

        GuardasDeDeterminismo.Disparou($"ajuste {chave} virou comando");

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = Comando(pawn.thingIDNumber, chave, numero, texto),
        });

        Log.Message(
            $"[WithFriends] {pawn.LabelShortCap}: {chave} → {numero}" +
            $"{(texto.Length > 0 ? $" ({texto})" : "")} proposto como comando");
        return false;
    }
}

/// <summary>
/// Prioridade de trabalho. Muda o que o colono faz no tick seguinte — é a
/// decisão de pawn com maior alcance depois da ordem direta.
/// </summary>
[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
public static class PrioridadeViraComando
{
    static readonly System.Reflection.FieldInfo? CampoDoPawn =
        AccessTools.Field(typeof(Pawn_WorkSettings), "pawn");

    [HarmonyPrefix]
    public static bool Antes(Pawn_WorkSettings __instance, WorkTypeDef w, int priority) =>
        AjustesDePawn.DeixarPassar(
            CampoDoPawn?.GetValue(__instance) as Pawn, "prioridade", priority, w?.defName ?? "");
}

/// <summary>
/// Restrição de área. Decide para onde o pawn pode ir, e portanto que trabalho
/// ele consegue pegar.
/// </summary>
[HarmonyPatch(typeof(Pawn_PlayerSettings),
    nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
public static class AreaViraComando
{
    static readonly System.Reflection.FieldInfo? CampoDoPawn =
        AccessTools.Field(typeof(Pawn_PlayerSettings), "pawn");

    [HarmonyPrefix]
    public static bool Antes(Pawn_PlayerSettings __instance, Area value) =>
        AjustesDePawn.DeixarPassar(
            CampoDoPawn?.GetValue(__instance) as Pawn, "area", value?.ID ?? -1);
}

/// <summary>Mestre do animal: decide a quem ele obedece.</summary>
[HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.Master), MethodType.Setter)]
public static class MestreViraComando
{
    static readonly System.Reflection.FieldInfo? CampoDoPawn =
        AccessTools.Field(typeof(Pawn_PlayerSettings), "pawn");

    [HarmonyPrefix]
    public static bool Antes(Pawn_PlayerSettings __instance, Pawn value) =>
        AjustesDePawn.DeixarPassar(
            CampoDoPawn?.GetValue(__instance) as Pawn, "mestre", value?.thingIDNumber ?? -1);
}

/// <summary>
/// "Seguir quando alistado" e "seguir no trabalho".
///
/// <para>Remendo no <b>chamador</b>, contra a regra da ADR 0015, porque não há
/// fonte: <c>followDrafted</c> e <c>followFieldwork</c> são campos públicos, e
/// campo não tem setter. O Multiplayer registra os mesmos dois chamadores pelo
/// mesmo motivo.</para>
/// </summary>
[HarmonyPatch]
public static class SeguirViraComando
{
    static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var tipo in new[]
                 {
                     typeof(PawnColumnWorker_FollowDrafted),
                     typeof(PawnColumnWorker_FollowFieldwork),
                 })
        {
            var m = AccessTools.Method(tipo, "SetValue");
            if (m != null) yield return m;
        }
    }

    [HarmonyPrefix]
    public static bool Antes(System.Reflection.MethodBase __originalMethod, Pawn pawn, bool value)
    {
        string chave = __originalMethod.DeclaringType == typeof(PawnColumnWorker_FollowDrafted)
            ? "seguirAlistado"
            : "seguirTrabalho";

        return AjustesDePawn.DeixarPassar(pawn, chave, value ? 1 : 0);
    }
}
