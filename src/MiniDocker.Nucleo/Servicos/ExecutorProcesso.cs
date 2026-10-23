using System.Diagnostics;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos;

internal sealed record ResultadoExecucao(int CodigoSaida, ContabilidadeTrabalho? Contabilidade);

/// <summary>
/// Lança o processo principal de um container dentro de um grupo de processos
/// já criado. Usado pelo start do container e pelos passos <c>RUN</c> do build,
/// que também rodam isolados. Como o processo é montado depende da plataforma
/// (ver <see cref="Runtimes.IRuntime"/>); o ciclo é sempre o mesmo.
/// </summary>
internal static class ExecutorProcesso
{
    /// <param name="saida">
    /// Recebe cada linha de stdout/stderr. Com <c>null</c>, o processo herda o
    /// console de quem chamou — é o modo interativo (<c>-it</c>), sem log.
    /// </param>
    public static ResultadoExecucao Executar(
        IGrupoProcessos grupo,
        ProcessStartInfo informacoes,
        Action<string>? saida,
        Action<int>? aoIniciar = null)
    {
        var redirecionar = saida is not null;
        informacoes.UseShellExecute = false;
        informacoes.RedirectStandardOutput = redirecionar;
        informacoes.RedirectStandardError = redirecionar;
        // Com a saída redirecionada o container não precisa de console; sem
        // isto, um container lançado pelo shim (que não tem console) faria
        // o Windows abrir uma janela nova para o processo.
        informacoes.CreateNoWindow = redirecionar;
        if (!redirecionar)
        {
            SemCodificacao(informacoes);
        }

        using var processo = new Process { StartInfo = informacoes };

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
        var noGrupo = grupo.Atribuir(processo);
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
        grupo.EncerrarRestantes();

        var contabilidade = noGrupo ? grupo.Contabilizar() : null;

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
    public static int ExecutarAnexado(IGrupoProcessos grupo, ProcessStartInfo informacoes)
    {
        informacoes.UseShellExecute = false;
        SemCodificacao(informacoes);
        using var processo = Process.Start(informacoes)!;
        grupo.Atribuir(processo);
        processo.WaitForExit();
        return processo.ExitCode;
    }

    /// <summary>
    /// Espera só pelo processo principal. <c>WaitForExit()</c> sem argumentos
    /// também esperaria o fim dos pipes redirecionados, que um neto pode manter
    /// abertos indefinidamente; com timeout finito ele espera só o processo.
    /// </summary>
    /// <summary>
    /// Quem herda o console escreve direto nele; o .NET recusa codificação
    /// definida para uma saída que não foi redirecionada.
    /// </summary>
    private static void SemCodificacao(ProcessStartInfo informacoes)
    {
        informacoes.StandardOutputEncoding = null;
        informacoes.StandardErrorEncoding = null;
    }

    private static void AguardarProcessoPrincipal(Process processo)
    {
        while (!processo.WaitForExit(int.MaxValue))
        {
        }
    }
}
