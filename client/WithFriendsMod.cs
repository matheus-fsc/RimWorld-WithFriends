using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using WithFriends.Client.Colony;
using WithFriends.Client.Net;
using WithFriends.Client.Patches;
using WithFriends.Transport;

namespace WithFriends.Client;

/// <summary>
/// Ponto de entrada do mod. Só instala patches e registra estado —
/// nada aqui bloqueia o jogo solo (§11).
/// </summary>
public class WithFriendsMod : Mod
{
    public const string PackageId = "withfriends.rimworld";

    public static Harmony Harmony { get; private set; } = null!;
    public static WithFriendsSettings Settings { get; private set; } = null!;

    /// <summary>Uma conexão por sessão de jogo, viva entre partidas.</summary>
    public static ClienteCoordenador Cliente { get; } = new();

    public WithFriendsMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<WithFriendsSettings>();
        // Conferir antes de remendar: alvo ausente vira aviso legível, não
        // exceção no meio do carregamento (§9.1, §14.5).
        CatalogoDePatches.Verificar();

        Harmony = new Harmony(PackageId);
        RemendarTudo();

        // O diário abre logo depois dos remendos — ele **é** um remendo no
        // `Log`, então antes disso não haveria o que gravar. As linhas da carga
        // ficam guardadas e caem no arquivo assim que ele abre.
        Session.DiarioDaInstancia.Abrir(Settings.PlayerIdOuNovo());

        // Efeitos visuais e sonoros não podem mover o estado compartilhado.
        // São dezenas de métodos, então o remendo é em lote (ADR 0012).
        Session.EfeitosNaoDeterministicos.Instalar(Harmony);
        Log.Message(
            $"[WithFriends] carregado — protocolo v{Protocol.ProtocolVersion.Current}, " +
            $"jogador {Settings.PlayerIdOuNovo()}");
    }

    /// <summary>
    /// Remenda cada classe por conta própria, em vez de <c>PatchAll()</c>.
    ///
    /// <para><c>PatchAll</c> é tudo ou nada: a primeira classe que estoura
    /// interrompe o laço, e as classes seguintes **nunca são remendadas** — em
    /// silêncio, porque a exceção fala do alvo que falhou e não do que deixou
    /// de existir.</para>
    ///
    /// <para>Foi o que aconteceu ao remendar as sobrescritas dos designadores:
    /// uma delas nomeia o parâmetro diferente, o Harmony recusou, e o freio de
    /// tick da sessão nunca foi instalado. O sintoma não foi "construir não
    /// funciona" — foi os dois jogos congelados na barreira, a três arquivos de
    /// distância da causa.</para>
    ///
    /// <para>A regra do §14.5 vale aqui também: um acoplamento que quebra
    /// desliga o recurso dele, com log legível, e não leva o resto junto.</para>
    /// </summary>
    void RemendarTudo()
    {
        int remendadas = 0;
        var falhas = new List<string>();

        foreach (var tipo in Assembly.GetExecutingAssembly().GetTypes())
        {
            try
            {
                var processador = Harmony.CreateClassProcessor(tipo);
                if (processador.Patch()?.Count > 0) remendadas++;
            }
            catch (Exception e)
            {
                falhas.Add($"  {tipo.Name}: {e.InnerException?.Message ?? e.Message}");
            }
        }

        if (falhas.Count == 0)
        {
            Log.Message($"[WithFriends] {remendadas} classe(s) de remendo instalada(s).");
            return;
        }

        Log.Error(
            $"[WithFriends] {remendadas} classe(s) de remendo instalada(s), " +
            $"{falhas.Count} falhou(ram):\n" + string.Join("\n", falhas) +
            "\n  O resto do mod segue funcionando; o recurso dessas classes está desligado.");
    }

    public override string SettingsCategory() => "With Friends";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var lista = new Listing_Standard();
        lista.Begin(inRect);

        lista.Label("Endereço do coordenador");
        lista.Label(
            "Aceita IPv6 entre colchetes, IPv4 e nome de host. Sem porta, usa "
            + $"{EnderecoServidor.PortaPadrao}.",
            tooltip: "Exemplos: [2804:14c::1]:25555 · 192.168.0.4:25555 · casa.exemplo.com");
        Settings.endereco = lista.TextEntry(Settings.endereco);

        if (!string.IsNullOrWhiteSpace(Settings.endereco))
        {
            if (EnderecoServidor.TentarAnalisar(Settings.endereco, out var endereco, out string erro))
                lista.Label($"→ {endereco}");
            else
                lista.Label($"<color=#ff6666>{erro}</color>");
        }

        if (lista.ButtonText($"Usar coordenador local  {Conexao.EnderecoLocal}"))
            Settings.endereco = Conexao.EnderecoLocal;

        lista.GapLine();
        lista.CheckboxLabeled(
            "Conectar automaticamente ao entrar numa partida",
            ref Settings.conectarAoIniciar,
            "Tenta a cada 15s enquanto não conseguir. Jogar sozinho nunca depende disso.");

        lista.CheckboxLabeled(
            "Enviar checkpoint junto com todo save do jogo",
            ref Settings.checkpointAoSalvar,
            "Pega carona no autosave: nenhum custo novo de serialização.");
        if (Settings.checkpointAoSalvar)
        {
            lista.Label($"Piso entre envios: {Settings.intervaloMinimoMinutos} min");
            Settings.intervaloMinimoMinutos =
                (int)lista.Slider(Settings.intervaloMinimoMinutos, 0f, 60f);
        }

        lista.GapLine();
        lista.Label($"Jogador: {Settings.PlayerIdOuNovo()}");
        lista.Label($"Conexão: {Cliente.Estado}"
            + (Cliente.Descricao.Length > 0 ? $" ({Cliente.Descricao})" : ""));
        if (Cliente.UltimoErro.Length > 0)
            lista.Label($"<color=#ff6666>{Cliente.UltimoErro}</color>");

        if (Cliente.Conectado || Cliente.Estado == EstadoConexao.Conectando)
        {
            if (lista.ButtonText("Desconectar")) Cliente.Desconectar();
        }
        else if (lista.ButtonText("Conectar") && !Conexao.Conectar(out string falha))
        {
            Log.Error($"[WithFriends] {falha}");
        }

        lista.End();
    }
}
