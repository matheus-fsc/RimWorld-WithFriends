using System.Security.Cryptography;
using System.Text;

namespace WithFriends.Protocol;

/// <summary>
/// Hash de conteúdo. Um checkpoint é endereçado pelo hash do que ele contém
/// (§7.1 regra 1) — nome de arquivo nunca é identidade (regra 5).
/// </summary>
public static class Hashing
{
    public const string Prefix = "sha256:";

    public static string OfBytes(byte[] content)
    {
        using var sha = SHA256.Create();
        return Format(sha.ComputeHash(content));
    }

    public static string OfString(string text) => OfBytes(Encoding.UTF8.GetBytes(text));

    static string Format(byte[] hash)
    {
        var sb = new StringBuilder(Prefix, Prefix.Length + hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
