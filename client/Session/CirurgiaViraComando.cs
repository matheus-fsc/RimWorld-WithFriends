using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Marcar uma operação — anestesiar, amputar, instalar um implante.
///
/// <para><b>Como apareceu.</b> Num teste à mão, com tudo o mais passando: de
/// todos os gestos de interface, só a operação divergiu. Faz sentido — a conta
/// de cirurgia entra na pilha de trabalhos do pawn e muda, no tick seguinte, o
/// que o médico faz e o que o paciente faz. Um lado com a conta e o outro sem é
/// duas simulações.</para>
///
/// <para><b>Por que não viaja a conta, e sim a receita dela.</b> Um
/// <c>Bill_Medical</c> tem receita, parte do corpo, ingredientes reservados,
/// estado de suspensão, um id de carga. Serializar tudo isso seria um documento
/// por clique — e ainda deixaria o id nascendo de um lado só. Em vez disso viaja
/// o que o jogador escolheu (receita, parte, ingredientes) e os dois lados
/// <b>criam a conta</b> com o mesmo código do jogo, no mesmo passo. O id sai do
/// contador do save, que os dois chamam na mesma ordem.</para>
///
/// <para><b>O remendo é na fonte, e isso importa aqui mais que o normal.</b>
/// <c>CreateSurgeryBill</c> é chamado por sete lambdas de menu e também pelo
/// <b>tick</b> — <c>Pawn_GuestTracker.GuestTrackerTickInterval</c> marca
/// operações sozinho. Remendar os chamadores seria remendar sete closures e
/// ainda transformar simulação em comando. Remendando a fonte, o guarda de
/// interface separa os dois casos: o que veio do tick continua sendo simulação,
/// que já acontece igual nos dois lados.</para>
/// </summary>
public static class Cirurgia
{
    /// <summary>
    /// A parte do corpo, pelo índice na lista do corpo — não pelo nome.
    ///
    /// <para>Um corpo tem várias partes com o mesmo <c>def</c>: dois braços,
    /// dez dedos. Mandar o nome do def escolheria "o primeiro que casa", e
    /// operar o braço esquerdo de um lado e o direito do outro é exatamente a
    /// divergência que isto evita. A lista é a mesma nos dois lados porque vem
    /// do mesmo def de corpo.</para>
    /// </summary>
    static int IndiceDaParte(Pawn pawn, BodyPartRecord? parte)
    {
        if (parte == null) return -1;
        var partes = pawn.RaceProps?.body?.AllParts;
        return partes == null ? -1 : partes.IndexOf(parte);
    }

    static BodyPartRecord? ParteDoIndice(Pawn pawn, int indice)
    {
        if (indice < 0) return null;
        var partes = pawn.RaceProps?.body?.AllParts;
        return partes != null && indice < partes.Count ? partes[indice] : null;
    }

    public static bool Aplicando { get; private set; }

    public static string Aplicar(int pawnId, string receita, int indiceDaParte, int[] ingredientes)
    {
        var pawn = ComandoDeSessao.EncontrarPawn(pawnId);
        if (pawn == null) return $"pawn {pawnId} não encontrado para a operação";

        var def = DefDatabase<RecipeDef>.GetNamedSilentFail(receita);
        if (def == null) return $"receita desconhecida: {receita}";

        var coisas = ingredientes
            .Select(ComandoDeSessao.EncontrarCoisa)
            .Where(c => c != null)
            .ToList();

        Aplicando = true;
        try
        {
            HealthCardUtility.CreateSurgeryBill(
                pawn, def, ParteDoIndice(pawn, indiceDaParte),
                coisas.Count > 0 ? coisas! : null,
                sendMessages: false);
        }
        finally { Aplicando = false; }

        return $"{pawn.LabelShortCap}: operação {def.defName}" +
               (indiceDaParte >= 0 ? $" em {ParteDoIndice(pawn, indiceDaParte)?.Label}" : "");
    }

    public static byte[] Comando(int pawnId, string receita, int indiceDaParte, int[] ingredientes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Protocol.Messages.TipoDeComando.Cirurgia);
        w.Write(pawnId);
        w.Write(receita);
        w.Write(indiceDaParte);
        w.Write(ingredientes.Length);
        foreach (var id in ingredientes) w.Write(id);
        return ms.ToArray();
    }

    /// <summary>
    /// Vira comando e recusa a criação local. <c>true</c> quando o original deve
    /// rodar mesmo assim.
    /// </summary>
    public static bool DeixarPassar(Pawn? pawn, RecipeDef? receita,
                                    BodyPartRecord? parte, List<Thing>? ingredientes)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando || Aplicando) return true;

        // O tick também marca operações (o anfitrião de prisioneiro, por
        // exemplo). Aquilo é simulação e já acontece igual nos dois lados.
        if (!NaInterface.Agora) return true;
        if (pawn == null || receita == null) return true;

        // Operar um pawn é decidir sobre ele. O corte é o mesmo de toda ordem de
        // pawn: o dono manda no seu.
        if (!PosseDePawns.EhMeu(pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(pawn);
            return false;
        }

        GuardasDeDeterminismo.Disparou("operação virou comando");

        var ids = ingredientes?.Select(i => i.thingIDNumber).ToArray() ?? System.Array.Empty<int>();

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = Comando(pawn.thingIDNumber, receita.defName, IndiceDaParte(pawn, parte), ids),
        });

        Log.Message($"[WithFriends] {pawn.LabelShortCap}: operação {receita.defName} proposta como comando");
        return false;
    }
}

/// <summary>
/// A criação da conta de cirurgia — a fonte que os sete menus e o tick usam.
/// </summary>
[HarmonyPatch(typeof(HealthCardUtility), nameof(HealthCardUtility.CreateSurgeryBill))]
public static class CriarContaDeCirurgiaViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Pawn medPawn, RecipeDef recipe, BodyPartRecord part,
                             List<Thing> uniqueIngredients, ref Bill_Medical __result)
    {
        if (Cirurgia.DeixarPassar(medPawn, recipe, part, uniqueIngredients)) return true;

        // Sem conta local: ela nasce quando o comando voltar carimbado. Quem
        // chama daqui é menu de interface, e nenhum deles usa o resultado — o
        // que se vê é a conta aparecer na lista um instante depois, como
        // qualquer outro clique dentro de uma visita.
        __result = null!;
        return false;
    }
}
