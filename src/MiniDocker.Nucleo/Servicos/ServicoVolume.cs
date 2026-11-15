using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Repositorios;

namespace MiniDocker.Nucleo.Servicos;

/// <summary>Um volume nomeado e quem o usa.</summary>
public sealed record InfoVolume(string Nome, string Caminho, long TamanhoBytes, IReadOnlyList<string> Conteineres);

/// <summary>
/// Volumes nomeados: pastas que o próprio MiniDocker guarda em
/// <c>.minidocker\volumes\NOME</c>. Diferente do rootfs, não pertencem a nenhum
/// container — sobrevivem ao <c>rm</c> e podem ser montados em vários.
/// </summary>
public class ServicoVolume
{
    private readonly string raizVolumes;
    private readonly IRepositorioConteiner conteineres;

    public ServicoVolume(string? raizWorkspace = null, IRepositorioConteiner? conteineres = null)
    {
        raizVolumes = Path.Combine(Workspace.Raiz(raizWorkspace), "volumes");
        this.conteineres = conteineres ?? new RepositorioConteinerArquivo(raizWorkspace);
        Directory.CreateDirectory(raizVolumes);
    }

    /// <summary>Cria o volume se não existir (o <c>-v</c> também cria sozinho) e devolve a pasta.</summary>
    public string Criar(string nome)
    {
        EspecificacaoVolume.ValidarNome(nome);
        var caminho = Caminho(nome);
        Directory.CreateDirectory(caminho);
        return caminho;
    }

    public IReadOnlyList<InfoVolume> Listar()
    {
        var todos = conteineres.ObterTodos();
        return Directory.EnumerateDirectories(raizVolumes)
            .Select(Path.GetFileName)
            .OrderBy(nome => nome, StringComparer.Ordinal)
            .Select(nome => Informacoes(nome!, todos))
            .ToList();
    }

    public InfoVolume Obter(string nome)
    {
        EspecificacaoVolume.ValidarNome(nome);
        if (!Directory.Exists(Caminho(nome)))
        {
            throw new InvalidOperationException($"Volume '{nome}' nao existe.");
        }

        return Informacoes(nome, conteineres.ObterTodos());
    }

    /// <summary>Apaga o volume e os dados. Recusa enquanto algum container o tiver montado.</summary>
    public void Remover(string nome)
    {
        var volume = Obter(nome);
        if (volume.Conteineres.Count > 0)
        {
            throw new InvalidOperationException(
                $"O volume '{nome}' esta em uso por: {string.Join(", ", volume.Conteineres)}. Remova os containers antes.");
        }

        Workspace.ApagarDiretorio(volume.Caminho);
    }

    private string Caminho(string nome) => Path.Combine(raizVolumes, nome);

    private InfoVolume Informacoes(string nome, IReadOnlyList<Conteiner> todos)
    {
        var caminho = Caminho(nome);
        var usando = todos
            .Where(c => c.Volumes.Any(v => v.NomeVolume == nome))
            .Select(c => c.Nome)
            .ToList();
        return new InfoVolume(nome, caminho, Workspace.TamanhoDiretorio(caminho), usando);
    }
}
