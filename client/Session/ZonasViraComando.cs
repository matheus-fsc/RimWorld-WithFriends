using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// A família <b>zona/área</b> do mapa de decisões — apagar e inverter área,
/// criar área nova, renomear, apagar zona e plano, escolher a planta da zona de
/// plantio.
///
/// <para><b>Por que esta família primeiro.</b> É a única das seis que cabe
/// inteira numa sessão de trabalho: dez registros contra 104 de <c>comp</c>. E é
/// onde a interface já nos mordeu uma vez — <c>CelulasDeZonaNaoEmbaralhamNaInterface</c>
/// existe porque <c>Zone.cells</c> é embaralhada em processo. Fechar uma família
/// inteira vale mais que arranhar a maior.</para>
///
/// <para><b>Por que não é enfeite.</b> Apagar uma zona de plantio muda o que
/// todo colono livre faz no tick seguinte: o trabalho some da lista e cada um
/// escolhe outra coisa. Inverter uma área muda para onde os pawns podem ir. São
/// decisões que reescrevem o plano de trabalho do mapa inteiro — a mesma classe
/// da prioridade de trabalho, com alcance maior.</para>
///
/// <para><b>A forma.</b> Mesma de <see cref="AjustesDePawn"/>: um tipo de
/// comando só, com um payload <c>(chave, alvo, número, texto)</c> e um registro
/// que sabe o que cada chave quer dizer. Acrescentar a próxima decisão de zona
/// custa uma entrada, sem tocar no protocolo.</para>
///
/// <para><b>Quem pode: os dois — e isso foi decidido pelo avesso.</b> A primeira
/// versão pôs esta família na lista de só-anfitrião, no argumento de que zona e
/// área são a arrumação da casa. Um teste à mão derrubou o argumento em um
/// minuto: o visitante apagou a zona, o coordenador recusou, o apagar local
/// tinha sido bloqueado, e a zona simplesmente ficou lá. Da cadeira dele, o jogo
/// desfez o que ele acabou de fazer.</para>
///
/// <para>E o log mostrou por que a regra era indefensível: no mesmo minuto, o
/// visitante <b>encolheu a zona célula a célula e a expandiu em 117 células</b>
/// — os designadores de zona nunca foram restritos. Podia desfazer a zona
/// inteira pela borda, não podia apagá-la pelo botão. Mesma superfície, mesmo
/// gesto, regras opostas.</para>
///
/// <para>O §4 fala de decisões que admitem <b>uma resposta só</b> — missões,
/// aceitar ou recusar um evento, provocar um acontecimento. Zona não é diálogo;
/// é trabalho de colônia, e o visitante está ali justamente para ajudar (§5).
/// Duas pessoas mexendo em zona é como duas pessoas construindo, que já
/// valia.</para>
/// </summary>
public static class AjustesDeZona
{
    public delegate void Aplicador(Map mapa, int alvo, int numero, string texto);

