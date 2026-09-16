using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// As chaves liga/desliga do jogador, num registro só.
///
/// <para><b>Por que genérico.</b> O Multiplayer registra uma linha por
/// propriedade — <c>Pawn_DraftController.FireAtWill</c>,
/// <c>CompForbiddable.Forbidden</c>, <c>StorageSettings.Priority</c> e mais umas
/// dezenas. São todas a mesma forma: uma coisa do mapa, um nome, um valor. Um
/// tipo de comando por linha dessas seria oito trocas de protocolo para oito
/// booleanos.</para>
///
/// <para>Então o protocolo carrega <c>(coisa, chave, valor)</c> e quem sabe o
/// que a chave quer dizer é este registro. Acrescentar a próxima custa uma
/// entrada aqui e um remendo no setter — sem tocar no protocolo, sem teste de
/// ida e volta novo.</para>
///
/// <para><b>O que não cabe aqui.</b> Botões cuja ação é um closure que escreve
/// direto no campo privado, sem passar por propriedade nenhuma — o "manter
/// aberta" da porta é assim (<c>holdOpenInt</c> escrito de dentro do
/// <c>Command_Toggle</c>). Esses não têm fonte para remendar, e é exatamente por
/// isso que o Multiplayer tem 227 registros de lambda: para cada um deles é
/// preciso identificar o closure. Ver docs/PROGRESSO.md.</para>
/// </summary>
public static class AlternaveisDeSessao
{
    /// <summary>O que fazer com o valor quando o comando volta carimbado.</summary>
    public delegate void Aplicador(Thing dono, bool valor);

    static readonly Dictionary<string, Aplicador> Registro = new()
    {
        // Segurar fogo. O dono é o pawn; o alvo é o `drafter` dele.
        ["segurarFogo"] = (dono, valor) =>
        {
            if (dono is Pawn { drafter: not null } pawn)
                ComoSistema(() => pawn.drafter.FireAtWill = valor);
        },

        // Proibir / liberar. O dono é a própria coisa proibida.
        ["proibido"] = (dono, valor) =>
        {
            if (dono.TryGetComp<CompForbiddable>() is { } comp)
                ComoSistema(() => comp.Forbidden = valor);
        },

        // Cama médica. Decide quem deita ali quando cai ferido — e numa visita
        // cai gente das duas colônias.
        ["camaMedica"] = (dono, valor) =>
        {
            if (dono is Building_Bed cama) ComoSistema(() => cama.Medical = valor);
        },

        // Ponto de encontro (fogueira, mesa): liga e desliga o lugar para onde
        // os colonos vão descansar em grupo.
        ["pontoDeEncontro"] = (dono, valor) =>
        {
            if (dono.TryGetComp<CompGatherSpot>() is { } comp)
                ComoSistema(() => comp.Active = valor);
        },

        // "Não cortar esta planta": tira o trabalho da lista de quem colhe.
        ["naoCortar"] = (dono, valor) =>
        {
            if (dono.TryGetComp<CompPlantPreventCutting>() is { } comp)
                ComoSistema(() => comp.PreventCutting = valor);
        },
    };

    /// <summary>
    /// Enquanto isto vale, o setter remendado deixa passar: é o comando
    /// chegando, não o jogador clicando.
    ///
    /// <para>Sem isto a aplicação do comando bateria no próprio remendo e
    /// proporia o comando de novo, para sempre. É o mesmo cuidado de
    /// <c>ControleDeVelocidade.ComoSistema</c>, pelo mesmo motivo.</para>
    /// </summary>
    public static bool Aplicando { get; private set; }

    static void ComoSistema(Action escrever)
    {
        Aplicando = true;
        try { escrever(); }
        finally { Aplicando = false; }
    }

    /// <summary>Aplica um comando já carimbado. Devolve o resumo para o log.</summary>
    public static string Aplicar(int coisaId, string chave, bool valor)
    {
        if (!Registro.TryGetValue(chave, out var aplicar))
            return $"chave desconhecida ({chave}) — versões diferentes do mod?";

        var dono = ComandoDeSessao.EncontrarCoisa(coisaId);
        if (dono == null) return $"coisa {coisaId} não encontrada para {chave}";

        aplicar(dono, valor);
        return $"{dono.LabelShortCap}: {chave} → {valor}";
    }

