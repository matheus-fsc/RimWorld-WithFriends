// Inspirado em Source/Client/Patches/VTRSyncPatch.cs de rwmt/Multiplayer, MIT,
// Copyright (c) 2018 Zetrith. Ver THIRD_PARTY/Multiplayer-MIT.txt

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A bala não pode andar mais depressa para quem está olhando.
///
/// <para><b>O buraco.</b> O RimWorld 1.6 trouxe ritmo de atualização variável:
/// cada <c>Thing</c> tem <c>UpdateRateTicks</c>, e quem está fora da tela é
/// atualizado de 15 em 15 ticks. A base deriva isso de
/// <c>GenTicks.GetCameraUpdateRate</c>, que nós já neutralizamos. Mas
/// <c>Projectile</c> <b>sobrescreve o getter</b> e nunca passa por lá:</para>
///
/// <code>
/// public override int UpdateRateTicks =>
///     Spawned &amp;&amp; Find.CurrentMap == Map &amp;&amp; Find.CameraDriver.InViewOf(this) ? 1 : 15;
/// </code>
///
/// <para>Dois jogadores olhando para pontos diferentes do mapa: a bala anda de
/// tick em tick para um e de quinze em quinze para o outro. <c>ticksToImpact</c>
/// é decrementado pelo delta, e o impacto cai em ticks diferentes.</para>
///
/// <para>Medido, nos dois diários da mesma visita — os ticks em que houve
/// <c>Bullet.Impact</c>:</para>
///
/// <code>
/// A:  2309, 2312, 2316, 2348
/// B:  2306, 2309,       2316
/// </code>
///
/// <para>E o rastreio de RNG mostrava só a consequência: <c>RoundRandom</c> em
/// <c>DamageWorker.Apply</c> e em <c>DropFilthDueToDamage</c>, os dois sob
/// <c>Bullet.Impact</c>. É por isso que <b>todo</b> desync medido foi em combate:
/// fora dele não há projétil.</para>
///
/// <para>É a mesma forma que já nos mordeu com o designador de construção — a
/// base remendada e a sobrescrita passando por baixo. Remendar a fonte cobre os
/// chamadores dela, não quem a substitui.</para>
///
/// <para><b>Como o Multiplayer trata.</b> Para ele o ritmo é estado
/// <b>compartilhado</b>: <c>VTRSync.GetSynchronizedUpdateRate</c> devolve o VTR
/// do componente de tempo daquele mapa, e trocar de mapa observado vira
/// <b>comando de rede</b> — todos os clientes precisam concordar sobre quais
/// mapas têm alguém olhando. Ele não deriva o ritmo; ele o negocia.</para>
///
/// <para>Aqui não há esse maquinário, e para uma visita não precisa haver: um
/// valor fixo é igual dos dois lados, que é a única propriedade que importa.
/// Projétil fica em 1 — são poucos, vivem pouco, e é o ritmo com que o jogo os
/// simula quando alguém está vendo, ou seja, o de maior fidelidade.</para>
/// </summary>
[HarmonyPatch(typeof(Projectile), nameof(Projectile.UpdateRateTicks), MethodType.Getter)]
public static class RitmoDoProjetilForaDaCamera
{
    [HarmonyPrefix]
    public static bool Antes(ref int __result)
    {
        if (!RelogioDeSessaoRimWorld.EmSessao) return true;

        GuardasDeDeterminismo.Disparou("Projectile.UpdateRateTicks (câmera)");
        __result = 1;
        return false;
    }
}

/// <summary>
/// O mesmo para objetos do mundo, que decidem por "o planeta está aberto?".
///
/// <code>
/// protected virtual int UpdateRateTicks => WorldRendererUtility.WorldSelected ? 1 : 15;
/// </code>
///
/// <para>Um jogador abrir o mapa do planeta não pode mudar o ritmo com que as
/// coisas do mundo são simuladas — e <c>World.WorldTick</c> roda dentro do nosso
/// passo de sessão.</para>
///
/// <para>Fixo em 15, que é o valor de "ninguém olhando": durante uma visita o
/// que importa acontece no mapa, e o mundo não precisa de fidelidade de tick em
/// tick. O que ele precisa é ser igual dos dois lados.</para>
///
/// <para>Por <c>TargetMethods</c>, alcançando as sobrescritas: foi assim que o
/// <c>Projectile</c> escapou da vez passada.</para>
/// </summary>
[HarmonyPatch]
public static class RitmoDoMundoForaDaCamera
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var tipo in typeof(WorldObject).AllSubclasses().AddItem(typeof(WorldObject)))
        {
            var getter = AccessTools.DeclaredPropertyGetter(tipo, "UpdateRateTicks");
            if (getter != null) yield return getter;
        }
    }

    [HarmonyPrefix]
    public static bool Antes(ref int __result)
    {
        if (!RelogioDeSessaoRimWorld.EmSessao) return true;

        GuardasDeDeterminismo.Disparou("WorldObject.UpdateRateTicks (planeta aberto)");
        __result = 15;
        return false;
    }
}
