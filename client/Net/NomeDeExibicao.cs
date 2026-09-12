using System;
using Verse;
using Verse.Steam;

namespace WithFriends.Client.Net;

/// <summary>
/// Nome mostrado aos outros jogadores.
///
/// §17.5 na prática: se a Steam estiver disponível, usamos o nome dela como
/// **melhoria**. Sem Steam — instalação GOG, cliente fechado, modo offline —
/// nada quebra, só cai para o nome do usuário do sistema.
/// </summary>
public static class NomeDeExibicao
{
    public static string Obter()
    {
        try
        {
            if (SteamManager.Initialized)
            {
                // Sem Steam esta propriedade devolve "???" em vez de falhar.
                string nome = SteamUtility.SteamPersonaName;
                if (!string.IsNullOrWhiteSpace(nome) && nome != "???") return nome;
            }
        }
        catch (Exception)
        {
            // Steamworks ausente ou não inicializado: seguimos sem ele.
        }

        return Environment.UserName is { Length: > 0 } usuario ? usuario : "jogador";
    }
}
