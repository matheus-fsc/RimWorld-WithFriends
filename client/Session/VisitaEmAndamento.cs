using UnityEngine;
using Verse;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;

/// <summary>
/// O que precisa sobreviver à troca de partida.
///
/// Carregar a partida do anfitrião destrói o <c>Game</c> inteiro — e com ele o
/// <c>SincronizacaoComponent</c>, que é onde a sessão mora. Sem um lugar fora
/// da partida, o visitante chegaria do outro lado sem saber que está numa
/// visita, sem ponto de retorno e sem sessão.
///
/// Estático de propósito: é o único estado do mod que **não** pertence a uma
/// partida, porque ele existe justamente para atravessar a troca de uma.
/// </summary>
public static class VisitaEmAndamento
{
    public static SessaoInicio? Inicio { get; private set; }

    /// <summary>
    /// Ponto de retorno do visitante: o <c>checkpoint_pre_sessao</c> da
    /// colônia **dele**, não da partida onde ele está agora.
    /// </summary>
    public static string? HashPreSessao { get; private set; }

    /// <summary>Nome do save temporário da visita, para limpar na saída.</summary>
    public static string? SaveDaVisita { get; private set; }

    public static bool SouVisitante { get; private set; }

    /// <summary>Já retomamos a sessão depois da troca de partida?</summary>
    public static bool Retomada { get; set; }

    /// <summary>
    /// A partida de onde saímos.
    ///
    /// <c>GameDataSaveLoader.LoadGame</c> **enfileira** a carga num long event:
    /// a partida antiga continua atualizando por vários frames depois da
    /// chamada. Sem comparar a referência, a sessão era retomada dentro da
    /// colônia do próprio visitante — que então reportava a digital dela, e a
    /// visita abortava no tick 0 por divergência perfeitamente real.
    /// </summary>
    public static Game? JogoDeOrigem { get; private set; }

    /// <summary>A troca de partida já aconteceu de fato?</summary>
    public static bool JaTrocouDePartida =>
        Ativa && JogoDeOrigem != null && !ReferenceEquals(Current.Game, JogoDeOrigem);

    public static bool Ativa => Inicio != null;

    public static void Comecar(SessaoInicio inicio, string? hashPreSessao, string? saveDaVisita, bool souVisitante)
    {
        JogoDeOrigem = Current.Game;
        Inicio = inicio;

        // **O ponto de retorno nunca é apagado por uma travessia nova.**
        //
        // Esta função passou a ser chamada duas vezes: no bootstrap e de novo a
        // cada ponto de junção refeito (ressincronização). Na segunda vez, quem
        // chama está **dentro** da partida do anfitrião, e o congelador de lá
        // não conhece hash nenhum — passaria `null`.
        //
        // Apagar aqui seria apagar o caminho de volta para a colônia do
        // visitante. Perder o encontro é aceitável; perder a colônia não é
        // (§2.3), e a ressincronização existe justamente para não perder nem
        // um nem outro.
        if (hashPreSessao != null) HashPreSessao = hashPreSessao;

        SaveDaVisita = saveDaVisita;
        SouVisitante = souVisitante;
        Retomada = false;

        // **O jogo não pode parar quando a janela perde o foco.**
        //
        // Numa visita o outro lado espera a barreira, e barreira não anda com um
        // dos dois congelado. Sem "rodar em segundo plano", quem sai da janela
        // trava os dois — e do lado de dentro isso é indistinguível de desconexão.
        //
        // Não dá para deixar isso na mão da preferência de cada um: duas
        // instâncias na mesma máquina, que é como se testa, nunca estão as duas
        // em foco. O Multiplayer força o mesmo, pelo mesmo motivo.
        rodavaEmSegundoPlano = Application.runInBackground;
        Application.runInBackground = true;

        Log.Message(
            $"[WithFriends] visita {inicio.SessaoId} atravessando a troca de partida — " +
            $"papel: {(souVisitante ? "visitante" : "anfitrião")}, " +
            $"ponto de retorno: {hashPreSessao ?? "(nenhum)"}");
    }

    static bool rodavaEmSegundoPlano;

    public static void Limpar()
    {
        // A preferência é do jogador: fora da visita, volta a ser dele.
        Application.runInBackground = rodavaEmSegundoPlano;

        JogoDeOrigem = null;
        Inicio = null;
        HashPreSessao = null;
        SaveDaVisita = null;
        SouVisitante = false;
        Retomada = false;
    }
}
