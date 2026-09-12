using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Durante a visita, **só os mapas da sessão tickam** — ADR 0009.
///
/// É o que torna válido o push/pop único de RNG: se a colônia privada de cada
/// um continuasse simulando, ela consumiria quantidades diferentes de números
/// aleatórios e o contador divergiria por construção.
///
/// O RimWorld não ticka por mapa: as <c>thing lists</c> são globais e misturam
/// coisas de todos os mapas. O Multiplayer resolve isso dando tick lists
/// próprias a cada mapa (~2.280 linhas). Aqui a necessidade é menor — não
/// queremos linhas do tempo independentes, queremos **parar** o que não é da
/// sessão — então bastam três prefixos que devolvem <c>false</c>.
/// </summary>
public static class CongeladorDeMapas
{
    static readonly HashSet<int> MapasDaSessao = new();

    public static bool Ativo { get; private set; }

    public static void Congelar(params int[] mapasDaSessao)
    {
        MapasDaSessao.Clear();
        foreach (int id in mapasDaSessao) MapasDaSessao.Add(id);
        Ativo = true;

        Log.Message(
            $"[WithFriends] mapas congelados fora da sessão. Simulando apenas: " +
            $"[{string.Join(", ", mapasDaSessao)}]");
    }

    public static void Liberar()
    {
        if (!Ativo) return;
        MapasDaSessao.Clear();
        Ativo = false;
        Log.Message("[WithFriends] mapas liberados — o jogo volta a simular tudo");
    }

    public static IReadOnlyCollection<int> Ids => MapasDaSessao;

    /// <summary>Fora de sessão isto é sempre <c>true</c> na primeira comparação.</summary>
    public static bool PodeSimular(Map? mapa) =>
        !Ativo || (mapa != null && MapasDaSessao.Contains(mapa.uniqueID));
}

/// <summary>
/// Dois papéis no mesmo prefixo, porque é o mesmo ponto de corte:
///
/// 1. congelar o que não é da sessão (ADR 0009);
/// 2. **manter o cosmético fora do estado determinístico.**
///
/// Motes — fumaça, faíscas, números de dano — são <c>Thing</c> como qualquer
/// outra e tickam como qualquer outra, mas **não são salvos**. Duas instâncias
/// da mesma partida nunca terão o mesmo conjunto deles: o anfitrião está
/// rodando há minutos, o visitante acabou de carregar.
///
/// Se o tick de um mote consumir o RNG da sessão, a divergência é garantida e
/// não tem conserto possível — não dá para sincronizar fumaça. Então o mote
/// ticka com o RNG **do processo**, e o estado da sessão nem sente.
/// </summary>
[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
public static class CongelarThings
{
    [HarmonyPrefix]
    public static bool Antes(Thing __instance, out bool __state)
    {
        __state = false;

        if (CongeladorDeMapas.Ativo && !CongeladorDeMapas.PodeSimular(__instance.MapHeld))
            return false;

        if (RngDeSessao.Ativo && __instance is Mote)
        {
            RngDeSessao.SairDoTickCosmetico();
            __state = true;
        }

        return true;
    }

    [HarmonyPostfix]
    public static void Depois(bool __state)
    {
        if (__state) RngDeSessao.VoltarAoTickCosmetico();
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
public static class CongelarMapaPreTick
{
    [HarmonyPrefix]
    public static bool Antes(Map __instance) => CongeladorDeMapas.PodeSimular(__instance);
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
public static class CongelarMapaPostTick
{
    [HarmonyPrefix]
    public static bool Antes(Map __instance) => CongeladorDeMapas.PodeSimular(__instance);
}
