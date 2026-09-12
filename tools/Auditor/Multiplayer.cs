using System.Text.RegularExpressions;

namespace WithFriends.Auditor;

/// <summary>
/// O que o Multiplayer (MIT, Zetrith) já remenda, extraído do fonte dele.
///
/// <para>Ele sustenta 602 remendos há anos, num mod que funciona. Cada alvo
/// naquela lista é conhecimento pago com bug de alguém: se ele remenda um
/// método, houve motivo.</para>
///
/// <para>Cruzando com a nossa auditoria de IL, um achado que ele também remenda
/// deixa de ser suspeita e vira prioridade. E um que ele <b>não</b> remenda
/// merece a pergunta contrária — por que não? Pode ser que não importe, pode ser
/// que o desenho dele não passe por ali (tempo assíncrono, sessões persistentes),
/// pode ser que ainda não tenha mordido ninguém.</para>
///
/// <para>Extração por texto, de propósito: o fonte dele é a fonte da verdade, e
/// compilar o mod inteiro para descobrir isso seria desproporcional.</para>
/// </summary>
public static class Multiplayer
{
    static readonly Regex[] Padroes =
    {
        // [HarmonyPatch(typeof(X), nameof(X.Y))]  e  [HarmonyPatch(typeof(X), "Y")]
        new(@"HarmonyPatch\s*\(\s*typeof\(\s*([\w\.]+)\s*\)\s*,\s*(?:nameof\s*\(\s*[\w\.]*?(\w+)\s*\)|""(\w+)"")"),
        // [MpPrefix(typeof(X), nameof(X.Y))] e irmãos
        new(@"Mp(?:Prefix|Postfix|Transpiler)\s*\(\s*typeof\(\s*([\w\.]+)\s*\)\s*,\s*(?:nameof\s*\(\s*[\w\.]*?(\w+)\s*\)|""(\w+)"")"),
        // SyncMethod.Register(typeof(X), nameof(X.Y)) e irmãos
        new(@"Sync\w*\.\w+\s*\(\s*typeof\(\s*([\w\.]+)\s*\)\s*,\s*(?:nameof\s*\(\s*[\w\.]*?(\w+)\s*\)|""(\w+)"")"),
    };

    /// <summary>Só o tipo: <c>[HarmonyPatch(typeof(X))]</c> sem membro.</summary>
    static readonly Regex TipoInteiro = new(@"HarmonyPatch\s*\(\s*typeof\(\s*([\w\.]+)\s*\)\s*\)");

    /// <summary>Chaves <c>TipoCurto.Membro</c>, mais <c>TipoCurto.*</c>.</summary>
    public static HashSet<string> Extrair(string diretorio)
    {
        var alvos = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(diretorio)) return alvos;

        foreach (var arquivo in Directory.EnumerateFiles(diretorio, "*.cs", SearchOption.AllDirectories))
        {
            string texto;
            try { texto = File.ReadAllText(arquivo); }
            catch (IOException) { continue; }

            foreach (var padrao in Padroes)
            foreach (Match m in padrao.Matches(texto))
            {
                string tipo = Curto(m.Groups[1].Value);
                string membro = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (membro.Length > 0) alvos.Add($"{tipo}.{membro}");
            }

            foreach (Match m in TipoInteiro.Matches(texto))
                alvos.Add($"{Curto(m.Groups[1].Value)}.*");
        }

        return alvos;
    }

    public static bool Remenda(HashSet<string> alvos, string metodoCompleto)
    {
        // "Verse.AI.Pawn_JobTracker.TryTakeOrderedJob" → "Pawn_JobTracker", "TryTakeOrderedJob"
        int ponto = metodoCompleto.LastIndexOf('.');
        if (ponto < 0) return false;

        string membro = metodoCompleto.Substring(ponto + 1);
        string tipo = Curto(metodoCompleto.Substring(0, ponto));

        // Lambdas viram Tipo/<>c__DisplayClassN_0.<Metodo>b__0 — o que importa
        // é o tipo de fora.
        int barra = tipo.IndexOf('/');
        if (barra >= 0) tipo = tipo.Substring(0, barra);

        return alvos.Contains($"{tipo}.{membro}") || alvos.Contains($"{tipo}.*");
    }

    static string Curto(string nome)
    {
        int ponto = nome.LastIndexOf('.');
        return ponto < 0 ? nome : nome.Substring(ponto + 1);
    }
}
