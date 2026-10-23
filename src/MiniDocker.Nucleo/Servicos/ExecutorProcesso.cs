using System.Diagnostics;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos;

internal sealed record ResultadoExecucao(int CodigoSaida, ContabilidadeTrabalho Contabilidade);

/// <summary>
/// Lança o processo principal de um container dentro de um Job Object já
/// criado. Usado pelo start do container e pelos passos <c>RUN</c> do build,
/// que também rodam isolados.
/// </summary>
internal static class ExecutorProcesso
{
    /// <param name="saida">
    /// Recebe cada linha de stdout/stderr. Com <c>null</c>, o processo herda o
    /// console de quem chamou — é o modo interativo (<c>-it</c>), sem log.
    /// </param>
    public static ResultadoExecucao Executar(
        ObjetoTrabalho trabalho,
        string comando,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente,
        Action<string>? saida,
        Action<int>? aoIniciar = null)
    {
        var redirecionar = saida is not null;
        using var processo = CriarProcesso(comando, caminhoRootFs, ambiente, redirecionar);

        if (saida is not null)
        {
            // stdout e stderr chegam em threads diferentes; a trava mantém as
            // linhas inteiras no log.
            var trava = new object();
            DataReceivedEventHandler encaminhar = (_, e) =>
            {
                if (e.Data is { } linha)
                {
                    lock (trava)
                    {
                        saida(linha);
                    }
                }
            };
            processo.OutputDataReceived += encaminhar;
            processo.ErrorDataReceived += encaminhar;
        }

        processo.Start();
        var noGrupo = trabalho.Atribuir(processo);
        aoIniciar?.Invoke(processo.Id);

        if (redirecionar)
        {
            processo.BeginOutputReadLine();
            processo.BeginErrorReadLine();
        }

        AguardarProcessoPrincipal(processo);
        var codigoSaida = processo.ExitCode;

        // Como no Docker, o container acaba quando o processo principal acaba.
        // Netos que ficaram para trás (um "start /b") são encerrados aqui; sem
        // isso eles segurariam o pipe de saída aberto e a leitura não terminaria.
        trabalho.EncerrarRestantes();

        var contabilidade = noGrupo
            ? trabalho.Contabilizar()
            : new ContabilidadeTrabalho(0, 0, 0, 0);

        if (redirecionar)
        {
            // Agora que ninguém mais segura o pipe, espera o fim da leitura
            // para não perder as últimas linhas.
            processo.WaitForExit();
        }

        return new ResultadoExecucao(codigoSaida, contabilidade);
    }

    /// <summary>
    /// Executa um processo adicional no grupo de um container vivo, ligado ao
    /// console de quem chamou. É o <c>docker exec</c>.
    /// </summary>
    public static int ExecutarAnexado(
        ObjetoTrabalho trabalho,
        string comando,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente)
    {
        using var processo = CriarProcesso(comando, caminhoRootFs, ambiente, redirecionar: false);
        processo.Start();
        trabalho.Atribuir(processo);
        processo.WaitForExit();
        return processo.ExitCode;
    }

    /// <summary>
    /// Espera só pelo processo principal. <c>WaitForExit()</c> sem argumentos
    /// também esperaria o fim dos pipes redirecionados, que um neto pode manter
    /// abertos indefinidamente; com timeout finito ele espera só o processo.
    /// </summary>
    private static void AguardarProcessoPrincipal(Process processo)
    {
        while (!processo.WaitForExit(int.MaxValue))
        {
        }
    }

    private static Process CriarProcesso(
        string comando,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente,
        bool redirecionar)
    {
        var informacoes = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = caminhoRootFs,
            UseShellExecute = false,
            RedirectStandardOutput = redirecionar,
            RedirectStandardError = redirecionar,
            // Com a saída redirecionada o container não precisa de console; sem
            // isto, um container lançado pelo shim (que não tem console) faria
            // o Windows abrir uma janela nova para o cmd.exe.
            CreateNoWindow = redirecionar
        };
        informacoes.ArgumentList.Add("/c");
        informacoes.ArgumentList.Add(comando);
        IsolarAmbiente(informacoes, caminhoRootFs, ambiente);
        return new Process { StartInfo = informacoes };
    }

    /// <summary>
    /// Substitui o ambiente herdado do processo pai por um conjunto mínimo. Sem
    /// isto, toda variável do host — inclusive credenciais e caminhos pessoais —
    /// entraria no container. As variáveis de temporário apontam para o rootfs,
    /// então arquivos temporários do container ficam dentro dele. Por cima vêm
    /// as variáveis da imagem (<c>ENV</c>) e do <c>-e</c>.
    /// </summary>
    private static void IsolarAmbiente(
        ProcessStartInfo informacoes,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente)
    {
        var raizWindows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        informacoes.Environment.Clear();
        informacoes.Environment["SystemRoot"] = raizWindows;
        informacoes.Environment["ComSpec"] = Path.Combine(raizWindows, "system32", "cmd.exe");
        informacoes.Environment["Path"] =
            $"{Path.Combine(raizWindows, "system32")};{raizWindows}";
        informacoes.Environment["TEMP"] = caminhoRootFs;
        informacoes.Environment["TMP"] = caminhoRootFs;

        foreach (var (chave, valor) in ambiente)
        {
            informacoes.Environment[chave] = valor;
        }
    }
}
