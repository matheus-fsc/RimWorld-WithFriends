using System;
using Verse;

namespace WithFriends.Client.Colony;

/// <summary>
/// Identidade do jogador, por instalação. Persistida nas settings do mod,
/// não no save: um jogador tem várias colônias.
///
/// Nunca é derivada de endereço de rede — foi exatamente isso que fez saves
/// "sumirem" ao trocar o IP do servidor no RT (§15.2, §17.3 regra 5).
/// </summary>
public class WithFriendsSettings : ModSettings
{
    public string playerId = "";
    /// <summary>Endereço do coordenador, como o jogador digitou (§17.3 regra 2).</summary>
    public string endereco = "[::1]:25555";

    /// <summary>Conectar sozinho ao entrar numa partida.</summary>
    public bool conectarAoIniciar = true;

    /// <summary>Enviar checkpoint junto com todo save do jogo (§7).</summary>
    public bool checkpointAoSalvar = true;

    /// <summary>Piso entre dois envios automáticos, em minutos.</summary>
    public int intervaloMinimoMinutos = 5;

    public string PlayerIdOuNovo()
    {
        if (string.IsNullOrEmpty(playerId))
        {
            playerId = Guid.NewGuid().ToString("D");
            Write();
        }
        return playerId;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref playerId, "playerId", "");
        Scribe_Values.Look(ref endereco, "endereco", "");
        Scribe_Values.Look(ref conectarAoIniciar, "conectarAoIniciar", true);
        Scribe_Values.Look(ref checkpointAoSalvar, "checkpointAoSalvar", true);
        Scribe_Values.Look(ref intervaloMinimoMinutos, "intervaloMinimoMinutos", 5);
    }
}
