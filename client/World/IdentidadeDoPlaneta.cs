using System.Globalization;
using RimWorld.Planet;
using Verse;
using WithFriends.Protocol;

namespace WithFriends.Client.World;

/// <summary>
/// Identidade do planeta: o hash das variáveis que o geram.
///
/// O mundo compartilhado da §2.1 pressupõe **o mesmo planeta**. Um evento diz
/// "assentamento no tile 113533" — e tile é índice, não coordenada. Em outro
/// planeta esse índice ou aponta para outro lugar, ou não existe.
///
/// Não é preciso sincronizar o planeta: ele é determinístico a partir da
/// semente e das opções de geração, todas escolhidas pelo jogador na criação
/// do mundo. Basta conferir que são as mesmas.
/// </summary>
public static class IdentidadeDoPlaneta
{
    public static string Calcular()
    {
        var info = Find.World?.info;
        if (info == null) return "";

        // Tudo o que entra na geração. Mudar qualquer um destes muda os tiles,
        // e portanto muda o significado de todo evento de mundo.
        string variaveis = string.Join("|",
            info.seedString,
            info.planetCoverage.ToString("R", CultureInfo.InvariantCulture),
            info.overallRainfall,
            info.overallTemperature,
            info.overallPopulation,
            info.landmarkDensity,
            info.pollution.ToString("R", CultureInfo.InvariantCulture));

        return Hashing.OfString(variaveis);
    }

    public static string Descrever()
    {
        var info = Find.World?.info;
        if (info == null) return "(sem mundo carregado)";

        return
            $"semente \"{info.seedString}\", cobertura {info.planetCoverage:P0}, " +
            $"{Find.WorldGrid?.TilesCount ?? 0} tiles, chuva {info.overallRainfall}, " +
            $"temperatura {info.overallTemperature}, população {info.overallPopulation}";
    }

    /// <summary>
    /// Um tile só é utilizável se existir **neste** planeta. Sem esta
    /// checagem, um índice de outro planeta vai direto para dentro do
    /// WorldGrid e o jogo quebra longe da causa.
    /// </summary>
    public static bool TileExiste(int tile) =>
        tile >= 0 && Find.WorldGrid != null && Find.WorldGrid.InBounds(new PlanetTile(tile));
}
