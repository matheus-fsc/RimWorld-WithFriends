using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WithFriends.Protocol.Determinismo;

/// <summary>
/// Opinião de um cliente sobre um intervalo de ticks: o que ele afirma ter
/// calculado.
///
/// Ideia reusada de `Source/Client/Desyncs/ClientSyncOpinion.cs` do
/// Multiplayer (MIT, Copyright (c) 2018 Zetrith) — ver
/// THIRD_PARTY/Multiplayer-MIT.txt. Implementação própria.
///
/// Quatro decisões vêm de lá (§14.3):
/// 1. a impressão digital é o **estado do RNG** — barato e diverge na hora;
/// 2. granularidade **por mapa** — saber onde divergiu, não só que divergiu;
/// 3. o **modo de arredondamento** entra na comparação;
/// 4. stack traces completos só trafegam **depois** do desync; em jogo normal
///    só os hashes.
/// </summary>
public sealed class OpiniaoDeSincronia
{
    public long TickInicial { get; init; }
    public long TickFinal { get; set; }

    /// <summary>Estado do RNG do mundo, amostrado a cada tick do intervalo.</summary>
    public List<uint> EstadosDoMundo { get; } = new();

    /// <summary>Estado do RNG por mapa. A chave é o id do mapa no jogo.</summary>
    public Dictionary<int, List<uint>> EstadosPorMapa { get; } = new();

    /// <summary>Estado do RNG imediatamente após aplicar cada comando da sessão.</summary>
    public List<uint> EstadosDeComandos { get; } = new();

    public ModoDeArredondamentoFP ModoDeArredondamento { get; set; } = ModoDeArredondamentoFP.MaisProximo;

    /// <summary>
    /// Hashes de stack trace. Só os hashes trafegam em jogo normal; o texto
    /// completo é pedido depois de um desync (decisão 4).
    /// </summary>
    public List<int> HashesDeStackTrace { get; } = new();

    public List<uint> EstadosDoMapa(int mapaId)
    {
        if (!EstadosPorMapa.TryGetValue(mapaId, out var estados))
            EstadosPorMapa[mapaId] = estados = new List<uint>();
        return estados;
    }

    /// <summary>
    /// Compara com a opinião do outro lado e devolve **motivo legível**, não
    /// booleano (§14.3 decisão 2). <c>null</c> significa que bateu.
    ///
    /// A ordem importa: o que é mais barato de diagnosticar vem primeiro.
    /// Descobrir "modo de arredondamento diferente" poupa horas de caçada em
    /// estado de RNG que nunca ia bater.
    /// </summary>
    public string? Comparar(OpiniaoDeSincronia outra)
    {
        if (ModoDeArredondamento != outra.ModoDeArredondamento)
            return $"Modo de arredondamento de ponto flutuante não bate: " +
                   $"{ModoDeArredondamento} != {outra.ModoDeArredondamento}";

        if (TickInicial != outra.TickInicial)
            return $"Intervalos diferentes: começa em {TickInicial}, o outro em {outra.TickInicial}";

        var meusMapas = EstadosPorMapa.Keys.OrderBy(id => id).ToList();
        var outrosMapas = outra.EstadosPorMapa.Keys.OrderBy(id => id).ToList();
        if (!meusMapas.SequenceEqual(outrosMapas))
            return $"Os mapas em jogo não batem: [{string.Join(", ", meusMapas)}] != " +
                   $"[{string.Join(", ", outrosMapas)}]";

        foreach (int mapaId in meusMapas)
        {
            var meus = EstadosPorMapa[mapaId];
            var outros = outra.EstadosPorMapa[mapaId];
            int divergeEm = PrimeiraDiferenca(meus, outros);
            if (divergeEm >= 0)
                return $"Estado de RNG errado no mapa {mapaId}, no tick {TickInicial + divergeEm}";
        }

        int mundoDivergeEm = PrimeiraDiferenca(EstadosDoMundo, outra.EstadosDoMundo);
        if (mundoDivergeEm >= 0)
            return $"Estado de RNG errado no mundo, no tick {TickInicial + mundoDivergeEm}";

        if (!EstadosDeComandos.SequenceEqual(outra.EstadosDeComandos))
            return "Estado de RNG depois dos comandos não bate: " +
                   "os dois lados aplicaram comandos diferentes, ou na ordem diferente";

        if (HashesDeStackTrace.Count > 0 && outra.HashesDeStackTrace.Count > 0 &&
            !HashesDeStackTrace.SequenceEqual(outra.HashesDeStackTrace))
            return "Os caminhos de execução divergiram (hashes de stack trace diferentes)";

        return null;
    }

    /// <summary>
    /// Resumo compacto para viajar em toda barreira. A opinião inteira só é
    /// trocada depois que este resumo discordar (decisão 4).
    /// </summary>
    public string Resumo()
    {
        var texto = new StringBuilder();
        texto.Append("fp:").Append((short)ModoDeArredondamento).Append('|');
        texto.Append("passo:").Append(TickInicial).Append('-').Append(TickFinal).Append('|');

        foreach (int mapaId in EstadosPorMapa.Keys.OrderBy(id => id))
            texto.Append("mapa").Append(mapaId).Append(':')
                 .Append(Combinar(EstadosPorMapa[mapaId]).ToString("x8")).Append('|');

        texto.Append("mundo:").Append(Combinar(EstadosDoMundo).ToString("x8")).Append('|');
        texto.Append("cmd:").Append(Combinar(EstadosDeComandos).ToString("x8"));

        return texto.ToString();
    }