    static readonly Dictionary<string, Aplicador> Registro = new()
    {
        ["apagarZona"] = (mapa, alvo, _, _) =>
        {
            var zona = Zona(mapa, alvo);
            if (zona != null) ComoSistema(() => zona.Delete());
        },

        ["apagarArea"] = (mapa, alvo, _, _) =>
        {
            var area = Area(mapa, alvo);
            if (area != null) ComoSistema(area.Delete);
        },

        ["inverterArea"] = (mapa, alvo, _, _) =>
        {
            var area = Area(mapa, alvo);
            if (area != null) ComoSistema(area.Invert);
        },

        // **Área nova não tem alvo: ela nasce aqui.**
        //
        // O id vem de `UniqueIDsManager`, um contador do save — e os dois lados
        // aplicam o comando no mesmo passo, a partir do mesmo estado. Logo os
        // dois chamam o contador na mesma ordem e a área nasce com o mesmo id
        // dos dois lados, que é o que permite falar dela depois.
        ["novaArea"] = (mapa, _, _, texto) => ComoSistema(() =>
        {
            if (mapa.areaManager.TryMakeNewAllowed(out var nova) &&
                !string.IsNullOrEmpty(texto))
                nova.RenamableLabel = texto;
        }),

        ["renomearArea"] = (mapa, alvo, _, texto) =>
        {
            if (Area(mapa, alvo) is Area_Allowed area)
                ComoSistema(() => area.RenamableLabel = texto);
        },

        // **Plano se identifica por célula, porque plano não tem id.**
        //
        // `Zone` e `Area` têm um `ID` público; `Plan` não tem nenhum campo que
        // sirva de nome — só `Cells`. Então o alvo aqui é o índice de uma célula
        // que o plano ocupa, e quem o encontra do outro lado é `PlanAt`. Enquanto
        // os dois lados têm o mesmo mapa, a mesma célula acha o mesmo plano.
        ["apagarPlano"] = (mapa, alvo, _, _) =>
        {
            var plano = mapa.planManager?.PlanAt(mapa.cellIndices.IndexToCell(alvo));
            if (plano != null) ComoSistema(() => plano.Delete());
        },

        // A planta da zona de plantio: o texto é o def. Vale para a zona e para
        // o vaso — os dois são `IPlantToGrowSettable`, e o gizmo é o mesmo.
        ["plantaParaCultivar"] = (mapa, alvo, _, texto) =>
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(texto);
            if (def == null) return;
            var onde = Cultivavel(mapa, alvo);
            if (onde != null) ComoSistema(() => onde.SetPlantDefToGrow(def));
        },
    };

    static Zone? Zona(Map mapa, int id) =>
        mapa.zoneManager?.AllZones?.FirstOrDefault(z => z.ID == id);

    static Area? Area(Map mapa, int id) =>
        mapa.areaManager?.AllAreas?.FirstOrDefault(a => a.ID == id);

    /// <summary>
    /// Onde se planta, pelo id. Zona e vaso convivem no mesmo espaço de ids
    /// aqui porque vêm de contadores diferentes do jogo: um id de zona nunca é
    /// procurado como coisa, e vice-versa — o registro decide pela chave.
    /// </summary>
    static IPlantToGrowSettable? Cultivavel(Map mapa, int id) =>
        mapa.zoneManager?.AllZones?.FirstOrDefault(z => z.ID == id) as IPlantToGrowSettable
        ?? mapa.listerThings?.AllThings?.FirstOrDefault(t => t.thingIDNumber == id)
            as IPlantToGrowSettable;

    /// <summary>
    /// Enquanto vale, os remendos deixam passar: é o comando chegando, não o
    /// jogador clicando. Mesmo cuidado de <see cref="AjustesDePawn.Aplicando"/>.
    /// </summary>
    public static bool Aplicando { get; private set; }

    static void ComoSistema(Action escrever)
    {
        Aplicando = true;
        try { escrever(); }
        finally { Aplicando = false; }
    }

    public static string Aplicar(string chave, int alvo, int numero, string texto)
    {
        if (!Registro.TryGetValue(chave, out var aplicar))
            return $"ajuste de zona desconhecido ({chave}) — versões diferentes do mod?";

        var mapa = MapaDaSessao();
        if (mapa == null) return $"sem mapa de sessão para {chave}";

        aplicar(mapa, alvo, numero, texto);
        return $"zona: {chave} em {alvo}{(texto.Length > 0 ? $" ({texto})" : "")}";
    }

    /// <summary>
    /// O mapa onde o comando se aplica.
    ///
    /// <para><c>Find.CurrentMap</c> e não "o mapa da sessão, pelo id": quem
    /// responde <c>CurrentMap</c> durante a simulação já é
    /// <see cref="MapaDaSessaoNaSimulacao"/>, que existe exatamente para isso.
    /// Duas fontes para a mesma resposta é uma a mais para divergir.</para>
    /// </summary>
    static Map? MapaDaSessao() => Find.CurrentMap;

    public static byte[] Comando(string chave, int alvo, int numero = 0, string texto = "")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Protocol.Messages.TipoDeComando.Zona);
        w.Write(chave);
        w.Write(alvo);
        w.Write(numero);
        w.Write(texto);
        return ms.ToArray();
    }

    /// <summary>
    /// Vira comando e recusa a escrita local. <c>true</c> quando o original deve
    /// rodar mesmo assim.
    /// </summary>
    public static bool DeixarPassar(string chave, int alvo, int numero = 0, string texto = "")
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando || Aplicando) return true;

        // **Só o clique.** O jogo apaga zona por dentro — a zona some quando a
        // última célula dela é removida, o plano some com o prédio. Aquilo é
        // simulação, e simulação já acontece igual nos dois lados; interceptar
        // seria transformar consequência em decisão.
        if (!NaInterface.Agora) return true;

        GuardasDeDeterminismo.Disparou($"zona {chave} virou comando");

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = Comando(chave, alvo, numero, texto),
        });

        Log.Message($"[WithFriends] zona: {chave} em {alvo}" +
                    $"{(texto.Length > 0 ? $" ({texto})" : "")} proposto como comando");
        return false;
    }
}

