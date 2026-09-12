using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace WithFriends.Client.Auditoria;

/// <summary>Um método do jogo que toca uma fonte local.</summary>
public readonly struct Vazamento
{
    public string Metodo { get; init; }
    public FonteLocal Fonte { get; init; }
    public string MembroTocado { get; init; }

    /// <summary>
    /// Heurística de "isto parece interface".
    ///
    /// Não serve para decidir nada — serve para ordenar o relatório. Interface
    /// **pode** ler câmera e teclado; é o trabalho dela. O que não pode é
    /// simulação.
    /// </summary>
    public bool ParecemInterface { get; init; }
}

/// <summary>
/// Varre o IL do jogo procurando quem toca fonte local — ADR 0015.
///
/// <para>Inverte a pergunta. Hoje perguntamos "qual método divergiu?", e isso só
/// se responde reproduzindo a falha. Aqui perguntamos <b>"quais métodos podem
/// divergir?"</b>, que se responde sem jogar — e se responde de novo, sozinho, a
/// cada atualização do jogo.</para>
///
/// <para><b>Por que não roda na subida.</b> São dezenas de milhares de métodos e
/// ler IL custa; a subida do jogo não é lugar para isso. Roda por debug action,
/// escreve relatório em arquivo, e guarda cache com o hash do assembly — se o
/// jogo não mudou, o relatório de ontem ainda vale.</para>
///
/// <para><c>HarmonyLib.PatchProcessor.ReadMethodBody</c> devolve o IL de
/// qualquer método sem precisar de Mono.Cecil.</para>
/// </summary>
public static class AuditorDeIL
{
    public static string CaminhoDoRelatorio =>
        Path.Combine(GenFilePaths.SaveDataFolderPath, "WithFriends-auditoria.txt");

    public static List<Vazamento> Auditar(Assembly assembly, out int metodosLidos)
    {
        var vazamentos = new List<Vazamento>();
        metodosLidos = 0;

        foreach (var tipo in Tipos(assembly))
        foreach (var metodo in Metodos(tipo))
        {
            metodosLidos++;
            try
            {
                foreach (var instrucao in PatchProcessor.ReadMethodBody(metodo))
                {
                    if (instrucao.Value is not MethodBase chamado) continue;

                    var fonte = FontesLocais.Casar(chamado.DeclaringType?.FullName, chamado.Name);
                    if (fonte == null) continue;

                    vazamentos.Add(new Vazamento
                    {
                        Metodo = $"{tipo.FullName}.{metodo.Name}",
                        Fonte = fonte,
                        MembroTocado = $"{chamado.DeclaringType?.Name}.{chamado.Name}",
                        ParecemInterface = FontesLocais.Balde(tipo.FullName, metodo.Name) == "interface",
                    });
                }
            }
            catch (Exception)
            {
                // Método sem corpo, genérico aberto, extern. Não é achado nem erro.
            }
        }

        return vazamentos;
    }