    /// <summary>
    /// O que difere entre dois resumos — <c>null</c> se batem.
    ///
    /// <para><b>Por que o resumo deixou de ser um sha256.</b> Ele era: todas as
    /// partes entravam num hash só, e o que chegava do outro lado era "diferente".
    /// Divergiu — mas no mundo, no mapa, nos comandos, ou no modo de
    /// arredondamento? Cada uma dessas aponta para um lugar diferente, e sem
    /// saber qual era eu gastava uma rodada comparando rastreio de pawn à
    /// mão.</para>
    ///
    /// <para>Colapsar também não economizava nada: o composto tem umas 60 letras,
    /// e o sha256 tem 64.</para>
    ///
    /// <para>É a mesma ideia do <c>CheckForDesync</c> do Multiplayer, que
    /// devolve <i>"Wrong random state on map 0"</i> ou <i>"Random state from
    /// commands doesn't match"</i> em vez de um booleano — a diferença é que lá
    /// a comparação acontece com as duas opiniões inteiras em mãos, e aqui com
    /// os dois resumos que já viajam em toda barreira.</para>
    /// </summary>
    public static string? DiferencaEntreResumos(string meu, string doOutro)
    {
        if (meu == doOutro) return null;
        if (meu.Length == 0 || doOutro.Length == 0) return null;

        var minhas = Partes(meu);
        var dele = Partes(doOutro);

        var diferentes = new List<string>();

        foreach (var par in minhas)
        {
            if (!dele.TryGetValue(par.Key, out string? outro))
                diferentes.Add($"{par.Key} só existe de um lado");
            else if (outro != par.Value)
                diferentes.Add($"{par.Key}: {par.Value} != {outro}");
        }

        foreach (var par in dele)
            if (!minhas.ContainsKey(par.Key))
                diferentes.Add($"{par.Key} só existe do outro lado");

        return diferentes.Count == 0
            ? "os resumos diferem mas nenhuma parte foi reconhecida (versões diferentes do mod?)"
            : string.Join("; ", diferentes);
    }

    static Dictionary<string, string> Partes(string resumo)
    {
        var partes = new Dictionary<string, string>();
        foreach (string pedaco in resumo.Split('|'))
        {
            int corte = pedaco.IndexOf(':');
            // `Substring`, não range: o protocolo compila para o netstandard do
            // jogo, onde `System.Index`/`System.Range` não existem.
            if (corte > 0)
                partes[pedaco.Substring(0, corte)] = pedaco.Substring(corte + 1);
        }

        return partes;
    }

    /// <summary>Índice do primeiro elemento diferente, ou -1 se são iguais.</summary>
    static int PrimeiraDiferenca(List<uint> a, List<uint> b)
    {
        int comum = Math.Min(a.Count, b.Count);
        for (int i = 0; i < comum; i++)
            if (a[i] != b[i]) return i;

        return a.Count == b.Count ? -1 : comum;
    }

    static uint Combinar(List<uint> estados)
    {
        unchecked
        {
            uint acumulado = 2166136261u;
            foreach (uint estado in estados)
                acumulado = (acumulado ^ estado) * 16777619u;
            return acumulado;
        }
    }

    public void Write(BinaryWriter w)
    {
        w.Write(TickInicial);
        w.Write(TickFinal);
        w.Write((short)ModoDeArredondamento);
        Escrever(w, EstadosDoMundo);
        Escrever(w, EstadosDeComandos);

        w.Write(EstadosPorMapa.Count);
        foreach (var par in EstadosPorMapa.OrderBy(p => p.Key))
        {
            w.Write(par.Key);
            Escrever(w, par.Value);
        }

        w.Write(HashesDeStackTrace.Count);
        foreach (int hash in HashesDeStackTrace) w.Write(hash);
    }

    public static OpiniaoDeSincronia Read(BinaryReader r)
    {
        var opiniao = new OpiniaoDeSincronia
        {
            TickInicial = r.ReadInt64(),
            TickFinal = r.ReadInt64(),
            ModoDeArredondamento = (ModoDeArredondamentoFP)r.ReadInt16(),
        };

        opiniao.EstadosDoMundo.AddRange(Ler(r));
        opiniao.EstadosDeComandos.AddRange(Ler(r));

        int mapas = r.ReadInt32();
        for (int i = 0; i < mapas; i++)
        {
            int mapaId = r.ReadInt32();
            opiniao.EstadosDoMapa(mapaId).AddRange(Ler(r));
        }

        int hashes = r.ReadInt32();
        for (int i = 0; i < hashes; i++) opiniao.HashesDeStackTrace.Add(r.ReadInt32());

        return opiniao;
    }

    static void Escrever(BinaryWriter w, List<uint> estados)
    {
        w.Write(estados.Count);
        foreach (uint estado in estados) w.Write(estado);
    }

    static IEnumerable<uint> Ler(BinaryReader r)
    {
        int quantidade = r.ReadInt32();
        var estados = new uint[quantidade];
        for (int i = 0; i < quantidade; i++) estados[i] = r.ReadUInt32();
        return estados;
    }
}