/// <summary>
/// Apagar uma zona. Some o trabalho que ela gerava, e todo colono livre escolhe
/// outra coisa no tick seguinte.
/// </summary>
/// <para>A sobrecarga com <c>bool</c>, não a sem parâmetro: <c>Delete()</c>
/// chama <c>Delete(true)</c>, e remendar a fonte cobre os dois (ADR 0015).
/// Remendar as duas mandaria dois comandos para um clique só.</para>
[HarmonyPatch(typeof(Zone), nameof(Zone.Delete), new[] { typeof(bool) })]
public static class ApagarZonaViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Zone __instance) =>
        AjustesDeZona.DeixarPassar("apagarZona", __instance.ID);
}

/// <summary>Apagar uma área: os pawns restritos a ela ficam sem restrição.</summary>
[HarmonyPatch(typeof(Area), nameof(Area.Delete))]
public static class ApagarAreaViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Area __instance) =>
        AjustesDeZona.DeixarPassar("apagarArea", __instance.ID);
}

/// <summary>Inverter uma área muda, de uma vez, para onde todo mundo pode ir.</summary>
[HarmonyPatch(typeof(Area), nameof(Area.Invert))]
public static class InverterAreaViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Area __instance) =>
        AjustesDeZona.DeixarPassar("inverterArea", __instance.ID);
}

/// <summary>
/// Criar área nova.
///
/// <para>Chama o contador de ids únicos do save. Sem virar comando, um lado
/// avança o contador e o outro não — e a próxima área de cada lado nasce com id
/// diferente, o que estraga toda referência a área daí em diante, inclusive a
/// restrição de pawn que já é comando.</para>
/// </summary>
[HarmonyPatch(typeof(AreaManager), nameof(AreaManager.TryMakeNewAllowed))]
public static class NovaAreaViraComando
{
    [HarmonyPrefix]
    public static bool Antes(ref bool __result)
    {
        if (AjustesDeZona.DeixarPassar("novaArea", -1)) return true;
        __result = false;
        return false;
    }
}

/// <summary>Apagar um plano — o desenho que orienta o que construir depois.</summary>
[HarmonyPatch(typeof(Plan), nameof(Plan.Delete), new[] { typeof(bool) })]
public static class ApagarPlanoViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Plan __instance)
    {
        var celula = __instance.Cells?.FirstOrDefault() ?? IntVec3.Invalid;
        var mapa = __instance.Map;
        if (!celula.IsValid || mapa == null) return true;

        return AjustesDeZona.DeixarPassar(
            "apagarPlano", mapa.cellIndices.CellToIndex(celula));
    }
}

