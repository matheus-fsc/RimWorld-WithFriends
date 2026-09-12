using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using WithFriends.Protocol;

namespace WithFriends.Client.Colony;

/// <summary>
/// Impressão digital do estado **vivo** da simulação, barata o bastante para
/// ir em todo heartbeat (§7.1 regra 4).
///
/// Não é o hash do último checkpoint: aquele fica igual, e deve ficar, entre
/// dois checkpoints. O que precisa mudar a cada heartbeat é a prova de que a
/// simulação realmente andou.
///
/// A ideia de usar o estado do RNG como impressão digital vem do Multiplayer
/// (MIT, Zetrith) — ver §14.3: "barato de comparar e diverge imediatamente".
/// Aqui ele é lido por reflexão porque <c>Rand.StateCompressed</c> é privado;
/// se um dia sumir, os agregados públicos abaixo seguram o sinal sozinhos.
/// </summary>
public static class FingerprintVivo
{
    static readonly MethodInfo? LerEstadoRand =
        AccessTools.PropertyGetter(typeof(Rand), "StateCompressed");

    public static string Calcular()
    {
        unchecked
        {
            ulong acumulado = 14695981039346656037UL;

            void Misturar(long valor)
            {
                acumulado = (acumulado ^ (ulong)valor) * 1099511628211UL;
            }

            Misturar(EstadoRand());
            Misturar(Find.Maps.Count);

            foreach (var mapa in Find.Maps)
            {
                var pawns = mapa.mapPawns.AllPawnsSpawned;
                Misturar(mapa.uniqueID);
                Misturar(pawns.Count);

                // Posição e trabalho atual de cada pawn: muda o tempo todo
                // enquanto o jogo roda, e é estável com o jogo pausado.
                foreach (var pawn in pawns)
                {
                    Misturar(pawn.thingIDNumber);
                    Misturar(pawn.Position.x * 31 + pawn.Position.z);
                    Misturar(pawn.CurJobDef?.shortHash ?? 0);
                    Misturar((long)(pawn.health?.summaryHealth?.SummaryHealthPercent * 1000f ?? 0f));
                }
            }

            return Hashing.OfString(acumulado.ToString("x16"));
        }
    }

    static long EstadoRand()
    {
        try
        {
            if (LerEstadoRand != null)
                return (long)(ulong)LerEstadoRand.Invoke(null, null);
        }
        catch (Exception)
        {
            // Campo interno mudou de nome: seguimos só com os agregados.
        }
        return 0;
    }
}
