using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using WithFriends.Client.Colony;

namespace WithFriends.Client.Session;

/// <summary>
/// De quem é cada pawn durante a visita.
///
/// <para><b>A regra do mod.</b> O visitante comanda os pawns que ele trouxe, e
/// só eles. O anfitrião comanda os da casa, e só eles. Ninguém mexe nos colonos
/// do outro — é o que dá sentido a "visita": você trouxe gente para ajudar, não
/// assumiu o controle da colônia alheia.</para>
///
/// <para><b>Onde a posse mora.</b> Num dicionário do
/// <see cref="SincronizacaoComponent"/>, que é <c>GameComponent</c> e tem
/// <c>ExposeData</c>. Isso não é detalhe: a visita transfere a partida inteira
/// do anfitrião (ADR 0010), então tudo que estiver no save chega ao outro lado
/// sozinho, com os mesmos ids. A posse viaja de carona, sem mensagem nova e sem
/// chance de os dois lados discordarem.</para>
///
/// <para><b>O padrão é o anfitrião.</b> Pawn sem dono registrado é da casa — ele
/// já estava lá antes de alguém chegar. Só quem entra pela mala do visitante
/// ganha registro.</para>
///
/// <para>Fora de sessão isto não vale para nada: a colônia é sua e os pawns
/// também.</para>
/// </summary>
public static class PosseDePawns
{
    /// <summary>Registra que este pawn é de quem chegou com ele.</summary>
    public static void Registrar(IEnumerable<Pawn> pawns, string playerId)
    {
        var estado = SincronizacaoComponent.Atual;
        if (estado == null) return;

        foreach (var pawn in pawns)
            estado.DonoPorPawn[pawn.thingIDNumber] = playerId;

        Log.Message(
            $"[WithFriends] posse registrada: {pawns.Count()} pawn(s) de {playerId}");
    }

    public static void Limpar() => SincronizacaoComponent.Atual?.DonoPorPawn.Clear();

    /// <summary>
    /// Quem manda neste pawn. <c>null</c> fora de sessão — aí não há dono
    /// porque não há disputa.
    /// </summary>
    public static string? Dono(Pawn pawn)
    {
        var sessao = SincronizacaoComponent.Atual?.Sessao;
        if (sessao?.Atual == null) return null;

        var mapa = SincronizacaoComponent.Atual!.DonoPorPawn;
        return mapa.TryGetValue(pawn.thingIDNumber, out var dono) ? dono : sessao.Atual.Anfitriao;
    }

    /// <summary>Este pawn responde a mim?</summary>
    public static bool EhMeu(Pawn? pawn)
    {
        if (pawn == null) return false;

        string? dono = Dono(pawn);
        if (dono == null) return true;   // fora de sessão, tudo é seu

        return dono == WithFriendsMod.Settings.PlayerIdOuNovo();
    }

    /// <summary>
    /// Recusa visível. Sem isto o jogador clica, nada acontece, e ele não sabe
    /// se o mod travou ou se a ordem não era dele para dar.
    /// </summary>
    static readonly Dictionary<int, float> ultimoAviso = new();

    /// <summary>Intervalo mínimo entre dois avisos sobre o mesmo pawn.</summary>
    const float SegundosEntreAvisos = 3f;

    public static void AvisarQueNaoEhSeu(Pawn pawn)
    {
        // Repetir o mesmo aviso muitas vezes por segundo não informa mais, só
        // atrapalha — e foi assim que ele apareceu, como texto permanente.
        if (ultimoAviso.TryGetValue(pawn.thingIDNumber, out float quando)
            && UnityEngine.Time.realtimeSinceStartup - quando < SegundosEntreAvisos)
            return;

        ultimoAviso[pawn.thingIDNumber] = UnityEngine.Time.realtimeSinceStartup;

        Messages.Message(
            $"With Friends: {pawn.LabelShort} não é seu. Numa visita, cada um comanda " +
            "os próprios pawns.",
            MessageTypeDefOf.RejectInput, historical: false);
    }
}
