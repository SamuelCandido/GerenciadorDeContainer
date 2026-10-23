using System.Diagnostics;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos.Runtimes;

/// <summary>
/// Imagem Windows: o container é o <c>cmd.exe</c> rodando no rootfs, dentro de
/// um Job Object, com um ambiente mínimo em vez do ambiente do host.
/// </summary>
internal sealed class RuntimeWindows : IRuntime
{
    public bool AplicaLimites => true;

    public void PrepararRootFs(Imagem imagem, Conteiner conteiner)
    {
        if (imagem.EhScratch)
        {
            Directory.CreateDirectory(conteiner.CaminhoRootFs);
            return;
        }

        // Cópia, não referência: o container altera o próprio rootfs sem
        // tocar na imagem nem nos outros containers criados dela.
        Workspace.CopiarDiretorio(imagem.CaminhoRootFs, conteiner.CaminhoRootFs);
    }

    public ProcessStartInfo CriarProcesso(Conteiner conteiner, string comando) =>
        Processo(conteiner.CaminhoRootFs, conteiner.Ambiente, comando);

    public ProcessStartInfo CriarProcessoExec(Conteiner conteiner, string comando, IGrupoProcessos grupo) =>
        Processo(conteiner.CaminhoRootFs, conteiner.Ambiente, comando);

    public void Remover(Conteiner conteiner) => Workspace.ApagarDiretorio(conteiner.CaminhoRootFs);

    /// <summary>Também usado pelo <c>RUN</c> do build.</summary>
    public static ProcessStartInfo Processo(
        string caminhoRootFs, IReadOnlyDictionary<string, string> ambiente, string comando)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "Imagens Windows so executam num host Windows. No Linux, use uma imagem Linux: minidocker pull alpine");
        }

        var informacoes = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = caminhoRootFs
        };
        informacoes.ArgumentList.Add("/c");
        informacoes.ArgumentList.Add(comando);
        IsolarAmbiente(informacoes, caminhoRootFs, ambiente);
        return informacoes;
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
