using System;
using System.IO;

namespace WithFriends.Protocol.Messages;

/// <summary>Interações da §5. Todas herdam o mesmo fluxo de sessão.</summary>
public enum TipoSessao
{
    Visitar = 1,
    AjudarNaDefesa = 2,
    AtaqueConjunto = 3,
    Pvp = 4,
    ComercioPresencial = 5,

    /// <summary>
    /// Ensaio: os dois simulam sob a mesma barreira, **sem** mapa
    /// compartilhado. Serve para validar tempo, barreira, pausa e aborto
    /// antes de existir troca de estado — e por isso as impressões digitais
    /// não são comparadas (os dois estão em colônias diferentes, divergir é
    /// o esperado).
    /// </summary>
    Ensaio = 6,
}

public enum MotivoFimDeSessao
{
    EncerradaPelosParticipantes = 1,
    ParticipanteDesconectou = 2,
    Desync = 3,
    ModsIncompativeis = 4,
    Expirou = 5,
}

/// <summary>
/// sessao.convite — §2.3. Uma sessão é um acordo temporário entre N jogadores
/// (v1: N=2) para simular em conjunto um subconjunto de mapas.
///
/// O <c>SessionModSetHash</c> viaja aqui porque a verificação de mods acontece
/// **na entrada da sessão**, nunca no login (§8) — assim divergência de mod
/// jamais impede alguém de jogar sozinho.
/// </summary>
public sealed class SessaoConvite : IMessage
{
    public string ConviteId { get; init; } = "";
    /// <summary>Preenchido pelo servidor: é a conexão que manda, não o que o cliente diz.</summary>
    public string De { get; init; } = "";
    public string Para { get; init; } = "";
    public TipoSessao Tipo { get; init; }
    /// <summary>Colônia que hospeda o encontro — o mapa que entra na sessão.</summary>
    public string ColoniaAnfitria { get; init; } = "";
    public string SessionModSetHash { get; init; } = "";

    public MessageId Id => MessageId.SessaoConvite;

    public void Write(BinaryWriter w)
    {
        w.Write(ConviteId);
        w.Write(De);
        w.Write(Para);
        w.Write((int)Tipo);
        w.Write(ColoniaAnfitria);
        w.Write(SessionModSetHash);
    }

    public static SessaoConvite Read(BinaryReader r) => new()
    {
        ConviteId = r.ReadString(),
        De = r.ReadString(),
        Para = r.ReadString(),
        Tipo = (TipoSessao)r.ReadInt32(),
        ColoniaAnfitria = r.ReadString(),
        SessionModSetHash = r.ReadString(),
    };
}

/// <summary>sessao.aceite — o convidado topa e declara o próprio conjunto de mods.</summary>
public sealed class SessaoAceite : IMessage
{
    public string ConviteId { get; init; } = "";
    public string SessionModSetHash { get; init; } = "";

    public MessageId Id => MessageId.SessaoAceite;

    public void Write(BinaryWriter w)
    {
        w.Write(ConviteId);
        w.Write(SessionModSetHash);
    }

    public static SessaoAceite Read(BinaryReader r) => new()
    {
        ConviteId = r.ReadString(),
        SessionModSetHash = r.ReadString(),
    };
}

/// <summary>sessao.recusa — sempre com motivo legível (§9.1).</summary>
public sealed class SessaoRecusa : IMessage
{
    public string ConviteId { get; init; } = "";
    public string Explicacao { get; init; } = "";

    public MessageId Id => MessageId.SessaoRecusa;

    public void Write(BinaryWriter w)
    {
        w.Write(ConviteId);
        w.Write(Explicacao);
    }

    public static SessaoRecusa Read(BinaryReader r) => new()
    {
        ConviteId = r.ReadString(),
        Explicacao = r.ReadString(),
    };
}

/// <summary>
/// sessao.inicio — os dois lados congelam, tiram <c>checkpoint_pre_sessao</c>
/// e passam a simular sob barreira comum a partir de <c>TickInicial</c>.
///
/// A <c>Semente</c> é o estado inicial de RNG combinado: dentro da sessão o
/// determinismo é o contrato, e RNG divergente é a primeira coisa a quebrar
/// (§14.3).
/// </summary>
public sealed class SessaoInicio : IMessage
{
    public string SessaoId { get; init; } = "";
    public TipoSessao Tipo { get; init; }
    public string Anfitriao { get; init; } = "";
    public string Visitante { get; init; } = "";
    public string ColoniaAnfitria { get; init; } = "";
    public long TickInicial { get; init; }
    public int Semente { get; init; }

    /// <summary>
    /// Se divergência de impressão digital aborta a sessão. Falso no ensaio,
    /// onde os dois lados simulam colônias diferentes de propósito.
    /// </summary>
    public bool CompararDigitais { get; init; } = true;