    static IEnumerable<Type> Tipos(Assembly assembly)
    {
        Type[] tipos;
        try { tipos = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { tipos = e.Types.Where(t => t != null).ToArray()!; }
        return tipos;
    }

    static IEnumerable<MethodBase> Metodos(Type tipo)
    {
        const BindingFlags Tudo = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Instance | BindingFlags.Static
                                  | BindingFlags.DeclaredOnly;

        IEnumerable<MethodBase> metodos;
        try
        {
            metodos = tipo.GetMethods(Tudo).Cast<MethodBase>()
                .Concat(tipo.GetConstructors(Tudo));
        }
        catch (Exception) { yield break; }

        foreach (var metodo in metodos)
            if (!metodo.IsAbstract && !metodo.ContainsGenericParameters)
                yield return metodo;
    }

    /// <summary>
    /// Todo assembly que pode conter simulação: o jogo e **os mods carregados**.
    ///
    /// <para>Auditar só o `Assembly-CSharp` responde pela metade. Um mod que
    /// sorteia dentro do tick, ou que lê a câmera para decidir algo, quebra a
    /// visita exatamente como o jogo quebraria — e ninguém tem como saber
    /// olhando o código dele, porque ninguém lê o código de todos os mods que
    /// usa.</para>
    ///
    /// <para>Isto não impede o problema; faz dele uma linha no relatório em vez
    /// de um desync sem nome.</para>
    /// </summary>
    static IEnumerable<(string nome, Assembly assembly)> AssembliesParaAuditar()
    {
        yield return ("RimWorld", typeof(Pawn).Assembly);

        foreach (var mod in LoadedModManager.RunningModsListForReading)
        foreach (var assembly in mod.assemblies?.loadedAssemblies ?? new List<Assembly>())
        {
            // O nosso próprio código não interessa: ele existe para tocar nas
            // fontes locais, e fora do tick isso é o trabalho dele.
            if (assembly == typeof(AuditorDeIL).Assembly) continue;

            yield return (mod.Name, assembly);
        }
    }

    /// <summary>Roda a auditoria e escreve o relatório. Devolve o caminho.</summary>
    public static string Rodar()
    {
        var relogio = Stopwatch.StartNew();
        var assembly = typeof(Pawn).Assembly;
        var vazamentos = new List<Vazamento>();
        int metodosLidos = 0;

        foreach (var (nome, alvo) in AssembliesParaAuditar())
        {
            var doAlvo = Auditar(alvo, out int lidos);
            metodosLidos += lidos;

            foreach (var vazamento in doAlvo)
                vazamentos.Add(nome == "RimWorld"
                    ? vazamento
                    : new Vazamento
                    {
                        Metodo = $"[{nome}] {vazamento.Metodo}",
                        Fonte = vazamento.Fonte,
                        MembroTocado = vazamento.MembroTocado,
                        ParecemInterface = vazamento.ParecemInterface,
                    });
        }

        relogio.Stop();

        string texto = Relatorio(assembly, vazamentos, metodosLidos, relogio.Elapsed);
        File.WriteAllText(CaminhoDoRelatorio, texto);

        Log.Message(
            $"[WithFriends] auditoria de IL: {metodosLidos} método(s) lidos em " +
            $"{relogio.Elapsed.TotalSeconds:F1}s, {vazamentos.Count} toque(s) em fonte local.\n" +
            $"  relatório: {CaminhoDoRelatorio}");

        return CaminhoDoRelatorio;
    }

    static string Relatorio(
        Assembly assembly, List<Vazamento> vazamentos, int metodosLidos, TimeSpan duracao)
    {
        var texto = new StringBuilder();

        texto.AppendLine("WithFriends — auditoria de fontes locais (ADR 0015)");
        texto.AppendLine();
        texto.AppendLine($"assembly       {assembly.GetName().Name} {assembly.GetName().Version}");
        texto.AppendLine($"métodos lidos  {metodosLidos}");
        texto.AppendLine($"toques         {vazamentos.Count}");
        texto.AppendLine($"duração        {duracao.TotalSeconds:F1}s");
        texto.AppendLine();
        texto.AppendLine("A pergunta que este arquivo responde não é \"o que divergiu\" — é");
        texto.AppendLine("\"o que PODE divergir\". Interface pode ler câmera e teclado; é o");
        texto.AppendLine("trabalho dela. Simulação não pode. A separação abaixo é heurística");
        texto.AppendLine("por nome, então serve para ordenar a leitura, não para concluir.");
        texto.AppendLine();

        foreach (var grupo in vazamentos
                     .GroupBy(v => v.Fonte)
                     .OrderByDescending(g => g.Count(v => !v.ParecemInterface)))
        {
            var fonte = grupo.Key;
            var simulacao = grupo.Where(v => !v.ParecemInterface).ToList();
            var interfaceGrafica = grupo.Count() - simulacao.Count;

            texto.AppendLine(new string('=', 78));
            texto.AppendLine($"FONTE  {fonte}");
            texto.AppendLine($"       {fonte.Motivo}");
            texto.AppendLine(fonte.Estado switch
            {
                Tratamento.Fonte => "       FONTE NEUTRALIZADA no tick — cobre todo chamador",
                Tratamento.Chamadores => "       chamadores tratados um a um; reveja a lista a cada versão",
                _ => "       *** ainda não tratada ***",
            });
            texto.AppendLine($"       {simulacao.Count} em código que não parece interface, " +
                             $"{interfaceGrafica} em código que parece");
            texto.AppendLine();

            foreach (var v in simulacao.OrderBy(v => v.Metodo, StringComparer.Ordinal).Take(400))
                texto.AppendLine($"    {v.Metodo}   →  {v.MembroTocado}");

            if (simulacao.Count > 400)
                texto.AppendLine($"    … e mais {simulacao.Count - 400}");

            texto.AppendLine();
        }

        return texto.ToString();
    }
}