/// <summary>
/// Renomear uma área.
///
/// <para>Dois escritores independentes para o mesmo campo: o setter de
/// <c>RenamableLabel</c> e <c>SetLabel</c> escrevem em <c>labelInt</c> cada um
/// por si, e nenhum chama o outro. Não há fonte única para remendar — então vão
/// os dois, e o guarda de <c>Aplicando</c> impede que a aplicação do comando
/// dispare o outro.</para>
///
/// <para>Rótulo parece enfeite e não é: é por ele que o jogador reconhece a área
/// no menu de restrição, e a restrição é comando de simulação.</para>
/// </summary>
/// <para><b>Dois remendos e não um, por causa do nome do parâmetro.</b> A
/// primeira versão apontava os dois métodos de uma classe só, com um prefixo
/// pedindo <c>string value</c>. O Harmony injeta parâmetro <b>por nome</b>: a
/// propriedade chama o dela de <c>value</c>, o método chama de <c>newLabel</c>,
/// e a classe inteira foi recusada —</para>
///
/// <code>
/// RenomearAreaViraComando: Parameter "value" not found in method
///   System.Void RimWorld.Area_Allowed::SetLabel(System.String newLabel)
/// </code>
///
/// <para>— o que desligou o recurso nos dois caminhos, não só no que tinha o
/// nome errado. Duas classes custam quatro linhas e não têm como errar isso.</para>
[HarmonyPatch(typeof(Area_Allowed), nameof(Area_Allowed.RenamableLabel), MethodType.Setter)]
public static class RenomearAreaPelaPropriedade
{
    [HarmonyPrefix]
    public static bool Antes(Area_Allowed __instance, string value) =>
        AjustesDeZona.DeixarPassar("renomearArea", __instance.ID, 0, value ?? "");
}

/// <summary>O outro escritor de <c>labelInt</c>, que não passa pela propriedade.</summary>
[HarmonyPatch(typeof(Area_Allowed), "SetLabel", new[] { typeof(string) })]
public static class RenomearAreaPeloMetodo
{
    [HarmonyPrefix]
    public static bool Antes(Area_Allowed __instance, string newLabel) =>
        AjustesDeZona.DeixarPassar("renomearArea", __instance.ID, 0, newLabel ?? "");
}

/// <summary>
/// Escolher a planta da zona de plantio — e do vaso, que é o mesmo gizmo.
///
/// <para>O mapa de decisões aponta <c>Zone_Growing.GetGizmos</c>, porque o
/// Multiplayer sincroniza a <b>lambda</b> de dentro do gizmo. Aqui o remendo vai
/// no efeito, não no botão: <c>SetPlantDefToGrow</c> é onde a escolha vira
/// estado, e quem passar por outro caminho — um mod, um atalho, o vaso — passa
/// pelo mesmo lugar. É a ADR 0015 outra vez: remendar a fonte cobre os
/// chamadores dela.</para>
///
/// <para>E o alcance é de simulação: a planta da zona decide o que os colonos
/// semeiam no tick seguinte, e uma semente diferente de cada lado é uma colheita
/// diferente de cada lado.</para>
/// </summary>
[HarmonyPatch]
public static class PlantaDaZonaViraComando
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var tipo in new[] { typeof(Zone_Growing), typeof(Building_PlantGrower) })
        {
            var m = AccessTools.Method(tipo, nameof(IPlantToGrowSettable.SetPlantDefToGrow),
                new[] { typeof(ThingDef) });
            if (m != null) yield return m;
        }
    }

    [HarmonyPrefix]
    public static bool Antes(object __instance, ThingDef plantDef)
    {
        int alvo = __instance switch
        {
            Zone zona => zona.ID,
            Thing coisa => coisa.thingIDNumber,
            _ => -1,
        };

        if (alvo < 0) return true;
        return AjustesDeZona.DeixarPassar("plantaParaCultivar", alvo, 0, plantDef?.defName ?? "");
    }
}
