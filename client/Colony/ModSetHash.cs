using System.Linq;
using Verse;
using WithFriends.Protocol;

namespace WithFriends.Client.Colony;

/// <summary>
/// Hash do conjunto de mods. Por ora cobre todos os ativos, em ordem de carga.
///
/// A classificação por impacto (§8) — <c>world</c> / <c>session</c> /
/// <c>client</c> — entra aqui quando M3 chegar. Enquanto isso o hash é só
/// metadado de checkpoint e **não** bloqueia login: o enforcement tudo-ou-nada
/// do RT chegou a impedir o login por causa de um mod cosmético (§15.5).
/// </summary>
public static class ModSetHash
{
    public static string Calcular()
    {
        var ids = ModsConfig.ActiveModsInLoadOrder
            .Select(m => m.PackageIdPlayerFacing)
            .ToArray();
        return Hashing.OfString(string.Join("\n", ids));
    }
}
