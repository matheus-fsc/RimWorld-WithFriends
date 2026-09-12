using Verse;
using WithFriends.Client.Colony;
using WithFriends.Transport;

namespace WithFriends.Client.Net;

/// <summary>Ponte entre as settings e o <see cref="ClienteCoordenador"/>.</summary>
public static class Conexao
{
    public const string EnderecoLocal = "[::1]:25555";

    /// <summary>
    /// Conecta usando o endereço das settings. Devolve o erro legível em vez
    /// de lançar: quem chama é UI ou debug action, e os dois querem mostrar.
    /// </summary>
    public static bool Conectar(out string erro)
    {
        string texto = WithFriendsMod.Settings.endereco;
        if (string.IsNullOrWhiteSpace(texto))
        {
            erro = "Endereço vazio. Configure em Opções → Mod settings → With Friends.";
            return false;
        }

        if (!EnderecoServidor.TentarAnalisar(texto, out var endereco, out erro))
            return false;

        WithFriendsMod.Cliente.Conectar(
            endereco,
            WithFriendsMod.Settings.PlayerIdOuNovo(),
            NomeDeExibicao.Obter());
        Log.Message($"[WithFriends] conectando em {endereco}…");
        return true;
    }
}