    public MessageId Id => MessageId.SessaoInicio;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write((int)Tipo);
        w.Write(Anfitriao);
        w.Write(Visitante);
        w.Write(ColoniaAnfitria);
        w.Write(TickInicial);
        w.Write(Semente);
        w.Write(CompararDigitais);
    }

    public static SessaoInicio Read(BinaryReader r) => new()
    {
        SessaoId = r.ReadString(),
        Tipo = (TipoSessao)r.ReadInt32(),
        Anfitriao = r.ReadString(),
        Visitante = r.ReadString(),
        ColoniaAnfitria = r.ReadString(),
        TickInicial = r.ReadInt64(),
        Semente = r.ReadInt32(),
        CompararDigitais = r.ReadBoolean(),
    };
}

/// <summary>
/// sessao.comando — dentro da sessão **só comandos trafegam**, ambos simulam
/// (§2.3). O servidor agenda cada comando para um tick futuro e devolve o
/// mesmo agendamento para todos: é isso que torna a ordem idêntica dos dois
/// lados sem o servidor entender de RimWorld.
/// </summary>
public sealed class SessaoComando : IMessage
{
    public string SessaoId { get; init; } = "";
    public string Autor { get; init; } = "";
    /// <summary>Preenchido pelo servidor. Zero na proposta do cliente.</summary>
    public long TickAlvo { get; init; }
    /// <summary>Ordem dentro do mesmo tick — desempate estável.</summary>
    public int Ordem { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public MessageId Id => MessageId.SessaoComando;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write(Autor);
        w.Write(TickAlvo);
        w.Write(Ordem);
        w.Write(Payload.Length);
        w.Write(Payload);
    }

    public static SessaoComando Read(BinaryReader r)
    {
        string sessaoId = r.ReadString();
        string autor = r.ReadString();
        long tickAlvo = r.ReadInt64();
        int ordem = r.ReadInt32();
        int tamanho = r.ReadInt32();
        return new SessaoComando
        {
            SessaoId = sessaoId,
            Autor = autor,
            TickAlvo = tickAlvo,
            Ordem = ordem,
            Payload = r.ReadBytes(tamanho),
        };
    }
}

/// <summary>
/// sessao.barreira — cliente reporta "simulei até o tick N"; servidor devolve
/// o tick até onde **todos** já chegaram. Ninguém passa da barreira comum.
///
/// Carrega também a impressão digital de RNG do intervalo: divergência aparece
/// aqui, no tick em que aconteceu, e não horas depois (§14.3).
/// </summary>
public sealed class SessaoBarreira : IMessage
{
    public string SessaoId { get; init; } = "";
    public string Autor { get; init; } = "";
    public long Tick { get; init; }
    /// <summary>Estado de RNG ao fim do intervalo. Vazio = não verificado.</summary>
    public string Fingerprint { get; init; } = "";
    /// <summary>Do servidor para os clientes: até onde é seguro simular.</summary>
    public long TickLiberado { get; init; }
    /// <summary>Do cliente: se ele quer o tempo parado (§3, consenso de pausa).</summary>
    public bool Pausado { get; init; }

    /// <summary>
    /// Do servidor: **todos** os participantes pediram pausa.
    ///
    /// <para>Não é o mesmo que <see cref="Pausado"/>, e a diferença é o que
    /// torna possível dar ordens com o jogo parado. Comando só acontece dentro
    /// de um tick, e pausado não há tick — então, pausado, alistar um pawn não
    /// fazia nada até alguém despausar.</para>
    ///
    /// <para><b>Não basta para aplicar comando parado, e a tentativa custou um
    /// desync.</b> "Todos pausados" não implica "todos no mesmo tick": a pausa
    /// para cada lado onde ele está, e a barreira segura a liberação no mínimo —
    /// então quem estava à frente continua à frente, para sempre, enquanto a
    /// pausa durar. Medido: um lado parado no tick 972, o outro no 952.</para>
    ///
    /// <para>Fica na mensagem porque continua sendo a informação certa para
    /// exibir "todos pausados" e para a decisão futura de tick guiado pela
    /// barreira — ver docs/COMANDOS-DE-SESSAO.md.</para>
    /// </summary>
    public bool TodosPausados { get; init; }

    /// <summary>
    /// Do cliente: a velocidade que **ele** está pedindo.
    ///
    /// <para>Voto, não ordem. O coordenador junta os pedidos, o mais lento
    /// manda, e o resultado é o ritmo com que ele avança o tick liberado. O
    /// relógio de cada jogador continua sendo dele; o que ele controla é o
    /// próprio pedido.</para>
    /// </summary>
    public VelocidadeDeSessao Velocidade { get; init; } = VelocidadeDeSessao.Normal;

