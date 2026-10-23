namespace MiniDocker.Nucleo;

/// <summary>Onde o MiniDocker guarda containers, imagens e logs.</summary>
internal static class Workspace
{
    public static string Raiz(string? raiz) => raiz ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minidocker");

    public static void CopiarDiretorio(string origem, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (var diretorio in Directory.EnumerateDirectories(origem, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destino, Path.GetRelativePath(origem, diretorio)));
        }

        foreach (var arquivo in Directory.EnumerateFiles(origem, "*", SearchOption.AllDirectories))
        {
            File.Copy(arquivo, Path.Combine(destino, Path.GetRelativePath(origem, arquivo)), overwrite: true);
        }
    }

    public static long TamanhoDiretorio(string diretorio) =>
        Directory.Exists(diretorio)
            ? Directory.EnumerateFiles(diretorio, "*", SearchOption.AllDirectories)
                .Sum(arquivo => new FileInfo(arquivo).Length)
            : 0;

    public static void ApagarDiretorio(string diretorio)
    {
        if (Directory.Exists(diretorio))
        {
            Directory.Delete(diretorio, recursive: true);
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
