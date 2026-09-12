namespace WithFriends.Protocol.Messages;

/// <summary>
/// O que trafega no primeiro byte de <c>SessaoComando.Payload</c>.
///
/// <para>Vive no protocolo, e não no cliente, porque o <b>coordenador</b>
/// precisa entender o suficiente para aplicar autoridade — ver
/// <see cref="AutoridadeDeComando"/>. Ele continua sem entender de jogo: lê um
/// byte, não um comando.</para>
/// </summary>
public enum TipoDeComando : byte
{
    /// <summary>Velocidade do tempo — §3: o tempo é negociado, nunca imposto.</summary>
    Velocidade = 1,

    /// <summary>Alistar/desalistar um pawn — a primeira ordem de jogo de verdade.</summary>
    Alistar = 2,

    /// <summary>
    /// Ordem direta a um pawn (clique com o botão direito). Passa toda ordem
    /// manual do jogador — é o comando de maior alcance.
    /// </summary>
    OrdemDeTrabalho = 3,

    /// <summary>
    /// Um designador aplicado a uma célula ou a uma coisa: construir, minerar,
    /// cortar, demolir, cancelar. É a outra metade do que o jogador faz com o
    /// mouse — a primeira era a ordem direta.
    /// </summary>
    Designar = 4,

    /// <summary>
    /// Provocar um incidente no mapa da visita. Só o anfitrião.
    ///
    /// <para>Nasceu de uma necessidade de teste — ferramentas de debug estão
    /// bloqueadas na visita (e devem estar), então não havia como chamar um
    /// raid para exercitar combate. Mas não é gambiarra de teste: é a primeira
    /// decisão de colônia, e decisão de colônia é do dono dela.</para>
    /// </summary>
    Incidente = 5,

    /// <summary>
    /// Um designador aplicado a **várias** células de uma vez — o arrasto.
    ///
    /// <para>Existe por fluidez, e a diferença é grande: arrastar sobre 40
    /// células gerava 40 comandos, cada um carimbado para o seu tick. Com o jogo
    /// pausado, cada comando ganha um passo de um tick — então planejar uma
    /// parede virava quarenta passos, um de cada vez, e parecia que o jogo
    /// estava engasgando ou perdendo ordens.</para>
    /// </summary>
    DesignarVarias = 6,
}

/// <summary>
/// Quem pode propor o quê — §4: a visita acontece <b>na colônia do anfitrião</b>.
///
/// <para>A regra que o autor do projeto colocou: as decisões do mapa são de quem
/// mora nele. O visitante manda nos próprios pawns — ajuda humanitária, tropas
/// (§5) — e não nas escolhas da colônia: missões, aceitar ou recusar eventos,
/// provocar acontecimentos.</para>
///
/// <para>Não é anti-cheat (§1.1, não é o objetivo). É evitar que duas pessoas
/// respondam ao mesmo diálogo, que é bagunça mesmo entre amigos — e, do lado
/// técnico, evitar duas respostas para uma pergunta que só admite uma.</para>
///
/// <para>Quem aplica é o coordenador, no momento de carimbar. Um comando
/// recusado nunca chega a existir para ninguém, então não há metade aplicada.</para>
/// </summary>
public static class AutoridadeDeComando
{
    /// <summary>Este tipo é decisão da colônia, e portanto só do anfitrião?</summary>
    public static bool SoDoAnfitriao(byte tipo) =>
        (TipoDeComando)tipo == TipoDeComando.Incidente;

    /// <summary>
    /// Lê o tipo de um payload. <c>0</c> para payload vazio — que nenhum
    /// <see cref="TipoDeComando"/> usa, então nunca é confundido com um válido.
    /// </summary>
    public static byte TipoDe(byte[]? payload) =>
        payload is { Length: > 0 } ? payload[0] : (byte)0;
}