    /// <summary>
    /// Do cliente: <b>este jogador acabou de mexer no botão</b>.
    ///
    /// <para>Sem isto não dá para distinguir "estou pedindo Normal porque
    /// cliquei agora" de "estou repetindo o Normal que já valia". É a diferença
    /// entre um controle global que responde a quem mexeu e um que fica preso
    /// no último consenso.</para>
    /// </summary>
    public bool MudouVelocidade { get; init; }

    /// <summary>
    /// Do servidor: a velocidade que está valendo para todos.
    ///
    /// Volta para os clientes porque os dois precisam **ver** o tempo que está
    /// correndo. Botão que mostra outra coisa é pior que botão que não obedece.
    /// </summary>
    public VelocidadeDeSessao VelocidadeAcordada { get; init; } = VelocidadeDeSessao.Normal;

    /// <summary>
    /// Do servidor: quem mexeu no tempo por último.
    ///
    /// Saber <b>quem</b> pausou é parte da mecânica, não enfeite: numa visita a
    /// pausa costuma ser um pedido de atenção ("vi uma ameaça, para"), e ela só
    /// funciona como pedido se dá para ver de quem veio.
    /// </summary>
    public string QuemMudou { get; init; } = "";

    /// <summary>
    /// Do servidor: quantas vezes o tempo mudou nesta sessão.
    ///
    /// O cliente guarda a versão que viu quando pediu; só uma versão **maior**
    /// encerra o pedido dele. "Quem mudou por último" não serve: continua
    /// respondendo "o outro" muito depois de o outro ter mexido.
    /// </summary>
    public long VersaoDoTempo { get; init; }

    /// <summary>
    /// Do servidor: a partir de qual passo <see cref="VelocidadeAcordada"/>
    /// vale.
    ///
    /// O cliente precisa decidir "este passo simula?" por **número de passo**, e
    /// não por "o que eu sei agora" — senão os dois lados decidem diferente para
    /// o mesmo passo, que é desync na certa.
    /// </summary>
    public long VelocidadeDesdePasso { get; init; }

    /// <summary>
    /// Do servidor: o pedido de despausar deste cliente foi recusado porque a
    /// pausa é recente.
    ///
    /// Ver <c>Sessao.ProtecaoDaPausa</c>. Volta explicado para o jogador não
    /// achar que a tecla falhou.
    /// </summary>
    public bool DespausarRecusado { get; init; }

    public MessageId Id => MessageId.SessaoBarreira;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write(Autor);
        w.Write(Tick);
        w.Write(Fingerprint);
        w.Write(TickLiberado);
        w.Write(Pausado);
        w.Write(TodosPausados);
        w.Write((byte)Velocidade);
        w.Write(MudouVelocidade);
        w.Write((byte)VelocidadeAcordada);
        w.Write(QuemMudou);
        w.Write(VersaoDoTempo);
        w.Write(VelocidadeDesdePasso);
        w.Write(DespausarRecusado);
    }

    public static SessaoBarreira Read(BinaryReader r) => new()
    {
        SessaoId = r.ReadString(),
        Autor = r.ReadString(),
        Tick = r.ReadInt64(),
        Fingerprint = r.ReadString(),
        TickLiberado = r.ReadInt64(),
        Pausado = r.ReadBoolean(),
        TodosPausados = r.ReadBoolean(),
        Velocidade = (VelocidadeDeSessao)r.ReadByte(),
        MudouVelocidade = r.ReadBoolean(),
        VelocidadeAcordada = (VelocidadeDeSessao)r.ReadByte(),
        QuemMudou = r.ReadString(),
        VersaoDoTempo = r.ReadInt64(),
        VelocidadeDesdePasso = r.ReadInt64(),
        DespausarRecusado = r.ReadBoolean(),
    };
}

/// <summary>
/// sessao.mapa — o bootstrap da visita (§4, ADR 0007).
///
/// O anfitrião serializa o mapa do encontro e manda **uma vez**, comprimido.
/// Medido: 11,09 MB de XML viram 0,64 MB — ver docs/MEDICOES.md. O que
/// sustenta a visita depois disso é o lockstep, não mais transferência.
///
/// O coordenador não interpreta nada disto: repassa (§10).
/// </summary>
public sealed class SessaoMapa : IMessage
{
    public string SessaoId { get; init; } = "";
    /// <summary>Id do mapa no jogo do anfitrião.</summary>
    public int MapaId { get; init; }
    /// <summary>Tick de sessão em que o mapa foi congelado.</summary>
    public long Tick { get; init; }
    /// <summary>Tamanho antes de comprimir, para barra de progresso e sanidade.</summary>
    public int TamanhoCru { get; init; }
    /// <summary>Hash do conteúdo **cru** — conferido antes de tocar o disco.</summary>
    public string ContentHash { get; init; } = "";
    /// <summary>XML do mapa, comprimido com gzip.</summary>
    public byte[] Comprimido { get; init; } = Array.Empty<byte>();

