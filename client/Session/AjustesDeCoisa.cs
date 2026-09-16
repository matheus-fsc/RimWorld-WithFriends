using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Os ajustes de uma coisa do mapa que <b>não são liga/desliga</b> — nível de
/// combustível desejado, estado do tanque, nome do pawn.
///
/// <para><b>Por que um tipo novo.</b> <see cref="AlternaveisDeSessao"/> carrega
/// <c>bool</c> e mais nada, e estes três não são booleanos: um é fração, um é
/// enum, um é texto. A alternativa seria um tipo de comando por item — três
/// trocas de protocolo para três decisões. Aqui vai um payload
/// <c>(coisa, chave, valor, texto)</c>, e a próxima custa uma entrada no
/// registro.</para>
///
/// <para><b>Por que estes três e não os outros vinte.</b> O mapa de decisões
/// (<c>wf decisoes --forma</c>) separa propriedade de campo: propriedade tem
/// setter, e setter é onde se remenda. Estes são as três propriedades de valor
/// que faltavam. Os vinte e três restantes são campos públicos escritos dentro
/// da lambda de um botão — não há fonte, e é outro problema (o mesmo dos 227
/// registros de lambda do Multiplayer).</para>
/// </summary>
public static class AjustesDeCoisa
{
    public delegate void Aplicador(Thing dono, float valor, string texto);

    static readonly Dictionary<string, Aplicador> Registro = new()
    {
        // Quanto combustível o jogador quer que o gerador mantenha. Decide se um
        // colono larga o que está fazendo para ir abastecer — no tick seguinte,
        // dos dois lados.
        ["combustivelAlvo"] = (dono, valor, _) =>
        {
            if (dono.TryGetComp<CompRefuelable>() is { } comp)
                ComoSistema(() => comp.TargetFuelLevel = valor);
        },

        // O tanque do gestador de mecanoides: encher, esvaziar, manter.
        ["estadoDoTanque"] = (dono, valor, _) =>
        {
            if (dono.TryGetComp<CompMechGestatorTank>() is { } comp)
                ComoSistema(() => comp.State = (CompMechGestatorTank.TankState)(int)valor);
        },

        ["nome"] = (dono, _, texto) =>
        {
            if (dono is Pawn pawn && LerNome(texto) is { } nome)
                ComoSistema(() => pawn.Name = nome);
        },
    };

    /// <summary>
    /// Nome de pawn em texto — <c>T|primeiro|apelido|último</c> ou <c>S|nome</c>.
    ///
    /// <para>Escrito à mão e não pelo <c>Scribe</c> porque <c>Name</c> tem dois
    /// formatos e nada mais: um triplo e um simples. Serializar o objeto inteiro
    /// custaria um documento XML por renomeação, para carregar três
    /// palavras.</para>
    /// </summary>
    static string EscreverNome(Name nome) => nome switch
    {
        NameTriple t => $"T|{t.First}|{t.Nick}|{t.Last}",
        NameSingle s => $"S|{s.Name}",
        _ => "",
    };

    static Name? LerNome(string texto)
    {
        var partes = texto.Split('|');
        if (partes.Length == 4 && partes[0] == "T")
            return new NameTriple(partes[1], partes[2], partes[3]);
        if (partes.Length == 2 && partes[0] == "S")
            return new NameSingle(partes[1]);
        return null;
    }

    /// <summary>
    /// Enquanto vale, os setters remendados deixam passar: é o comando chegando,
    /// não o jogador clicando. Mesmo cuidado de
    /// <see cref="AlternaveisDeSessao.Aplicando"/>.
    /// </summary>
    public static bool Aplicando { get; private set; }

    static void ComoSistema(Action escrever)
    {
        Aplicando = true;
        try { escrever(); }
        finally { Aplicando = false; }
    }

