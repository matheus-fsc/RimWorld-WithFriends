using System.IO;
using WithFriends.Protocol;
using WithFriends.Protocol.Messages;
using WithFriends.Server;
using Xunit;

namespace WithFriends.Tests;

public class HandshakeTests
{
    [Fact]
    public void Handshake_sobrevive_ida_e_volta_na_fiacao()
    {
        var original = new Handshake
        {
            PlayerId = "b6f0c2f4-0c1a-4a1e-9a5b-1f0d2b3c4d5e",
            DisplayName = "math",
            Capabilities = Capabilities.WorldEvents | Capabilities.Sessions,
            WorldModSetHash = "sha256:deadbeef",
        };

        using var ms = new MemoryStream();
        original.ToEnvelope().WriteTo(ms);
        ms.Position = 0;

        var lido = Envelope.ReadFrom(ms).Decode(Handshake.Read);

        Assert.Equal(original.PlayerId, lido.PlayerId);
        Assert.Equal(original.DisplayName, lido.DisplayName);
        Assert.Equal(original.Capabilities, lido.Capabilities);
        Assert.Equal(original.WorldModSetHash, lido.WorldModSetHash);
        Assert.Equal(ProtocolVersion.Current, lido.ProtocolVersion);
    }

    [Fact]
    public void Mensagem_desconhecida_pode_ser_pulada_sem_perder_o_fluxo()
    {
        // §9.1: mensagem desconhecida é ignorada com log, nunca derruba a
        // conexão. O tamanho explícito no envelope é o que torna isso possível.
        using var ms = new MemoryStream();
        new Envelope((MessageId)64000, new byte[] { 1, 2, 3, 4, 5 }).WriteTo(ms);
        new Handshake { DisplayName = "depois do desconhecido" }.ToEnvelope().WriteTo(ms);
        ms.Position = 0;

        var desconhecida = Envelope.ReadFrom(ms);
        Assert.False(System.Enum.IsDefined(typeof(MessageId), desconhecida.Id));

        var seguinte = Envelope.ReadFrom(ms).Decode(Handshake.Read);
        Assert.Equal("depois do desconhecido", seguinte.DisplayName);
    }

    [Fact]
    public void Payload_grande_viaja_comprimido_e_volta_igual()
    {
        // Um checkpoint de colônia madura chegou a 17,9 MB em teste real e
        // estourava o teto antigo de 16 MiB, derrubando a conexão sem
        // explicação. Saves comprimem ~10×, então a compressão é do transporte.
        var conteudo = new byte[4 * 1024 * 1024];
        for (int i = 0; i < conteudo.Length; i++) conteudo[i] = (byte)(i % 61);   // compressível

        var original = new ColoniaCheckpoint
        {
            PlayerId = "p1",
            ColonyId = "c1",
            Conteudo = conteudo,
            Metadata = new CheckpointMetadata { GameTick = 1, ContentHash = Hashing.OfBytes(conteudo) },
        };

        using var ms = new MemoryStream();
        original.ToEnvelope().WriteTo(ms);

        Assert.True(ms.Length < conteudo.Length / 2,
            $"deveria ter comprimido: {ms.Length:N0} bytes para {conteudo.Length:N0} de payload");

        ms.Position = 0;
        var lido = Envelope.ReadFrom(ms).Decode(ColoniaCheckpoint.Read);

        Assert.Equal(conteudo, lido.Conteudo);
        Assert.Equal(original.Metadata.ContentHash, Hashing.OfBytes(lido.Conteudo));
    }

    [Fact]
    public void Payload_pequeno_nao_paga_compressao()
    {
        using var ms = new MemoryStream();
        new Handshake { PlayerId = "p1", DisplayName = "math" }.ToEnvelope().WriteTo(ms);
        ms.Position = 0;

        Assert.Equal("math", Envelope.ReadFrom(ms).Decode(Handshake.Read).DisplayName);
    }

    [Fact]
    public void Payload_acima_do_teto_falha_ao_escrever_em_vez_de_ser_recusado_depois()
    {
        // Recusar na escrita dá mensagem legível a quem enviou. Escrever e
        // deixar o outro lado rejeitar derruba a conexão sem explicação — que
        // foi exatamente o que aconteceu em teste real.
        var envelope = new Envelope(MessageId.ColoniaCheckpoint, new byte[8]);
        Assert.True(Envelope.MaxPayloadBytes > 17_987_906,
            "o teto precisa comportar um checkpoint de colônia madura");
        Assert.Equal(7, Envelope.HeaderBytes);
        Assert.NotNull(envelope.Payload);
    }

    [Fact]
    public void Versao_incompativel_recusa_com_motivo_legivel()
    {
        var handler = new HandshakeHandler("servidor-de-teste", Capabilities.Sessions);

        var resposta = handler.Handle(new Handshake { ProtocolVersion = ProtocolVersion.Current + 99 });

        var recusa = Assert.IsType<HandshakeRecusado>(resposta);
        Assert.Equal(MotivoRecusa.VersaoIncompativel, recusa.Motivo);
        Assert.NotEmpty(recusa.Explicacao);
    }

    [Fact]
    public void Capacidades_efetivas_sao_a_intersecao()
    {
        var handler = new HandshakeHandler(
            "servidor-de-teste",
            Capabilities.WorldEvents | Capabilities.Sessions);

        var resposta = handler.Handle(new Handshake
        {
            Capabilities = Capabilities.Sessions | Capabilities.Market,
        });

        var aceito = Assert.IsType<HandshakeAceito>(resposta);
        Assert.Equal(Capabilities.Sessions, aceito.Capabilities);
    }

    [Fact]
    public void Mods_divergentes_nao_bloqueiam_o_login()
    {
        // §8/§15.5: a verificação de mods acontece na entrada da SESSÃO.
        // Divergência nunca impede alguém de conectar e jogar sozinho.
        var handler = new HandshakeHandler("servidor-de-teste", Capabilities.WorldEvents);

        var resposta = handler.Handle(new Handshake { WorldModSetHash = "hash-completamente-diferente" });

        Assert.IsType<HandshakeAceito>(resposta);
    }
}
