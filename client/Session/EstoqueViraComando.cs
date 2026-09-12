using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Prioridade e filtro de estoque viram comando — pelo <b>estado</b>, não pela
/// operação.
///
/// <para><b>Por que isto apareceu.</b> Caçando "o menu de priorizar não
/// aparece", a causa era regra do jogo: material fora de estoque não gera
/// trabalho, e sem trabalho não há o que priorizar. Mas isso quer dizer que a
/// configuração do estoque **decide o que a simulação faz** — e ela estava
/// inteiramente fora da sessão. Um jogador permitir aço num estoque muda o que
/// os pawns dos dois lados vão fazer no próximo tick.</para>
///
/// <para><b>Estado em vez de operação.</b> O <c>ThingFilter</c> tem oito
/// mutadores, vários com parâmetros que não atravessam a rede de graça — listas
/// de exceção, um filtro-pai inteiro. Em vez de sincronizar cada um, deixamos a
/// interface calcular o resultado, <b>desfazemos</b> o efeito local e mandamos o
/// estado inteiro: quais defs valem agora, quais filtros especiais estão
/// desligados, as duas faixas.</para>
///
/// <para>Um payload cobre os oito mutadores, os widgets que ainda não existem e
/// os que vêm de mod. E, por ser estado absoluto, dois comandos fora de ordem
/// não deixam o filtro num meio-termo que ninguém pediu.</para>
///
/// <para><b>Só filtros com dono conhecido.</b> Filtro de bill, de caravana e de
/// diálogo passam direto: não são estado do mapa compartilhado, e cancelá-los
/// quebraria janelas que não têm nada a ver com a visita.</para>
/// </summary>
public static class EstoqueDeSessao
{
    public static bool Aplicando { get; private set; }

    static readonly FieldInfo? CampoPermitidos = AccessTools.Field(typeof(ThingFilter), "allowedDefs");
    static readonly FieldInfo? CampoEspeciaisDesligados =
        AccessTools.Field(typeof(ThingFilter), "disallowedSpecialFilters");
    static readonly FieldInfo? CampoFaixaVida =
        AccessTools.Field(typeof(ThingFilter), "allowedHitPointsPercents");
    static readonly FieldInfo? CampoFaixaQualidade =
        AccessTools.Field(typeof(ThingFilter), "allowedQualities");

    // ---------------------------------------------------------------- dono

    /// <summary>
    /// Como um estoque é endereçado na rede.
    ///
    /// <para>Estoque é ou uma <b>zona</b> (o retângulo no chão) ou uma
    /// <b>coisa</b> (prateleira, caixa). Zona não é <c>Thing</c> e não tem
    /// <c>thingIDNumber</c> — tem <c>ID</c>, único por partida, que serve
    /// igual: os dois lados carregaram a mesma partida (ADR 0010).</para>
    /// </summary>
    public readonly record struct Dono(byte Tipo, int Id)
    {
        public const byte Coisa = 1;
        public const byte Zona = 2;
    }

    /// <summary>Acha de quem é este filtro. <c>null</c> = não é nosso, deixa passar.</summary>
    public static (Dono dono, StorageSettings ajustes)? DonoDoFiltro(ThingFilter filtro)
    {
        if (Current.Game == null) return null;

        foreach (var mapa in Find.Maps)
        {
            foreach (var zona in mapa.zoneManager.AllZones)
                if (zona is Zone_Stockpile pilha && ReferenceEquals(pilha.settings?.filter, filtro))
                    return (new Dono(Dono.Zona, zona.ID), pilha.settings);

            foreach (var coisa in mapa.listerThings.AllThings)
                if (coisa is IStoreSettingsParent guarda &&
                    ReferenceEquals(guarda.GetStoreSettings()?.filter, filtro))
                    return (new Dono(Dono.Coisa, coisa.thingIDNumber), guarda.GetStoreSettings());
        }

        return null;
    }

