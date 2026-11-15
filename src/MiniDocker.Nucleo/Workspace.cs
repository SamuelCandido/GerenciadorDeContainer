using System.Diagnostics;

namespace MiniDocker.Nucleo;

/// <summary>Onde o MiniDocker guarda containers, imagens, volumes e logs.</summary>
internal static class Workspace
{
    /// <summary>
    /// Percorre a árvore sem entrar em pontos de reanálise (junctions e links).
    /// Um volume montado no rootfs é uma junction para uma pasta de fora; seguir
    /// essa junction faria um <c>commit</c> copiar os dados do volume para a
    /// imagem e um <c>rm</c> apagar os dados do host.
    /// </summary>
    private static readonly EnumerationOptions SemSeguirLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static string Raiz(string? raiz) => raiz ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minidocker");

    public static void CopiarDiretorio(string origem, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (var diretorio in Directory.EnumerateDirectories(origem, "*", SemSeguirLinks))
        {
            Directory.CreateDirectory(Path.Combine(destino, Path.GetRelativePath(origem, diretorio)));
        }

        foreach (var arquivo in Directory.EnumerateFiles(origem, "*", SemSeguirLinks))
        {
            File.Copy(arquivo, Path.Combine(destino, Path.GetRelativePath(origem, arquivo)), overwrite: true);
        }
    }

    public static long TamanhoDiretorio(string diretorio) =>
        Directory.Exists(diretorio)
            ? Directory.EnumerateFiles(diretorio, "*", SemSeguirLinks).Sum(arquivo => new FileInfo(arquivo).Length)
            : 0;

    public static bool DiretorioVazio(string diretorio) =>
        !Directory.Exists(diretorio) || !Directory.EnumerateFileSystemEntries(diretorio).Any();

    /// <summary>
    /// Apaga a árvore inteira, mas remove junctions e links como links, sem
    /// entrar neles. É o que garante que o <c>rm</c> de um container com volume
    /// apaga o ponto de montagem e não os dados do host.
    /// </summary>
    public static void ApagarDiretorio(string diretorio)
    {
        if (!Directory.Exists(diretorio))
        {
            return;
        }

        if (EhLink(diretorio))
        {
            RemoverLink(diretorio);
            return;
        }

        foreach (var entrada in Directory.EnumerateFileSystemEntries(diretorio))
        {
            if (Directory.Exists(entrada))
            {
                ApagarDiretorio(entrada);
            }
            else
            {
                File.Delete(entrada);
            }
        }

        Directory.Delete(diretorio);
    }

    public static bool EhLink(string caminho) =>
        (File.Exists(caminho) || Directory.Exists(caminho))
        && File.GetAttributes(caminho).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Cria em <paramref name="link"/> uma junction NTFS que aponta para
    /// <paramref name="alvo"/> — o equivalente Windows de um bind mount. Junction,
    /// e não link simbólico, porque não exige privilégio de administrador. O
    /// <c>mklink /J</c> grava um ponto de reanálise (FSCTL_SET_REPARSE_POINT) no
    /// diretório: ao abrir o caminho, o NTFS redireciona para o alvo. Fora do
    /// Windows (onde só rodam os testes) usa um link simbólico, que tem o mesmo
    /// efeito.
    /// </summary>
    public static void CriarJuncao(string link, string alvo)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, alvo);
            return;
        }

        var informacoes = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                "System32", "cmd.exe"),
            // /s: o cmd tira só as aspas externas e preserva as dos caminhos.
            Arguments = $"/s /c \"mklink /J \"{link}\" \"{alvo}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var processo = Process.Start(informacoes)!;
        var erro = processo.StandardError.ReadToEnd();
        processo.StandardOutput.ReadToEnd();
        processo.WaitForExit();

        if (processo.ExitCode != 0)
        {
            throw new IOException($"Nao foi possivel montar '{alvo}' em '{link}': {erro.Trim()}");
        }
    }

    /// <summary>Remove a junction sem tocar no conteúdo do alvo.</summary>
    public static void RemoverLink(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            // RemoveDirectory num ponto de reanálise apaga o ponto, não o alvo.
            Directory.Delete(link, recursive: false);
        }
        else
        {
            File.Delete(link);
        }
    }

    /// <summary>
    /// Resolve <paramref name="relativo"/> dentro de <paramref name="raiz"/> e
    /// recusa caminhos que escapem dela, como <c>..\..\Windows</c>.
    /// </summary>
    public static string CaminhoDentro(string raiz, string relativo)
    {
        var raizCompleta = Path.GetFullPath(raiz);
        var caminho = Path.GetFullPath(Path.Combine(raizCompleta, relativo.TrimStart('/', '\\')));
        var relativoFinal = Path.GetRelativePath(raizCompleta, caminho);

        if (relativoFinal == ".." || relativoFinal.StartsWith(".." + Path.DirectorySeparatorChar)
            || Path.IsPathRooted(relativoFinal))
        {
            throw new InvalidOperationException($"O caminho '{relativo}' sai de '{raiz}'.");
        }

        return caminho;
    }
}