    public static string Aplicar(int coisaId, string chave, float valor, string texto)
    {
        var dono = ComandoDeSessao.EncontrarCoisa(coisaId);
        if (dono == null) return $"coisa {coisaId} não encontrada para {chave}";

        // Campo observado usa o mesmo payload: o que muda é só quem sabe
        // escrever nele. Ver CamposObservados — ali não há setter para remendar,
        // e a mudança é reconhecida comparando o valor antes e depois da
        // interface.
        if (CamposObservados.Conhece(chave))
        {
            ComoSistema(() => CamposObservados.Aplicar(dono, chave, valor));
            return $"{dono.LabelShortCap}: {CamposObservados.Descrever(chave)} → {valor:0.##}";
        }

        if (!Registro.TryGetValue(chave, out var aplicar))
            return $"ajuste de coisa desconhecido ({chave}) — versões diferentes do mod?";

        aplicar(dono, valor, texto);
        return $"{dono.LabelShortCap}: {chave} → {(texto.Length > 0 ? texto : valor.ToString("0.##"))}";
    }

    public static byte[] Comando(int coisaId, string chave, float valor, string texto = "")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Protocol.Messages.TipoDeComando.AjusteDeCoisa);
        w.Write(coisaId);
        w.Write(chave);
        w.Write(valor);
        w.Write(texto);
        return ms.ToArray();
    }

    /// <summary>
    /// Vira comando e recusa a escrita local. <c>true</c> quando o original deve
    /// rodar mesmo assim.
    /// </summary>
    public static bool DeixarPassar(Thing? dono, string chave, float valor, string texto = "")
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando || Aplicando) return true;

        // Só o clique. O jogo mexe nessas mesmas coisas por dentro — o tanque
        // muda de estado sozinho quando termina — e aquilo é simulação, que já
        // acontece igual nos dois lados.
        if (!NaInterface.Agora) return true;
        if (dono == null) return true;

        if (dono is Pawn pawn && !PosseDePawns.EhMeu(pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(pawn);
            return false;
        }

        GuardasDeDeterminismo.Disparou($"ajuste de coisa {chave} virou comando");

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = Comando(dono.thingIDNumber, chave, valor, texto),
        });

        Log.Message($"[WithFriends] {dono.LabelShortCap}: {chave} → " +
                    $"{(texto.Length > 0 ? texto : valor.ToString("0.##"))} proposto como comando");
        return false;
    }

    /// <summary>Para os remendos: o texto de um nome, no formato do payload.</summary>
    public static string NomeEmTexto(Name? nome) => nome == null ? "" : EscreverNome(nome);
}

/// <summary>
/// Nível de combustível desejado. Decide se um colono larga o que está fazendo
/// para ir abastecer — no tick seguinte, dos dois lados.
/// </summary>
[HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.TargetFuelLevel), MethodType.Setter)]
public static class CombustivelAlvoViraComando
{
    [HarmonyPrefix]
    public static bool Antes(CompRefuelable __instance, float value) =>
        AjustesDeCoisa.DeixarPassar(__instance.parent, "combustivelAlvo", value);
}

/// <summary>O tanque do gestador de mecanoides: encher, esvaziar, manter.</summary>
[HarmonyPatch(typeof(CompMechGestatorTank), nameof(CompMechGestatorTank.State), MethodType.Setter)]
public static class EstadoDoTanqueViraComando
{
    [HarmonyPrefix]
    public static bool Antes(CompMechGestatorTank __instance, CompMechGestatorTank.TankState value) =>
        AjustesDeCoisa.DeixarPassar(__instance.parent, "estadoDoTanque", (int)value);
}

/// <summary>
/// Renomear um pawn.
///
/// <para>Parece enfeite e não é, por uma razão que só aparece em visita: o nome
/// é como <b>o outro jogador</b> reconhece o colono de quem se está falando. Um
/// lado chamando "Kasumi" o que o outro chama de "Fitz" é confusão entre duas
/// pessoas, que é pior do que confusão dentro de uma cabeça só.</para>
/// </summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.Name), MethodType.Setter)]
public static class NomeViraComando
{
    [HarmonyPrefix]
    public static bool Antes(Pawn __instance, Name value) =>
        AjustesDeCoisa.DeixarPassar(__instance, "nome", 0f, AjustesDeCoisa.NomeEmTexto(value));
}