    public static StorageSettings? Achar(Dono dono)
    {
        foreach (var mapa in Find.Maps)
        {
            if (dono.Tipo == Dono.Zona)
            {
                foreach (var zona in mapa.zoneManager.AllZones)
                    if (zona.ID == dono.Id && zona is Zone_Stockpile pilha) return pilha.settings;
            }
            else
            {
                foreach (var coisa in mapa.listerThings.AllThings)
                    if (coisa.thingIDNumber == dono.Id && coisa is IStoreSettingsParent guarda)
                        return guarda.GetStoreSettings();
            }
        }

        return null;
    }

    // ------------------------------------------------------------- payload

    public static byte[] Comando(Dono dono, StorageSettings ajustes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Protocol.Messages.TipoDeComando.Estoque);
        w.Write(dono.Tipo);
        w.Write(dono.Id);
        w.Write((int)ajustes.Priority);

        var filtro = ajustes.filter;
        var permitidos = CampoPermitidos?.GetValue(filtro) as HashSet<ThingDef> ?? new HashSet<ThingDef>();
        w.Write(permitidos.Count);
        foreach (var def in permitidos) w.Write(def.defName);

        var desligados = CampoEspeciaisDesligados?.GetValue(filtro) as List<SpecialThingFilterDef>
                         ?? new List<SpecialThingFilterDef>();
        w.Write(desligados.Count);
        foreach (var def in desligados) w.Write(def.defName);

        var vida = (FloatRange?)CampoFaixaVida?.GetValue(filtro) ?? FloatRange.ZeroToOne;
        w.Write(vida.min);
        w.Write(vida.max);

        var qualidade = (QualityRange?)CampoFaixaQualidade?.GetValue(filtro) ?? QualityRange.All;
        w.Write((int)qualidade.min);
        w.Write((int)qualidade.max);

        return ms.ToArray();
    }

    public static string Aplicar(BinaryReader r)
    {
        var dono = new Dono(r.ReadByte(), r.ReadInt32());
        var prioridade = (StoragePriority)r.ReadInt32();

        int quantos = r.ReadInt32();
        var permitidos = new List<string>(quantos);
        for (int i = 0; i < quantos; i++) permitidos.Add(r.ReadString());

        int quantosEspeciais = r.ReadInt32();
        var desligados = new List<string>(quantosEspeciais);
        for (int i = 0; i < quantosEspeciais; i++) desligados.Add(r.ReadString());

        var vida = new FloatRange(r.ReadSingle(), r.ReadSingle());
        var qualidade = new QualityRange((QualityCategory)r.ReadInt32(), (QualityCategory)r.ReadInt32());

        var ajustes = Achar(dono);
        if (ajustes?.filter == null)
            return $"estoque {dono.Tipo}:{dono.Id} não encontrado";

        Aplicando = true;
        try
        {
            ajustes.Priority = prioridade;

            var novos = new HashSet<ThingDef>();
            foreach (var nome in permitidos)
                if (DefDatabase<ThingDef>.GetNamedSilentFail(nome) is { } def) novos.Add(def);

            var novosEspeciais = new List<SpecialThingFilterDef>();
            foreach (var nome in desligados)
                if (DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(nome) is { } def)
                    novosEspeciais.Add(def);

            CampoPermitidos?.SetValue(ajustes.filter, novos);
            CampoEspeciaisDesligados?.SetValue(ajustes.filter, novosEspeciais);
            CampoFaixaVida?.SetValue(ajustes.filter, vida);
            CampoFaixaQualidade?.SetValue(ajustes.filter, qualidade);

            // Quem escuta o filtro precisa saber que ele mudou — é isto que
            // reavalia o que está guardado no lugar errado.
            AccessTools.Method(typeof(ThingFilter), "SettingsChangedCallback")
                ?.Invoke(ajustes.filter, null);
        }
        finally { Aplicando = false; }

        return $"estoque {dono.Tipo}:{dono.Id} → prioridade {prioridade}, {permitidos.Count} def(s)";
    }

    // -------------------------------------------------------- intercepção

    /// <summary>
    /// Roda uma escrita **nossa** no filtro: a guarda impede que o próprio
    /// remendo a intercepte e proponha comando de novo, para sempre.
    /// </summary>
    public static void ComoSistema(Action escrever)
    {
        Aplicando = true;
        try { escrever(); }
        finally { Aplicando = false; }
    }

    /// <summary>
    /// Manda o estado já calculado como comando. Chamado **depois** de a
    /// interface calcular o resultado e de o efeito local ser desfeito.
    /// </summary>
    public static void Enviar(byte[] payload, string oQue)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao?.Atual == null) return;

        GuardasDeDeterminismo.Disparou($"estoque: {oQue}");

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = payload,
        });

        Log.Message($"[WithFriends] estoque: {oQue} proposto como comando");
    }

    /// <summary>Estamos numa visita e é o jogador mexendo, não o jogo nem um comando?</summary>
    public static bool ÉMexidaDoJogador()
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return false;
        return !ComandoDeSessao.Aplicando && !Aplicando && NaInterface.Agora;
    }
}

