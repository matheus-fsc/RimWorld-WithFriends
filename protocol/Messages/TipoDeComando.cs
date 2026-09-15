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

    /// <summary>
    /// "Priorizar este trabalho" — o clique direito que manda um pawn fazer
    /// **agora** o que ele faria depois.
    ///
    /// <para>Parece a mesma coisa que <see cref="OrdemDeTrabalho"/>, e por fora
    /// é: <c>TryTakeOrderedJobPrioritizedWork</c> chama
    /// <c>TryTakeOrderedJob</c> por dentro. Mas o que vem **depois** dessa
    /// chamada não é ordem nenhuma — é o chamador escrevendo em
    /// <c>job.workGiverDef</c> e em <c>pawn.mindState.priorityWork</c>.</para>
    ///
    /// <para>Com só a chamada de dentro interceptada, a nossa recusa devolvia
    /// "aceito", o chamador escrevia essas duas coisas no lado de quem clicou, e
    /// mais nada acontecia no outro. Divergência silenciosa a partir do próximo
    /// tick — o pior tipo, porque nada dá erro.</para>
    /// </summary>
    OrdemPriorizada = 7,

    /// <summary>
    /// Uma chave liga/desliga do jogador: segurar fogo, proibir um item.
    ///
    /// <para>Genérico de propósito. Cada um desses é uma propriedade
    /// <c>bool</c> com dono conhecido, e o Multiplayer registra uma linha por
    /// propriedade (<c>Pawn_DraftController.FireAtWill</c>,
    /// <c>CompForbiddable.Forbidden</c>, …). Em vez de um tipo de comando por
    /// linha dessas, um tipo só com uma chave: quem sabe o que a chave
    /// significa é o registro em <c>AlternaveisDeSessao</c>, e acrescentar o
    /// próximo custa uma linha, sem mexer no protocolo.</para>
    /// </summary>
    Alternar = 8,

    /// <summary>
    /// A configuração de um estoque: prioridade e filtro, inteiros.
    ///
    /// <para><b>Estado, não operação.</b> O <c>ThingFilter</c> tem oito
    /// mutadores (<c>SetAllow</c> em quatro sabores, <c>SetAllowAll</c>,
    /// <c>SetDisallowAll</c>, <c>SetFromPreset</c>, <c>CopyAllowancesFrom</c>),
    /// vários com parâmetros que não atravessam rede de graça — listas de
    /// exceções, um filtro-pai inteiro. O Multiplayer sincroniza a interação com
    /// o widget e precisa de marcadores de contexto para saber de quem é o
    /// filtro que está sendo mexido.</para>
    ///
    /// <para>Aqui viaja o **resultado**: quais defs estão permitidas agora, mais
    /// as faixas. Um payload cobre os oito mutadores, os widgets que ainda não
    /// existem e os que vêm de mod — e, por ser estado absoluto, dois comandos
    /// fora de ordem não deixam o filtro num meio-termo que ninguém pediu.</para>
    ///
    /// <para>Custa mais bytes: "permitir tudo" são umas centenas de nomes de
    /// def. É comando raro e de jogador, não de tick.</para>
    /// </summary>
    Estoque = 9,

    /// <summary>
    /// Encerrar o job atual de um pawn — o outro jeito de o jogador mandar,
    /// e o que faltava.
    ///
    /// <para>Toda ordem manual passava por <c>TryTakeOrderedJob</c>, que já era
    /// comando. Mas a ordem de ir a pé tem um caminho que não passa por lá:</para>
    ///
    /// <code>
    /// // FloatMenuOptionProvider_DraftedMove.PawnGotoAction
    /// if (pawn.Position == gotoLoc) {
    ///     if (pawn.CurJobDef == JobDefOf.Goto)
    ///         pawn.jobs.EndCurrentJob(JobCondition.Succeeded);   // direto, na interface
    /// }
    /// </code>
    ///
    /// <para>Arrastar o marcador de "ir aqui" até a célula onde o colono já
    /// está encerra o <c>Goto</c> dele <b>na hora e só na máquina de quem
    /// clicou</b>. O outro lado continua andando, e a partir do tick seguinte
    /// são duas simulações.</para>
    ///
    /// <para>Foi assim que apareceu, com a pilha de chamada no rastreio:</para>
    ///
    /// <code>
    /// passo 580 #68 Fitz: job Goto encerrado: Succeeded
    ///   &lt; FloatMenuOptionProvider_DraftedMove.PawnGotoAction
    ///   &lt; MultiPawnGotoController.IssueGotoJobs
    ///   &lt; Selector.HandleMapClicks &lt; MapInterface.HandleLowPriorityInput
    /// </code>
    /// </summary>
    EncerrarJob = 10,

    /// <summary>
    /// Um ajuste de pawn: prioridade de trabalho, restrição de área, mestre de
    /// animal, seguir alistado.
    ///
    /// <para><b>Por que um tipo e não quatro.</b> Mesma razão de
    /// <see cref="Alternar"/>, um degrau acima. Aqueles são todos
    /// <c>bool</c>; estes têm formas diferentes — um inteiro com um def
    /// (prioridade), uma referência (área, mestre), um booleano (seguir). Um
    /// payload de <c>(pawn, chave, número, texto)</c> cobre os quatro, e quem
    /// sabe o que a chave significa é o registro em <c>AjustesDePawn</c>.</para>
    ///
    /// <para>Vieram do mapa de decisões do Multiplayer (<c>wf decisoes</c>), da
    /// família <b>pawn</b> — a que uma visita de verdade exercita. E são
    /// decisão de simulação, não enfeite: prioridade de trabalho muda o que o
    /// colono faz no tick seguinte, que é exatamente a classe de divergência
    /// que custou o dia 14.</para>
    /// </summary>
    AjusteDePawn = 11,
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
