using System.Text.Json;
using WithFriends.Protocol;

namespace WithFriends.Server.Colonias;

/// <summary>
/// Leitura do índice append-only de uma colônia. Fica separado porque duas
/// coisas precisam dele: o histórico (para restauração e retenção) e o
/// monitor (para reconstruir a referência depois de um reinício).
/// </summary>
public static class IndiceCheckpoints
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Serializar(EntradaCheckpoint entrada) =>
        JsonSerializer.Serialize(entrada, Json);

    public static IReadOnlyList<EntradaCheckpoint> Ler(IArmazenamento armazenamento, ColonyIdentity identity)
    {
        var entradas = new List<EntradaCheckpoint>();

        foreach (string linha in armazenamento.LerIndice(identity))
        {
            try
            {
                var entrada = JsonSerializer.Deserialize<EntradaCheckpoint>(linha, Json);
                if (entrada != null) entradas.Add(entrada);
            }
            catch (JsonException e)
            {
                // Uma linha corrompida não invalida o índice inteiro: o resto
                // do histórico continua sendo evidência utilizável.
                Console.WriteLine($"!! índice de {identity}: linha ilegível ignorada ({e.Message})");
            }
        }

        return entradas;
    }
}
