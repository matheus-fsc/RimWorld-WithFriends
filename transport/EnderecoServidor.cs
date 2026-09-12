using System;
using System.Net;

namespace WithFriends.Transport;

/// <summary>
/// Endereço de um coordenador. Aceita literal IPv6 entre colchetes, nome de
/// host e IPv4 — §17.3 regra 2.
///
/// **Nunca faz `split(':')`**: `[2804:14c::1]:25555` tem sete dois-pontos e
/// só o último separa a porta. Foi essa a falha do RT #315, onde o endereço
/// IPv6 não funcionava nem digitado nem colado.
/// </summary>
public readonly struct EnderecoServidor
{
    public const int PortaPadrao = 25555;

    /// <summary>Host sem colchetes: nome, literal IPv4 ou literal IPv6.</summary>
    public readonly string Host;
    public readonly int Porta;

    public EnderecoServidor(string host, int porta)
    {
        Host = host;
        Porta = porta;
    }

    public static bool TentarAnalisar(string texto, out EnderecoServidor endereco, out string erro)
    {
        endereco = default;
        erro = "";

        if (string.IsNullOrWhiteSpace(texto))
        {
            erro = "Endereço vazio.";
            return false;
        }

        texto = texto.Trim();

        // [ipv6]:porta ou [ipv6]
        if (texto[0] == '[')
        {
            int fecha = texto.IndexOf(']');
            if (fecha < 0)
            {
                erro = "Falta o ']' no literal IPv6. Exemplo: [2804:14c::1]:25555";
                return false;
            }

            string literal = texto.Substring(1, fecha - 1);
            if (!IPAddress.TryParse(literal, out _))
            {
                erro = $"'{literal}' não é um endereço IPv6 válido.";
                return false;
            }

            string resto = texto.Substring(fecha + 1);
            if (resto.Length == 0) { endereco = new EnderecoServidor(literal, PortaPadrao); return true; }
            if (resto[0] != ':')
            {
                erro = $"Esperava ':porta' depois de ']', veio '{resto}'.";
                return false;
            }
            return TentarComPorta(literal, resto.Substring(1), ref endereco, ref erro);
        }

        // Literal IPv6 sem colchetes: só é aceito se não houver porta junto,
        // porque aí não há ambiguidade.
        if (IPAddress.TryParse(texto, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            endereco = new EnderecoServidor(texto, PortaPadrao);
            return true;
        }

        // host:porta ou ipv4:porta — o último ':' separa.
        int ultimo = texto.LastIndexOf(':');
        if (ultimo < 0)
        {
            endereco = new EnderecoServidor(texto, PortaPadrao);
            return true;
        }

        return TentarComPorta(texto.Substring(0, ultimo), texto.Substring(ultimo + 1), ref endereco, ref erro);
    }

    /// <summary>
    /// Um host todo numérico não é nome válido (RFC 1123) nem IPv4 completo.
    /// Na prática significa uma coisa só: IPv6 digitado sem colchetes, com os
    /// dois-pontos perdidos pelo caminho — teclado ABNT2 e campos de texto do
    /// Unity são uma combinação conhecida por comer ':' e '['.
    /// </summary>
    static bool PareceIPv6Truncado(string host)
    {
        foreach (char c in host)
            if (!char.IsDigit(c)) return false;
        return host.Length > 0;
    }

    static bool TentarComPorta(string host, string porta, ref EnderecoServidor endereco, ref string erro)
    {
        if (!int.TryParse(porta, out int valor) || valor <= 0 || valor > 65535)
        {
            erro = $"'{porta}' não é uma porta válida (1–65535).";
            return false;
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            erro = "Host vazio.";
            return false;
        }
        if (PareceIPv6Truncado(host))
        {
            erro =
                $"'{host}' não é um host válido. Isso costuma ser um endereço IPv6 " +
                "digitado sem colchetes — escreva assim: [::1]:25555";
            return false;
        }
        endereco = new EnderecoServidor(host, valor);
        return true;
    }

    public static EnderecoServidor Analisar(string texto) =>
        TentarAnalisar(texto, out var endereco, out string erro)
            ? endereco
            : throw new FormatException(erro);

    /// <summary>Volta ao formato canônico, com colchetes quando é IPv6.</summary>
    public override string ToString() =>
        IPAddress.TryParse(Host, out var ip) &&
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{Host}]:{Porta}"
            : $"{Host}:{Porta}";
}
