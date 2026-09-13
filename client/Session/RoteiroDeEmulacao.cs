using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// O que a emulação <b>faz</b> durante a visita.
///
/// <para><b>Por que não basta deixar rodando.</b> Uma visita parada exercita
/// plantas crescendo e animais perambulando — e nenhuma das divergências que
/// perseguimos apareceu aí. Todas apareceram em <b>combate</b>: projétil,
/// dano, sangue, cadáver. Uma emulação que não briga testa o caminho errado.</para>
///
/// <para>Então o roteiro provoca: alista os colonos, chama um assalto, e deixa
/// a briga acontecer. Tudo pelos mesmos comandos de sessão que um jogador
/// usaria — alistar passa pelo setter de <c>Drafted</c>, o assalto é
/// <c>TipoDeComando.Incidente</c>. Não há atalho para dentro da simulação: se o
/// caminho do comando estiver quebrado, a emulação quebra junto, que é
/// exatamente o que se quer de um teste.</para>
///
/// <para><b>Só o anfitrião roteiriza.</b> Incidente é decisão da colônia e o
/// coordenador recusa do visitante (§4); alistar, cada um comanda os seus. O
/// árbitro fica só olhando — é o papel dele.</para>
/// </summary>
public static class RoteiroDeEmulacao
{
    /// <summary>Um momento do roteiro: quando, o que, e como contar depois.</summary>
    readonly record struct Passo(float Segundos, string Nome, Action Fazer);

    static List<Passo>? passos;
    static int proximo;
    static float comecouEm;

    /// <summary>
    /// Qual roteiro rodar, de <c>-emulacaoroteiro=</c>. Sem ele, nenhum — a
    /// visita só corre, que ainda serve para medir deriva sem combate.
    /// </summary>
    public static string Nome =>
        GenCommandLine.TryGetCommandLineArg("emulacaoroteiro", out string r) ? r : "";

    public static bool Algum => !string.IsNullOrWhiteSpace(Nome);

    public static void Comecar()
    {
        comecouEm = Time.realtimeSinceStartup;
        proximo = 0;
        passos = Montar(Nome);

        Log.Message(
            $"[WithFriends/roteiro] \"{Nome}\": {passos.Count} passo(s) — " +
            string.Join(", ", passos.Select(p => $"{p.Segundos:F0}s {p.Nome}")));
    }

    /// <summary>Chamado a cada quadro enquanto a visita corre.</summary>
    public static void Acompanhar()
    {
        if (passos == null || proximo >= passos.Count) return;

        var passo = passos[proximo];
        if (Time.realtimeSinceStartup - comecouEm < passo.Segundos) return;

        proximo++;
        Log.Message($"[WithFriends/roteiro] {passo.Nome}");

        try { passo.Fazer(); }
        catch (Exception e)
        {
            // Um passo que falha não derruba o resto: o roteiro é o teste, e
            // saber que ele falhou vale mais que parar tudo.
            Log.Error($"[WithFriends/roteiro] {passo.Nome} falhou: {e.Message}");
        }
    }

    static List<Passo> Montar(string nome) => nome switch
    {
        "raid" => new List<Passo>
        {
            new(5f,  "alistar os colonos",        AlistarTodos),
            new(10f, "chamar um assalto",         () => Assalto(1f)),
            new(60f, "chamar um assalto maior",   () => Assalto(2f)),
        },

        "combate" => new List<Passo>
        {
            new(5f,  "alistar os colonos",        AlistarTodos),
            new(10f, "chamar um assalto",         () => Assalto(1f)),
            new(40f, "desalistar",                () => AlistarTodos(false)),
            new(50f, "alistar de novo",           AlistarTodos),
            new(70f, "chamar um assalto maior",   () => Assalto(2.5f)),
        },

        _ => new List<Passo>(),
    };

    static void AlistarTodos() => AlistarTodos(true);

    /// <summary>
    /// Alistar passa pelo setter de <c>Drafted</c>, que vira comando — o mesmo
    /// caminho do clique no gizmo.
    /// </summary>
    static void AlistarTodos(bool alistar)
    {
        var mapa = Find.CurrentMap;
        if (mapa == null) return;

        int mudaram = 0, jaEstavam = 0, caidos = 0;
        var colonos = mapa.mapPawns.FreeColonistsSpawned.ToList();

        foreach (var pawn in colonos)
        {
            if (pawn.drafter == null || pawn.Downed) { caidos++; continue; }
            if (pawn.drafter.Drafted == alistar) { jaEstavam++; continue; }

            pawn.drafter.Drafted = alistar;
            mudaram++;
        }

        // O detalhe importa: "0 colonos" é ambíguo entre "não há colono",
        // "todos já estavam" e "todos caídos", e as três levam a lugares
        // diferentes quando o roteiro não produz combate.
        Log.Message(
            $"[WithFriends/roteiro]   {mudaram} mudaram, {jaEstavam} já estavam, " +
            $"{caidos} caído(s) — de {colonos.Count} colono(s) no mapa");
    }

    /// <summary>
    /// Assalto pela porta normal: <c>TipoDeComando.Incidente</c>, carimbado pelo
    /// coordenador e aplicado no mesmo tick dos dois lados.
    /// </summary>
    static void Assalto(float fator)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao?.Atual == null || Find.CurrentMap == null) return;

        float pontos = StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap) * fator;

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = ComandoDeSessao.Incidente(IncidentDefOf.RaidEnemy.defName, pontos),
        });

        Log.Message($"[WithFriends/roteiro]   {pontos:F0} pontos de ameaça");
    }
}
