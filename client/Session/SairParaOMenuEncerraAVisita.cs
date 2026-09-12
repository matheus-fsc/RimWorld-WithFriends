using HarmonyLib;
using Verse;

namespace WithFriends.Client.Session;

/// <summary>
/// Sair para o menu principal encerra a visita.
///
/// <para><b>O que acontecia.</b> O jogador saía para o menu e a visita
/// continuava existindo: o coordenador nunca era avisado, o outro jogador ficava
/// parado na barreira esperando um relato que não vinha mais, e o estado estático
/// da visita — que atravessa a troca de partida de propósito — sobrevivia para a
/// próxima partida carregada.</para>
///
/// <para>Desconectar avisaria; sair para o menu não desconecta nada. O processo
/// continua vivo, a conexão continua aberta, e do lado de fora é
/// indistinguível de um jogador que travou.</para>
///
/// <para><b>Por que aqui e não em <c>ClearAllMapsAndWorld</c>.</b> É onde o
/// Multiplayer engancha, mas lá não serve para nós: o nosso ponto de junção
/// refeito recarrega a partida (ADR 0019), e recarregar passa pelo mesmo
/// caminho. A visita seria encerrada a cada ressincronização — exatamente o
/// contrário do que ela existe para fazer.</para>
///
/// <para><c>GenScene.GoToMainMenu</c> diz uma coisa só, e é essa.</para>
/// </summary>
[HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
public static class SairParaOMenuEncerraAVisita
{
    [HarmonyPrefix]
    public static void Antes()
    {
        if (!VisitaEmAndamento.Ativa) return;

        Log.Message("[WithFriends] saindo para o menu principal — encerrando a visita.");

        // Avisar primeiro, enquanto a conexão e o componente ainda existem: é
        // esta mensagem que solta o outro jogador da barreira.
        Colony.SincronizacaoComponent.Atual?.Sessao
            .PedirEncerramento("o jogador saiu para o menu principal");

        // O save temporário da visita não fica para trás (ADR 0008).
        BootstrapDaPartida.Descartar(VisitaEmAndamento.SaveDaVisita);

        // E o estado estático some junto. Ele atravessa a troca de partida de
        // propósito; sem limpar aqui, a próxima partida carregada acharia que
        // está no meio de uma visita.
        VisitaEmAndamento.Limpar();
    }
}
