using WithFriends.Protocol.Messages;

namespace WithFriends.Server.Colonias;

/// <summary>
/// Para onde vão os alertas. "Em voz alta" (§7.1 regra 3) significa que
/// existe um destino explícito — nunca um <c>catch</c> vazio.
/// </summary>
public interface ISinkAlertas
{
    void Emitir(ColoniaAlerta alerta);
}

public sealed class AlertasConsole : ISinkAlertas
{
    public void Emitir(ColoniaAlerta alerta) =>
        Console.WriteLine($"!! ALERTA [{alerta.PlayerId}/{alerta.ColonyId}] {alerta.Tipo}: {alerta.Explicacao}");
}

/// <summary>Guarda os alertas para inspeção — usado nos testes e pelo painel.</summary>
public sealed class AlertasEmMemoria : ISinkAlertas
{
    readonly List<ColoniaAlerta> alertas = new();

    public IReadOnlyList<ColoniaAlerta> Todos => alertas;

    public void Emitir(ColoniaAlerta alerta) => alertas.Add(alerta);
}

public sealed class AlertasCompostos : ISinkAlertas
{
    readonly ISinkAlertas[] destinos;

    public AlertasCompostos(params ISinkAlertas[] destinos) => this.destinos = destinos;

    public void Emitir(ColoniaAlerta alerta)
    {
        foreach (var destino in destinos) destino.Emitir(alerta);
    }
}