    public static byte[] Comando(int coisaId, string chave, bool valor)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Protocol.Messages.TipoDeComando.Alternar);
        w.Write(coisaId);
        w.Write(chave);
        w.Write(valor);
        return ms.ToArray();
    }

    /// <summary>
    /// O caminho comum das intercepções: vira comando e recusa a escrita local.
    /// Devolve <c>true</c> quando o setter original deve rodar mesmo assim.
    /// </summary>
    public static bool DeixarPassar(Thing? dono, string chave, bool valor)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando || Aplicando) return true;

        // Só o clique do jogador vira comando. O jogo escreve nessas mesmas
        // propriedades por dentro — proibir o que caiu de um cadáver, largar o
        // "segurar fogo" ao desalistar — e aquilo é simulação, que já acontece
        // igual nos dois lados.
        if (!NaInterface.Agora) return true;

        if (dono == null) return true;

        // Pawn é de quem o comanda (§4). Coisa do chão não é de ninguém: quem
        // pode designar pode proibir.
        if (dono is Pawn pawn && !PosseDePawns.EhMeu(pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(pawn);
            return false;
        }

        GuardasDeDeterminismo.Disparou($"alternar {chave} virou comando");

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = Comando(dono.thingIDNumber, chave, valor),
        });

        Log.Message($"[WithFriends] {dono.LabelShortCap}: {chave} → {valor} proposto como comando");
        return false;
    }
}

/// <summary>
/// Segurar fogo. Botão de pawn alistado, e portanto de combate — o que mais
/// acontece numa visita.
/// </summary>
[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.FireAtWill), MethodType.Setter)]
public static class SegurarFogoViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Pawn_DraftController __instance, bool value) =>
        AlternaveisDeSessao.DeixarPassar(__instance.pawn, "segurarFogo", value);
}

/// <summary>
/// Proibir / liberar. Aparece o tempo todo depois de um assalto, quando os dois
/// jogadores estão recolhendo o que ficou no chão.
/// </summary>
[HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
public static class ProibirViraComando
{
    [HarmonyPrefix]
    public static bool Antes(CompForbiddable __instance, bool value) =>
        AlternaveisDeSessao.DeixarPassar(__instance.parent, "proibido", value);
}

/// <summary>
/// Cama médica. Decide quem deita ali quando cai ferido — e numa visita cai
/// gente das duas colônias, o que faz desta uma chave de combate.
/// </summary>
[HarmonyPatch(typeof(Building_Bed), nameof(Building_Bed.Medical), MethodType.Setter)]
public static class CamaMedicaViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Building_Bed __instance, bool value) =>
        AlternaveisDeSessao.DeixarPassar(__instance, "camaMedica", value);
}

/// <summary>
/// Ponto de encontro: liga e desliga o lugar para onde os colonos vão descansar
/// em grupo, e portanto para onde eles andam nas folgas.
/// </summary>
[HarmonyPatch(typeof(CompGatherSpot), nameof(CompGatherSpot.Active), MethodType.Setter)]
public static class PontoDeEncontroViraComando
{
    [HarmonyPrefix]
    public static bool Antes(CompGatherSpot __instance, bool value) =>
        AlternaveisDeSessao.DeixarPassar(__instance.parent, "pontoDeEncontro", value);
}

/// <summary>
/// "Não cortar esta planta": tira o trabalho da lista de quem colhe, no tick
/// seguinte, dos dois lados.
/// </summary>
[HarmonyPatch(typeof(CompPlantPreventCutting),
    nameof(CompPlantPreventCutting.PreventCutting), MethodType.Setter)]
public static class NaoCortarViraComando
{
    [HarmonyPrefix]
    public static bool Antes(CompPlantPreventCutting __instance, bool value) =>
        AlternaveisDeSessao.DeixarPassar(__instance.parent, "naoCortar", value);
}