/// <summary>
/// Os mutadores do filtro. Um remendo só, por <c>TargetMethods</c> — a fonte,
/// não os widgets que chamam (ADR 0015).
///
/// <para>O jeito: deixa rodar, lê o resultado, <b>desfaz</b> e manda o estado.
/// Desfazer é o que mantém os dois lados iguais até o comando voltar carimbado;
/// calcular localmente é o que dispensa serializar os parâmetros esquisitos de
/// cada mutador.</para>
/// </summary>
[HarmonyPatch]
public static class FiltroDeEstoqueViraComando
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var nome in new[]
                 {
                     "SetAllow", "SetAllowAll", "SetDisallowAll",
                     "SetAllowAllWhoCanMake", "SetFromPreset", "CopyAllowancesFrom",
                 })
        foreach (var metodo in typeof(ThingFilter).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (metodo.Name == nome)
                yield return metodo;
    }

    [HarmonyPrefix]
    public static void Antes(ThingFilter __instance, out ThingFilter? __state)
    {
        __state = null;
        if (!EstoqueDeSessao.ÉMexidaDoJogador()) return;
        if (EstoqueDeSessao.DonoDoFiltro(__instance) == null) return;

        // Cópia do antes, para desfazer. `CopyAllowancesFrom` também é alvo
        // deste remendo — a guarda de `Aplicando` é o que impede a recursão.
        var antes = new ThingFilter();
        EstoqueDeSessao.ComoSistema(() => antes.CopyAllowancesFrom(__instance));
        __state = antes;
    }

    [HarmonyPostfix]
    public static void Depois(ThingFilter __instance, ThingFilter? __state)
    {
        if (__state == null) return;

        var achado = EstoqueDeSessao.DonoDoFiltro(__instance);
        if (achado == null) return;
        var (dono, ajustes) = achado.Value;

        // O estado novo vai no comando; o local volta ao que era até ele
        // chegar carimbado.
        var payload = EstoqueDeSessao.Comando(dono, ajustes);
        EstoqueDeSessao.ComoSistema(() => __instance.CopyAllowancesFrom(__state));

        EstoqueDeSessao.Enviar(payload, $"filtro ({dono.Tipo}:{dono.Id})");
    }
}

/// <summary>
/// A prioridade do estoque. Decide quem guarda o quê primeiro, e portanto o que
/// os pawns fazem — é simulação, não preferência de tela.
/// </summary>
[HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.Priority), MethodType.Setter)]
public static class PrioridadeDeEstoqueViraComando
{
    [HarmonyPrefix]
    public static bool Antes(StorageSettings __instance, StoragePriority value)
    {
        if (!EstoqueDeSessao.ÉMexidaDoJogador()) return true;
        if (__instance.filter == null) return true;

        var achado = EstoqueDeSessao.DonoDoFiltro(__instance.filter);
        if (achado == null) return true;
        var dono = achado.Value.dono;

        // Mesmo jeito do filtro: aplica, lê o estado, desfaz, manda.
        var anterior = __instance.Priority;
        byte[] payload = Array.Empty<byte>();
        EstoqueDeSessao.ComoSistema(() =>
        {
            __instance.Priority = value;
            payload = EstoqueDeSessao.Comando(dono, __instance);
            __instance.Priority = anterior;
        });

        EstoqueDeSessao.Enviar(payload, $"prioridade de {dono.Tipo}:{dono.Id} → {value}");
        return false;
    }
}
