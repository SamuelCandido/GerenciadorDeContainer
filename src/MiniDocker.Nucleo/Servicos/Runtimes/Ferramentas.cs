using System.Diagnostics;

namespace MiniDocker.Nucleo.Servicos.Runtimes;

/// <summary>Executa ferramentas do sistema (tar, wsl.exe) e espera terminar.</summary>
internal static class Ferramentas
{
    public static void Executar(string arquivo, params string[] argumentos)
    {
        var (codigo, erro) = Rodar(arquivo, argumentos);
        if (codigo != 0)
        {
            throw new InvalidOperationException($"'{arquivo} {argumentos[0]}' falhou: {erro.Trim()}");
        }
    }

    /// <summary>Como <see cref="Executar"/>, mas ignora falhas (limpeza).</summary>
    public static void Tentar(string arquivo, params string[] argumentos)
    {
        try
        {
            Rodar(arquivo, argumentos);
        }
        catch (Exception excecao) when (excecao is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static (int Codigo, string Erro) Rodar(string arquivo, string[] argumentos)
    {
        var informacoes = new ProcessStartInfo
        {
            FileName = arquivo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Sem isto o wsl.exe escreve as próprias mensagens em UTF-16.
        informacoes.Environment["WSL_UTF8"] = "1";
        foreach (var argumento in argumentos)
        {
            informacoes.ArgumentList.Add(argumento);
        }

        using var processo = Process.Start(informacoes)!;
        var saida = processo.StandardOutput.ReadToEndAsync();
        var erro = processo.StandardError.ReadToEnd();
        processo.WaitForExit();
        return (processo.ExitCode, erro.Length > 0 ? erro : saida.Result);
    }
}
