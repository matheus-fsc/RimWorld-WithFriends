using System.Collections.Generic;

namespace WithFriends.Protocol.Messages;

/// <summary>
/// A velocidade que um jogador está pedindo. Espelha <c>Verse.TimeSpeed</c>.
///
/// <para>Vive no protocolo porque o <b>coordenador</b> precisa dela: é ele que
/// decide o ritmo do relógio compartilhado. Ele continua sem entender de jogo —
/// para ele isto é um número que vira multiplicador.</para>
/// </summary>
public enum VelocidadeDeSessao : byte
{
    Pausado = 0,
    Normal = 1,
    Rapido = 2,
    MuitoRapido = 3,
    Ultra = 4,
}

/// <summary>
/// Como as velocidades pedidas viram um ritmo só — §3: o tempo é negociado,
/// nunca imposto.
/// </summary>
public static class RitmoDaSessao
{
    /// <summary>Ticks por segundo de tempo real em velocidade Normal.</summary>
    public const int TicksPorSegundo = 60;

    /// <summary>Multiplicadores do próprio RimWorld.</summary>
    public static float Multiplicador(VelocidadeDeSessao velocidade) => velocidade switch
    {
        VelocidadeDeSessao.Normal => 1f,
        VelocidadeDeSessao.Rapido => 3f,
        VelocidadeDeSessao.MuitoRapido => 6f,
        VelocidadeDeSessao.Ultra => 15f,
        _ => 0f,
    };

    // Houve aqui um "o mais lento manda", e ele foi descartado com razão: com
    // ele, pausado, **só quem pausou conseguia despausar** — porque o pedido do
    // outro continuava sendo o mais rápido dos dois e perdia sempre. Um controle
    // que só responde a uma pessoa por vez não é um controle compartilhado.
    //
    // O que vale agora é um relógio só, que qualquer um dos dois muda: §3 ("o
    // tempo é negociado") lido como controle comum em vez de veto mútuo.
}
