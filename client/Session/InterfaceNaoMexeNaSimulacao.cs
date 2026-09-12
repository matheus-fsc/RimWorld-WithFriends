// Adaptado de Source/Client/Patches/Determinism.cs (TryGetMeleeVerbPatch)
// de rwmt/Multiplayer, commit 4a3be27, MIT, Copyright (c) 2018 Zetrith
// Ver THIRD_PARTY/Multiplayer-MIT.txt

using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A interface pode **perguntar**, mas não pode **mudar** a simulação.
///
/// <c>Pawn_MeleeVerbs.TryGetMeleeVerb</c> escolhe um verbo de ataque ao acaso e
/// guarda o resultado em cache. Quem chama, entre outros, é o menu flutuante —
/// ou seja, **passar o mouse sobre um inimigo altera o estado do pawn**.
///
/// Numa visita, um jogador passa o mouse e o outro não. O pawn fica com verbos
/// diferentes em cache nos dois lados, e o próximo ataque diverge.
///
/// Dentro da interface, a resposta passa a ser determinística: o primeiro verbo
/// com peso de seleção não nulo, sem sortear e sem cachear.
/// </summary>
[HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryGetMeleeVerb))]
public static class InterfaceNaoEscolheVerbo
{
    [HarmonyPrefix]
    public static bool Antes() => !NaInterface.Agora;

    [HarmonyPostfix]
    public static void Depois(Pawn_MeleeVerbs __instance, Thing target, ref Verb __result)
    {
        if (!NaInterface.Agora) return;

        __result = __instance
            .GetUpdatedAvailableVerbsList(false)
            .FirstOrDefault(v => v.GetSelectionWeight(target) != 0)
            .verb;
    }
}
