namespace WithFriends.Server.Colonias;

/// <summary>
/// Escadinha de retenção — §7.2. Janela fixa não serve: 6 backups de hora em
/// hora dão 6 horas de histórico, e numa investigação real isso quase expirou
/// antes de a causa ser encontrada (§15.7).
///
/// ```
/// últimas 24h  → a cada hora
/// últimos 7d   → a cada dia
/// sempre       → os 3 checkpoints marcados como pré-sessão
/// ```
/// </summary>
public static class Retencao
{
    public const int PreSessoesSempreMantidas = 3;

    /// <summary>
    /// Decide o que fica. Função pura: recebe o histórico, devolve o
    /// subconjunto a manter. Quem apaga é outro (e só apaga o que sobrar).
    /// </summary>
    public static IReadOnlyList<EntradaCheckpoint> Manter(
        IEnumerable<EntradaCheckpoint> historico, DateTime agora)
    {
        var todos = historico.OrderByDescending(e => e.RecebidoEm).ToList();
        var manter = new HashSet<EntradaCheckpoint>();

        // Os 3 pré-sessão mais recentes ficam para sempre: são os pontos de
        // rollback de aborto de sessão (§2.3).
        foreach (var entrada in todos.Where(e => e.PreSessao).Take(PreSessoesSempreMantidas))
            manter.Add(entrada);

        // Um por hora nas últimas 24h.
        Adicionar(manter, todos, agora, TimeSpan.FromHours(24),
            e => new DateTime(e.RecebidoEm.Year, e.RecebidoEm.Month, e.RecebidoEm.Day, e.RecebidoEm.Hour, 0, 0, e.RecebidoEm.Kind));

        // Um por dia nos últimos 7 dias.
        Adicionar(manter, todos, agora, TimeSpan.FromDays(7),
            e => e.RecebidoEm.Date);

        // Um checkpoint suspeito é evidência de investigação: nunca é o
        // primeiro a ser descartado dentro da janela de 7 dias.
        foreach (var entrada in todos.Where(e => e.Suspeito && agora - e.RecebidoEm <= TimeSpan.FromDays(7)))
            manter.Add(entrada);

        // O mais recente sempre fica, mesmo fora de toda janela.
        if (todos.Count > 0) manter.Add(todos[0]);

        return todos.Where(manter.Contains).ToArray();
    }

    public static IReadOnlyList<EntradaCheckpoint> Descartar(
        IEnumerable<EntradaCheckpoint> historico, DateTime agora)
    {
        var todos = historico.ToList();
        var manter = new HashSet<EntradaCheckpoint>(Manter(todos, agora));
        return todos.Where(e => !manter.Contains(e)).ToArray();
    }

    static void Adicionar(
        HashSet<EntradaCheckpoint> manter,
        List<EntradaCheckpoint> todos,
        DateTime agora,
        TimeSpan janela,
        Func<EntradaCheckpoint, DateTime> balde)
    {
        var vistos = new HashSet<DateTime>();
        foreach (var entrada in todos.Where(e => agora - e.RecebidoEm <= janela))
            if (vistos.Add(balde(entrada)))
                manter.Add(entrada); // o mais recente do balde, pela ordenação
    }
}
