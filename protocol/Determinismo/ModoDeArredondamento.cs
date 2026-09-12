// Baseado em Source/Common/RoundMode.cs de rwmt/Multiplayer,
// commit 4a3be27, MIT, Copyright (c) 2018 Zetrith.
// Ver THIRD_PARTY/Multiplayer-MIT.txt
//
// Adaptado: nomes traduzidos e documentação própria. A técnica de detecção
// (comparar somas com epsilon) é de https://lemire.me/blog/2022/11/16/

namespace WithFriends.Protocol.Determinismo;

/// <remarks>https://en.cppreference.com/w/cpp/numeric/fenv/FE_round</remarks>
public enum ModoDeArredondamentoFP : short
{
    /// <summary>Para o representável mais próximo — o padrão em toda máquina sã.</summary>
    MaisProximo = 0x0000,
    /// <summary>Para menos infinito.</summary>
    ParaBaixo = 0x0100,
    /// <summary>Para mais infinito.</summary>
    ParaCima = 0x0200,
    /// <summary>Para zero.</summary>
    ParaZero = 0x0300,
}

/// <summary>
/// Modo de arredondamento de ponto flutuante do processo — §14.3 decisão 3,
/// armadilha clássica de determinismo.
///
/// Duas máquinas com modos diferentes divergem em cálculos idênticos, e a
/// divergência aparece longe da causa. Comparar isto **antes** de comparar
/// estado de RNG transforma horas de investigação numa linha de diagnóstico.
///
/// A detecção não usa chamada nativa: uma soma com epsilon revela o modo.
/// </summary>
public static class ModoDeArredondamento
{
    // volatile para o JIT não dobrar a conta em tempo de compilação.
    static volatile float epsilon = 1e-38f;

    public static ModoDeArredondamentoFP Atual()
    {
        float e = epsilon;
        return (-1f + e == -1f, 1f - e == 1f) switch
        {
            (true, true) => ModoDeArredondamentoFP.MaisProximo,
            (false, true) => ModoDeArredondamentoFP.ParaCima,
            (true, false) => ModoDeArredondamentoFP.ParaBaixo,
            (false, false) => ModoDeArredondamentoFP.ParaZero,
        };
    }
}
