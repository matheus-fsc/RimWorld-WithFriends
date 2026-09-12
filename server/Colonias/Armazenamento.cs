using System.Text;
using WithFriends.Protocol;

namespace WithFriends.Server.Colonias;

/// <summary>
/// Armazenamento append-only endereçado por hash — §7.1 regra 1.
/// Não existe operação de sobrescrita nesta interface, de propósito.
/// </summary>
public interface IArmazenamento
{
    bool ConteudoExiste(string contentHash);
    void GravarConteudo(string contentHash, byte[] conteudo);
    byte[] LerConteudo(string contentHash);

    /// <summary>Anexa uma linha ao índice da colônia. Nunca reescreve o índice.</summary>
    void AnexarAoIndice(ColonyIdentity identity, string linha);
    IReadOnlyList<string> LerIndice(ColonyIdentity identity);
    void RemoverConteudo(string contentHash);
}

public sealed class ArmazenamentoEmMemoria : IArmazenamento
{
    readonly Dictionary<string, byte[]> conteudos = new();
    readonly Dictionary<ColonyIdentity, List<string>> indices = new();

    public bool ConteudoExiste(string contentHash) => conteudos.ContainsKey(contentHash);

    public void GravarConteudo(string contentHash, byte[] conteudo)
    {
        // Idempotente por construção: mesmo hash, mesmo conteúdo.
        if (!conteudos.ContainsKey(contentHash)) conteudos[contentHash] = conteudo;
    }

    public byte[] LerConteudo(string contentHash) => conteudos[contentHash];

    public void AnexarAoIndice(ColonyIdentity identity, string linha)
    {
        if (!indices.TryGetValue(identity, out var lista))
            indices[identity] = lista = new List<string>();
        lista.Add(linha);
    }

    public IReadOnlyList<string> LerIndice(ColonyIdentity identity) =>
        indices.TryGetValue(identity, out var lista) ? lista : Array.Empty<string>();

    public void RemoverConteudo(string contentHash) => conteudos.Remove(contentHash);
}

/// <summary>
/// Em disco. O caminho é derivado de <c>(player_id, colony_id)</c> e do hash —
/// nunca do nome que o save tem na máquina do jogador (§7.1 regra 5, §15.1).
/// </summary>
public sealed class ArmazenamentoEmDisco : IArmazenamento
{
    // Sem BOM: o índice é jsonl, lido linha a linha por qualquer ferramenta.
    static readonly UTF8Encoding Utf8SemBom = new(encoderShouldEmitUTF8Identifier: false);

    readonly string raiz;

    public ArmazenamentoEmDisco(string raiz)
    {
        this.raiz = raiz;
        Directory.CreateDirectory(Path.Combine(raiz, "conteudo"));
        Directory.CreateDirectory(Path.Combine(raiz, "colonias"));
    }

    string CaminhoConteudo(string contentHash)
    {
        string limpo = contentHash.StartsWith(Hashing.Prefix)
            ? contentHash.Substring(Hashing.Prefix.Length)
            : contentHash;
        // Dois níveis para não criar um diretório com milhares de entradas.
        string prefixo = limpo.Substring(0, 2);
        Directory.CreateDirectory(Path.Combine(raiz, "conteudo", prefixo));
        return Path.Combine(raiz, "conteudo", prefixo, limpo + ".bin");
    }

    string CaminhoIndice(ColonyIdentity identity)
    {
        string dir = Path.Combine(raiz, "colonias", Sanitizar(identity.PlayerId), Sanitizar(identity.ColonyId));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "index.jsonl");
    }

    static string Sanitizar(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    public bool ConteudoExiste(string contentHash) => File.Exists(CaminhoConteudo(contentHash));

    public void GravarConteudo(string contentHash, byte[] conteudo)
    {
        string caminho = CaminhoConteudo(contentHash);
        if (File.Exists(caminho)) return; // mesmo hash, mesmo conteúdo
        // Grava em temporário e move: um checkpoint parcial nunca vira um
        // checkpoint válido.
        string temporario = caminho + ".parcial";
        File.WriteAllBytes(temporario, conteudo);
        File.Move(temporario, caminho);
    }

    public byte[] LerConteudo(string contentHash) => File.ReadAllBytes(CaminhoConteudo(contentHash));

    public void AnexarAoIndice(ColonyIdentity identity, string linha) =>
        File.AppendAllText(CaminhoIndice(identity), linha + "\n", Utf8SemBom);

    public IReadOnlyList<string> LerIndice(ColonyIdentity identity)
    {
        string caminho = CaminhoIndice(identity);
        return File.Exists(caminho)
            ? File.ReadAllLines(caminho).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();
    }

    public void RemoverConteudo(string contentHash)
    {
        string caminho = CaminhoConteudo(contentHash);
        if (File.Exists(caminho)) File.Delete(caminho);
    }
}
