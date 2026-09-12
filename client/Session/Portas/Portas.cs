using System.Collections.Generic;
using WithFriends.Protocol.Determinismo;

namespace WithFriends.Client.Session.Portas;

// Anel 2 da componentização (ver docs/SESSAO-COMPONENTES.md).
//
// Cada porta nomeia poucos membros do RimWorld e existe para concentrar num
// arquivo só o que uma atualização do jogo pode quebrar. As interfaces em si
// não mencionam nenhum tipo do jogo — ticks são `long`, impressões digitais
// são `string` — para que o núcleo (anel 1) continue testável sem o jogo.

/// <summary>
/// Impressão digital determinística do estado de simulação — §14.3 decisão 1:
/// o estado do RNG é barato de comparar e diverge imediatamente.
///
/// Granularidade por mapa (decisão 2): saber *onde* divergiu vale mais do que
/// saber *que* divergiu.
///
/// Ideia reusada de `Source/Client/Desyncs/ClientSyncOpinion.cs` do
/// Multiplayer (MIT, Zetrith) — ver THIRD_PARTY/Multiplayer-MIT.txt.
/// </summary>
public interface IImpressaoDigital
{
    /// <summary>Começa a coletar a opinião de um novo intervalo.</summary>
    void IniciarIntervalo(long tick);

    /// <summary>Amostra o estado do mundo. Chamado uma vez por tick simulado.</summary>
    void AmostrarMundo();

    /// <summary>
    /// Amostra o estado depois que um mapa tickou. Granularidade por mapa —
    /// §14.3 decisão 2: saber *onde* divergiu, não só *que* divergiu.
    /// </summary>
    void AmostrarMapa(int mapaId);

    /// <summary>Amostra o estado logo depois de aplicar um comando da sessão.</summary>
    void AmostrarComando();

    /// <summary>Fecha o intervalo e devolve a opinião a comparar.</summary>
    OpiniaoDeSincronia FecharIntervalo(long tick);
}

/// <summary>
/// Controle de tempo dentro da sessão. Fora dela, o jogador manda no próprio
/// tempo sem restrição alguma (§3).
/// </summary>
public interface IRelogioDeSessao
{
    long TickAtual { get; }

    /// <summary>Se a simulação local está parada.</summary>
    bool Pausado { get; }

    /// <summary>
    /// Teto de simulação: o jogo não avança além disto. É a barreira comum da
    /// §2.3 aplicada localmente — o servidor diz até onde, e este é o freio.
    /// </summary>
    void LimitarAte(long tick);

    void Pausar();
    void Retomar();
}

/// <summary>
/// Congelamento e retorno — a propriedade de segurança da §2.3.
///
/// O <c>checkpoint_pre_sessao</c> é tirado antes de qualquer tick compartilhado.
/// Se der desync, volta-se a ele: **perde-se o encontro, nunca a colônia.**
/// </summary>
public interface ICongelador
{
    /// <summary>
    /// Para a simulação e produz o checkpoint pré-sessão. Devolve o
    /// <c>content_hash</c>, que é a identidade do ponto de retorno (§7.1).
    /// </summary>
    string CongelarECheckpoint();

    /// <summary>
    /// Volta a colônia ao checkpoint indicado. Nunca é chamado sozinho: a
    /// decisão é do núcleo, a execução é daqui.
    /// </summary>
    /// <param name="guardarEstadoAtual">
    /// Guarda o estado atual num checkpoint antes de voltar — append-only vale
    /// também para o rollback (§7.1 regra 1). Só é <c>false</c> ao voltar de
    /// uma visita, quando o jogo carregado é o do **anfitrião** e não seria
    /// checkpoint de ninguém aqui.
    /// </param>
    void Restaurar(string contentHash, bool guardarEstadoAtual = true);

    void Descongelar();
}

/// <summary>
/// Aplica um comando de sessão no tick agendado. Dentro da sessão **só
/// comandos trafegam**, ambos simulam (§2.3).
///
/// Esta é a porta que concentra a dívida de manutenção: cada tipo de comando
/// suportado nomeia comportamento do jogo. O Multiplayer registra ~370
/// membros; aqui cada entrada é uma decisão de escopo consciente
/// (ver docs/SESSAO-COMPONENTES.md §3).
/// </summary>
public interface IAplicadorDeComando
{
    /// <summary>Executa. Deve ser determinístico dado o mesmo estado.</summary>
    void Aplicar(string autor, byte[] payload);

    /// <summary>Tipos de comando que este build entende, para o handshake de sessão.</summary>
    IReadOnlyCollection<string> ComandosSuportados { get; }
}

/// <summary>
/// Contexto de facção: dentro de uma sessão cada jogador controla apenas os
/// próprios pawns, como facções distintas no mesmo mapa (§5).
///
/// Conceito nativo do RimWorld — não precisa inventar, só amarrar.
/// </summary>
public interface IContextoDeFaccao
{
    /// <summary>Executa <paramref name="acao"/> como se fosse a facção do jogador dado.</summary>
    void Como(string playerId, System.Action acao);

    /// <summary>Facção local associada a um participante da sessão.</summary>
    int FaccaoDe(string playerId);
}