    public MessageId Id => MessageId.SessaoMapa;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write(MapaId);
        w.Write(Tick);
        w.Write(TamanhoCru);
        w.Write(ContentHash);
        w.Write(Comprimido.Length);
        w.Write(Comprimido);
    }

    public static SessaoMapa Read(BinaryReader r)
    {
        string sessaoId = r.ReadString();
        int mapaId = r.ReadInt32();
        long tick = r.ReadInt64();
        int cru = r.ReadInt32();
        string hash = r.ReadString();
        int tamanho = r.ReadInt32();
        return new SessaoMapa
        {
            SessaoId = sessaoId,
            MapaId = mapaId,
            Tick = tick,
            TamanhoCru = cru,
            ContentHash = hash,
            Comprimido = r.ReadBytes(tamanho),
        };
    }
}

/// <summary>
/// sessao.partida — o bootstrap da visita (ADR 0010).
///
/// O anfitrião manda a **partida inteira**, não o mapa. Um mapa referencia
/// ideologias, políticas, facções e relações que vivem na partida; sozinho ele
/// abre e não se reconecta a nada — medido e documentado em
/// `docs/MEDICOES.md`.
///
/// Medido: 13,12 MB de save viram 1,31 MB comprimidos. O coordenador repassa
/// sem interpretar (§10).
/// </summary>
public sealed class SessaoPartida : IMessage
{
    public string SessaoId { get; init; } = "";
    /// <summary>Tick de jogo do anfitrião no congelamento.</summary>
    public long Tick { get; init; }
    /// <summary>Tamanho antes de comprimir, para a barra de progresso.</summary>
    public int TamanhoCru { get; init; }
    /// <summary>Hash do save **cru** — conferido antes de tocar o disco.</summary>
    public string ContentHash { get; init; } = "";
    public byte[] Comprimido { get; init; } = Array.Empty<byte>();

    public MessageId Id => MessageId.SessaoPartida;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write(Tick);
        w.Write(TamanhoCru);
        w.Write(ContentHash);
        w.Write(Comprimido.Length);
        w.Write(Comprimido);
    }

    public static SessaoPartida Read(BinaryReader r)
    {
        string sessaoId = r.ReadString();
        long tick = r.ReadInt64();
        int cru = r.ReadInt32();
        string hash = r.ReadString();
        int tamanho = r.ReadInt32();
        return new SessaoPartida
        {
            SessaoId = sessaoId,
            Tick = tick,
            TamanhoCru = cru,
            ContentHash = hash,
            Comprimido = r.ReadBytes(tamanho),
        };
    }
}

/// <summary>sessao.fim — encerramento combinado; cada lado faz commit do próprio save.</summary>
public sealed class SessaoFim : IMessage
{
    public string SessaoId { get; init; } = "";
    public MotivoFimDeSessao Motivo { get; init; }
    public string Explicacao { get; init; } = "";

    public MessageId Id => MessageId.SessaoFim;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write((int)Motivo);
        w.Write(Explicacao);
    }

    public static SessaoFim Read(BinaryReader r) => new()
    {
        SessaoId = r.ReadString(),
        Motivo = (MotivoFimDeSessao)r.ReadInt32(),
        Explicacao = r.ReadString(),
    };
}

/// <summary>
/// sessao.aborto — §2.3, propriedade de segurança: volta os dois para o
/// <c>checkpoint_pre_sessao</c>. **Perde-se o encontro, nunca a colônia.**
/// </summary>
public sealed class SessaoAborto : IMessage
{
    public string SessaoId { get; init; } = "";
    public MotivoFimDeSessao Motivo { get; init; }
    public string Explicacao { get; init; } = "";
    /// <summary>Último tick comprovadamente consistente — o ponto de rollback.</summary>
    public long UltimoTickValido { get; init; }

    public MessageId Id => MessageId.SessaoAborto;

    public void Write(BinaryWriter w)
    {
        w.Write(SessaoId);
        w.Write((int)Motivo);
        w.Write(Explicacao);
        w.Write(UltimoTickValido);
    }

    public static SessaoAborto Read(BinaryReader r) => new()
    {
        SessaoId = r.ReadString(),
        Motivo = (MotivoFimDeSessao)r.ReadInt32(),
        Explicacao = r.ReadString(),
        UltimoTickValido = r.ReadInt64(),
    };
}
