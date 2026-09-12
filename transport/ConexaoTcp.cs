using System;
using System.IO;
using System.Net.Sockets;
using WithFriends.Protocol;

namespace WithFriends.Transport;

/// <summary>
/// Conexão TCP com enquadramento do <see cref="Envelope"/>.
/// A leitura acumula num buffer e só entrega mensagem completa — assim o
/// chamador pode chamar <see cref="TentarReceber"/> de dentro do laço do
/// jogo sem nunca bloquear.
/// </summary>
public sealed class ConexaoTcp : IConexao
{
    readonly TcpClient cliente;
    readonly NetworkStream stream;
    readonly MemoryStream buffer = new();
    readonly byte[] leitura = new byte[64 * 1024];

    // TcpClient.Connected mente: só fica falso depois de uma operação de I/O
    // falhar. Sem este flag, um socket morto continua "conectado" e tudo o que
    // for enviado some em silêncio — que é a falha que este projeto existe
    // para não repetir.
    bool viva = true;

    public ConexaoTcp(TcpClient cliente, EnderecoServidor endereco)
    {
        this.cliente = cliente;
        stream = cliente.GetStream();
        Descricao = cliente.Client.RemoteEndPoint?.ToString() ?? endereco.ToString();
    }

    public bool Conectado
    {
        get
        {
            if (!viva || !cliente.Connected) return false;

            try
            {
                // Poll legível com zero bytes disponíveis é FIN do outro lado:
                // fim de fluxo. Sem esta checagem só descobriríamos na segunda
                // escrita, porque a primeira costuma caber no buffer do TCP e
                // "ter sucesso" num socket que já morreu.
                var socket = cliente.Client;
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    viva = false;
                    return false;
                }
            }
            catch (Exception)
            {
                viva = false;
                return false;
            }

            return true;
        }
    }

    public string Descricao { get; }

    public void Enviar(IMessage mensagem)
    {
        if (!Conectado)
            throw new IOException("A conexão com o coordenador não está mais viva.");

        try
        {
            mensagem.ToEnvelope().WriteTo(stream);
        }
        catch (Exception)
        {
            viva = false;
            throw;
        }
    }

    public bool TentarReceber(out Envelope envelope)
    {
        envelope = default;

        while (viva && stream.DataAvailable)
        {
            int lidos;
            try
            {
                lidos = stream.Read(leitura, 0, leitura.Length);
            }
            catch (IOException)
            {
                viva = false;
                throw;
            }

            // Leitura de zero bytes com dados "disponíveis" é o outro lado
            // tendo fechado: fim de fluxo, não pausa.
            if (lidos == 0)
            {
                viva = false;
                break;
            }

            long posicao = buffer.Position;
            buffer.Position = buffer.Length;
            buffer.Write(leitura, 0, lidos);
            buffer.Position = posicao;
        }

        long disponivel = buffer.Length - buffer.Position;
        if (disponivel < Envelope.HeaderBytes) return false;

        // Espia o tamanho sem consumir: mensagem parcial fica para a próxima.
        long inicio = buffer.Position;
        var cabecalho = new byte[Envelope.HeaderBytes];
        buffer.Read(cabecalho, 0, Envelope.HeaderBytes);
        int tamanho = BitConverter.ToInt32(cabecalho, 3);   // [id:2][flags:1][tamanho:4]
        buffer.Position = inicio;

        if (tamanho < 0 || tamanho > Envelope.MaxPayloadBytes)
            throw new InvalidDataException($"Tamanho de payload inválido: {tamanho}");

        if (disponivel < Envelope.HeaderBytes + tamanho) return false;

        envelope = Envelope.ReadFrom(buffer);
        CompactarSeVazio();
        return true;
    }

    void CompactarSeVazio()
    {
        if (buffer.Position != buffer.Length) return;
        buffer.SetLength(0);
        buffer.Position = 0;
    }

    public void Dispose()
    {
        viva = false;
        try { cliente.Close(); } catch { /* fechar é best-effort */ }
        buffer.Dispose();
    }
}
